using System.Text;
using FinSight.Core.Domain;
using FinSight.Core.Import;
using FinSight.Core.Import.Csv;
using FinSight.Core.Parsing;

namespace FinSight.Tests.Import;

/// <summary>Synthetic CSV downloads in the layouts Canadian banks use. No real bank data.</summary>
public sealed class CsvStatementParserTests
{
    private static readonly StatementParseContext Context = new("CAD", new DateOnly(2026, 9, 1));

    private static ParsedStatement Parse(string csv, StructuredImportLimits? limits = null) =>
        new CsvStatementParser(limits ?? StructuredImportLimits.Default).Parse(Encoding.UTF8.GetBytes(csv), Context);

    private static decimal[] Amounts(ParsedStatement parsed) => parsed.Transactions.Select(t => t.Amount).ToArray();

    [Fact]
    public void Cibc_credit_card_download_has_no_header_and_masks_the_card_number()
    {
        var parsed = Parse("""
            2026-08-03,LOBLAWS #221 TORONTO ON,84.10,,4500********5190
            2026-08-05,PAYMENT THANK YOU/PAIEMENT MERCI,,500.00,4500********5190
            2026-08-09,NETFLIX.COM,20.99,,4500********5190
            2026-08-12,AMAZON.CA REFUND,,15.00,4500********5190
            """);

        parsed.Failure.Should().Be(ParseFailure.None);
        parsed.Metadata.Institution.Should().Be("CIBC");
        parsed.Metadata.AccountType.Should().Be(AccountType.CreditCard);
        parsed.Metadata.AccountMask.Should().Be("5190");
        parsed.Metadata.PeriodStart.Should().Be(new DateOnly(2026, 8, 3));
        parsed.Metadata.PeriodEnd.Should().Be(new DateOnly(2026, 8, 12));

        // The debit column is a charge (money out) and the credit column a payment or refund (money in).
        Amounts(parsed).Should().Equal(-84.10m, 500m, -20.99m, 15m);
        parsed.Transactions[0].Description.Should().Be("LOBLAWS #221 TORONTO ON");
        parsed.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Cibc_chequing_download_has_four_columns_and_no_account_number()
    {
        var parsed = Parse("""
            2026-08-01,PAYROLL DEPOSIT ACME CORP,,3100.00
            2026-08-04,INTERNET BILL PAY 000000123456,85.00,
            2026-08-15,E-TRANSFER SENT,1850.00,
            """);

        parsed.Metadata.Institution.Should().Be("CIBC");
        parsed.Metadata.AccountType.Should().Be(AccountType.Chequing);
        parsed.Metadata.AccountMask.Should().BeNull();
        Amounts(parsed).Should().Equal(3100m, -85m, -1850m);
        parsed.Transactions[1].Description.Should().NotContain("000000123456", "long digit runs in descriptions are masked");
    }

    [Fact]
    public void Headed_signed_amounts_with_a_balance_reconcile()
    {
        var parsed = Parse("""
            Date,Description,Amount,Balance
            08/01/2026,PAYROLL ACME CORP,3100.00,4100.00
            08/04/2026,NETFLIX.COM,-20.99,4079.01
            08/15/2026,RENT,-1850.00,2229.01
            """);

        parsed.Failure.Should().Be(ParseFailure.None);
        parsed.Metadata.Institution.Should().BeNull();
        parsed.Transactions.Select(t => t.Date).Should().Equal(new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 4), new DateOnly(2026, 8, 15));
        Amounts(parsed).Should().Equal(3100m, -20.99m, -1850m);
        parsed.Metadata.OpeningBalance.Should().Be(1000m);
        parsed.Metadata.ClosingBalance.Should().Be(2229.01m);
        parsed.Reconciled.Should().BeTrue();
        parsed.Confidence.Should().BeGreaterThan(0.95);
    }

    [Fact]
    public void Separate_withdrawal_and_deposit_columns_listed_newest_first()
    {
        var parsed = Parse("""
            Transaction Date,Description,Withdrawals ($),Deposits ($),Balance ($)
            2026-08-20,"GROCERY STORE",45.10,,954.90
            2026-08-10,"INTEREST",,0.25,1000.00
            2026-08-02,"COFFEE",4.75,,999.75
            """);

        parsed.Failure.Should().Be(ParseFailure.None);
        parsed.Transactions.Select(t => t.Description).Should().Equal("COFFEE", "INTEREST", "GROCERY STORE");
        Amounts(parsed).Should().Equal(-4.75m, 0.25m, -45.10m);
        parsed.Metadata.OpeningBalance.Should().Be(1004.50m);
        parsed.Metadata.ClosingBalance.Should().Be(954.90m);
    }

    [Fact]
    public void A_day_over_twelve_anywhere_in_the_file_makes_every_date_day_first()
    {
        var parsed = Parse("""
            Date,Description,Amount
            03/08/2026,COFFEE,-4.50
            25/08/2026,BOOKS,-30.00
            """);

        parsed.Transactions.Select(t => t.Date).Should().Equal(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 25));
        parsed.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void When_every_date_reads_both_ways_the_order_of_the_rows_decides()
    {
        // Day first these run Aug 2, Aug 10, Sep 3; month first they would jump Feb 8, Oct 8, Mar 9.
        var parsed = Parse("""
            Date,Description,Amount
            02/08/2026,COFFEE,-4.50
            10/08/2026,BOOKS,-30.00
            03/09/2026,LUNCH,-12.00
            """);

        parsed.Transactions.Select(t => t.Date).Should().Equal(new DateOnly(2026, 8, 2), new DateOnly(2026, 8, 10), new DateOnly(2026, 9, 3));
    }

    [Fact]
    public void A_true_tie_reads_month_first_and_says_so()
    {
        var parsed = Parse("""
            Date,Description,Amount
            05/06/2026,COFFEE,-4.50
            """);

        parsed.Transactions.Single().Date.Should().Be(new DateOnly(2026, 5, 6));
        parsed.Warnings.Should().ContainMatch("*month/day*");
    }

    [Fact]
    public void Byte_order_mark_quotes_and_crlf_line_ends_are_handled()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }
            .Concat(Encoding.UTF8.GetBytes("\"Date\",\"Description\",\"Amount\"\r\n\"2026-08-04\",\"NETFLIX.COM, INC.\",\"-20.99\"\r\n\"2026-08-05\",\"CAFE \"\"BLUE DOOR\"\"\",\"-4.50\"\r\n"))
            .ToArray();

        var parsed = new CsvStatementParser(StructuredImportLimits.Default).Parse(bytes, Context);

        parsed.Failure.Should().Be(ParseFailure.None);
        parsed.Transactions.Select(t => t.Description).Should().Equal("NETFLIX.COM, INC.", "CAFE \"BLUE DOOR\"");
        Amounts(parsed).Should().Equal(-20.99m, -4.50m);
    }

    [Fact]
    public void Semicolon_files_with_decimal_commas()
    {
        var parsed = Parse("""
            Date;Description;Débit;Crédit;Solde
            2026-08-04;NETFLIX;20,99;;1.234,56
            2026-08-06;VIREMENT REÇU;;1.000,00;2.234,56
            """);

        parsed.Failure.Should().Be(ParseFailure.None);
        Amounts(parsed).Should().Equal(-20.99m, 1000m);
        parsed.Metadata.ClosingBalance.Should().Be(2234.56m);
        parsed.Transactions[1].Description.Should().Be("VIREMENT REÇU");
    }

    [Theory]
    [InlineData("Foo,Bar,Baz\nhello,world,again\nmore,words,here")]
    [InlineData("Date,Description,Amount\nyesterday,COFFEE,-4.50\nlast week,BOOKS,-30.00\nsoon,LUNCH,-1.00")]
    [InlineData("2026-08-04,COFFEE,ABC-123\n2026-08-05,BOOKS,DEF-456")]
    [InlineData("Date,Amount\n2026-08-04,-4.50\n2026-08-05,-30.00")]
    public void Files_whose_columns_cant_be_mapped_are_unrecognized_not_guessed(string csv)
    {
        var parsed = Parse(csv);

        parsed.Failure.Should().Be(ParseFailure.Unrecognized);
        parsed.Transactions.Should().BeEmpty();
    }

    [Fact]
    public void Full_card_numbers_are_reduced_to_the_last_four_digits()
    {
        var parsed = Parse("""
            Card Number,Transaction Date,Description,Amount
            4111111111111111,2026-08-04,COFFEE PAID WITH 4111 1111 1111 1111,-4.50
            4111111111111111,2026-08-05,PAYMENT - THANK YOU,100.00
            """);

        parsed.Metadata.AccountMask.Should().Be("1111");
        parsed.Metadata.AccountType.Should().Be(AccountType.CreditCard);
        parsed.Transactions.Should().OnlyContain(t => !t.Description.Contains("4111 1111", StringComparison.Ordinal));
        Amounts(parsed).Should().Equal(-4.50m, 100m);
    }

    [Fact]
    public void Td_download_has_no_header_and_a_running_balance()
    {
        var parsed = Parse("""
            08/03/2026,TIM HORTONS #1234,4.50,,995.50
            08/07/2026,PAYROLL DEP,,2000.00,2995.50
            """);

        parsed.Metadata.Institution.Should().Be("TD Bank");
        Amounts(parsed).Should().Equal(-4.50m, 2000m);
        parsed.Metadata.OpeningBalance.Should().Be(1000m);
        parsed.Reconciled.Should().BeTrue();
    }

    [Fact]
    public void Scotiabank_older_download_has_a_signed_amount_and_a_dash_column()
    {
        var parsed = Parse("""
            8/15/2026,-45.67,-,"POS PURCHASE","SHOPPERS DRUG MART"
            8/20/2026,1200.00,-,"DEPOSIT","PAYROLL ACME"
            """);

        parsed.Metadata.Institution.Should().Be("Scotiabank");
        parsed.Transactions.Select(t => t.Description).Should().Equal("POS PURCHASE SHOPPERS DRUG MART", "DEPOSIT PAYROLL ACME");
        Amounts(parsed).Should().Equal(-45.67m, 1200m);
    }

    [Fact]
    public void Tangerine_rbc_and_simplii_headers_name_the_bank()
    {
        Parse("""
            Transaction date,Transaction,Name,Memo,Amount
            8/4/2026,DEBIT,NETFLIX.COM,Rewards earned: 0.20,-20.99
            8/6/2026,CREDIT,EFT Deposit from ACME CORP,,3100
            """).Metadata.Institution.Should().Be("Tangerine");

        var rbc = Parse("""
            "Account Type","Account Number","Transaction Date","Cheque Number","Description 1","Description 2","CAD$","USD$"
            Chequing,00123-1234567,8/4/2026,,"NETFLIX.COM","",-20.99,
            Chequing,00123-1234567,8/6/2026,,"PAYROLL","ACME CORP",3100.00,
            """);
        rbc.Metadata.Institution.Should().Be("RBC Royal Bank");
        rbc.Metadata.AccountType.Should().Be(AccountType.Chequing);
        rbc.Metadata.AccountMask.Should().Be("4567");
        rbc.Transactions[1].Description.Should().Be("PAYROLL ACME CORP");

        Parse("""
            Date, Transaction Details, Funds Out, Funds In
            08/04/2026,NETFLIX.COM,20.99,
            """).Metadata.Institution.Should().Be("Simplii Financial");
    }

    [Fact]
    public void A_file_with_several_accounts_is_refused()
    {
        var parsed = Parse("""
            "Account Type","Account Number","Transaction Date","Cheque Number","Description 1","Description 2","CAD$","USD$"
            Chequing,00123-1234567,8/4/2026,,"NETFLIX.COM","",-20.99,
            Savings,00123-7654321,8/6/2026,,"INTEREST","",0.25,
            """);

        parsed.Failure.Should().Be(ParseFailure.MultipleAccounts);
    }

    [Fact]
    public void Card_exports_with_positive_charges_are_reversed()
    {
        var parsed = Parse("""
            Date,Date Processed,Description,Cardmember,Amount
            04 Aug 2026,05 Aug 2026,LOBLAWS TORONTO,A CARDMEMBER,84.10
            06 Aug 2026,06 Aug 2026,PAYMENT RECEIVED - THANK YOU,A CARDMEMBER,-500.00
            09 Aug 2026,10 Aug 2026,NETFLIX.COM,A CARDMEMBER,20.99
            """);

        parsed.Metadata.Institution.Should().Be("American Express");
        parsed.Metadata.AccountType.Should().Be(AccountType.CreditCard);
        Amounts(parsed).Should().Equal(-84.10m, 500m, -20.99m);
        parsed.Transactions[0].PostingDate.Should().Be(new DateOnly(2026, 8, 5));
        parsed.Warnings.Should().Contain(w => w.Contains("reversed", StringComparison.Ordinal));
        parsed.Transactions.Should().OnlyContain(t => !t.Description.Contains("CARDMEMBER", StringComparison.Ordinal), "the cardholder's name isn't a description");
    }

    [Fact]
    public void Unsigned_amounts_take_their_sign_from_a_debit_credit_column_and_pending_rows_are_skipped()
    {
        var parsed = Parse("""
            Filter,Date,Description,Sub-description,Status,Type of Transaction,Amount
            All,2026-08-04,POS PURCHASE,SHOPPERS DRUG MART,Posted,Debit,45.67
            All,2026-08-05,DEPOSIT,PAYROLL ACME,Posted,Credit,1200.00
            All,2026-08-06,POS PURCHASE,CAFE,Pending,Debit,4.50
            """);

        parsed.Metadata.Institution.Should().Be("Scotiabank");
        Amounts(parsed).Should().Equal(-45.67m, 1200m);
        parsed.Warnings.Should().ContainMatch("1 pending transaction was skipped*");
    }

    [Fact]
    public void Preamble_lines_above_the_header_are_skipped()
    {
        var parsed = Parse("""
            Following data is valid as of 20260831120000 (Year/Month/Day/Hour/Minute/Second)


            First Bank Card,Transaction Type,Date Posted, Transaction Amount,Description
            '5191230000001234',DEBIT,20260804,-20.99,[DN]NETFLIX.COM
            '5191230000001234',CREDIT,20260806,3100.00,[DD]PAYROLL ACME
            """);

        parsed.Failure.Should().Be(ParseFailure.None);
        parsed.Metadata.Institution.Should().Be("BMO");
        parsed.Metadata.AccountMask.Should().BeNull("a debit card number isn't the account number");
        parsed.Transactions.Select(t => t.Date).Should().Equal(new DateOnly(2026, 8, 4), new DateOnly(2026, 8, 6));
        Amounts(parsed).Should().Equal(-20.99m, 3100m);
    }

    [Fact]
    public void Row_cap_fails_the_file_as_too_large()
    {
        var csv = "Date,Description,Amount\n" + string.Join('\n', Enumerable.Range(1, 12).Select(d => $"2026-08-{d:00},COFFEE,-4.50"));

        Parse(csv, new StructuredImportLimits(10, TimeSpan.FromSeconds(10))).Failure.Should().Be(ParseFailure.TooLarge);
        Parse(csv, new StructuredImportLimits(12, TimeSpan.FromSeconds(10))).Failure.Should().Be(ParseFailure.None);
    }

    [Fact]
    public void Cancellation_stops_parsing()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var parser = new CsvStatementParser(StructuredImportLimits.Default);

        var act = () => parser.Parse(Encoding.UTF8.GetBytes("Date,Description,Amount\n2026-08-04,COFFEE,-4.50"), Context, cancelled.Token);

        act.Should().Throw<OperationCanceledException>();
    }

    [Theory]
    [InlineData("1,234.56", false, 1234.56)]
    [InlineData("$1,234.56", false, 1234.56)]
    [InlineData("(45.00)", false, -45.00)]
    [InlineData("45.00-", false, -45.00)]
    [InlineData("-45.5", false, -45.5)]
    [InlineData("1.234,56", true, 1234.56)]
    [InlineData("20,99", true, 20.99)]
    [InlineData("1 234,56", true, 1234.56)]
    public void Amount_cells(string cell, bool decimalComma, double expected)
    {
        CsvAmounts.TryParse(cell, decimalComma, out var value, out _).Should().BeTrue();
        value.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData("12-34-56")]
    [InlineData("4111111111111111")]
    [InlineData("1,23,4")]
    [InlineData("abc")]
    public void Non_amount_cells(string cell) => CsvAmounts.TryParse(cell, false, out _, out _).Should().BeFalse();
}
