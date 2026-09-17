using Npgsql;

namespace FinSight.Tests.TestHelpers;

/// <summary>
/// A throwaway PostgreSQL database for one test class, created on the server named by <c>FINSIGHT_TEST_POSTGRES</c> and dropped
/// afterwards. Without that variable, Postgres tests are skipped, so the default suite needs nothing but SQLite.
/// </summary>
/// <remarks>
/// <c>FINSIGHT_TEST_POSTGRES</c> is a connection string for a role that may create databases, for example
/// <c>Host=localhost;Port=5432;Username=postgres;Password=postgres</c>. Setting <c>FINSIGHT_TEST_PROVIDER=Postgres</c> as well
/// runs every API test (anything built on <see cref="FinSightApiFactory"/>) against PostgreSQL instead of SQLite.
/// </remarks>
public sealed class PostgresTestDatabase : IDisposable
{
    public const string ConnectionVariable = "FINSIGHT_TEST_POSTGRES";
    public const string ProviderVariable = "FINSIGHT_TEST_PROVIDER";

    private readonly string _name;
    private bool _disposed;

    private PostgresTestDatabase(string name, string connectionString)
    {
        _name = name;
        ConnectionString = connectionString;
    }

    public static string? ServerConnectionString =>
        Environment.GetEnvironmentVariable(ConnectionVariable) is { Length: > 0 } value ? value : null;

    public static bool IsAvailable => ServerConnectionString is not null;

    /// <summary>True when API tests should run against PostgreSQL rather than SQLite.</summary>
    public static bool IsDefaultProvider =>
        IsAvailable && string.Equals(Environment.GetEnvironmentVariable(ProviderVariable), "Postgres", StringComparison.OrdinalIgnoreCase);

    public string ConnectionString { get; }

    public static PostgresTestDatabase Create()
    {
        var server = ServerConnectionString ?? throw new InvalidOperationException($"Set {ConnectionVariable} to run PostgreSQL tests.");
        var name = $"finsight_test_{Guid.NewGuid():N}";

        using (var connection = new NpgsqlConnection(server))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{name}\"";
            command.ExecuteNonQuery();
        }

        var builder = new NpgsqlConnectionStringBuilder(server) { Database = name };
        return new PostgresTestDatabase(name, builder.ConnectionString);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            NpgsqlConnection.ClearAllPools();
            using var connection = new NpgsqlConnection(ServerConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)";
            command.ExecuteNonQuery();
        }
        catch (NpgsqlException)
        {
            // Best effort cleanup; a CI service container is discarded anyway.
        }
    }
}

/// <summary>A fact that runs only when <c>FINSIGHT_TEST_POSTGRES</c> points at a PostgreSQL server.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (!PostgresTestDatabase.IsAvailable)
        {
            Skip = $"Set {PostgresTestDatabase.ConnectionVariable} to a PostgreSQL connection string to run this test.";
        }
    }
}

/// <summary>A theory that runs only when <c>FINSIGHT_TEST_POSTGRES</c> points at a PostgreSQL server.</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (!PostgresTestDatabase.IsAvailable)
        {
            Skip = $"Set {PostgresTestDatabase.ConnectionVariable} to a PostgreSQL connection string to run this test.";
        }
    }
}
