using FinSight.Core.Domain;
using FinSight.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace FinSight.Api.Auth;

/// <summary>
/// Server-side record of signed-in sessions. The cookie is encrypted and signed, but on its own it stays valid until it
/// expires; tying it to a row lets sign-out and account deletion end it for good, and caps a session's total lifetime
/// even when sliding expiration keeps renewing the cookie.
/// </summary>
/// <remarks>Every method acts for the user in <see cref="UserContext"/>, which must already be set.</remarks>
public sealed class SessionService(FinSightDbContext db, IMemoryCache cache, TimeProvider time)
{
    /// <summary>A session ends this long after sign-in, however active it is.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromDays(30);

    /// <summary>How long a positive "this session is live" answer is reused before the database is asked again.</summary>
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    public async Task<Guid> StartAsync(Guid userId, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();

        // Tidy up this user's sessions that ended without a sign-out.
        var expired = (await db.UserSessions.Select(s => new { s.Id, s.CreatedAt }).ToListAsync(cancellationToken))
            .Where(s => s.CreatedAt + MaxLifetime <= now)
            .Select(s => s.Id)
            .ToList();
        if (expired.Count > 0)
        {
            await db.UserSessions.Where(s => expired.Contains(s.Id)).ExecuteDeleteAsync(cancellationToken);
        }

        var session = new UserSession { UserId = userId, CreatedAt = now };
        db.UserSessions.Add(session);
        await db.SaveChangesAsync(cancellationToken);
        return session.Id;
    }

    public async Task<bool> IsActiveAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var createdAt = await cache.GetOrCreateAsync(CacheKey(sessionId), async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheDuration;
            return await db.UserSessions.Where(s => s.Id == sessionId).Select(s => (DateTimeOffset?)s.CreatedAt).SingleOrDefaultAsync(cancellationToken);
        });

        return createdAt is not null && createdAt.Value + MaxLifetime > time.GetUtcNow();
    }

    public async Task EndAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        await db.UserSessions.Where(s => s.Id == sessionId).ExecuteDeleteAsync(cancellationToken);
        cache.Remove(CacheKey(sessionId));
    }

    private static string CacheKey(Guid sessionId) => $"session:{sessionId}";
}
