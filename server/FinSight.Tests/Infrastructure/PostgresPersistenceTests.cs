using FinSight.Core.Domain;
using FinSight.Infrastructure.Persistence;
using FinSight.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FinSight.Tests.Infrastructure;

/// <summary>
/// The persistence guarantees of <see cref="PersistenceTests"/>, checked on PostgreSQL: migrations, value converters, ownership
/// filters and bulk deletes. Skipped unless FINSIGHT_TEST_POSTGRES is set (CI runs them against a postgres service container).
/// </summary>
[Trait("Category", "Postgres")]
public sealed class PostgresPersistenceTests
{
    [PostgresFact]
    public async Task Migrations_create_a_schema_that_matches_the_model()
    {
        await using var testDb = await PostgresTestDb.CreateAsync();

        var (db, _) = testDb.SystemContext();
        await using (db)
        {
            db.Database.IsNpgsql().Should().BeTrue();
            db.Database.GetMigrations().Should().NotBeEmpty("the PostgreSQL migrations assembly must contain the initial migration");
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
            db.Database.HasPendingModelChanges().Should().BeFalse("a model change needs a PostgreSQL migration as well as a SQLite one");
        }
    }

    [PostgresFact]
    public async Task Instances_starting_together_migrate_one_at_a_time()
    {
        await using var testDb = await PostgresTestDb.CreateAsync(migrate: false);

        var contexts = Enumerable.Range(0, 4).Select(_ => testDb.SystemContext().Db).ToList();
        try
        {
            await Task.WhenAll(contexts.Select(c => DatabaseSetup.MigrateAsync(c)));
        }
        finally
        {
            foreach (var context in contexts)
            {
                await context.DisposeAsync();
            }
        }

        var (db, _) = testDb.SystemContext();
        await using (db)
        {
            (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
            (await db.Database.GetAppliedMigrationsAsync()).Should().OnlyHaveUniqueItems();
        }
    }

    [PostgresFact]
    public async Task Queries_without_a_user_return_nothing_and_each_user_sees_only_their_own_rows()
    {
        await using var testDb = await PostgresTestDb.CreateAsync();
        var alice = await testDb.AddUserAsync("Alice");
        var bob = await testDb.AddUserAsync("Bob");
        var aliceStatement = await testDb.AddStatementAsync(alice.Id);
        await testDb.AddStatementAsync(bob.Id);

        var (nobody, _) = testDb.Context(userId: null);
        await using (nobody)
        {
            (await nobody.Users.CountAsync()).Should().Be(0);
            (await nobody.Statements.CountAsync()).Should().Be(0);
        }

        var (db, _) = testDb.Context(bob.Id);
        await using (db)
        {
            (await db.Users.SingleAsync()).Id.Should().Be(bob.Id);
            (await db.Statements.AnyAsync(s => s.Id == aliceStatement.Id)).Should().BeFalse();
            (await db.Statements.CountAsync()).Should().Be(1);
        }
    }

    [PostgresFact]
    public async Task Saving_another_users_row_is_refused()
    {
        await using var testDb = await PostgresTestDb.CreateAsync();
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

    [PostgresFact]
    public async Task Bulk_deletes_only_touch_the_current_users_rows()
    {
        await using var testDb = await PostgresTestDb.CreateAsync();
        var alice = await testDb.AddUserAsync("Alice");
        var bob = await testDb.AddUserAsync("Bob");
        var aliceStatement = await testDb.AddStatementAsync(alice.Id);
        var bobStatement = await testDb.AddStatementAsync(bob.Id);
        await AddTransactionAsync(testDb, aliceStatement, -5m);
        await AddTransactionAsync(testDb, bobStatement, -7m);

        var (db, _) = testDb.Context(bob.Id);
        await using (db)
        {
            (await db.Transactions.ExecuteDeleteAsync()).Should().Be(1);
            (await db.Statements.ExecuteDeleteAsync()).Should().Be(1);
        }

        var (system, _) = testDb.SystemContext();
        await using (system)
        {
            (await system.Statements.Select(s => s.UserId).ToListAsync()).Should().Equal(alice.Id);
            (await system.Transactions.Select(t => t.UserId).ToListAsync()).Should().Equal(alice.Id);
        }
    }

    [PostgresFact]
    public async Task Deleting_a_statement_deletes_its_transactions_and_deleting_a_user_deletes_everything()
    {
        await using var testDb = await PostgresTestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var first = await testDb.AddStatementAsync(user.Id);
        var second = await testDb.AddStatementAsync(user.Id);
        await AddTransactionAsync(testDb, first, -5m);
        await AddTransactionAsync(testDb, second, -6m);

        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            await db.Statements.Where(s => s.Id == first.Id).ExecuteDeleteAsync();
            (await db.Transactions.CountAsync()).Should().Be(1);

            // The same order AccountController uses when an account is deleted.
            await db.Transactions.ExecuteDeleteAsync();
            await db.Statements.ExecuteDeleteAsync();
            await db.Users.ExecuteDeleteAsync();
        }

        var (system, _) = testDb.SystemContext();
        await using (system)
        {
            (await system.Users.CountAsync()).Should().Be(0);
            (await system.Statements.CountAsync()).Should().Be(0);
        }
    }

    [PostgresFact]
    public async Task Descriptions_are_encrypted_at_rest_and_readable_through_the_model()
    {
        await using var testDb = await PostgresTestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var statement = await testDb.AddStatementAsync(user.Id);
        await AddTransactionAsync(testDb, statement, 3100m, "PAYROLL DEPOSIT ACME CORP");

        var raw = await ScalarAsync(testDb, "SELECT \"Description\" FROM \"Transactions\"");
        ((string)raw!).Should().StartWith("enc:").And.NotContain("PAYROLL");

        var (reader, _) = testDb.Context(user.Id);
        await using (reader)
        {
            (await reader.Transactions.SingleAsync()).Description.Should().Be("PAYROLL DEPOSIT ACME CORP");
        }
    }

    [PostgresTheory]
    [InlineData("12.34")]
    [InlineData("-1850.00")]
    [InlineData("0.01")]
    [InlineData("98765432.10")]
    public async Task Money_round_trips_exactly_as_integer_cents(string amount)
    {
        var value = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);
        await using var testDb = await PostgresTestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var statement = await testDb.AddStatementAsync(user.Id);
        await AddTransactionAsync(testDb, statement, value);

        (await ScalarAsync(testDb, "SELECT \"Amount\" FROM \"Transactions\"")).Should().Be((long)(value * 100));

        var (reader, _) = testDb.Context(user.Id);
        await using (reader)
        {
            (await reader.Transactions.SingleAsync()).Amount.Should().Be(value);
        }
    }

    [PostgresFact]
    public async Task Money_and_timestamps_filter_sort_and_total_in_the_database()
    {
        await using var testDb = await PostgresTestDb.CreateAsync();
        var user = await testDb.AddUserAsync();
        var statement = await testDb.AddStatementAsync(user.Id);
        var start = new DateTimeOffset(2026, 8, 4, 9, 30, 0, TimeSpan.FromHours(-4));
        var amounts = new[] { -9.99m, -100m, -20.5m, 1000m };

        var (db, _) = testDb.Context(user.Id);
        await using (db)
        {
            for (var i = 0; i < amounts.Length; i++)
            {
                var transaction = TestDb.NewTransaction(statement, new DateOnly(2026, 8, 4).AddDays(i), "X", amounts[i], "x");
                transaction.CreatedAt = start.AddHours(i);
                db.Transactions.Add(transaction);
            }

            await db.SaveChangesAsync();
        }

        var (reader, _) = testDb.Context(user.Id);
        await using (reader)
        {
            (await reader.Transactions.Where(t => t.Amount < -10m).OrderBy(t => t.Amount).Select(t => t.Amount).ToListAsync())
                .Should().Equal(-100m, -20.5m);
            (await reader.Transactions.SumAsync(t => t.Amount)).Should().Be(amounts.Sum());
            (await reader.Transactions.Where(t => t.CreatedAt > start.AddMinutes(90)).OrderBy(t => t.CreatedAt).Select(t => t.CreatedAt).ToListAsync())
                .Should().Equal(start.AddHours(2), start.AddHours(3));
            (await reader.Transactions.Where(t => t.Date >= new DateOnly(2026, 8, 6)).CountAsync()).Should().Be(2);
        }
    }

    [PostgresFact]
    public async Task Google_accounts_are_unique_while_demo_users_have_no_google_subject()
    {
        await using var testDb = await PostgresTestDb.CreateAsync();
        await testDb.AddUserAsync("Demo1");
        await testDb.AddUserAsync("Demo2");
        await testDb.AddUserAsync("Sam", googleSubject: "google-123");

        var duplicate = () => testDb.AddUserAsync("Imposter", googleSubject: "google-123");

        (await duplicate.Should().ThrowAsync<DbUpdateException>()).WithInnerException<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    private static async Task AddTransactionAsync(PostgresTestDb testDb, Statement statement, decimal amount, string description = "X")
    {
        var (db, _) = testDb.Context(statement.UserId);
        await using (db)
        {
            db.Transactions.Add(TestDb.NewTransaction(statement, new DateOnly(2026, 8, 4), description, amount, "x"));
            await db.SaveChangesAsync();
        }
    }

    private static async Task<object?> ScalarAsync(PostgresTestDb testDb, string sql)
    {
        await using var connection = new NpgsqlConnection(testDb.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
