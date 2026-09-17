using System.Collections.Concurrent;
using FinSight.Core.Abstractions;
using Microsoft.Extensions.Options;

namespace FinSight.Infrastructure.Gemini;

public sealed class AiOptions
{
    public const string Section = "Ai";

    public AiDailyLimits DailyLimits { get; set; } = new();

    /// <summary>
    /// Calls per UTC day made with the server's Gemini key, across every account. Per-user limits bound what one person can spend;
    /// this bounds what everyone together can spend of the server key. Calls on a user's own key don't count towards it.
    /// </summary>
    public int GlobalDailyLimit { get; set; } = 400;

    /// <summary>Accounts still served after <see cref="GlobalDailyLimit"/> is reached, so strangers can't lock the owner out. Matched case-insensitively.</summary>
    public string[] PriorityEmails { get; set; } = [];

    /// <summary>Retries AI categorization for merchants left uncategorized when AI was unavailable. At most once per user per <see cref="PendingRetryHours"/>.</summary>
    public bool PendingRetryEnabled { get; set; } = true;

    public double PendingRetryHours { get; set; } = 6;
}

/// <summary>Calls per user per UTC day, by kind. Set well above normal use: a person who reaches one is automating something.</summary>
public sealed class AiDailyLimits
{
    /// <summary>Batches of up to 40 merchants. An import sends at most four.</summary>
    public int Categorization { get; set; } = 40;

    public int Analysis { get; set; } = 20;

    public int RecurringReview { get; set; } = 20;
}

public enum AiCallKind
{
    Categorization,
    Analysis,
    RecurringReview,
}

public enum AiQuotaDecision
{
    Allowed,

    /// <summary>This user's allowance for this kind of call is used up today.</summary>
    UserLimit,

    /// <summary>The server key's shared allowance is used up today.</summary>
    GlobalLimit,
}

/// <summary>
/// FinSight's own daily allowances for Gemini calls. Counters live in memory: this is a single-instance app, so they reset when the
/// server restarts and at midnight UTC. Refused calls aren't counted.
/// </summary>
public sealed class AiQuota(IOptions<AiOptions> options, TimeProvider time)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<(Guid UserId, AiCallKind Kind), int> _perUser = [];
    private DateOnly _day;
    private int _global;

    /// <summary>Counts one call if it's within every limit that applies.</summary>
    /// <param name="serverKey">Whether the call goes out on the server's key. Only those count towards the global ceiling.</param>
    public AiQuotaDecision TryConsume(Guid? userId, string? email, bool serverKey, AiCallKind kind)
    {
        lock (_lock)
        {
            var decision = Check(userId, email, serverKey, kind);
            if (decision != AiQuotaDecision.Allowed)
            {
                return decision;
            }

            if (userId is { } id)
            {
                _perUser[(id, kind)] = _perUser.GetValueOrDefault((id, kind)) + 1;
            }

            if (serverKey)
            {
                _global++;
            }

            return AiQuotaDecision.Allowed;
        }
    }

    /// <summary>What <see cref="TryConsume"/> would decide, without counting anything.</summary>
    public AiQuotaDecision Peek(Guid? userId, string? email, bool serverKey, AiCallKind kind)
    {
        lock (_lock)
        {
            return Check(userId, email, serverKey, kind);
        }
    }

    private AiQuotaDecision Check(Guid? userId, string? email, bool serverKey, AiCallKind kind)
    {
        var today = DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);
        if (today != _day)
        {
            _day = today;
            _perUser.Clear();
            _global = 0;
        }

        var settings = options.Value;
        if (userId is { } id && _perUser.GetValueOrDefault((id, kind)) >= LimitFor(settings.DailyLimits, kind))
        {
            return AiQuotaDecision.UserLimit;
        }

        // Checked second, so a person over their own allowance is told that rather than blamed for everyone else.
        return serverKey && _global >= settings.GlobalDailyLimit && !IsPriority(settings, email)
            ? AiQuotaDecision.GlobalLimit
            : AiQuotaDecision.Allowed;
    }

    private static int LimitFor(AiDailyLimits limits, AiCallKind kind) => kind switch
    {
        AiCallKind.Categorization => limits.Categorization,
        AiCallKind.Analysis => limits.Analysis,
        _ => limits.RecurringReview,
    };

    private static bool IsPriority(AiOptions settings, string? email) =>
        !string.IsNullOrWhiteSpace(email) && settings.PriorityEmails.Any(e => string.Equals(e?.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase));
}

public sealed record AiKeyFailure(AiFailure Failure, DateTimeOffset At);

/// <summary>
/// The last time Google refused a user's key or reported its quota used up, so Settings can say so. In memory, per user: cleared
/// by the next successful call, by saving or removing a key, and by a restart.
/// </summary>
public sealed class AiKeyHealth(TimeProvider time)
{
    private readonly ConcurrentDictionary<Guid, AiKeyFailure> _failures = new();

    public void Record(Guid? userId, AiFailure failure)
    {
        if (userId is { } id && failure is AiFailure.KeyRefused or AiFailure.RateLimited)
        {
            _failures[id] = new AiKeyFailure(failure, time.GetUtcNow());
        }
    }

    public void Clear(Guid? userId)
    {
        if (userId is { } id)
        {
            _failures.TryRemove(id, out _);
        }
    }

    public AiKeyFailure? Get(Guid? userId) => userId is { } id ? _failures.GetValueOrDefault(id) : null;
}
