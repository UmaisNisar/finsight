using FinSight.Api.Contracts;
using FinSight.Api.Middleware;
using FinSight.Core.Abstractions;
using FinSight.Core.Analytics;
using FinSight.Core.Categories;
using FinSight.Core.Insights;
using FinSight.Infrastructure.Gemini;
using FinSight.Infrastructure.Insights;
using FinSight.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FinSight.Api.Controllers;

// Also enforced by the fallback policy; explicit so every endpoint here visibly requires a signed-in user.
[Authorize]
[ApiController]
[Route("api")]
public sealed partial class InsightsController(
    FinSightDbContext db,
    DashboardService dashboard,
    AnalysisService analysis,
    IGeminiService gemini,
    IGeminiKeyResolver keys,
    AiQuota quota,
    AiKeyHealth keyHealth,
    ILogger<InsightsController> logger) : ControllerBase
{
    /// <summary>Deterministic financial summary for a period: every number on the overview.</summary>
    [HttpGet("summary")]
    public async Task<ActionResult<SummaryResponse>> Summary([FromQuery] PeriodQuery query, CancellationToken cancellationToken)
    {
        if (!query.TryResolve(dashboard.Today, out var range, out var period))
        {
            return InvalidPeriod();
        }

        var snapshot = await dashboard.GetSnapshotAsync(range, cancellationToken);
        var latest = await dashboard.LatestTransactionDateAsync(cancellationToken);

        return new SummaryResponse(
            period,
            snapshot.Settings.Currency,
            snapshot.Summary,
            snapshot.Recurring.Where(r => r.IsActive).Select(r => ToDto(r, snapshot.ResolveCategory, null)).ToList(),
            snapshot.Anomalies.Select(a => new AnomalyDto(a.TransactionId, a.Date, a.Merchant, a.CategoryId, snapshot.ResolveCategory(a.CategoryId).Name, a.Amount, a.Kind, a.TypicalAmount)).ToList(),
            latest,
            latest is not null);
    }

    [HttpGet("analysis")]
    public async Task<ActionResult<AnalysisResponse>> GetAnalysis([FromQuery] PeriodQuery query, CancellationToken cancellationToken)
    {
        if (!query.TryResolve(dashboard.Today, out var range, out var period))
        {
            return InvalidPeriod();
        }

        var stored = await analysis.GetAsync(range, cancellationToken);
        return await ToResponseAsync(period, stored, cancellationToken);
    }

    /// <summary>
    /// Asks Gemini to interpret the computed facts for the period. Validated before it is stored or returned. When Gemini isn't
    /// available, FinSight writes the analysis from the same facts and the response says why (<c>source</c>, <c>fallbackReason</c>).
    /// </summary>
    [HttpPost("analysis/generate")]
    [EnableRateLimiting(RateLimits.Ai)]
    public async Task<ActionResult<AnalysisResponse>> GenerateAnalysis([FromQuery] PeriodQuery query, CancellationToken cancellationToken)
    {
        if (!query.TryResolve(dashboard.Today, out var range, out var period))
        {
            return InvalidPeriod();
        }

        var stored = await analysis.GenerateAsync(range, cancellationToken);
        return await ToResponseAsync(period, stored, cancellationToken);
    }

    /// <summary>Subscriptions, bills and regular income detected across all history.</summary>
    [HttpGet("recurring")]
    public async Task<RecurringResponse> Recurring([FromServices] IMemoryCache cache, CancellationToken cancellationToken)
    {
        var (series, resolve) = await dashboard.GetRecurringAsync(cancellationToken);
        var settings = (await db.Users.AsNoTracking().SingleAsync(cancellationToken)).Settings;

        var reviews = new Dictionary<string, RecurringReview>();
        var reviewed = false;
        var expenses = series.Where(s => !s.IsIncome).ToList();

        if (settings.AiCategorizationEnabled && expenses.Count > 0 && await gemini.IsConfiguredAsync(cancellationToken))
        {
            var requests = expenses.Select((s, i) => new RecurringReviewRequest($"R{i + 1}", s.Merchant, resolve(s.CategoryId).Name, s.TypicalAmount,
                s.Frequency.ToString().ToLowerInvariant(), s.Occurrences, s.AmountVaries)).ToList();
            var cacheKey = "recurring-review:" + string.Join('|', requests.Select(r => $"{r.Merchant}:{r.Amount}:{r.Frequency}")).GetHashCode(StringComparison.Ordinal);

            try
            {
                var result = await cache.GetOrCreateAsync($"{Auth.FinSightClaims.GetUserId(User)}:{cacheKey}", async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(12);
                    return await gemini.DetectRecurringPatternsAsync(requests, cancellationToken);
                }) ?? [];

                foreach (var review in result)
                {
                    if (int.TryParse(review.Ref.AsSpan(1), System.Globalization.CultureInfo.InvariantCulture, out var number)
                        && number - 1 is var index && index >= 0 && index < expenses.Count)
                    {
                        reviews[expenses[index].MerchantKey] = review;
                    }
                }

                reviewed = true;
            }
            catch (AiUnavailableException ex)
            {
                // Recurring detection is deterministic; the AI only labels it. Rule-based labels are used instead.
                LogRecurringFallback(logger, ex.Failure);
            }
        }

        var items = series
            .Where(s => !(reviews.TryGetValue(s.MerchantKey, out var r) && r.Kind == RecurringKind.NotRecurring && s.Confidence < 0.8))
            .Select(s => ToDto(s, resolve, reviews.GetValueOrDefault(s.MerchantKey)))
            .ToList();

        var active = items.Where(i => i.IsActive && !i.IsIncome).ToList();
        var monthly = active.Sum(i => i.MonthlyEquivalent);
        return new RecurringResponse(
            items,
            decimal.Round(monthly, 2),
            decimal.Round(active.Where(i => i.Kind == RecurringKindDto.Subscription).Sum(i => i.MonthlyEquivalent), 2),
            decimal.Round(monthly * 12, 2),
            reviewed,
            settings.Currency);
    }

    private async Task<AnalysisResponse> ToResponseAsync(PeriodDto period, StoredAnalysis stored, CancellationToken cancellationToken)
    {
        var settings = (await db.Users.AsNoTracking().SingleAsync(cancellationToken)).Settings;
        var configured = await gemini.IsConfiguredAsync(cancellationToken);
        return new AnalysisResponse(period, stored.State, stored.Analysis, stored.Corrections, stored.Model, stored.GeneratedAt,
            new AnalysisAvailability(settings.AiInsightsEnabled, configured, configured ? await BlockedAsync(cancellationToken) : null), settings.Currency,
            stored.Analysis is null ? null : stored.Source, stored.FallbackReason);
    }

    /// <summary>Whether a new try with Gemini is known to be pointless right now.</summary>
    private async Task<string?> BlockedAsync(CancellationToken cancellationToken)
    {
        if (keyHealth.Get(Auth.FinSightClaims.GetUserId(User)) is { Failure: AiFailure.KeyRefused })
        {
            return AnalysisFallback.KeyRefused;
        }

        var key = await keys.ResolveDetailsAsync(cancellationToken);
        return key is not null && quota.Peek(key.UserId, key.Email, !key.IsUserKey, AiCallKind.Analysis) != AiQuotaDecision.Allowed
            ? AnalysisFallback.LimitReached
            : null;
    }

    private static RecurringDto ToDto(RecurringSeries s, Func<string, CategoryDefinition> resolve, RecurringReview? review)
    {
        var category = resolve(s.CategoryId);
        var kind = s.IsIncome ? RecurringKindDto.Income
            : review is not null && review.Kind != RecurringKind.NotRecurring ? (RecurringKindDto)(int)review.Kind
            : RecurringLabeler.Label(s.Merchant, category) is { } rule ? (RecurringKindDto)(int)rule
            : RecurringKindDto.Other;

        return new RecurringDto(s.MerchantKey, s.Merchant, category.Id, category.Name, kind, review is not null && !s.IsIncome, s.Frequency,
            s.TypicalAmount, s.MonthlyEquivalent, s.AmountVaries, s.Occurrences, s.FirstDate, s.LastDate, s.NextExpectedDate, s.IsActive, s.Confidence, s.IsIncome);
    }

    [LoggerMessage(LogLevel.Warning, "Recurring payments labelled by rules: AI review unavailable ({Failure})")]
    private static partial void LogRecurringFallback(ILogger logger, AiFailure failure);

    private static ObjectResult InvalidPeriod() =>
        ApiErrors.BadRequest("invalid_period", "Choose a valid time period. Custom ranges need a start and end date, up to three years apart.");
}
