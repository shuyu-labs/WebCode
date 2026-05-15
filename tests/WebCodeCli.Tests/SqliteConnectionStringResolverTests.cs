using System.Data.SQLite;
using System.Reflection;
using WebCodeCli.Helpers;
using Xunit;

namespace WebCodeCli.Tests;

public sealed class SqliteConnectionStringResolverTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), "SqliteConnectionStringResolverTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Resolve_RebasesRelativeDataSourceUnderApplicationBaseDirectory_AndCreatesParentDirectory()
    {
        var applicationBaseDirectory = CreateDirectory("publish");
        var connectionString = "Data Source=data/WebCodeCli.db";

        var resolved = InvokeResolve(connectionString, applicationBaseDirectory);
        var builder = new SQLiteConnectionStringBuilder(resolved);
        var expectedDatabasePath = Path.GetFullPath(Path.Combine(applicationBaseDirectory, "data", "WebCodeCli.db"));

        Assert.Equal(expectedDatabasePath, Path.GetFullPath(builder.DataSource));
        Assert.True(Directory.Exists(Path.Combine(applicationBaseDirectory, "data")));
    }

    [Fact]
    public void Resolve_LeavesInMemoryConnectionStringUntouched()
    {
        const string connectionString = "Data Source=:memory:";

        var resolved = InvokeResolve(connectionString, CreateDirectory("publish"));

        Assert.Equal(connectionString, resolved);
    }

    [Fact]
    public void Resolve_ReturnsConnectionStringThatAllowsSQLiteToCreateTheDatabaseFile()
    {
        var applicationBaseDirectory = CreateDirectory("publish");
        var resolved = InvokeResolve("Data Source=data/WebCodeCli.db", applicationBaseDirectory);

        using (var connection = new SQLiteConnection(resolved))
        {
            connection.Open();
        }

        Assert.True(File.Exists(Path.Combine(applicationBaseDirectory, "data", "WebCodeCli.db")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private string CreateDirectory(params string[] segments)
    {
        var path = Path.Combine(new[] { _testRoot }.Concat(segments).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private static string InvokeResolve(string connectionString, string applicationBaseDirectory)
    {
        var resolverType = typeof(WebRootPathResolver).Assembly.GetType("WebCodeCli.Helpers.SqliteConnectionStringResolver");
        Assert.NotNull(resolverType);

        var resolveMethod = resolverType!.GetMethod(
            "Resolve",
            BindingFlags.Public | BindingFlags.Static,
            binder: null,
            types: new[] { typeof(string), typeof(string) },
            modifiers: null);

        Assert.NotNull(resolveMethod);

        var resolved = resolveMethod!.Invoke(null, new object[] { connectionString, applicationBaseDirectory });

        return Assert.IsType<string>(resolved);
    }
}
