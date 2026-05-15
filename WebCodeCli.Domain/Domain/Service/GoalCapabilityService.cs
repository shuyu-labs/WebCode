using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service;

public interface IGoalCapabilityService
{
    Task<GoalCapabilitySnapshot> GetStateAsync(
        GoalCapabilityContext context,
        CancellationToken cancellationToken = default);

    Task<GoalCapabilityProbeResult> ProbeAsync(
        GoalCapabilityContext context,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
}

public enum GoalCapabilityState
{
    Unknown,
    Available,
    Unavailable
}

public enum GoalCapabilityProbeOutcome
{
    Available,
    UnsupportedTool,
    UnsupportedVersion,
    MissingFeature,
    ProbeFailed
}

public sealed class GoalCapabilityContext
{
    public string ToolId { get; set; } = string.Empty;

    public string? ProviderId { get; set; }

    public string? WorkspacePath { get; set; }
}

public class GoalCapabilitySnapshot
{
    public string ToolId { get; set; } = string.Empty;

    public string ProviderId { get; set; } = GoalCapabilityService.UnscopedProviderId;

    public string CacheKey { get; set; } = string.Empty;

    public GoalCapabilityState State { get; set; }

    public string? Message { get; set; }

    public string? DetectedVersion { get; set; }
}

public sealed class GoalCapabilityProbeResult : GoalCapabilitySnapshot
{
    public GoalCapabilityProbeOutcome Outcome { get; set; }

    public bool FromCache { get; set; }

    public bool HasGoalsFeature { get; set; }
}

[ServiceDescription(typeof(IGoalCapabilityService), ServiceLifetime.Singleton)]
public class GoalCapabilityService : IGoalCapabilityService
{
    public const string UnscopedProviderId = "__default__";

    private static readonly Version MinimumCodexVersion = new(0, 128, 0);
    private static readonly Regex VersionRegex = new(@"(?<version>\d+\.\d+\.\d+)", RegexOptions.Compiled);
    private static readonly string[] WindowsExecutableExtensions =
    [
        ".exe",
        ".cmd",
        ".bat",
        ".ps1"
    ];

    private readonly ConcurrentDictionary<string, GoalCapabilityCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ICcSwitchService _ccSwitchService;
    private readonly ILogger<GoalCapabilityService> _logger;
    private readonly IReadOnlyList<CliToolConfig> _toolConfigs;

    public GoalCapabilityService(
        ICcSwitchService ccSwitchService,
        IOptions<CliToolsOption> options,
        ILogger<GoalCapabilityService> logger)
    {
        _ccSwitchService = ccSwitchService;
        _logger = logger;
        _toolConfigs = options.Value.Tools;
    }

    public async Task<GoalCapabilitySnapshot> GetStateAsync(
        GoalCapabilityContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resolvedContext = await ResolveContextAsync(context, cancellationToken);
        if (_cache.TryGetValue(resolvedContext.CacheKey, out var cached))
        {
            return cached.ToSnapshot();
        }

        return new GoalCapabilitySnapshot
        {
            ToolId = resolvedContext.ToolId,
            ProviderId = resolvedContext.ProviderId,
            CacheKey = resolvedContext.CacheKey,
            State = GoalCapabilityState.Unknown
        };
    }

    public async Task<GoalCapabilityProbeResult> ProbeAsync(
        GoalCapabilityContext context,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resolvedContext = await ResolveContextAsync(context, cancellationToken);
        if (!forceRefresh && _cache.TryGetValue(resolvedContext.CacheKey, out var cached))
        {
            return cached.ToProbeResult(fromCache: true);
        }

        try
        {
            var result = await ProbeCoreAsync(resolvedContext, cancellationToken);
            if (result.State == GoalCapabilityState.Unknown)
            {
                _cache.TryRemove(resolvedContext.CacheKey, out _);
            }
            else
            {
                _cache[resolvedContext.CacheKey] = GoalCapabilityCacheEntry.From(result);
            }

            return result;
        }
        catch (Exception ex)
        {
            _cache.TryRemove(resolvedContext.CacheKey, out _);
            _logger.LogWarning(
                ex,
                "检测 /goal 能力失败: ToolId={ToolId}, ProviderId={ProviderId}",
                resolvedContext.ToolId,
                resolvedContext.ProviderId);

            return new GoalCapabilityProbeResult
            {
                ToolId = resolvedContext.ToolId,
                ProviderId = resolvedContext.ProviderId,
                CacheKey = resolvedContext.CacheKey,
                State = GoalCapabilityState.Unknown,
                Outcome = GoalCapabilityProbeOutcome.ProbeFailed,
                Message = GoalQuickActionDefaults.CapabilityProbeFailedText
            };
        }
    }

    private async Task<GoalCapabilityProbeResult> ProbeCoreAsync(
        ResolvedGoalCapabilityContext context,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(context.ToolId, "codex", StringComparison.OrdinalIgnoreCase))
        {
            return new GoalCapabilityProbeResult
            {
                ToolId = context.ToolId,
                ProviderId = context.ProviderId,
                CacheKey = context.CacheKey,
                State = GoalCapabilityState.Unavailable,
                Outcome = GoalCapabilityProbeOutcome.UnsupportedTool,
                Message = "只有 Codex 支持 /goal 工作流"
            };
        }

        var command = ResolveCodexCommand();
        var versionOutput = await RunCommandAsync(
            command,
            "--version",
            context.WorkspacePath,
            cancellationToken);
        if (!versionOutput.Success)
        {
            return ProbeFailed(context, "无法检测 Codex 版本");
        }

        var detectedVersion = ParseVersion(versionOutput.Output);
        if (detectedVersion == null)
        {
            return ProbeFailed(context, "无法解析 Codex 版本");
        }

        if (detectedVersion < MinimumCodexVersion)
        {
            return new GoalCapabilityProbeResult
            {
                ToolId = context.ToolId,
                ProviderId = context.ProviderId,
                CacheKey = context.CacheKey,
                State = GoalCapabilityState.Unavailable,
                Outcome = GoalCapabilityProbeOutcome.UnsupportedVersion,
                Message = $"当前 Codex 版本 {detectedVersion} 不支持 /goal，请升级到 {MinimumCodexVersion} 或更高版本",
                DetectedVersion = detectedVersion.ToString()
            };
        }

        var featureOutput = await RunCommandAsync(
            command,
            "features list",
            context.WorkspacePath,
            cancellationToken);
        if (!featureOutput.Success)
        {
            return ProbeFailed(context, "无法检测 Codex goals feature", detectedVersion.ToString());
        }

        var hasGoalsFeature = featureOutput.Output
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(static line => line.TrimStart().StartsWith("goals", StringComparison.OrdinalIgnoreCase));
        if (!hasGoalsFeature)
        {
            return new GoalCapabilityProbeResult
            {
                ToolId = context.ToolId,
                ProviderId = context.ProviderId,
                CacheKey = context.CacheKey,
                State = GoalCapabilityState.Unavailable,
                Outcome = GoalCapabilityProbeOutcome.MissingFeature,
                Message = "当前 Codex 未提供 goals feature，无法启用 /goal",
                DetectedVersion = detectedVersion.ToString()
            };
        }

        return new GoalCapabilityProbeResult
        {
            ToolId = context.ToolId,
            ProviderId = context.ProviderId,
            CacheKey = context.CacheKey,
            State = GoalCapabilityState.Available,
            Outcome = GoalCapabilityProbeOutcome.Available,
            DetectedVersion = detectedVersion.ToString(),
            HasGoalsFeature = true
        };
    }

    private async Task<ResolvedGoalCapabilityContext> ResolveContextAsync(
        GoalCapabilityContext context,
        CancellationToken cancellationToken)
    {
        var normalizedToolId = NormalizeToolId(context.ToolId);
        var providerId = NormalizeProviderId(context.ProviderId);

        if (string.IsNullOrWhiteSpace(providerId)
            && !string.IsNullOrWhiteSpace(normalizedToolId)
            && _ccSwitchService.IsManagedTool(normalizedToolId))
        {
            var toolStatus = await _ccSwitchService.GetToolStatusAsync(normalizedToolId, cancellationToken);
            providerId = NormalizeProviderId(toolStatus.ActiveProviderId);
        }

        providerId = string.IsNullOrWhiteSpace(providerId)
            ? UnscopedProviderId
            : providerId;

        return new ResolvedGoalCapabilityContext
        {
            ToolId = normalizedToolId,
            ProviderId = providerId,
            WorkspacePath = NormalizeWorkspacePath(context.WorkspacePath),
            CacheKey = $"{normalizedToolId}::{providerId}"
        };
    }

    private string ResolveCodexCommand()
    {
        var configuredCommand = _toolConfigs
            .FirstOrDefault(static tool => string.Equals(tool.Id, "codex", StringComparison.OrdinalIgnoreCase))
            ?.Command
            ?.Trim();

        return string.IsNullOrWhiteSpace(configuredCommand) ? "codex" : configuredCommand;
    }

    protected virtual async Task<CommandProbeResult> RunCommandAsync(
        string command,
        string arguments,
        string? workingDirectory,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

        var invocation = ResolveCommandInvocation(command, arguments);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = invocation.FileName,
                Arguments = invocation.Arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = !string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory)
                    ? workingDirectory
                    : Environment.CurrentDirectory
            }
        };

        try
        {
            if (!process.Start())
            {
                return new CommandProbeResult(false, string.Empty);
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(timeoutCts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;

            return new CommandProbeResult(process.ExitCode == 0, output.Trim());
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new CommandProbeResult(false, string.Empty);
        }
        catch
        {
            TryKill(process);
            return new CommandProbeResult(false, string.Empty);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    internal static CommandInvocation ResolveCommandInvocation(
        string command,
        string arguments,
        string? pathEnvironment = null,
        string? comSpec = null,
        string? powershellPath = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return new CommandInvocation(string.Empty, arguments);
        }

        var trimmedCommand = command.Trim().Trim('"');
        if (!OperatingSystem.IsWindows())
        {
            return new CommandInvocation(trimmedCommand, arguments);
        }

        var resolvedPath = ResolveWindowsCommandPath(trimmedCommand, pathEnvironment);
        var extension = Path.GetExtension(resolvedPath);

        if (string.Equals(extension, ".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var shellPath = string.IsNullOrWhiteSpace(powershellPath)
                ? "powershell.exe"
                : powershellPath;
            return new CommandInvocation(
                shellPath,
                $"-NoProfile -ExecutionPolicy Bypass -File \"{resolvedPath}\" {arguments}".Trim());
        }

        if (string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase))
        {
            var cmdPath = string.IsNullOrWhiteSpace(comSpec)
                ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe"
                : comSpec;
            var commandArguments = string.IsNullOrWhiteSpace(arguments)
                ? $"\"{resolvedPath}\""
                : $"\"{resolvedPath}\" {arguments}";
            return new CommandInvocation(
                cmdPath,
                $"/d /c \"{commandArguments}\"");
        }

        return new CommandInvocation(resolvedPath, arguments);
    }

    internal static string ResolveWindowsCommandPath(string command, string? pathEnvironment = null)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return string.Empty;
        }

        if (Path.IsPathRooted(command) || command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            return command;
        }

        if (Path.HasExtension(command))
        {
            return FindCommandOnPath(command, pathEnvironment) ?? command;
        }

        foreach (var extension in WindowsExecutableExtensions)
        {
            var candidate = FindCommandOnPath(command + extension, pathEnvironment);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return FindCommandOnPath(command, pathEnvironment) ?? command;
    }

    private static string? FindCommandOnPath(string command, string? pathEnvironment)
    {
        var effectivePath = string.IsNullOrWhiteSpace(pathEnvironment)
            ? Environment.GetEnvironmentVariable("PATH")
            : pathEnvironment;
        if (string.IsNullOrWhiteSpace(effectivePath))
        {
            return null;
        }

        foreach (var rawDirectory in effectivePath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.IsNullOrWhiteSpace(rawDirectory))
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(rawDirectory, command);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static Version? ParseVersion(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var match = VersionRegex.Match(output);
        return match.Success && Version.TryParse(match.Groups["version"].Value, out var version)
            ? version
            : null;
    }

    private static GoalCapabilityProbeResult ProbeFailed(
        ResolvedGoalCapabilityContext context,
        string message,
        string? detectedVersion = null)
    {
        return new GoalCapabilityProbeResult
        {
            ToolId = context.ToolId,
            ProviderId = context.ProviderId,
            CacheKey = context.CacheKey,
            State = GoalCapabilityState.Unknown,
            Outcome = GoalCapabilityProbeOutcome.ProbeFailed,
            Message = message,
            DetectedVersion = detectedVersion
        };
    }

    private static string NormalizeToolId(string? toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return string.Empty;
        }

        if (toolId.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            return "claude-code";
        }

        if (toolId.Equals("opencode-cli", StringComparison.OrdinalIgnoreCase))
        {
            return "opencode";
        }

        return toolId.Trim();
    }

    private static string NormalizeProviderId(string? providerId)
    {
        return string.IsNullOrWhiteSpace(providerId)
            ? string.Empty
            : providerId.Trim();
    }

    private static string? NormalizeWorkspacePath(string? workspacePath)
    {
        return string.IsNullOrWhiteSpace(workspacePath)
            ? null
            : workspacePath.Trim();
    }

    private sealed class ResolvedGoalCapabilityContext
    {
        public string ToolId { get; init; } = string.Empty;

        public string ProviderId { get; init; } = UnscopedProviderId;

        public string? WorkspacePath { get; init; }

        public string CacheKey { get; init; } = string.Empty;
    }

    private sealed class GoalCapabilityCacheEntry
    {
        public string ToolId { get; init; } = string.Empty;

        public string ProviderId { get; init; } = UnscopedProviderId;

        public string CacheKey { get; init; } = string.Empty;

        public GoalCapabilityState State { get; init; }

        public GoalCapabilityProbeOutcome Outcome { get; init; }

        public string? Message { get; init; }

        public string? DetectedVersion { get; init; }

        public bool HasGoalsFeature { get; init; }

        public static GoalCapabilityCacheEntry From(GoalCapabilityProbeResult result)
        {
            return new GoalCapabilityCacheEntry
            {
                ToolId = result.ToolId,
                ProviderId = result.ProviderId,
                CacheKey = result.CacheKey,
                State = result.State,
                Outcome = result.Outcome,
                Message = result.Message,
                DetectedVersion = result.DetectedVersion,
                HasGoalsFeature = result.HasGoalsFeature
            };
        }

        public GoalCapabilitySnapshot ToSnapshot()
        {
            return new GoalCapabilitySnapshot
            {
                ToolId = ToolId,
                ProviderId = ProviderId,
                CacheKey = CacheKey,
                State = State,
                Message = Message,
                DetectedVersion = DetectedVersion
            };
        }

        public GoalCapabilityProbeResult ToProbeResult(bool fromCache)
        {
            return new GoalCapabilityProbeResult
            {
                ToolId = ToolId,
                ProviderId = ProviderId,
                CacheKey = CacheKey,
                State = State,
                Outcome = Outcome,
                Message = Message,
                DetectedVersion = DetectedVersion,
                HasGoalsFeature = HasGoalsFeature,
                FromCache = fromCache
            };
        }
    }

    protected readonly record struct CommandProbeResult(bool Success, string Output);
    internal readonly record struct CommandInvocation(string FileName, string Arguments);
}

internal sealed class NullGoalCapabilityService : IGoalCapabilityService
{
    public static NullGoalCapabilityService Instance { get; } = new();

    public Task<GoalCapabilitySnapshot> GetStateAsync(
        GoalCapabilityContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GoalCapabilitySnapshot
        {
            ToolId = context.ToolId,
            ProviderId = context.ProviderId ?? GoalCapabilityService.UnscopedProviderId,
            CacheKey = $"{context.ToolId}::{context.ProviderId ?? GoalCapabilityService.UnscopedProviderId}",
            State = GoalCapabilityState.Unknown
        });
    }

    public Task<GoalCapabilityProbeResult> ProbeAsync(
        GoalCapabilityContext context,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new GoalCapabilityProbeResult
        {
            ToolId = context.ToolId,
            ProviderId = context.ProviderId ?? GoalCapabilityService.UnscopedProviderId,
            CacheKey = $"{context.ToolId}::{context.ProviderId ?? GoalCapabilityService.UnscopedProviderId}",
            State = GoalCapabilityState.Unknown,
            Outcome = GoalCapabilityProbeOutcome.ProbeFailed,
            Message = GoalQuickActionDefaults.CapabilityProbeFailedText
        });
    }
}
