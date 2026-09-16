using FinSight.Core.Categories;
using FinSight.Core.Domain;
using FinSight.Core.Normalization;
using FinSight.Core.Parsing;

namespace FinSight.Tests.Normalization;

public class NormalizationTests
{
    private static ParsedStatement Statement(params ParsedTransaction[] transactions) => new(
        new StatementMetadata("TD Bank", AccountType.Chequing, "8901", "CAD", new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31), null, null),
        transactions, [], 0.9, false, ParseFailure.None);

    private static ParsedTransaction Row(int day, string description, decimal amount) =>
        new(new DateOnly(2026, 8, day), null, description, amount, null, 0.9, 1);

    [Fact]
    public void Fingerprints_are_stable_and_distinguish_identical_same_day_purchases()
    {
        var key = TransactionNormalizer.AccountKey("TD Bank", AccountType.Chequing, "8901");
        var statement = Statement(Row(4, "STARBUCKS 0421", -6.45m), Row(4, "STARBUCKS 0421", -6.45m), Row(5, "NETFLIX.COM", -22.99m));

        var first = TransactionNormalizer.Normalize(statement, key);
        var second = TransactionNormalizer.Normalize(statement, key);

        first.Select(t => t.Fingerprint).Should().Equal(second.Select(t => t.Fingerprint));
        first.Select(t => t.Fingerprint).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Same_transaction_in_a_different_account_gets_a_different_fingerprint()
    {
        var statement = Statement(Row(5, "NETFLIX.COM", -22.99m));

        var chequing = TransactionNormalizer.Normalize(statement, TransactionNormalizer.AccountKey("TD Bank", AccountType.Chequing, "8901"));
        var card = TransactionNormalizer.Normalize(statement, TransactionNormalizer.AccountKey("TD Bank", AccountType.CreditCard, "4417"));

        chequing[0].Fingerprint.Should().NotBe(card[0].Fingerprint);
    }

    [Fact]
    public void Marks_charge_and_its_reversal()
    {
        var statement = Statement(Row(10, "BEST BUY #123", -499.99m), Row(12, "BEST BUY #123 REVERSAL", 499.99m), Row(12, "BEST BUY #123", -49.99m));

        var result = TransactionNormalizer.Normalize(statement, "k");

        result.Where(t => t.IsReversal).Select(t => t.Amount).Should().BeEquivalentTo([-499.99m, 499.99m]);
        result.Single(t => !t.IsReversal).Amount.Should().Be(-49.99m);
    }

    [Fact]
    public void Pairs_bank_payment_with_credit_card_payment_received()
    {
        var bankSide = new TransferCandidate(Guid.NewGuid(), "td|Chequing|8901", AccountType.Chequing, new DateOnly(2026, 8, 15), -600m,
            "ONLINE BANKING PAYMENT", CategoryTaxonomy.Uncategorized, TransactionType.Expense, 0.2, false);
        var cardSide = new TransferCandidate(Guid.NewGuid(), "td|CreditCard|4417", AccountType.CreditCard, new DateOnly(2026, 8, 16), 600m,
            "PAYMENT - THANK YOU", CategoryTaxonomy.CreditCardPayments, TransactionType.Transfer, 0.97, false);

        var pairs = TransferMatcher.Match([bankSide, cardSide]);

        pairs.Should().ContainSingle();
        pairs[0].CategoryId.Should().Be(CategoryTaxonomy.CreditCardPayments);
    }

    [Fact]
    public void Does_not_pair_unrelated_income_and_spending_of_equal_size()
    {
        var rent = new TransferCandidate(Guid.NewGuid(), "a", AccountType.Chequing, new DateOnly(2026, 8, 1), -2000m,
            "ACME PROPERTY MGMT RENT", "housing.rent", TransactionType.Expense, 0.8, false);
        var salary = new TransferCandidate(Guid.NewGuid(), "b", AccountType.Savings, new DateOnly(2026, 8, 1), 2000m,
            "PAYROLL ACME", "income.salary", TransactionType.Income, 0.93, false);

        TransferMatcher.Match([rent, salary]).Should().BeEmpty();
    }

    [Fact]
    public void Never_overrides_transactions_the_user_edited()
    {
        var a = new TransferCandidate(Guid.NewGuid(), "a", AccountType.Chequing, new DateOnly(2026, 8, 1), -300m,
            "TRANSFER TO", CategoryTaxonomy.Transfers, TransactionType.Transfer, 0.85, IsLocked: true);
        var b = new TransferCandidate(Guid.NewGuid(), "b", AccountType.Savings, new DateOnly(2026, 8, 1), 300m,
            "TRANSFER FROM", CategoryTaxonomy.Transfers, TransactionType.Transfer, 0.85, false);

        TransferMatcher.Match([a, b]).Should().BeEmpty();
    }
}
