using System.Text.RegularExpressions;

namespace FinSight.Core.Receipts;

/// <summary>
/// Removes personal details from a receipt email before its text is sent to the AI: email addresses, phone numbers,
/// card and account numbers, postal and ZIP codes, and the shipping-address block. What survives is the merchant,
/// the items and the prices — enough to list what was bought, with nothing that identifies the buyer.
///
/// This is a privacy safeguard, not a security boundary: it is deliberately aggressive (it would rather drop a real
/// item line than let an address through), and the AI only ever sees the redacted text.
/// </summary>
public static partial class ReceiptRedactor
{
    [GeneratedRegex(@"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase)]
    private static partial Regex Email();

    // +1 (416) 555-0199, 416-555-0199, 4165550199 — 10+ digit runs with phone punctuation.
    [GeneratedRegex(@"(?<!\d)(?:\+?\d{1,3}[ .\-]?)?\(?\d{3}\)?[ .\-]?\d{3}[ .\-]?\d{4}(?!\d)")]
    private static partial Regex Phone();

    // Any digit run of 5+ (card, account, order-looking numbers, long IDs). Prices have at most 4 integer digits
    // before a decimal, so "129.00" and "1,299.99" survive; "4505123412348764" does not.
    [GeneratedRegex(@"(?<![\d.])\d[\d ]{4,}\d(?![\d.])")]
    private static partial Regex LongDigits();

    // Canadian postal codes (A1A 1A1) and US ZIP / ZIP+4.
    [GeneratedRegex(@"\b[A-Za-z]\d[A-Za-z][ -]?\d[A-Za-z]\d\b|\b\d{5}(?:-\d{4})?\b")]
    private static partial Regex PostalCode();

    // A street address anywhere in the text: a house number, a street name, and a street-type word —
    // "123 Main St", "45 Spadina Ave", "123 Spadina Ave, Unit 4". Item lines ("BILLY bookcase") lack the street word.
    [GeneratedRegex(@"\b\d{1,6}\s+[A-Za-z][A-Za-z0-9 .,'\-]{0,40}?\b(?:ST|STREET|AVE|AVENUE|RD|ROAD|BLVD|BOULEVARD|DR|DRIVE|CRES|CRESCENT|CT|COURT|WAY|LANE|LN|PL|PLACE|TERR|HWY|UNIT|APT|SUITE|STE)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex StreetAddress();

    /// <summary>Redacted receipt text, trimmed to <paramref name="maxChars"/> so one long email can't dominate a prompt.</summary>
    public static string Redact(string body, int maxChars = 6000)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var text = Email().Replace(body, "[email]");
        text = Phone().Replace(text, "[phone]");
        text = StreetAddress().Replace(text, "[address]");
        text = PostalCode().Replace(text, "[postal]");
        text = LongDigits().Replace(text, "[number]");

        // Collapse the whitespace that HTML-to-text and the redactions leave behind.
        text = Whitespace().Replace(text, " ").Trim();
        text = BlankLines().Replace(text, "\n");

        return text.Length > maxChars ? text[..maxChars] : text;
    }

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"(?:\s*\n){2,}")]
    private static partial Regex BlankLines();
}
