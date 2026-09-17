namespace FinSight.Infrastructure.Automation;

public sealed class AutomationOptions
{
    public const string Section = "Automation";

    /// <summary>Runs automatic Gmail scans and monthly summary emails. Turning it off stops both for every user.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often each opted-in user's Gmail is scanned. At least an hour.</summary>
    public TimeSpan ScanInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>How often the scheduler wakes to look for due work.</summary>
    public TimeSpan TickInterval { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Most users handled per tick, for scans and for emails.</summary>
    public int BatchSize { get; set; } = 10;

    /// <summary>A breather between users, so a busy slot doesn't hammer Gmail, the database or the SMTP server.</summary>
    public TimeSpan PauseBetweenUsers { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>How long to wait for a scheduled scan to finish before moving on (automatic import is then skipped until the next scan).</summary>
    public TimeSpan ScanTimeout { get; set; } = TimeSpan.FromMinutes(10);

    public TimeSpan JobPollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>A user busy with another job when their scan is due is tried again after this long.</summary>
    public TimeSpan BusyRetryDelay { get; set; } = TimeSpan.FromMinutes(30);

    public TimeSpan EffectiveScanInterval => ScanInterval < TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : ScanInterval;
}
