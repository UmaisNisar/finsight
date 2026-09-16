using FinSight.Core.Categories;

namespace FinSight.Core.Analytics;

public enum RecurrenceFrequency
{
    Weekly,
    Biweekly,
    Monthly,
    Quarterly,
    Annual,
}

public sealed record RecurringSeries(
    string MerchantKey,
    string Merchant,
    string CategoryId,
    bool IsIncome,
    RecurrenceFrequency Frequency,
    decimal TypicalAmount,
    decimal MonthlyEquivalent,
    bool AmountVaries,
    int Occurrences,
    DateOnly FirstDate,
    DateOnly LastDate,
    DateOnly NextExpectedDate,
    bool IsActive,
    double Confidence,
    IReadOnlyList<Guid> TransactionIds);

/// <summary>
/// Finds subscriptions, bills and regular income from transaction history. Pure and deterministic;
/// the AI layer may describe these series but never invents them.
/// </summary>
public static class RecurringDetector
{
    private sealed record Window(RecurrenceFrequency Frequency, int MinDays, int MaxDays, int Nominal, int MinOccurrences, decimal PerMonth);

    private static readonly Window[] Windows =
    [
        new(RecurrenceFrequency.Weekly, 6, 8, 7, 4, 52m / 12),
        new(RecurrenceFrequency.Biweekly, 13, 16, 14, 3, 26m / 12),
        new(RecurrenceFrequency.Monthly, 26, 35, 30, 2, 1m),
        new(RecurrenceFrequency.Quarterly, 84, 98, 91, 2, 1m / 3),
        new(RecurrenceFrequency.Annual, 350, 380, 365, 2, 1m / 12),
    ];

    /// <summary>Bills whose amount legitimately changes month to month.</summary>
    private static readonly HashSet<string> VariableBills =
    [
        "housing.utilities", "housing.phone-internet", "financial.insurance", "housing.rent", "housing.mortgage", "income.salary",
    ];

    public static IReadOnlyList<RecurringSeries> Detect(IEnumerable<AnalyticsTransaction> transactions, DateOnly asOf)
    {
        var series = new List<RecurringSeries>();

        var groups = transactions
            .Where(t => !t.IsIgnored && t.Type != Domain.TransactionType.Transfer && !t.IsRefund && t.MerchantKey != "unknown")
            .Where(t => t.Date <= asOf)
            .GroupBy(t => (t.MerchantKey, Inflow: t.Amount > 0));

        foreach (var group in groups)
        {
            var items = group.OrderBy(t => t.Date).ToList();
            if (items.Count < 2)
            {
                continue;
            }

            var intervals = items.Zip(items.Skip(1), (a, b) => b.Date.DayNumber - a.Date.DayNumber).ToList();
            var median = Median(intervals.Select(i => (decimal)i).ToList());
            var window = Windows.FirstOrDefault(w => median >= w.MinDays && median <= w.MaxDays);
            if (window is null || items.Count < window.MinOccurrences)
            {
                continue;
            }

            var regular = intervals.Count(i => i >= window.MinDays - 2 && i <= window.MaxDays + 2);
            if (regular < Math.Ceiling(intervals.Count * 0.75))
            {
                continue;
            }

            var amounts = items.Select(t => Math.Abs(t.Amount)).ToList();
            var typical = Median(amounts);
            var spread = amounts.Max() - amounts.Min();
            var amountVaries = spread > Math.Max(2m, typical * 0.1m);
            var categoryId = items[^1].CategoryId;

            if (amountVaries && !VariableBills.Contains(categoryId))
            {
                // Weekly grocery runs at the same store are habits, not bills.
                continue;
            }

            var nominalDays = window.Frequency == RecurrenceFrequency.Monthly ? 0 : window.Nominal;
            var next = window.Frequency == RecurrenceFrequency.Monthly
                ? items[^1].Date.AddMonths(1)
                : items[^1].Date.AddDays(nominalDays);

            var isActive = asOf.DayNumber - items[^1].Date.DayNumber <= window.MaxDays * 1.5;

            var confidence = items.Count switch
            {
                2 => 0.6,
                3 => 0.75,
                _ => 0.88,
            };

            var category = CategoryTaxonomy.Find(categoryId);
            if (category?.IsFixed == true)
            {
                confidence += 0.08;
            }

            if (amountVaries)
            {
                confidence -= 0.12;
            }

            series.Add(new RecurringSeries(
                group.Key.MerchantKey,
                items[^1].Merchant,
                categoryId,
                group.Key.Inflow,
                window.Frequency,
                decimal.Round(typical, 2),
                decimal.Round(typical * window.PerMonth, 2),
                amountVaries,
                items.Count,
                items[0].Date,
                items[^1].Date,
                next,
                isActive,
                Math.Clamp(confidence, 0.3, 0.97),
                items.Select(t => t.Id).ToList()));
        }

        return series.OrderByDescending(s => s.MonthlyEquivalent).ToList();
    }

    internal static decimal Median(IReadOnlyList<decimal> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.Order().ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 0 ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle];
    }
}
