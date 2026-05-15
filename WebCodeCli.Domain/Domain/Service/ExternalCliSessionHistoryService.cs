using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service;

public interface IExternalCliSessionHistoryService
{
    /// <summary>
    /// 读取当前系统账户下外部 CLI 原生会话的最近历史消息
    /// </summary>
    Task<ExternalCliHistoryResult> GetRecentHistoryAsync(
        string toolId,
        string cliThreadId,
        int maxCount = 20,
        string? workspacePath = null,
        CancellationToken cancellationToken = default);

    Task<List<ExternalCliHistoryMessage>> GetRecentMessagesAsync(
        string toolId,
        string cliThreadId,
        int maxCount = 20,
        string? workspacePath = null,
        CancellationToken cancellationToken = default);
}

[ServiceDescription(typeof(IExternalCliSessionHistoryService), ServiceLifetime.Scoped)]
public class ExternalCliSessionHistoryService : IExternalCliSessionHistoryService
{
    private readonly ILogger<ExternalCliSessionHistoryService> _logger;

    public ExternalCliSessionHistoryService(ILogger<ExternalCliSessionHistoryService> logger)
    {
        _logger = logger;
    }

    public async Task<ExternalCliHistoryResult> GetRecentHistoryAsync(
        string toolId,
        string cliThreadId,
        int maxCount = 20,
        string? workspacePath = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toolId) || string.IsNullOrWhiteSpace(cliThreadId))
        {
            return new ExternalCliHistoryResult();
        }

        var normalizedToolId = NormalizeToolId(toolId);
        var normalizedThreadId = cliThreadId.Trim();
        var effectiveMaxCount = maxCount <= 0 ? 20 : maxCount;

        try
        {
            var messages = await GetRecentMessagesAsync(
                normalizedToolId,
                normalizedThreadId,
                effectiveMaxCount,
                workspacePath,
                cancellationToken);

            var sourcePath = normalizedToolId switch
            {
                "codex" => FindCodexRolloutFile(normalizedThreadId, workspacePath, cancellationToken),
                "claude-code" => FindClaudeTranscriptFile(normalizedThreadId, cancellationToken),
                "opencode" => $"opencode export {normalizedThreadId}",
                _ => null
            };

            return new ExternalCliHistoryResult
            {
                Messages = messages,
                SourcePath = sourcePath
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "读取外部 CLI 历史结果失败: ToolId={ToolId}, CliThreadId={CliThreadId}",
                normalizedToolId,
                normalizedThreadId);
            return new ExternalCliHistoryResult();
        }
    }

    public async Task<List<ExternalCliHistoryMessage>> GetRecentMessagesAsync(
        string toolId,
        string cliThreadId,
        int maxCount = 20,
        string? workspacePath = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(toolId) || string.IsNullOrWhiteSpace(cliThreadId))
        {
            return [];
        }

        var normalizedToolId = NormalizeToolId(toolId);
        var normalizedThreadId = cliThreadId.Trim();
        var effectiveMaxCount = maxCount <= 0 ? 20 : maxCount;

        try
        {
            var messages = normalizedToolId switch
            {
                "codex" => await GetCodexMessagesAsync(normalizedThreadId, effectiveMaxCount, workspacePath, cancellationToken),
                "claude-code" => await GetClaudeCodeMessagesAsync(normalizedThreadId, effectiveMaxCount, cancellationToken),
                "opencode" => await GetOpenCodeMessagesAsync(normalizedThreadId, effectiveMaxCount, cancellationToken),
                _ => []
            };

            return messages
                .Where(message => !string.IsNullOrWhiteSpace(message.Role) && !string.IsNullOrWhiteSpace(message.Content))
                .OrderBy(message => message.CreatedAt ?? DateTime.MinValue)
                .TakeLast(effectiveMaxCount)
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "读取外部 CLI 原生历史失败: ToolId={ToolId}, CliThreadId={CliThreadId}",
                normalizedToolId,
                normalizedThreadId);
            return [];
        }
    }

    protected virtual string? GetCodexConfigRootPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? null
            : Path.Combine(userProfile, ".codex");
    }

    protected virtual string? GetClaudeProjectsRootPath()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(userProfile)
            ? null
            : Path.Combine(userProfile, ".claude", "projects");
    }

    protected virtual async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var waitForExitTask = process.WaitForExitAsync(cancellationToken);

        var completedTask = await Task.WhenAny(waitForExitTask, Task.Delay(timeout, cancellationToken));
        if (completedTask != waitForExitTask)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignore
            }

            return (-1, string.Empty, $"Process timeout after {timeout.TotalSeconds:F0}s");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    private async Task<List<ExternalCliHistoryMessage>> GetCodexMessagesAsync(
        string cliThreadId,
        int maxCount,
        string? workspacePath,
        CancellationToken cancellationToken)
    {
        var filePath = FindCodexRolloutFile(cliThreadId, workspacePath, cancellationToken);
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return [];
        }

        return await ParseCodexRolloutFileAsync(filePath, maxCount, cancellationToken);
    }

    private async Task<List<ExternalCliHistoryMessage>> GetClaudeCodeMessagesAsync(
        string cliThreadId,
        int maxCount,
        CancellationToken cancellationToken)
    {
        var transcriptPath = FindClaudeTranscriptFile(cliThreadId, cancellationToken);
        if (string.IsNullOrWhiteSpace(transcriptPath) || !File.Exists(transcriptPath))
        {
            return [];
        }

        return await ParseClaudeTranscriptFileAsync(transcriptPath, maxCount, cancellationToken);
    }

    private async Task<List<ExternalCliHistoryMessage>> GetOpenCodeMessagesAsync(
        string cliThreadId,
        int maxCount,
        CancellationToken cancellationToken)
    {
        var escapedSessionId = cliThreadId.Replace("\"", "\\\"", StringComparison.Ordinal);
        var (exitCode, stdout, stderr) = await RunProcessAsync(
            "opencode",
            $"export {escapedSessionId}",
            TimeSpan.FromSeconds(15),
            cancellationToken);

        if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
        {
            _logger.LogDebug(
                "OpenCode export 失败: ExitCode={ExitCode}, CliThreadId={CliThreadId}, Stderr={Stderr}",
                exitCode,
                cliThreadId,
                Truncate(stderr, 400));
            return [];
        }

        return ParseOpenCodeExport(stdout, maxCount);
    }

    private string? FindCodexRolloutFile(string cliThreadId, string? workspacePath, CancellationToken cancellationToken)
    {
        try
        {
            var candidates = GetCodexSessionsRootCandidates(workspacePath).ToList();
            _logger.LogInformation(
                "[CodexHistory] Start resolving rollout: CliThreadId={CliThreadId}, WorkspacePath={WorkspacePath}, Roots={Roots}",
                cliThreadId,
                workspacePath,
                string.Join(" | ", candidates.Select(candidate => $"{candidate.Scope}:{candidate.Path}")));

            foreach (var sessionsRoot in candidates)
            {
                var directCandidates = Directory
                    .EnumerateFiles(sessionsRoot.Path, $"*{cliThreadId}*.jsonl", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc)
                    .ToList();

                if (directCandidates.Count > 0)
                {
                    var rolloutPath = directCandidates[0];
                    LogCodexRolloutResolved(cliThreadId, workspacePath, sessionsRoot, rolloutPath, "filename", directCandidates.Count);
                    return rolloutPath;
                }

                foreach (var file in Directory.EnumerateFiles(sessionsRoot.Path, "rollout-*.jsonl", SearchOption.AllDirectories))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var firstLine = ReadFirstNonEmptyLine(file, maxLines: 3);
                    if (string.IsNullOrWhiteSpace(firstLine))
                    {
                        continue;
                    }

                    try
                    {
                        using var document = JsonDocument.Parse(firstLine);
                        var root = document.RootElement;
                        if (!TryGetProperty(root, "payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var sessionId = GetString(payload, "id");
                        if (string.Equals(sessionId, cliThreadId, StringComparison.OrdinalIgnoreCase))
                        {
                            LogCodexRolloutResolved(cliThreadId, workspacePath, sessionsRoot, file, "payload.id", directCandidateCount: 0);
                            return file;
                        }
                    }
                    catch
                    {
                        // ignore broken lines
                    }
                }
            }

            _logger.LogWarning(
                "[CodexHistory] Rollout not found: CliThreadId={CliThreadId}, WorkspacePath={WorkspacePath}, Roots={Roots}",
                cliThreadId,
                workspacePath,
                string.Join(" | ", candidates.Select(candidate => $"{candidate.Scope}:{candidate.Path}")));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(
                ex,
                "[CodexHistory] Resolve rollout failed: CliThreadId={CliThreadId}, WorkspacePath={WorkspacePath}",
                cliThreadId,
                workspacePath);
        }

        return null;
    }

    private string? FindClaudeTranscriptFile(string cliThreadId, CancellationToken cancellationToken)
    {
        var projectsRoot = GetClaudeProjectsRootPath();
        if (string.IsNullOrWhiteSpace(projectsRoot) || !Directory.Exists(projectsRoot))
        {
            return null;
        }

        try
        {
            foreach (var indexFile in Directory.EnumerateFiles(projectsRoot, "sessions-index.json", SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var json = ReadAllTextShared(indexFile);
                    if (string.IsNullOrWhiteSpace(json))
                    {
                        continue;
                    }

                    using var document = JsonDocument.Parse(json);
                    if (!TryGetProperty(document.RootElement, "entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var entry in entries.EnumerateArray())
                    {
                        if (entry.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        var sessionId = GetString(entry, "sessionId", "session_id");
                        if (!string.Equals(sessionId, cliThreadId, StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        var fullPath = GetString(entry, "fullPath", "full_path");
                        if (!string.IsNullOrWhiteSpace(fullPath) && File.Exists(fullPath))
                        {
                            return fullPath;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "读取 Claude sessions-index.json 失败，继续尝试直接定位 transcript: {File}", indexFile);
                }
            }

            var transcriptCandidates = Directory
                .EnumerateFiles(projectsRoot, $"{cliThreadId}.jsonl", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}subagents{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .ToList();

            return transcriptCandidates.FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "定位 Claude Code transcript 文件失败");
            return null;
        }
    }

    private async Task<List<ExternalCliHistoryMessage>> ParseCodexRolloutFileAsync(
        string filePath,
        int maxCount,
        CancellationToken cancellationToken)
    {
        var messages = new List<ExternalCliHistoryMessage>();

        await foreach (var line in ReadLinesAsync(filePath, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (!string.Equals(GetString(root, "type"), "response_item", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TryGetProperty(root, "payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!string.Equals(GetString(payload, "type"), "message", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var role = GetString(payload, "role");
                if (!IsSupportedRole(role))
                {
                    continue;
                }

                var content = ExtractCodexMessageContent(payload);
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                messages.Add(new ExternalCliHistoryMessage
                {
                    Role = role!,
                    Content = content,
                    CreatedAt = GetDateTime(root, "timestamp") ?? GetDateTime(payload, "timestamp"),
                    RawType = "codex.message"
                });
            }
            catch (JsonException)
            {
                // ignore bad lines
            }
        }

        return messages.TakeLast(maxCount).ToList();
    }

    private async Task<List<ExternalCliHistoryMessage>> ParseClaudeTranscriptFileAsync(
        string filePath,
        int maxCount,
        CancellationToken cancellationToken)
    {
        var messages = new List<ExternalCliHistoryMessage>();

        await foreach (var line in ReadLinesAsync(filePath, cancellationToken))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                var recordType = GetString(root, "type");
                if (!IsSupportedRole(recordType))
                {
                    continue;
                }

                if (!TryGetProperty(root, "message", out var messageElement) || messageElement.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var role = GetString(messageElement, "role") ?? recordType;
                if (!IsSupportedRole(role))
                {
                    continue;
                }

                var content = ExtractClaudeMessageContent(messageElement);
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                messages.Add(new ExternalCliHistoryMessage
                {
                    Role = role!,
                    Content = content,
                    CreatedAt = GetDateTime(root, "timestamp"),
                    RawType = $"claude.{recordType}"
                });
            }
            catch (JsonException)
            {
                // ignore bad lines
            }
        }

        return messages.TakeLast(maxCount).ToList();
    }

    private List<ExternalCliHistoryMessage> ParseOpenCodeExport(string json, int maxCount)
    {
        var messages = new List<ExternalCliHistoryMessage>();

        try
        {
            using var document = JsonDocument.Parse(json);
            if (!TryGetProperty(document.RootElement, "messages", out var items) || items.ValueKind != JsonValueKind.Array)
            {
                return messages;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!TryGetProperty(item, "info", out var info) || info.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var role = GetString(info, "role");
                if (!IsSupportedRole(role))
                {
                    continue;
                }

                if (!TryGetProperty(item, "parts", out var parts) || parts.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var content = ExtractTextParts(parts, "text");
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                DateTime? createdAt = null;
                if (TryGetProperty(info, "time", out var timeElement) && timeElement.ValueKind == JsonValueKind.Object)
                {
                    createdAt = GetDateTime(timeElement, "created");
                }

                messages.Add(new ExternalCliHistoryMessage
                {
                    Role = role!,
                    Content = content,
                    CreatedAt = createdAt,
                    RawType = "opencode.message"
                });
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "解析 OpenCode export JSON 失败");
        }

        return messages.TakeLast(maxCount).ToList();
    }

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

        return toolId.Trim();
    }

    private static bool IsSupportedRole(string? role)
    {
        return string.Equals(role, "user", StringComparison.OrdinalIgnoreCase)
               || string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractCodexMessageContent(JsonElement payload)
    {
        if (!TryGetProperty(payload, "content", out var contentElement))
        {
            return string.Empty;
        }

        if (contentElement.ValueKind == JsonValueKind.String)
        {
            return contentElement.GetString() ?? string.Empty;
        }

        if (contentElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return ExtractTextParts(contentElement, "input_text", "output_text", "text");
    }

    private static string ExtractClaudeMessageContent(JsonElement messageElement)
    {
        if (!TryGetProperty(messageElement, "content", out var contentElement))
        {
            return string.Empty;
        }

        if (contentElement.ValueKind == JsonValueKind.String)
        {
            return contentElement.GetString() ?? string.Empty;
        }

        if (contentElement.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        return ExtractTextParts(contentElement, "text");
    }

    private static string ExtractTextParts(JsonElement items, params string[] supportedPartTypes)
    {
        if (items.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var texts = new List<string>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var textValue = item.GetString();
                if (!string.IsNullOrWhiteSpace(textValue))
                {
                    texts.Add(textValue.Trim());
                }

                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = GetString(item, "type");
            if (string.IsNullOrWhiteSpace(type)
                || !supportedPartTypes.Any(candidate => string.Equals(candidate, type, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var text = GetString(item, "text", "content");
            if (!string.IsNullOrWhiteSpace(text))
            {
                texts.Add(text.Trim());
            }
        }

        return string.Join("\n", texts.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    private static string? ReadFirstNonEmptyLine(string filePath, int maxLines)
    {
        using var stream = OpenSharedReadStream(filePath);
        using var reader = new StreamReader(stream);
        for (var index = 0; index < maxLines && !reader.EndOfStream; index++)
        {
            var line = reader.ReadLine();
            if (!string.IsNullOrWhiteSpace(line))
            {
                return line;
            }
        }

        return null;
    }

    private void LogCodexRolloutResolved(
        string cliThreadId,
        string? workspacePath,
        CodexSessionsRootCandidate sessionsRoot,
        string rolloutPath,
        string matchKind,
        int directCandidateCount)
    {
        var metadata = ReadCodexRolloutMetadata(rolloutPath);
        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(rolloutPath);
        _logger.LogInformation(
            "[CodexHistory] Rollout resolved: CliThreadId={CliThreadId}, Scope={Scope}, MatchKind={MatchKind}, DirectCandidateCount={DirectCandidateCount}, RolloutPath={RolloutPath}, RootPath={RootPath}, LastWriteTimeUtc={LastWriteTimeUtc:O}, FirstLineThreadId={FirstLineThreadId}, FirstLineModelProvider={FirstLineModelProvider}, FirstLineCwd={FirstLineCwd}, WorkspacePath={WorkspacePath}",
            cliThreadId,
            sessionsRoot.Scope,
            matchKind,
            directCandidateCount,
            rolloutPath,
            sessionsRoot.Path,
            lastWriteTimeUtc,
            metadata.ThreadId,
            metadata.ModelProvider,
            metadata.Cwd,
            workspacePath);
    }

    private static CodexRolloutMetadata ReadCodexRolloutMetadata(string rolloutPath)
    {
        try
        {
            var firstLine = ReadFirstNonEmptyLine(rolloutPath, maxLines: 3);
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                return new CodexRolloutMetadata(null, null, null);
            }

            using var document = JsonDocument.Parse(firstLine);
            if (!TryGetProperty(document.RootElement, "payload", out var payload)
                || payload.ValueKind != JsonValueKind.Object)
            {
                return new CodexRolloutMetadata(null, null, null);
            }

            return new CodexRolloutMetadata(
                GetString(payload, "id"),
                GetString(payload, "model_provider"),
                GetString(payload, "cwd"));
        }
        catch
        {
            return new CodexRolloutMetadata(null, null, null);
        }
    }

    private IEnumerable<CodexSessionsRootCandidate> GetCodexSessionsRootCandidates(string? workspacePath)
    {
        var yielded = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var workspaceSessionsRoot in GetWorkspaceCodexSessionsRootPaths(workspacePath))
        {
            if (!Directory.Exists(workspaceSessionsRoot))
            {
                continue;
            }

            var normalized = Path.GetFullPath(workspaceSessionsRoot);
            if (yielded.Add(normalized))
            {
                yield return new CodexSessionsRootCandidate(normalized, "workspace");
            }
        }

        foreach (var globalSessionsRoot in GetGlobalCodexSessionsRootPaths())
        {
            if (!Directory.Exists(globalSessionsRoot))
            {
                continue;
            }

            var normalized = Path.GetFullPath(globalSessionsRoot);
            if (yielded.Add(normalized))
            {
                yield return new CodexSessionsRootCandidate(normalized, "global");
            }
        }
    }

    private sealed record CodexSessionsRootCandidate(string Path, string Scope);

    private sealed record CodexRolloutMetadata(string? ThreadId, string? ModelProvider, string? Cwd);

    private static IEnumerable<string> GetWorkspaceCodexSessionsRootPaths(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath))
        {
            yield break;
        }

        var codexRoot = Path.Combine(workspacePath, ".codex");
        yield return Path.Combine(codexRoot, "sessions");
        yield return Path.Combine(codexRoot, "archived_sessions");
    }

    private IEnumerable<string> GetGlobalCodexSessionsRootPaths()
    {
        var configuredRoot = GetCodexConfigRootPath();
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            yield break;
        }

        var normalizedRoot = configuredRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leafName = Path.GetFileName(normalizedRoot);

        if (string.Equals(leafName, "sessions", StringComparison.OrdinalIgnoreCase)
            || string.Equals(leafName, "archived_sessions", StringComparison.OrdinalIgnoreCase))
        {
            yield return normalizedRoot;

            var siblingRoot = Path.Combine(Path.GetDirectoryName(normalizedRoot)!, "archived_sessions");
            if (!string.Equals(normalizedRoot, siblingRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                yield return siblingRoot;
            }

            yield break;
        }

        var directRolloutExists = Directory.Exists(normalizedRoot)
                                 && Directory.EnumerateFiles(normalizedRoot, "rollout-*.jsonl", SearchOption.TopDirectoryOnly).Any();
        if (directRolloutExists)
        {
            yield return normalizedRoot;
            yield break;
        }

        yield return Path.Combine(normalizedRoot, "sessions");
        yield return Path.Combine(normalizedRoot, "archived_sessions");
    }

    private static string ReadAllTextShared(string filePath)
    {
        using var stream = OpenSharedReadStream(filePath);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(
        string filePath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var stream = OpenSharedReadStream(filePath);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line != null)
            {
                yield return line;
            }
        }
    }

    private static FileStream OpenSharedReadStream(string filePath)
    {
        return new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }

            if (value.ValueKind == JsonValueKind.Number || value.ValueKind == JsonValueKind.True || value.ValueKind == JsonValueKind.False)
            {
                return value.ToString();
            }
        }

        return null;
    }

    private static DateTime? GetDateTime(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out var value))
            {
                continue;
            }

            if (value.ValueKind == JsonValueKind.String && DateTime.TryParse(value.GetString(), out var parsedDateTime))
            {
                return parsedDateTime;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var unixValue))
            {
                if (unixValue > 10_000_000_000)
                {
                    return DateTimeOffset.FromUnixTimeMilliseconds(unixValue).LocalDateTime;
                }

                return DateTimeOffset.FromUnixTimeSeconds(unixValue).LocalDateTime;
            }
        }

        return null;
    }

    private static string Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length <= maxLength)
        {
            return value ?? string.Empty;
        }

        return value[..maxLength] + "...";
    }
}
