using System.Globalization;
using System.Text.RegularExpressions;

namespace FinSight.Core.Import.Csv;

/// <summary>How a file writes its dates, settled across every row of the date column.</summary>
/// <param name="Order">"ymd", "compact" (yyyyMMdd), "named" (Aug 15, 2026 or 15-Aug-2026), "mdy" or "dmy".</param>
/// <param name="Ambiguous">Every date read both ways (day and month all 12 or less) and nothing told the readings apart.</param>
internal sealed record CsvDateReader(string Order, char Separator, bool Ambiguous, Func<string, DateOnly?> Parse);

internal static partial class CsvDates
{
    [GeneratedRegex(@"^(\d{4})([-/.])(\d{1,2})[-/.](\d{1,2})$")]
    private static partial Regex YearFirst();

    [GeneratedRegex(@"^((?:19|20)\d{2})(\d{2})(\d{2})$")]
    private static partial Regex Compact();

    [GeneratedRegex(@"^(\d{1,2})([-/.])(\d{1,2})[-/.](\d{4}|\d{2})$")]
    private static partial Regex Numeric();

    [GeneratedRegex(@"^(\d{1,2})[ \-]([A-Za-z]{3,9})\.?[ \-,]+(\d{4}|\d{2})$")]
    private static partial Regex DayMonthName();

    [GeneratedRegex(@"^([A-Za-z]{3,9})\.?[ \-](\d{1,2}),?[ \-](\d{4})$")]
    private static partial Regex MonthNameDay();

    [GeneratedRegex(@"^(\S+(?: \S+ \S+)?)[ T]\d{1,2}:\d{2}(?::\d{2}(?:\.\d+)?)?(?: ?[AaPp][Mm])?(?:Z|[+-]\d{2}:?\d{2})?$")]
    private static partial Regex WithTime();

    private static readonly string[] Months = ["jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec"];

    /// <summary>The date part of a cell: time of day and a leading apostrophe (Excel's text marker) removed.</summary>
    public static string Clean(string value)
    {
        var text = value.Trim().TrimStart('\'');
        var match = WithTime().Match(text);
        return match.Success ? match.Groups[1].Value : text;
    }

    public static bool LooksLikeDate(string value)
    {
        var text = Clean(value);
        return text.Length is >= 6 and <= 20
            && (ReadYearFirst(text) is not null || ReadCompact(text) is not null || ReadNamed(text) is not null
                || ReadNumeric(text, monthFirst: true) is not null || ReadNumeric(text, monthFirst: false) is not null);
    }

    /// <summary>
    /// Settles the date format from all of a column's values (in file order). Day-first and month-first are told apart by any
    /// value over 12; when every value reads both ways, the reading that keeps the rows in date order wins, and a true tie
    /// reads month first (the North American default) and is reported as ambiguous. Null when no single format fits.
    /// </summary>
    public static CsvDateReader? Detect(IReadOnlyList<string> values)
    {
        var cleaned = values.Select(Clean).Where(v => v.Length > 0).ToList();
        if (cleaned.Count == 0)
        {
            return null;
        }

        var tolerance = Math.Max(1, cleaned.Count / 50);
        bool Fits(Func<string, DateOnly?> read, out int parsed)
        {
            parsed = cleaned.Count(v => read(v) is not null);
            return parsed > 0 && cleaned.Count - parsed <= tolerance;
        }

        if (Fits(ReadYearFirst, out _))
        {
            var separator = YearFirst().Match(cleaned.First(v => ReadYearFirst(v) is not null)).Groups[2].Value[0];
            return new CsvDateReader("ymd", separator, false, v => ReadYearFirst(Clean(v)));
        }

        if (Fits(ReadCompact, out _))
        {
            return new CsvDateReader("compact", '\0', false, v => ReadCompact(Clean(v)));
        }

        if (Fits(ReadNamed, out _))
        {
            return new CsvDateReader("named", ' ', false, v => ReadNamed(Clean(v)));
        }

        var monthFirst = Fits(v => ReadNumeric(v, true), out var monthFirstCount);
        var dayFirst = Fits(v => ReadNumeric(v, false), out var dayFirstCount);
        if (!monthFirst && !dayFirst)
        {
            return null;
        }

        var numericSeparator = Numeric().Match(cleaned.First(v => Numeric().IsMatch(v))).Groups[2].Value[0];
        CsvDateReader Reader(bool first, bool ambiguous) =>
            new(first ? "mdy" : "dmy", numericSeparator, ambiguous, v => ReadNumeric(Clean(v), first));

        if (monthFirst != dayFirst || monthFirstCount != dayFirstCount)
        {
            return Reader(monthFirst && (!dayFirst || monthFirstCount > dayFirstCount), false);
        }

        var monthFirstDisorder = Disorder(cleaned.Select(v => ReadNumeric(v, true)));
        var dayFirstDisorder = Disorder(cleaned.Select(v => ReadNumeric(v, false)));
        return monthFirstDisorder == dayFirstDisorder
            ? Reader(true, cleaned.Any(v => ReadNumeric(v, true) != ReadNumeric(v, false)))
            : Reader(monthFirstDisorder < dayFirstDisorder, false);
    }

    /// <summary>How far a sequence is from sorted, in either direction: adjacent pairs out of order.</summary>
    private static int Disorder(IEnumerable<DateOnly?> dates)
    {
        var list = dates.OfType<DateOnly>().ToList();
        int ascending = 0, descending = 0;
        for (var i = 1; i < list.Count; i++)
        {
            if (list[i] < list[i - 1])
            {
                ascending++;
            }
            else if (list[i] > list[i - 1])
            {
                descending++;
            }
        }

        return Math.Min(ascending, descending);
    }

    private static DateOnly? ReadYearFirst(string text)
    {
        var match = YearFirst().Match(text);
        return match.Success ? Make(match.Groups[1].Value, match.Groups[3].Value, match.Groups[4].Value) : null;
    }

    private static DateOnly? ReadCompact(string text)
    {
        var match = Compact().Match(text);
        return match.Success ? Make(match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value) : null;
    }

    private static DateOnly? ReadNumeric(string text, bool monthFirst)
    {
        var match = Numeric().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var (month, day) = monthFirst ? (match.Groups[1].Value, match.Groups[3].Value) : (match.Groups[3].Value, match.Groups[1].Value);
        return Make(match.Groups[4].Value, month, day);
    }

    private static DateOnly? ReadNamed(string text)
    {
        var match = DayMonthName().Match(text);
        if (match.Success)
        {
            return MonthNumber(match.Groups[2].Value) is { } month ? Make(match.Groups[3].Value, month.ToString(CultureInfo.InvariantCulture), match.Groups[1].Value) : null;
        }

        match = MonthNameDay().Match(text);
        return match.Success && MonthNumber(match.Groups[1].Value) is { } m
            ? Make(match.Groups[3].Value, m.ToString(CultureInfo.InvariantCulture), match.Groups[2].Value)
            : null;
    }

    private static int? MonthNumber(string name)
    {
        var lower = name.ToLowerInvariant();
        var index = Array.FindIndex(Months, m => lower.StartsWith(m, StringComparison.Ordinal));
        return index < 0 || (lower.Length > 3 && !IsMonthSpelling(lower, index)) ? null : index + 1;
    }

    private static bool IsMonthSpelling(string lower, int index)
    {
        string[] full = ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];
        return full[index].StartsWith(lower, StringComparison.Ordinal) || (index == 8 && lower == "sept");
    }

    private static DateOnly? Make(string year, string month, string day)
    {
        var y = int.Parse(year, CultureInfo.InvariantCulture);
        if (year.Length == 2)
        {
            y += 2000;
        }

        var m = int.Parse(month, CultureInfo.InvariantCulture);
        var d = int.Parse(day, CultureInfo.InvariantCulture);
        return y is >= 1990 and <= 2100 && m is >= 1 and <= 12 && d >= 1 && d <= DateTime.DaysInMonth(y, m) ? new DateOnly(y, m, d) : null;
    }
}

internal static partial class CsvAmounts
{
    [GeneratedRegex(@"^(?:\d[\d.,]*|[.,]\d+)$")]
    private static partial Regex NumberShape();

    private static readonly string[] CurrencyMarks = ["CA$", "US$", "C$", "CAD", "USD", "EUR", "GBP", "$", "€", "£"];

    /// <summary>
    /// Reads an amount cell: <c>1,234.56</c>, <c>-45.5</c>, <c>$45.00</c>, <c>(45.00)</c>, <c>45.00-</c>, <c>45.00 CR</c>, and with
    /// <paramref name="decimalComma"/> (semicolon files) <c>1.234,56</c>. <paramref name="hasCents"/> is true when it has exactly two
    /// decimals, which tells amounts from reference numbers in files without a header.
    /// </summary>
    public static bool TryParse(string raw, bool decimalComma, out decimal value, out bool hasCents)
    {
        value = 0;
        hasCents = false;
        var s = raw.Trim().TrimStart('\'').Replace(" ", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        if (s.Length == 0 || s.Length > 32)
        {
            return false;
        }

        var negative = false;
        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            negative = true;
            s = s[1..^1];
        }

        if (s.EndsWith("CR", StringComparison.OrdinalIgnoreCase))
        {
            s = s[..^2];
        }
        else if (s.EndsWith("DR", StringComparison.OrdinalIgnoreCase))
        {
            negative = true;
            s = s[..^2];
        }

        foreach (var mark in CurrencyMarks)
        {
            s = s.Replace(mark, "", StringComparison.OrdinalIgnoreCase);
        }

        if (s.Length > 0 && s[0] is '-' or '−' or '–')
        {
            negative = true;
            s = s[1..];
        }
        else if (s.StartsWith('+'))
        {
            s = s[1..];
        }

        if (s.Length > 0 && s[^1] is '-' or '−')
        {
            negative = true;
            s = s[..^1];
        }

        if (!NumberShape().IsMatch(s))
        {
            return false;
        }

        var lastDot = s.LastIndexOf('.');
        var lastComma = s.LastIndexOf(',');
        char? decimalSeparator;
        if (lastDot >= 0 && lastComma >= 0)
        {
            decimalSeparator = lastDot > lastComma ? '.' : ',';
        }
        else if (lastComma >= 0)
        {
            var after = s.Length - lastComma - 1;
            decimalSeparator = s.Count(c => c == ',') == 1 && (after is 1 or 2 || (decimalComma && after != 3)) ? ',' : null;
        }
        else if (lastDot >= 0)
        {
            var after = s.Length - lastDot - 1;
            decimalSeparator = s.Count(c => c == '.') == 1 && !(decimalComma && after == 3) ? '.' : null;
        }
        else
        {
            decimalSeparator = null;
        }

        var split = decimalSeparator is { } separator ? s.LastIndexOf(separator) : -1;
        var integerPart = split >= 0 ? s[..split] : s;
        var fraction = split >= 0 ? s[(split + 1)..] : "";
        if (fraction.Length > 4 || !fraction.All(char.IsAsciiDigit))
        {
            return false;
        }

        if (!integerPart.All(char.IsAsciiDigit))
        {
            // Thousands separators must group digits in threes: 1,234,567 or 1.234.567.
            var thousands = integerPart.First(c => !char.IsAsciiDigit(c));
            var groups = integerPart.Split(thousands);
            if (groups[0].Length is < 1 or > 3 || groups.Skip(1).Any(g => g.Length != 3 || !g.All(char.IsAsciiDigit)) || !groups[0].All(char.IsAsciiDigit))
            {
                return false;
            }

            integerPart = string.Concat(groups);
        }

        if (integerPart.Length > 12 || (integerPart.Length == 0 && fraction.Length == 0))
        {
            return false;
        }

        value = decimal.Parse($"{(integerPart.Length == 0 ? "0" : integerPart)}.{(fraction.Length == 0 ? "0" : fraction)}", CultureInfo.InvariantCulture);
        if (negative)
        {
            value = -value;
        }

        hasCents = fraction.Length == 2;
        return true;
    }
}
