using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FinSight.Core.Categories;

namespace FinSight.Core.Normalization;

public sealed record MerchantName(string Display, string Key);

/// <summary>
/// Turns noisy statement descriptors ("POS PURCHASE SQ *BLUE DOOR CAFE #0042 TORONTO ON") into a
/// display name ("Blue Door Cafe") and a grouping key ("bluedoorcafe").
/// </summary>
public static partial class MerchantNormalizer
{
    private static readonly string[] Prefixes =
    [
        "POINT OF SALE PURCHASE", "POS PURCHASE", "DEBIT CARD PURCHASE", "VISA DEBIT PURCHASE", "VISA DEBIT",
        "INTERAC PURCHASE", "INTERAC RETAIL PURCHASE", "CONTACTLESS INTERAC PURCHASE", "CONTACTLESS PURCHASE",
        "CONTACTLESS", "PRE-AUTHORIZED DEBIT", "PREAUTHORIZED DEBIT", "PRE-AUTH DEBIT", "RECURRING PAYMENT",
        "CHECKCARD", "CHECK CARD", "CARD PURCHASE", "PURCHASE AUTHORIZED ON", "PURCHASE", "APPLE PAY",
        "GOOGLE PAY", "DIRECT DEBIT", "BILL PAYMENT", "ONLINE PAYMENT", "WEB PAYMENT", "PAYMENT TO", "POS",
        "DEBIT", "PAP", "DD", "BPY", "OPOS", "FPOS", "IDP PURCHASE", "MOBILE PURCHASE",
    ];

    private static readonly string[] ProcessorPrefixes = ["SQ *", "SQ*", "TST* ", "TST*", "PP*", "PAYPAL *", "PAYPAL*", "SP * ", "SP *", "SPO*", "GOOGLE *", "WWW."];

    private static readonly HashSet<string> RegionCodes =
    [
        "ON", "QC", "BC", "AB", "MB", "SK", "NS", "NB", "NL", "PE", "YT", "NT", "NU",
        "AL", "AK", "AZ", "AR", "CA", "CO", "CT", "DE", "FL", "GA", "HI", "ID", "IL", "IN", "IA", "KS", "KY",
        "LA", "ME", "MD", "MA", "MI", "MN", "MS", "MO", "MT", "NE", "NV", "NH", "NJ", "NM", "NY", "NC", "ND",
        "OH", "OK", "OR", "PA", "RI", "SC", "SD", "TN", "TX", "UT", "VT", "VA", "WA", "WV", "WI", "WY", "DC",
        "CAN", "USA", "GBR", "UK", "GB", "IE", "FR", "DE",
    ];

    [GeneratedRegex(@"[•*X]{2,}\d{2,4}|#\s?\d+|\bSTORE\s?\d+|\b\d{2}/\d{2}(/\d{2,4})?\b|\b\d{3,}\b|\b[A-Z]*\d+[A-Z\d]*\b|\+?1?[-. ]?\(?\d{3}\)?[-. ]\d{3}[-. ]\d{4}", RegexOptions.IgnoreCase)]
    private static partial Regex Noise();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[^A-Za-z0-9&'.+ -]")]
    private static partial Regex Punctuation();

    public static MerchantName Normalize(string description)
    {
        var upper = description.ToUpperInvariant().Trim();

        foreach (var entry in MerchantCatalog.Merchants)
        {
            if (entry.Merchant is not null && entry.Regex.IsMatch(upper))
            {
                return new MerchantName(entry.Merchant, KeyOf(entry.Merchant));
            }
        }

        var cleaned = StripPrefixes(upper);
        cleaned = Noise().Replace(cleaned, " ");
        cleaned = Punctuation().Replace(cleaned, " ");
        cleaned = Spaces().Replace(cleaned, " ").Trim(' ', '-', '.', '\'');

        var tokens = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        // Drop a trailing region code and the city before it: "BLUE DOOR CAFE TORONTO ON".
        if (tokens.Count >= 3 && RegionCodes.Contains(tokens[^1]))
        {
            tokens.RemoveAt(tokens.Count - 1);
            if (tokens.Count >= 3)
            {
                tokens.RemoveAt(tokens.Count - 1);
            }
        }

        if (tokens.Count > 4)
        {
            tokens = tokens.Take(4).ToList();
        }

        var display = tokens.Count == 0 ? "Unknown merchant" : TitleCase(string.Join(' ', tokens));
        return new MerchantName(display, KeyOf(display));
    }

    public static string KeyOf(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                builder.Append(c);
            }
        }

        return builder.Length == 0 ? "unknown" : builder.ToString();
    }

    private static string StripPrefixes(string value)
    {
        var changed = true;
        while (changed)
        {
            changed = false;
            value = value.TrimStart(' ', '-', ':');

            foreach (var prefix in Prefixes)
            {
                if (value.StartsWith(prefix + " ", StringComparison.Ordinal) || value.StartsWith(prefix + "-", StringComparison.Ordinal))
                {
                    value = value[prefix.Length..];
                    changed = true;
                    break;
                }
            }

            foreach (var prefix in ProcessorPrefixes)
            {
                if (value.StartsWith(prefix, StringComparison.Ordinal))
                {
                    value = value[prefix.Length..];
                    changed = true;
                    break;
                }
            }
        }

        return value;
    }

    private static string TitleCase(string value)
    {
        var words = value.ToLowerInvariant().Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            if (word.Length == 0)
            {
                continue;
            }

            // Keep short all-consonant tokens like "BMO" or "KFC" upper-case.
            words[i] = word.Length <= 3 && !word.Any(c => "aeiouy".Contains(c)) && word.All(char.IsLetter)
                ? word.ToUpperInvariant()
                : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(word);
        }

        return string.Join(' ', words);
    }
}
