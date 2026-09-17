using System.Collections.Concurrent;
using System.Threading.Channels;
using FinSight.Core.Categories;
using FinSight.Core.Domain;
using FinSight.Infrastructure.Gemini;
using FinSight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace FinSight.Infrastructure.Pipeline;

/// <summary>
/// Gives AI categorization another go for merchants that stayed uncategorized because Gemini was unavailable when they were
/// imported. Runs hourly, trying each user at most once per <see cref="AiOptions.PendingRetryHours"/>, and straight away when a
/// user saves a Gemini key. Demo users and users with AI categorization off are skipped.
/// </summary>
/// <remarks>
/// State is in memory (single instance): when the last try was, and merchants Gemini already looked at without placing them,
/// so the same unplaceable merchants aren't sent again. A restart forgets both, which costs at most one extra try.
/// </remarks>
public sealed partial class PendingCategorizationRetry(IServiceScopeFactory scopes, IOptions<AiOptions> options, TimeProvider time, ILogger<PendingCategorizationRetry> logger)
    : BackgroundService
{
    private readonly Channel<Guid> _requests = Channel.CreateUnbounded<Guid>(new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastAttempt = new();
    private readonly ConcurrentDictionary<Guid, HashSet<string>> _declined = new();

    /// <summary>Retries this user soon, regardless of when they were last tried. Used when a key is saved.</summary>
    public void Request(Guid userId)
    {
        _declined.TryRemove(userId, out _);
        _requests.Writer.TryWrite(userId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.PendingRetryEnabled)
        {
            return;
        }

        var requested = ConsumeRequestsAsync(stoppingToken);

        // Let startup settle before the first sweep.
        await Task.Delay(TimeSpan.FromMinutes(1), time, stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), time);
        do
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));

        await requested;
    }

    private async Task ConsumeRequestsAsync(CancellationToken stoppingToken)
    {
        await foreach (var userId in _requests.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await RetryUserAsync(userId, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(logger, ex.GetType().Name);
            }
        }
    }

    /// <summary>Tries every user with pending merchants who is due. Public so tests can drive it without the timer.</summary>
    public async Task SweepAsync(CancellationToken cancellationToken)
    {
        List<Guid> users;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var userContext = scope.ServiceProvider.GetRequiredService<UserContext>();
            var db = scope.ServiceProvider.GetRequiredService<FinSightDbContext>();
            using (userContext.BeginSystemScope())
            {
                var candidates = db.Transactions
                    .Where(t => t.UserCategoryId == null && t.UserType == null && !t.IsReversal
                        && t.CategorySource != CategorySource.User && t.CategorySource != CategorySource.Ai
                        && (t.CategorySource == CategorySource.Default || t.CategoryConfidence < CategorizationResult.AiThreshold))
                    .Select(t => t.UserId)
                    .Distinct();

                users = await db.Users
                    .Where(u => !u.IsDemo && u.Settings.AiCategorizationEnabled && candidates.Contains(u.Id))
                    .Select(u => u.Id)
                    .ToListAsync(cancellationToken);
            }
        }

        var due = time.GetUtcNow() - TimeSpan.FromHours(options.Value.PendingRetryHours);
        foreach (var userId in users.Where(u => !_lastAttempt.TryGetValue(u, out var last) || last <= due))
        {
            await RetryUserAsync(userId, cancellationToken);
        }
    }

    public async Task<PendingCategorizationResult?> RetryUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<UserContext>().SetUser(userId);
        var db = scope.ServiceProvider.GetRequiredService<FinSightDbContext>();

        var settings = await db.Users.AsNoTracking().Select(u => new { u.IsDemo, u.Settings.AiCategorizationEnabled }).SingleOrDefaultAsync(cancellationToken);
        if (settings is null || settings.IsDemo || !settings.AiCategorizationEnabled)
        {
            return null;
        }

        _lastAttempt[userId] = time.GetUtcNow();
        var declined = _declined.GetOrAdd(userId, _ => new HashSet<string>(StringComparer.Ordinal));
        IReadOnlySet<string> skip;
        lock (declined)
        {
            skip = declined.ToHashSet(StringComparer.Ordinal);
        }

        var result = await scope.ServiceProvider.GetRequiredService<CategorizationService>().RetryPendingWithAiAsync(skip, cancellationToken);
        lock (declined)
        {
            declined.UnionWith(result.Declined);
        }

        if (result.Outcome.MerchantsCategorized > 0)
        {
            LogCategorized(logger, result.Outcome.MerchantsCategorized, userId);
        }

        return result;
    }

    [LoggerMessage(LogLevel.Information, "AI categorized {Count} pending merchants for user {UserId}")]
    private static partial void LogCategorized(ILogger logger, int count, Guid userId);

    [LoggerMessage(LogLevel.Warning, "Pending categorization retry failed with {ExceptionType}")]
    private static partial void LogFailed(ILogger logger, string exceptionType);
}
