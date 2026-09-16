using FinSight.Core.Analytics;
using FinSight.Core.Categories;
using FinSight.Core.Domain;

namespace FinSight.Tests.Analytics;

public class AnalyticsTests
{
    private static readonly DateRange August = new(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));

    private static AnalyticsTransaction Tx(int month, int day, decimal amount, string categoryId, string merchant,
        TransactionType? type = null, bool refund = false, bool ignored = false) =>
        new(Guid.NewGuid(), new DateOnly(2026, month, day), amount,
            type ?? (amount > 0 && !refund ? TransactionType.Income : TransactionType.Expense),
            categoryId, merchant, merchant.ToLowerInvariant().Replace(" ", "", StringComparison.Ordinal), refund, ignored);

    private static FinancialSummary Calculate(IReadOnlyList<AnalyticsTransaction> transactions, DateRange? range = null) =>
        FinancialMetricsCalculator.Calculate(transactions, range ?? August, new HashSet<string>(), id => CategoryTaxonomy.Resolve(id));

    [Fact]
    public void Computes_income_expenses_savings_and_rate_deterministically()
    {
        var transactions = new List<AnalyticsTransaction>
        {
            Tx(8, 1, 3100m, "income.salary", "Acme"),
            Tx(8, 15, 3100m, "income.salary", "Acme"),
            Tx(8, 1, -1850m, "housing.rent", "Landlord"),
            Tx(8, 3, -620m, "food.groceries", "Loblaws"),
            Tx(8, 5, -410m, "transportation.fuel", "Shell"),
            Tx(8, 9, -1300m, "shopping.general", "Amazon"),
            Tx(8, 12, -12m, CategoryTaxonomy.Subscriptions, "Spotify"),
            Tx(8, 14, 12m, "shopping.general", "Amazon", refund: true),
            // Transfers and excluded rows never count.
            Tx(8, 20, -600m, CategoryTaxonomy.CreditCardPayments, "Visa", TransactionType.Transfer),
            Tx(8, 21, -999m, "shopping.general", "Excluded", ignored: true),
        };

        var summary = Calculate(transactions);

        summary.Income.Should().Be(6200m);
        summary.GrossSpending.Should().Be(4192m);
        summary.Refunds.Should().Be(12m);
        summary.Expenses.Should().Be(4180m);
        summary.NetCashFlow.Should().Be(2020m);
        summary.SavingsRate.Should().Be(32.6m);
        summary.Transfers.Should().Be(new TransferSummary(1, 600m));
        summary.Categories.First(c => c.CategoryId == "shopping.general").Amount.Should().Be(1288m);
        summary.FixedExpenses.Should().Be(1862m);
        summary.Categories.Sum(c => c.Amount).Should().Be(summary.Expenses);
    }

    [Fact]
    public void Compares_with_the_previous_period()
    {
        var transactions = new List<AnalyticsTransaction>
        {
            Tx(7, 3, -500m, "food.restaurants", "Bistro"),
            Tx(8, 3, -670m, "food.restaurants", "Bistro"),
        };

        var summary = Calculate(transactions);

        summary.Previous.Range.Should().Be(new DateRange(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31)));
        summary.Previous.ExpenseChangePercent.Should().Be(34.0m);
        summary.Categories.Single().ChangePercent.Should().Be(34.0m);
    }

    [Fact]
    public void Savings_rate_is_null_without_income()
    {
        Calculate([Tx(8, 3, -20m, "food.coffee", "Cafe")]).SavingsRate.Should().BeNull();
    }

    [Fact]
    public void Averages_use_months_that_have_data_and_report_partial_coverage()
    {
        var range = new DateRange(new DateOnly(2026, 3, 1), new DateOnly(2026, 8, 31));
        var summary = Calculate([Tx(7, 3, -300m, "food.groceries", "Store"), Tx(8, 3, -500m, "food.groceries", "Store")], range);

        summary.AverageMonthlyExpenses.Should().Be(400m);
        summary.Coverage.MonthsInRange.Should().Be(6);
        summary.Coverage.MonthsWithData.Should().Be(2);
        summary.Coverage.IsPartial.Should().BeTrue();
    }

    [Theory]
    [InlineData(PeriodPreset.LastMonth, "2026-08-01", "2026-08-31")]
    [InlineData(PeriodPreset.ThisMonth, "2026-09-01", "2026-09-30")]
    [InlineData(PeriodPreset.Last3Months, "2026-06-01", "2026-08-31")]
    [InlineData(PeriodPreset.Last12Months, "2025-09-01", "2026-08-31")]
    public void Resolves_period_presets_to_complete_months(PeriodPreset preset, string start, string end)
    {
        PeriodResolver.Resolve(preset, new DateOnly(2026, 9, 16))
            .Should().Be(new DateRange(DateOnly.Parse(start), DateOnly.Parse(end)));
    }

    [Fact]
    public void Detects_monthly_subscription_and_biweekly_salary()
    {
        var transactions = new List<AnalyticsTransaction>();
        for (var month = 3; month <= 8; month++)
        {
            transactions.Add(Tx(month, 12, -22.99m, CategoryTaxonomy.Subscriptions, "Netflix"));
        }

        for (var day = new DateOnly(2026, 6, 5); day <= new DateOnly(2026, 8, 31); day = day.AddDays(14))
        {
            transactions.Add(Tx(day.Month, day.Day, 2400m, "income.salary", "Acme"));
        }

        // Weekly groceries with varying amounts are a habit, not a recurring bill.
        for (var day = new DateOnly(2026, 6, 1); day <= new DateOnly(2026, 8, 31); day = day.AddDays(7))
        {
            transactions.Add(Tx(day.Month, day.Day, -(60m + day.Day * 3), "food.groceries", "Loblaws"));
        }

        var series = RecurringDetector.Detect(transactions, new DateOnly(2026, 9, 1));

        var netflix = series.Single(s => s.Merchant == "Netflix");
        netflix.Frequency.Should().Be(RecurrenceFrequency.Monthly);
        netflix.MonthlyEquivalent.Should().Be(22.99m);
        netflix.IsActive.Should().BeTrue();
        netflix.NextExpectedDate.Should().Be(new DateOnly(2026, 9, 12));

        var salary = series.Single(s => s.Merchant == "Acme");
        salary.IsIncome.Should().BeTrue();
        salary.Frequency.Should().Be(RecurrenceFrequency.Biweekly);

        series.Should().NotContain(s => s.Merchant == "Loblaws");
    }

    [Fact]
    public void Flags_unusually_large_and_duplicate_transactions()
    {
        var transactions = new List<AnalyticsTransaction>
        {
            Tx(6, 2, -80m, "food.restaurants", "Bistro"),
            Tx(6, 9, -65m, "food.restaurants", "Bistro"),
            Tx(7, 2, -72m, "food.restaurants", "Grill"),
            Tx(7, 20, -90m, "food.restaurants", "Pho"),
            Tx(8, 8, -420m, "food.restaurants", "Steakhouse"),
            Tx(8, 10, -45m, "shopping.general", "Store"),
            Tx(8, 10, -45m, "shopping.general", "Store"),
        };

        var anomalies = AnomalyDetector.Detect(transactions, August);

        anomalies.Should().Contain(a => a.Merchant == "Steakhouse" && a.Kind == AnomalyKind.UnusuallyLarge);
        anomalies.Should().Contain(a => a.Merchant == "Store" && a.Kind == AnomalyKind.PossibleDuplicate);
    }
}
