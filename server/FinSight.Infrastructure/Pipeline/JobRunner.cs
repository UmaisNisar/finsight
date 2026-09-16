using System.Globalization;
using FinSight.Core.Abstractions;
using FinSight.Core.Analytics;
using FinSight.Core.Domain;
using FinSight.Infrastructure.Gmail;
using FinSight.Infrastructure.Insights;
using FinSight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FinSight.Infrastructure.Pipeline;

public static class StatementLabels
{
    public static string Title(Statement statement)
    {
        var culture = CultureInfo.InvariantCulture;
        if (statement.PeriodEnd is { } end)
        {
            return end.ToString("MMMM yyyy", culture);
        }

        return statement.ReceivedAt is { } received
            ? $"Received {received.ToString("MMM d, yyyy", culture)}"
            : statement.Filename;
    }

    public static string StepLabel(Statement statement) =>
        statement.Institution is null ? Title(statement) : $"{Title(statement)} · {statement.Institution}";

    public static IEnumerable<JobStep> ProcessingPlan(IEnumerable<Statement> statements) =>
        statements.Select(s => new JobStep($"s:{s.Id}", StepLabel(s), StepStatus.Pending))
            .Append(new JobStep("categorize", "Categorizing transactions", StepStatus.Pending))
            .Append(new JobStep("insights", "Generating insights", StepStatus.Pending));

    public static IEnumerable<JobStep> SyncPlan() =>
    [
        new("gmail", "Connecting to Gmail", StepStatus.Pending),
        new("search", "Finding statements", StepStatus.Pending),
    ];
}

/// <summary>Executes queued jobs: Gmail discovery, and the statement processing pipeline.</summary>
public sealed partial class JobRunner(
    FinSightDbContext db,
    StatementDiscoveryService discovery,
    StatementImportService importer,
    CategorizationService categorization,
    AnalysisService analysis,
    IGeminiService gemini,
    TimeProvider time,
    ILogger<JobRunner> logger)
{
    public async Task RunAsync(JobWorkItem item, CancellationToken cancellationToken)
    {
        var reporter = new JobReporter(db, item.JobId, time);
        await reporter.LoadAsync(cancellationToken);
        await reporter.StartAsync(cancellationToken);

        try
        {
            if (item.Kind == JobKind.Sync)
            {
                await RunSyncAsync(item, reporter, cancellationToken);
            }
            else
            {
                await RunProcessingAsync(item, reporter, cancellationToken);
            }

            await reporter.CompleteAsync(cancellationToken);
        }
        catch (GmailAuthExpiredException)
        {
            await reporter.FailAsync(StatementFailure.GmailAuthExpired, CancellationToken.None);
        }
        catch (GmailNotConnectedException)
        {
            await reporter.FailAsync(StatementFailure.GmailNotConnected, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Exception messages can include request data; log the type and job id only.
            LogJobFailed(logger, ex.GetType().Name, item.JobId);
            await reporter.FailAsync(StatementFailure.Unexpected, CancellationToken.None);
        }
    }

    private async Task RunSyncAsync(JobWorkItem item, JobReporter reporter, CancellationToken cancellationToken)
    {
        await reporter.SetAsync("gmail", "Connecting to Gmail", StepStatus.Running, cancellationToken: cancellationToken);
        await reporter.SetAsync("search", "Finding statements", StepStatus.Running, cancellationToken: cancellationToken);
        await reporter.SetAsync("gmail", "Gmail connected", StepStatus.Done, cancellationToken: cancellationToken);

        var result = await discovery.DiscoverAsync(item.UserId, cancellationToken);
        var detail = result.NewStatements == 0
            ? result.TotalStatements == 0 ? "No statements found" : "No new statements"
            : $"{result.NewStatements} new statement{(result.NewStatements == 1 ? "" : "s")} found";
        await reporter.SetAsync("search", detail, StepStatus.Done, cancellationToken: cancellationToken);
    }

    private async Task RunProcessingAsync(JobWorkItem item, JobReporter reporter, CancellationToken cancellationToken)
    {
        var user = await db.Users.SingleAsync(cancellationToken);
        var statements = await db.Statements.Where(s => item.StatementIds.Contains(s.Id)).ToListAsync(cancellationToken);
        var imported = new List<Statement>();

        foreach (var statement in statements.OrderByDescending(s => s.PeriodEnd ?? DateOnly.FromDateTime((s.ReceivedAt ?? s.CreatedAt).UtcDateTime)))
        {
            var key = $"s:{statement.Id}";
            var label = StatementLabels.StepLabel(statement);

            try
            {
                var bytes = await ObtainPdfAsync(item, statement, reporter, key, label, cancellationToken);
                if (bytes is null)
                {
                    await reporter.SetAsync(key, label, StepStatus.Failed, StatementFailure.Message(statement.FailureCode), cancellationToken);
                    continue;
                }

                await reporter.SetAsync(key, label, StepStatus.Running, "Reading statement", cancellationToken);
                var result = await importer.ImportAsync(statement, bytes, user.Settings.Currency, cancellationToken);

                label = StatementLabels.StepLabel(statement);
                switch (result.Outcome)
                {
                    case ImportOutcome.Imported:
                        imported.Add(statement);
                        await reporter.SetAsync(key, label, StepStatus.Done,
                            $"{result.TransactionCount} transaction{(result.TransactionCount == 1 ? "" : "s")}", cancellationToken);
                        break;
                    case ImportOutcome.DuplicateFile:
                        await reporter.SetAsync(key, label, StepStatus.Skipped, "Already imported", cancellationToken);
                        break;
                    default:
                        await reporter.SetAsync(key, label, StepStatus.Failed, StatementFailure.Message(result.FailureCode), cancellationToken);
                        break;
                }
            }
            catch (GmailAuthExpiredException)
            {
                await importer.FailAsync(statement, StatementFailure.GmailAuthExpired, CancellationToken.None);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not GmailNotConnectedException)
            {
                LogStatementFailed(logger, ex.GetType().Name, statement.Id);
                db.ChangeTracker.Clear();
                var fresh = await db.Statements.SingleAsync(s => s.Id == statement.Id, CancellationToken.None);
                await importer.FailAsync(fresh, StatementFailure.Unexpected, CancellationToken.None);
                await reporter.SetAsync(key, label, StepStatus.Failed, StatementFailure.Message(StatementFailure.Unexpected), CancellationToken.None);
            }
        }

        await CategorizeAsync(user, imported, reporter, cancellationToken);
        await GenerateInsightsAsync(user, imported, reporter, cancellationToken);
    }

    private async Task<byte[]?> ObtainPdfAsync(JobWorkItem item, Statement statement, JobReporter reporter, string key, string label, CancellationToken cancellationToken)
    {
        if (item.Uploads?.TryGetValue(statement.Id, out var uploaded) == true)
        {
            return uploaded;
        }

        if (statement.Source != StatementSourceKind.Gmail)
        {
            await importer.FailAsync(statement, StatementFailure.UploadRequired, cancellationToken);
            return null;
        }

        await reporter.SetAsync(key, label, StepStatus.Running, "Downloading", cancellationToken);
        statement.Status = StatementStatus.Downloading;
        await db.SaveChangesAsync(cancellationToken);

        try
        {
            return await discovery.DownloadAsync(item.UserId, statement, cancellationToken);
        }
        catch (FileNotFoundException)
        {
            await importer.FailAsync(statement, StatementFailure.AttachmentMissing, cancellationToken);
        }
        catch (HttpRequestException)
        {
            await importer.FailAsync(statement, StatementFailure.DownloadFailed, cancellationToken);
        }

        return null;
    }

    private async Task CategorizeAsync(User user, List<Statement> imported, JobReporter reporter, CancellationToken cancellationToken)
    {
        if (imported.Count == 0)
        {
            await reporter.SetAsync("categorize", "Categorizing transactions", StepStatus.Skipped, "Nothing new to categorize", cancellationToken);
            return;
        }

        await reporter.SetAsync("categorize", "Categorizing transactions", StepStatus.Running, cancellationToken: cancellationToken);

        var detail = "Categorized with rules";
        if (user.Settings.AiCategorizationEnabled)
        {
            var outcome = await categorization.CategorizeWithAiAsync(imported.Select(s => s.Id).ToList(), cancellationToken);
            detail = outcome.Failure switch
            {
                AiFailure.NotConfigured => "Categorized with rules (AI not configured)",
                not null => "Categorized with rules (AI temporarily unavailable)",
                _ when outcome.MerchantsCategorized > 0 => $"AI categorized {outcome.MerchantsCategorized} merchant{(outcome.MerchantsCategorized == 1 ? "" : "s")}",
                _ => detail,
            };
        }

        var dates = await db.Transactions.Where(t => imported.Select(s => s.Id).Contains(t.StatementId))
            .GroupBy(_ => 1)
            .Select(g => new { Min = g.Min(t => t.Date), Max = g.Max(t => t.Date) })
            .FirstOrDefaultAsync(cancellationToken);

        if (dates is not null)
        {
            var pairs = await categorization.MatchTransfersAsync(dates.Min, dates.Max, cancellationToken);
            if (pairs > 0)
            {
                detail += $" · {pairs} transfer{(pairs == 1 ? "" : "s")} matched";
            }
        }

        await reporter.SetAsync("categorize", "Categorizing transactions", StepStatus.Done, detail, cancellationToken);
    }

    private async Task GenerateInsightsAsync(User user, List<Statement> imported, JobReporter reporter, CancellationToken cancellationToken)
    {
        if (imported.Count == 0 || !user.Settings.AiInsightsEnabled || !gemini.IsConfigured)
        {
            var reason = imported.Count == 0 ? "No new data" : !user.Settings.AiInsightsEnabled ? "AI insights are turned off" : "AI not configured";
            await reporter.SetAsync("insights", "Generating insights", StepStatus.Skipped, reason, cancellationToken);
            return;
        }

        await reporter.SetAsync("insights", "Generating insights", StepStatus.Running, cancellationToken: cancellationToken);

        // Analyze the most recent complete month that has data, which is what the overview opens on.
        var latest = await db.Transactions.MaxAsync(t => (DateOnly?)t.Date, cancellationToken);
        if (latest is null)
        {
            await reporter.SetAsync("insights", "Generating insights", StepStatus.Skipped, "No transactions", cancellationToken);
            return;
        }

        var month = new DateOnly(latest.Value.Year, latest.Value.Month, 1);
        var range = new DateRange(month, month.AddMonths(1).AddDays(-1));

        try
        {
            await analysis.GenerateAsync(range, cancellationToken);
            await reporter.SetAsync("insights", "Insights ready", StepStatus.Done, range.Label(), cancellationToken);
        }
        catch (Exception ex) when (ex is AiUnavailableException or AnalysisUnavailableException)
        {
            await reporter.SetAsync("insights", "Generating insights", StepStatus.Skipped, "AI analysis is temporarily unavailable", cancellationToken);
        }
    }

    [LoggerMessage(LogLevel.Error, "Job {JobId} failed with {ExceptionType}")]
    private static partial void LogJobFailed(ILogger logger, string exceptionType, Guid jobId);

    [LoggerMessage(LogLevel.Error, "Statement {StatementId} failed with {ExceptionType}")]
    private static partial void LogStatementFailed(ILogger logger, string exceptionType, Guid statementId);
}
