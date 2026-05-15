using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using WebCodeCli.Domain.Domain.Service;

namespace WebCodeCli.Domain.Tests;

public class CodexLaunchCommandHelperTests
{
    [Fact]
    public async Task TryRewriteWindowsCodexCmdToNode_RewritesCodexCmdLaunchToNodeJsAppServer()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var tempRoot = Path.Combine(Path.GetTempPath(), "WebCodeCli.Tests", Guid.NewGuid().ToString("N"));
        var commandPath = Path.Combine(tempRoot, "codex.cmd");
        var cliJsPath = Path.Combine(tempRoot, "node_modules", "@openai", "codex", "bin", "codex.js");
        var nodePath = Path.Combine(tempRoot, "node.exe");

        Directory.CreateDirectory(Path.GetDirectoryName(cliJsPath)!);
        await File.WriteAllTextAsync(cliJsPath, "console.log('codex');");
        await File.WriteAllTextAsync(nodePath, string.Empty);

        try
        {
            var helperType = typeof(CliExecutorService).Assembly.GetType(
                "WebCodeCli.Domain.Domain.Service.CodexLaunchCommandHelper",
                throwOnError: true)!;
            var method = helperType.GetMethod(
                "TryRewriteWindowsCodexCmdToNode",
                BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(ProcessStartInfo), typeof(string), typeof(string), typeof(Func<string?>), typeof(Microsoft.Extensions.Logging.ILogger)])!;

            var startInfo = new ProcessStartInfo
            {
                FileName = commandPath,
                Arguments = "app-server"
            };

            var rewritten = (bool)method.Invoke(
                null,
                new object?[]
                {
                    startInfo,
                    commandPath,
                    "app-server",
                    new Func<string?>(() => nodePath),
                    NullLogger.Instance
                })!;

            Assert.True(rewritten);
            Assert.Equal(nodePath, startInfo.FileName);
            Assert.Equal($"\"{cliJsPath}\" app-server", startInfo.Arguments);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }
}
