using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FinSight.Infrastructure.Persistence;

public enum DatabaseProvider
{
    /// <summary>A single database file. The default, used for local development and the test suite.</summary>
    Sqlite,

    /// <summary>PostgreSQL, for container and hosted deployments. Migrations come from FinSight.Migrations.Postgres.</summary>
    Postgres,
}

public sealed class DatabaseOptions
{
    public const string Section = "Database";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    /// <summary>Applies pending migrations when the app starts. Turn off to migrate as a separate release step.</summary>
    public bool MigrateOnStartup { get; set; } = true;

    /// <summary>Applies pending migrations and exits without serving requests, for a release or init step.</summary>
    public bool MigrateOnly { get; set; }
}

public static class DatabaseSetup
{
    public const string DefaultSqliteConnectionString = "Data Source=.data/finsight.db";

    /// <summary>EF migrations are provider-specific: SQLite's live with the DbContext, PostgreSQL's in their own assembly.</summary>
    public const string PostgresMigrationsAssembly = "FinSight.Migrations.Postgres";

    /// <summary>Any fixed 64-bit number; every instance of FinSight must use the same one.</summary>
    private const long MigrationLockKey = 0x46696E5369676874; // "FinSight"

    public static DatabaseOptions GetDatabaseOptions(this IConfiguration configuration) =>
        configuration.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>() ?? new DatabaseOptions();

    public static IServiceCollection AddFinSightDatabase(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.Section));

        var provider = configuration.GetDatabaseOptions().Provider;
        var connectionString = configuration.GetConnectionString("FinSight");
        services.AddDbContext<FinSightDbContext>(options => Configure(options, provider, connectionString));
        return services;
    }

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, DatabaseProvider provider, string? connectionString)
    {
        switch (provider)
        {
            case DatabaseProvider.Sqlite:
                return options.UseSqlite(string.IsNullOrWhiteSpace(connectionString) ? DefaultSqliteConnectionString : connectionString);

            case DatabaseProvider.Postgres:
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new InvalidOperationException(
                        "Database:Provider is Postgres but ConnectionStrings:FinSight is empty. Set it, for example " +
                        "ConnectionStrings__FinSight=\"Host=postgres;Database=finsight;Username=finsight;Password=...\".");
                }

                // No retrying execution strategy: the app uses explicit transactions, which it doesn't support.
                return options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsAssembly(PostgresMigrationsAssembly));

            default:
                throw new InvalidOperationException($"Unsupported Database:Provider '{provider}'. Use Sqlite or Postgres.");
        }
    }

    /// <summary>
    /// Applies pending migrations. On PostgreSQL a session advisory lock is held for the duration, so instances that start
    /// at the same time migrate one after another: the first applies the migrations and the rest find nothing to do.
    /// (SQLite is a single local file, so only one host can use it anyway.)
    /// </summary>
    public static async Task MigrateAsync(FinSightDbContext db, CancellationToken cancellationToken = default)
    {
        if (!db.Database.IsNpgsql())
        {
            await db.Database.MigrateAsync(cancellationToken);
            return;
        }

        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await ExecuteAsync(connection, "SELECT pg_advisory_lock(@key)", cancellationToken);
            try
            {
                await db.Database.MigrateAsync(cancellationToken);
            }
            finally
            {
                // Closing the connection would release the lock too; unlocking explicitly keeps pooled connections clean.
                // A failure here (the connection dropped) must not hide the migration's own exception.
                try
                {
                    await ExecuteAsync(connection, "SELECT pg_advisory_unlock(@key)", CancellationToken.None);
                }
                catch (DbException)
                {
                }
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var key = command.CreateParameter();
        key.ParameterName = "key";
        key.DbType = DbType.Int64;
        key.Value = MigrationLockKey;
        command.Parameters.Add(key);
        await command.ExecuteScalarAsync(cancellationToken);
    }
}
