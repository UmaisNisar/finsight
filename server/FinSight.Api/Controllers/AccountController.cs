using FinSight.Api.Auth;
using FinSight.Api.Contracts;
using FinSight.Api.Middleware;
using FinSight.Core.Categories;
using FinSight.Core.Domain;
using FinSight.Infrastructure.Gmail;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Pipeline;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FinSight.Api.Controllers;

[ApiController]
[Route("api")]
public sealed class AccountController(FinSightDbContext db) : ControllerBase
{
    private static readonly string[] DateFormats = ["MMM d, yyyy", "d MMM yyyy", "yyyy-MM-dd", "MM/dd/yyyy", "dd/MM/yyyy"];

    [HttpGet("settings")]
    public async Task<SettingsDto> GetSettings(CancellationToken cancellationToken) =>
        (await db.Users.AsNoTracking().SingleAsync(cancellationToken)).Settings.ToDto();

    [HttpPut("settings")]
    public async Task<ActionResult<SettingsDto>> UpdateSettings(SettingsDto request, CancellationToken cancellationToken)
    {
        if (!Currencies.IsSupported(request.Currency))
        {
            return ApiErrors.BadRequest("invalid_currency", "Choose CAD, USD, EUR or GBP.");
        }

        if (!DateFormats.Contains(request.DateFormat))
        {
            return ApiErrors.BadRequest("invalid_date_format", "That date format isn't supported.");
        }

        var user = await db.Users.SingleAsync(cancellationToken);
        user.Settings = new UserSettings
        {
            Currency = request.Currency.ToUpperInvariant(),
            DateFormat = request.DateFormat,
            Theme = request.Theme,
            AiCategorizationEnabled = request.AiCategorizationEnabled,
            AiInsightsEnabled = request.AiInsightsEnabled,
            NotificationsEnabled = request.NotificationsEnabled,
        };
        await db.SaveChangesAsync(cancellationToken);
        return user.Settings.ToDto();
    }

    [HttpGet("categories")]
    public async Task<IReadOnlyList<CategoryGroupDto>> Categories(CancellationToken cancellationToken)
    {
        var custom = await db.CustomCategories.AsNoTracking().ToListAsync(cancellationToken);
        return CategoryTaxonomy.Groups
            .Select(g => new CategoryGroupDto(g.Id, g.Name,
                CategoryTaxonomy.All.Where(c => c.GroupId == g.Id).Select(c => new CategoryDto(c.Id, c.Name, c.GroupId, c.Kind, false))
                    .Concat(custom.Where(c => c.GroupId == g.Id).OrderBy(c => c.Name).Select(c => new CategoryDto(c.CategoryId, c.Name, c.GroupId, CategoryKind.Expense, true)))
                    .ToList()))
            .ToList();
    }

    [HttpPost("categories")]
    public async Task<ActionResult<CategoryDto>> CreateCategory(CreateCategoryRequest request, CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 40 || CategoryTaxonomy.FindGroup(request.GroupId) is null || request.GroupId is "income" or "financial")
        {
            return ApiErrors.BadRequest("invalid_category", "Give the category a name (2–40 characters) and a spending group.");
        }

        var slug = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        var id = $"custom.{request.GroupId}.{slug}";
        if (await db.CustomCategories.AnyAsync(c => c.CategoryId == id, cancellationToken))
        {
            return ApiErrors.Problem(StatusCodes.Status409Conflict, "category_exists", "You already have a category with that name.");
        }

        db.CustomCategories.Add(new CustomCategory { UserId = User.GetUserId(), CategoryId = id, Name = name, GroupId = request.GroupId });
        await db.SaveChangesAsync(cancellationToken);
        return new CategoryDto(id, name, request.GroupId, CategoryKind.Expense, true);
    }

    [HttpGet("jobs/{id:guid}")]
    public async Task<ActionResult<JobDto>> GetJob(Guid id, [FromServices] JobService jobs, CancellationToken cancellationToken)
    {
        var job = await jobs.FindAsync(id, cancellationToken);
        return job is null ? ApiErrors.NotFound("job") : ToDto(job);
    }

    [HttpGet("jobs/active")]
    public async Task<ActionResult<JobDto?>> GetActiveJob([FromServices] JobService jobs, CancellationToken cancellationToken)
    {
        var job = await jobs.LatestActiveAsync(cancellationToken);
        return job is null ? NoContent() : ToDto(job);
    }

    /// <summary>Deletes all transactions. Gmail statements return to "discovered"; uploaded statements are removed.</summary>
    [HttpDelete("data/transactions")]
    public async Task<DeleteResult> DeleteTransactions(CancellationToken cancellationToken)
    {
        var deleted = await db.Transactions.ExecuteDeleteAsync(cancellationToken);
        await db.Statements.Where(s => s.Source != StatementSourceKind.Gmail).ExecuteDeleteAsync(cancellationToken);
        await db.Statements.ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, StatementStatus.Discovered)
            .SetProperty(x => x.TransactionCount, 0)
            .SetProperty(x => x.ContentHash, (string?)null)
            .SetProperty(x => x.FailureCode, (string?)null), cancellationToken);
        await db.FinancialAnalyses.ExecuteDeleteAsync(cancellationToken);
        return new DeleteResult(deleted);
    }

    /// <summary>Deletes all statements and their transactions.</summary>
    [HttpDelete("data/statements")]
    public async Task<DeleteResult> DeleteStatements(CancellationToken cancellationToken)
    {
        await db.Transactions.ExecuteDeleteAsync(cancellationToken);
        var deleted = await db.Statements.ExecuteDeleteAsync(cancellationToken);
        await db.FinancialAnalyses.ExecuteDeleteAsync(cancellationToken);
        return new DeleteResult(deleted);
    }

    /// <summary>Deletes every piece of financial data and revokes Gmail access. The sign-in account remains.</summary>
    [HttpDelete("data")]
    public async Task<IActionResult> DeleteAllData([FromServices] GoogleTokenService tokens, CancellationToken cancellationToken)
    {
        await tokens.DisconnectAsync(User.GetUserId(), cancellationToken);
        await db.Transactions.ExecuteDeleteAsync(cancellationToken);
        await db.Statements.ExecuteDeleteAsync(cancellationToken);
        await db.MerchantRules.ExecuteDeleteAsync(cancellationToken);
        await db.CustomCategories.ExecuteDeleteAsync(cancellationToken);
        await db.FinancialAnalyses.ExecuteDeleteAsync(cancellationToken);
        await db.ProcessingJobs.ExecuteDeleteAsync(cancellationToken);
        return NoContent();
    }

    /// <summary>Deletes the account and everything in it, then signs out.</summary>
    [HttpDelete("account")]
    public async Task<IActionResult> DeleteAccount([FromServices] GoogleTokenService tokens, [FromServices] IMemoryCache cache, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        await DeleteAllData(tokens, cancellationToken);
        await db.Users.ExecuteDeleteAsync(cancellationToken);
        cache.Remove($"user-exists:{userId}");
        await HttpContext.SignOutAsync();
        return NoContent();
    }

    private static JobDto ToDto(ProcessingJob job) => new(
        job.Id,
        job.Kind,
        job.Status,
        JobSteps.Read(job.StepsJson),
        job.ErrorCode,
        job.ErrorCode is null ? null : job.ErrorCode == "interrupted" ? "This job was interrupted. Try again." : StatementFailure.Message(job.ErrorCode),
        job.CreatedAt,
        job.CompletedAt);
}
