using System.Globalization;
using System.Text.RegularExpressions;

namespace FinSight.Core.Parsing;

public enum DateOrder
{
    MonthFirst,
    DayFirst,
}

/// <summary>
/// A date as printed on a statement, before the year and day/month order are known.
/// For named-month dates <see cref="First"/> is the month and <see cref="Second"/> the day.
/// For numeric dates ("08/05") the order is ambiguous until the whole document has been seen.
/// </summary>
public readonly record struct PartialDate(int First, int Second, int? Year, bool IsNumeric)
{
    public DateOnly? Resolve(DateOrder order, Func<int, int, int> inferYear)
    {
        var (month, day) = IsNumeric && order == DateOrder.DayFirst ? (Second, First) : (First, Second);
        if (month is < 1 or > 12 || day < 1)
        {
            return null;
        }

        var year = Year ?? inferYear(month, day);
        if (year is < 1990 or > 2100 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }

        return new DateOnly(year, month, day);
    }
}

public static partial class DateTokenParser
{
    private static readonly string[] MonthNames =
        ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];

    private static readonly Dictionary<string, int> FrenchMonths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JANV"] = 1, ["FÉVR"] = 2, ["FEVR"] = 2, ["MARS"] = 3, ["AVR"] = 4, ["MAI"] = 5, ["JUIN"] = 6,
        ["JUIL"] = 7, ["AOÛT"] = 8, ["AOUT"] = 8, ["SEPT"] = 9, ["OCT"] = 10, ["NOV"] = 11, ["DÉC"] = 12, ["DEC"] = 12,
    };

    [GeneratedRegex(@"^(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})$")]
    private static partial Regex Iso();

    [GeneratedRegex(@"^(\d{1,2})[/.-](\d{1,2})(?:[/.-](\d{2}|\d{4}))?$")]
    private static partial Regex Numeric();

    [GeneratedRegex(@"^(\d{1,2})[-\s]?([A-Za-zÀ-ÿ]{3,9})\.?(?:[-\s,]?(\d{2}|\d{4}))?$")]
    private static partial Regex DayMonthCompact();

    [GeneratedRegex(@"^([A-Za-zÀ-ÿ]{3,9})\.?[-\s]?(\d{1,2})(?:,?[-\s]?(\d{4}))?,?$")]
    private static partial Regex MonthDayCompact();

    [GeneratedRegex(@"^\d{1,2}(?:st|nd|rd|th)?,?$", RegexOptions.IgnoreCase)]
    private static partial Regex DayOnly();

    [GeneratedRegex(@"^\d{4},?$")]
    private static partial Regex YearOnly();

    /// <summary>
    /// Tries to read a date starting at <paramref name="index"/>, spanning up to three words
    /// ("Aug", "12,", "2026"). Returns how many words were consumed.
    /// </summary>
    public static bool TryParseAt(IReadOnlyList<string> words, int index, out PartialDate date, out int consumed)
    {
        date = default;
        consumed = 0;
        if (index >= words.Count)
        {
            return false;
        }

        var w0 = words[index].Trim().TrimEnd(',');

        if (TryParseSingle(w0, out date))
        {
            consumed = 1;
            if (date.Year is null && index + 1 < words.Count && YearOnly().IsMatch(words[index + 1]) && !date.IsNumeric)
            {
                date = date with { Year = int.Parse(words[index + 1].TrimEnd(','), CultureInfo.InvariantCulture) };
                consumed = 2;
            }

            return true;
        }

        if (index + 1 >= words.Count)
        {
            return false;
        }

        var w1 = words[index + 1].Trim().TrimEnd(',', '.');

        // "Aug 12" / "August 12, 2026"
        if (TryMonth(w0, out var month) && DayOnly().IsMatch(words[index + 1]))
        {
            date = new PartialDate(month, ParseDay(w1), null, IsNumeric: false);
            consumed = 2;
        }
        // "12 Aug" / "12 August 2026"
        else if (DayOnly().IsMatch(w0) && TryMonth(w1, out month))
        {
            date = new PartialDate(month, ParseDay(w0), null, IsNumeric: false);
            consumed = 2;
        }
        else
        {
            return false;
        }

        if (index + 2 < words.Count && YearOnly().IsMatch(words[index + 2]))
        {
            date = date with { Year = int.Parse(words[index + 2].TrimEnd(','), CultureInfo.InvariantCulture) };
            consumed = 3;
        }

        return date.Second is >= 1 and <= 31;
    }

    /// <summary>Parses a free-text date such as "August 1, 2026", "01/08/2026" or "2026-08-01".</summary>
    public static bool TryParseText(string text, out PartialDate date)
    {
        var words = text.Split([' ', ' '], StringSplitOptions.RemoveEmptyEntries);
        return TryParseAt(words, 0, out date, out var consumed) && consumed == words.Length;
    }

    private static bool TryParseSingle(string token, out PartialDate date)
    {
        date = default;

        var iso = Iso().Match(token);
        if (iso.Success)
        {
            date = new PartialDate(Int(iso.Groups[2]), Int(iso.Groups[3]), Int(iso.Groups[1]), IsNumeric: false);
            return date.First is >= 1 and <= 12;
        }

        var numeric = Numeric().Match(token);
        if (numeric.Success)
        {
            var a = Int(numeric.Groups[1]);
            var b = Int(numeric.Groups[2]);
            int? year = numeric.Groups[3].Success ? NormalizeYear(Int(numeric.Groups[3])) : null;

            // Needs a year or a slash to avoid reading "12-50" style references as dates.
            if (!numeric.Groups[3].Success && !token.Contains('/'))
            {
                return false;
            }

            if (a is < 1 or > 31 || b is < 1 or > 31 || (a > 12 && b > 12))
            {
                return false;
            }

            date = new PartialDate(a, b, year, IsNumeric: true);
            return true;
        }

        var dayMonth = DayMonthCompact().Match(token);
        if (dayMonth.Success && TryMonth(dayMonth.Groups[2].Value, out var month))
        {
            int? year = dayMonth.Groups[3].Success ? NormalizeYear(Int(dayMonth.Groups[3])) : null;
            date = new PartialDate(month, Int(dayMonth.Groups[1]), year, IsNumeric: false);
            return date.Second is >= 1 and <= 31;
        }

        var monthDay = MonthDayCompact().Match(token);
        if (monthDay.Success && monthDay.Groups[2].Value.Length > 0 && TryMonth(monthDay.Groups[1].Value, out month)
            && !token.Contains(' '))
        {
            int? year = monthDay.Groups[3].Success ? Int(monthDay.Groups[3]) : null;
            date = new PartialDate(month, Int(monthDay.Groups[2]), year, IsNumeric: false);
            return date.Second is >= 1 and <= 31;
        }

        return false;
    }

    public static bool TryMonth(string token, out int month)
    {
        month = 0;
        var upper = token.Trim().TrimEnd('.', ',').ToUpperInvariant();
        if (upper.Length < 3)
        {
            return false;
        }

        for (var i = 0; i < MonthNames.Length; i++)
        {
            var full = CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(i + 1).ToUpperInvariant();
            if (upper == MonthNames[i] || upper == full || (upper == "SEPT" && i == 8))
            {
                month = i + 1;
                return true;
            }
        }

        return FrenchMonths.TryGetValue(upper, out month);
    }

    private static int ParseDay(string token) =>
        int.Parse(new string(token.TakeWhile(char.IsAsciiDigit).ToArray()), CultureInfo.InvariantCulture);

    private static int Int(Group group) => int.Parse(group.Value, CultureInfo.InvariantCulture);

    private static int NormalizeYear(int year) => year < 100 ? 2000 + year : year;
}
