using Microsoft.Data.Sqlite;

namespace FinSight.Api.Hosting;

public static class SqlitePaths
{
    /// <summary>
    /// Makes a relative SQLite data source absolute under <paramref name="contentRoot"/> and creates its directory.
    /// In-memory and absolute paths are returned unchanged.
    /// </summary>
    public static string Resolve(string connectionString, string contentRoot)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var source = builder.DataSource;
        if (string.IsNullOrWhiteSpace(source) || source == ":memory:" || builder.Mode == SqliteOpenMode.Memory)
        {
            return connectionString;
        }

        var path = Path.IsPathRooted(source) ? source : Path.GetFullPath(source, contentRoot);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        builder.DataSource = path;
        return builder.ToString();
    }
}
