using FinSight.Core.Domain;
using FinSight.Core.Statements;

namespace FinSight.Tests.Statements;

public class StatementEmailClassifierTests
{
    private static EmailCandidate Email(string subject, string from, string filename, string snippet = "") =>
        new("m1", "t1", subject, from, snippet, DateTimeOffset.UtcNow, [new EmailAttachment("1", filename, "application/pdf", 120_000)]);

    [Fact]
    public void Detects_known_bank_statement()
    {
        var result = StatementEmailClassifier.Classify(Email("Your eStatement is ready", "TD Canada Trust <noreply@td.com>", "Statement_Aug2026.pdf"));

        result.IsStatement.Should().BeTrue();
        result.Institution.Should().Be("TD Bank");
        result.Confidence.Should().BeGreaterThan(0.8);
    }

    [Fact]
    public void Detects_statement_from_unlisted_bank()
    {
        var result = StatementEmailClassifier.Classify(Email("Monthly account statement", "Maple Credit Union <statements@maplecu.example>",
            "2026-08.pdf", "Your chequing account statement is attached"));

        result.IsStatement.Should().BeTrue();
        result.Kind.Should().Be(DocumentKind.BankStatement);
    }

    [Fact]
    public void Detects_credit_card_statements()
    {
        var result = StatementEmailClassifier.Classify(Email("Your Visa card statement", "Rogers Bank <alerts@rogersbank.com>", "stmt.pdf"));

        result.Kind.Should().Be(DocumentKind.CreditCardStatement);
    }

    [Theory]
    [InlineData("Your order receipt", "Shop <orders@shop.example>", "receipt.pdf")]
    [InlineData("Invoice #4412 for August", "Hosting <billing@host.example>", "invoice.pdf")]
    public void Rejects_receipts_and_invoices(string subject, string from, string filename)
    {
        StatementEmailClassifier.Classify(Email(subject, from, filename)).IsStatement.Should().BeFalse();
    }

    [Fact]
    public void Ignores_emails_without_pdf()
    {
        var email = new EmailCandidate("m", null, "Your statement", "noreply@td.com", "", DateTimeOffset.UtcNow,
            [new EmailAttachment("1", "logo.png", "image/png", 2000)]);

        StatementEmailClassifier.Classify(email).IsStatement.Should().BeFalse();
    }
}
