using FinSight.Core.Domain;
using FinSight.Core.Parsing;
using FinSight.Tests.TestHelpers;

namespace FinSight.Tests.Parsing;

public class StatementParserTests
{
    private static readonly StatementParseContext CadContext = new("CAD", new DateOnly(2026, 9, 16));

    [Fact]
    public void Parses_chequing_statement_with_debit_credit_and_balance_columns()
    {
        var document = new StatementLayout()
            .Text("TD Canada Trust")
            .Text("Everyday Chequing Account")
            .Text("Account number: 1234-5678901")
            .Text("Statement period: August 1, 2026 to August 31, 2026")
            .Gap()
            .Line((40, "Date"), (100, "Description"), (-400, "Withdrawals ($)"), (-480, "Deposits ($)"), (-570, "Balance ($)"))
            .Line((40, "Aug 1"), (100, "Opening balance"), (-570, "2,500.00"))
            .Line((40, "Aug 3"), (100, "PAYROLL ACME CORP"), (-480, "3,100.00"), (-570, "5,600.00"))
            .Line((40, "Aug 4"), (100, "NETFLIX.COM"), (-400, "22.99"), (-570, "5,577.01"))
            .Line((100, "STARBUCKS #1234 TORONTO ON"), (-400, "6.45"), (-570, "5,570.56"))
            .Line((40, "Aug 5"), (100, "POS PURCHASE LOBLAWS #221"), (-400, "142.30"), (-570, "5,428.26"))
            .Line((100, "TORONTO ON"))
            .Line((40, "Aug 12"), (100, "E-TRANSFER RENT PAYMENT"), (-400, "1,850.00"), (-570, "3,578.26"))
            .Line((40, "Aug 15"), (100, "TD VISA PAYMENT"), (-400, "600.00"), (-570, "2,978.26"))
            .Line((40, "Aug 31"), (100, "MONTHLY ACCOUNT FEE"), (-400, "4.95"), (-570, "2,973.31"))
            .Line((40, "Aug 31"), (100, "Closing balance"), (-570, "2,973.31"))
            .Build();

        var result = StatementParser.Parse(document, CadContext);

        result.Failure.Should().Be(ParseFailure.None);
        result.Reconciled.Should().BeTrue();
        result.Confidence.Should().BeGreaterThan(0.9);
        result.Metadata.Institution.Should().Be("TD Bank");
        result.Metadata.AccountType.Should().Be(AccountType.Chequing);
        result.Metadata.AccountMask.Should().Be("8901");
        result.Metadata.PeriodStart.Should().Be(new DateOnly(2026, 8, 1));
        result.Metadata.PeriodEnd.Should().Be(new DateOnly(2026, 8, 31));
        result.Metadata.OpeningBalance.Should().Be(2500.00m);
        result.Metadata.ClosingBalance.Should().Be(2973.31m);

        result.Transactions.Select(t => t.Amount).Should().Equal(3100.00m, -22.99m, -6.45m, -142.30m, -1850.00m, -600.00m, -4.95m);

        // The Starbucks row had no date: it inherits Aug 4.
        result.Transactions[2].Date.Should().Be(new DateOnly(2026, 8, 4));
        result.Transactions[2].Description.Should().Contain("STARBUCKS");

        // Wrapped description line is joined to its transaction.
        result.Transactions[3].Description.Should().Be("POS PURCHASE LOBLAWS #221 TORONTO ON");
    }

    [Fact]
    public void Parses_credit_card_statement_with_two_dates_and_signed_amounts()
    {
        var document = new StatementLayout()
            .Text("Scotiabank Scene+ Visa Card")
            .Text("Statement period Jul 22 - Aug 21, 2026")
            .Text("Account ending in 4417")
            .Text("Previous balance $812.40")
            .Text("Minimum payment $10.00")
            .Text("Credit limit $5,000.00")
            .Text("Payment due date Sep 12, 2026")
            .Gap()
            .Line((40, "Trans. date"), (100, "Post date"), (160, "Details"), (-560, "Amount ($)"))
            .Line((40, "JUL 23"), (100, "JUL 24"), (160, "UBER* TRIP TORONTO"), (-560, "18.40"))
            .Line((40, "JUL 25"), (100, "JUL 26"), (160, "PAYMENT - THANK YOU"), (-560, "-812.40"))
            .Line((40, "AUG 02"), (100, "AUG 04"), (160, "AMZN Mktp CA*RT4KX"), (-560, "64.99"))
            .Line((40, "AUG 06"), (100, "AUG 07"), (160, "AMZN Mktp CA REFUND"), (-560, "12.00CR"))
            .Line((40, "AUG 10"), (100, "AUG 11"), (160, "SPOTIFY P2A3B"), (-560, "11.99"))
            .Gap()
            .Text("New balance $83.38")
            .Build();

        var result = StatementParser.Parse(document, CadContext);

        result.Metadata.AccountType.Should().Be(AccountType.CreditCard);
        result.Metadata.Institution.Should().Be("Scotiabank");
        result.Metadata.AccountMask.Should().Be("4417");
        result.Metadata.PeriodStart.Should().Be(new DateOnly(2026, 7, 22));
        result.Reconciled.Should().BeTrue();

        result.Transactions.Select(t => t.Amount).Should().Equal(-18.40m, 812.40m, -64.99m, 12.00m, -11.99m);
        result.Transactions[0].Date.Should().Be(new DateOnly(2026, 7, 23));
        result.Transactions[0].PostingDate.Should().Be(new DateOnly(2026, 7, 24));
    }

    [Fact]
    public void Parses_day_first_dates_and_paid_out_paid_in_columns()
    {
        var document = new StatementLayout()
            .Text("Current account statement")
            .Text("Sort code 20-00-00 Account number 12345678")
            .Text("Period: 01/08/2026 to 31/08/2026")
            .Text("All amounts in GBP")
            .Gap()
            .Line((40, "Date"), (110, "Description"), (-400, "Paid out"), (-480, "Paid in"), (-570, "Balance"))
            .Line((40, "01/08/2026"), (110, "BALANCE BROUGHT FORWARD"), (-570, "1,000.00"))
            .Line((40, "02/08/2026"), (110, "TESCO STORES 3321"), (-400, "45.20"), (-570, "954.80"))
            .Line((40, "13/08/2026"), (110, "SALARY ACME LTD"), (-480, "2,400.00"), (-570, "3,354.80"))
            .Line((40, "20/08/2026"), (110, "TFL TRAVEL CH"), (-400, "32.10"), (-570, "3,322.70"))
            .Line((40, "31/08/2026"), (110, "BALANCE CARRIED FORWARD"), (-570, "3,322.70"))
            .Text("Registered in England. GBP deposits are protected.")
            .Build();

        var result = StatementParser.Parse(document, CadContext);

        result.Metadata.Currency.Should().Be("GBP");
        result.Metadata.AccountMask.Should().Be("5678");
        result.Reconciled.Should().BeTrue();
        result.Transactions.Select(t => t.Date).Should().Equal(
            new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 13), new DateOnly(2026, 8, 20));
        result.Transactions.Select(t => t.Amount).Should().Equal(-45.20m, 2400.00m, -32.10m);
    }

    [Fact]
    public void Infers_direction_from_running_balance_when_there_is_no_header()
    {
        var document = new StatementLayout()
            .Text("Checking account statement. Amounts in USD")
            .Text("Statement period 08/01/2026 - 08/31/2026")
            .Text("Beginning balance $1,200.00")
            .Gap()
            .Line((40, "08/03/2026"), (110, "ACME PAYROLL"), (-470, "3,000.00"), (-570, "4,200.00"))
            .Line((40, "08/15/2026"), (110, "SHELL OIL 57544"), (-470, "45.00"), (-570, "4,155.00"))
            .Line((40, "08/20/2026"), (110, "WHOLE FOODS MARKET"), (-470, "88.12"), (-570, "4,066.88"))
            .Gap()
            .Text("Ending balance $4,066.88")
            .Build();

        var result = StatementParser.Parse(document, new StatementParseContext("CAD", new DateOnly(2026, 9, 16)));

        result.Metadata.Currency.Should().Be("USD");
        result.Transactions.Select(t => t.Amount).Should().Equal(3000.00m, -45.00m, -88.12m);
        result.Transactions.Should().OnlyContain(t => t.Confidence > 0.8);
        result.Reconciled.Should().BeTrue();
    }

    [Fact]
    public void Assigns_previous_year_to_december_rows_in_a_statement_ending_in_january()
    {
        var document = new StatementLayout()
            .Text("Mastercard statement. Minimum payment and credit limit shown below.")
            .Text("Statement period Dec 15, 2025 - Jan 14, 2026")
            .Line((40, "Trans. date"), (160, "Description"), (-560, "Amount"))
            .Line((40, "DEC 20"), (160, "CINEPLEX ENTERTAINMENT"), (-560, "34.50"))
            .Line((40, "JAN 03"), (160, "PRESTO FARE"), (-560, "3.30"))
            .Text("Payment due Feb 5, 2026. Available credit $4,000.00. Card member services.")
            .Build();

        var result = StatementParser.Parse(document, CadContext);

        result.Transactions.Select(t => t.Date).Should().Equal(new DateOnly(2025, 12, 20), new DateOnly(2026, 1, 3));
    }

    [Fact]
    public void Reports_missing_text_layer_for_scanned_pdfs()
    {
        var document = new StatementLayout().Text("Scanned").Build();

        var result = StatementParser.Parse(document, CadContext);

        result.Failure.Should().Be(ParseFailure.NoTextLayer);
        result.Transactions.Should().BeEmpty();
    }

    [Fact]
    public void Never_keeps_full_card_numbers_in_descriptions()
    {
        var document = new StatementLayout()
            .Text("Chequing account statement with deposits and withdrawals for your records and reference only")
            .Line((40, "Date"), (100, "Description"), (-400, "Withdrawals"), (-480, "Deposits"), (-570, "Balance"))
            .Line((40, "Aug 3"), (100, "VISA DEBIT 4520123412341234 COFFEE"), (-400, "5.00"), (-570, "95.00"))
            .Line((40, "Aug 4"), (100, "TRANSFER TO 00012345678"), (-400, "5.00"), (-570, "90.00"))
            .Build();

        var result = StatementParser.Parse(document, CadContext);

        result.Transactions.Should().HaveCount(2);
        result.Transactions[0].Description.Should().NotContain("4520123412341234").And.Contain("••••1234");
        result.Transactions[1].Description.Should().NotContain("00012345678").And.Contain("••••5678");
    }
}
