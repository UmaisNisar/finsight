using FinSight.Core.Categories;
using FinSight.Core.Domain;
using FinSight.Core.Normalization;
using FinSight.Core.Text;

namespace FinSight.Tests.Categorization;

public class CategorizationTests
{
    private static CategorizationResult Categorize(string description, decimal amount, AccountType accountType = AccountType.Chequing,
        IReadOnlyDictionary<string, MerchantRule>? rules = null) =>
        RuleCategorizer.Categorize(new CategorizationInput(description, MerchantNormalizer.Normalize(description).Key, amount, accountType), rules);

    [Theory]
    [InlineData("NETFLIX.COM 866-579-7172 CA", "Netflix", CategoryTaxonomy.Subscriptions)]
    [InlineData("UBER* EATS PENDING TORONTO", "Uber Eats", "food.delivery")]
    [InlineData("UBER *TRIP HELP.UBER.COM", "Uber", "transportation.ride-sharing")]
    [InlineData("SQ *STARBUCKS 0421 TORONTO ON", "Starbucks", "food.coffee")]
    [InlineData("POS PURCHASE LOBLAWS #221 TORONTO ON", "Loblaws", "food.groceries")]
    [InlineData("SHELL C12345 MISSISSAUGA ON", "Shell", "transportation.fuel")]
    [InlineData("AMZN Mktp CA*RT4KX", "Amazon", "shopping.general")]
    public void Categorizes_known_merchants(string description, string merchant, string categoryId)
    {
        var result = Categorize(description, -20m);

        MerchantNormalizer.Normalize(description).Display.Should().Be(merchant);
        result.CategoryId.Should().Be(categoryId);
        result.Type.Should().Be(TransactionType.Expense);
        result.NeedsAi.Should().BeFalse();
    }

    [Fact]
    public void Treats_payment_on_card_statement_as_transfer_not_income()
    {
        var result = Categorize("PAYMENT - THANK YOU", 812.40m, AccountType.CreditCard);

        result.Type.Should().Be(TransactionType.Transfer);
        result.CategoryId.Should().Be(CategoryTaxonomy.CreditCardPayments);
    }

    [Theory]
    [InlineData("TD VISA PAYMENT", CategoryTaxonomy.CreditCardPayments)]
    [InlineData("ONLINE BANKING PAYMENT AMEX", CategoryTaxonomy.CreditCardPayments)]
    [InlineData("WEALTHSIMPLE INVESTMENTS TFSA", CategoryTaxonomy.Investments)]
    [InlineData("INTERNET TRANSFER TO SAVINGS 0042", CategoryTaxonomy.Transfers)]
    public void Treats_own_account_movements_as_transfers(string description, string categoryId)
    {
        var result = Categorize(description, -500m);

        result.Type.Should().Be(TransactionType.Transfer);
        result.CategoryId.Should().Be(categoryId);
    }

    [Fact]
    public void Categorizes_payroll_interest_and_fees()
    {
        Categorize("PAYROLL DEPOSIT ACME CORP", 3100m).CategoryId.Should().Be("income.salary");
        Categorize("INTEREST PAID", 1.23m).CategoryId.Should().Be(CategoryTaxonomy.InterestIncome);
        Categorize("MONTHLY ACCOUNT FEE", -4.95m).CategoryId.Should().Be(CategoryTaxonomy.BankFees);
        Categorize("PURCHASE INTEREST CHARGE", -18.20m).CategoryId.Should().Be(CategoryTaxonomy.InterestCharges);
    }

    [Fact]
    public void Money_back_from_a_merchant_is_a_refund_that_offsets_spending()
    {
        var result = Categorize("AMZN Mktp CA REFUND", 12m, AccountType.CreditCard);

        result.IsRefund.Should().BeTrue();
        result.Type.Should().Be(TransactionType.Expense);
        result.CategoryId.Should().Be("shopping.general");
    }

    [Fact]
    public void Person_to_person_payments_count_but_are_flagged_for_review()
    {
        var result = Categorize("SEND E-TFR JANE DOE", -120m);

        result.Type.Should().Be(TransactionType.Expense);
        result.NeedsAi.Should().BeTrue();
    }

    [Fact]
    public void Unknown_merchants_are_sent_for_ai_categorization()
    {
        var result = Categorize("ZXQ HOLDINGS 4411", -64m);

        result.CategoryId.Should().Be(CategoryTaxonomy.Uncategorized);
        result.NeedsAi.Should().BeTrue();
    }

    [Fact]
    public void User_rules_take_precedence_over_built_in_rules()
    {
        var key = MerchantNormalizer.Normalize("NETFLIX.COM").Key;
        var rules = new Dictionary<string, MerchantRule>
        {
            [key] = new() { MerchantKey = key, CategoryId = "personal.education", Source = CategorySource.User, Confidence = 1 },
        };

        var result = Categorize("NETFLIX.COM", -22.99m, rules: rules);

        result.CategoryId.Should().Be("personal.education");
        result.Source.Should().Be(CategorySource.User);
    }

    [Fact]
    public void Merchant_normalizer_strips_processor_prefixes_store_numbers_and_location()
    {
        var name = MerchantNormalizer.Normalize("POS PURCHASE SQ *BLUE DOOR CAFE #0042 TORONTO ON");

        name.Display.Should().Be("Blue Door Cafe");
        name.Key.Should().Be("bluedoorcafe");
    }

    [Theory]
    [InlineData("CARD 4520 1234 5678 9012", "CARD ••••9012")]
    [InlineData("ACCT 1234567890 TRANSFER", "ACCT ••••7890 TRANSFER")]
    [InlineData("CARD XXXXXXXXXXXX4417", "CARD ••••4417")]
    [InlineData("STARBUCKS 0421", "STARBUCKS 0421")]
    public void Masks_card_and_account_numbers(string input, string expected)
    {
        SensitiveDataMasker.Mask(input).Should().Be(expected);
    }
}
