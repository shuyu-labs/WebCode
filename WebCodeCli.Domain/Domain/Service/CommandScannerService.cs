using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Domain.Model;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace WebCodeCli.Domain.Domain.Service;

[ServiceDescription(typeof(CommandScannerService), ServiceLifetime.Singleton)]
public class CommandScannerService : IDisposable
{
    private readonly ConcurrentDictionary<string, CommandInfo> _commandCache = new();
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly List<CommandScanDirectory> _scanDirectories = new();
    private bool _disposed;

    public CommandScannerService()
    {
        InitializeScanDirectories();
    }

    public void Initialize()
    {
        ScanAllDirectories();
        StartFileSystemWatchers();
    }

    public void ScanAllDirectories()
    {
        _commandCache.Clear();

        foreach (var directory in _scanDirectories.Where(d => Directory.Exists(d.Path)))
        {
            ScanDirectory(directory);
        }
    }

    public List<CommandInfo> GetAllCommands(string? toolId = null)
    {
        var normalizedToolId = NormalizeToolId(toolId);
        return _commandCache.Values
            .Where(c => normalizedToolId == null || c.ToolId.Equals(normalizedToolId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public List<CommandInfo> GetCommandsByCategory(string category, string? toolId = null)
    {
        var normalizedToolId = NormalizeToolId(toolId);
        return _commandCache.Values
            .Where(c => c.Category.Equals(category, StringComparison.OrdinalIgnoreCase))
            .Where(c => normalizedToolId == null || c.ToolId.Equals(normalizedToolId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private void InitializeScanDirectories()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var claudeGlobalSkills = Path.Combine(userProfile, ".claude", "skills");
        var claudeGlobalPlugins = Path.Combine(userProfile, ".claude", "plugins");
        var codexGlobalSkills = Path.Combine(userProfile, ".codex", "skills");
        var openCodeGlobalSkills = Path.Combine(userProfile, ".opencode", "skills");
        var openCodeGlobalCommands = Path.Combine(userProfile, ".opencode", "commands");
        var openCodeGlobalPlugins = Path.Combine(userProfile, ".opencode", "plugins");
        var openCodeConfigSkills = Path.Combine(userProfile, ".config", "opencode", "skills");
        var openCodeConfigCommands = Path.Combine(userProfile, ".config", "opencode", "commands");
        var openCodeConfigPlugins = Path.Combine(userProfile, ".config", "opencode", "plugins");

        AddScanDirectory(claudeGlobalSkills, "skills_global", "claude-code");
        AddScanDirectory(claudeGlobalPlugins, "plugins", "claude-code");
        AddScanDirectory(codexGlobalSkills, "skills_global", "codex");
        AddScanDirectory(openCodeGlobalSkills, "skills_global", "opencode");
        AddScanDirectory(openCodeGlobalCommands, "commands_global", "opencode");
        AddScanDirectory(openCodeGlobalPlugins, "plugins", "opencode");
        AddScanDirectory(openCodeConfigSkills, "skills_global", "opencode");
        AddScanDirectory(openCodeConfigCommands, "commands_global", "opencode");
        AddScanDirectory(openCodeConfigPlugins, "plugins", "opencode");

        var projectClaudeSkills = FindProjectDirectory("skills", "claude");
        var projectCodexSkills = FindProjectDirectory(".codex", "skills");
        var legacyProjectCodexSkills = FindProjectDirectory("skills", "codex");
        var projectOpenCodeSkills = FindProjectDirectory(".opencode", "skills");
        var projectOpenCodeCommands = FindProjectDirectory(".opencode", "commands");
        var projectOpenCodePlugins = FindProjectDirectory(".opencode", "plugins");

        if (!string.IsNullOrWhiteSpace(projectClaudeSkills))
        {
            AddScanDirectory(projectClaudeSkills, "skills_project", "claude-code");
        }

        if (!string.IsNullOrWhiteSpace(projectCodexSkills))
        {
            AddScanDirectory(projectCodexSkills, "skills_project", "codex");
        }

        if (!string.IsNullOrWhiteSpace(legacyProjectCodexSkills)
            && !string.Equals(legacyProjectCodexSkills, projectCodexSkills, StringComparison.OrdinalIgnoreCase))
        {
            AddScanDirectory(legacyProjectCodexSkills, "skills_project", "codex");
        }

        if (!string.IsNullOrWhiteSpace(projectOpenCodeSkills))
        {
            AddScanDirectory(projectOpenCodeSkills, "skills_project", "opencode");
        }

        if (!string.IsNullOrWhiteSpace(projectOpenCodeCommands))
        {
            AddScanDirectory(projectOpenCodeCommands, "commands_project", "opencode");
        }

        if (!string.IsNullOrWhiteSpace(projectOpenCodePlugins))
        {
            AddScanDirectory(projectOpenCodePlugins, "plugins", "opencode");
        }

        if (ShouldIncludeClaudeSkillsForOpenCode())
        {
            AddScanDirectory(claudeGlobalSkills, "skills_global", "opencode");
            if (!string.IsNullOrWhiteSpace(projectClaudeSkills))
            {
                AddScanDirectory(projectClaudeSkills, "skills_project", "opencode");
            }
        }
    }

    private void AddScanDirectory(string? path, string category, string toolId)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (_scanDirectories.Any(x =>
                x.Path.Equals(path, StringComparison.OrdinalIgnoreCase)
                && x.Category.Equals(category, StringComparison.OrdinalIgnoreCase)
                && x.ToolId.Equals(toolId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _scanDirectories.Add(new CommandScanDirectory(path, category, NormalizeToolId(toolId)!));
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

    private static string? FindProjectDirectory(params string[] segments)
    {
        var currentDir = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(currentDir))
        {
            var candidate = Path.Combine(new[] { currentDir }.Concat(segments).ToArray());
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            var parent = Directory.GetParent(currentDir);
            currentDir = parent?.FullName ?? string.Empty;
        }

        return null;
    }

    private void ScanDirectory(CommandScanDirectory directory)
    {
        var mdFiles = Directory.EnumerateFiles(directory.Path, "*.md", SearchOption.AllDirectories)
            .Where(IsSupportedCommandMarkdown);

        foreach (var file in mdFiles)
        {
            try
            {
                var commandInfo = ParseMarkdownDocument(file, directory);
                if (commandInfo != null)
                {
                    _commandCache[BuildCacheKey(commandInfo)] = commandInfo;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"解析命令文档失败 {file}: {ex.Message}");
            }
        }
    }

    private static bool IsSupportedCommandMarkdown(string filePath)
    {
        var fileName = Path.GetFileName(filePath);
        return fileName.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("COMMAND.md", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".command.md", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("README.md", StringComparison.OrdinalIgnoreCase);
    }

    private CommandInfo? ParseMarkdownDocument(string filePath, CommandScanDirectory directory)
    {
        var content = File.ReadAllText(filePath);
        var commandInfo = new CommandInfo
        {
            ToolId = directory.ToolId,
            SourcePath = filePath,
            LastUpdated = File.GetLastWriteTime(filePath),
            Category = directory.Category
        };

        var yamlMatch = Regex.Match(content, @"^---\s*\n(.*?)\n---\s*\n", RegexOptions.Singleline);
        if (yamlMatch.Success)
        {
            try
            {
                var yamlContent = yamlMatch.Groups[1].Value;
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(CamelCaseNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();

                var frontMatter = deserializer.Deserialize<SkillYamlFrontMatter>(yamlContent);
                commandInfo.Name = frontMatter.Name;
                commandInfo.Description = frontMatter.Description;
                commandInfo.Usage = frontMatter.Usage;

                content = content.Substring(yamlMatch.Length);
            }
            catch
            {
                // 忽略 front matter 解析错误，继续使用正文兜底
            }
        }

        if (string.IsNullOrWhiteSpace(commandInfo.Name))
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            commandInfo.Name = fileName.ToLowerInvariant().Replace(" ", "-");
        }

        if (string.IsNullOrWhiteSpace(commandInfo.Description))
        {
            var paragraphs = Regex.Split(content, @"\n\s*\n", RegexOptions.Multiline)
                .Where(p => !string.IsNullOrWhiteSpace(p) && !p.StartsWith("#"))
                .ToList();

            if (paragraphs.Any())
            {
                commandInfo.Description = paragraphs.First().Trim().Replace("\n", " ");
            }
        }

        if (string.IsNullOrWhiteSpace(commandInfo.Usage))
        {
            var codeBlockMatches = Regex.Matches(content, @"```(bash|shell)?\s*\n(.*?)\n```", RegexOptions.Singleline);
            var usages = new List<string>();
            foreach (Match match in codeBlockMatches)
            {
                usages.Add(match.Groups[2].Value.Trim());
            }

            commandInfo.Usage = string.Join("\n", usages.Take(3));
        }

        if (string.IsNullOrWhiteSpace(commandInfo.Name) || string.IsNullOrWhiteSpace(commandInfo.Description))
        {
            return null;
        }

        commandInfo.Invocation = BuildInvocationText(commandInfo.Name, directory.Category, filePath);
        if (string.IsNullOrWhiteSpace(commandInfo.Usage))
        {
            commandInfo.Usage = commandInfo.Invocation;
        }

        return commandInfo;
    }

    private static string BuildInvocationText(string name, string category, string filePath)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        if (name.StartsWith("/") || name.StartsWith("$"))
        {
            return name;
        }

        var fileName = Path.GetFileName(filePath);
        if (category.StartsWith("skills", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("SKILL.md", StringComparison.OrdinalIgnoreCase))
        {
            return $"${name}";
        }

        if (category.StartsWith("commands", StringComparison.OrdinalIgnoreCase)
            || category.Equals("plugins", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith("COMMAND.md", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".command.md", StringComparison.OrdinalIgnoreCase))
        {
            return $"/{name}";
        }

        return name;
    }

    private static string BuildCacheKey(CommandInfo commandInfo)
        => $"{NormalizeToolId(commandInfo.ToolId)}::{commandInfo.Name}::{commandInfo.SourcePath}";

    private static string? NormalizeToolId(string? toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return null;
        }

        if (toolId.Equals("claude", StringComparison.OrdinalIgnoreCase))
        {
            return "claude-code";
        }

        if (toolId.Equals("opencode-cli", StringComparison.OrdinalIgnoreCase))
        {
            return "opencode";
        }

        return toolId;
    }

    private void StartFileSystemWatchers()
    {
        foreach (var directory in _scanDirectories
                     .Select(d => d.Path)
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(Directory.Exists))
        {
            try
            {
                var watcher = new FileSystemWatcher(directory)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Size,
                    Filter = "*.md",
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true
                };

                watcher.Created += OnFileChanged;
                watcher.Changed += OnFileChanged;
                watcher.Deleted += OnFileChanged;
                watcher.Renamed += OnFileRenamed;

                _watchers.Add(watcher);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"启动目录监听失败 {directory}: {ex.Message}");
            }
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        try
        {
            Task.Delay(500).Wait();

            RemoveCachedEntriesForFile(e.FullPath);
            if (!File.Exists(e.FullPath) || !IsSupportedCommandMarkdown(e.FullPath))
            {
                return;
            }

            var matchedDirectories = _scanDirectories
                .Where(d => IsPathInsideDirectory(e.FullPath, d.Path))
                .ToList();

            foreach (var directory in matchedDirectories)
            {
                var commandInfo = ParseMarkdownDocument(e.FullPath, directory);
                if (commandInfo != null)
                {
                    _commandCache[BuildCacheKey(commandInfo)] = commandInfo;
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"处理文件变更失败 {e.FullPath}: {ex.Message}");
        }
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        RemoveCachedEntriesForFile(e.OldFullPath);
        OnFileChanged(sender, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(e.FullPath)!, Path.GetFileName(e.FullPath)));
    }

    private void RemoveCachedEntriesForFile(string filePath)
    {
        var keysToRemove = _commandCache
            .Where(c => c.Value.SourcePath.Equals(filePath, StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Key)
            .ToList();

        foreach (var key in keysToRemove)
        {
            _commandCache.TryRemove(key, out _);
        }
    }

    private static bool IsPathInsideDirectory(string filePath, string directoryPath)
    {
        var normalizedDirectory = Path.GetFullPath(directoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var normalizedFilePath = Path.GetFullPath(filePath);

        return normalizedFilePath.StartsWith(normalizedDirectory, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (disposing)
        {
            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
        }

        _disposed = true;
    }

    private sealed record CommandScanDirectory(string Path, string Category, string ToolId);
}
