using System.Text.RegularExpressions;

namespace FinSight.Core.Text;

/// <summary>
/// Masks card and account numbers so they are never stored, logged or sent to an AI model.
/// Keeps the last four digits, which is what users recognise their accounts by.
/// </summary>
public static partial class SensitiveDataMasker
{
    public const string MaskPrefix = "••••";

    // 13-19 digits, optionally grouped by spaces or dashes (card numbers).
    [GeneratedRegex(@"(?<!\d)(?:\d[ -]?){12,18}\d(?!\d)")]
    private static partial Regex CardNumber();

    // 7-12 consecutive digits (account, transit and reference numbers).
    [GeneratedRegex(@"(?<!\d)\d{7,12}(?!\d)")]
    private static partial Regex AccountNumber();

    // Already-masked forms banks print: XXXX1234, ****1234, xxxx-xxxx-1234.
    [GeneratedRegex(@"(?:[X\*•]{2,}[ -]?)+(\d{3,4})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex PreMasked();

    [GeneratedRegex(@"\b(?:\d{3}-\d{2}-\d{4}|\d{3} \d{3} \d{3})\b")]
    private static partial Regex GovernmentId();

    public static string Mask(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var result = GovernmentId().Replace(input, MaskPrefix);
        result = CardNumber().Replace(result, m => MaskPrefix + LastFour(m.Value));
        result = AccountNumber().Replace(result, m => MaskPrefix + LastFour(m.Value));
        result = PreMasked().Replace(result, m => MaskPrefix + m.Groups[1].Value);
        return result;
    }

    /// <summary>Returns only the last four digits of an account identifier, or null if there are none.</summary>
    public static string? LastFourOf(string? identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        var digits = new string(identifier.Where(char.IsAsciiDigit).ToArray());
        return digits.Length >= 3 ? LastFour(digits) : null;
    }

    private static string LastFour(string value)
    {
        var digits = new string(value.Where(char.IsAsciiDigit).ToArray());
        return digits.Length <= 4 ? digits : digits[^4..];
    }
}
