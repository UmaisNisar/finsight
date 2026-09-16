using FinSight.Api.Contracts;
using FinSight.Core.Analytics;

namespace FinSight.Api.Controllers;

/// <summary>?period=last-month | this-month | last-3-months | last-6-months | last-12-months | custom&amp;from=&amp;to=</summary>
public sealed class PeriodQuery
{
    public string? Period { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }

    public bool TryResolve(DateOnly today, out DateRange range, out PeriodDto dto)
    {
        var preset = (Period ?? "last-month").ToLowerInvariant() switch
        {
            "this-month" => PeriodPreset.ThisMonth,
            "last-month" => PeriodPreset.LastMonth,
            "last-3-months" => PeriodPreset.Last3Months,
            "last-6-months" => PeriodPreset.Last6Months,
            "last-12-months" => PeriodPreset.Last12Months,
            "custom" => PeriodPreset.Custom,
            _ => (PeriodPreset?)null,
        };

        range = new DateRange(today, today);
        dto = new PeriodDto("last-month", today, today, string.Empty);

        if (preset is null || (preset == PeriodPreset.Custom && (From is null || To is null || From > To || To.Value.DayNumber - From.Value.DayNumber > 3 * 366)))
        {
            return false;
        }

        range = PeriodResolver.Resolve(preset.Value, today, From, To);
        dto = new PeriodDto(Period?.ToLowerInvariant() ?? "last-month", range.Start, range.End, range.Label());
        return true;
    }
}
