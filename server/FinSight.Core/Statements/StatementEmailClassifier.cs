using System.Text.RegularExpressions;
using FinSight.Core.Domain;
using FinSight.Core.Text;

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
/// An email that says a statement is ready to view online but attaches no PDF (CIBC sends only these).
/// Keeps nothing from the message beyond the institution, the account type and the last four digits.
/// </summary>
public sealed record StatementAlert(
    string? Institution,
    AccountType AccountType,
    string? AccountMask,
    DateTimeOffset ReceivedAt,
    double Confidence,
    IReadOnlyList<string> Reasons)
{
    public const string SourceKeySuffix = ":alert";

    public static string SourceKey(string messageId) => $"gmail:{messageId}{SourceKeySuffix}";

    public static bool IsAlert(Statement statement) =>
        statement.Source == StatementSourceKind.Gmail && statement.SourceKey.EndsWith(SourceKeySuffix, StringComparison.Ordinal);

    public DocumentKind DocumentKind => AccountType is AccountType.CreditCard ? DocumentKind.CreditCardStatement : DocumentKind.BankStatement;
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

    // "Your eStatement ... is now available", "statement is ready", "view your statement", "eStatement Alert".
    [GeneratedRegex(@"\b(e-?statements?|statements?)\b[^.!?\n]{0,120}?\b(is|are|has been|have been)\s+(now\s+)?(available|ready|online|posted|issued)\b"
        + @"|\b(view|access|see|download|check)\s+(your\s+)?(latest\s+|new\s+|recent\s+|monthly\s+|current\s+)?(e-?statements?|statements?)\b"
        + @"|\b(e-?statements?|statements?)\s+(alert|notification|notice|available|ready)\b"
        + @"|\b(new|latest)\s+(e-?statements?|statements?)\b"
        + @"|\brelev[ée]s?\b[^.!?\n]{0,80}?\bdisponibles?\b", RegexOptions.IgnoreCase)]
    private static partial Regex StatementAvailableWords();

    [GeneratedRegex(@"\b(payments? (is |are |was |has been )?(due|received|overdue|posted|processed|scheduled|confirmation|reminder)|due date|minimum payment|past due|overdue|thank you for your payment|we received your payment|autopay|automatic payment|pre-?authori[sz]ed (payment|debit))\b", RegexOptions.IgnoreCase)]
    private static partial Regex PaymentReminderWords();

    [GeneratedRegex(@"\b(transactions?|purchases?|withdrawals?|deposits?|e-?transfers?|interac|charged?|spent|authori[sz](ed|ation)|declined|low balance|balance (alert|is below|update)|fraud|suspicious|sign-?in|log-?in|password|verification code|security code)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TransactionAlertWords();

    [GeneratedRegex(@"\b(offers?|promo(tion)?s?|special|exclusive|rewards?|earn|win|contest|apply now|pre-?approved|limited time|upgrade|bonus|cash ?back|webinar|newsletter|survey|tax|privacy|annual)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MarketingWords();

    // "ending in 5190", "ending 5190", "ends in 5190", "ending with ****5190".
    [GeneratedRegex(@"\b(?:ending|ends)(?:\s+(?:in|with))?\s*[:#]?\s*(?:[x*•]+[ -]?)?(\d{3,})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex EndingDigits();

    // Pre-masked numbers: "****5190", "XXXX-5190", "•••• 5190".
    [GeneratedRegex(@"(?:[x*•]{2,}[ -]?)+(\d{4})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex MaskedDigits();

    [GeneratedRegex(@"\b(go paperless|switch to e-?statements|sign up for e-?statements|enrol(l)?( now)? (in|for) e-?statements|turn on e-?statements)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PaperlessInvitation();

    [GeneratedRegex(@"\bline of credit\b", RegexOptions.IgnoreCase)]
    private static partial Regex LineOfCreditWords();

    [GeneratedRegex(@"\b(credit card|visa|mastercard|amex|american express|card ending|card account)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CreditCardWords();

    [GeneratedRegex(@"\b(chequing|checking)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ChequingWords();

    [GeneratedRegex(@"\bsavings\b", RegexOptions.IgnoreCase)]
    private static partial Regex SavingsWords();

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
            .. AlertSearchQueries(after),
        ];
    }

    /// <summary>
    /// Statement alerts carry no PDF, so these have no attachment filter: known bank senders mentioning a statement
    /// (a limited number of domains per query), plus any sender whose subject says a statement is ready.
    /// </summary>
    private static IEnumerable<string> AlertSearchQueries(string after)
    {
        const int DomainsPerQuery = 20;
        var domains = KnownInstitutions.All.SelectMany(i => i.SenderDomains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        for (var i = 0; i < domains.Count; i += DomainsPerQuery)
        {
            yield return $"{after} from:({string.Join(" OR ", domains.Skip(i).Take(DomainsPerQuery))}) (statement OR estatement OR \"e-statement\")";
        }

        yield return $"{after} subject:(statement OR estatement OR \"e-statement\") (\"is ready\" OR \"is available\" OR \"now available\" OR \"view your statement\" OR \"statement alert\")";
    }

    public static StatementDetection Classify(EmailCandidate email)
    {
        var reasons = new List<string>();
        var score = 0.0;
        var text = $"{email.Subject}\n{email.Snippet}";

        var pdfs = email.Attachments
            .Where(a => IsPdf(a) && a.SizeBytes is > 0 and <= MaxPdfBytes)
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

    /// <summary>
    /// Detects an email announcing that a statement is ready online, with no PDF attached. Needs a known bank sender or
    /// strong statement-alert wording, and rejects payment reminders, transaction alerts and marketing. Emails with a PDF
    /// go through <see cref="Classify"/> instead. Returns null when the email is not a statement alert.
    /// </summary>
    public static StatementAlert? DetectAlert(EmailCandidate email)
    {
        if (email.Attachments.Any(IsPdf))
        {
            return null;
        }

        var subject = email.Subject;
        var snippet = email.Snippet;
        var text = $"{subject}\n{snippet}";
        var subjectMentionsStatement = StatementWords().IsMatch(subject);
        var subjectAnnouncesStatement = subjectMentionsStatement && StatementAvailableWords().IsMatch(subject);

        if (!StatementAvailableWords().IsMatch(text) || !StatementWords().IsMatch(text))
        {
            return null;
        }

        // The subject says what the email is about: payment, transaction and marketing subjects are never alerts.
        if (PaymentReminderWords().IsMatch(subject) || TransactionAlertWords().IsMatch(subject) || MarketingWords().IsMatch(subject)
            || NegativeWords().IsMatch(subject) || IncomeDocumentWords().IsMatch(text))
        {
            return null;
        }

        // Without a statement subject, the message text decides; payment, transaction or marketing wording there rules it out.
        if (!subjectMentionsStatement && (PaymentReminderWords().IsMatch(snippet) || TransactionAlertWords().IsMatch(snippet) || MarketingWords().IsMatch(snippet)))
        {
            return null;
        }

        // "Go paperless: sign up for eStatements" reads like an alert but is an invitation.
        if (PaperlessInvitation().IsMatch(text))
        {
            return null;
        }

        var reasons = new List<string>();
        var institution = KnownInstitutions.FindBySenderDomain(ExtractAddress(email.From));
        double confidence;
        if (institution is not null)
        {
            confidence = subjectMentionsStatement ? 0.9 : 0.75;
            reasons.Add($"Sent by {institution.Name}");
        }
        else
        {
            // Unknown senders need the subject itself to announce a statement, and bank-like wording.
            var financial = FinancialWords().IsMatch(text) || FinancialWords().IsMatch(email.From) || KnownInstitutions.FindInText(email.From) is not null;
            if (!subjectAnnouncesStatement || !financial)
            {
                return null;
            }

            institution = KnownInstitutions.FindInText(email.From) ?? KnownInstitutions.FindInText(subject);
            confidence = AutomatedSender().IsMatch(email.From) ? 0.7 : 0.6;
            if (institution is not null)
            {
                reasons.Add($"Mentions {institution.Name}");
            }
        }

        reasons.Add(subjectMentionsStatement ? "Subject says a statement is ready" : "Message says a statement is ready");
        reasons.Add("No PDF attached");

        return new StatementAlert(institution?.Name, AccountTypeOf(text), ExtractAccountMask(text), email.ReceivedAt, confidence, reasons);
    }

    /// <summary>The last four digits of the account an alert refers to ("ending in 5190"), or null. Never more than four digits.</summary>
    public static string? ExtractAccountMask(string text)
    {
        var ending = EndingDigits().Match(text);
        if (ending.Success)
        {
            return SensitiveDataMasker.LastFourOf(ending.Groups[1].Value) is { Length: 4 } lastFour ? lastFour : null;
        }

        var masked = MaskedDigits().Match(text);
        return masked.Success ? masked.Groups[1].Value : null;
    }

    public static AccountType AccountTypeOf(string text) =>
        LineOfCreditWords().IsMatch(text) ? AccountType.LineOfCredit
        : CreditCardWords().IsMatch(text) ? AccountType.CreditCard
        : ChequingWords().IsMatch(text) ? AccountType.Chequing
        : SavingsWords().IsMatch(text) ? AccountType.Savings
        : AccountType.Unknown;

    private static bool IsPdf(EmailAttachment attachment) =>
        attachment.MimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
        || attachment.Filename.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase);

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
