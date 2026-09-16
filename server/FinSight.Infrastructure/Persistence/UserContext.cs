namespace FinSight.Infrastructure.Persistence;

/// <summary>
/// Who the current unit of work acts for. Every query on user-owned data is filtered by this, and
/// an unset context matches no rows, so forgetting to set it fails closed.
/// </summary>
public interface IUserContext
{
    Guid? UserId { get; }

    /// <summary>True only inside <see cref="UserContext.BeginSystemScope"/>, for maintenance jobs.</summary>
    bool IsSystem { get; }
}

public sealed class UserContext : IUserContext
{
    private int _systemDepth;

    public Guid? UserId { get; private set; }

    public bool IsSystem => _systemDepth > 0;

    public void SetUser(Guid userId)
    {
        if (UserId is not null && UserId != userId)
        {
            throw new InvalidOperationException("The user context of a scope cannot change once set.");
        }

        UserId = userId;
    }

    /// <summary>Disables ownership filters for sign-in lookups and cleanup. Use sparingly.</summary>
    public IDisposable BeginSystemScope()
    {
        _systemDepth++;
        return new Scope(this);
    }

    private sealed class Scope(UserContext owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                owner._systemDepth--;
                _disposed = true;
            }
        }
    }
}
