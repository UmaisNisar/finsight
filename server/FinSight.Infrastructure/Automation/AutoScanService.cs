using System.Security.Cryptography;
using FinSight.Core.Domain;
using FinSight.Core.Statements;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Pipeline;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinSight.Infrastructure.Automation;

public enum AutoScanOutcome
{
    /// <summary>Another runner claimed this user first.</summary>
    NotClaimed,

    /// <summary>The user already had a job running; the scan was moved to later.</summary>
    Busy,

    Scanned,

    /// <summary>The scan job failed (for example Gmail access expired). The next attempt is the next daily slot.</summary>
    Failed,

    /// <summary>The scan was queued but didn't finish in time; automatic import waits for the next scan.</summary>
    TimedOut,
}

public sealed record AutoScanResult(Guid UserId, AutoScanOutcome Outcome, Guid? ScanJobId = null, Guid? ImportJobId = null);

/// <summary>
/// Scheduled Gmail scans for users who opted in. Each user has a stable, pseudo-random time of day, so scans spread
/// out. A scan goes through the same job path as "Scan Gmail" (<see cref="JobService"/>, <see cref="JobWorker"/>,
/// <see cref="JobRunner"/>), so discovery, statement alerts and deduplication are identical. Scans only discover:
/// the one exception is the user's opt-in to import statements for accounts they have imported before.
/// </summary>
public sealed partial class AutoScanService(IServiceScopeFactory scopes, TimeProvider time, IOptions<AutomationOptions> options, ILogger<AutoScanService> logger)
{
    public const int MaxStatementsPerImport = 60;

    /// <summary>
    /// The first slot strictly after <paramref name="after"/>. A user's slots sit at the same offset within every
    /// interval (derived from their id), so a daily scan happens at about the same time each day.
    /// </summary>
    public static DateTimeOffset NextSlot(Guid userId, DateTimeOffset after, TimeSpan interval)
    {
        var hash = BitConverter.ToUInt64(SHA256.HashData(userId.ToByteArray()), 0);
        // Whole seconds, so a slot survives the database's timestamp precision unchanged.
        var offset = (long)(hash % (ulong)(interval.Ticks / TimeSpan.TicksPerSecond)) * TimeSpan.TicksPerSecond;
        var anchor = DateTimeOffset.UnixEpoch.UtcTicks + offset;
        var periods = (after.UtcTicks - anchor) / interval.Ticks;
        var next = anchor + (periods + 1) * interval.Ticks;
        while (next <= after.UtcTicks)
        {
            next += interval.Ticks;
        }

        return new DateTimeOffset(next, TimeSpan.Zero);
    }

    public async Task<IReadOnlyList<AutoScanResult>> RunDueAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var interval = settings.EffectiveScanInterval;
        var now = time.GetUtcNow();
        List<(Guid Id, DateTimeOffset? Next)> due;

        await using (var scope = scopes.CreateAsyncScope())
        {
            var userContext = scope.ServiceProvider.GetRequiredService<UserContext>();
            var db = scope.ServiceProvider.GetRequiredService<FinSightDbContext>();
            using (userContext.BeginSystemScope())
            {
                // Users who just opted in get their daily slot; the first scan happens then, not immediately.
                var unscheduled = await db.Users.AsNoTracking()
                    .Where(u => u.Settings.AutoScanEnabled && !u.IsDemo && u.NextAutoScanAt == null)
                    .Select(u => u.Id)
                    .Take(500)
                    .ToListAsync(cancellationToken);
                foreach (var id in unscheduled)
                {
                    var slot = NextSlot(id, now, interval);
                    await db.Users.Where(u => u.Id == id && u.NextAutoScanAt == null)
                        .ExecuteUpdateAsync(s => s.SetProperty(u => u.NextAutoScanAt, slot), cancellationToken);
                }

                // Only active connections: an expired grant is never retried until the user reconnects.
                var rows = await db.Users.AsNoTracking()
                    .Where(u => u.Settings.AutoScanEnabled && !u.IsDemo && u.NextAutoScanAt != null && u.NextAutoScanAt <= now)
                    .Where(u => db.GmailConnections.Any(c => c.UserId == u.Id && c.Status == GmailConnectionStatus.Active))
                    .OrderBy(u => u.NextAutoScanAt)
                    .Select(u => new { u.Id, u.NextAutoScanAt })
                    .Take(Math.Max(1, settings.BatchSize))
                    .ToListAsync(cancellationToken);
                due = rows.Select(r => (r.Id, r.NextAutoScanAt)).ToList();
            }
        }

        var results = new List<AutoScanResult>();
        for (var i = 0; i < due.Count; i++)
        {
            if (i > 0 && settings.PauseBetweenUsers > TimeSpan.Zero)
            {
                await Task.Delay(settings.PauseBetweenUsers, time, cancellationToken);
            }

            try
            {
                results.Add(await ScanUserAsync(due[i].Id, due[i].Next, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogScanError(logger, ex.GetType().Name, due[i].Id);
                results.Add(new AutoScanResult(due[i].Id, AutoScanOutcome.Failed));
            }
        }

        return results;
    }

    private async Task<AutoScanResult> ScanUserAsync(Guid userId, DateTimeOffset? observedNext, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = time.GetUtcNow();

        await using (var system = scopes.CreateAsyncScope())
        {
            var userContext = system.ServiceProvider.GetRequiredService<UserContext>();
            var db = system.ServiceProvider.GetRequiredService<FinSightDbContext>();
            using (userContext.BeginSystemScope())
            {
                var busy = await HasActiveJobAsync(db, userId, cancellationToken);
                // At least half an interval ahead: a late run (after downtime) never leads to a second scan shortly after.
                var interval = settings.EffectiveScanInterval;
                var next = busy ? now + settings.BusyRetryDelay : NextSlot(userId, now + interval / 2, interval);

                // Claim by moving the due time forward from the value this runner read. A second runner reading the same
                // row updates nothing, so a user is never scanned twice for one slot.
                var claimed = await db.Users.Where(u => u.Id == userId && u.NextAutoScanAt == observedNext)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.NextAutoScanAt, next), cancellationToken);
                if (claimed == 0)
                {
                    return new AutoScanResult(userId, AutoScanOutcome.NotClaimed);
                }

                if (busy)
                {
                    return new AutoScanResult(userId, AutoScanOutcome.Busy);
                }
            }
        }

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().SetUser(userId);
        var jobs = scope.ServiceProvider.GetRequiredService<JobService>();
        var userDb = scope.ServiceProvider.GetRequiredService<FinSightDbContext>();

        var job = await jobs.CreateAsync(userId, JobKind.Sync, StatementLabels.SyncPlan(), cancellationToken);
        await jobs.EnqueueAsync(new JobWorkItem(job.Id, userId, JobKind.Sync, []), cancellationToken);

        var status = await WaitForJobAsync(userDb, job.Id, cancellationToken);
        if (status is null)
        {
            return new AutoScanResult(userId, AutoScanOutcome.TimedOut, job.Id);
        }

        if (status != JobStatus.Succeeded)
        {
            LogScanFailed(logger, userId);
            return new AutoScanResult(userId, AutoScanOutcome.Failed, job.Id);
        }

        var importJob = await ImportFromKnownAccountsAsync(scope.ServiceProvider, userId, cancellationToken);
        return new AutoScanResult(userId, AutoScanOutcome.Scanned, job.Id, importJob);
    }

    private static Task<bool> HasActiveJobAsync(FinSightDbContext db, Guid userId, CancellationToken cancellationToken) =>
        db.ProcessingJobs.AnyAsync(j => j.UserId == userId && (j.Status == JobStatus.Queued || j.Status == JobStatus.Running), cancellationToken);

    private async Task<JobStatus?> WaitForJobAsync(FinSightDbContext db, Guid jobId, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var deadline = time.GetUtcNow() + settings.ScanTimeout;
        while (true)
        {
            var status = await db.ProcessingJobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => (JobStatus?)j.Status).SingleOrDefaultAsync(cancellationToken);
            if (status is null or JobStatus.Succeeded or JobStatus.Failed)
            {
                return status ?? JobStatus.Failed;
            }

            if (time.GetUtcNow() >= deadline)
            {
                return null;
            }

            await Task.Delay(settings.JobPollInterval, time, cancellationToken);
        }
    }

    /// <summary>
    /// The opt-in exception to "scans only discover": statements found after the user turned it on, from a bank and
    /// account (last four digits, read from the masked subject or file name) they have already imported, are processed.
    /// Anything less certain waits for the user.
    /// </summary>
    private static async Task<Guid?> ImportFromKnownAccountsAsync(IServiceProvider services, Guid userId, CancellationToken cancellationToken)
    {
        var db = services.GetRequiredService<FinSightDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(cancellationToken);
        if (!user.Settings.AutoScanEnabled || !user.Settings.AutoImportEnabled || user.AutoImportEnabledAt is not { } since)
        {
            return null;
        }

        if (await HasActiveJobAsync(db, userId, cancellationToken))
        {
            return null;
        }

        var discovered = await db.Statements.AsNoTracking()
            .Where(s => s.Source == StatementSourceKind.Gmail && s.Status == StatementStatus.Discovered && s.CreatedAt >= since && s.Institution != null)
            .ToListAsync(cancellationToken);
        if (discovered.Count == 0)
        {
            return null;
        }

        var known = (await db.Statements.AsNoTracking()
            .Where(s => s.Status == StatementStatus.Processed && s.Institution != null && s.AccountMask != null)
            .Select(s => new { s.Institution, s.AccountMask })
            .Distinct()
            .ToListAsync(cancellationToken))
            .Select(k => new KnownAccount(k.Institution!, k.AccountMask!))
            .ToList();

        var selected = AutoImportRules.Select(discovered, known).Take(MaxStatementsPerImport).ToList();
        if (selected.Count == 0)
        {
            return null;
        }

        var jobs = services.GetRequiredService<JobService>();
        var job = await jobs.CreateAsync(userId, JobKind.Process, StatementLabels.ProcessingPlan(selected), cancellationToken);
        await jobs.EnqueueAsync(new JobWorkItem(job.Id, userId, JobKind.Process, selected.Select(s => s.Id).ToList()), cancellationToken);
        return job.Id;
    }

    [LoggerMessage(LogLevel.Information, "Scheduled Gmail scan for user {UserId} did not succeed")]
    private static partial void LogScanFailed(ILogger logger, Guid userId);

    [LoggerMessage(LogLevel.Error, "Scheduled Gmail scan for user {UserId} failed with {ExceptionType}")]
    private static partial void LogScanError(ILogger logger, string exceptionType, Guid userId);
}

public sealed record KnownAccount(string Institution, string AccountMask);

public static class AutoImportRules
{
    /// <summary>
    /// Discovered Gmail statements (not alerts, bank or credit card statements only) whose institution and last four
    /// digits both match an account the user has already imported. A statement whose email doesn't show the last four
    /// digits is never selected.
    /// </summary>
    public static IEnumerable<Statement> Select(IEnumerable<Statement> discovered, IReadOnlyCollection<KnownAccount> known) =>
        discovered.Where(s =>
            s.Source == StatementSourceKind.Gmail
            && s.Status == StatementStatus.Discovered
            && !StatementAlert.IsAlert(s)
            && s.DocumentKind is DocumentKind.BankStatement or DocumentKind.CreditCardStatement
            && s.Institution is not null
            && MaskOf(s) is { } mask
            && known.Any(k => string.Equals(k.Institution, s.Institution, StringComparison.OrdinalIgnoreCase) && k.AccountMask == mask));

    public static string? MaskOf(Statement statement) =>
        statement.AccountMask ?? StatementEmailClassifier.ExtractAccountMask($"{statement.Subject} {statement.Filename}");
}
