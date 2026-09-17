using FinSight.Api.Auth;
using FinSight.Api.Contracts;
using FinSight.Api.Middleware;
using FinSight.Core.Domain;
using FinSight.Core.Statements;
using FinSight.Infrastructure.Gmail;
using FinSight.Infrastructure.Insights;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Pipeline;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace FinSight.Api.Controllers;

[ApiController]
[Route("api/statements")]
public sealed class StatementsController(FinSightDbContext db, JobService jobs, TimeProvider time) : ControllerBase
{
    private const int MaxStatementsPerJob = 60;
    private const long MaxUploadBytes = 20 * 1024 * 1024;
    private const int MaxPendingUploads = 30;

    [HttpGet]
    public async Task<IReadOnlyList<StatementDto>> List(CancellationToken cancellationToken)
    {
        var statements = await db.Statements.AsNoTracking().Where(s => s.Status != StatementStatus.Dismissed).ToListAsync(cancellationToken);
        return statements
            .OrderByDescending(s => s.PeriodEnd ?? DateOnly.FromDateTime((s.ReceivedAt ?? s.CreatedAt).UtcDateTime))
            .Select(s => s.ToDto())
            .ToList();
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<StatementDetailDto>> Get(Guid id, [FromServices] DashboardService dashboard, CancellationToken cancellationToken)
    {
        var statement = await db.Statements.AsNoTracking()
            .Include(s => s.Transactions)
            .SingleOrDefaultAsync(s => s.Id == id && s.Status != StatementStatus.Dismissed, cancellationToken);

        if (statement is null)
        {
            return ApiErrors.NotFound("statement");
        }

        var resolve = await dashboard.CategoryResolverAsync(cancellationToken);
        var transactions = statement.Transactions.OrderBy(t => t.Date).ThenBy(t => t.CreatedAt).ToList();
        foreach (var transaction in transactions)
        {
            transaction.Statement = statement;
        }

        return new StatementDetailDto(
            statement.ToDto(),
            Mapping.MaskedSubject(statement.Subject),
            statement.Currency,
            statement.OpeningBalance,
            statement.ClosingBalance,
            Mapping.ReadList(statement.DetectionReasons),
            Mapping.ReadList(statement.ExtractionWarnings),
            transactions.Select(t => t.ToDto(resolve)).ToList());
    }

    /// <summary>Searches Gmail for new statements. Returns a job to poll.</summary>
    [HttpPost("sync")]
    [EnableRateLimiting(RateLimits.Sync)]
    public async Task<ActionResult<JobStartedResponse>> Sync(CancellationToken cancellationToken)
    {
        if (User.IsDemoUser())
        {
            return ApiErrors.Problem(StatusCodes.Status409Conflict, "demo_mode", "Demo mode uses sample statements. Sign in with Google to scan your Gmail.");
        }

        var connection = await db.GmailConnections.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (connection is null)
        {
            return ApiErrors.Map(new GmailNotConnectedException());
        }

        if (connection.Status == GmailConnectionStatus.Expired)
        {
            return ApiErrors.Map(new GmailAuthExpiredException());
        }

        var userId = User.GetUserId();
        var job = await jobs.CreateAsync(userId, JobKind.Sync, StatementLabels.SyncPlan(), cancellationToken);
        await jobs.EnqueueAsync(new JobWorkItem(job.Id, userId, JobKind.Sync, []), cancellationToken);
        return Accepted(new JobStartedResponse(job.Id));
    }

    /// <summary>Downloads, extracts and analyzes the selected statements.</summary>
    [HttpPost("process")]
    [EnableRateLimiting(RateLimits.Sync)]
    public async Task<ActionResult<JobStartedResponse>> Process(ProcessStatementsRequest request, CancellationToken cancellationToken)
    {
        var ids = request.StatementIds.Distinct().Take(MaxStatementsPerJob).ToList();
        var statements = await db.Statements.Where(s => ids.Contains(s.Id) && s.Status != StatementStatus.Dismissed).ToListAsync(cancellationToken);

        if (statements.Count == 0)
        {
            return ApiErrors.BadRequest("no_statements", "Choose at least one statement to analyze.");
        }

        if (statements.Any(s => s.Source == StatementSourceKind.Demo))
        {
            return ApiErrors.Problem(StatusCodes.Status409Conflict, "demo_mode", "Sample statements are already processed.");
        }

        // Uploaded statements and statement alerts (emails without the PDF) can only be processed from an uploaded file.
        if (statements.Any(s => s.Source == StatementSourceKind.ManualUpload || s.Status == StatementStatus.AwaitingUpload || StatementAlert.IsAlert(s)))
        {
            return ApiErrors.Problem(StatusCodes.Status409Conflict, StatementFailure.UploadRequired, StatementFailure.Message(StatementFailure.UploadRequired));
        }

        var userId = User.GetUserId();
        var job = await jobs.CreateAsync(userId, JobKind.Process, StatementLabels.ProcessingPlan(statements), cancellationToken);
        await jobs.EnqueueAsync(new JobWorkItem(job.Id, userId, JobKind.Process, statements.Select(s => s.Id).ToList()), cancellationToken);
        return Accepted(new JobStartedResponse(job.Id));
    }

    [HttpPost("{id:guid}/process")]
    [EnableRateLimiting(RateLimits.Sync)]
    public Task<ActionResult<JobStartedResponse>> Reprocess(Guid id, CancellationToken cancellationToken) =>
        Process(new ProcessStatementsRequest([id]), cancellationToken);

    /// <summary>
    /// Manual upload through the same pipeline. The PDF is processed in memory and never written to disk.
    /// Pass <c>statementId</c> to re-upload the file for an existing statement, or to supply the PDF a statement alert asked for.
    /// Without it, a successful import also clears the matching statement alert (same bank and account, around the period end).
    /// </summary>
    [HttpPost("/api/uploads/statements")]
    [EnableRateLimiting(RateLimits.Upload)]
    [RequestSizeLimit(MaxUploadBytes + (1024 * 1024))]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxUploadBytes + (1024 * 1024))]
    public async Task<ActionResult<UploadStartedResponse>> Upload(IFormFile? file, [FromForm] Guid? statementId, CancellationToken cancellationToken)
    {
        if (file is null || file.Length == 0)
        {
            return ApiErrors.BadRequest("file_missing", "Choose a PDF statement to upload.");
        }

        if (file.Length > MaxUploadBytes)
        {
            return ApiErrors.BadRequest("file_too_large", "That file is larger than 20 MB.");
        }

        using var buffer = new MemoryStream((int)file.Length);
        await file.CopyToAsync(buffer, cancellationToken);
        var bytes = buffer.ToArray();

        if (bytes.Length < 5 || !bytes.AsSpan(0, 5).SequenceEqual("%PDF-"u8))
        {
            return ApiErrors.BadRequest("not_a_pdf", "That file isn't a PDF. Download the statement as a PDF from your bank and try again.");
        }

        var userId = User.GetUserId();

        // Uploads are processed one at a time per user and held in memory until then, so cap how many can wait.
        var pending = await db.ProcessingJobs.CountAsync(j => j.Kind == JobKind.Upload && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), cancellationToken);
        if (pending >= MaxPendingUploads)
        {
            return ApiErrors.Problem(StatusCodes.Status429TooManyRequests, "rate_limited", "Your earlier uploads are still processing. Wait a moment and try again.");
        }

        var now = time.GetUtcNow();
        var hash = StatementImportService.HashOf(bytes);
        Statement? statement;

        if (statementId is not null)
        {
            statement = await db.Statements.SingleOrDefaultAsync(s => s.Id == statementId && s.Status != StatementStatus.Dismissed, cancellationToken);
            if (statement is null)
            {
                return ApiErrors.NotFound("statement");
            }
        }
        else
        {
            statement = await db.Statements.SingleOrDefaultAsync(s => s.SourceKey == $"upload:{hash}", cancellationToken);
            if (statement is null)
            {
                var filename = Path.GetFileName(file.FileName);
                statement = new Statement
                {
                    UserId = userId,
                    Source = StatementSourceKind.ManualUpload,
                    SourceKey = $"upload:{hash}",
                    Filename = Core.Text.SensitiveDataMasker.Mask(filename.Length > 200 ? filename[..200] : filename),
                    SizeBytes = bytes.Length,
                    ReceivedAt = now,
                    DetectionConfidence = 1,
                    DetectionReasons = """["Uploaded manually"]""",
                    Status = StatementStatus.Discovered,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                db.Statements.Add(statement);
                try
                {
                    await db.SaveChangesAsync(cancellationToken);
                }
                catch (DbUpdateException)
                {
                    // The same file uploaded twice at once: the unique source key lets exactly one insert win.
                    db.ChangeTracker.Clear();
                    statement = await db.Statements.SingleAsync(s => s.SourceKey == $"upload:{hash}", cancellationToken);
                }
            }
        }

        var job = await jobs.CreateAsync(userId, JobKind.Upload, StatementLabels.ProcessingPlan([statement]), cancellationToken);
        await jobs.EnqueueAsync(new JobWorkItem(job.Id, userId, JobKind.Upload, [statement.Id], new Dictionary<Guid, byte[]> { [statement.Id] = bytes }), cancellationToken);
        return Accepted(new UploadStartedResponse(job.Id, statement.Id));
    }

    /// <summary>Hides a statement alert the user doesn't want to upload. A rescan never brings it back.</summary>
    [HttpPost("{id:guid}/dismiss")]
    public async Task<IActionResult> Dismiss(Guid id, CancellationToken cancellationToken)
    {
        var statement = await db.Statements.SingleOrDefaultAsync(s => s.Id == id && s.Status != StatementStatus.Dismissed, cancellationToken);
        if (statement is null)
        {
            return ApiErrors.NotFound("statement");
        }

        if (statement.Status != StatementStatus.AwaitingUpload)
        {
            return ApiErrors.Problem(StatusCodes.Status409Conflict, "not_awaiting_upload", "Only statement alerts waiting for an upload can be dismissed.");
        }

        statement.Status = StatementStatus.Dismissed;
        statement.UpdatedAt = time.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>
    /// Deletes the statement and every transaction imported from it. A statement alert's row is kept, dismissed and emptied,
    /// because Gmail discovery skips emails it has already recorded; deleting it would let the next scan recreate it.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromServices] CategorizationService categorization, CancellationToken cancellationToken)
    {
        var statement = await db.Statements.SingleOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (statement is null || statement.Status == StatementStatus.Dismissed)
        {
            return ApiErrors.NotFound("statement");
        }

        int deleted;
        if (StatementAlert.IsAlert(statement))
        {
            var alert = statement;
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Transactions.Where(t => t.StatementId == id).ExecuteDeleteAsync(cancellationToken);
            alert.Status = StatementStatus.Dismissed;
            alert.TransactionCount = 0;
            alert.ContentHash = null;
            alert.FailureCode = null;
            alert.UpdatedAt = time.GetUtcNow();
            deleted = await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        else
        {
            deleted = await db.Statements.Where(s => s.Id == id).ExecuteDeleteAsync(cancellationToken);
        }

        if (deleted == 0)
        {
            return ApiErrors.NotFound("statement");
        }

        // Transfers matched against this statement's transactions lose their other side.
        await categorization.ReleaseOrphanedTransfersAsync(cancellationToken);
        return NoContent();
    }
}
