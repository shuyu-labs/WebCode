using WebCodeCli.Domain.Domain.Service;
using Xunit;

namespace WebCodeCli.Tests;

public sealed class GoalCapabilityServiceTests
{
    [Fact]
    public void ResolveWindowsCommandPath_PrefersCmdWrapper_FromPath()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "webcode-goal-tests", Guid.NewGuid().ToString("N"));
        var binDirectory = Path.Combine(tempRoot, "bin");
        Directory.CreateDirectory(binDirectory);

        try
        {
            var cmdPath = Path.Combine(binDirectory, "codex.cmd");
            var ps1Path = Path.Combine(binDirectory, "codex.ps1");
            File.WriteAllText(cmdPath, "@echo off");
            File.WriteAllText(ps1Path, "Write-Host test");

            var resolved = GoalCapabilityService.ResolveWindowsCommandPath("codex", binDirectory);

            Assert.Equal(cmdPath, resolved, StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void ResolveCommandInvocation_WrapsCmdScripts_WithComSpec()
    {
        var invocation = GoalCapabilityService.ResolveCommandInvocation(
            @"E:\npm-global\codex.cmd",
            "--version",
            comSpec: @"C:\Windows\System32\cmd.exe");

        Assert.Equal(@"C:\Windows\System32\cmd.exe", invocation.FileName);
        Assert.Equal(@"/d /c """"E:\npm-global\codex.cmd"" --version""", invocation.Arguments);
    }

    [Fact]
    public void ResolveCommandInvocation_WrapsPowerShellScripts_WithPowerShellHost()
    {
        var invocation = GoalCapabilityService.ResolveCommandInvocation(
            @"E:\npm-global\codex.ps1",
            "features list",
            powershellPath: "powershell.exe");

        Assert.Equal("powershell.exe", invocation.FileName);
        Assert.Equal(@"-NoProfile -ExecutionPolicy Bypass -File ""E:\npm-global\codex.ps1"" features list", invocation.Arguments);
    }
}
