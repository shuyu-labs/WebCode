using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebCodeCli.Domain.Common.Extensions;

namespace WebCodeCli.Domain.Domain.Service;

public interface ISuperpowersCapabilityService
{
    Task<SuperpowersCapabilitySnapshot> GetStateAsync(
        SuperpowersCapabilityContext context,
        CancellationToken cancellationToken = default);

    Task<SuperpowersCapabilityProbeResult> ProbeAsync(
        SuperpowersCapabilityContext context,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
}

public enum SuperpowersCapabilityState
{
    Unknown,
    Available,
    Unavailable
}

public enum SuperpowersCapabilityProbeOutcome
{
    Available,
    MissingCapability,
    ProbeFailed
}

public sealed class SuperpowersCapabilityContext
{
    public string ToolId { get; set; } = string.Empty;

    public string? ProviderId { get; set; }

    public string? WorkspacePath { get; set; }
}

public class SuperpowersCapabilitySnapshot
{
    public string ToolId { get; set; } = string.Empty;

    public string ProviderId { get; set; } = SuperpowersCapabilityService.UnscopedProviderId;

    public string CacheKey { get; set; } = string.Empty;

    public SuperpowersCapabilityState State { get; set; }

    public string? Message { get; set; }
}

public sealed class SuperpowersCapabilityProbeResult : SuperpowersCapabilitySnapshot
{
    public SuperpowersCapabilityProbeOutcome Outcome { get; set; }

    public bool FromCache { get; set; }

    public bool HasSuperpowersPlugin { get; set; }

    public bool UsedSkillFallback { get; set; }

    public IReadOnlyList<string> MissingSkills { get; set; } = Array.Empty<string>();
}

[ServiceDescription(typeof(ISuperpowersCapabilityService), ServiceLifetime.Singleton)]
public sealed class SuperpowersCapabilityService : ISuperpowersCapabilityService
{
    public const string UnscopedProviderId = "__default__";

    private static readonly string[] RequiredSkillNames =
    [
        "brainstorming",
        "dispatching-parallel-agents",
        "executing-plans",
        "finishing-a-development-branch",
        "receiving-code-review",
        "requesting-code-review",
        "subagent-driven-development",
        "systematic-debugging",
        "test-driven-development",
        "using-git-worktrees",
        "using-superpowers",
        "verification-before-completion",
        "writing-plans",
        "writing-skills"
    ];

    private static readonly Regex SkillFrontMatterRegex = new(
        @"^---\s*\r?\n(?<frontMatter>.*?)\r?\n---",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex SkillNameRegex = new(
        @"(?m)^\s*name:\s*(?<name>.+?)\s*$",
        RegexOptions.Compiled);

    private static readonly EnumerationOptions RecursiveEnumerationOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        ReturnSpecialDirectories = false,
        MatchCasing = MatchCasing.CaseInsensitive
    };

    private readonly ConcurrentDictionary<string, SuperpowersCapabilityCacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ICcSwitchService _ccSwitchService;
    private readonly ILogger<SuperpowersCapabilityService> _logger;
    private readonly Func<string> _userProfileResolver;

    // Capability probe is a user-facing diagnostic. Log both via ILogger and Console
    // so results are visible in console-hosted scenarios.
    private void LogProbe(string message)
    {
        _logger.LogInformation("{Message}", message);
        try
        {
            Console.WriteLine($"[superpowers-probe] {message}");
        }
        catch
        {
            // ignored
        }
    }

    public SuperpowersCapabilityService(
        ICcSwitchService ccSwitchService,
        ILogger<SuperpowersCapabilityService> logger)
        : this(ccSwitchService, logger, ResolveUserProfilePath)
    {
    }

    public SuperpowersCapabilityService(
        ICcSwitchService ccSwitchService,
        ILogger<SuperpowersCapabilityService> logger,
        Func<string> userProfileResolver)
    {
        _ccSwitchService = ccSwitchService;
        _logger = logger;
        _userProfileResolver = userProfileResolver;
    }

    public async Task<SuperpowersCapabilitySnapshot> GetStateAsync(
        SuperpowersCapabilityContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resolvedContext = await ResolveContextAsync(context, cancellationToken);
        if (_cache.TryGetValue(resolvedContext.CacheKey, out var cached))
        {
            return cached.ToSnapshot();
        }

        return CreateUnknownSnapshot(resolvedContext);
    }

    public async Task<SuperpowersCapabilityProbeResult> ProbeAsync(
        SuperpowersCapabilityContext context,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var resolvedContext = await ResolveContextAsync(context, cancellationToken);
        if (!forceRefresh && _cache.TryGetValue(resolvedContext.CacheKey, out var cached))
        {
            LogProbe(
                $"cache hit: ToolId={resolvedContext.ToolId}, ProviderId={resolvedContext.ProviderId}, CacheKey={resolvedContext.CacheKey}, State={cached.State}");
            return cached.ToProbeResult(fromCache: true);
        }

        try
        {
            LogProbe(
                $"start: ToolId={resolvedContext.ToolId}, ProviderId={resolvedContext.ProviderId}, CacheKey={resolvedContext.CacheKey}, Workspace={resolvedContext.WorkspacePath ?? "<null>"}, forceRefresh={forceRefresh}");
            var result = ProbeCore(resolvedContext);
            LogProbe(
                $"result: ToolId={result.ToolId}, ProviderId={result.ProviderId}, State={result.State}, Outcome={result.Outcome}, HasPlugin={result.HasSuperpowersPlugin}, UsedSkillFallback={result.UsedSkillFallback}, MissingSkills={result.MissingSkills.Count}");
            if (result.State == SuperpowersCapabilityState.Available
                || result.State == SuperpowersCapabilityState.Unavailable)
            {
                _cache[resolvedContext.CacheKey] = SuperpowersCapabilityCacheEntry.From(result);
            }
            else
            {
                _cache.TryRemove(resolvedContext.CacheKey, out _);
            }

            return result;
        }
        catch (Exception ex)
        {
            _cache.TryRemove(resolvedContext.CacheKey, out _);
            _logger.LogWarning(
                ex,
                "检测 Superpowers 能力失败: ToolId={ToolId}, ProviderId={ProviderId}, Workspace={WorkspacePath}",
                resolvedContext.ToolId,
                resolvedContext.ProviderId,
                resolvedContext.WorkspacePath);

            return new SuperpowersCapabilityProbeResult
            {
                ToolId = resolvedContext.ToolId,
                ProviderId = resolvedContext.ProviderId,
                CacheKey = resolvedContext.CacheKey,
                State = SuperpowersCapabilityState.Unknown,
                Outcome = SuperpowersCapabilityProbeOutcome.ProbeFailed,
                Message = "检测 superpowers 能力失败，请重试"
            };
        }
    }

    private SuperpowersCapabilityProbeResult ProbeCore(ResolvedSuperpowersCapabilityContext context)
    {
        if (!IsSupportedTool(context.ToolId))
        {
            LogProbe($"unsupported tool: ToolId={context.ToolId}");
            return new SuperpowersCapabilityProbeResult
            {
                ToolId = context.ToolId,
                ProviderId = context.ProviderId,
                CacheKey = context.CacheKey,
                State = SuperpowersCapabilityState.Unavailable,
                Outcome = SuperpowersCapabilityProbeOutcome.MissingCapability,
                Message = "当前工具不支持 superpowers 能力检测"
            };
        }

        var pluginRoots = ResolvePluginRoots(context.ToolId, context.WorkspacePath);
        LogProbe($"scan plugin roots: [{string.Join("; ", pluginRoots)}]");
        var pluginScanResult = TryFindSuperpowersPluginManifest(pluginRoots);
        var pluginManifestPath = pluginScanResult.ManifestPath;
        LogProbe($"plugin manifest: {(string.IsNullOrWhiteSpace(pluginManifestPath) ? "<not found>" : pluginManifestPath)} (hadReadError={pluginScanResult.HadReadError})");
        var hasSuperpowersPlugin = !string.IsNullOrWhiteSpace(pluginManifestPath);
        if (hasSuperpowersPlugin)
        {
            _logger.LogInformation(
                "检测到 Superpowers 插件: ToolId={ToolId}, ProviderId={ProviderId}, Manifest={ManifestPath}",
                context.ToolId,
                context.ProviderId,
                pluginManifestPath);

            if (string.Equals(context.ToolId, "codex", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(context.WorkspacePath))
            {
                if (!EnsureWorkspaceSuperpowersSkillsFromPlugin(
                        context.WorkspacePath,
                        pluginManifestPath!,
                        out var materializeError))
                {
                    return new SuperpowersCapabilityProbeResult
                    {
                        ToolId = context.ToolId,
                        ProviderId = context.ProviderId,
                        CacheKey = context.CacheKey,
                        State = SuperpowersCapabilityState.Unknown,
                        Outcome = SuperpowersCapabilityProbeOutcome.ProbeFailed,
                        HasSuperpowersPlugin = true,
                        Message = string.IsNullOrWhiteSpace(materializeError)
                            ? "检测 superpowers 能力失败，请重试"
                            : materializeError
                    };
                }
            }
        }

        var skillRoots = ResolveSkillRoots(context.ToolId, context.WorkspacePath);
        LogProbe($"scan skill roots: [{string.Join("; ", skillRoots)}]");
        var skillScanResult = LoadSkillNames(skillRoots);
        var installedSkills = skillScanResult.SkillNames;
        var missingSkills = RequiredSkillNames
            .Where(required => !installedSkills.Contains(required))
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        LogProbe(
            $"skills scanned: installed={installedSkills.Count}, missing={missingSkills.Length}, hadReadError(skill={skillScanResult.HadReadError})");
        if (missingSkills.Length > 0)
        {
            LogProbe($"missing skills: {string.Join(", ", missingSkills)}");
        }

        if (missingSkills.Length > 0 && (pluginScanResult.HadReadError || skillScanResult.HadReadError))
        {
            return new SuperpowersCapabilityProbeResult
            {
                ToolId = context.ToolId,
                ProviderId = context.ProviderId,
                CacheKey = context.CacheKey,
                State = SuperpowersCapabilityState.Unknown,
                Outcome = SuperpowersCapabilityProbeOutcome.ProbeFailed,
                HasSuperpowersPlugin = hasSuperpowersPlugin,
                Message = "检测 superpowers 能力失败，请重试"
            };
        }

        if (missingSkills.Length == 0)
        {
            if (hasSuperpowersPlugin)
            {
                _logger.LogInformation(
                    "Superpowers 插件与技能已就绪: ToolId={ToolId}, ProviderId={ProviderId}",
                    context.ToolId,
                    context.ProviderId);
            }
            else
            {
                _logger.LogInformation(
                    "未检测到 Superpowers 插件，但技能兜底完整: ToolId={ToolId}, ProviderId={ProviderId}",
                    context.ToolId,
                    context.ProviderId);
            }

            return new SuperpowersCapabilityProbeResult
            {
                ToolId = context.ToolId,
                ProviderId = context.ProviderId,
                CacheKey = context.CacheKey,
                State = SuperpowersCapabilityState.Available,
                Outcome = SuperpowersCapabilityProbeOutcome.Available,
                HasSuperpowersPlugin = hasSuperpowersPlugin,
                UsedSkillFallback = !hasSuperpowersPlugin
            };
        }

        _logger.LogInformation(
            "当前 Provider 缺少 Superpowers 能力: ToolId={ToolId}, ProviderId={ProviderId}, MissingSkills={MissingSkills}",
            context.ToolId,
            context.ProviderId,
            string.Join(", ", missingSkills));

        return new SuperpowersCapabilityProbeResult
        {
            ToolId = context.ToolId,
            ProviderId = context.ProviderId,
            CacheKey = context.CacheKey,
            State = SuperpowersCapabilityState.Unavailable,
            Outcome = SuperpowersCapabilityProbeOutcome.MissingCapability,
            HasSuperpowersPlugin = hasSuperpowersPlugin,
            Message = "当前 Provider 缺少 superpowers 能力",
            MissingSkills = missingSkills
        };
    }

    private async Task<ResolvedSuperpowersCapabilityContext> ResolveContextAsync(
        SuperpowersCapabilityContext context,
        CancellationToken cancellationToken)
    {
        var normalizedToolId = SessionLaunchOverrideHelper.NormalizeToolId(context.ToolId);
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

        return new ResolvedSuperpowersCapabilityContext
        {
            ToolId = normalizedToolId,
            ProviderId = providerId,
            WorkspacePath = NormalizeWorkspacePath(context.WorkspacePath),
            CacheKey = BuildCacheKey(normalizedToolId, providerId)
        };
    }

    private static string BuildCacheKey(string toolId, string providerId)
    {
        return $"{toolId}::{providerId}";
    }

    private static SuperpowersCapabilitySnapshot CreateUnknownSnapshot(ResolvedSuperpowersCapabilityContext context)
    {
        return new SuperpowersCapabilitySnapshot
        {
            ToolId = context.ToolId,
            ProviderId = context.ProviderId,
            CacheKey = context.CacheKey,
            State = SuperpowersCapabilityState.Unknown
        };
    }

    private IEnumerable<string> ResolvePluginRoots(string toolId, string? workspacePath)
    {
        var userProfile = _userProfileResolver();
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        switch (toolId)
        {
            case "claude-code":
                AddPath(roots, Path.Combine(userProfile, ".claude", "plugins"));
                AddWorkspacePath(roots, workspacePath, ".claude", "plugins");
                break;
            case "codex":
                AddPath(roots, Path.Combine(userProfile, ".codex", "plugins"));
                AddWorkspacePath(roots, workspacePath, ".codex", "plugins");
                break;
            case "opencode":
                AddPath(roots, Path.Combine(userProfile, ".opencode", "plugins"));
                AddPath(roots, Path.Combine(userProfile, ".config", "opencode", "plugins"));
                AddWorkspacePath(roots, workspacePath, ".opencode", "plugins");
                break;
        }

        return roots;
    }

    private IEnumerable<string> ResolveSkillRoots(string toolId, string? workspacePath)
    {
        var userProfile = _userProfileResolver();
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        switch (toolId)
        {
            case "claude-code":
                AddPath(roots, Path.Combine(userProfile, ".claude", "skills"));
                AddWorkspacePath(roots, workspacePath, "skills", "claude");
                break;
            case "codex":
                AddPath(roots, Path.Combine(userProfile, ".codex", "skills"));
                AddWorkspacePath(roots, workspacePath, ".codex", "skills");
                AddWorkspacePath(roots, workspacePath, "skills", "codex");
                break;
            case "opencode":
                AddPath(roots, Path.Combine(userProfile, ".opencode", "skills"));
                AddPath(roots, Path.Combine(userProfile, ".config", "opencode", "skills"));
                AddWorkspacePath(roots, workspacePath, ".opencode", "skills");

                if (ShouldIncludeClaudeSkillsForOpenCode())
                {
                    AddPath(roots, Path.Combine(userProfile, ".claude", "skills"));
                    AddWorkspacePath(roots, workspacePath, "skills", "claude");
                }

                break;
        }

        return roots;
    }

    private static PluginScanResult TryFindSuperpowersPluginManifest(IEnumerable<string> pluginRoots)
    {
        var hadReadError = false;

        foreach (var root in pluginRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> manifestPaths;
            try
            {
                manifestPaths = Directory.EnumerateFiles(root, "plugin.json", RecursiveEnumerationOptions);
            }
            catch
            {
                hadReadError = true;
                continue;
            }

            foreach (var manifestPath in manifestPaths)
            {
                var manifestDirectory = Path.GetFileName(Path.GetDirectoryName(manifestPath));
                if (!string.Equals(manifestDirectory, ".codex-plugin", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var manifestResult = IsSuperpowersPluginManifest(manifestPath);
                if (manifestResult.IsMatch)
                {
                    return new PluginScanResult(manifestPath, hadReadError || manifestResult.HadReadError);
                }

                if (manifestResult.HadReadError)
                {
                    hadReadError = true;
                }
            }
        }

        return new PluginScanResult(null, hadReadError);
    }

    private static PluginManifestMatchResult IsSuperpowersPluginManifest(string manifestPath)
    {
        try
        {
            using var stream = File.OpenRead(manifestPath);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;

            return new PluginManifestMatchResult(
                root.TryGetProperty("name", out var nameElement)
                && string.Equals(nameElement.GetString(), "superpowers", StringComparison.OrdinalIgnoreCase),
                HadReadError: false);
        }
        catch
        {
            return new PluginManifestMatchResult(IsMatch: false, HadReadError: true);
        }
    }

    private bool EnsureWorkspaceSuperpowersSkillsFromPlugin(
        string workspacePath,
        string pluginManifestPath,
        out string? errorMessage)
    {
        errorMessage = null;

        var workspaceSkillsRoot = ResolveWorkspaceCodexSkillsDirectory(workspacePath);
        var workspaceSkillScan = LoadSkillNames([workspaceSkillsRoot]);
        if (workspaceSkillScan.SkillNames.Contains("using-superpowers"))
        {
            LogProbe($"workspace already contains using-superpowers: {workspaceSkillsRoot}");
            return true;
        }

        var pluginSkillsRoot = ResolveSuperpowersPluginSkillsDirectory(pluginManifestPath);
        if (string.IsNullOrWhiteSpace(pluginSkillsRoot) || !Directory.Exists(pluginSkillsRoot))
        {
            errorMessage = "检测到 superpowers 插件，但未找到可同步的 skills 目录";
            LogProbe($"plugin skills root missing: {pluginSkillsRoot ?? "<null>"}");
            return false;
        }

        try
        {
            LogProbe($"copy plugin skills: source={pluginSkillsRoot}, target={workspaceSkillsRoot}");
            CopyDirectory(pluginSkillsRoot, workspaceSkillsRoot);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "同步 superpowers plugin skills 到工作区失败: Workspace={WorkspacePath}, Source={SourceSkillsRoot}, Target={TargetSkillsRoot}",
                workspacePath,
                pluginSkillsRoot,
                workspaceSkillsRoot);
            errorMessage = "同步 superpowers skills 到工作区失败，请重试";
            return false;
        }

        var refreshedWorkspaceSkills = LoadSkillNames([workspaceSkillsRoot]);
        if (refreshedWorkspaceSkills.SkillNames.Contains("using-superpowers"))
        {
            LogProbe($"workspace skill materialized: {workspaceSkillsRoot}");
            return true;
        }

        errorMessage = "检测到 superpowers 插件，但工作区仍缺少 using-superpowers skill";
        LogProbe($"workspace still missing using-superpowers after copy: {workspaceSkillsRoot}");
        return false;
    }

    private static string? ResolveSuperpowersPluginSkillsDirectory(string pluginManifestPath)
    {
        var manifestDirectory = Path.GetDirectoryName(pluginManifestPath);
        var pluginPackageDirectory = string.IsNullOrWhiteSpace(manifestDirectory)
            ? null
            : Directory.GetParent(manifestDirectory)?.FullName;

        return string.IsNullOrWhiteSpace(pluginPackageDirectory)
            ? null
            : Path.Combine(pluginPackageDirectory, "skills");
    }

    private static string ResolveWorkspaceCodexSkillsDirectory(string workspacePath)
    {
        var trimmedWorkspace = workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetFileName(trimmedWorkspace), ".codex", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(trimmedWorkspace, "skills")
            : Path.Combine(trimmedWorkspace, ".codex", "skills");
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            var targetFile = Path.Combine(targetDir, fileName);
            File.Copy(file, targetFile, overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(subDir);
            var targetSubDir = Path.Combine(targetDir, dirName);
            CopyDirectory(subDir, targetSubDir);
        }
    }

    private static SkillScanResult LoadSkillNames(IEnumerable<string> skillRoots)
    {
        var skills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hadReadError = false;

        foreach (var root in skillRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> skillPaths;
            try
            {
                skillPaths = Directory.EnumerateFiles(root, "SKILL.md", RecursiveEnumerationOptions);
            }
            catch
            {
                hadReadError = true;
                continue;
            }

            foreach (var skillPath in skillPaths)
            {
                var readResult = TryReadSkillName(skillPath);
                if (readResult.HadReadError)
                {
                    hadReadError = true;
                }

                var skillName = readResult.SkillName;
                if (!string.IsNullOrWhiteSpace(skillName))
                {
                    skills.Add(skillName);
                }
            }
        }

        return new SkillScanResult(skills, hadReadError);
    }

    private static SkillReadResult TryReadSkillName(string skillPath)
    {
        try
        {
            var content = File.ReadAllText(skillPath);
            var frontMatterMatch = SkillFrontMatterRegex.Match(content);
            if (!frontMatterMatch.Success)
            {
                return new SkillReadResult(null, HadReadError: false);
            }

            var nameMatch = SkillNameRegex.Match(frontMatterMatch.Groups["frontMatter"].Value);
            return new SkillReadResult(
                nameMatch.Success ? nameMatch.Groups["name"].Value.Trim() : null,
                HadReadError: false);
        }
        catch
        {
            return new SkillReadResult(null, HadReadError: true);
        }
    }

    private static void AddPath(ISet<string> paths, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            paths.Add(path);
        }
    }

    private static void AddWorkspacePath(ISet<string> paths, string? workspacePath, params string[] segments)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            return;
        }

        paths.Add(CombineWorkspacePath(workspacePath, segments));
    }

    private static string CombineWorkspacePath(string workspacePath, params string[] segments)
    {
        var trimmedWorkspace = workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Length == 0)
        {
            return trimmedWorkspace;
        }

        if (string.Equals(Path.GetFileName(trimmedWorkspace), segments[0], StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(new[] { trimmedWorkspace }.Concat(segments.Skip(1)).ToArray());
        }

        return Path.Combine(new[] { trimmedWorkspace }.Concat(segments).ToArray());
    }

    private static bool ShouldIncludeClaudeSkillsForOpenCode()
    {
        var rawValue = Environment.GetEnvironmentVariable("OPENCODE_DISABLE_CLAUDE_CODE_SKILLS");
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        return !rawValue.Equals("1", StringComparison.OrdinalIgnoreCase)
            && !rawValue.Equals("true", StringComparison.OrdinalIgnoreCase)
            && !rawValue.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSupportedTool(string toolId)
    {
        return toolId is "claude-code" or "codex" or "opencode";
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

    private static string ResolveUserProfilePath()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    private sealed class ResolvedSuperpowersCapabilityContext
    {
        public string ToolId { get; init; } = string.Empty;

        public string ProviderId { get; init; } = UnscopedProviderId;

        public string? WorkspacePath { get; init; }

        public string CacheKey { get; init; } = string.Empty;
    }

    private sealed class SuperpowersCapabilityCacheEntry
    {
        public string ToolId { get; init; } = string.Empty;

        public string ProviderId { get; init; } = UnscopedProviderId;

        public string CacheKey { get; init; } = string.Empty;

        public SuperpowersCapabilityState State { get; init; }

        public SuperpowersCapabilityProbeOutcome Outcome { get; init; }

        public string? Message { get; init; }

        public bool HasSuperpowersPlugin { get; init; }

        public bool UsedSkillFallback { get; init; }

        public IReadOnlyList<string> MissingSkills { get; init; } = Array.Empty<string>();

        public static SuperpowersCapabilityCacheEntry From(SuperpowersCapabilityProbeResult result)
        {
            return new SuperpowersCapabilityCacheEntry
            {
                ToolId = result.ToolId,
                ProviderId = result.ProviderId,
                CacheKey = result.CacheKey,
                State = result.State,
                Outcome = result.Outcome,
                Message = result.Message,
                HasSuperpowersPlugin = result.HasSuperpowersPlugin,
                UsedSkillFallback = result.UsedSkillFallback,
                MissingSkills = result.MissingSkills
            };
        }

        public SuperpowersCapabilitySnapshot ToSnapshot()
        {
            return new SuperpowersCapabilitySnapshot
            {
                ToolId = ToolId,
                ProviderId = ProviderId,
                CacheKey = CacheKey,
                State = State,
                Message = Message
            };
        }

        public SuperpowersCapabilityProbeResult ToProbeResult(bool fromCache)
        {
            return new SuperpowersCapabilityProbeResult
            {
                ToolId = ToolId,
                ProviderId = ProviderId,
                CacheKey = CacheKey,
                State = State,
                Outcome = Outcome,
                Message = Message,
                FromCache = fromCache,
                HasSuperpowersPlugin = HasSuperpowersPlugin,
                UsedSkillFallback = UsedSkillFallback,
                MissingSkills = MissingSkills
            };
        }
    }

    private readonly record struct PluginManifestMatchResult(bool IsMatch, bool HadReadError);

    private readonly record struct PluginScanResult(string? ManifestPath, bool HadReadError);

    private readonly record struct SkillReadResult(string? SkillName, bool HadReadError);

    private readonly record struct SkillScanResult(HashSet<string> SkillNames, bool HadReadError);
}
