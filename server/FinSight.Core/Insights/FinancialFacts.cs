using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FinSight.Core.Analytics;
using FinSight.Core.Categories;

namespace FinSight.Core.Insights;

public sealed record CategoryFact(string Id, string Name, string Group, decimal Amount, decimal SharePercent, decimal MonthlyAverage,
    decimal PreviousAmount, decimal? ChangePercent, int Transactions, bool IsFixed);

public sealed record MerchantFact(string Merchant, string Category, decimal Amount, int Transactions);

public sealed record RecurringFact(string Merchant, string Category, decimal Amount, string Frequency, decimal MonthlyEquivalent, bool AmountVaries);

public sealed record AnomalyFact(string Ref, string Date, string Merchant, string Category, decimal Amount, string Reason, decimal? TypicalAmount);

public sealed record TransactionFact(string Date, string Merchant, string Category, decimal Amount);

public sealed record MonthFact(string Month, decimal Income, decimal Expenses, decimal NetCashFlow, decimal? SavingsRatePercent);

public sealed record PreviousPeriodFact(string Period, decimal Income, decimal Expenses, decimal? IncomeChangePercent, decimal? ExpenseChangePercent);

public sealed record IncomeFact(string Source, decimal Amount, int Transactions);

/// <summary>
/// The only financial data the AI model ever sees. Aggregates and merchant names: no account
/// numbers, no raw statement descriptions, no names or emails. Every number is pre-computed.
/// </summary>
public sealed record FinancialFacts(
    string Period,
    string PeriodStart,
    string PeriodEnd,
    string Currency,
    int MonthsInPeriod,
    int MonthsWithData,
    decimal Income,
    decimal Expenses,
    decimal NetCashFlow,
    decimal? SavingsRatePercent,
    decimal AverageMonthlyIncome,
    decimal AverageMonthlyExpenses,
    decimal FixedExpenses,
    decimal VariableExpenses,
    decimal Refunds,
    decimal Fees,
    decimal TransfersExcludedFromSpending,
    PreviousPeriodFact? PreviousPeriod,
    IReadOnlyList<IncomeFact> IncomeSources,
    IReadOnlyList<CategoryFact> Categories,
    IReadOnlyList<MerchantFact> TopMerchants,
    IReadOnlyList<RecurringFact> RecurringExpenses,
    IReadOnlyList<AnomalyFact> AnomalyCandidates,
    IReadOnlyList<TransactionFact> LargestTransactions,
    IReadOnlyList<MonthFact> MonthlyTrend,
    IReadOnlyList<string> DataNotes)
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    /// <summary>Identifies the exact data an analysis was generated from.</summary>
    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson())))[..32];
}

public static class FinancialFactsBuilder
{
    public static FinancialFacts Build(
        FinancialSummary summary,
        IReadOnlyList<RecurringSeries> recurring,
        IReadOnlyList<AnomalyCandidate> anomalies,
        string currency,
        Func<string, CategoryDefinition> resolveCategory)
    {
        var culture = CultureInfo.InvariantCulture;
        string Date(DateOnly d) => d.ToString("yyyy-MM-dd", culture);
        string CategoryName(string id) => resolveCategory(id).Name;

        var notes = new List<string>();
        if (summary.Coverage.IsPartial)
        {
            notes.Add($"Only {summary.Coverage.MonthsWithData} of {summary.Coverage.MonthsInRange} months in this period have imported statements. Totals cover imported data only; averages use months with data.");
        }

        if (summary.Transfers.Count > 0)
        {
            notes.Add($"{summary.Transfers.Count} transfers between the user's own accounts (including credit card payments) are excluded from income and expenses.");
        }

        if (summary.Refunds > 0)
        {
            notes.Add("Refunds are subtracted from expenses, not counted as income.");
        }

        if (!summary.Previous.HasData)
        {
            notes.Add("No data exists for the previous period, so period-over-period comparisons are unavailable.");
        }

        return new FinancialFacts(
            summary.Range.Label(),
            Date(summary.Range.Start),
            Date(summary.Range.End),
            currency,
            summary.Coverage.MonthsInRange,
            summary.Coverage.MonthsWithData,
            summary.Income,
            summary.Expenses,
            summary.NetCashFlow,
            summary.SavingsRate,
            summary.AverageMonthlyIncome,
            summary.AverageMonthlyExpenses,
            summary.FixedExpenses,
            summary.VariableExpenses,
            summary.Refunds,
            summary.Fees,
            summary.Transfers.Total,
            summary.Previous.HasData
                ? new PreviousPeriodFact(summary.Previous.Range.Label(), summary.Previous.Income, summary.Previous.Expenses,
                    summary.Previous.IncomeChangePercent, summary.Previous.ExpenseChangePercent)
                : null,
            summary.IncomeSources.Select(s => new IncomeFact(s.Name, s.Amount, s.TransactionCount)).ToList(),
            summary.Categories.Select(c => new CategoryFact(c.CategoryId, c.Name, c.GroupName, c.Amount, c.SharePercent, c.MonthlyAverage,
                c.PreviousAmount, c.ChangePercent, c.TransactionCount, c.IsFixed)).ToList(),
            summary.TopMerchants.Select(m => new MerchantFact(m.Merchant, CategoryName(m.CategoryId), m.Amount, m.TransactionCount)).ToList(),
            recurring.Where(r => !r.IsIncome && r.IsActive)
                .Select(r => new RecurringFact(r.Merchant, CategoryName(r.CategoryId), r.TypicalAmount, r.Frequency.ToString().ToLowerInvariant(),
                    r.MonthlyEquivalent, r.AmountVaries))
                .ToList(),
            anomalies.Select((a, i) => new AnomalyFact($"A{i + 1}", Date(a.Date), a.Merchant, CategoryName(a.CategoryId), a.Amount,
                a.Kind switch
                {
                    AnomalyKind.UnusuallyLarge => "much larger than usual for this category",
                    AnomalyKind.NewMerchant => "large payment to a merchant not seen before",
                    _ => "same merchant and amount charged twice on the same day",
                },
                a.TypicalAmount)).ToList(),
            summary.LargestExpenses.Select(t => new TransactionFact(Date(t.Date), t.Merchant, CategoryName(t.CategoryId), t.Amount)).ToList(),
            summary.Monthly.Where(m => m.HasData)
                .Select(m => new MonthFact(m.Month.ToString("yyyy-MM", culture), m.Income, m.Expenses, m.NetCashFlow, m.SavingsRate))
                .ToList(),
            notes);
    }
}
