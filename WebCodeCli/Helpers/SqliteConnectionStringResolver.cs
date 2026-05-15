using System.Data.SQLite;

namespace WebCodeCli.Helpers;

public static class SqliteConnectionStringResolver
{
    public static string Resolve(string connectionString, string applicationBasePath)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return connectionString;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(applicationBasePath);

        var builder = new SQLiteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource?.Trim();

        if (string.IsNullOrWhiteSpace(dataSource) || IsSpecialDataSource(dataSource))
        {
            return connectionString;
        }

        var resolvedDatabasePath = Path.IsPathRooted(dataSource)
            ? Path.GetFullPath(dataSource)
            : Path.GetFullPath(Path.Combine(applicationBasePath, dataSource));

        var parentDirectory = Path.GetDirectoryName(resolvedDatabasePath);
        if (!string.IsNullOrWhiteSpace(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        builder.DataSource = resolvedDatabasePath;
        return builder.ConnectionString;
    }

    private static bool IsSpecialDataSource(string dataSource)
    {
        return dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase)
            || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
    }
}
