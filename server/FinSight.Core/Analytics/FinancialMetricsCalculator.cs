using FinSight.Core.Categories;

namespace FinSight.Core.Analytics;

public static class FinancialMetricsCalculator
{
    private const int TopMerchantCount = 10;
    private const int LargestExpenseCount = 10;

    /// <param name="transactions">Transactions covering at least the range and the previous period.</param>
    /// <param name="recurringExpenseMerchants">Merchant keys of recurring expenses, which count as fixed spending.</param>
    /// <param name="resolveCategory">Resolves built-in and the user's custom categories.</param>
    public static FinancialSummary Calculate(
        IReadOnlyList<AnalyticsTransaction> transactions,
        DateRange range,
        IReadOnlySet<string> recurringExpenseMerchants,
        Func<string, CategoryDefinition> resolveCategory)
    {
        var previousRange = PeriodResolver.Previous(range);
        var current = transactions.Where(t => range.Contains(t.Date)).ToList();
        var previous = transactions.Where(t => previousRange.Contains(t.Date)).ToList();

        var income = SumIncome(current);
        var grossSpending = -current.Where(t => t.IsSpending && t.Amount < 0).Sum(t => t.Amount);
        var refunds = current.Where(t => t.IsSpending && t.Amount > 0).Sum(t => t.Amount);
        var expenses = Math.Max(0, grossSpending - refunds);
        var net = income - expenses;

        var monthsWithData = current.Select(t => (t.Date.Year, t.Date.Month)).Distinct().Count();
        var divisor = Math.Max(1, monthsWithData);

        // Per-category net spending (purchases minus refunds), for this period and the previous one.
        var previousByCategory = SpendingByCategory(previous);
        var currentByCategory = SpendingByCategory(current);

        var categories = currentByCategory
            .Select(kv =>
            {
                var definition = resolveCategory(kv.Key);
                var group = CategoryTaxonomy.FindGroup(definition.GroupId);
                var previousAmount = previousByCategory.GetValueOrDefault(kv.Key).Amount;
                return new CategorySpending(
                    definition.Id,
                    definition.Name,
                    definition.GroupId,
                    group?.Name ?? definition.GroupId,
                    Round(kv.Value.Amount),
                    Share(kv.Value.Amount, expenses),
                    kv.Value.Count,
                    Round(previousAmount),
                    Change(kv.Value.Amount, previousAmount, previous.Count > 0),
                    Round(kv.Value.Amount / divisor),
                    definition.IsFixed);
            })
            .Where(c => c.Amount > 0)
            .OrderByDescending(c => c.Amount)
            .ToList();

        var groups = categories
            .GroupBy(c => (c.GroupId, c.GroupName))
            .Select(g =>
            {
                var previousAmount = previousByCategory
                    .Where(kv => resolveCategory(kv.Key).GroupId == g.Key.GroupId)
                    .Sum(kv => kv.Value.Amount);
                var amount = g.Sum(c => c.Amount);
                return new CategoryGroupSpending(g.Key.GroupId, g.Key.GroupName, amount, Share(amount, expenses), Round(previousAmount),
                    Change(amount, previousAmount, previous.Count > 0));
            })
            .OrderByDescending(g => g.Amount)
            .ToList();

        var fixedExpenses = current
            .Where(t => t.IsSpending && (resolveCategory(t.CategoryId).IsFixed || recurringExpenseMerchants.Contains(t.MerchantKey)))
            .Sum(t => -t.Amount);
        fixedExpenses = Math.Clamp(fixedExpenses, 0, expenses);

        var incomeSources = current
            .Where(t => t.IsIncome)
            .GroupBy(t => t.CategoryId)
            .Select(g => new IncomeSource(g.Key, resolveCategory(g.Key).Name, Round(g.Sum(t => t.Amount)), g.Count()))
            .OrderByDescending(s => s.Amount)
            .ToList();

        var topMerchants = current
            .Where(t => t.IsSpending)
            .GroupBy(t => t.MerchantKey)
            .Select(g => new MerchantSpending(g.Key, g.OrderByDescending(t => t.Date).First().Merchant, MostCommon(g.Select(t => t.CategoryId)),
                Round(-g.Sum(t => t.Amount)), g.Count(t => t.Amount < 0)))
            .Where(m => m.Amount > 0)
            .OrderByDescending(m => m.Amount)
            .Take(TopMerchantCount)
            .ToList();

        var largest = current
            .Where(t => t.IsSpending && t.Amount < 0)
            .OrderBy(t => t.Amount)
            .Take(LargestExpenseCount)
            .Select(t => new NotableTransaction(t.Id, t.Date, t.Merchant, t.CategoryId, Round(-t.Amount)))
            .ToList();

        var monthly = range.Months()
            .Select(month =>
            {
                var inMonth = current.Where(t => t.Date.Year == month.Year && t.Date.Month == month.Month).ToList();
                var monthIncome = SumIncome(inMonth);
                var monthExpenses = Math.Max(0, -inMonth.Where(t => t.IsSpending).Sum(t => t.Amount));
                return new MonthlyCashFlow(month, Round(monthIncome), Round(monthExpenses), Round(monthIncome - monthExpenses),
                    SavingsRate(monthIncome, monthIncome - monthExpenses), inMonth.Count > 0);
            })
            .ToList();

        var previousIncome = SumIncome(previous);
        var previousExpenses = Math.Max(0, -previous.Where(t => t.IsSpending).Sum(t => t.Amount));
        var transfers = current.Where(t => t.IsTransfer).ToList();

        return new FinancialSummary(
            range,
            Round(income),
            Round(expenses),
            Round(grossSpending),
            Round(refunds),
            Round(net),
            SavingsRate(income, net),
            Round(income / divisor),
            Round(expenses / divisor),
            Round(fixedExpenses),
            Round(expenses - fixedExpenses),
            Round(current.Where(t => t.IsSpending && t.CategoryId is CategoryTaxonomy.BankFees or CategoryTaxonomy.InterestCharges).Sum(t => -t.Amount)),
            Round(current.Where(t => t.IsIncome && t.CategoryId == CategoryTaxonomy.InterestIncome).Sum(t => t.Amount)),
            new TransferSummary(transfers.Count, Round(transfers.Where(t => t.Amount < 0).Sum(t => -t.Amount))),
            new PeriodComparison(previousRange, previous.Count > 0, Round(previousIncome), Round(previousExpenses),
                Change(income, previousIncome, previous.Count > 0), Change(expenses, previousExpenses, previous.Count > 0)),
            categories,
            groups,
            incomeSources,
            topMerchants,
            largest,
            monthly,
            current.Count(t => !t.IsIgnored),
            new DataCoverage(
                current.Count == 0 ? null : current.Min(t => t.Date),
                current.Count == 0 ? null : current.Max(t => t.Date),
                range.Months().Count(),
                monthsWithData));
    }

    private static decimal SumIncome(IEnumerable<AnalyticsTransaction> transactions) =>
        transactions.Where(t => t.IsIncome).Sum(t => t.Amount);

    private static Dictionary<string, (decimal Amount, int Count)> SpendingByCategory(IEnumerable<AnalyticsTransaction> transactions) =>
        transactions
            .Where(t => t.IsSpending)
            .GroupBy(t => t.CategoryId)
            .ToDictionary(g => g.Key, g => (Math.Max(0, -g.Sum(t => t.Amount)), g.Count(t => t.Amount < 0)));

    /// <summary>Savings rate as a percentage with one decimal, or null when there was no income.</summary>
    public static decimal? SavingsRate(decimal income, decimal net) =>
        income > 0 ? decimal.Round(net / income * 100, 1, MidpointRounding.AwayFromZero) : null;

    private static decimal Share(decimal part, decimal total) =>
        total > 0 ? decimal.Round(part / total * 100, 1, MidpointRounding.AwayFromZero) : 0;

    private static decimal? Change(decimal current, decimal previous, bool hasPrevious) =>
        hasPrevious && previous > 0 ? decimal.Round((current - previous) / previous * 100, 1, MidpointRounding.AwayFromZero) : null;

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string MostCommon(IEnumerable<string> values) =>
        values.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
}
