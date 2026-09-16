using System.Globalization;
using System.Text.RegularExpressions;

namespace FinSight.Core.Parsing;

public enum AmountSignHint
{
    None,
    Negative,
    Credit,
    Debit,
}

public readonly record struct ParsedMoney(decimal Value, AmountSignHint Sign);

/// <summary>
/// Parses statement money tokens: <c>1,234.56</c>, <c>$1,234.56</c>, <c>-45.00</c>, <c>45.00-</c>,
/// <c>(45.00)</c>, <c>45.00CR</c>, <c>€1.234,56</c>. Requires exactly two decimals so that reference
/// numbers and quantities are not mistaken for amounts.
/// </summary>
public static partial class MoneyParser
{
    [GeneratedRegex(@"^(?<open>\()?(?<lead>[-−–+])?(?<cur>[$€£]|CA\$|US\$|C\$)?(?<lead2>[-−–])?(?<num>(?:\d{1,3}(?:[,. ]\d{3})+|\d+)[.,]\d{2})(?<close>\))?(?<trail>-|−|CR|DR|Cr|Dr)?$")]
    private static partial Regex Money();

    public static bool IsMoney(string token) => TryParse(token, out _);

    public static bool TryParse(string token, out ParsedMoney money)
    {
        money = default;
        var match = Money().Match(token.Trim());
        if (!match.Success)
        {
            return false;
        }

        var raw = match.Groups["num"].Value;
        if (!TryParseNumber(raw, out var value))
        {
            return false;
        }

        var trail = match.Groups["trail"].Value.ToUpperInvariant();
        var negative = match.Groups["lead"].Value is "-" or "−" or "–"
            || match.Groups["lead2"].Success
            || trail is "-" or "−"
            || (match.Groups["open"].Success && match.Groups["close"].Success);

        var sign = trail switch
        {
            "CR" => AmountSignHint.Credit,
            "DR" => AmountSignHint.Debit,
            _ when negative => AmountSignHint.Negative,
            _ => AmountSignHint.None,
        };

        money = new ParsedMoney(value, sign);
        return true;
    }

    private static bool TryParseNumber(string raw, out decimal value)
    {
        // The last separator before exactly two trailing digits is the decimal separator.
        var decimalSeparator = raw[^3];
        var integerPart = raw[..^3];
        var fraction = raw[^2..];

        var digits = new string(integerPart.Where(char.IsAsciiDigit).ToArray());
        if (digits.Length == 0)
        {
            digits = "0";
        }

        // "1.234.567" style thousands with a comma decimal, or "1,234,567" with a dot decimal.
        var thousands = integerPart.Where(c => c is ',' or '.' or ' ').Distinct().ToList();
        if (thousands.Count > 1 || (thousands.Count == 1 && thousands[0] == decimalSeparator))
        {
            value = 0;
            return false;
        }

        return decimal.TryParse($"{digits}.{fraction}", NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }
}
