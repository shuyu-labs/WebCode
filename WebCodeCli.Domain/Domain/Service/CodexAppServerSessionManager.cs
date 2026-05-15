using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service.Adapters;

namespace WebCodeCli.Domain.Domain.Service;

internal sealed class CodexAppServerSessionManager : ICodexAppServerSessionManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, AppServerSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private long _nextRequestId;
    private bool _disposed;

    public CodexAppServerSessionManager(ILogger logger)
    {
        _logger = logger;
    }

    public async Task<string> EnsureThreadAsync(
        string sessionId,
        string commandPath,
        CliToolConfig tool,
        string workingDirectory,
        Dictionary<string, string>? environmentVariables,
        CliSessionContext sessionContext,
        string? existingThreadId,
        CancellationToken cancellationToken = default)
    {
        var session = await GetOrCreateSessionAsync(
            sessionId,
            commandPath,
            tool,
            workingDirectory,
            environmentVariables,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(session.ThreadId))
        {
            return session.ThreadId!;
        }

        var threadId = !string.IsNullOrWhiteSpace(existingThreadId)
            ? await ResumeThreadAsync(session, existingThreadId!, sessionContext, cancellationToken)
            : await StartThreadAsync(session, sessionContext, cancellationToken);

        session.ThreadId = threadId;
        return threadId;
    }

    public async Task<AppServerTurnRun> StartTurnAsync(
        string sessionId,
        string commandPath,
        CliToolConfig tool,
        string workingDirectory,
        Dictionary<string, string>? environmentVariables,
        CliSessionContext sessionContext,
        string userPrompt,
        string? existingThreadId,
        CancellationToken cancellationToken = default)
    {
        var session = await GetOrCreateSessionAsync(
            sessionId,
            commandPath,
            tool,
            workingDirectory,
            environmentVariables,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(session.ActiveTurnId))
        {
            throw new InvalidOperationException("当前 app-server 运行中已有一个 turn，无法再启动新的 turn。");
        }

        var threadId = await EnsureThreadAsync(
            sessionId,
            commandPath,
            tool,
            workingDirectory,
            environmentVariables,
            sessionContext,
            existingThreadId,
            cancellationToken);

        var outputChannel = Channel.CreateUnbounded<StreamOutputChunk>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        session.ActiveOutputWriter = outputChannel.Writer;

        try
        {
            var requestParams = new Dictionary<string, object?>
            {
                ["threadId"] = threadId,
                ["input"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "text",
                        ["text"] = userPrompt
                    }
                }
            };

            if (!string.IsNullOrWhiteSpace(sessionContext.LaunchModelOverride))
            {
                requestParams["model"] = sessionContext.LaunchModelOverride;
            }

            if (!string.IsNullOrWhiteSpace(sessionContext.LaunchReasoningEffortOverride))
            {
                requestParams["effort"] = sessionContext.LaunchReasoningEffortOverride;
            }

            var turn = await SendRequestAsync<AppServerTurnResponse>(
                session,
                "turn/start",
                requestParams,
                cancellationToken);

            if (turn?.Turn == null || string.IsNullOrWhiteSpace(turn.Turn.Id))
            {
                throw new InvalidOperationException("Codex app-server 未返回 turn ID。");
            }

            session.ActiveTurnId = turn.Turn.Id;
            _logger.LogInformation(
                "Codex app-server turn 已启动: Session={SessionId}, Thread={ThreadId}, Turn={TurnId}",
                sessionId,
                threadId,
                session.ActiveTurnId);

            return new AppServerTurnRun(
                threadId,
                session.ActiveTurnId,
                outputChannel.Reader.ReadAllAsync(cancellationToken));
        }
        catch
        {
            session.ActiveOutputWriter = null;
            outputChannel.Writer.TryComplete();
            throw;
        }
    }

    public async Task<AppServerGoalSnapshot?> GetGoalAsync(
        string sessionId,
        string commandPath,
        CliToolConfig tool,
        string workingDirectory,
        Dictionary<string, string>? environmentVariables,
        CliSessionContext sessionContext,
        string? existingThreadId,
        CancellationToken cancellationToken = default)
    {
        var session = await GetOrCreateSessionAsync(
            sessionId,
            commandPath,
            tool,
            workingDirectory,
            environmentVariables,
            cancellationToken);

        var threadId = await EnsureThreadAsync(
            sessionId,
            commandPath,
            tool,
            workingDirectory,
            environmentVariables,
            sessionContext,
            existingThreadId,
            cancellationToken);

        var response = await SendRequestAsync<AppServerGoalGetResponse>(
            session,
            "thread/goal/get",
            new Dictionary<string, object?>
            {
                ["threadId"] = threadId
            },
            cancellationToken);

        return response?.Goal == null
            ? null
            : new AppServerGoalSnapshot(
                response.Goal.Objective,
                response.Goal.Status,
                response.Goal.TokenBudget,
                response.Goal.TokensUsed,
                response.Goal.TimeUsedSeconds);
    }

    public async Task<AppServerGoalSnapshot?> SetGoalAsync(
        string sessionId,
        string commandPath,
        CliToolConfig tool,
        string workingDirectory,
        Dictionary<string, string>? environmentVariables,
        CliSessionContext sessionContext,
        string objective,
        string status,
        long? tokenBudget,
        string? existingThreadId,
        CancellationToken cancellationToken = default)
    {
        var session = await GetOrCreateSessionAsync(
            sessionId,
            commandPath,
            tool,
            workingDirectory,
            environmentVariables,
            cancellationToken);

        var threadId = await EnsureThreadAsync(
            sessionId,
            commandPath,
            tool,
            workingDirectory,
            environmentVariables,
            sessionContext,
            existingThreadId,
            cancellationToken);

        var response = await SendRequestAsync<AppServerGoalSetResponse>(
            session,
            "thread/goal/set",
            new Dictionary<string, object?>
            {
                ["threadId"] = threadId,
                ["objective"] = objective,
                ["status"] = status,
                ["tokenBudget"] = tokenBudget
            },
            cancellationToken);

        return response?.Goal == null
            ? null
            : new AppServerGoalSnapshot(
                response.Goal.Objective,
                response.Goal.Status,
                response.Goal.TokenBudget,
                response.Goal.TokensUsed,
                response.Goal.TimeUsedSeconds);
    }

    public async Task<bool> ClearGoalAsync(
        string sessionId,
        string commandPath,
        CliToolConfig tool,
        string workingDirectory,
        Dictionary<string, string>? environmentVariables,
        CliSessionContext sessionContext,
        string? existingThreadId,
        CancellationToken cancellationToken = default)
    {
        var session = await GetOrCreateSessionAsync(
            sessionId,
            commandPath,
            tool,
            workingDirectory,
            environmentVariables,
            cancellationToken);

        var threadId = await EnsureThreadAsync(
            sessionId,
            commandPath,
            tool,
            workingDirectory,
            environmentVariables,
            sessionContext,
            existingThreadId,
            cancellationToken);

        var response = await SendRequestAsync<AppServerGoalClearResponse>(
            session,
            "thread/goal/clear",
            new Dictionary<string, object?>
            {
                ["threadId"] = threadId
            },
            cancellationToken);

        return response?.Cleared == true;
    }

    public async Task<bool> InterruptActiveTurnAsync(
        string sessionId,
        string commandPath,
        CliToolConfig tool,
        string workingDirectory,
        Dictionary<string, string>? environmentVariables,
        CliSessionContext sessionContext,
        string? existingThreadId,
        CancellationToken cancellationToken = default)
    {
        var session = await GetOrCreateSessionAsync(
            sessionId,
            commandPath,
            tool,
            workingDirectory,
            environmentVariables,
            cancellationToken);

        var threadId = await EnsureThreadAsync(
            sessionId,
            commandPath,
            tool,
            workingDirectory,
            environmentVariables,
            sessionContext,
            existingThreadId,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(session.ActiveTurnId))
        {
            return false;
        }

        await SendRequestAsync<JsonElement>(
            session,
            "turn/interrupt",
            new Dictionary<string, object?>
            {
                ["threadId"] = threadId,
                ["turnId"] = session.ActiveTurnId
            },
            cancellationToken);

        return true;
    }

    public async Task<bool> InterruptActiveTurnAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!_sessions.TryGetValue(sessionId, out var session) || !session.IsRunning)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(session.ThreadId) || string.IsNullOrWhiteSpace(session.ActiveTurnId))
        {
            return false;
        }

        await SendRequestAsync<JsonElement>(
            session,
            "turn/interrupt",
            new Dictionary<string, object?>
            {
                ["threadId"] = session.ThreadId,
                ["turnId"] = session.ActiveTurnId
            },
            cancellationToken);

        return true;
    }

    public bool CleanupSession(string sessionId)
    {
        if (_sessions.TryRemove(sessionId, out var session))
        {
            session.Dispose();
            return true;
        }

        return false;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var session in _sessions.Values)
        {
            session.Dispose();
        }

        _sessions.Clear();
    }

    private async Task<AppServerSession> GetOrCreateSessionAsync(
        string sessionId,
        string commandPath,
        CliToolConfig tool,
        string workingDirectory,
        Dictionary<string, string>? environmentVariables,
        CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(CodexAppServerSessionManager));
        }

        if (_sessions.TryGetValue(sessionId, out var existing))
        {
            if (existing.IsRunning)
            {
                return existing;
            }

            CleanupSession(sessionId);
        }

        var session = await CreateSessionAsync(
            sessionId,
            commandPath,
            tool,
            workingDirectory,
            environmentVariables,
            cancellationToken);

        _sessions[sessionId] = session;
        return session;
    }

    private async Task<AppServerSession> CreateSessionAsync(
        string sessionId,
        string commandPath,
        CliToolConfig tool,
        string workingDirectory,
        Dictionary<string, string>? environmentVariables,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = commandPath,
            Arguments = "app-server",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = workingDirectory
        };

        CodexLaunchCommandHelper.TryRewriteWindowsCodexCmdToNode(
            startInfo,
            commandPath,
            "app-server",
            () => CodexLaunchCommandHelper.ResolvePreferredNodeExecutablePath(_logger),
            _logger);

        var process = new Process
        {
            StartInfo = startInfo
        };

        if (environmentVariables != null)
        {
            foreach (var kvp in environmentVariables)
            {
                if (string.IsNullOrEmpty(kvp.Value))
                {
                    process.StartInfo.EnvironmentVariables.Remove(kvp.Key);
                    continue;
                }

                process.StartInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
            }
        }

        if (!process.Start())
        {
            throw new InvalidOperationException("启动 Codex app-server 进程失败。");
        }

        var session = new AppServerSession(sessionId, process);
        session.OutputReaderTask = Task.Run(() => ReadOutputLoopAsync(session, cancellationToken), CancellationToken.None);
        session.ErrorReaderTask = Task.Run(() => ReadErrorLoopAsync(session, cancellationToken), CancellationToken.None);

        await SendRequestAsync<AppServerInitializeResponse>(
            session,
            "initialize",
            new Dictionary<string, object?>
            {
                ["clientInfo"] = new Dictionary<string, object?>
                {
                    ["name"] = "WebCode",
                    ["version"] = "1.0.0",
                    ["title"] = "WebCode"
                },
                ["capabilities"] = new Dictionary<string, object?>
                {
                    ["experimentalApi"] = true
                }
            },
            cancellationToken);

        await SendNotificationAsync(
            session,
            "initialized",
            null,
            cancellationToken);

        _logger.LogInformation(
            "Codex app-server 会话已启动: Session={SessionId}, PID={ProcessId}",
            sessionId,
            process.Id);

        return session;
    }

    private async Task<string> StartThreadAsync(
        AppServerSession session,
        CliSessionContext sessionContext,
        CancellationToken cancellationToken)
    {
        var requestParams = new Dictionary<string, object?>
        {
            ["cwd"] = sessionContext.WorkingDirectory
        };

        if (!string.IsNullOrWhiteSpace(sessionContext.LaunchModelOverride))
        {
            requestParams["model"] = sessionContext.LaunchModelOverride;
        }

        var response = await SendRequestAsync<AppServerThreadResponse>(
            session,
            "thread/start",
            requestParams,
            cancellationToken);

        if (response?.Thread == null || string.IsNullOrWhiteSpace(response.Thread.Id))
        {
            throw new InvalidOperationException("Codex app-server 未返回线程 ID。");
        }

        return response.Thread.Id;
    }

    private async Task<string> ResumeThreadAsync(
        AppServerSession session,
        string threadId,
        CliSessionContext sessionContext,
        CancellationToken cancellationToken)
    {
        var requestParams = new Dictionary<string, object?>
        {
            ["threadId"] = threadId,
            ["excludeTurns"] = true
        };

        if (!string.IsNullOrWhiteSpace(sessionContext.LaunchModelOverride))
        {
            requestParams["model"] = sessionContext.LaunchModelOverride;
        }

        var response = await SendRequestAsync<AppServerThreadResponse>(
            session,
            "thread/resume",
            requestParams,
            cancellationToken);

        if (response?.Thread == null || string.IsNullOrWhiteSpace(response.Thread.Id))
        {
            throw new InvalidOperationException("Codex app-server 未返回可恢复的线程 ID。");
        }

        return response.Thread.Id;
    }

    private async Task<TResponse> SendRequestAsync<TResponse>(
        AppServerSession session,
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var requestId = Interlocked.Increment(ref _nextRequestId);
        var request = new Dictionary<string, object?>
        {
            ["id"] = requestId,
            ["method"] = method
        };

        if (parameters != null)
        {
            request["params"] = parameters;
        }

        var payload = JsonSerializer.Serialize(request, JsonOptions);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.PendingResponses[requestId.ToString(System.Globalization.CultureInfo.InvariantCulture)] = tcs;

        await session.WriteLock.WaitAsync(cancellationToken);
        try
        {
            try
            {
                await session.Process.StandardInput.WriteLineAsync(payload);
                await session.Process.StandardInput.FlushAsync();
            }
            catch
            {
                session.PendingResponses.TryRemove(requestId.ToString(System.Globalization.CultureInfo.InvariantCulture), out _);
                throw;
            }
        }
        finally
        {
            session.WriteLock.Release();
        }

        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            tcs.TrySetCanceled(cancellationToken);
        });

        var result = await tcs.Task;
        return result.ValueKind == JsonValueKind.Undefined
            ? default!
            : JsonSerializer.Deserialize<TResponse>(result.GetRawText(), JsonOptions)!;
    }

    private async Task SendNotificationAsync(
        AppServerSession session,
        string method,
        object? parameters,
        CancellationToken cancellationToken)
    {
        var request = new Dictionary<string, object?>
        {
            ["method"] = method
        };

        if (parameters != null)
        {
            request["params"] = parameters;
        }

        var payload = JsonSerializer.Serialize(request, JsonOptions);
        await session.WriteLock.WaitAsync(cancellationToken);
        try
        {
            await session.Process.StandardInput.WriteLineAsync(payload);
            await session.Process.StandardInput.FlushAsync();
        }
        finally
        {
            session.WriteLock.Release();
        }
    }

    private async Task ReadOutputLoopAsync(AppServerSession session, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !session.Process.HasExited)
            {
                var line = await session.Process.StandardOutput.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    break;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                HandleLine(session, line);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 Codex app-server 输出失败: Session={SessionId}", session.SessionId);
        }
        finally
        {
            FailPendingRequests(session, new IOException("Codex app-server 输出流已关闭。"));
            CompleteActiveOutput(session, errorMessage: "Codex app-server 输出流已关闭。");
        }
    }

    private async Task ReadErrorLoopAsync(AppServerSession session, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && !session.Process.HasExited)
            {
                var line = await session.Process.StandardError.ReadLineAsync(cancellationToken);
                if (line == null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    _logger.LogDebug("Codex app-server stderr: {Line}", line);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取 Codex app-server 错误输出失败: Session={SessionId}", session.SessionId);
        }
    }

    private void HandleLine(AppServerSession session, string line)
    {
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            _logger.LogDebug("忽略无法解析的 app-server 行: {Line}", line);
            return;
        }

        using (document)
        {
            var root = document.RootElement;

            if (root.TryGetProperty("id", out var idElement))
            {
                var requestId = idElement.ToString();
                if (root.TryGetProperty("error", out var errorElement))
                {
                    if (session.PendingResponses.TryRemove(requestId, out var pendingError))
                    {
                        var message = errorElement.TryGetProperty("message", out var messageElement)
                            ? messageElement.GetString() ?? "Codex app-server 请求失败。"
                            : "Codex app-server 请求失败。";
                        pendingError.TrySetException(new InvalidOperationException(message));
                    }

                    return;
                }

                if (root.TryGetProperty("result", out var resultElement))
                {
                    if (session.PendingResponses.TryRemove(requestId, out var pendingResult))
                    {
                        pendingResult.TrySetResult(resultElement.Clone());
                    }

                    return;
                }
            }

            if (!root.TryGetProperty("method", out var methodElement))
            {
                return;
            }

            var method = methodElement.GetString();
            if (string.IsNullOrWhiteSpace(method))
            {
                return;
            }

            var parameters = root.TryGetProperty("params", out var paramsElement)
                ? paramsElement
                : default;

            HandleNotification(session, method, parameters);
        }
    }

    private void HandleNotification(AppServerSession session, string method, JsonElement parameters)
    {
        switch (method)
        {
            case "thread/started":
                if (TryGetString(parameters, "thread", "id", out var threadId))
                {
                    session.ThreadId = threadId;
                }
                break;
            case "thread/goal/updated":
                break;
            case "thread/goal/cleared":
                break;
            case "turn/started":
                if (TryGetString(parameters, "turn", "id", out var turnId))
                {
                    session.ActiveTurnId = turnId;
                }
                break;
            case "turn/completed":
            case "turn/failed":
                if (TryGetString(parameters, "turn", "id", out var completedTurnId))
                {
                    session.ActiveTurnId = null;
                }

                CompleteActiveOutput(session, errorMessage: method == "turn/failed" ? "Codex turn 执行失败。" : null);
                break;
            case "item/agentMessage/delta":
                AppendDelta(session, parameters, "delta");
                break;
            case "command/exec/outputDelta":
            case "process/outputDelta":
            case "item/commandExecution/outputDelta":
            case "item/fileChange/outputDelta":
            case "item/mcpToolCall/progress":
                AppendDelta(session, parameters, "delta");
                break;
            case "error":
                CompleteActiveOutput(session, errorMessage: TryGetString(parameters, "message", out var message) ? message : "Codex app-server 返回了错误。");
                break;
        }
    }

    private void AppendDelta(AppServerSession session, JsonElement parameters, string propertyName)
    {
        if (!TryGetString(parameters, propertyName, out var delta) || string.IsNullOrEmpty(delta))
        {
            return;
        }

        if (session.ActiveOutputWriter == null)
        {
            return;
        }

        session.ActiveOutputWriter.TryWrite(new StreamOutputChunk
        {
            Content = delta,
            IsCompleted = false,
            IsError = false
        });
    }

    private static bool TryGetString(JsonElement parameters, string propertyName, out string? value)
    {
        if (parameters.ValueKind == JsonValueKind.Undefined
            || parameters.ValueKind == JsonValueKind.Null)
        {
            value = null;
            return false;
        }

        if (parameters.TryGetProperty(propertyName, out var element))
        {
            value = element.GetString();
            return !string.IsNullOrWhiteSpace(value);
        }

        value = null;
        return false;
    }

    private static bool TryGetString(JsonElement parameters, string parentPropertyName, string childPropertyName, out string? value)
    {
        value = null;
        if (parameters.ValueKind == JsonValueKind.Undefined
            || parameters.ValueKind == JsonValueKind.Null
            || !parameters.TryGetProperty(parentPropertyName, out var parentElement))
        {
            return false;
        }

        if (!parentElement.TryGetProperty(childPropertyName, out var element))
        {
            return false;
        }

        value = element.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }

    private void FailPendingRequests(AppServerSession session, Exception exception)
    {
        foreach (var entry in session.PendingResponses)
        {
            if (session.PendingResponses.TryRemove(entry.Key, out var pending))
            {
                pending.TrySetException(exception);
            }
        }
    }

    private void CompleteActiveOutput(AppServerSession session, string? errorMessage)
    {
        var writer = session.ActiveOutputWriter;
        if (writer == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            writer.TryWrite(new StreamOutputChunk
            {
                Content = string.Empty,
                IsError = true,
                IsCompleted = true,
                ErrorMessage = errorMessage
            });
        }
        else
        {
            writer.TryWrite(new StreamOutputChunk
            {
                Content = string.Empty,
                IsError = false,
                IsCompleted = true
            });
        }

        writer.TryComplete();
        session.ActiveOutputWriter = null;
    }

    private sealed class AppServerSession : IDisposable
    {
        public AppServerSession(string sessionId, Process process)
        {
            SessionId = sessionId;
            Process = process;
        }

        public string SessionId { get; }
        public Process Process { get; }
        public ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> PendingResponses { get; } = new(StringComparer.Ordinal);
        public SemaphoreSlim WriteLock { get; } = new(1, 1);
        public Task? OutputReaderTask { get; set; }
        public Task? ErrorReaderTask { get; set; }
        public string? ThreadId { get; set; }
        public string? ActiveTurnId { get; set; }
        public ChannelWriter<StreamOutputChunk>? ActiveOutputWriter { get; set; }

        public bool IsRunning => !Process.HasExited;

        public void Dispose()
        {
            try
            {
                ActiveOutputWriter?.TryComplete();
            }
            catch
            {
            }

            try
            {
                if (!Process.HasExited)
                {
                    Process.Kill(true);
                    Process.WaitForExit(5000);
                }
            }
            catch
            {
            }
            finally
            {
                Process.Dispose();
                WriteLock.Dispose();
            }
        }
    }
}

public sealed record AppServerTurnRun(
    string ThreadId,
    string TurnId,
    IAsyncEnumerable<StreamOutputChunk> Output);

public sealed record AppServerGoalSnapshot(
    string Objective,
    string Status,
    long? TokenBudget,
    long TokensUsed,
    long TimeUsedSeconds);

internal sealed record AppServerThreadResponse(AppServerThread? Thread);

internal sealed record AppServerTurnResponse(AppServerTurn? Turn);

internal sealed record AppServerGoalGetResponse(AppServerGoal? Goal);

internal sealed record AppServerGoalSetResponse(AppServerGoal? Goal);

internal sealed record AppServerGoalClearResponse(bool Cleared);

internal sealed record AppServerInitializeResponse(
    string CodexHome,
    string PlatformFamily,
    string PlatformOs,
    string UserAgent);

internal sealed record AppServerThread(
    string Id);

internal sealed record AppServerTurn(
    string Id);

internal sealed record AppServerGoal(
    string Objective,
    string Status,
    string ThreadId,
    long TimeUsedSeconds,
    long TokensUsed,
    long? TokenBudget);

