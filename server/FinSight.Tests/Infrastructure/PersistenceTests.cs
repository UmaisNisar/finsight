using FinSight.Core.Domain;
using FinSight.Infrastructure.Persistence;
using FinSight.Infrastructure.Security;
using FinSight.Tests.TestHelpers;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace FinSight.Tests.Infrastructure;

public sealed class PersistenceTests
{
    [Fact]
    public async Task Migrations_create_a_schema_that_matches_the_model()
    {
        // MigrateAsync throws when the model has changes that no migration captures.
        await using var testDb = await TestDb.CreateAsync();

        var (db, _) = testDb.SystemContext();
        await using (db)
        {
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
            db.Database.HasPendingModelChanges().Should().BeFalse();
        }
    }

    [Fact]
    public async Task Queries_without_a_user_return_nothing()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        await testDb.AddStatementAsync(user.Id);

        var (db, _) = testDb.Context(userId: null);
        await using (db)
        {
            (await db.Users.CountAsync()).Should().Be(0);
            (await db.Statements.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public async Task Each_user_sees_only_their_own_rows()
    {
        await using var testDb = await TestDb.CreateAsync();
        var alice = await testDb.AddUserAsync("Alice");
        var bob = await testDb.AddUserAsync("Bob");
        var aliceStatement = await testDb.AddStatementAsync(alice.Id);
        await testDb.AddStatementAsync(bob.Id);

        var (db, _) = testDb.Context(bob.Id);
        await using (db)
        {
            (await db.Users.SingleAsync()).Id.Should().Be(bob.Id);
            (await db.Statements.AnyAsync(s => s.Id == aliceStatement.Id)).Should().BeFalse();
            (await db.Statements.CountAsync()).Should().Be(1);
        }
    }

    [Fact]
    public async Task Saving_another_users_row_is_refused()
    {
        await using var testDb = await TestDb.CreateAsync();
        var alice = await testDb.AddUserAsync("Alice");
        var bob = await testDb.AddUserAsync("Bob");

        var (db, _) = testDb.Context(bob.Id);
        await using (db)
        {
            db.Statements.Add(new Statement { UserId = alice.Id, SourceKey = "x", Filename = "x.pdf" });

            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<UnauthorizedAccessException>();
        }
    }

    [Fact]
    public async Task Writes_without_a_user_are_refused()
    {
        await using var testDb = await TestDb.CreateAsync();
        var alice = await testDb.AddUserAsync("Alice");

        var (db, _) = testDb.Context(userId: null);
        await using (db)
        {
            db.MerchantRules.Add(new MerchantRule { UserId = alice.Id, MerchantKey = "netflix", CategoryId = "entertainment.subscriptions" });

            var save = () => db.SaveChangesAsync();
            await save.Should().ThrowAsync<UnauthorizedAccessException>();
        }
    }

    [Fact]
    public void A_scope_cannot_switch_to_another_user()
    {
        var context = new UserContext();
        context.SetUser(Guid.NewGuid());

        var act = () => context.SetUser(Guid.NewGuid());

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void System_scopes_nest_and_end()
    {
        var context = new UserContext();

        using (context.BeginSystemScope())
        {
            using (context.BeginSystemScope())
            {
                context.IsSystem.Should().BeTrue();
            }

            context.IsSystem.Should().BeTrue();
        }

        context.IsSystem.Should().BeFalse();
    }

    [Fact]
    public async Task Descriptions_are_encrypted_at_rest_and_readable_through_the_model()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var statement = await testDb.AddStatementAsync(user.Id);

        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            db.Transactions.Add(TestDb.NewTransaction(statement, new DateOnly(2026, 8, 4), "PAYROLL DEPOSIT ACME CORP", 3100m, "acme"));
            await db.SaveChangesAsync();
        }

        await using (var command = testDb.Connection.CreateCommand())
        {
            command.CommandText = "SELECT Description FROM Transactions";
            var raw = (string)(await command.ExecuteScalarAsync())!;
            raw.Should().StartWith("enc:").And.NotContain("PAYROLL");
        }

        var (reader, _) = testDb.Context(user.Id);
        await using (reader)
        {
            (await reader.Transactions.SingleAsync()).Description.Should().Be("PAYROLL DEPOSIT ACME CORP");
        }
    }

    [Theory]
    [InlineData("12.34")]
    [InlineData("-1850.00")]
    [InlineData("0.01")]
    [InlineData("98765432.10")]
    public async Task Money_round_trips_exactly_as_cents(string amount)
    {
        var value = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var statement = await testDb.AddStatementAsync(user.Id);

        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            db.Transactions.Add(TestDb.NewTransaction(statement, new DateOnly(2026, 8, 4), "X", value, "x"));
            await db.SaveChangesAsync();
        }

        await using (var command = testDb.Connection.CreateCommand())
        {
            command.CommandText = "SELECT Amount FROM Transactions";
            (await command.ExecuteScalarAsync()).Should().Be((long)(value * 100));
        }

        var (reader, _) = testDb.Context(user.Id);
        await using (reader)
        {
            (await reader.Transactions.SingleAsync()).Amount.Should().Be(value);
        }
    }

    [Fact]
    public async Task Money_filters_and_sorts_correctly_in_the_database()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var statement = await testDb.AddStatementAsync(user.Id);

        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            foreach (var amount in new[] { -9.99m, -100m, -20.5m, 1000m })
            {
                db.Transactions.Add(TestDb.NewTransaction(statement, new DateOnly(2026, 8, 4), "X", amount, "x"));
            }

            await db.SaveChangesAsync();
            (await db.Transactions.Where(t => t.Amount < -10m).OrderBy(t => t.Amount).Select(t => t.Amount).ToListAsync())
                .Should().Equal(-100m, -20.5m);
        }
    }

    [Fact]
    public async Task Deleting_a_statement_deletes_its_transactions()
    {
        await using var testDb = await TestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var statement = await testDb.AddStatementAsync(user.Id);

        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            db.Transactions.Add(TestDb.NewTransaction(statement, new DateOnly(2026, 8, 4), "X", -5m, "x"));
            await db.SaveChangesAsync();

            await db.Statements.Where(s => s.Id == statement.Id).ExecuteDeleteAsync();

            (await db.Transactions.CountAsync()).Should().Be(0);
        }
    }

    [Fact]
    public void Field_protector_round_trips_and_marks_ciphertext()
    {
        var protector = new DataProtectionFieldProtector(new EphemeralDataProtectionProvider());

        var cipher = protector.Protect("NETFLIX.COM");

        cipher.Should().StartWith("enc:").And.NotContain("NETFLIX");
        protector.Unprotect(cipher).Should().Be("NETFLIX.COM");
    }

    [Fact]
    public void Field_protector_passes_through_legacy_plaintext_and_hides_undecryptable_values()
    {
        var protector = new DataProtectionFieldProtector(new EphemeralDataProtectionProvider());
        var otherKeys = new DataProtectionFieldProtector(new EphemeralDataProtectionProvider());

        protector.Unprotect("PLAIN TEXT").Should().Be("PLAIN TEXT");
        protector.Unprotect(otherKeys.Protect("SECRET")).Should().Be("[unavailable]");
    }

    [Fact]
    public void Tokens_and_fields_use_separate_keys()
    {
        var protector = new DataProtectionFieldProtector(new EphemeralDataProtectionProvider());
        ITokenProtector tokens = protector;

        var token = tokens.Protect("refresh-token");
        var field = protector.Protect("refresh-token");

        tokens.TryUnprotect(token).Should().Be("refresh-token");
        tokens.TryUnprotect(field["enc:".Length..]).Should().BeNull();
        protector.Unprotect("enc:" + token).Should().Be("[unavailable]");
        tokens.TryUnprotect("garbage").Should().BeNull();
    }

    [Fact]
    public void Relative_sqlite_paths_resolve_under_the_content_root()
    {
        var root = Path.Combine(Path.GetTempPath(), "finsight-paths", Guid.NewGuid().ToString("N"));
        try
        {
            var resolved = FinSight.Api.Hosting.SqlitePaths.Resolve("Data Source=.data/app.db", root);

            new SqliteConnectionStringBuilder(resolved).DataSource.Should().Be(Path.Combine(root, ".data", "app.db"));
            Directory.Exists(Path.Combine(root, ".data")).Should().BeTrue();
            FinSight.Api.Hosting.SqlitePaths.Resolve("Data Source=:memory:", root).Should().Be("Data Source=:memory:");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
