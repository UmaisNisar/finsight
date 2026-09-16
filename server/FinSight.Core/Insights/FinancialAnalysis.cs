namespace FinSight.Core.Insights;

public enum InsightSeverity
{
    Info,
    Attention,
    Positive,
}

public sealed record KeyInsight(string Title, string Description, InsightSeverity Severity);

/// <param name="CurrentMonthlySpending">Taken from the computed facts, never from the model.</param>
/// <param name="EstimatedMonthlySavings">Recomputed as current minus target when a target is given.</param>
public sealed record SavingsOpportunity(
    string CategoryId,
    string Category,
    decimal CurrentMonthlySpending,
    decimal? SuggestedMonthlyTarget,
    decimal? EstimatedMonthlySavings,
    string Explanation);

public sealed record RecurringExpenseInsight(string Merchant, decimal Amount, string Frequency, string? Note);

public sealed record AnomalyInsight(string Ref, string Merchant, string Description, decimal Amount, string Date, string Explanation);

public sealed record Recommendation(string Title, string Description, string? PotentialImpact);

/// <summary>A validated analysis. Only this type is stored and rendered, never raw model output.</summary>
public sealed record FinancialAnalysis(
    string Summary,
    IReadOnlyList<KeyInsight> KeyInsights,
    IReadOnlyList<SavingsOpportunity> SavingsOpportunities,
    IReadOnlyList<RecurringExpenseInsight> RecurringExpenses,
    IReadOnlyList<AnomalyInsight> Anomalies,
    IReadOnlyList<Recommendation> Recommendations,
    IReadOnlyList<string> Caveats);

/// <summary>Something the validator changed or removed. Shown to the user so corrections are never hidden.</summary>
public sealed record AnalysisCorrection(string Section, string Message);

public sealed record ValidatedAnalysis(FinancialAnalysis Analysis, IReadOnlyList<AnalysisCorrection> Corrections);

// Raw shapes as the model returns them. Every field is optional: nothing is trusted until validated.
#pragma warning disable CA2227, CA1002
public sealed class RawAnalysis
{
    public string? Summary { get; set; }
    public List<RawInsight>? KeyInsights { get; set; }
    public List<RawSavingsOpportunity>? SavingsOpportunities { get; set; }
    public List<RawRecurring>? RecurringExpenses { get; set; }
    public List<RawAnomaly>? Anomalies { get; set; }
    public List<RawRecommendation>? Recommendations { get; set; }
    public List<string>? Caveats { get; set; }
}

public sealed class RawInsight
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Severity { get; set; }
}

public sealed class RawSavingsOpportunity
{
    public string? CategoryId { get; set; }
    public decimal? SuggestedMonthlyTarget { get; set; }
    public decimal? EstimatedMonthlySavings { get; set; }
    public string? Explanation { get; set; }
}

public sealed class RawRecurring
{
    public string? Merchant { get; set; }
    public string? Note { get; set; }
}

public sealed class RawAnomaly
{
    public string? Ref { get; set; }
    public string? Description { get; set; }
    public string? Explanation { get; set; }
}

public sealed class RawRecommendation
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? PotentialImpact { get; set; }
}
#pragma warning restore CA2227, CA1002
