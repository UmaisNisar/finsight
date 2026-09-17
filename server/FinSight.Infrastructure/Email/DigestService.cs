using FinSight.Core.Analytics;
using FinSight.Infrastructure.Automation;
using FinSight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinSight.Infrastructure.Email;

public enum TestDigestResult
{
    Sent,
    NotConfigured,
    DemoUser,
    Failed,
}

/// <summary>
/// Sends monthly summary emails. A month is claimed per user with compare-and-set updates on the user row, and marked
/// sent before the message goes out (reverted if delivery fails), so restarts and a second server instance never send
/// the same month twice. Failed deliveries are retried on later ticks with a growing delay, up to
/// <see cref="EmailOptions.DigestMaxAttempts"/> per month.
/// </summary>
public sealed partial class DigestService(
    IServiceScopeFactory scopes,
    IEmailSender sender,
    EmailAvailability availability,
    EmailLinks links,
    UnsubscribeTokens unsubscribeTokens,
    TimeProvider time,
    IOptions<EmailOptions> emailOptions,
    IOptions<AutomationOptions> automationOptions,
    ILogger<DigestService> logger)
{
    /// <summary>Long enough to build and send one message; another instance can take over after it.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(15);

    /// <summary>A month with no data yet is checked again this much later, while its summary is still due.</summary>
    public static readonly TimeSpan NoDataRetry = TimeSpan.FromDays(1);

    public static readonly TimeSpan FailureBackoff = TimeSpan.FromHours(1);

    /// <returns>How many summaries were sent.</returns>
    public async Task<int> RunDueAsync(CancellationToken cancellationToken)
    {
        if (!availability.IsConfigured)
        {
            return 0;
        }

        var now = time.GetUtcNow();
        if (DigestSchedule.DueMonth(now) is not { } month)
        {
            return 0;
        }

        var key = MonthlyDigest.MonthKeyOf(month.Start);
        var maxAttempts = Math.Max(1, emailOptions.Value.DigestMaxAttempts);
        var settings = automationOptions.Value;

        List<Guid> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var userContext = scope.ServiceProvider.GetRequiredService<UserContext>();
            var db = scope.ServiceProvider.GetRequiredService<FinSightDbContext>();
            using (userContext.BeginSystemScope())
            {
                due = await db.Users.AsNoTracking()
                    .Where(u => u.Settings.MonthlyDigestEnabled && !u.IsDemo)
                    .Where(u => u.LastDigestSentFor == null || u.LastDigestSentFor != key)
                    .Where(u => u.DigestNextAttemptAt == null || u.DigestNextAttemptAt <= now)
                    .Where(u => !(u.DigestFailedFor == key && u.DigestFailures >= maxAttempts))
                    .OrderBy(u => u.DigestNextAttemptAt)
                    .Select(u => u.Id)
                    .Take(Math.Max(1, settings.BatchSize))
                    .ToListAsync(cancellationToken);
            }
        }

        var sent = 0;
        for (var i = 0; i < due.Count; i++)
        {
            if (i > 0 && settings.PauseBetweenUsers > TimeSpan.Zero)
            {
                await Task.Delay(settings.PauseBetweenUsers, time, cancellationToken);
            }

            try
            {
                if (await SendDueAsync(due[i], month, key, maxAttempts, cancellationToken))
                {
                    sent++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogDigestError(logger, ex.GetType().Name, due[i]);
            }
        }

        return sent;
    }

    private async Task<bool> SendDueAsync(Guid userId, DateRange month, string key, int maxAttempts, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        await using var system = scopes.CreateAsyncScope();
        var systemContext = system.ServiceProvider.GetRequiredService<UserContext>();
        var systemDb = system.ServiceProvider.GetRequiredService<FinSightDbContext>();
        using var _ = systemContext.BeginSystemScope();

        // Claim: only one runner can move the attempt time forward from what it was.
        var claimed = await systemDb.Users
            .Where(u => u.Id == userId && u.Settings.MonthlyDigestEnabled && !u.IsDemo)
            .Where(u => u.LastDigestSentFor == null || u.LastDigestSentFor != key)
            .Where(u => u.DigestNextAttemptAt == null || u.DigestNextAttemptAt <= now)
            .Where(u => !(u.DigestFailedFor == key && u.DigestFailures >= maxAttempts))
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.DigestNextAttemptAt, now + Lease), cancellationToken);
        if (claimed == 0)
        {
            return false;
        }

        var state = await systemDb.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new { u.Email, u.LastDigestSentFor, u.DigestFailedFor, u.DigestFailures })
            .SingleAsync(cancellationToken);

        var marked = false;
        try
        {
            MonthlyDigest digest;
            await using (var userScope = scopes.CreateAsyncScope())
            {
                userScope.ServiceProvider.GetRequiredService<UserContext>().SetUser(userId);
                digest = await userScope.ServiceProvider.GetRequiredService<DigestBuilder>().BuildAsync(month, cancellationToken);
            }

            if (!digest.HasData)
            {
                await systemDb.Users.Where(u => u.Id == userId)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.DigestNextAttemptAt, now + NoDataRetry), cancellationToken);
                return false;
            }

            // Mark the month sent first: a crash mid-send then loses one email rather than sending it twice.
            marked = await systemDb.Users.Where(u => u.Id == userId && (u.LastDigestSentFor == null || u.LastDigestSentFor != key))
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastDigestSentFor, key), cancellationToken) == 1;
            if (!marked)
            {
                return false;
            }

            await sender.SendAsync(Compose(userId, state.Email, digest, isTest: false), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Delivery failures and unexpected errors alike count towards the month's cap, so nothing retries forever.
            var failures = state.DigestFailedFor == key ? state.DigestFailures + 1 : 1;
            var retryAt = time.GetUtcNow() + FailureBackoff * Math.Pow(2, failures - 1);
            // Unmark the month (the claim guarantees it held the earlier value before this attempt).
            await systemDb.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
                .SetProperty(u => u.LastDigestSentFor, state.LastDigestSentFor)
                .SetProperty(u => u.DigestFailedFor, key)
                .SetProperty(u => u.DigestFailures, failures)
                .SetProperty(u => u.DigestNextAttemptAt, retryAt), CancellationToken.None);
            if (ex is EmailDeliveryException)
            {
                LogDigestFailed(logger, userId, failures, maxAttempts);
            }
            else
            {
                LogDigestError(logger, ex.GetType().Name, userId);
            }

            return false;
        }

        await systemDb.Users.Where(u => u.Id == userId).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.DigestNextAttemptAt, (DateTimeOffset?)null)
            .SetProperty(u => u.DigestFailedFor, (string?)null)
            .SetProperty(u => u.DigestFailures, 0), CancellationToken.None);
        LogDigestSent(logger, userId);
        return true;
    }

    /// <summary>Sends the signed-in user a summary of last month now, marked as a test. Doesn't count as the month's summary.</summary>
    public async Task<TestDigestResult> SendTestAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (!availability.IsConfigured)
        {
            return TestDigestResult.NotConfigured;
        }

        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().SetUser(userId);
        var db = scope.ServiceProvider.GetRequiredService<FinSightDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(cancellationToken);
        if (user.IsDemo)
        {
            return TestDigestResult.DemoUser;
        }

        var month = DigestSchedule.PreviousMonth(DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime));
        var digest = await scope.ServiceProvider.GetRequiredService<DigestBuilder>().BuildAsync(month, cancellationToken);

        try
        {
            await sender.SendAsync(Compose(userId, user.Email, digest, isTest: true), cancellationToken);
            return TestDigestResult.Sent;
        }
        catch (EmailDeliveryException)
        {
            return TestDigestResult.Failed;
        }
    }

    public EmailMessage Compose(Guid userId, string to, MonthlyDigest digest, bool isTest)
    {
        var unsubscribe = links.Unsubscribe(unsubscribeTokens.Create(userId));
        var rendered = DigestRenderer.Render(digest, links.AppUrl!, unsubscribe, isTest);
        return new EmailMessage(to, rendered.Subject, rendered.Html, rendered.Text, new Dictionary<string, string>
        {
            // RFC 8058 one-click unsubscribe: mail clients POST "List-Unsubscribe=One-Click" to the URL.
            ["List-Unsubscribe"] = $"<{unsubscribe}>",
            ["List-Unsubscribe-Post"] = "List-Unsubscribe=One-Click",
            ["Auto-Submitted"] = "auto-generated",
        });
    }

    // Log user ids only: never the address together with any figure.
    [LoggerMessage(LogLevel.Information, "Monthly summary sent for user {UserId}")]
    private static partial void LogDigestSent(ILogger logger, Guid userId);

    [LoggerMessage(LogLevel.Warning, "Monthly summary for user {UserId} failed to send (attempt {Attempt} of {MaxAttempts})")]
    private static partial void LogDigestFailed(ILogger logger, Guid userId, int attempt, int maxAttempts);

    [LoggerMessage(LogLevel.Error, "Monthly summary for user {UserId} failed with {ExceptionType}")]
    private static partial void LogDigestError(ILogger logger, string exceptionType, Guid userId);
}
