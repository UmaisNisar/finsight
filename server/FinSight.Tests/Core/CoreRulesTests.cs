using FinSight.Core.Analytics;
using FinSight.Core.Categories;
using FinSight.Core.Domain;
using FinSight.Core.Insights;
using FinSight.Core.Parsing;
using FinSight.Core.Text;
using FinSight.Infrastructure.Pipeline;

namespace FinSight.Tests.Core;

public sealed class MoneyParserTests
{
    [Theory]
    [InlineData("1,234.56", 1234.56, AmountSignHint.None)]
    [InlineData("$1,234.56", 1234.56, AmountSignHint.None)]
    [InlineData("-45.00", 45.00, AmountSignHint.Negative)]
    [InlineData("45.00-", 45.00, AmountSignHint.Negative)]
    [InlineData("(45.00)", 45.00, AmountSignHint.Negative)]
    [InlineData("-$45.00", 45.00, AmountSignHint.Negative)]
    [InlineData("$-45.00", 45.00, AmountSignHint.Negative)]
    [InlineData("45.00CR", 45.00, AmountSignHint.Credit)]
    [InlineData("45.00DR", 45.00, AmountSignHint.Debit)]
    [InlineData("€1.234,56", 1234.56, AmountSignHint.None)]
    [InlineData("1 234,56", 1234.56, AmountSignHint.None)]
    [InlineData("CA$12.00", 12.00, AmountSignHint.None)]
    [InlineData("0.99", 0.99, AmountSignHint.None)]
    [InlineData("1234567.89", 1234567.89, AmountSignHint.None)]
    public void Parses_statement_money_formats(string token, double expected, AmountSignHint sign)
    {
        MoneyParser.TryParse(token, out var money).Should().BeTrue();
        money.Value.Should().Be((decimal)expected);
        money.Sign.Should().Be(sign);
    }

    [Theory]
    [InlineData("0421")]
    [InlineData("2026")]
    [InlineData("12.5")]
    [InlineData("1,234")]
    [InlineData("1.234.567")]
    [InlineData("1,234.567")]
    [InlineData("1.234,567.89")]
    [InlineData("$")]
    [InlineData("08/31")]
    [InlineData("")]
    public void Rejects_numbers_that_are_not_amounts(string token)
    {
        MoneyParser.IsMoney(token).Should().BeFalse();
    }
}

public sealed class MaskingTests
{
    [Theory]
    [InlineData("SIN 123 456 789 ON FILE", "SIN •••• ON FILE")]
    [InlineData("SSN 123-45-6789", "SSN ••••")]
    [InlineData("CARD 4520-1234-5678-9012 PURCHASE", "CARD ••••9012 PURCHASE")]
    [InlineData("TRANSFER TO ****5521", "TRANSFER TO ••••5521")]
    [InlineData("xxxx-xxxx-xxxx-1234", "••••1234")]
    public void Masks_identifiers(string input, string expected)
    {
        SensitiveDataMasker.Mask(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("PAYMENT 1,850.00 ON 2026-08-31")]
    [InlineData("STORE #1021 TORONTO ON")]
    [InlineData("CALL 416-555-0199")]
    [InlineData("REF 123456")]
    public void Leaves_amounts_dates_and_short_references_alone(string input)
    {
        SensitiveDataMasker.Mask(input).Should().Be(input);
    }

    [Fact]
    public void Masking_is_idempotent()
    {
        var once = SensitiveDataMasker.Mask("ACCT 000123-4567890 CARD 4520123456789012");

        SensitiveDataMasker.Mask(once).Should().Be(once);
        once.Should().NotContain("4567890").And.NotContain("452012345678");
    }

    [Theory]
    [InlineData("000123-4567890", "7890")]
    [InlineData("ending in 21", null)]
    [InlineData("  ", null)]
    [InlineData("4417", "4417")]
    public void Keeps_only_the_last_four_digits_of_an_account(string identifier, string? expected)
    {
        SensitiveDataMasker.LastFourOf(identifier).Should().Be(expected);
    }
}

public sealed class MerchantRuleTests
{
    private static Dictionary<string, MerchantRule> Rules(params MerchantRule[] rules) => rules.ToDictionary(r => r.MerchantKey);

    [Fact]
    public void An_income_rule_never_turns_a_payment_out_into_income()
    {
        var rules = Rules(new MerchantRule { MerchantKey = "acme", CategoryId = "income.salary", Source = CategorySource.Ai, Type = TransactionType.Income, Confidence = 0.9 });

        var salary = RuleCategorizer.Categorize(new CategorizationInput("ACME", "acme", 3100m, AccountType.Chequing), rules);
        var purchase = RuleCategorizer.Categorize(new CategorizationInput("ACME", "acme", -45m, AccountType.Chequing), rules);

        salary.CategoryId.Should().Be("income.salary");
        salary.Type.Should().Be(TransactionType.Income);
        purchase.Type.Should().Be(TransactionType.Expense);
        purchase.CategoryId.Should().NotStartWith("income.");
    }

    [Fact]
    public void Money_back_under_a_spending_rule_is_a_refund()
    {
        var rules = Rules(new MerchantRule { MerchantKey = "bestbuy", CategoryId = "shopping.electronics", Source = CategorySource.User, Confidence = 1 });

        var result = RuleCategorizer.Categorize(new CategorizationInput("BEST BUY", "bestbuy", 199.99m, AccountType.CreditCard), rules);

        result.CategoryId.Should().Be("shopping.electronics");
        result.Type.Should().Be(TransactionType.Expense);
        result.IsRefund.Should().BeTrue();
        result.Source.Should().Be(CategorySource.User);
    }

    [Fact]
    public void A_transfer_rule_keeps_both_directions_as_transfers()
    {
        var rules = Rules(new MerchantRule { MerchantKey = "wise", CategoryId = CategoryTaxonomy.Transfers, Source = CategorySource.User, Type = TransactionType.Transfer, Confidence = 1 });

        RuleCategorizer.Categorize(new CategorizationInput("WISE", "wise", -500m, AccountType.Chequing), rules).Type.Should().Be(TransactionType.Transfer);
        RuleCategorizer.Categorize(new CategorizationInput("WISE", "wise", 500m, AccountType.Chequing), rules).Type.Should().Be(TransactionType.Transfer);
    }

    [Theory]
    [InlineData("BURRITO BOYZ", "food.restaurants")]
    [InlineData("PAI NORTHERN THAI KITCHEN", "food.restaurants")]
    [InlineData("KINTON RAMEN", "food.restaurants")]
    public void Common_restaurant_words_are_recognised(string description, string categoryId)
    {
        RuleCategorizer.Categorize(new CategorizationInput(description, description.ToLowerInvariant(), -30m, AccountType.CreditCard)).CategoryId.Should().Be(categoryId);
    }
}

public sealed class TaxonomyTests
{
    [Fact]
    public void Category_ids_are_unique_and_every_category_has_a_known_group()
    {
        CategoryTaxonomy.All.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        CategoryTaxonomy.All.Should().OnlyContain(c => CategoryTaxonomy.FindGroup(c.GroupId) != null);
    }

    [Fact]
    public void Unknown_ids_resolve_to_uncategorized_and_custom_categories_resolve_by_id()
    {
        var custom = new CustomCategory { CategoryId = "custom.personal.dog-care", Name = "Dog care", GroupId = "personal" };

        CategoryTaxonomy.Resolve("does.not.exist").Id.Should().Be(CategoryTaxonomy.Uncategorized);
        var resolved = CategoryTaxonomy.Resolve("custom.personal.dog-care", [custom]);
        resolved.Name.Should().Be("Dog care");
        resolved.Kind.Should().Be(CategoryKind.Expense);
    }

    [Theory]
    [InlineData("financial.transfers", -10, TransactionType.Transfer)]
    [InlineData("financial.transfers", 10, TransactionType.Transfer)]
    [InlineData("income.salary", 10, TransactionType.Income)]
    [InlineData("income.salary", -10, TransactionType.Expense)]
    [InlineData("food.coffee", 10, TransactionType.Expense)]
    public void Categories_imply_a_type_for_each_direction(string categoryId, int amount, TransactionType expected)
    {
        CategoryTaxonomy.ImpliedType(CategoryTaxonomy.Resolve(categoryId), amount).Should().Be(expected);
    }

    [Fact]
    public void Every_failure_code_has_human_copy()
    {
        var codes = typeof(StatementFailure).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetValue(null)!).ToList();

        codes.Should().HaveCountGreaterThan(5);
        foreach (var code in codes)
        {
            var message = StatementFailure.Message(code);
            message.Should().NotBeNullOrWhiteSpace().And.NotContain(code).And.EndWith(".");
        }

        StatementFailure.Message("something_new").Should().Contain("Something went wrong");
        StatementFailure.Message(null).Should().BeEmpty();
    }
}

public sealed class AiValidationTests
{
    [Fact]
    public void The_same_merchant_can_be_categorized_in_each_direction()
    {
        var requests = new[]
        {
            new MerchantCategorizationRequest("M1", "acme", "Acme", "ACME", MerchantDirection.In, 3100m, 2),
            new MerchantCategorizationRequest("M2", "acme", "Acme", "ACME", MerchantDirection.Out, 45m, 1),
        };
        var raw = new RawMerchantCategorizationResponse
        {
            Results =
            [
                new RawMerchantCategorization { Ref = "m1", CategoryId = "income.salary", Confidence = 0.9 },
                new RawMerchantCategorization { Ref = " M2 ", CategoryId = "shopping.general", Confidence = 0.8 },
                new RawMerchantCategorization { Ref = "M2", CategoryId = "food.coffee", Confidence = 0.99 },
            ],
        };

        var results = MerchantCategorizationValidator.Validate(raw, requests);

        results.Should().HaveCount(2);
        results.Single(r => r.Direction == MerchantDirection.In).Type.Should().Be(TransactionType.Income);
        results.Single(r => r.Direction == MerchantDirection.Out).CategoryId.Should().Be("shopping.general");
    }

    [Theory]
    [InlineData(MerchantDirection.In, "food.coffee", 0.9)]
    [InlineData(MerchantDirection.Out, "income.salary", 0.9)]
    [InlineData(MerchantDirection.Out, "food.coffee", 0.4)]
    [InlineData(MerchantDirection.Out, CategoryTaxonomy.Uncategorized, 0.9)]
    public void Answers_that_do_not_fit_the_money_direction_or_are_unsure_are_dropped(MerchantDirection direction, string categoryId, double confidence)
    {
        var requests = new[] { new MerchantCategorizationRequest("M1", "acme", "Acme", "ACME", direction, 10m, 1) };
        var raw = new RawMerchantCategorizationResponse { Results = [new RawMerchantCategorization { Ref = "M1", CategoryId = categoryId, Confidence = confidence }] };

        MerchantCategorizationValidator.Validate(raw, requests).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Blue Door Cafe", "Blue Door Cafe")]
    [InlineData("12345", null)]
    [InlineData("X", null)]
    [InlineData("Card ••4417", null)]
    [InlineData("A merchant name that is far too long to be a sensible display", null)]
    public void Suggested_merchant_names_are_only_kept_when_sensible(string suggested, string? kept)
    {
        var requests = new[] { new MerchantCategorizationRequest("M1", "bluedoor", "Blue Door", "BLUE DOOR", MerchantDirection.Out, 10m, 1) };
        var raw = new RawMerchantCategorizationResponse { Results = [new RawMerchantCategorization { Ref = "M1", CategoryId = "food.coffee", Confidence = 0.9, Merchant = suggested }] };

        MerchantCategorizationValidator.Validate(raw, requests).Single().Merchant.Should().Be(kept);
    }

    [Fact]
    public void Recurring_reviews_keep_known_references_and_valid_kinds_only()
    {
        var requests = new[]
        {
            new RecurringReviewRequest("R1", "Netflix", "Subscriptions", 20.99m, "monthly", 12, false),
            new RecurringReviewRequest("R2", "Goodlife", "Fitness", 64.99m, "monthly", 12, false),
        };
        var raw = new RawRecurringReviewResponse
        {
            Results =
            [
                new RawRecurringReview { Ref = "R1", Kind = "subscription", Reason = "Streaming" },
                new RawRecurringReview { Ref = "R1", Kind = "loan", Reason = "Duplicate" },
                new RawRecurringReview { Ref = "R2", Kind = "not_recurring", Reason = new string('x', 500) },
                new RawRecurringReview { Ref = "R3", Kind = "bill" },
                new RawRecurringReview { Ref = "R2", Kind = "mortgage" },
            ],
        };

        var reviews = MerchantCategorizationValidator.ValidateRecurring(raw, requests);

        reviews.Should().HaveCount(2);
        reviews[0].Should().Be(new RecurringReview("R1", RecurringKind.Subscription, "Streaming"));
        reviews[1].Kind.Should().Be(RecurringKind.NotRecurring);
        reviews[1].Reason.Should().HaveLength(200);
    }
}

public sealed class FactsTests
{
    private static readonly DateRange August = new(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31));

    private static FinancialFacts Build(decimal rent = -1850m)
    {
        var transactions = new List<AnalyticsTransaction>
        {
            new(Guid.NewGuid(), new DateOnly(2026, 8, 1), 5200m, TransactionType.Income, "income.salary", "Acme Corp", "acmecorp", false, false),
            new(Guid.NewGuid(), new DateOnly(2026, 8, 2), rent, TransactionType.Expense, "housing.rent", "Maple Residential", "mapleresidential", false, false),
            new(Guid.NewGuid(), new DateOnly(2026, 8, 5), -500m, TransactionType.Transfer, CategoryTaxonomy.Transfers, "Savings", "savings", false, false),
        };
        var summary = FinancialMetricsCalculator.Calculate(transactions, August, new HashSet<string>(), id => CategoryTaxonomy.Resolve(id));
        return FinancialFactsBuilder.Build(summary, [], [], "CAD", id => CategoryTaxonomy.Resolve(id));
    }

    [Fact]
    public void Facts_carry_computed_totals_and_explain_what_was_excluded()
    {
        var facts = Build();

        facts.Income.Should().Be(5200m);
        facts.Expenses.Should().Be(1850m);
        facts.TransfersExcludedFromSpending.Should().Be(500m);
        facts.DataNotes.Should().Contain(n => n.Contains("transfers", StringComparison.OrdinalIgnoreCase));
        facts.DataNotes.Should().Contain(n => n.Contains("previous period", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_hash_is_stable_for_the_same_data_and_changes_with_it()
    {
        Build().Hash().Should().Be(Build().Hash());
        Build().Hash().Should().NotBe(Build(rent: -1900m).Hash());
    }
}

public sealed class PeriodTests
{
    [Fact]
    public void The_previous_period_of_whole_months_is_the_same_number_of_months()
    {
        var quarter = new DateRange(new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30));

        PeriodResolver.Previous(quarter).Should().Be(new DateRange(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31)));
    }

    [Fact]
    public void The_previous_period_of_a_custom_range_has_the_same_number_of_days()
    {
        var range = new DateRange(new DateOnly(2026, 3, 10), new DateOnly(2026, 3, 19));

        PeriodResolver.Previous(range).Should().Be(new DateRange(new DateOnly(2026, 2, 28), new DateOnly(2026, 3, 9)));
    }

    [Fact]
    public void This_month_covers_the_whole_calendar_month_across_year_ends()
    {
        PeriodResolver.Resolve(PeriodPreset.ThisMonth, new DateOnly(2026, 12, 31)).Should().Be(new DateRange(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31)));
        PeriodResolver.Resolve(PeriodPreset.LastMonth, new DateOnly(2027, 1, 1)).Should().Be(new DateRange(new DateOnly(2026, 12, 1), new DateOnly(2026, 12, 31)));
    }

    [Theory]
    [InlineData("2026-08-01", "2026-08-31", "August 2026")]
    [InlineData("2026-06-01", "2026-08-31", "Jun 1 – Aug 31, 2026")]
    [InlineData("2025-12-15", "2026-01-15", "Dec 15, 2025 – Jan 15, 2026")]
    public void Ranges_have_readable_labels(string start, string end, string label)
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        new DateRange(DateOnly.Parse(start, culture), DateOnly.Parse(end, culture)).Label().Should().Be(label);
    }

    [Fact]
    public void A_custom_range_needs_ordered_dates()
    {
        var act = () => PeriodResolver.Resolve(PeriodPreset.Custom, new DateOnly(2026, 9, 1), new DateOnly(2026, 5, 1), new DateOnly(2026, 4, 1));

        act.Should().Throw<ArgumentException>();
    }
}
