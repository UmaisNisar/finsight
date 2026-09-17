using FinSight.Core.Domain;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace FinSight.Tests.TestHelpers;

/// <summary>
/// The PostgreSQL counterpart of <see cref="TestDb"/>: a new database with the PostgreSQL migrations applied through
/// <see cref="DatabaseSetup.MigrateAsync"/>, the real encryption converter and the real ownership filters.
/// </summary>
internal sealed class PostgresTestDb : IAsyncDisposable
{
    private readonly PostgresTestDatabase _database;

    private PostgresTestDb(PostgresTestDatabase database)
    {
        _database = database;
    }

    public DataProtectionFieldProtector Protector { get; } = new(new EphemeralDataProtectionProvider());

    public string ConnectionString => _database.ConnectionString;

    public static async Task<PostgresTestDb> CreateAsync(bool migrate = true)
    {
        var db = new PostgresTestDb(PostgresTestDatabase.Create());
        if (migrate)
        {
            var (context, _) = db.SystemContext();
            await using (context)
            {
                await DatabaseSetup.MigrateAsync(context);
            }
        }

        return db;
    }

    /// <summary>A unit of work acting for <paramref name="userId"/>, or for nobody when null.</summary>
    public (FinSightDbContext Db, UserContext User) Context(Guid? userId)
    {
        var user = new UserContext();
        if (userId is not null)
        {
            user.SetUser(userId.Value);
        }

        var options = DatabaseSetup.Configure(new DbContextOptionsBuilder<FinSightDbContext>(), DatabaseProvider.Postgres, ConnectionString);
        return (new FinSightDbContext((DbContextOptions<FinSightDbContext>)options.Options, user, Protector), user);
    }

    public (FinSightDbContext Db, UserContext User) SystemContext()
    {
        var (db, user) = Context(null);
        user.BeginSystemScope();
        return (db, user);
    }

    public async Task<User> AddUserAsync(string name = "Sam", string? googleSubject = null)
    {
        var user = new User { Email = $"{name.ToLowerInvariant()}@example.com", DisplayName = name, GoogleSubject = googleSubject, CreatedAt = DateTimeOffset.UtcNow };
        var (db, _) = SystemContext();
        await using (db)
        {
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        return user;
    }

    public async Task<Statement> AddStatementAsync(Guid userId)
    {
        var now = DateTimeOffset.UtcNow;
        var statement = new Statement
        {
            UserId = userId,
            Source = StatementSourceKind.ManualUpload,
            SourceKey = $"test:{Guid.NewGuid():N}",
            Filename = "statement.pdf",
            Institution = "Maple Bank",
            AccountType = AccountType.Chequing,
            AccountMask = "4821",
            Status = StatementStatus.Processed,
            CreatedAt = now,
            UpdatedAt = now,
        };

        var (db, _) = Context(userId);
        await using (db)
        {
            db.Statements.Add(statement);
            await db.SaveChangesAsync();
        }

        return statement;
    }

    public ValueTask DisposeAsync()
    {
        _database.Dispose();
        return ValueTask.CompletedTask;
    }
}
