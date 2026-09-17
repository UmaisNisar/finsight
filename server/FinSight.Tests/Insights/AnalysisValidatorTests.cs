using FinSight.Core.Insights;

namespace FinSight.Tests.Insights;

public class AnalysisValidatorTests
{
    private static FinancialFacts Facts() => new(
        "August 2026", "2026-08-01", "2026-08-31", "CAD", 1, 1,
        Income: 6200m, Expenses: 4180m, NetCashFlow: 2020m, SavingsRatePercent: 32.6m,
        AverageMonthlyIncome: 6200m, AverageMonthlyExpenses: 4180m, FixedExpenses: 2100m, VariableExpenses: 2080m,
        Refunds: 0m, Fees: 4.95m, TransfersExcludedFromSpending: 600m,
        PreviousPeriod: new PreviousPeriodFact("July 2026", 6200m, 4540m, 0m, -7.9m),
        IncomeSources: [new IncomeFact("Salary", 6200m, 2)],
        Categories:
        [
            new CategoryFact("housing.rent", "Rent", "Housing", 1850m, 44.3m, 1850m, 1850m, 0m, 1, true),
            new CategoryFact("food.restaurants", "Restaurants", "Food", 210m, 5m, 210m, 157m, 33.8m, 5, false),
        ],
        TopMerchants: [new MerchantFact("Landlord", "Rent", 1850m, 1)],
        RecurringExpenses: [new RecurringFact("Netflix", "Subscriptions", 20.99m, "monthly", 20.99m, false)],
        AnomalyCandidates: [new AnomalyFact("A1", "2026-08-08", "Steakhouse", "Restaurants", 420m, "much larger than usual", 76m)],
        LargestTransactions: [new TransactionFact("2026-08-01", "Landlord", "Rent", 1850m)],
        MonthlyTrend: [new MonthFact("2026-08", 6200m, 4180m, 2020m, 32.6m)],
        DataNotes: []);

    [Fact]
    public void Keeps_valid_analysis_and_overwrites_numbers_the_app_owns()
    {
        var raw = new RawAnalysis
        {
            Summary = "You earned $6,200 and spent $4,180, saving 32.6% of your income.",
            KeyInsights = [new RawInsight { Title = "Restaurants up", Description = "Restaurant spending rose to $210.", Severity = "attention" }],
            SavingsOpportunities = [new RawSavingsOpportunity { CategoryId = "food.restaurants", SuggestedMonthlyTarget = 168m, EstimatedMonthlySavings = 40m, Explanation = "Cut back a little." }],
            RecurringExpenses = [new RawRecurring { Merchant = "netflix", Note = "Review if still used." }],
            Anomalies = [new RawAnomaly { Ref = "A1", Description = "Big dinner", Explanation = "Much larger than your usual $76." }],
            Recommendations = [new RawRecommendation { Title = "Set a dining target", Description = "Aim for $168 a month on restaurants." }],
        };

        var result = AnalysisValidator.Validate(raw, Facts());

        var opportunity = result.Analysis.SavingsOpportunities.Single();
        opportunity.CurrentMonthlySpending.Should().Be(210m);
        opportunity.EstimatedMonthlySavings.Should().Be(42m);
        result.Corrections.Should().ContainSingle(c => c.Message.Contains("Recalculated"));

        result.Analysis.RecurringExpenses.Single().Should().Be(new RecurringExpenseInsight("Netflix", 20.99m, "monthly", "Review if still used."));
        result.Analysis.Anomalies.Single().Amount.Should().Be(420m);
        result.Analysis.KeyInsights.Single().Severity.Should().Be(InsightSeverity.Attention);
    }

    [Fact]
    public void Removes_invented_categories_merchants_and_anomalies()
    {
        var raw = new RawAnalysis
        {
            Summary = "Summary.",
            SavingsOpportunities = [new RawSavingsOpportunity { CategoryId = "shopping.jewelry", SuggestedMonthlyTarget = 10m, Explanation = "x" }],
            RecurringExpenses = [new RawRecurring { Merchant = "Hulu" }],
            Anomalies = [new RawAnomaly { Ref = "A9", Explanation = "Made up." }],
        };

        var result = AnalysisValidator.Validate(raw, Facts());

        result.Analysis.SavingsOpportunities.Should().BeEmpty();
        result.Analysis.RecurringExpenses.Should().BeEmpty();
        result.Analysis.Anomalies.Should().BeEmpty();
        result.Corrections.Should().HaveCount(3);
    }

    [Fact]
    public void Rejects_targets_that_are_not_savings()
    {
        var raw = new RawAnalysis
        {
            Summary = "Summary.",
            SavingsOpportunities = [new RawSavingsOpportunity { CategoryId = "food.restaurants", SuggestedMonthlyTarget = 500m, EstimatedMonthlySavings = 900m, Explanation = "x" }],
        };

        var result = AnalysisValidator.Validate(raw, Facts());

        var opportunity = result.Analysis.SavingsOpportunities.Single();
        opportunity.SuggestedMonthlyTarget.Should().BeNull();
        opportunity.EstimatedMonthlySavings.Should().BeNull();
    }

    [Fact]
    public void Flags_currency_figures_that_do_not_match_the_data()
    {
        var raw = new RawAnalysis { Summary = "You spent $4,180 but also $9,999 on something." };

        var result = AnalysisValidator.Validate(raw, Facts());

        result.Corrections.Should().ContainSingle(c => c.Message.Contains("$9,999"));
    }

    [Theory]
    [InlineData("Rs 9,999")]
    [InlineData("Rs. 9,999")]
    [InlineData("₨9,999")]
    [InlineData("A$9,999")]
    [InlineData("PKR 9,999")]
    public void Flags_unmatched_figures_in_australian_dollars_and_rupees(string figure)
    {
        var raw = new RawAnalysis { Summary = $"You spent $4,180 but also {figure} on something." };

        var result = AnalysisValidator.Validate(raw, Facts());

        result.Corrections.Should().ContainSingle(c => c.Message.Contains("9,999"));
    }

    [Fact]
    public void Validates_ai_merchant_categorization_against_the_taxonomy()
    {
        var requests = new[]
        {
            new MerchantCategorizationRequest("M1", "zxqholdings", "Zxq Holdings", "ZXQ HOLDINGS", MerchantDirection.Out, 64m, 2),
            new MerchantCategorizationRequest("M2", "bluecafe", "Blue Cafe", "BLUE CAFE", MerchantDirection.Out, 8m, 5),
            new MerchantCategorizationRequest("M3", "acme", "Acme", "ACME", MerchantDirection.Out, 50m, 1),
        };
        var raw = new RawMerchantCategorizationResponse
        {
            Results =
            [
                new RawMerchantCategorization { Ref = "M1", CategoryId = "made.up", Confidence = 0.9 },
                new RawMerchantCategorization { Ref = "M2", CategoryId = "food.coffee", Merchant = "Blue Cafe", Confidence = 0.8, Reason = "Cafe" },
                new RawMerchantCategorization { Ref = "M3", CategoryId = "income.salary", Confidence = 0.9 },
                new RawMerchantCategorization { Ref = "M4", CategoryId = "food.coffee", Confidence = 0.9 },
            ],
        };

        var results = MerchantCategorizationValidator.Validate(raw, requests);

        results.Should().ContainSingle().Which.CategoryId.Should().Be("food.coffee");
    }
}
