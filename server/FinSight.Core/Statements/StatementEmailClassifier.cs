using System.Text.RegularExpressions;
using FinSight.Core.Domain;

namespace FinSight.Core.Statements;

public sealed record EmailAttachment(string PartId, string Filename, string MimeType, long SizeBytes);

public sealed record EmailCandidate(
    string MessageId,
    string? ThreadId,
    string Subject,
    string From,
    string Snippet,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<EmailAttachment> Attachments);

public sealed record StatementDetection(
    bool IsFinancial,
    bool IsStatement,
    DocumentKind Kind,
    double Confidence,
    string? Institution,
    IReadOnlyList<EmailAttachment> PdfAttachments,
    IReadOnlyList<string> Reasons)
{
    /// <summary>Candidates at or above this confidence are shown to the user as discovered statements.</summary>
    public const double DiscoveryThreshold = 0.5;
}

/// <summary>
/// Scores an email for "is this a bank or card statement with a PDF?" using sender, subject, snippet
/// and attachment names. Works for any bank: known institutions only add confidence.
/// </summary>
public static partial class StatementEmailClassifier
{
    private const long MaxPdfBytes = 20 * 1024 * 1024;

    [GeneratedRegex(@"\b(e-?statements?|statements?|account summary|monthly summary|relevé|estmt|stmt)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StatementWords();

    [GeneratedRegex(@"\b(bank|banking|credit union|financial|card ?member|credit card|visa|mastercard|amex|chequing|checking|savings|account ending|acct)\b", RegexOptions.IgnoreCase)]
    private static partial Regex FinancialWords();

    [GeneratedRegex(@"\b(credit card|card statement|visa|mastercard|amex|american express|card ending)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CardWords();

    [GeneratedRegex(@"\b(pay ?stub|payslip|pay slip|earnings statement|pay statement|remittance advice|T4|W-?2|1099)\b", RegexOptions.IgnoreCase)]
    private static partial Regex IncomeDocumentWords();

    [GeneratedRegex(@"\b(invoice|receipt|order confirmation|your order|shipping|booking confirmation|itinerary|ticket|quote|proposal|resume|cv|contract|newsletter|webinar|unsubscribe|promo|offer)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NegativeWords();

    [GeneratedRegex(@"\b(statement|stmt|estmt|estatement|e-statement|releve|account|acct|summary|\d{4}[-_]?\d{2})\b|statement", RegexOptions.IgnoreCase)]
    private static partial Regex StatementFilename();

    [GeneratedRegex(@"\b(noreply|no-reply|donotreply|alerts?|estatements?|statements?|notifications?)@", RegexOptions.IgnoreCase)]
    private static partial Regex AutomatedSender();

    /// <summary>
    /// Gmail search queries. Kept broad on purpose: the classifier does the precise work, and a
    /// query per phrasing avoids Gmail's limits on long OR expressions.
    /// </summary>
    public static IReadOnlyList<string> SearchQueries(DateOnly since)
    {
        var after = $"after:{since:yyyy/MM/dd}";
        return
        [
            $"{after} has:attachment filename:pdf (statement OR estatement OR \"e-statement\" OR \"account summary\")",
            $"{after} has:attachment filename:pdf subject:(\"bank statement\" OR \"account statement\" OR \"monthly statement\" OR \"card statement\" OR \"PDF statement\")",
            $"{after} has:attachment filename:pdf (payslip OR \"pay stub\" OR \"earnings statement\")",
        ];
    }

    public static StatementDetection Classify(EmailCandidate email)
    {
        var reasons = new List<string>();
        var score = 0.0;
        var text = $"{email.Subject}\n{email.Snippet}";

        var pdfs = email.Attachments
            .Where(a => (a.MimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
                    || a.Filename.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                && a.SizeBytes is > 0 and <= MaxPdfBytes)
            .ToList();

        if (pdfs.Count == 0)
        {
            return new StatementDetection(false, false, DocumentKind.Unknown, 0, null, [], ["No PDF attachment"]);
        }

        var institution = KnownInstitutions.FindBySenderDomain(ExtractAddress(email.From)) ?? KnownInstitutions.FindInText(email.From);
        if (institution is not null)
        {
            score += 0.35;
            reasons.Add($"Sent by {institution.Name}");
        }

        if (StatementWords().IsMatch(email.Subject))
        {
            score += 0.35;
            reasons.Add("Subject mentions a statement");
        }
        else if (StatementWords().IsMatch(email.Snippet))
        {
            score += 0.2;
            reasons.Add("Message mentions a statement");
        }

        var isFinancial = institution is not null || FinancialWords().IsMatch(text) || FinancialWords().IsMatch(email.From);
        if (isFinancial && institution is null)
        {
            score += 0.15;
            reasons.Add("Financial wording");
        }

        if (pdfs.Any(p => StatementFilename().IsMatch(p.Filename)))
        {
            score += 0.15;
            reasons.Add("Attachment name looks like a statement");
        }

        if (AutomatedSender().IsMatch(email.From))
        {
            score += 0.05;
        }

        if (NegativeWords().IsMatch(email.Subject))
        {
            score -= 0.35;
            reasons.Add("Looks like a receipt, invoice or marketing email");
        }

        var kind = IncomeDocumentWords().IsMatch(text) || pdfs.Any(p => IncomeDocumentWords().IsMatch(p.Filename))
            ? DocumentKind.IncomeDocument
            : CardWords().IsMatch(text) ? DocumentKind.CreditCardStatement
            : isFinancial || score >= 0.5 ? DocumentKind.BankStatement
            : DocumentKind.Unknown;

        if (kind == DocumentKind.IncomeDocument)
        {
            score += 0.2;
            reasons.Add("Looks like a pay stub or income document");
        }

        var confidence = Math.Round(Math.Clamp(score, 0, 0.99), 2);
        return new StatementDetection(
            isFinancial || kind == DocumentKind.IncomeDocument,
            confidence >= StatementDetection.DiscoveryThreshold,
            kind,
            confidence,
            institution?.Name,
            pdfs,
            reasons);
    }

    /// <summary>After download: does the extracted text look like it contains transactions?</summary>
    public static bool LooksTransactional(Parsing.PdfTextDocument document) =>
        document.WordCount >= 25 && document.Pages.Sum(p => p.Words.Count(w => Parsing.MoneyParser.IsMoney(w.Text))) >= 3;

    public static string ExtractAddress(string from)
    {
        var start = from.LastIndexOf('<');
        var end = from.LastIndexOf('>');
        return start >= 0 && end > start ? from[(start + 1)..end] : from.Trim();
    }
}
