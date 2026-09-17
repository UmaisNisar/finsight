using System.Text;
using FinSight.Core.Domain;
using FinSight.Core.Import;
using FinSight.Core.Import.Ofx;
using FinSight.Core.Normalization;
using FinSight.Core.Parsing;
using FinSight.Tests.TestHelpers;
using Row = FinSight.Tests.TestHelpers.StatementFiles.OfxRow;

namespace FinSight.Tests.Import;

public sealed class OfxStatementParserTests
{
    private static readonly StatementParseContext Context = new("USD", new DateOnly(2026, 9, 1));

    private static ParsedStatement Parse(byte[] file, StructuredImportLimits? limits = null) =>
        new OfxStatementParser(limits ?? StructuredImportLimits.Default).Parse(file, Context);

    private static readonly Row[] ChequingRows =
    [
        new("20260803120000[-5:EST]", 3100m, "PAYROLL ACME CORP", "TD20260803001", "CREDIT"),
        new("20260804", -20.99m, "NETFLIX.COM", "TD20260804001"),
        new("20260815000000.000[-4:EDT]", -1850m, "E-TRANSFER SENT", "TD20260815001", Memo: "RENT AUGUST"),
    ];

    [Fact]
    public void Ofx_1_sgml_bank_statement()
    {
        var parsed = Parse(StatementFiles.Ofx1("0012345678901", "CHECKING", ChequingRows, ledger: 2229.01m));

        parsed.Failure.Should().Be(ParseFailure.None);
        parsed.Metadata.Institution.Should().Be("TD Bank");
        parsed.Metadata.AccountType.Should().Be(AccountType.Chequing);
        parsed.Metadata.AccountMask.Should().Be("8901");
        parsed.Metadata.Currency.Should().Be("CAD", "CURDEF wins over the user's default currency");
        parsed.Metadata.PeriodStart.Should().Be(new DateOnly(2026, 8, 1));
        parsed.Metadata.PeriodEnd.Should().Be(new DateOnly(2026, 8, 31));
        parsed.Metadata.ClosingBalance.Should().Be(2229.01m);

        // Dates with time zone suffixes keep the calendar date the bank wrote.
        parsed.Transactions.Select(t => t.Date).Should().Equal(new DateOnly(2026, 8, 3), new DateOnly(2026, 8, 4), new DateOnly(2026, 8, 15));
        parsed.Transactions.Select(t => t.Amount).Should().Equal(3100m, -20.99m, -1850m);
        parsed.Transactions.Select(t => t.ExternalId).Should().Equal("TD20260803001", "TD20260804001", "TD20260815001");
        parsed.Transactions[2].Description.Should().Be("E-TRANSFER SENT RENT AUGUST");
    }

    [Fact]
    public void Ofx_2_xml_credit_card_statement_keeps_the_specification_signs()
    {
        var parsed = Parse(StatementFiles.Ofx2CreditCard("4111111111111111",
        [
            new("20260803", -84.10m, "LOBLAWS #221", "C1"),
            new("20260805", 500m, "PAYMENT THANK YOU", "C2", "CREDIT"),
            new("20260809", -12.40m, "A&amp;W RESTAURANT", "C3"),
        ], ledger: -1234.56m));

        parsed.Failure.Should().Be(ParseFailure.None);
        parsed.Metadata.Institution.Should().Be("CIBC");
        parsed.Metadata.AccountType.Should().Be(AccountType.CreditCard);
        parsed.Metadata.AccountMask.Should().Be("1111");
        parsed.Transactions.Select(t => t.Amount).Should().Equal(-84.10m, 500m, -12.40m);
        parsed.Transactions[2].Description.Should().Be("A&W RESTAURANT");
        parsed.Metadata.ClosingBalance.Should().Be(-1234.56m);
        parsed.Warnings.Should().BeEmpty();
    }

    [Fact]
    public void Card_files_that_list_charges_as_positive_are_reversed()
    {
        var parsed = Parse(StatementFiles.Ofx1("4500123412345190", "", [
            new("20260803", 84.10m, "LOBLAWS #221", "X1"),
            new("20260805", -500m, "PAYMENT - THANK YOU", "X2", "CREDIT"),
        ], creditCard: true));

        parsed.Metadata.AccountType.Should().Be(AccountType.CreditCard);
        parsed.Transactions.Select(t => t.Amount).Should().Equal(-84.10m, 500m);
        parsed.Warnings.Should().Contain(StructuredImportLimitsWarning());
    }

    [Fact]
    public void Debit_rows_that_are_positive_also_reveal_reversed_card_signs()
    {
        var parsed = Parse(StatementFiles.Ofx1("4500123412345190", "", [
            new("20260803", 84.10m, "LOBLAWS #221", "X1"),
            new("20260804", 20.99m, "NETFLIX.COM", "X2"),
        ], creditCard: true));

        parsed.Transactions.Select(t => t.Amount).Should().Equal(-84.10m, -20.99m);
    }

    [Fact]
    public void The_full_account_id_never_leaves_the_parser()
    {
        const string accountId = "000412345678901234";
        var parsed = Parse(StatementFiles.Ofx1(accountId, "SAVINGS", [new("20260804", 1.25m, $"INTEREST {accountId}", "I1", "CREDIT")]));

        parsed.Metadata.AccountMask.Should().Be("1234");
        parsed.Metadata.AccountType.Should().Be(AccountType.Savings);
        parsed.Transactions.Single().Description.Should().NotContain(accountId);
        parsed.Warnings.Should().NotContain(w => w.Contains(accountId, StringComparison.Ordinal));
    }

    [Fact]
    public void Transactions_repeated_in_one_file_are_kept_once()
    {
        var parsed = Parse(StatementFiles.Ofx1("0012345678901", "CHECKING", [.. ChequingRows, ChequingRows[1]]));

        parsed.Transactions.Should().HaveCount(3);
    }

    [Theory]
    [InlineData("<OFX><SIGNONMSGSRSV1><SONRS><STATUS><CODE>0</STATUS></SONRS></SIGNONMSGSRSV1></OFX>")]
    [InlineData("OFXHEADER:100\r\nDATA:OFXSGML\r\n\r\nnot really an ofx body")]
    public void Files_without_a_statement_are_unreadable(string ofx) =>
        Parse(Encoding.ASCII.GetBytes(ofx)).Failure.Should().Be(ParseFailure.Unrecognized);

    [Fact]
    public void Two_accounts_in_one_file_are_refused()
    {
        var first = Encoding.ASCII.GetString(StatementFiles.Ofx1("0012345678901", "CHECKING", ChequingRows));
        var second = Encoding.ASCII.GetString(StatementFiles.Ofx1("0099999999999", "SAVINGS", ChequingRows));
        var combined = first.Replace("</BANKMSGSRSV1>", second[second.IndexOf("<STMTTRNRS>", StringComparison.Ordinal)..second.IndexOf("</BANKMSGSRSV1>", StringComparison.Ordinal)] + "</BANKMSGSRSV1>", StringComparison.Ordinal);

        Parse(Encoding.ASCII.GetBytes(combined)).Failure.Should().Be(ParseFailure.MultipleAccounts);
    }

    [Fact]
    public void Transaction_cap_fails_the_file_as_too_large()
    {
        var rows = Enumerable.Range(1, 12).Select(d => new Row($"202608{d:00}", -1m, "COFFEE", $"F{d}")).ToList();

        Parse(StatementFiles.Ofx1("0012345678901", "CHECKING", rows), new StructuredImportLimits(10, TimeSpan.FromSeconds(10))).Failure.Should().Be(ParseFailure.TooLarge);
    }

    [Theory]
    [InlineData("20260815120000[-5:EST]", 2026, 8, 15)]
    [InlineData("20260815", 2026, 8, 15)]
    [InlineData("20260815235959.999[+10:AEST]", 2026, 8, 15)]
    [InlineData("202608150000", 2026, 8, 15)]
    public void Ofx_dates(string value, int year, int month, int day) =>
        OfxStatementParser.ReadDate(value).Should().Be(new DateOnly(year, month, day));

    [Fact]
    public void The_bank_transaction_id_fingerprints_exactly_even_when_descriptions_change_between_downloads()
    {
        var first = Parse(StatementFiles.Ofx1("0012345678901", "CHECKING", ChequingRows));
        var renamed = Parse(StatementFiles.Ofx1("0012345678901", "CHECKING", ChequingRows.Select(r => r with { Name = r.Name + " ONLINE" })));
        var key = TransactionNormalizer.AccountKey(first.Metadata.Institution, first.Metadata.AccountType, first.Metadata.AccountMask);

        TransactionNormalizer.Normalize(renamed, key).Select(t => t.Fingerprint)
            .Should().Equal(TransactionNormalizer.Normalize(first, key).Select(t => t.Fingerprint));

        // Without FITIDs, a changed description is a different transaction.
        var noIds = Parse(StatementFiles.Ofx1("0012345678901", "CHECKING", ChequingRows.Select(r => r with { FitId = null })));
        var noIdsRenamed = Parse(StatementFiles.Ofx1("0012345678901", "CHECKING", ChequingRows.Select(r => r with { FitId = null, Name = r.Name + " ONLINE" })));
        TransactionNormalizer.Normalize(noIdsRenamed, key).Select(t => t.Fingerprint)
            .Should().NotIntersectWith(TransactionNormalizer.Normalize(noIds, key).Select(t => t.Fingerprint));
    }

    private static string StructuredImportLimitsWarning() => "This file lists card charges as positive amounts, so FinSight reversed the signs.";
}

public sealed class StatementFileSnifferTests
{
    [Fact]
    public void Pdfs_are_known_by_their_header() =>
        StatementFileSniffer.Detect(PdfStatementBuilder.SampleChequingStatement(), "anything.csv").Should().Be(StatementFileFormat.Pdf);

    [Fact]
    public void Ofx_and_qfx_are_known_by_their_header_or_root_element()
    {
        StatementFileSniffer.Detect(StatementFiles.Ofx1("1", "CHECKING", []), "download.txt").Should().Be(StatementFileFormat.Ofx);
        StatementFileSniffer.Detect(StatementFiles.Ofx1("1", "CHECKING", []), "download.QFX").Should().Be(StatementFileFormat.Qfx);
        StatementFileSniffer.Detect(StatementFiles.Ofx2CreditCard("1", [], 0), "x.ofx").Should().Be(StatementFileFormat.Ofx);
        StatementFileSniffer.Detect(StatementFiles.Ofx2CreditCard("1", [], 0, quicken: true), "x.ofx").Should().Be(StatementFileFormat.Qfx);
        StatementFileSniffer.Detect("\n\n<OFX><SIGNONMSGSRSV1>"u8.ToArray()).Should().Be(StatementFileFormat.Ofx);
    }

    [Fact]
    public void Csv_is_delimited_text_in_any_common_encoding()
    {
        StatementFileSniffer.Detect(StatementFiles.CibcCreditCardCsv(), "statement.pdf").Should().Be(StatementFileFormat.Csv, "the name doesn't decide");
        StatementFileSniffer.Detect("﻿Date;Description;Amount\r\n2026-08-04;COFFEE;-4,50\r\n"u8.ToArray()).Should().Be(StatementFileFormat.Csv);
        StatementFileSniffer.Detect(Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("Date\tDescription\tAmount\r\n2026-08-04\tCOFFEE\t-4.50\r\n")).ToArray())
            .Should().Be(StatementFileFormat.Csv);
    }

    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52 })]
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x14, 0x00, 0x06, 0x00, 0x08, 0x00 })]
    [InlineData(new byte[] { 0x68, 0x65, 0x6C, 0x6C, 0x6F })]
    [InlineData(new byte[0])]
    public void Images_spreadsheets_and_plain_text_are_not_statement_files(byte[] content) =>
        StatementFileSniffer.Detect(content, "statement.csv").Should().BeNull();

    [Fact]
    public void Html_pages_saved_with_a_csv_name_are_not_statement_files() =>
        StatementFileSniffer.Detect("<!doctype html><html><body>Sign in, please, to continue</body></html>"u8.ToArray(), "export.csv").Should().BeNull();
}
