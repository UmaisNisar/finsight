using System.Text.Json;
using FinSight.Core.Abstractions;
using FinSight.Core.Analytics;
using FinSight.Core.Domain;
using FinSight.Core.Insights;
using FinSight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FinSight.Infrastructure.Insights;

public enum AnalysisState
{
    /// <summary>An analysis exists and was generated from exactly the current data.</summary>
    Fresh,

    /// <summary>An analysis exists but transactions changed since it was generated.</summary>
    Stale,

    None,
}

/// <summary>Who wrote an analysis: Gemini, or FinSight's built-in writer when Gemini wasn't available.</summary>
public enum AnalysisSource
{
    Ai,
    BuiltIn,
}

/// <param name="Model">The Gemini model that answered, possibly a fallback. Null for built-in analyses.</param>
/// <param name="FallbackReason">Why the built-in writer was used (<see cref="AnalysisFallback"/> codes). Null for AI analyses.</param>
public sealed record StoredAnalysis(
    AnalysisState State,
    FinancialAnalysis? Analysis,
    IReadOnlyList<AnalysisCorrection> Corrections,
    string? Model,
    DateTimeOffset? GeneratedAt,
    AnalysisSource? Source = null,
    string? FallbackReason = null);

/// <summary>Stable codes for why an analysis was written without AI.</summary>
public static class AnalysisFallback
{
    public const string NotConfigured = "not_configured";
    public const string KeyRefused = "key_refused";
    public const string QuotaExhausted = "quota_exhausted";
    public const string LimitReached = "limit_reached";
    public const string Unavailable = "unavailable";

    /// <summary>
    /// Built-in analyses are stored in the same table without a schema change: the model column holds this prefix and the reason.
    /// </summary>
    public const string ModelPrefix = "finsight:built-in:";

    public static string ReasonFor(AiFailure failure) => failure switch
    {
        AiFailure.NotConfigured => NotConfigured,
        AiFailure.KeyRefused => KeyRefused,
        AiFailure.RateLimited => QuotaExhausted,
        AiFailure.LimitReached => LimitReached,
        _ => Unavailable,
    };

    public static bool IsBuiltIn(string? model) => model?.StartsWith(ModelPrefix, StringComparison.Ordinal) == true;

    public static string? ReasonOf(string? model) => IsBuiltIn(model) ? model![ModelPrefix.Length..] : null;
}

public enum AnalysisUnavailableReason
{
    Disabled,
    NotConfigured,
    NotEnoughData,
}

public sealed class AnalysisUnavailableException(AnalysisUnavailableReason reason) : Exception($"Analysis unavailable: {reason}")
{
    public AnalysisUnavailableReason Reason { get; } = reason;
}

/// <summary>
/// Builds privacy-conscious facts, asks Gemini to interpret them, and stores the validated result. When Gemini isn't available
/// (no key, key refused, quota or daily limit used up, or it failed), FinSight writes the analysis itself from the same facts
/// and says so. The next generate tries Gemini again, and its answer replaces the built-in one.
/// </summary>
public sealed partial class AnalysisService(FinSightDbContext db, DashboardService dashboard, IGeminiService gemini, TimeProvider time, ILogger<AnalysisService> logger)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public async Task<StoredAnalysis> GetAsync(DateRange range, CancellationToken cancellationToken)
    {
        var record = await db.FinancialAnalyses.AsNoTracking()
            .Where(a => a.PeriodStart == range.Start && a.PeriodEnd == range.End)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (record is null)
        {
            return new StoredAnalysis(AnalysisState.None, null, [], null, null);
        }

        var snapshot = await dashboard.GetSnapshotAsync(range, cancellationToken);
        var facts = BuildFacts(snapshot);

        return ToStored(record, facts.Hash() == record.FactsHash ? AnalysisState.Fresh : AnalysisState.Stale);
    }

    public async Task<StoredAnalysis> GenerateAsync(DateRange range, CancellationToken cancellationToken)
    {
        var snapshot = await dashboard.GetSnapshotAsync(range, cancellationToken);

        if (!snapshot.Settings.AiInsightsEnabled)
        {
            throw new AnalysisUnavailableException(AnalysisUnavailableReason.Disabled);
        }

        if (snapshot.Summary.TransactionCount == 0)
        {
            throw new AnalysisUnavailableException(AnalysisUnavailableReason.NotEnoughData);
        }

        var facts = BuildFacts(snapshot);
        var hash = facts.Hash();

        GeminiAnalysisResult? ai = null;
        string? fallback = null;
        if (!await gemini.IsConfiguredAsync(cancellationToken))
        {
            fallback = AnalysisFallback.NotConfigured;
        }
        else
        {
            try
            {
                ai = await gemini.AnalyzeFinancialDataAsync(facts, cancellationToken);
            }
            catch (AiUnavailableException ex)
            {
                fallback = AnalysisFallback.ReasonFor(ex.Failure);
            }
        }

        var old = await db.FinancialAnalyses.Where(a => a.PeriodStart == range.Start && a.PeriodEnd == range.End).ToListAsync(cancellationToken);

        if (ai is null)
        {
            // Never silently: a degraded install should look degraded in the logs.
            LogBuiltIn(logger, fallback!);

            // An AI analysis of exactly this data is better than a built-in one; keep it rather than replace it.
            var current = old.OrderByDescending(a => a.CreatedAt).FirstOrDefault();
            if (current is not null && current.FactsHash == hash && !AnalysisFallback.IsBuiltIn(current.Model))
            {
                return ToStored(current, AnalysisState.Fresh);
            }
        }

        var result = ai?.Result ?? BuiltInAnalysis.Write(facts);
        var now = time.GetUtcNow();
        var userId = await db.Users.Select(u => u.Id).SingleAsync(cancellationToken);
        var record = new FinancialAnalysisRecord
        {
            UserId = userId,
            PeriodStart = range.Start,
            PeriodEnd = range.End,
            FactsHash = hash,
            Model = ai?.Model ?? AnalysisFallback.ModelPrefix + fallback,
            ResultJson = JsonSerializer.Serialize(result.Analysis, Json),
            CorrectionsJson = JsonSerializer.Serialize(result.Corrections, Json),
            CreatedAt = now,
        };

        db.FinancialAnalyses.RemoveRange(old);
        db.FinancialAnalyses.Add(record);
        await db.SaveChangesAsync(cancellationToken);

        return new StoredAnalysis(AnalysisState.Fresh, result.Analysis, result.Corrections, ai?.Model, now,
            ai is null ? AnalysisSource.BuiltIn : AnalysisSource.Ai, fallback);
    }

    private static StoredAnalysis ToStored(FinancialAnalysisRecord record, AnalysisState state)
    {
        var builtIn = AnalysisFallback.IsBuiltIn(record.Model);
        return new StoredAnalysis(
            state,
            JsonSerializer.Deserialize<FinancialAnalysis>(record.ResultJson, Json),
            JsonSerializer.Deserialize<List<AnalysisCorrection>>(record.CorrectionsJson, Json) ?? [],
            builtIn ? null : record.Model,
            record.CreatedAt,
            builtIn ? AnalysisSource.BuiltIn : AnalysisSource.Ai,
            AnalysisFallback.ReasonOf(record.Model));
    }

    [LoggerMessage(LogLevel.Warning, "Analysis written without AI: {Reason}")]
    private static partial void LogBuiltIn(ILogger logger, string reason);

    public static FinancialFacts BuildFacts(FinancialSnapshot snapshot) =>
        FinancialFactsBuilder.Build(snapshot.Summary, snapshot.Recurring, snapshot.Anomalies, snapshot.Settings.Currency, snapshot.ResolveCategory);
}
