using System.Text.Json;
using FinSight.Core.Abstractions;
using FinSight.Core.Analytics;
using FinSight.Core.Domain;
using FinSight.Core.Insights;
using FinSight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FinSight.Infrastructure.Insights;

public enum AnalysisState
{
    /// <summary>An analysis exists and was generated from exactly the current data.</summary>
    Fresh,

    /// <summary>An analysis exists but transactions changed since it was generated.</summary>
    Stale,

    None,
}

public sealed record StoredAnalysis(
    AnalysisState State,
    FinancialAnalysis? Analysis,
    IReadOnlyList<AnalysisCorrection> Corrections,
    string? Model,
    DateTimeOffset? GeneratedAt);

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

/// <summary>Builds privacy-conscious facts, asks Gemini to interpret them, and stores the validated result.</summary>
public sealed class AnalysisService(FinSightDbContext db, DashboardService dashboard, IGeminiService gemini, TimeProvider time)
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

        return new StoredAnalysis(
            facts.Hash() == record.FactsHash ? AnalysisState.Fresh : AnalysisState.Stale,
            JsonSerializer.Deserialize<FinancialAnalysis>(record.ResultJson, Json),
            JsonSerializer.Deserialize<List<AnalysisCorrection>>(record.CorrectionsJson, Json) ?? [],
            record.Model,
            record.CreatedAt);
    }

    public async Task<StoredAnalysis> GenerateAsync(DateRange range, CancellationToken cancellationToken)
    {
        var snapshot = await dashboard.GetSnapshotAsync(range, cancellationToken);

        if (!snapshot.Settings.AiInsightsEnabled)
        {
            throw new AnalysisUnavailableException(AnalysisUnavailableReason.Disabled);
        }

        if (!await gemini.IsConfiguredAsync(cancellationToken))
        {
            throw new AnalysisUnavailableException(AnalysisUnavailableReason.NotConfigured);
        }

        if (snapshot.Summary.TransactionCount == 0)
        {
            throw new AnalysisUnavailableException(AnalysisUnavailableReason.NotEnoughData);
        }

        var facts = BuildFacts(snapshot);
        var result = await gemini.AnalyzeFinancialDataAsync(facts, cancellationToken);
        var now = time.GetUtcNow();

        var userId = await db.Users.Select(u => u.Id).SingleAsync(cancellationToken);
        var old = await db.FinancialAnalyses.Where(a => a.PeriodStart == range.Start && a.PeriodEnd == range.End).ToListAsync(cancellationToken);
        db.FinancialAnalyses.RemoveRange(old);
        db.FinancialAnalyses.Add(new FinancialAnalysisRecord
        {
            UserId = userId,
            PeriodStart = range.Start,
            PeriodEnd = range.End,
            FactsHash = facts.Hash(),
            Model = result.Model,
            ResultJson = JsonSerializer.Serialize(result.Result.Analysis, Json),
            CorrectionsJson = JsonSerializer.Serialize(result.Result.Corrections, Json),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(cancellationToken);

        return new StoredAnalysis(AnalysisState.Fresh, result.Result.Analysis, result.Result.Corrections, result.Model, now);
    }

    public static FinancialFacts BuildFacts(FinancialSnapshot snapshot) =>
        FinancialFactsBuilder.Build(snapshot.Summary, snapshot.Recurring, snapshot.Anomalies, snapshot.Settings.Currency, snapshot.ResolveCategory);
}
