using System.Text.Json;
using FinSight.Core.Categories;
using FinSight.Core.Insights;

namespace FinSight.Tests.Insights;

/// <summary>The analysis FinSight writes itself when Gemini isn't available.</summary>
public sealed class BuiltInAnalysisTests
{
    private static FinancialFacts Facts(string currency = "CAD") => new(
        "August 2026", "2026-08-01", "2026-08-31", currency, 1, 1,
        Income: 6200m, Expenses: 4180m, NetCashFlow: 2020m, SavingsRatePercent: 32.6m,
        AverageMonthlyIncome: 6200m, AverageMonthlyExpenses: 4180m, FixedExpenses: 2100m, VariableExpenses: 2080m,
        Refunds: 0m, Fees: 4.95m, TransfersExcludedFromSpending: 600m,
        PreviousPeriod: new PreviousPeriodFact("July 2026", 6200m, 4540m, 0m, -7.9m),
        IncomeSources: [new IncomeFact("Salary", 6200m, 2)],
        Categories:
        [
            new CategoryFact("housing.rent", "Rent", "Housing", 1850m, 44.3m, 1850m, 1850m, 0m, 1, true),
            new CategoryFact("food.groceries", "Groceries", "Food", 640.40m, 15.3m, 640.40m, 820.15m, -21.9m, 9, false),
            new CategoryFact("food.restaurants", "Restaurants", "Food", 210.49m, 5m, 210.49m, 157.20m, 33.9m, 5, false),
            new CategoryFact("entertainment.subscriptions", "Subscriptions", "Entertainment", 20.99m, 0.5m, 20.99m, 20.99m, 0m, 1, true),
        ],
        TopMerchants: [new MerchantFact("Landlord", "Rent", 1850m, 1), new MerchantFact("Loblaws", "Groceries", 402.18m, 4)],
        RecurringExpenses: [new RecurringFact("Netflix", "Subscriptions", 20.99m, "monthly", 20.99m, false), new RecurringFact("Rogers", "Phone & Internet", 85m, "monthly", 85m, false)],
        AnomalyCandidates: [new AnomalyFact("A1", "2026-08-08", "Steakhouse", "Restaurants", 420m, "much larger than usual for this category", 76m)],
        LargestTransactions: [new TransactionFact("2026-08-01", "Landlord", "Rent", 1850m)],
        MonthlyTrend: [new MonthFact("2026-08", 6200m, 4180m, 2020m, 32.6m)],
        DataNotes: []);

    [Fact]
    public void Summarizes_the_period_from_the_facts()
    {
        var analysis = BuiltInAnalysis.Write(Facts()).Analysis;

        analysis.Summary.Should().Be(
            "In August 2026, you earned $6,200 and spent $4,180, leaving $2,020. Spending was down 8% on July 2026. " +
            "Your largest category was Rent at $1,850, 44% of spending.");
    }

    [Fact]
    public void Key_insights_cover_savings_rate_category_changes_anomalies_and_the_top_merchant()
    {
        var insights = BuiltInAnalysis.Write(Facts()).Analysis.KeyInsights;

        insights.Select(i => i.Title).Should().Equal(
            "You kept 33% of your income", "Less on Groceries", "More on Restaurants", "One transaction stood out", "Most spent at Landlord", "Fees and interest");

        insights[0].Description.Should().Be("Income was $6,200 and spending $4,180, leaving $2,020. That's up from 27% in July 2026.");
        insights[0].Severity.Should().Be(InsightSeverity.Positive);
        insights[1].Description.Should().Be("You spent $640 on Groceries, $180 less than in July 2026 ($820).");
        insights[1].Severity.Should().Be(InsightSeverity.Positive);
        insights[2].Description.Should().Be("You spent $210 on Restaurants, $53 more than in July 2026 ($157).");
        insights[2].Severity.Should().Be(InsightSeverity.Attention);
        insights[3].Description.Should().Be("Steakhouse, $420 on Aug 8: much larger than usual for this category.");
        insights[4].Description.Should().Be("$1,850 across 1 transaction, in Rent.");
    }

    [Fact]
    public void Savings_opportunities_are_conservative_and_only_for_flexible_spending()
    {
        var opportunities = BuiltInAnalysis.Write(Facts()).Analysis.SavingsOpportunities;

        opportunities.Select(o => o.CategoryId).Should().Equal("food.restaurants", "food.groceries");
        opportunities[0].Should().Be(new SavingsOpportunity("food.restaurants", "Restaurants", 210.49m, 189.49m, 21m,
            "Restaurants rose 34% compared with July 2026. Spending a tenth less would keep about $21 a month."));
        opportunities[1].EstimatedMonthlySavings.Should().Be(64m);
        opportunities.Should().NotContain(o => o.CategoryId == "housing.rent", "rent is fixed");
    }

    [Fact]
    public void Recurring_payments_anomalies_and_caveats_come_from_the_facts_only()
    {
        var analysis = BuiltInAnalysis.Write(Facts()).Analysis;

        analysis.RecurringExpenses.Select(r => (r.Merchant, r.Amount)).Should().Equal(("Netflix", 20.99m), ("Rogers", 85m));
        analysis.Anomalies.Should().ContainSingle().Which.Should().Be(new AnomalyInsight("A1", "Steakhouse", "Steakhouse", 420m, "2026-08-08",
            "Much larger than usual for this category, where a typical payment is about $76."));
        analysis.Recommendations.Select(r => r.Title).Should().Equal("Review your recurring payments", "Check the transactions that stood out");
        analysis.Recommendations[0].Description.Should().Contain("$106 a month");
        analysis.Caveats.Should().Equal("Transfers between your own accounts, including card payments, aren't counted as income or spending.");
    }

    [Theory]
    [InlineData("CAD", "$6,200")]
    [InlineData("USD", "$6,200")]
    [InlineData("EUR", "€6,200")]
    [InlineData("GBP", "£6,200")]
    [InlineData("AUD", "A$6,200")]
    [InlineData("PKR", "Rs 6,200")]
    public void Every_figure_passes_the_validator_in_every_supported_currency(string currency, string income)
    {
        var result = BuiltInAnalysis.Write(Facts(currency));

        result.Corrections.Should().BeEmpty("every figure is taken from the facts, so the validator finds nothing to correct");
        result.Analysis.Summary.Should().Contain($"you earned {income} and");
    }

    [Fact]
    public void Sparse_facts_still_produce_a_valid_analysis_with_honest_caveats()
    {
        var facts = Facts() with
        {
            Income = 0m, NetCashFlow = -300m, Expenses = 300m, SavingsRatePercent = null, MonthsInPeriod = 3, MonthsWithData = 1,
            PreviousPeriod = null, Categories = [new CategoryFact("food.coffee", "Coffee", "Food", 300m, 100m, 100m, 0m, null, 30, false)],
            TopMerchants = [], RecurringExpenses = [], AnomalyCandidates = [], Fees = 0m, TransfersExcludedFromSpending = 0m,
        };

        var result = BuiltInAnalysis.Write(facts);

        result.Corrections.Should().BeEmpty();
        result.Analysis.Summary.Should().Be("In August 2026, you spent $300 and no income was recorded. Your largest category was Coffee at $300, 100% of spending.");
        result.Analysis.KeyInsights.Select(i => i.Title).Should().Equal("No income recorded");
        result.Analysis.SavingsOpportunities.Should().ContainSingle().Which.EstimatedMonthlySavings.Should().Be(10m);
        result.Analysis.Recommendations.Should().BeEmpty();
        result.Analysis.Caveats.Should().Equal(
            "Only 1 of 3 months in this period have imported statements, so totals may be incomplete.",
            "There's no data for the previous period, so nothing here is compared with it.");
    }

    [Fact]
    public void Spending_more_than_income_is_stated_plainly()
    {
        var facts = Facts() with { Income = 3000m, NetCashFlow = -1180m, SavingsRatePercent = -39.3m };

        var result = BuiltInAnalysis.Write(facts);

        result.Corrections.Should().BeEmpty();
        result.Analysis.Summary.Should().StartWith("In August 2026, you earned $3,000 and spent $4,180, which was $1,180 more than you earned.");
        result.Analysis.KeyInsights[0].Should().Be(new KeyInsight("Spending was more than income", "You spent $1,180 more than you earned in August 2026.", InsightSeverity.Attention));
    }

    [Fact]
    public void The_same_facts_always_produce_the_same_analysis()
    {
        var first = JsonSerializer.Serialize(BuiltInAnalysis.Write(Facts()));
        var second = JsonSerializer.Serialize(BuiltInAnalysis.Write(Facts()));

        second.Should().Be(first);
    }
}

public sealed class RecurringLabelerTests
{
    [Theory]
    [InlineData("Netflix", "entertainment.subscriptions", RecurringKind.Subscription)]
    [InlineData("Spotify", "other.uncategorized", RecurringKind.Subscription)]
    [InlineData("GitHub", "shopping.general", RecurringKind.Subscription)]
    [InlineData("Toronto Hydro", "housing.utilities", RecurringKind.Bill)]
    [InlineData("Rogers", "housing.phone-internet", RecurringKind.Bill)]
    [InlineData("Sonnet", "financial.insurance", RecurringKind.Bill)]
    [InlineData("Landlord", "housing.rent", RecurringKind.Bill)]
    [InlineData("GoodLife Fitness", "health.fitness", RecurringKind.Membership)]
    [InlineData("CAA Membership", "other.uncategorized", RecurringKind.Membership)]
    [InlineData("Maple Auto Loan", "other.uncategorized", RecurringKind.Loan)]
    [InlineData("First Mortgage Co", "housing.mortgage", RecurringKind.Loan)]
    public void Labels_recurring_expenses_from_known_merchants_and_categories(string merchant, string categoryId, RecurringKind expected) =>
        RecurringLabeler.Label(merchant, CategoryTaxonomy.Resolve(categoryId)).Should().Be(expected);

    [Theory]
    [InlineData("Tim Hortons", "food.coffee")]
    [InlineData("Corner Store", "other.uncategorized")]
    public void Anything_it_cannot_identify_is_left_unlabelled(string merchant, string categoryId) =>
        RecurringLabeler.Label(merchant, CategoryTaxonomy.Resolve(categoryId)).Should().BeNull();
}
