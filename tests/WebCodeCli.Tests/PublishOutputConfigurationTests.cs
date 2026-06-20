using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace WebCodeCli.Tests;

public sealed class PublishOutputConfigurationTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), "PublishOutputConfigurationTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void PublishOutput_DoesNotIncludeDevelopmentSettingsFile()
    {
        var repoRoot = ResolveRepoRoot();
        var projectPath = Path.Combine(repoRoot, "WebCodeCli", "WebCodeCli.csproj");
        var publishDirectory = Path.Combine(_testRoot, "publish");

        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("publish");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(publishDirectory);
        startInfo.ArgumentList.Add("--no-restore");
        startInfo.ArgumentList.Add("/p:UseSharedCompilation=false");
        startInfo.ArgumentList.Add("/p:RunAnalyzers=false");

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        process!.WaitForExit();

        var standardOutput = process.StandardOutput.ReadToEnd();
        var standardError = process.StandardError.ReadToEnd();

        Assert.True(
            process.ExitCode == 0,
            $"dotnet publish failed with exit code {process.ExitCode}.{Environment.NewLine}STDOUT:{Environment.NewLine}{standardOutput}{Environment.NewLine}STDERR:{Environment.NewLine}{standardError}");

        Assert.False(File.Exists(Path.Combine(publishDirectory, "appsettings.Development.json")));

        var appsettingsPath = Path.Combine(publishDirectory, "appsettings.json");
        Assert.True(File.Exists(appsettingsPath));

        using var document = JsonDocument.Parse(File.ReadAllText(appsettingsPath));
        Assert.True(document.RootElement.TryGetProperty("DBConnection", out var dbConnection));
        Assert.Equal("Sqlite", dbConnection.GetProperty("DbType").GetString());
        Assert.Equal("Data Source=WebCodeCli.db", dbConnection.GetProperty("ConnectionStrings").GetString());

        Assert.False(document.RootElement.TryGetProperty("FeishuReplyTts", out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private static string ResolveRepoRoot()
    {
        var currentDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        while (currentDirectory != null)
        {
            if (File.Exists(Path.Combine(currentDirectory.FullName, "Directory.Build.props")) &&
                File.Exists(Path.Combine(currentDirectory.FullName, "WebCodeCli", "WebCodeCli.csproj")))
            {
                return currentDirectory.FullName;
            }

            currentDirectory = currentDirectory.Parent;
        }

        throw new InvalidOperationException("Could not resolve the repository root from the current test base directory.");
    }
}
