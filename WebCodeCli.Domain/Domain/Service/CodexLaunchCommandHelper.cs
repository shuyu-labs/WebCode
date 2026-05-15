using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace WebCodeCli.Domain.Domain.Service;

internal static class CodexLaunchCommandHelper
{
    private static readonly object PreferredNodeExecutableLock = new();
    private static string? _preferredNodeExecutablePath;

    internal static bool TryRewriteWindowsCodexCmdToNode(
        ProcessStartInfo startInfo,
        string commandPath,
        string originalArguments,
        Func<string?> preferredNodeExecutablePathResolver,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentException.ThrowIfNullOrWhiteSpace(commandPath);
        ArgumentNullException.ThrowIfNull(preferredNodeExecutablePathResolver);

        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        var extension = Path.GetExtension(commandPath);
        if (!string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogInformation("Codex 启动重写跳过：命令不是 .cmd，Extension={Extension}", extension);
            return false;
        }

        var commandDirectory = Path.GetDirectoryName(commandPath);
        if (string.IsNullOrWhiteSpace(commandDirectory))
        {
            logger?.LogWarning("Codex 启动重写跳过：无法获取命令目录，CommandPath={CommandPath}", commandPath);
            return false;
        }

        var cliJsPath = Path.Combine(commandDirectory, "node_modules", "@openai", "codex", "bin", "codex.js");
        logger?.LogInformation("Codex 启动重写检查 codex.js 路径: {CliJsPath}, Exists={Exists}", cliJsPath, File.Exists(cliJsPath));
        if (!File.Exists(cliJsPath))
        {
            return false;
        }

        var preferredNodePath = preferredNodeExecutablePathResolver();
        logger?.LogInformation("Codex 启动重写检查 Node.js 路径: {NodePath}", preferredNodePath ?? "<null>");
        if (string.IsNullOrWhiteSpace(preferredNodePath) || !File.Exists(preferredNodePath))
        {
            logger?.LogWarning("Codex 启动重写跳过：未找到可用的 Node.js");
            return false;
        }

        startInfo.FileName = preferredNodePath;
        startInfo.Arguments = $"\"{cliJsPath}\" {originalArguments}";
        logger?.LogInformation("Codex 改为直接使用 Node.js 启动: {NodePath}", preferredNodePath);
        return true;
    }

    internal static string? ResolvePreferredNodeExecutablePath(ILogger? logger = null)
    {
        lock (PreferredNodeExecutableLock)
        {
            if (!string.IsNullOrWhiteSpace(_preferredNodeExecutablePath) && File.Exists(_preferredNodeExecutablePath))
            {
                return _preferredNodeExecutablePath;
            }

            try
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var fnmRoot = Path.Combine(localAppData, "fnm_multishells");
                if (Directory.Exists(fnmRoot))
                {
                    var fnmCandidate = Directory.GetDirectories(fnmRoot)
                        .Select(directory => new FileInfo(Path.Combine(directory, "node.exe")))
                        .Where(file => file.Exists)
                        .OrderByDescending(file => file.LastWriteTimeUtc)
                        .FirstOrDefault();

                    if (fnmCandidate != null)
                    {
                        _preferredNodeExecutablePath = fnmCandidate.FullName;
                        logger?.LogInformation("优先选择 fnm Node.js: {NodePath}", _preferredNodeExecutablePath);
                        return _preferredNodeExecutablePath;
                    }
                }
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "扫描 fnm node.exe 路径失败");
            }

            var programFilesNode = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
            if (File.Exists(programFilesNode))
            {
                _preferredNodeExecutablePath = programFilesNode;
                logger?.LogInformation("回退使用系统 Node.js: {NodePath}", _preferredNodeExecutablePath);
                return _preferredNodeExecutablePath;
            }

            logger?.LogWarning("未找到可用的 node.exe");
            return null;
        }
    }
}
