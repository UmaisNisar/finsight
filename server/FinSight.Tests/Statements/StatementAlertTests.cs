using FinSight.Core.Domain;
using FinSight.Core.Statements;
using FinSight.Infrastructure.Pipeline;

namespace FinSight.Tests.Statements;

public class StatementAlertDetectionTests
{
    private static readonly DateTimeOffset Received = new(2026, 9, 15, 13, 0, 0, TimeSpan.Zero);

    private static EmailCandidate Email(string subject, string from, string snippet, params EmailAttachment[] attachments) =>
        new("m1", "t1", subject, from, snippet, Received, attachments);

    [Fact]
    public void Detects_the_cibc_estatement_alert()
    {
        var email = Email("eStatement Alert", "CIBC Banking <mailbox.noreply@cibc.com>",
            "Hi Sam Rivera, Your eStatement for the current month for your CIBC credit card ending in 5190 is now available. "
            + "Please sign on to CIBC Mobile Banking or CIBC Online Banking and go to My documents for details.");

        var alert = StatementEmailClassifier.DetectAlert(email);

        alert.Should().NotBeNull();
        alert!.Institution.Should().Be("CIBC");
        alert.AccountType.Should().Be(AccountType.CreditCard);
        alert.AccountMask.Should().Be("5190");
        alert.ReceivedAt.Should().Be(Received);
        alert.DocumentKind.Should().Be(DocumentKind.CreditCardStatement);
        alert.Confidence.Should().BeGreaterThanOrEqualTo(StatementDetection.DiscoveryThreshold);
        alert.Reasons.Should().Contain("Sent by CIBC").And.NotContain(r => r.Contains("Rivera", StringComparison.Ordinal));
        StatementEmailClassifier.Classify(email).IsStatement.Should().BeFalse("it has no PDF, so it is not a downloadable statement");
    }

    [Theory]
    [InlineData("Your TD eStatement is ready", "TD Canada Trust <noreply@td.com>",
        "Your statement for your TD chequing account ending 1234 is ready to view in EasyWeb.", "TD Bank", AccountType.Chequing, "1234")]
    [InlineData("Your RBC statement is available", "RBC Royal Bank <no-reply@alerts.rbc.com>",
        "Your RBC Avion Visa statement for the card ending in 4417 has been posted. Sign in to view your statement.", "RBC Royal Bank", AccountType.CreditCard, "4417")]
    [InlineData("New eStatement available", "Scotiabank <estatements@scotiabank.com>",
        "View your statement for your Scotia savings account ****5521 online.", "Scotiabank", AccountType.Savings, "5521")]
    [InlineData("Your monthly statement is ready", "Maple Credit Union <alerts@maplecu.example>",
        "Your line of credit statement is now available in online banking.", null, AccountType.LineOfCredit, null)]
    public void Detects_other_banks_phrasings(string subject, string from, string snippet, string? institution, AccountType type, string? mask)
    {
        var alert = StatementEmailClassifier.DetectAlert(Email(subject, from, snippet));

        alert.Should().NotBeNull();
        alert!.Institution.Should().Be(institution);
        alert.AccountType.Should().Be(type);
        alert.AccountMask.Should().Be(mask);
    }

    [Theory]
    [InlineData("Your payment is due soon", "CIBC Banking <mailbox.noreply@cibc.com>",
        "Your minimum payment for your credit card ending in 5190 is due Oct 5. Your statement is available online.")]
    [InlineData("Payment reminder", "TD Canada Trust <noreply@td.com>", "A payment is due on your TD Visa. View your statement in EasyWeb.")]
    [InlineData("We received your payment", "RBC Royal Bank <no-reply@rbc.com>", "Thank you for your payment. Your next statement will be available soon.")]
    [InlineData("Account update", "BMO <alerts@bmo.com>", "We received your payment of $250.00. View your statement for details.")]
    [InlineData("Earn 5% cash back with your new card", "CIBC <offers@cibc.com>", "Your statement is ready for bigger rewards: apply now.")]
    [InlineData("Go paperless today", "Scotiabank <news@scotiabank.com>", "Switch to eStatements: your statements are available online anytime.")]
    [InlineData("Purchase alert", "CIBC Banking <mailbox.noreply@cibc.com>",
        "A purchase of $45.00 was made on your credit card ending in 5190. View your statement for details.")]
    [InlineData("Transaction alert for your account", "TD Canada Trust <noreply@td.com>", "An e-Transfer was deposited. Your statement is now available.")]
    [InlineData("Your statement is ready", "Shop Rewards <news@shop.example>", "Your points statement is now available.")]
    [InlineData("Your annual tax statement is ready", "Wealthsimple <noreply@wealthsimple.com>", "Your T5 statement is now available.")]
    [InlineData("Your earnings statement is available", "Payroll <noreply@payroll.example>", "Your pay statement is now available.")]
    public void Rejects_payment_transaction_and_marketing_emails(string subject, string from, string snippet)
    {
        StatementEmailClassifier.DetectAlert(Email(subject, from, snippet)).Should().BeNull();
    }

    [Fact]
    public void Emails_with_a_pdf_still_go_through_the_statement_path()
    {
        var email = Email("Your eStatement is ready", "TD Canada Trust <noreply@td.com>", "Your statement is now available.",
            new EmailAttachment("2", "Statement_Aug2026.pdf", "application/pdf", 90_000));

        StatementEmailClassifier.DetectAlert(email).Should().BeNull();
        StatementEmailClassifier.Classify(email).IsStatement.Should().BeTrue();
    }

    [Fact]
    public void An_image_attachment_does_not_stop_an_alert()
    {
        var email = Email("eStatement Alert", "CIBC Banking <mailbox.noreply@cibc.com>", "Your eStatement is now available.",
            new EmailAttachment("2", "logo.png", "image/png", 4_000));

        StatementEmailClassifier.DetectAlert(email).Should().NotBeNull();
    }

    [Theory]
    [InlineData("your CIBC credit card ending in 5190 is now available", "5190")]
    [InlineData("card ending 5190", "5190")]
    [InlineData("account ends in 0042.", "0042")]
    [InlineData("card ending with ****5190", "5190")]
    [InlineData("Visa XXXX-XXXX-XXXX-3301 statement", "3301")]
    [InlineData("account ending in 5012345678901234", "1234")]
    [InlineData("account 5012345678901234", null)]
    [InlineData("ending in 87", null)]
    [InlineData("Your statement is ready", null)]
    public void Extracts_only_the_last_four_digits(string text, string? expected)
    {
        StatementEmailClassifier.ExtractAccountMask(text).Should().Be(expected);
    }

    [Fact]
    public void Search_queries_find_alerts_from_known_banks_without_requiring_an_attachment()
    {
        var queries = StatementEmailClassifier.SearchQueries(new DateOnly(2025, 8, 1));

        var alertQueries = queries.Where(q => !q.Contains("has:attachment", StringComparison.Ordinal)).ToList();
        alertQueries.Should().Contain(q => q.Contains("from:(", StringComparison.Ordinal) && q.Contains("cibc.com", StringComparison.Ordinal));
        alertQueries.Should().OnlyContain(q => q.StartsWith("after:2025/08/01 ", StringComparison.Ordinal) && q.Contains("statement", StringComparison.OrdinalIgnoreCase));
        queries.Should().OnlyContain(q => q.Length < 1000);
        KnownInstitutions.All.SelectMany(i => i.SenderDomains)
            .Should().OnlyContain(d => alertQueries.Any(q => q.Contains($" {d} ", StringComparison.Ordinal) || q.Contains($"({d} ", StringComparison.Ordinal) || q.Contains($" {d})", StringComparison.Ordinal)));
    }
}

public class KnownInstitutionMetadataTests
{
    [Fact]
    public void Sign_in_links_are_hard_coded_https_and_ids_are_unique()
    {
        KnownInstitutions.All.Where(i => i.SignInUrl is not null)
            .Should().OnlyContain(i => i.SignInUrl!.StartsWith("https://", StringComparison.Ordinal) && Uri.IsWellFormedUriString(i.SignInUrl, UriKind.Absolute));
        KnownInstitutions.All.Select(i => i.Id).Should().OnlyHaveUniqueItems().And.OnlyContain(id => System.Text.RegularExpressions.Regex.IsMatch(id, "^[a-z0-9]+(-[a-z0-9]+)*$"));
        KnownInstitutions.All.Where(i => i.DownloadHint is not null).Should().OnlyContain(i => i.SignInUrl != null);

        var cibc = KnownInstitutions.FindByName("cibc")!;
        cibc.SignInUrl.Should().Be("https://www.cibconline.cibc.com");
        cibc.DownloadHint.Should().Be("Sign in to CIBC Online Banking, open My documents, and download each month's statement as a PDF.");
        KnownInstitutions.FindByName("U.S. Bank")!.Id.Should().Be("us-bank");
        KnownInstitutions.FindBySenderDomain("mailbox.noreply@cibc.com").Should().Be(cibc);
    }
}

public class StatementAlertMatchingTests
{
    private static readonly DateOnly PeriodEnd = new(2026, 6, 30);

    private static Statement Imported(string? institution = "CIBC", string? mask = "7890", AccountType type = AccountType.Chequing) => new()
    {
        SourceKey = "upload:abc",
        Source = StatementSourceKind.ManualUpload,
        Filename = "june.pdf",
        Institution = institution,
        AccountMask = mask,
        AccountType = type,
        PeriodEnd = PeriodEnd,
        Status = StatementStatus.Processed,
    };

    private static Statement Alert(int daysAfterPeriodEnd, string? institution = "CIBC", string? mask = "7890", AccountType type = AccountType.Chequing) => new()
    {
        SourceKey = StatementAlert.SourceKey(Guid.NewGuid().ToString("N")),
        Source = StatementSourceKind.Gmail,
        Filename = string.Empty,
        Institution = institution,
        AccountMask = mask,
        AccountType = type,
        ReceivedAt = new DateTimeOffset(PeriodEnd.AddDays(daysAfterPeriodEnd).ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero),
        Status = StatementStatus.AwaitingUpload,
    };

    [Theory]
    [InlineData(-5, "CIBC", "7890", AccountType.Chequing, true)]
    [InlineData(25, "CIBC", "7890", AccountType.Chequing, true)]
    [InlineData(3, "c.i.b.c", "7890", AccountType.Chequing, true)]
    [InlineData(3, "CIBC", null, AccountType.Unknown, true)]
    [InlineData(-6, "CIBC", "7890", AccountType.Chequing, false)]
    [InlineData(26, "CIBC", "7890", AccountType.Chequing, false)]
    [InlineData(3, "CIBC", "1111", AccountType.Chequing, false)]
    [InlineData(3, "TD Bank", "7890", AccountType.Chequing, false)]
    [InlineData(3, "CIBC", "7890", AccountType.CreditCard, false)]
    [InlineData(3, null, "7890", AccountType.Chequing, false)]
    public void Matches_by_institution_account_and_date_window(int days, string? institution, string? mask, AccountType type, bool expected)
    {
        StatementAlertMatching.Matches(Alert(days, institution, mask, type), Imported()).Should().Be(expected);
    }

    [Fact]
    public void An_import_without_a_mask_matches_any_account_of_that_bank_and_the_closest_alert_wins()
    {
        var far = Alert(20);
        var near = Alert(2, mask: "1111");

        StatementAlertMatching.Closest([far, near, Alert(40)], Imported(mask: null)).Should().BeSameAs(near);
    }
}

public class StatementAlertLabelTests
{
    private static Statement Alert(string? institution, AccountType type, string? mask) => new()
    {
        SourceKey = StatementAlert.SourceKey("m1"),
        Source = StatementSourceKind.Gmail,
        Filename = string.Empty,
        Institution = institution,
        AccountType = type,
        AccountMask = mask,
        ReceivedAt = new DateTimeOffset(2026, 9, 15, 13, 0, 0, TimeSpan.Zero),
        Status = StatementStatus.AwaitingUpload,
    };

    [Theory]
    [InlineData("CIBC", AccountType.CreditCard, "5190", "CIBC credit card ending 5190")]
    [InlineData("TD Bank", AccountType.Chequing, null, "TD Bank chequing account")]
    [InlineData("CIBC", AccountType.Unknown, "5190", "CIBC account ending 5190")]
    [InlineData("CIBC", AccountType.Unknown, null, "CIBC statement")]
    [InlineData(null, AccountType.LineOfCredit, "0042", "Line of credit ending 0042")]
    [InlineData(null, AccountType.Unknown, null, "Statement")]
    public void Alert_titles_name_the_account(string? institution, AccountType type, string? mask, string expected)
    {
        StatementLabels.Title(Alert(institution, type, mask)).Should().Be(expected);
    }

    [Theory]
    [InlineData(3, 2, 5, "Found 3 new statements and 2 statement alerts")]
    [InlineData(1, 0, 5, "Found 1 new statement")]
    [InlineData(0, 1, 5, "Found 1 statement alert")]
    [InlineData(0, 0, 5, "No new statements")]
    [InlineData(0, 0, 0, "No statements found")]
    public void Sync_detail_mentions_alerts(int statements, int alerts, int total, string expected)
    {
        StatementLabels.SyncDetail(new DiscoveryResult(10, statements, total, alerts)).Should().Be(expected);
    }
}
