using FinSight.Core.Domain;
using FinSight.Infrastructure.Gemini;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Security;
using FinSight.Tests.TestHelpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Options;

namespace FinSight.Tests.Infrastructure;

public sealed class GeminiKeyResolverTests
{
    private const string ServerKey = "server-key-0123456789abcdef";

    [Fact]
    public async Task A_users_own_key_wins_over_the_server_key()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await AddUserWithKeyAsync(testDb, "user-key-0123456789abcdef");

        (await ResolveAsync(testDb, user.Id, ServerKey)).Should().Be("user-key-0123456789abcdef");
    }

    [Fact]
    public async Task Users_without_a_key_fall_back_to_the_server_key_or_to_nothing()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        await AddUserWithKeyAsync(testDb, "someone-elses-key-0123456789", name: "Alex");

        (await ResolveAsync(testDb, user.Id, ServerKey)).Should().Be(ServerKey);
        (await ResolveAsync(testDb, user.Id, serverKey: null)).Should().BeNull("another user's key is never borrowed");
        (await ResolveAsync(testDb, userId: null, ServerKey)).Should().Be(ServerKey, "signed out, only the server key counts");
    }

    [Fact]
    public async Task A_key_that_can_no_longer_be_decrypted_falls_back_to_the_server_key()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            var row = await db.Users.SingleAsync();
            row.EncryptedGeminiApiKey = "CfDJ8-not-a-valid-payload";
            await db.SaveChangesAsync();
        }

        (await ResolveAsync(testDb, user.Id, ServerKey)).Should().Be(ServerKey);
    }

    [Fact]
    public async Task The_key_is_cached_for_the_scope_only()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await AddUserWithKeyAsync(testDb, "first-key-0123456789abcdef");
        var (db, userContext) = testDb.Context(user.Id);
        await using (db)
        {
            var resolver = new GeminiKeyResolver(db, userContext, testDb.Protector, Options.Create(new GeminiOptions()));
            (await resolver.ResolveAsync(CancellationToken.None)).Should().Be("first-key-0123456789abcdef");

            await SetKeyAsync(testDb, user.Id, "second-key-0123456789abcdef");

            (await resolver.ResolveAsync(CancellationToken.None)).Should().Be("first-key-0123456789abcdef");
        }

        (await ResolveAsync(testDb, user.Id, serverKey: null)).Should().Be("second-key-0123456789abcdef", "a new scope reads the key again");
    }

    [Fact]
    public async Task Service_is_configured_per_user()
    {
        await using var testDb = await TestDb.CreateAsync();
        var withKey = await AddUserWithKeyAsync(testDb, "user-key-0123456789abcdef");
        var withoutKey = await testDb.AddUserAsync("Alex");

        (await IsConfiguredAsync(testDb, withKey.Id)).Should().BeTrue();
        (await IsConfiguredAsync(testDb, withoutKey.Id)).Should().BeFalse();
    }

    private static async Task<bool> IsConfiguredAsync(TestDb testDb, Guid userId)
    {
        var (db, userContext) = testDb.Context(userId);
        await using (db)
        {
            var resolver = new GeminiKeyResolver(db, userContext, testDb.Protector, Options.Create(new GeminiOptions()));
            var client = new GeminiClient(new HttpClient(new StubHttpHandler()), resolver, Options.Create(new GeminiOptions()),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<GeminiClient>.Instance);
            return await new GeminiService(client, resolver).IsConfiguredAsync(CancellationToken.None);
        }
    }

    private static async Task<string?> ResolveAsync(TestDb testDb, Guid? userId, string? serverKey)
    {
        var (db, userContext) = testDb.Context(userId);
        await using (db)
        {
            return await new GeminiKeyResolver(db, userContext, testDb.Protector, Options.Create(new GeminiOptions { ApiKey = serverKey }))
                .ResolveAsync(CancellationToken.None);
        }
    }

    private static async Task<User> AddUserWithKeyAsync(TestDb testDb, string key, string name = "Sam")
    {
        var user = await testDb.AddUserAsync(name);
        await SetKeyAsync(testDb, user.Id, key);
        return user;
    }

    private static async Task SetKeyAsync(TestDb testDb, Guid userId, string key)
    {
        var (db, _) = testDb.Context(userId);
        await using (db)
        {
            var row = await db.Users.SingleAsync();
            row.EncryptedGeminiApiKey = ((IApiKeyProtector)testDb.Protector).Protect(key);
            row.GeminiApiKeyHint = key[^4..];
            await db.SaveChangesAsync();
        }
    }
}

public sealed class OnboardingMigrationTests
{
    [Fact]
    public async Task Existing_users_with_statements_and_demo_users_are_marked_as_onboarded()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<FinSightDbContext>().UseSqlite(connection).Options;
        var userContext = new UserContext();
        userContext.BeginSystemScope();
        await using var db = new FinSightDbContext(options, userContext, new DataProtectionFieldProtector(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider()));
        var migrator = db.GetService<IMigrator>();
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);

        await migrator.MigrateAsync("20260916221600_Initial");
        await ExecuteAsync(connection, """
            INSERT INTO Users (Id, Email, DisplayName, IsDemo, CreatedAt, Settings_AiCategorizationEnabled, Settings_AiInsightsEnabled,
                Settings_Currency, Settings_DateFormat, Settings_NotificationsEnabled, Settings_Theme)
            VALUES ('11111111-1111-1111-1111-111111111111', 'a@example.com', 'Has statements', 0, 0, 1, 1, 'CAD', 'MMM d, yyyy', 0, 'System'),
                   ('22222222-2222-2222-2222-222222222222', 'b@example.com', 'New', 0, 0, 1, 1, 'CAD', 'MMM d, yyyy', 0, 'System'),
                   ('33333333-3333-3333-3333-333333333333', 'demo@finsight.local', 'Demo', 1, 0, 1, 1, 'CAD', 'MMM d, yyyy', 0, 'System');
            INSERT INTO Statements (Id, UserId, Source, SourceKey, Filename, DocumentKind, DetectionConfidence, AccountType, Status, TransactionCount, CreatedAt, UpdatedAt)
            VALUES ('44444444-4444-4444-4444-444444444444', '11111111-1111-1111-1111-111111111111', 'Gmail', 'gmail:m1:1', 'statement.pdf', 'BankStatement', 0.9, 'Chequing', 'Discovered', 0, 0, 0);
            """);

        await migrator.MigrateAsync();

        var converter = new DateTimeOffsetToBinaryConverter();
        var completed = await ReadCompletedAtAsync(connection, "11111111-1111-1111-1111-111111111111");
        completed.Should().NotBeNull();
        ((DateTimeOffset)converter.ConvertFromProvider(completed!.Value)!).Should().BeOnOrAfter(before).And.BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(1));
        (await ReadCompletedAtAsync(connection, "33333333-3333-3333-3333-333333333333")).Should().NotBeNull();
        (await ReadCompletedAtAsync(connection, "22222222-2222-2222-2222-222222222222")).Should().BeNull("a user with nothing imported still needs onboarding");
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long?> ReadCompletedAtAsync(SqliteConnection connection, string userId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT OnboardingCompletedAt FROM Users WHERE Id = $id";
        command.Parameters.AddWithValue("$id", userId);
        return await command.ExecuteScalarAsync() is long value ? value : null;
    }
}
