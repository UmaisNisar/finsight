using FinSight.Core.Domain;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FinSight.Tests.TestHelpers;

/// <summary>
/// An in-memory SQLite database with the real migrations applied, the real encryption converter and the real
/// ownership filters. Each <see cref="Context"/> call is a separate unit of work, like a request or a job.
/// </summary>
internal sealed class TestDb : IAsyncDisposable
{
    private readonly SqliteConnection _connection;

    private TestDb(SqliteConnection connection, DataProtectionFieldProtector protector)
    {
        _connection = connection;
        Protector = protector;
    }

    public DataProtectionFieldProtector Protector { get; }

    public SqliteConnection Connection => _connection;

    public static async Task<TestDb> CreateAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var db = new TestDb(connection, new DataProtectionFieldProtector(new EphemeralDataProtectionProvider()));

        var (context, _) = db.SystemContext();
        await using (context)
        {
            await context.Database.MigrateAsync();
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

        var options = new DbContextOptionsBuilder<FinSightDbContext>().UseSqlite(_connection).Options;
        return (new FinSightDbContext(options, user, Protector), user);
    }

    /// <summary>A unit of work with ownership filters disabled, for arranging test data across users.</summary>
    public (FinSightDbContext Db, UserContext User) SystemContext()
    {
        var (db, user) = Context(null);
        user.BeginSystemScope();
        return (db, user);
    }

    public async Task<User> AddUserAsync(string name = "Sam", bool isDemo = false, DateTimeOffset? createdAt = null)
    {
        var user = new User { Email = $"{name.ToLowerInvariant()}@example.com", DisplayName = name, IsDemo = isDemo, CreatedAt = createdAt ?? DateTimeOffset.UtcNow };
        var (db, _) = SystemContext();
        await using (db)
        {
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        return user;
    }

    public async Task<Statement> AddStatementAsync(Guid userId, string institution = "Maple Bank", AccountType accountType = AccountType.Chequing, string mask = "4821",
        StatementSourceKind source = StatementSourceKind.ManualUpload)
    {
        var now = DateTimeOffset.UtcNow;
        var statement = new Statement
        {
            UserId = userId,
            Source = source,
            SourceKey = $"test:{Guid.NewGuid():N}",
            Filename = "statement.pdf",
            Institution = institution,
            AccountType = accountType,
            AccountMask = mask,
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

    public static Transaction NewTransaction(Statement statement, DateOnly date, string description, decimal amount, string merchantKey, string categoryId = "other.uncategorized",
        TransactionType type = TransactionType.Expense, CategorySource source = CategorySource.Default, double confidence = 0.2) => new()
    {
        UserId = statement.UserId,
        StatementId = statement.Id,
        Fingerprint = Guid.NewGuid().ToString("N"),
        Date = date,
        Description = description,
        Amount = amount,
        Currency = "CAD",
        Merchant = merchantKey,
        MerchantKey = merchantKey,
        CategoryId = categoryId,
        Type = type,
        CategorySource = source,
        CategoryConfidence = confidence,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
