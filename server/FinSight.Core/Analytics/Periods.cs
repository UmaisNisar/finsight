using System.Globalization;

namespace FinSight.Core.Analytics;

public enum PeriodPreset
{
    ThisMonth,
    LastMonth,
    Last3Months,
    Last6Months,
    Last12Months,
    Custom,
}

/// <summary>An inclusive range of calendar days.</summary>
public sealed record DateRange(DateOnly Start, DateOnly End)
{
    public int Days => End.DayNumber - Start.DayNumber + 1;

    public bool Contains(DateOnly date) => date >= Start && date <= End;

    /// <summary>The first day of every calendar month the range touches.</summary>
    public IEnumerable<DateOnly> Months()
    {
        for (var month = new DateOnly(Start.Year, Start.Month, 1); month <= End; month = month.AddMonths(1))
        {
            yield return month;
        }
    }

    public string Label()
    {
        var culture = CultureInfo.InvariantCulture;
        if (Start.Day == 1 && End == new DateOnly(Start.Year, Start.Month, DateTime.DaysInMonth(Start.Year, Start.Month)))
        {
            return Start.ToString("MMMM yyyy", culture);
        }

        return Start.Year == End.Year
            ? $"{Start.ToString("MMM d", culture)} – {End.ToString("MMM d, yyyy", culture)}"
            : $"{Start.ToString("MMM d, yyyy", culture)} – {End.ToString("MMM d, yyyy", culture)}";
    }
}

public static class PeriodResolver
{
    /// <summary>
    /// "Last N months" means the N most recent complete calendar months. Statements arrive after a
    /// month closes, so including the current, partial month would understate every average.
    /// </summary>
    public static DateRange Resolve(PeriodPreset preset, DateOnly today, DateOnly? customStart = null, DateOnly? customEnd = null)
    {
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var lastMonthEnd = thisMonth.AddDays(-1);

        return preset switch
        {
            PeriodPreset.ThisMonth => new DateRange(thisMonth, thisMonth.AddMonths(1).AddDays(-1)),
            PeriodPreset.LastMonth => new DateRange(thisMonth.AddMonths(-1), lastMonthEnd),
            PeriodPreset.Last3Months => new DateRange(thisMonth.AddMonths(-3), lastMonthEnd),
            PeriodPreset.Last6Months => new DateRange(thisMonth.AddMonths(-6), lastMonthEnd),
            PeriodPreset.Last12Months => new DateRange(thisMonth.AddMonths(-12), lastMonthEnd),
            PeriodPreset.Custom when customStart is not null && customEnd is not null && customStart <= customEnd
                => new DateRange(customStart.Value, customEnd.Value),
            _ => throw new ArgumentException("A custom period needs a start date on or before its end date."),
        };
    }

    /// <summary>The period of equal length immediately before <paramref name="range"/>.</summary>
    public static DateRange Previous(DateRange range)
    {
        var isWholeMonths = range.Start.Day == 1 && range.End.AddDays(1).Day == 1;
        if (isWholeMonths)
        {
            var months = range.Months().Count();
            return new DateRange(range.Start.AddMonths(-months), range.Start.AddDays(-1));
        }

        return new DateRange(range.Start.AddDays(-range.Days), range.Start.AddDays(-1));
    }
}
