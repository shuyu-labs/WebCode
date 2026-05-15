using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebCodeCli.Domain.Common;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service.Adapters;
using WebCodeCli.Domain.Repositories.Base.ChatSession;
using WebCodeCli.Domain.Repositories.Base.SystemSettings;

namespace WebCodeCli.Domain.Domain.Service;

/// <summary>
/// CLI 执行服务实现
/// </summary>
[ServiceDescription(typeof(ICliExecutorService), ServiceLifetime.Singleton)]
public class CliExecutorService : ICliExecutorService
{
    private const string CodexLaunchBaseConfigFileName = "config.webcode.base.toml";

    private readonly ILogger<CliExecutorService> _logger;
    private readonly CliToolsOption _options;
    private readonly SemaphoreSlim _concurrencyLimiter;
    private readonly Dictionary<string, string> _sessionWorkspaces = new();
    private readonly object _workspaceLock = new();
    private readonly PersistentProcessManager _processManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly IChatSessionService _chatSessionService;
    private readonly ICliAdapterFactory _adapterFactory;
    private readonly ICcSwitchService _ccSwitchService;
    private readonly IGoalCapabilityService _goalCapabilityService;
    
    // 缓存的有效工作区根目录
    private string? _effectiveWorkspaceRoot;
    private readonly object _workspaceRootLock = new();

    // 存储每个会话的CLI Thread ID（适用于所有CLI工具）
    private readonly Dictionary<string, string> _cliThreadIds = new();
    private readonly object _cliSessionLock = new();

    // Codex 配置文件缓存（避免每次执行都重新生成）
    private string? _lastCodexConfigHash;
    private readonly object _codexConfigLock = new();

    // Windows 下为 Claude Code 选择可用的较高版本 Node.js
    private string? _preferredNodeExecutablePath;
    private readonly object _preferredNodeExecutableLock = new();

    public CliExecutorService(
        ILogger<CliExecutorService> logger,
        IOptions<CliToolsOption> options,
        ILogger<PersistentProcessManager> processManagerLogger,
        IServiceProvider serviceProvider,
        IChatSessionService chatSessionService,
        ICliAdapterFactory adapterFactory,
        ICcSwitchService ccSwitchService,
        IGoalCapabilityService? goalCapabilityService = null)
    {
        _logger = logger;
        _options = options.Value;
        _concurrencyLimiter = new SemaphoreSlim(_options.MaxConcurrentExecutions);
        _processManager = new PersistentProcessManager(processManagerLogger);
        _serviceProvider = serviceProvider;
        _chatSessionService = chatSessionService;
        _adapterFactory = adapterFactory;
        _ccSwitchService = ccSwitchService;
        _goalCapabilityService = goalCapabilityService ?? NullGoalCapabilityService.Instance;
        
        // 初始化工作区根目录（延迟加载，首次使用时从数据库获取）
        InitializeWorkspaceRoot();
    }
    
    /// <summary>
    /// 初始化工作区根目录
    /// </summary>
    private void InitializeWorkspaceRoot()
    {
        var workspaceRoot = GetEffectiveWorkspaceRoot();
        
        // 确保临时工作区根目录存在
        if (!Directory.Exists(workspaceRoot))
        {
            Directory.CreateDirectory(workspaceRoot);
            _logger.LogInformation("创建临时工作区根目录: {Root}", workspaceRoot);
        }
    }
    
    /// <summary>
    /// 获取有效的工作区根目录（优先数据库配置，否则使用配置文件，最后使用默认值）
    /// </summary>
    private string GetEffectiveWorkspaceRoot()
    {
        lock (_workspaceRootLock)
        {
            if (!string.IsNullOrWhiteSpace(_effectiveWorkspaceRoot))
            {
                return _effectiveWorkspaceRoot;
            }
            
            try
            {
                // 尝试从数据库获取
                using var scope = _serviceProvider.CreateScope();
                var repository = scope.ServiceProvider.GetService<ISystemSettingsRepository>();
                if (repository != null)
                {
                    var dbValue = repository.GetAsync(SystemSettingsKeys.WorkspaceRoot).GetAwaiter().GetResult();
                    if (!string.IsNullOrWhiteSpace(dbValue))
                    {
                        _effectiveWorkspaceRoot = dbValue;
                        _logger.LogInformation("从数据库加载工作区根目录: {Root}", dbValue);
                        return _effectiveWorkspaceRoot;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "从数据库加载工作区根目录失败，使用配置文件值");
            }
            
            // 使用配置文件中的值
            if (!string.IsNullOrWhiteSpace(_options.TempWorkspaceRoot))
            {
                _effectiveWorkspaceRoot = _options.TempWorkspaceRoot;
                return _effectiveWorkspaceRoot;
            }
            
            // 使用默认值
            _effectiveWorkspaceRoot = GetDefaultWorkspaceRoot();
            _logger.LogWarning("TempWorkspaceRoot 配置为空，使用默认路径: {Root}", _effectiveWorkspaceRoot);
            return _effectiveWorkspaceRoot;
        }
    }
    
    /// <summary>
    /// 获取默认工作区根目录
    /// </summary>
    private static string GetDefaultWorkspaceRoot()
    {
        // Docker 环境使用固定路径
        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
        {
            return "/app/workspaces";
        }
        
        // 非 Docker 环境使用应用根目录下的 workspaces 文件夹
        var appRoot = AppContext.BaseDirectory;
        return Path.Combine(appRoot, "workspaces");
    }
    
    /// <summary>
    /// 刷新工作区根目录缓存（当数据库配置更新时调用）
    /// </summary>
    public void RefreshWorkspaceRootCache()
    {
        lock (_workspaceRootLock)
        {
            _effectiveWorkspaceRoot = null;
        }
        InitializeWorkspaceRoot();
    }

    #region Adapter Methods

    public ICliToolAdapter? GetAdapter(CliToolConfig tool)
    {
        return _adapterFactory.GetAdapter(tool);
    }

    public ICliToolAdapter? GetAdapterById(string toolId)
    {
        return _adapterFactory.GetAdapter(toolId);
    }

    public bool SupportsStreamParsing(CliToolConfig tool)
    {
        return _adapterFactory.SupportsStreamParsing(tool);
    }

    public string? GetCliThreadId(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        lock (_cliSessionLock)
        {
            if (_cliThreadIds.TryGetValue(sessionId, out var cached) && !string.IsNullOrWhiteSpace(cached))
            {
                return cached;
            }
        }

        // 缓存未命中时，从数据库回退（ChatSession.CliThreadId / SessionOutput.ActiveThreadId）
        try
        {
            using var scope = _serviceProvider.CreateScope();

            var sessionRepo = scope.ServiceProvider.GetService<IChatSessionRepository>();
            var session = sessionRepo?.GetByIdAsync(sessionId).GetAwaiter().GetResult();
            var threadId = session?.CliThreadId;

            if (string.IsNullOrWhiteSpace(threadId))
            {
                var outputService = scope.ServiceProvider.GetService<ISessionOutputService>();
                var output = outputService?.GetBySessionIdAsync(sessionId).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(output?.ActiveThreadId))
                {
                    threadId = output.ActiveThreadId;
                }
            }

            if (string.IsNullOrWhiteSpace(threadId) && session != null)
            {
                threadId = CliThreadIdRecoveryHelper.TryRecoverFromImportedTitle(session.ToolId, session.Title);
                if (!string.IsNullOrWhiteSpace(threadId))
                {
                    _logger.LogInformation("从导入标题恢复会话 {SessionId} 的 CLI ThreadId: {ThreadId}", sessionId, threadId);

                    lock (_cliSessionLock)
                    {
                        _cliThreadIds[sessionId] = threadId;
                    }

                    if (sessionRepo != null)
                    {
                        _ = sessionRepo.UpdateCliThreadIdAsync(sessionId, threadId).GetAwaiter().GetResult();
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(threadId))
            {
                lock (_cliSessionLock)
                {
                    _cliThreadIds[sessionId] = threadId;
                }

                return threadId;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "从数据库回退 CLI ThreadId 失败: {SessionId}", sessionId);
        }

        return null;
    }

    public void SetCliThreadId(string sessionId, string threadId)
    {
        if (string.IsNullOrEmpty(threadId)) return;
        
        lock (_cliSessionLock)
        {
            _cliThreadIds[sessionId] = threadId;
            _logger.LogInformation("设置会话 {SessionId} 的CLI线程ID: {ThreadId}", sessionId, threadId);
        }

        // 最佳努力：持久化到数据库，保证服务重启/页面刷新后仍可恢复会话
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
            _ = repo.UpdateCliThreadIdAsync(sessionId, threadId).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "持久化 CLI ThreadId 失败: {SessionId}", sessionId);
        }
    }

    public async Task ResetSessionRuntimeAsync(
        string sessionId,
        bool clearCliThreadId = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        _processManager.CleanupSessionProcesses(sessionId);

        lock (_cliSessionLock)
        {
            if (clearCliThreadId)
            {
                _cliThreadIds.Remove(sessionId);
            }
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();

            var sessionRepository = scope.ServiceProvider.GetService<IChatSessionRepository>();
            if (sessionRepository != null && clearCliThreadId)
            {
                await sessionRepository.UpdateCliThreadIdAsync(sessionId, null);
            }

            var sessionOutputService = scope.ServiceProvider.GetService<ISessionOutputService>();
            if (sessionOutputService != null && clearCliThreadId)
            {
                var outputState = await sessionOutputService.GetBySessionIdAsync(sessionId);
                if (outputState != null && !string.IsNullOrWhiteSpace(outputState.ActiveThreadId))
                {
                    outputState.ActiveThreadId = string.Empty;
                    await sessionOutputService.SaveAsync(outputState);
                }
            }

            _logger.LogInformation("已重置会话运行态: {SessionId}", sessionId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "重置会话运行态失败(已完成内存清理): {SessionId}", sessionId);
        }
    }

    #endregion

    public async IAsyncEnumerable<StreamOutputChunk> ExecuteStreamAsync(
        string sessionId,
        string toolId,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var username = ResolveUsernameForToolOperation(null, sessionId);
        var tool = GetTool(toolId, username);
        if (tool == null && await HasValidCcSwitchSnapshotAsync(sessionId, toolId))
        {
            tool = _options.Tools
                .Where(t => t.Enabled)
                .FirstOrDefault(t => string.Equals(t.Id, toolId, StringComparison.OrdinalIgnoreCase));
        }

        if (tool == null)
        {
            var launchBlockingMessage = await GetLaunchBlockingMessageAsync(toolId, cancellationToken);
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = launchBlockingMessage ?? $"CLI 工具 '{toolId}' 不存在或当前用户无权使用"
            };
            yield break;
        }

        if (!tool.Enabled)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = $"CLI 工具 '{tool.Name}' 已禁用"
            };
            yield break;
        }

        // 限制并发执行数量
        await _concurrencyLimiter.WaitAsync(cancellationToken);

        try
        {
            await foreach (var chunk in ExecuteProcessStreamAsync(sessionId, tool, userPrompt, cancellationToken))
            {
                yield return chunk;
            }
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    public bool SupportsLowInterruptionContinue(string toolId)
    {
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return false;
        }

        var tool = GetTool(toolId);
        return SupportsLowInterruptionContinue(tool);
    }

    public bool CanStartLowInterruptionContinue(string sessionId, string toolId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return false;
        }

        var username = ResolveUsernameForToolOperation(null, sessionId);
        var tool = GetTool(toolId, username);
        return SupportsLowInterruptionContinue(tool) && !string.IsNullOrWhiteSpace(GetCliThreadId(sessionId));
    }

    public async IAsyncEnumerable<StreamOutputChunk> ExecuteLowInterruptionContinueStreamAsync(
        string sessionId,
        string toolId,
        string? prompt = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var username = ResolveUsernameForToolOperation(null, sessionId);
        var tool = GetTool(toolId, username);
        if (tool == null && await HasValidCcSwitchSnapshotAsync(sessionId, toolId))
        {
            tool = _options.Tools
                .Where(t => t.Enabled)
                .FirstOrDefault(t => string.Equals(t.Id, toolId, StringComparison.OrdinalIgnoreCase));
        }

        if (tool == null)
        {
            var launchBlockingMessage = await GetLaunchBlockingMessageAsync(toolId, cancellationToken);
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = launchBlockingMessage ?? $"CLI 宸ュ叿 '{toolId}' 涓嶅瓨鍦ㄦ垨褰撳墠鐢ㄦ埛鏃犳潈浣跨敤"
            };
            yield break;
        }

        if (!tool.Enabled)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = $"CLI 宸ュ叿 '{tool.Name}' 宸茬鐢?"
            };
            yield break;
        }

        if (!SupportsLowInterruptionContinue(tool))
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = $"CLI 宸ュ叿 '{tool.Name}' 涓嶆敮鎸佸皯鎵撴柇鎵ц"
            };
            yield break;
        }

        if (string.IsNullOrWhiteSpace(GetCliThreadId(sessionId)))
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = "Low-interruption continue requires an existing CLI thread/session id."
            };
            yield break;
        }

        await _concurrencyLimiter.WaitAsync(cancellationToken);

        try
        {
            await foreach (var chunk in ExecuteOneTimeProcessAsync(sessionId, tool, string.Empty, true, prompt, cancellationToken))
            {
                yield return chunk;
            }
        }
        finally
        {
            _concurrencyLimiter.Release();
        }
    }

    private bool SupportsLowInterruptionContinue(CliToolConfig? tool)
    {
        if (tool == null || !tool.Enabled)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(tool.LowInterruptionArgumentTemplate))
        {
            return true;
        }

        return _adapterFactory.GetAdapter(tool) != null;
    }

    private async IAsyncEnumerable<StreamOutputChunk> ExecuteProcessStreamAsync(
        string sessionId,
        CliToolConfig tool,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 根据工具配置选择执行模式
        if (tool.UsePersistentProcess)
        {
            _logger.LogInformation("【持久化进程模式】工具: {Tool}, UsePersistentProcess={Flag}", tool.Name, tool.UsePersistentProcess);
            await foreach (var chunk in ExecutePersistentProcessAsync(sessionId, tool, userPrompt, cancellationToken))
            {
                yield return chunk;
            }
        }
        else
        {
            _logger.LogInformation("【一次性进程模式】工具: {Tool}, UsePersistentProcess={Flag}", tool.Name, tool.UsePersistentProcess);
            await foreach (var chunk in ExecuteOneTimeProcessAsync(sessionId, tool, userPrompt, false, null, cancellationToken))
            {
                yield return chunk;
            }
        }
    }

    /// <summary>
    /// 使用持久化进程执行
    /// </summary>
    private async IAsyncEnumerable<StreamOutputChunk> ExecutePersistentProcessAsync(
        string sessionId,
        CliToolConfig tool,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var launchBlockingMessage = await GetLaunchBlockingMessageAsync(tool.Id, sessionId, cancellationToken);
        if (launchBlockingMessage != null)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = launchBlockingMessage
            };
            yield break;
        }

        var sessionWorkspace = GetOrCreateSessionWorkspace(sessionId);
        var workingDirectory = !string.IsNullOrWhiteSpace(tool.WorkingDirectory)
            ? tool.WorkingDirectory
            : sessionWorkspace;
        var workingDirectoryError = GetWorkingDirectoryValidationError(workingDirectory, sessionId);
        if (workingDirectoryError != null)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = workingDirectoryError
            };
            yield break;
        }
        
        // 解析命令路径
        var resolvedCommand = ResolveCommandPath(tool.Command);

        var managedSnapshotError = await EnsureManagedToolSessionSnapshotAsync(sessionId, tool.Id, sessionWorkspace, cancellationToken);
        if (managedSnapshotError != null)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = managedSnapshotError
            };
            yield break;
        }
        
        // 获取环境变量(优先从数据库)
        var environmentVariables = await GetToolEnvironmentVariablesAsync(tool.Id);

        if (string.Equals(tool.Id, "codex", StringComparison.OrdinalIgnoreCase)
            && !_ccSwitchService.IsManagedTool(tool.Id))
        {
            GenerateCodexConfigFile(environmentVariables);
        }
        
        // 获取适配器
        var adapter = _adapterFactory.GetAdapter(tool);
        bool hasAdapter = adapter != null;
        
        // 获取CLI线程ID（用于会话恢复）
        string? cliThreadId = GetCliThreadId(sessionId);
        
        // 构建会话上下文
        var sessionContext = await BuildCliSessionContextAsync(
            sessionId,
            tool.Id,
            sessionWorkspace,
            cliThreadId,
            environmentVariables,
            cancellationToken);
        
        _logger.LogInformation("使用持久化进程模式执行 CLI 工具: {Tool}, 会话: {Session}, 工作目录: {Workspace}, 命令: {Command}, CLI Thread: {CliThread}, 适配器: {Adapter}", 
            tool.Name, sessionId, sessionWorkspace, resolvedCommand, cliThreadId ?? "新会话", adapter?.GetType().Name ?? "无");

        PersistentProcessInfo? processInfo = null;
        bool hasError = false;
        string? errorMessage = null;
        
        // 创建带有解析后命令路径和环境变量的tool副本
        var toolWithResolvedCommand = new CliToolConfig
        {
            Id = tool.Id,
            Name = tool.Name,
            Description = tool.Description,
            Command = resolvedCommand, // 使用解析后的命令路径
            ArgumentTemplate = tool.ArgumentTemplate,
            LowInterruptionArgumentTemplate = tool.LowInterruptionArgumentTemplate,
            WorkingDirectory = tool.WorkingDirectory,
            Enabled = tool.Enabled,
            TimeoutSeconds = tool.TimeoutSeconds,
            EnvironmentVariables = environmentVariables, // 使用从数据库或配置文件获取的环境变量
            UsePersistentProcess = tool.UsePersistentProcess,
            PersistentModeArguments = tool.PersistentModeArguments
        };
        
        // 获取或创建持久化进程
        try
        {
            processInfo = _processManager.GetOrCreateProcess(
                sessionId, 
                tool.Id, 
                toolWithResolvedCommand, 
                sessionWorkspace,
                environmentVariables);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建持久化进程失败");
            hasError = true;
            errorMessage = $"创建进程失败: {ex.Message}";
        }

        if (hasError || processInfo == null)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = errorMessage ?? "创建进程失败"
            };
            yield break;
        }

        if (!processInfo.IsRunning)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = "进程未运行"
            };
            yield break;
        }

        // 向进程发送用户输入
        // 使用适配器构建命令（如果有适配器）
        string actualInput;
        if (hasAdapter)
        {
            actualInput = adapter!.BuildArguments(tool, userPrompt, sessionContext);
            _logger.LogInformation("使用适配器 {Adapter} 构建命令: PID={ProcessId}, IsResume={IsResume}, Prompt长度={Length}", 
                adapter.GetType().Name, processInfo.Process.Id, sessionContext.IsResume, userPrompt.Length);
        }
        else
        {
            // 无适配器时，直接发送用户输入
            actualInput = userPrompt;
            _logger.LogInformation("向持久化进程发送输入: PID={ProcessId}, Prompt长度={Length}", 
                processInfo.Process.Id, userPrompt.Length);
        }
        
        bool sendError = false;
        string? sendErrorMessage = null;
        
        try
        {
            await _processManager.SendInputAsync(processInfo, actualInput, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "发送输入到进程失败");
            sendError = true;
            sendErrorMessage = $"发送输入失败: {ex.Message}";
        }
        
        if (sendError)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = sendErrorMessage ?? "发送输入失败"
            };
            yield break;
        }

        // 读取输出
        using var outputCts = new CancellationTokenSource();
        if (tool.TimeoutSeconds > 0)
        {
            outputCts.CancelAfter(TimeSpan.FromSeconds(tool.TimeoutSeconds));
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, outputCts.Token);

        bool cancelled = false;
        bool terminalEventDetected = false;
        bool terminalEventIsError = false;
        string? terminalEventErrorMessage = null;
        var terminalEventBuffer = hasAdapter && adapter!.SupportsStreamParsing ? new StringBuilder() : null;
        var fullOutput = new StringBuilder(); // 用于解析thread id

        await using (var enumerator = ReadPersistentProcessOutputAsync(processInfo, linkedCts.Token)
            .GetAsyncEnumerator(linkedCts.Token))
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }

                if (!hasNext)
                {
                    break;
                }

                if (linkedCts.Token.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }

                var chunk = enumerator.Current;
                
                // 收集输出内容用于解析session id
                if (!chunk.IsError && !string.IsNullOrEmpty(chunk.Content))
                {
                    fullOutput.Append(chunk.Content);
                }

                if (terminalEventBuffer != null && !string.IsNullOrEmpty(chunk.Content))
                {
                    var terminalSignal = InspectAdapterTerminalSignal(
                        sessionId,
                        chunk.Content,
                        adapter!,
                        terminalEventBuffer);

                    if (terminalSignal.IsTerminal)
                    {
                        terminalEventDetected = true;
                        terminalEventIsError = terminalSignal.IsError;
                        terminalEventErrorMessage = terminalSignal.ErrorMessage;
                    }
                }
                
                yield return chunk;

                if (terminalEventDetected)
                {
                    _logger.LogInformation(
                        "检测到适配器终止事件，提前结束当前轮输出读取: Tool={ToolId}, Session={SessionId}, IsError={IsError}",
                        tool.Id,
                        sessionId,
                        terminalEventIsError);
                    break;
                }
            }
        }

        if (!terminalEventDetected && terminalEventBuffer is { Length: > 0 })
        {
            var terminalSignal = InspectAdapterTerminalSignal(
                sessionId,
                string.Empty,
                adapter!,
                terminalEventBuffer,
                flushRemaining: true);

            if (terminalSignal.IsTerminal)
            {
                terminalEventDetected = true;
                terminalEventIsError = terminalSignal.IsError;
                terminalEventErrorMessage = terminalSignal.ErrorMessage;
            }
        }
        
        // 如果有适配器且还没有CLI线程ID，尝试从输出中解析
        if (hasAdapter && string.IsNullOrEmpty(cliThreadId))
        {
            var output = fullOutput.ToString();
            var parsedThreadId = ParseCliThreadId(output, adapter!);
            if (!string.IsNullOrEmpty(parsedThreadId))
            {
                SetCliThreadId(sessionId, parsedThreadId);
                _logger.LogInformation("解析到CLI Thread ID: {CliThread} for 会话: {Session}", parsedThreadId, sessionId);
            }
        }

        if (cancelled)
        {
            _processManager.CleanupSessionProcesses(sessionId);

            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = "执行已取消或超时"
            };
        }
        else if (terminalEventDetected && terminalEventIsError)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = terminalEventErrorMessage ?? "执行失败"
            };
        }
        else
        {
            yield return new StreamOutputChunk
            {
                IsCompleted = true,
                Content = string.Empty
            };
        }
    }

    /// <summary>
    /// 读取持久化进程的输出
    /// </summary>
    private async IAsyncEnumerable<StreamOutputChunk> ReadPersistentProcessOutputAsync(
        PersistentProcessInfo processInfo,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var outputReader = processInfo.Process.StandardOutput;
        var errorReader = processInfo.Process.StandardError;
        var outputBuffer = new char[4096];
        var errorBuffer = new char[4096];
        var lastOutputTime = DateTime.UtcNow;
        var noOutputTimeout = TimeSpan.FromSeconds(2); // 2秒无新输出则认为结束
        var hasObservedOutput = false;
        var outputClosed = false;
        var errorClosed = false;
        Task<int>? outputReadTask = null;
        Task<int>? errorReadTask = null;

        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!outputClosed && outputReadTask == null)
                {
                    outputReadTask = outputReader
                        .ReadAsync(outputBuffer.AsMemory(0, outputBuffer.Length), readCts.Token)
                        .AsTask();
                }

                if (!errorClosed && errorReadTask == null)
                {
                    errorReadTask = errorReader
                        .ReadAsync(errorBuffer.AsMemory(0, errorBuffer.Length), readCts.Token)
                        .AsTask();
                }

                if (outputReadTask == null && errorReadTask == null)
                {
                    break;
                }

                var waitTasks = new List<Task>(3);
                if (outputReadTask != null)
                {
                    waitTasks.Add(outputReadTask);
                }

                if (errorReadTask != null)
                {
                    waitTasks.Add(errorReadTask);
                }

                var delayTask = Task.Delay(50, cancellationToken);
                waitTasks.Add(delayTask);

                var completedTask = await Task.WhenAny(waitTasks);

                if (ReferenceEquals(completedTask, delayTask))
                {
                    if ((outputReadTask?.IsCompleted ?? false) || (errorReadTask?.IsCompleted ?? false))
                    {
                        continue;
                    }

                    if (hasObservedOutput && (DateTime.UtcNow - lastOutputTime) > noOutputTimeout)
                    {
                        _logger.LogInformation("检测到输出结束（无新输出超过{Timeout}秒）", noOutputTimeout.TotalSeconds);
                        readCts.Cancel();
                        break;
                    }

                    continue;
                }

                if (ReferenceEquals(completedTask, outputReadTask))
                {
                    int charsRead;
                    try
                    {
                        charsRead = await outputReadTask!;
                    }
                    catch (OperationCanceledException) when (readCts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "读取标准输出时发生错误");
                        break;
                    }

                    outputReadTask = null;
                    if (charsRead <= 0)
                    {
                        outputClosed = true;
                    }
                    else
                    {
                        hasObservedOutput = true;
                        lastOutputTime = DateTime.UtcNow;
                        yield return new StreamOutputChunk
                        {
                            Content = new string(outputBuffer, 0, charsRead),
                            IsError = false,
                            IsCompleted = false
                        };
                    }
                }

                if (ReferenceEquals(completedTask, errorReadTask))
                {
                    int charsRead;
                    try
                    {
                        charsRead = await errorReadTask!;
                    }
                    catch (OperationCanceledException) when (readCts.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "读取错误输出时发生错误");
                        break;
                    }

                    errorReadTask = null;
                    if (charsRead <= 0)
                    {
                        errorClosed = true;
                    }
                    else
                    {
                        hasObservedOutput = true;
                        lastOutputTime = DateTime.UtcNow;
                        yield return new StreamOutputChunk
                        {
                            Content = new string(errorBuffer, 0, charsRead),
                            IsError = false, // Codex 输出到 stderr 也是正常内容
                            IsCompleted = false
                        };
                    }
                }
            }
        }
        finally
        {
            if (!readCts.IsCancellationRequested)
            {
                readCts.Cancel();
            }

            await ObservePendingPersistentReadTaskAsync(outputReadTask, "标准输出");
            await ObservePendingPersistentReadTaskAsync(errorReadTask, "错误输出");
        }
    }

    private async Task ObservePendingPersistentReadTaskAsync(Task<int>? readTask, string streamName)
    {
        if (readTask == null)
        {
            return;
        }

        try
        {
            await readTask;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("{StreamName}读取任务已取消", streamName);
        }
        catch (ObjectDisposedException)
        {
            _logger.LogDebug("{StreamName}读取任务对应的流已释放", streamName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{StreamName}读取任务在收尾阶段结束异常", streamName);
        }
    }

    private AdapterTerminalSignal InspectAdapterTerminalSignal(
        string sessionId,
        string content,
        ICliToolAdapter adapter,
        StringBuilder lineBuffer,
        bool flushRemaining = false)
    {
        if (!string.IsNullOrEmpty(content))
        {
            lineBuffer.Append(content);
        }

        while (TryReadBufferedAdapterLine(lineBuffer, flushRemaining, out var line))
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmedLine))
            {
                continue;
            }

            CliOutputEvent? outputEvent;
            try
            {
                outputEvent = adapter.ParseOutputLine(trimmedLine);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "解析适配器输出行失败: {Line}", trimmedLine.Length > 120 ? trimmedLine[..120] : trimmedLine);
                continue;
            }

            if (outputEvent == null)
            {
                continue;
            }

            var parsedSessionId = adapter.ExtractSessionId(outputEvent);
            if (!string.IsNullOrWhiteSpace(parsedSessionId))
            {
                var existingThreadId = GetCliThreadId(sessionId);
                if (!string.Equals(existingThreadId, parsedSessionId, StringComparison.Ordinal))
                {
                    SetCliThreadId(sessionId, parsedSessionId);
                }
            }

            if (outputEvent.EventType == "turn.completed")
            {
                return AdapterTerminalSignal.Completed();
            }

            if (outputEvent.EventType == "turn.failed")
            {
                return AdapterTerminalSignal.Failed(
                    outputEvent.ErrorMessage
                    ?? outputEvent.Content
                    ?? "本轮交互失败。");
            }
        }

        return AdapterTerminalSignal.None;
    }

    private static bool TryReadBufferedAdapterLine(StringBuilder lineBuffer, bool flushRemaining, out string line)
    {
        for (var i = 0; i < lineBuffer.Length; i++)
        {
            if (lineBuffer[i] != '\n')
            {
                continue;
            }

            line = lineBuffer.ToString(0, i).TrimEnd('\r');
            lineBuffer.Remove(0, i + 1);
            return true;
        }

        if (!flushRemaining || lineBuffer.Length == 0)
        {
            line = string.Empty;
            return false;
        }

        line = lineBuffer.ToString().TrimEnd('\r');
        lineBuffer.Clear();
        return true;
    }

    private readonly record struct AdapterTerminalSignal(bool IsTerminal, bool IsError, string? ErrorMessage)
    {
        public static AdapterTerminalSignal None => new(false, false, null);

        public static AdapterTerminalSignal Completed() => new(true, false, null);

        public static AdapterTerminalSignal Failed(string errorMessage) => new(true, true, errorMessage);
    }

    /// <summary>
    /// 使用一次性进程执行（原有逻辑）
    /// </summary>
    private async IAsyncEnumerable<StreamOutputChunk> ExecuteOneTimeProcessAsync(
        string sessionId,
        CliToolConfig tool,
        string userPrompt,
        bool useLowInterruption,
        string? lowInterruptionPrompt = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var launchBlockingMessage = await GetLaunchBlockingMessageAsync(tool.Id, sessionId, cancellationToken);
        if (launchBlockingMessage != null)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = launchBlockingMessage
            };
            yield break;
        }

        Process? process = null;
        
        // 获取适配器
        var adapter = _adapterFactory.GetAdapter(tool);
        bool hasAdapter = adapter != null;
        
        // 获取CLI线程ID（用于会话恢复）
        string? cliThreadId = GetCliThreadId(sessionId);
        
        // 获取或创建会话专属的工作目录
        var sessionWorkspace = GetOrCreateSessionWorkspace(sessionId);

        var managedSnapshotError = await EnsureManagedToolSessionSnapshotAsync(sessionId, tool.Id, sessionWorkspace, cancellationToken);
        if (managedSnapshotError != null)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = managedSnapshotError
            };
            yield break;
        }
        
        var environmentVariables = await GetToolEnvironmentVariablesAsync(tool.Id);

        if (string.Equals(tool.Id, "codex", StringComparison.OrdinalIgnoreCase)
            && !_ccSwitchService.IsManagedTool(tool.Id))
        {
            GenerateCodexConfigFile(environmentVariables);
        }

        // 构建会话上下文
        var sessionContext = await BuildCliSessionContextAsync(
            sessionId,
            tool.Id,
            sessionWorkspace,
            cliThreadId,
            environmentVariables,
            cancellationToken);

        // 构建参数，使用适配器（如果有）
        string arguments;
        if (useLowInterruption)
        {
            arguments = BuildLowInterruptionArguments(tool, adapter, sessionContext);
            _logger.LogInformation(
                "Using low-interruption arguments for CLI launch: Tool={Tool}, Adapter={Adapter}, CliThreadId={CliThreadId}",
                tool.Name,
                adapter?.GetType().Name ?? "none",
                sessionContext.CliThreadId);
        }
        else if (hasAdapter)
        {
            arguments = adapter!.BuildArguments(tool, userPrompt, sessionContext);
            _logger.LogInformation("????????{Adapter} ??????, IsResume={IsResume}", adapter.GetType().Name, sessionContext.IsResume);
        }
        else
        {
            var escapedPrompt = EscapeArgument(userPrompt);
            arguments = tool.ArgumentTemplate.Replace("{prompt}", escapedPrompt);
        }
        
        // 解析命令路径(如果配置了npm目录且命令是相对路径)
        var commandPath = ResolveCommandPath(tool.Command);

        _logger.LogInformation("执行 CLI 工具: {Tool}, 会话: {Session}, 工作目录: {Workspace}, 命令: {Command} {Arguments}", 
            tool.Name, sessionId, sessionWorkspace, commandPath, arguments);

        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            FileName = commandPath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // 设置工作目录
        if (!string.IsNullOrWhiteSpace(tool.WorkingDirectory))
        {
            startInfo.WorkingDirectory = tool.WorkingDirectory;
        }
        else
        {
            // 使用会话专属的工作目录
            startInfo.WorkingDirectory = sessionWorkspace;
        }

        var workingDirectoryError = GetWorkingDirectoryValidationError(startInfo.WorkingDirectory, sessionId);
        if (workingDirectoryError != null)
        {
            _logger.LogWarning("启动 CLI 进程前发现无效工作目录: {WorkingDirectory}", startInfo.WorkingDirectory);
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = workingDirectoryError
            };
            yield break;
        }

        // 设置环境变量
        if (environmentVariables != null && environmentVariables.Count > 0)
        {
            foreach (var kvp in environmentVariables)
            {
                // 空字符串表示显式移除环境变量（用于取消继承父进程的环境变量）
                // 这对于像 CLAUDECODE 这样的变量很重要，需要完全未设置而不是空字符串
                if (string.IsNullOrEmpty(kvp.Value))
                {
                    if (startInfo.EnvironmentVariables.ContainsKey(kvp.Key))
                    {
                        startInfo.EnvironmentVariables.Remove(kvp.Key);
                        _logger.LogDebug("移除环境变量: {Key}", kvp.Key);
                    }
                    continue;
                }
                // 非空值正常设置
                startInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
                _logger.LogDebug("设置环境变量: {Key} = {Value}", kvp.Key, kvp.Value);
            }
            
            // 在 Windows 上额外设置编码相关环境变量(仅在已修改环境变量时)
            if (OperatingSystem.IsWindows())
            {
                if (!startInfo.EnvironmentVariables.ContainsKey("PYTHONIOENCODING"))
                {
                    startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                    _logger.LogDebug("设置环境变量: PYTHONIOENCODING = utf-8");
                }
                if (!startInfo.EnvironmentVariables.ContainsKey("PYTHONLEGACYWINDOWSSTDIO"))
                {
                    startInfo.EnvironmentVariables["PYTHONLEGACYWINDOWSSTDIO"] = "utf-8";
                    _logger.LogDebug("设置环境变量: PYTHONLEGACYWINDOWSSTDIO = utf-8");
                }
                // 设置控制台输出代码页为 UTF-8
                if (!startInfo.EnvironmentVariables.ContainsKey("PYTHONLEGACYWINDOWSFSENCODING"))
                {
                    startInfo.EnvironmentVariables["PYTHONLEGACYWINDOWSFSENCODING"] = "utf-8";
                    _logger.LogDebug("设置环境变量: PYTHONLEGACYWINDOWSFSENCODING = utf-8");
                }
            }
        }

        RewriteCodexLaunchToNode(startInfo, tool, commandPath, arguments);
        RewriteClaudeLaunchToNode(startInfo, tool, commandPath, arguments);
        EnsurePreferredNodeForClaude(startInfo, tool, commandPath);

        _logger.LogInformation("准备启动进程: {FileName} {Arguments}", startInfo.FileName, startInfo.Arguments);

        process = new Process { StartInfo = startInfo };

        // 启动进程
        bool processStarted = false;
        string? startErrorMessage = null;
        
        try
        {
            processStarted = process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "启动 CLI 进程失败: {Tool}", tool.Name);
            startErrorMessage = $"启动进程失败: {ex.Message}";
        }

        // 检查启动错误
        if (startErrorMessage != null)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = startErrorMessage
            };
            process?.Dispose();
            yield break;
        }

        if (!processStarted)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = "无法启动 CLI 进程"
            };
            process?.Dispose();
            yield break;
        }

        WriteStandardInput(process, BuildStandardInput(tool, adapter, sessionContext, useLowInterruption, lowInterruptionPrompt));
        
        _logger.LogInformation("进程已启动，PID: {ProcessId}，开始读取输出流", process.Id);

        // 创建超时取消令牌
        using var timeoutCts = new CancellationTokenSource();
        if (tool.TimeoutSeconds > 0)
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(tool.TimeoutSeconds));
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);
        using var streamReadCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);

        // 同时读取标准输出和错误输出
        // 注意：某些 CLI 工具（如 Codex）会将正常输出也输出到 stderr
        _logger.LogInformation("创建标准输出读取任务");
        var outputTask = ReadStreamAsync(process.StandardOutput, false, streamReadCts.Token);
        _logger.LogInformation("创建错误输出读取任务");
        var errorTask = ReadStreamAsync(process.StandardError, false, streamReadCts.Token); // 不标记为错误

        _logger.LogInformation("开始合并流输出");
        int chunkCount = 0;
        bool terminalEventDetected = false;
        bool terminalEventIsError = false;
        string? terminalEventErrorMessage = null;
        var terminalEventBuffer = hasAdapter && adapter!.SupportsStreamParsing ? new StringBuilder() : null;
        var fullOutput = new StringBuilder(); // 用于解析thread id

        // 合并两个流的输出
        await using var mergedEnumerator = MergeStreamsAsync(outputTask, errorTask, streamReadCts.Token).GetAsyncEnumerator();
        while (true)
        {
            StreamOutputChunk chunk;
            try
            {
                if (!await mergedEnumerator.MoveNextAsync())
                {
                    break;
                }

                chunk = mergedEnumerator.Current;
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
                _logger.LogInformation(
                    "流输出合并已取消，等待后续超时/取消收尾逻辑: Tool={Tool}, Session={Session}, TimeoutTriggered={TimeoutTriggered}, CallerCancelled={CallerCancelled}",
                    tool.Name,
                    sessionId,
                    timeoutCts.IsCancellationRequested,
                    cancellationToken.IsCancellationRequested);
                break;
            }

            chunkCount++;

            // 收集输出用于后续解析session id
            if (!chunk.IsError && !string.IsNullOrEmpty(chunk.Content))
            {
                fullOutput.Append(chunk.Content);
            }

            if (terminalEventBuffer != null && !string.IsNullOrEmpty(chunk.Content))
            {
                var terminalSignal = InspectAdapterTerminalSignal(
                    sessionId,
                    chunk.Content,
                    adapter!,
                    terminalEventBuffer);

                if (terminalSignal.IsTerminal)
                {
                    terminalEventDetected = true;
                    terminalEventIsError = terminalSignal.IsError;
                    terminalEventErrorMessage = terminalSignal.ErrorMessage;
                }
            }

            yield return chunk;

            if (terminalEventDetected)
            {
                _logger.LogInformation(
                    "检测到一次性进程适配器终止事件，提前结束当前轮输出读取: Tool={ToolId}, Session={SessionId}, IsError={IsError}",
                    tool.Id,
                    sessionId,
                    terminalEventIsError);

                if (!streamReadCts.IsCancellationRequested)
                {
                    streamReadCts.Cancel();
                }

                break;
            }
        }

        if (!terminalEventDetected && terminalEventBuffer is { Length: > 0 })
        {
            var terminalSignal = InspectAdapterTerminalSignal(
                sessionId,
                string.Empty,
                adapter!,
                terminalEventBuffer,
                flushRemaining: true);

            if (terminalSignal.IsTerminal)
            {
                terminalEventDetected = true;
                terminalEventIsError = terminalSignal.IsError;
                terminalEventErrorMessage = terminalSignal.ErrorMessage;
            }
        }
        
        // 如果有适配器且还没有CLI线程ID，尝试从输出中解析
        if (hasAdapter && string.IsNullOrEmpty(cliThreadId))
        {
            var output = fullOutput.ToString();
            var parsedThreadId = ParseCliThreadId(output, adapter!);
            if (!string.IsNullOrEmpty(parsedThreadId))
            {
                SetCliThreadId(sessionId, parsedThreadId);
                _logger.LogInformation("解析到CLI Thread ID: {CliThread} for 会话: {Session}", parsedThreadId, sessionId);
            }
        }
        

        // 等待进程退出
        bool processTimedOut = false;
        bool processCancelled = false;
        
        if (terminalEventDetected)
        {
            if (!streamReadCts.IsCancellationRequested)
            {
                streamReadCts.Cancel();
            }

            await WaitForProcessExitAfterTerminalSignalAsync(process, tool.Id, sessionId);
        }
        else
        {
            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (timeoutCts.Token.IsCancellationRequested)
                {
                    processTimedOut = true;
                    try
                    {
                        process.Kill(true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "终止进程失败");
                    }
                }
                else
                {
                    processCancelled = true;
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill(true);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "取消执行时终止进程失败");
                    }
                }
            }
        }

        if (processTimedOut)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = $"执行超时（{tool.TimeoutSeconds} 秒）"
            };
        }
        else if (processCancelled)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = "执行已取消"
            };
        }
        else if (terminalEventDetected && terminalEventIsError)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = terminalEventErrorMessage ?? "执行失败"
            };
        }
        else if (terminalEventDetected)
        {
            yield return new StreamOutputChunk
            {
                IsCompleted = true,
                Content = string.Empty
            };

            _logger.LogInformation("CLI 工具通过语义终止事件完成: {Tool}", tool.Name);
        }
        else if (process.ExitCode != 0)
        {
            var output = fullOutput.ToString();
            var failureMessage = BuildDetailedFailureMessage(output, adapter, process.ExitCode);

            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = failureMessage
            };

            _logger.LogWarning("CLI 工具执行失败: {Tool}, 退出代码: {ExitCode}, 错误信息: {ErrorMessage}",
                tool.Name, process.ExitCode, failureMessage);
        }
        else
        {
            // 返回完成标记
            yield return new StreamOutputChunk
            {
                IsCompleted = true,
                Content = string.Empty
            };

            _logger.LogInformation("CLI 工具执行完成: {Tool}, 退出代码: {ExitCode}", 
                tool.Name, process.ExitCode);
        }

        process?.Dispose();
    }

    private async Task WaitForProcessExitAfterTerminalSignalAsync(Process process, string toolId, string sessionId)
    {
        if (process.HasExited)
        {
            return;
        }

        if (await WaitForProcessExitWithinAsync(
                process,
                TimeSpan.FromSeconds(5),
                toolId,
                sessionId,
                "等待语义终止后的进程自然退出时发生异常"))
        {
            return;
        }

        if (await TryRequestGracefulProcessExitAsync(process, toolId, sessionId))
        {
            return;
        }

        try
        {
            _logger.LogInformation(
                "语义终止事件已到达，优雅退出仍未完成，开始强制结束一次性进程: Tool={ToolId}, Session={SessionId}, PID={ProcessId}",
                toolId,
                sessionId,
                process.Id);
            process.Kill(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "主动结束一次性进程失败: Tool={ToolId}, Session={SessionId}", toolId, sessionId);
            return;
        }

        await WaitForProcessExitWithinAsync(
            process,
            TimeSpan.FromSeconds(2),
            toolId,
            sessionId,
            "等待被终止的一次性进程退出时发生异常");
    }

    /// <summary>
    /// 语义完成后优先尝试温和结束 CLI，尽量给原生 history/rollout 刷盘留时间，再回退到强制结束。
    /// </summary>
    protected virtual async Task<bool> TryRequestGracefulProcessExitAsync(Process process, string toolId, string sessionId)
    {
        if (process.HasExited)
        {
            return true;
        }

        if (TryCloseProcessMainWindow(process, toolId, sessionId)
            && await WaitForProcessExitWithinAsync(
                process,
                TimeSpan.FromSeconds(3),
                toolId,
                sessionId,
                "等待主窗口关闭后进程退出时发生异常"))
        {
            return true;
        }

        return process.HasExited;
    }

    private async Task<bool> WaitForProcessExitWithinAsync(
        Process process,
        TimeSpan timeout,
        string toolId,
        string sessionId,
        string logMessage)
    {
        try
        {
            using var exitCts = new CancellationTokenSource(timeout);
            await process.WaitForExitAsync(exitCts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return process.HasExited;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{LogMessage}: Tool={ToolId}, Session={SessionId}", logMessage, toolId, sessionId);
            return process.HasExited;
        }
    }

    private bool TryCloseProcessMainWindow(Process process, string toolId, string sessionId)
    {
        try
        {
            if (process.HasExited || !process.CloseMainWindow())
            {
                return false;
            }

            _logger.LogInformation(
                "语义终止事件已到达，已请求一次性进程通过主窗口关闭退出: Tool={ToolId}, Session={SessionId}, PID={ProcessId}",
                toolId,
                sessionId,
                process.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "请求一次性进程通过主窗口关闭退出失败: Tool={ToolId}, Session={SessionId}", toolId, sessionId);
            return false;
        }
    }

    private async IAsyncEnumerable<(string content, bool isError)> ReadStreamAsync(
        StreamReader reader,
        bool isErrorStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("开始读取流，isErrorStream: {IsError}", isErrorStream);
        var buffer = new char[4096];
        int chunkCount = 0;

        while (true)
        {
            int charsRead;

            try
            {
                charsRead = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("读取流被取消，isErrorStream: {IsError}, 已读取 {Count} 块", isErrorStream, chunkCount);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取流时发生错误，isErrorStream: {IsError}", isErrorStream);
                break;
            }

            if (charsRead <= 0)
            {
                _logger.LogInformation("流结束，isErrorStream: {IsError}, 共读取 {Count} 块", isErrorStream, chunkCount);
                break;
            }

            chunkCount++;
            var content = new string(buffer, 0, charsRead);
            var trimmedPreview = content.TrimStart();
            if (!trimmedPreview.StartsWith("{\"type\":\"system\""))
            {
                var preview = content.Length > 100 ? content[..100] + "..." : content;
                _logger.LogDebug("CLI输出块: {Preview}", preview.Replace("\r", "\\r").Replace("\n", "\\n"));
            }

            yield return (content, isErrorStream);
        }
    }

    private async IAsyncEnumerable<StreamOutputChunk> MergeStreamsAsync(
        IAsyncEnumerable<(string content, bool isError)> outputStream,
        IAsyncEnumerable<(string content, bool isError)> errorStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 使用 Channel 来更高效地合并两个流
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StreamOutputChunk>();
        var writer = channel.Writer;

        // 读取标准输出流的任务
        var outputTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var (content, isError) in outputStream.WithCancellation(cancellationToken))
                {
                    await writer.WriteAsync(new StreamOutputChunk
                    {
                        Content = content,
                        IsError = isError,
                        IsCompleted = false
                    }, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("标准输出流读取被取消");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取标准输出流时发生错误");
            }
        }, cancellationToken);

        // 读取错误输出流的任务
        var errorTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var (content, isError) in errorStream.WithCancellation(cancellationToken))
                {
                    await writer.WriteAsync(new StreamOutputChunk
                    {
                        Content = content,
                        IsError = isError,
                        IsCompleted = false
                    }, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                _logger.LogDebug("错误输出流读取被取消");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取错误输出流时发生错误");
            }
        }, cancellationToken);

        // 等待所有读取任务完成后关闭 writer
        _ = Task.WhenAll(outputTask, errorTask).ContinueWith(_ =>
        {
            writer.Complete();
            _logger.LogDebug("所有流读取完成，关闭 channel");
        }, cancellationToken);

        // 从 channel 中读取并返回结果
        await foreach (var chunk in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return chunk;
        }
    }

    public List<CliToolConfig> GetAvailableTools(string? username = null)
    {
        var enabledTools = FilterCcSwitchLaunchReadyTools(_options.Tools.Where(t => t.Enabled));
        var resolvedUsername = ResolveUsernameForToolOperation(username);
        if (string.IsNullOrWhiteSpace(resolvedUsername))
        {
            return enabledTools;
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var userAccountService = scope.ServiceProvider.GetRequiredService<IUserAccountService>();
            var account = userAccountService.GetByUsernameAsync(resolvedUsername).GetAwaiter().GetResult();
            if (account != null && !string.Equals(account.Status, UserAccessConstants.EnabledStatus, StringComparison.OrdinalIgnoreCase))
            {
                return new List<CliToolConfig>();
            }

            var policyService = scope.ServiceProvider.GetRequiredService<IUserToolPolicyService>();
            var allowedToolIds = policyService
                .GetAllowedToolIdsAsync(resolvedUsername, enabledTools.Select(t => t.Id))
                .GetAwaiter()
                .GetResult();

            return enabledTools
                .Where(t => allowedToolIds.Contains(t.Id))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "按用户过滤 CLI 工具失败，回退到全局工具列表");
            return enabledTools;
        }
    }

    public CliToolConfig? GetTool(string toolId, string? username = null)
    {
        return GetAvailableTools(username)
            .FirstOrDefault(t => string.Equals(t.Id, toolId, StringComparison.OrdinalIgnoreCase));
    }

    public bool ValidateTool(string toolId, string? username = null)
    {
        var tool = GetTool(toolId, username);
        if (tool == null || !tool.Enabled)
        {
            return false;
        }

        // 验证命令是否存在（简单检查）
        try
        {
            // 对于 Windows 系统，检查命令是否可执行
            if (OperatingSystem.IsWindows())
            {
                // 如果是完整路径，检查文件是否存在
                if (Path.IsPathRooted(tool.Command))
                {
                    return File.Exists(tool.Command);
                }
                // 否则假设是系统命令，返回 true
                return true;
            }
            else
            {
                // 对于 Linux/Mac，可以使用 which 命令检查
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 转义命令行参数以防止注入攻击
    /// </summary>
    private string EscapeArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }

        // 对于 Windows 系统
        if (OperatingSystem.IsWindows())
        {
            // 替换双引号并用双引号包裹
            return $"\"{argument.Replace("\"", "\\\"")}\"";
        }
        else
        {
            // 对于 Linux/Mac 系统
            return $"'{argument.Replace("'", "'\\''")}'";
        }
    }

    /// <summary>
    /// 获取或创建会话专属的工作目录
    /// </summary>
    private string GetOrCreateSessionWorkspace(string sessionId)
    {
        lock (_workspaceLock)
        {
            if (_sessionWorkspaces.TryGetValue(sessionId, out var existingWorkspace))
            {
                if (Directory.Exists(existingWorkspace))
                {
                    return existingWorkspace;
                }

                _logger.LogWarning("缓存中的会话工作目录已不存在，将重新解析: {SessionId}, {Workspace}", sessionId, existingWorkspace);
                _sessionWorkspaces.Remove(sessionId);
            }

            // 优先使用数据库中已绑定的工作目录（适用于：自定义目录 / 外部会话导入 / 进程重启后恢复）
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var chatSessionRepository = scope.ServiceProvider.GetService<IChatSessionRepository>();
                var session = chatSessionRepository?.GetByIdAsync(sessionId).GetAwaiter().GetResult();

                if (session != null)
                {
                    if (!string.IsNullOrWhiteSpace(session.WorkspacePath) && Directory.Exists(session.WorkspacePath))
                    {
                        _sessionWorkspaces[sessionId] = session.WorkspacePath;
                        return session.WorkspacePath;
                    }

                    // 自定义目录但不存在时，避免悄悄创建临时目录导致误用
                    if (session.IsCustomWorkspace && !string.IsNullOrWhiteSpace(session.WorkspacePath))
                    {
                        throw new InvalidOperationException(
                            $"会话 {sessionId} 工作目录不存在或已被清理，请重新创建会话");
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // 自定义目录不存在时：直接失败，避免静默创建临时目录导致误用
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "从数据库恢复会话工作目录失败，将创建临时目录: {SessionId}", sessionId);
            }

            // 创建新的会话工作目录
            var workspaceRoot = GetEffectiveWorkspaceRoot();
            var workspacePath = Path.Combine(workspaceRoot, sessionId);
            
            try
            {
                if (!Directory.Exists(workspacePath))
                {
                    Directory.CreateDirectory(workspacePath);
                    _logger.LogInformation("为会话 {SessionId} 创建工作目录: {Path}", sessionId, workspacePath);
                }

                _sessionWorkspaces[sessionId] = workspacePath;
                
                // 在工作目录中创建一个标记文件,记录创建时间
                var markerFile = Path.Combine(workspacePath, ".workspace_info");
                File.WriteAllText(markerFile, $"Created: {DateTime.UtcNow:O}\nSessionId: {sessionId}");

                // 最佳努力：把新创建的临时目录绑定写回数据库，避免后续 GetSessionWorkspacePath 查询不到
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var chatSessionRepository = scope.ServiceProvider.GetService<IChatSessionRepository>();
                    if (chatSessionRepository != null)
                    {
                        _ = chatSessionRepository.UpdateWorkspaceBindingAsync(sessionId, workspacePath, isCustomWorkspace: false)
                            .GetAwaiter().GetResult();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "写回会话工作目录绑定失败(可忽略): {SessionId}", sessionId);
                }
                
                return workspacePath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建会话工作目录失败: {SessionId}", sessionId);
                // 创建失败直接抛出异常，不降级使用根目录
                throw new InvalidOperationException($"创建会话 {sessionId} 工作目录失败: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// 清理指定会话的工作区
    /// </summary>
    public void CleanupSessionWorkspace(string sessionId)
    {
        // 清理持久化进程
        _processManager.CleanupSessionProcesses(sessionId);

        // 清理CLI thread id
        lock (_cliSessionLock)
        {
            _cliThreadIds.Remove(sessionId);
        }

        string? workspacePath = null;
        bool isCustomWorkspace = false;

        // 查询会话信息判断目录类型
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var chatSessionRepository = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
            var session = chatSessionRepository.GetByIdAsync(sessionId).GetAwaiter().GetResult();

            if (session != null)
            {
                workspacePath = session.WorkspacePath;
                isCustomWorkspace = session.IsCustomWorkspace;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "查询会话 {SessionId} 信息失败，将按临时目录处理", sessionId);
        }

        // 清理内存缓存
        lock (_workspaceLock)
        {
            if (_sessionWorkspaces.TryGetValue(sessionId, out var cachedPath))
            {
                workspacePath ??= cachedPath;
                _sessionWorkspaces.Remove(sessionId);
            }
        }

        // 自定义目录：只解除绑定，不删除内容
        if (isCustomWorkspace)
        {
            _logger.LogInformation("已解除自定义目录会话 {SessionId} 的绑定，保留目录内容: {Path}", sessionId, workspacePath);
            return;
        }

        // 临时目录：执行删除逻辑
        if (string.IsNullOrEmpty(workspacePath))
        {
            var workspaceRoot = GetEffectiveWorkspaceRoot();
            workspacePath = Path.Combine(workspaceRoot, sessionId);
        }

        try
        {
            var rootFullPath = Path.GetFullPath(GetEffectiveWorkspaceRoot());
            var workspaceFullPath = Path.GetFullPath(workspacePath);
            var expectedTempWorkspacePath = Path.GetFullPath(Path.Combine(rootFullPath, sessionId));

            // 防御：只允许删除 TempWorkspaceRoot 下的临时目录
            if (!IsPathWithinDirectory(rootFullPath, workspaceFullPath) ||
                !string.Equals(workspaceFullPath, expectedTempWorkspacePath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("跳过清理非临时目录: {SessionId}, {Path}", sessionId, workspaceFullPath);
                return;
            }

            if (Directory.Exists(workspaceFullPath))
            {
                try
                {
                    Directory.Delete(workspaceFullPath, recursive: true);
                    _logger.LogInformation("已清理临时会话 {SessionId} 的工作目录: {Path}", sessionId, workspaceFullPath);
                }
                catch (Exception ex)
                {
                    // Windows 上常见原因：只读属性、被占用。
                    try
                    {
                        NormalizeDirectoryAttributes(workspaceFullPath);
                        Directory.Delete(workspaceFullPath, recursive: true);
                        _logger.LogInformation("已清理临时会话 {SessionId} 的工作目录(重试成功): {Path}", sessionId, workspaceFullPath);
                    }
                    catch
                    {
                        _logger.LogWarning(ex, "清理临时会话工作目录失败: {SessionId}, {Path}", sessionId, workspaceFullPath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理临时会话工作目录失败(路径解析异常): {SessionId}", sessionId);
        }
    }

    private static void NormalizeDirectoryAttributes(string directoryPath)
    {
        try
        {
            // 先处理文件
            foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                    // 忽略单个文件失败
                }
            }

            // 再处理目录
            foreach (var dir in Directory.EnumerateDirectories(directoryPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    new DirectoryInfo(dir).Attributes = FileAttributes.Normal;
                }
                catch
                {
                    // 忽略单个目录失败
                }
            }

            // 最后处理根目录
            try
            {
                new DirectoryInfo(directoryPath).Attributes = FileAttributes.Normal;
            }
            catch
            {
                // ignore
            }
        }
        catch
        {
            // ignore
        }
    }
    
    /// <summary>
    /// 使用适配器从CLI输出中解析thread id
    /// </summary>
    private string? ParseCliThreadId(string output, ICliToolAdapter adapter)
    {
        try
        {
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine))
                {
                    continue;
                }

                // 使用适配器解析输出行
                var outputEvent = adapter.ParseOutputLine(trimmedLine);
                if (outputEvent != null)
                {
                    var sessionId = adapter.ExtractSessionId(outputEvent);
                    if (!string.IsNullOrWhiteSpace(sessionId))
                    {
                        _logger.LogDebug("从输出中解析到CLI thread id: {ThreadId}", sessionId);
                        return sessionId;
                    }
                }
            }

            _logger.LogDebug("未能从CLI输出中解析到thread id");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "解析CLI thread id失败");
            return null;
        }
    }

    /// <summary>
    /// 从CLI输出中构建更具体的失败信息，优先返回适配器解析出的上游错误
    /// </summary>
    private string BuildDetailedFailureMessage(string output, ICliToolAdapter? adapter, int exitCode)
    {
        if (adapter != null)
        {
            var parsedFailureMessage = ParseCliFailureMessage(output, adapter);
            if (!string.IsNullOrWhiteSpace(parsedFailureMessage))
            {
                return parsedFailureMessage;
            }
        }

        return $"执行失败（退出码 {exitCode}）";
    }

    /// <summary>
    /// 使用适配器从CLI输出中解析失败原因
    /// </summary>
    private string? ParseCliFailureMessage(string output, ICliToolAdapter adapter)
    {
        try
        {
            string? lastFallbackMessage = null;
            string? lastDetailedMessage = null;

            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine))
                {
                    continue;
                }

                var outputEvent = adapter.ParseOutputLine(trimmedLine);
                if (outputEvent == null || !outputEvent.IsError)
                {
                    continue;
                }

                var detailedMessage = SelectFailureMessage(outputEvent, skipTransientMessages: true);
                if (!string.IsNullOrWhiteSpace(detailedMessage))
                {
                    lastDetailedMessage = detailedMessage;
                }

                var fallbackMessage = SelectFailureMessage(outputEvent, skipTransientMessages: false);
                if (!string.IsNullOrWhiteSpace(fallbackMessage))
                {
                    lastFallbackMessage = fallbackMessage;
                }
            }

            return lastDetailedMessage ?? lastFallbackMessage;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "解析CLI失败信息失败");
            return null;
        }
    }

    private static string? SelectFailureMessage(CliOutputEvent outputEvent, bool skipTransientMessages)
    {
        var candidates = new[] { outputEvent.ErrorMessage, outputEvent.Content };

        foreach (var candidate in candidates)
        {
            var normalizedCandidate = NormalizeFailureMessage(candidate);
            if (string.IsNullOrWhiteSpace(normalizedCandidate))
            {
                continue;
            }

            if (skipTransientMessages && IsTransientFailureMessage(normalizedCandidate))
            {
                continue;
            }

            return normalizedCandidate;
        }

        return null;
    }

    private static string? NormalizeFailureMessage(string? message)
    {
        return string.IsNullOrWhiteSpace(message) ? null : message.Trim();
    }

    private static bool IsTransientFailureMessage(string message)
    {
        return message.StartsWith("Reconnecting...", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(message, "本轮交互失败。", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(message, "发生未知错误。", StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// 从Codex输出中解析thread id（兼容旧版session id格式）
    /// 已废弃，保留用于向后兼容
    /// </summary>
    [Obsolete("请使用 ParseCliThreadId 方法")]
    private string? ParseCodexThreadId(string output)
    {
        try
        {
            var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine))
                {
                    continue;
                }

                // 优先尝试解析JSONL格式
                if (trimmedLine.StartsWith("{", StringComparison.Ordinal))
                {
                    try
                    {
                        using var document = JsonDocument.Parse(trimmedLine);
                        var root = document.RootElement;

                        if (root.TryGetProperty("thread_id", out var threadIdElement))
                        {
                            var threadId = threadIdElement.GetString();
                            if (!string.IsNullOrWhiteSpace(threadId))
                            {
                                _logger.LogDebug("从JSONL输出中解析到thread id: {ThreadId}", threadId);
                                return threadId;
                            }
                        }

                        if (root.TryGetProperty("item", out var itemElement) &&
                            itemElement.ValueKind == JsonValueKind.Object &&
                            itemElement.TryGetProperty("thread_id", out var itemThreadId))
                        {
                            var threadId = itemThreadId.GetString();
                            if (!string.IsNullOrWhiteSpace(threadId))
                            {
                                _logger.LogDebug("从JSONL item中解析到thread id: {ThreadId}", threadId);
                                return threadId;
                            }
                        }
                    }
                    catch (JsonException jsonEx)
                    {
                        _logger.LogDebug(jsonEx, "解析Codex JSONL行失败，将尝试旧格式");
                    }
                }

                // 兼容旧版本session id文本格式
                if (trimmedLine.StartsWith("session id:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmedLine.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        var legacyId = parts[1].Trim();
                        if (!string.IsNullOrWhiteSpace(legacyId))
                        {
                            _logger.LogDebug("从旧格式输出中解析到thread id: {ThreadId}", legacyId);
                            return legacyId;
                        }
                    }
                }
            }

            _logger.LogWarning("未能从Codex输出中解析到thread id");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "解析Codex thread id失败");
            return null;
        }
    }
    
    /// <summary>
    /// 转义JSON字符串中的特殊字符
    /// </summary>
    private string EscapeJsonString(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }
        
        return input
            .Replace("\\", "\\\\")  // 反斜杠
            .Replace("\"", "\\\"")  // 双引号
            .Replace("\n", "\\n")   // 换行
            .Replace("\r", "\\r")   // 回车
            .Replace("\t", "\\t");  // 制表符
    }

    /// <summary>
    /// 清理所有过期的会话工作区
    /// </summary>
    public void CleanupExpiredWorkspaces()
    {
        try
        {
            var workspaceRoot = GetEffectiveWorkspaceRoot();
            if (!Directory.Exists(workspaceRoot))
            {
                return;
            }

            var expirationTime = DateTime.UtcNow.AddHours(-_options.WorkspaceExpirationHours);
            var directories = Directory.GetDirectories(workspaceRoot);
            
            _logger.LogInformation("开始清理过期工作区,总共 {Count} 个目录", directories.Length);

            foreach (var dir in directories)
            {
                try
                {
                    var markerFile = Path.Combine(dir, ".workspace_info");
                    
                    // 检查标记文件的最后修改时间
                    DateTime lastAccessTime;
                    if (File.Exists(markerFile))
                    {
                        lastAccessTime = File.GetLastWriteTimeUtc(markerFile);
                    }
                    else
                    {
                        // 如果没有标记文件,使用目录的最后访问时间
                        lastAccessTime = Directory.GetLastWriteTimeUtc(dir);
                    }

                    if (lastAccessTime < expirationTime)
                    {
                        var sessionId = Path.GetFileName(dir);
                        
                        lock (_workspaceLock)
                        {
                            _sessionWorkspaces.Remove(sessionId);
                        }
                        
                        Directory.Delete(dir, recursive: true);
                        _logger.LogInformation("已清理过期工作区: {Path}, 最后访问时间: {Time}", dir, lastAccessTime);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "清理目录失败: {Dir}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "清理过期工作区失败");
        }
    }

    /// <summary>
    /// 获取会话工作区路径
    /// </summary>
    public string GetSessionWorkspacePath(string sessionId)
    {
        lock (_workspaceLock)
        {
            if (_sessionWorkspaces.TryGetValue(sessionId, out var path))
            {
                return path;
            }

            // 缓存丢失时查询数据库获取会话绑定的工作目录
            using var scope = _serviceProvider.CreateScope();
            var chatSessionRepository = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
            var session = chatSessionRepository.GetByIdAsync(sessionId).GetAwaiter().GetResult();

            if (session != null && !string.IsNullOrEmpty(session.WorkspacePath) && Directory.Exists(session.WorkspacePath))
            {
                _sessionWorkspaces[sessionId] = session.WorkspacePath;
                return session.WorkspacePath;
            }

            // 会话不存在或工作目录无效，抛出异常（不自动创建临时目录）
            throw new InvalidOperationException($"会话 {sessionId} 工作目录不存在或已被清理，请重新创建会话");
        }
    }
    
    /// <summary>
    /// 初始化会话工作区（可选择关联项目）
    /// </summary>
    public async Task<string> InitializeSessionWorkspaceAsync(string sessionId, string? projectId = null, bool includeGit = false)
    {
        // 先创建基本工作区
        var workspacePath = GetOrCreateSessionWorkspace(sessionId);
        
        // 如果指定了项目ID，从项目复制代码
        if (!string.IsNullOrEmpty(projectId))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var projectService = scope.ServiceProvider.GetService<IProjectService>();
                
                if (projectService != null)
                {
                    // 检查工作区是否为空（只有空工作区才复制项目代码）
                    var workspaceIsEmpty = !Directory.Exists(workspacePath) || 
                                           !Directory.EnumerateFileSystemEntries(workspacePath)
                                               .Any(e => !Path.GetFileName(e).StartsWith(".workspace"));
                    
                    if (workspaceIsEmpty)
                    {
                        var (success, errorMessage) = await projectService.CopyProjectToWorkspaceAsync(projectId, workspacePath, includeGit);
                        
                        if (success)
                        {
                            _logger.LogInformation("已从项目 {ProjectId} 复制代码到会话工作区 {SessionId}", projectId, sessionId);
                        }
                        else
                        {
                            _logger.LogWarning("从项目复制代码失败: {Error}", errorMessage);
                        }
                    }
                    else
                    {
                        _logger.LogInformation("会话工作区已有内容，跳过项目代码复制: {SessionId}", sessionId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "初始化项目代码到工作区失败: {SessionId}, {ProjectId}", sessionId, projectId);
            }
        }
        
        return workspacePath;
    }

    private static string BuildLowInterruptionArguments(
        CliToolConfig tool,
        ICliToolAdapter? adapter,
        CliSessionContext sessionContext)
    {
        if (string.IsNullOrWhiteSpace(sessionContext.CliThreadId))
        {
            throw new InvalidOperationException("Low-interruption continue requires an existing CLI thread/session id.");
        }

        if (adapter != null)
        {
            return adapter.BuildLowInterruptionArguments(tool, sessionContext);
        }

        if (!string.IsNullOrWhiteSpace(tool.LowInterruptionArgumentTemplate))
        {
            var arguments = tool.LowInterruptionArgumentTemplate
                .Replace("{cliThreadId}", sessionContext.CliThreadId, StringComparison.Ordinal)
                .Replace("{session}", sessionContext.CliThreadId, StringComparison.Ordinal)
                .Trim();

            while (arguments.Contains("  ", StringComparison.Ordinal))
            {
                arguments = arguments.Replace("  ", " ", StringComparison.Ordinal);
            }

            return arguments;
        }

        throw new InvalidOperationException($"CLI tool '{tool.Id}' does not support low-interruption continue.");
    }

    private static string? BuildStandardInput(
        CliToolConfig tool,
        ICliToolAdapter? adapter,
        CliSessionContext sessionContext,
        bool useLowInterruption,
        string? lowInterruptionPrompt)
    {
        if (!useLowInterruption)
        {
            return null;
        }

        if (IsCodexExecution(tool, adapter))
        {
            return string.IsNullOrWhiteSpace(lowInterruptionPrompt)
                ? LowInterruptionContinueDefaults.DefaultPrompt
                : lowInterruptionPrompt.Trim();
        }

        return null;
    }

    private static bool IsCodexExecution(CliToolConfig tool, ICliToolAdapter? adapter)
    {
        return string.Equals(tool.Id, "codex", StringComparison.OrdinalIgnoreCase)
               || adapter is CodexAdapter
               || (adapter?.SupportedToolIds.Any(static id =>
                   string.Equals(id, "codex", StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    private static void WriteStandardInput(Process process, string? standardInput)
    {
        if (!string.IsNullOrWhiteSpace(standardInput))
        {
            process.StandardInput.Write(standardInput);
        }

        process.StandardInput.Close();
    }

    /// <summary>
    /// ?????????,????????pm???????????????,???????????
    /// </summary>
    private string ResolveCommandPath(string command)
    {
        // 如果命令已经是绝对路径,直接返回
        if (Path.IsPathRooted(command))
        {
            return command;
        }

        // Windows系统下,尝试解析npm安装的CLI工具
        if (OperatingSystem.IsWindows() && 
            (command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || 
             command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
             !command.Contains("."))) // 没有扩展名的,也可能是npm工具
        {
            // 确保命令有.cmd扩展名
            var cmdFileName = command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || 
                              command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                ? command 
                : command + ".cmd";
            
            // 尝试从配置或自动检测获取npm全局路径
            var npmGlobalPath = GetNpmGlobalPath();
            
            if (!string.IsNullOrWhiteSpace(npmGlobalPath))
            {
                var fullPath = Path.Combine(npmGlobalPath, cmdFileName);
                
                // 检查文件是否存在,如果存在则使用完整路径
                if (File.Exists(fullPath))
                {
                    _logger.LogDebug("将相对命令 {Command} 解析为完整路径: {FullPath}", command, fullPath);
                    return fullPath;
                }
                
                _logger.LogDebug("npm目录中未找到命令: {FullPath}, 尝试使用系统PATH", fullPath);
            }
        }

        var resolvedFromPath = ResolveCommandFromPathEnvironment(command);
        if (!string.IsNullOrWhiteSpace(resolvedFromPath))
        {
            _logger.LogDebug("将系统PATH中的命令 {Command} 解析为完整路径: {FullPath}", command, resolvedFromPath);
            return resolvedFromPath;
        }

        // 否则返回原始命令(假设是系统PATH中的命令)
        if (OperatingSystem.IsWindows())
        {
            var pathResolvedCommand = TryResolveWindowsCommandPathFromPath(command);
            if (!string.IsNullOrWhiteSpace(pathResolvedCommand))
            {
                _logger.LogDebug("浠?PATH 瑙ｆ瀽鍛戒护 {Command} 鍒? {FullPath}", command, pathResolvedCommand);
                return pathResolvedCommand;
            }
        }

        return command;
    }

    private string? ResolveCommandFromPathEnvironment(string command)
    {
        if (string.IsNullOrWhiteSpace(command) ||
            command.Contains(Path.DirectorySeparatorChar) ||
            command.Contains(Path.AltDirectorySeparatorChar))
        {
            return null;
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalizedDirectory = directory.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(normalizedDirectory) || !Directory.Exists(normalizedDirectory))
            {
                continue;
            }

            foreach (var candidate in GetPathResolutionCandidates(normalizedDirectory, command))
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private IEnumerable<string> GetPathResolutionCandidates(string directory, string command)
    {
        if (Path.HasExtension(command))
        {
            yield return Path.Combine(directory, command);
            yield break;
        }

        if (!OperatingSystem.IsWindows())
        {
            yield return Path.Combine(directory, command);
            yield break;
        }

        var pathExt = Environment.GetEnvironmentVariable("PATHEXT");
        var extensions = string.IsNullOrWhiteSpace(pathExt)
            ? [".COM", ".EXE", ".BAT", ".CMD"]
            : pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var extension in extensions)
        {
            var normalizedExtension = extension.StartsWith(".", StringComparison.Ordinal)
                ? extension
                : "." + extension;

            yield return Path.Combine(directory, command + normalizedExtension);
        }
    }

    private string? GetWorkingDirectoryValidationError(string? workingDirectory, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return "工作目录为空，无法启动 CLI 进程。";
        }

        if (Directory.Exists(workingDirectory))
        {
            return null;
        }

        return $"会话 {sessionId} 的工作目录不存在: {workingDirectory}。请确认目录存在，或重新创建会话。";
    }

    /// <summary>
    /// 获取NPM全局安装路径（优先使用配置的路径，如果未配置则自动检测）
    /// </summary>
    private string? TryResolveWindowsCommandPathFromPath(string command)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var candidate in BuildWindowsCommandCandidates(command))
            {
                var fullPath = Path.Combine(directory, candidate);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> BuildWindowsCommandCandidates(string command)
    {
        var extension = Path.GetExtension(command);
        if (!string.IsNullOrWhiteSpace(extension))
        {
            yield return command;
            yield break;
        }

        yield return command + ".cmd";
        yield return command + ".bat";
        yield return command + ".exe";
        yield return command + ".com";
        yield return command;
    }

    private string? GetNpmGlobalPath()
    {
        // 如果配置中指定了路径,直接使用
        if (!string.IsNullOrWhiteSpace(_options.NpmGlobalPath))
        {
            _logger.LogDebug("使用配置的NPM全局路径: {Path}", _options.NpmGlobalPath);
            return _options.NpmGlobalPath;
        }

        // 尝试自动检测NPM全局路径
        try
        {
            // 方法1: 通过执行 npm config get prefix 获取
            var startInfo = new ProcessStartInfo
            {
                FileName = "npm",
                Arguments = "config get prefix",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process != null)
            {
                process.WaitForExit(5000); // 5秒超时
                if (process.ExitCode == 0)
                {
                    var prefix = process.StandardOutput.ReadToEnd().Trim();
                    if (!string.IsNullOrWhiteSpace(prefix) && Directory.Exists(prefix))
                    {
                        _logger.LogInformation("自动检测到NPM全局路径: {Path}", prefix);
                        return prefix;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "自动检测NPM全局路径失败,尝试使用环境变量");
        }

        // 方法2: 尝试从环境变量中获取常见的NPM路径
        if (OperatingSystem.IsWindows())
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var npmPath = Path.Combine(appDataPath, "npm");
            
            if (Directory.Exists(npmPath))
            {
                _logger.LogInformation("通过AppData路径检测到NPM全局路径: {Path}", npmPath);
                return npmPath;
            }
        }
        else
        {
            // Linux/Mac 通常在 /usr/local/bin 或 ~/.npm-global
            var possiblePaths = new[] 
            { 
                "/usr/local/bin", 
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npm-global", "bin")
            };
            
            foreach (var path in possiblePaths)
            {
                if (Directory.Exists(path))
                {
                    _logger.LogInformation("检测到NPM全局路径: {Path}", path);
                    return path;
                }
            }
        }

        _logger.LogWarning("无法检测到NPM全局路径,将依赖系统PATH环境变量");
        return null;
    }

    private static bool IsPathWithinDirectory(string rootPath, string candidatePath)
    {
        var normalizedRoot = EnsureTrailingDirectorySeparator(Path.GetFullPath(rootPath));
        var normalizedCandidate = Path.GetFullPath(candidatePath);

        return normalizedCandidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string EnsureTrailingDirectorySeparator(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        return path[^1] == Path.DirectorySeparatorChar || path[^1] == Path.AltDirectorySeparatorChar
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    /// <summary>
    /// 将会话固定的 Provider 快照同步到当前 cc-switch 激活 Provider
    /// </summary>
    public async Task<CcSwitchSessionSnapshot?> SyncSessionCcSwitchSnapshotAsync(string sessionId, string? toolId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var session = await TryGetChatSessionAsync(sessionId);
        var effectiveToolId = NormalizeManagedToolId(toolId ?? session?.ToolId);
        if (string.IsNullOrWhiteSpace(effectiveToolId))
        {
            throw new InvalidOperationException("当前会话尚未绑定可同步的 CLI 工具。");
        }

        if (!_ccSwitchService.IsManagedTool(effectiveToolId))
        {
            throw new InvalidOperationException("当前会话未绑定由 cc-switch 管理的 CLI 工具。");
        }

        var sessionWorkspace = GetOrCreateSessionWorkspace(sessionId);
        return await MaterializeCcSwitchSessionSnapshotAsync(sessionId, effectiveToolId, sessionWorkspace, cancellationToken);
    }

    public async Task<CodexThreadProviderSyncResult> SyncCodexThreadProviderAsync(
        string sessionId,
        string? toolId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var cliThreadId = GetCliThreadId(sessionId);
        if (string.IsNullOrWhiteSpace(cliThreadId))
        {
            throw new InvalidOperationException("当前会话尚未绑定 CLI thread，无法同步 Codex provider。");
        }

        var session = await TryGetChatSessionAsync(sessionId);
        var effectiveToolId = NormalizeManagedToolId(toolId ?? session?.ToolId);
        if (string.IsNullOrWhiteSpace(effectiveToolId))
        {
            throw new InvalidOperationException("当前会话尚未绑定可同步的 CLI 工具。");
        }

        if (!_ccSwitchService.IsManagedTool(effectiveToolId))
        {
            throw new InvalidOperationException("当前会话未绑定由 cc-switch 管理的 CLI 工具。");
        }

        var snapshot = await SyncSessionCcSwitchSnapshotAsync(sessionId, effectiveToolId, cancellationToken);
        if (string.IsNullOrWhiteSpace(snapshot?.ProviderId))
        {
            throw new InvalidOperationException("当前 cc-switch 未激活可同步的 Provider。");
        }

        var sessionWorkspace = GetSessionWorkspacePath(sessionId);
        using var scope = _serviceProvider.CreateScope();
        var threadSyncService = scope.ServiceProvider.GetRequiredService<ICodexThreadProviderSyncService>();
        return await threadSyncService.SyncThreadProviderAsync(
            new CodexThreadProviderSyncRequest
            {
                SessionWorkspacePath = sessionWorkspace,
                ThreadId = cliThreadId,
                TargetProviderId = snapshot.ProviderId
            },
            cancellationToken);
    }

    private async Task<string?> EnsureManagedToolSessionSnapshotAsync(
        string sessionId,
        string toolId,
        string sessionWorkspace,
        CancellationToken cancellationToken)
    {
        var normalizedToolId = NormalizeManagedToolId(toolId);
        if (string.IsNullOrWhiteSpace(normalizedToolId) || !_ccSwitchService.IsManagedTool(normalizedToolId))
        {
            return null;
        }

        var session = await TryGetChatSessionAsync(sessionId);
        if (HasCcSwitchSnapshotForTool(session, normalizedToolId))
        {
            return GetCcSwitchSnapshotValidationError(session!, normalizedToolId);
        }

        try
        {
            await MaterializeCcSwitchSessionSnapshotAsync(sessionId, normalizedToolId, sessionWorkspace, cancellationToken);
            return null;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "同步会话 {SessionId} 的 cc-switch 快照失败: {ToolId}", sessionId, normalizedToolId);
            return ex.Message;
        }
    }

    private async Task<CcSwitchSessionSnapshot> MaterializeCcSwitchSessionSnapshotAsync(
        string sessionId,
        string toolId,
        string sessionWorkspace,
        CancellationToken cancellationToken)
    {
        var status = await _ccSwitchService.GetToolStatusAsync(toolId, cancellationToken);
        if (!status.IsLaunchReady)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(status.StatusMessage)
                    ? $"CLI 工具 '{ResolveManagedToolDisplayName(toolId)}' 依赖 cc-switch，但当前未就绪。"
                    : status.StatusMessage);
        }

        if (string.IsNullOrWhiteSpace(status.LiveConfigPath) || !File.Exists(status.LiveConfigPath))
        {
            throw new InvalidOperationException(
                $"未找到 {ResolveManagedToolDisplayName(toolId)} 当前激活 Provider 的 live 配置文件，请先在 cc-switch 中完成同步。");
        }

        var snapshotRelativePath = GetCcSwitchSnapshotRelativePath(toolId);
        var snapshotFullPath = Path.Combine(sessionWorkspace, snapshotRelativePath);
        var snapshotDirectory = Path.GetDirectoryName(snapshotFullPath);
        if (!string.IsNullOrWhiteSpace(snapshotDirectory))
        {
            Directory.CreateDirectory(snapshotDirectory);
        }

        if (string.Equals(toolId, "codex", StringComparison.OrdinalIgnoreCase))
        {
            var liveConfigContent = await File.ReadAllTextAsync(status.LiveConfigPath, cancellationToken);
            var sanitizedConfigContent = StripUnsupportedProjectCodexConfigSections(liveConfigContent);
            await WriteFileIfChangedAsync(snapshotFullPath, sanitizedConfigContent, cancellationToken);

            var baseConfigPath = GetCodexLaunchBaseConfigPath(sessionWorkspace);
            await WriteFileIfChangedAsync(baseConfigPath, sanitizedConfigContent, cancellationToken);

            await SyncCodexAuthSnapshotAsync(sessionWorkspace, status.LiveConfigPath, cancellationToken);
        }
        else
        {
            await using var source = new FileStream(status.LiveConfigPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            await using var target = new FileStream(snapshotFullPath, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(target, cancellationToken);
        }

        var snapshot = new CcSwitchSessionSnapshot
        {
            UsesSnapshot = true,
            ToolId = toolId,
            ProviderId = status.ActiveProviderId,
            ProviderName = status.ActiveProviderName,
            ProviderCategory = status.ActiveProviderCategory,
            SourceLiveConfigPath = status.LiveConfigPath,
            SnapshotRelativePath = snapshotRelativePath,
            SyncedAt = DateTime.Now
        };

        await PersistCcSwitchSessionSnapshotAsync(sessionId, toolId, sessionWorkspace, snapshot);
        return snapshot;
    }

    private async Task PersistCcSwitchSessionSnapshotAsync(
        string sessionId,
        string toolId,
        string workspacePath,
        CcSwitchSessionSnapshot snapshot)
    {
        using var scope = _serviceProvider.CreateScope();
        var chatSessionRepository = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
        var existingSession = await chatSessionRepository.GetByIdAsync(sessionId);

        if (existingSession != null)
        {
            existingSession.ToolId = toolId;
            existingSession.WorkspacePath ??= workspacePath;
            existingSession.IsWorkspaceValid = Directory.Exists(workspacePath);
            existingSession.UsesCcSwitchSnapshot = snapshot.UsesSnapshot;
            existingSession.CcSwitchSnapshotToolId = snapshot.ToolId;
            existingSession.CcSwitchProviderId = snapshot.ProviderId;
            existingSession.CcSwitchProviderName = snapshot.ProviderName;
            existingSession.CcSwitchProviderCategory = snapshot.ProviderCategory;
            existingSession.CcSwitchLiveConfigPath = snapshot.SourceLiveConfigPath;
            existingSession.CcSwitchSnapshotRelativePath = snapshot.SnapshotRelativePath;
            existingSession.CcSwitchSnapshotSyncedAt = snapshot.SyncedAt;
            existingSession.UpdatedAt = DateTime.Now;
            await chatSessionRepository.InsertOrUpdateAsync(existingSession);
            return;
        }

        var username = ResolveUsernameForToolOperation(null, sessionId) ?? "default";
        await chatSessionRepository.InsertOrUpdateAsync(new ChatSessionEntity
        {
            SessionId = sessionId,
            Username = username,
            Title = "新会话",
            WorkspacePath = workspacePath,
            ToolId = toolId,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            IsWorkspaceValid = Directory.Exists(workspacePath),
            UsesCcSwitchSnapshot = snapshot.UsesSnapshot,
            CcSwitchSnapshotToolId = snapshot.ToolId,
            CcSwitchProviderId = snapshot.ProviderId,
            CcSwitchProviderName = snapshot.ProviderName,
            CcSwitchProviderCategory = snapshot.ProviderCategory,
            CcSwitchLiveConfigPath = snapshot.SourceLiveConfigPath,
            CcSwitchSnapshotRelativePath = snapshot.SnapshotRelativePath,
            CcSwitchSnapshotSyncedAt = snapshot.SyncedAt
        });
    }

    private async Task<ChatSessionEntity?> TryGetChatSessionAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        using var scope = _serviceProvider.CreateScope();
        var chatSessionRepository = scope.ServiceProvider.GetService<IChatSessionRepository>();
        return chatSessionRepository == null
            ? null
            : await chatSessionRepository.GetByIdAsync(sessionId);
    }

    private async Task<SessionToolLaunchOverride?> GetEffectiveSessionLaunchOverrideAsync(string sessionId, string toolId)
    {
        var session = await TryGetChatSessionAsync(sessionId);
        if (session == null || string.IsNullOrWhiteSpace(session.ToolLaunchOverridesJson))
        {
            return null;
        }

        var overrides = SessionLaunchOverrideHelper.Deserialize(session.ToolLaunchOverridesJson);
        return SessionLaunchOverrideHelper.GetEffectiveOverride(
            overrides,
            toolId,
            session.ToolId,
            session.CcSwitchSnapshotToolId);
    }

    private async Task<CliSessionContext> BuildCliSessionContextAsync(
        string sessionId,
        string toolId,
        string sessionWorkspace,
        string? cliThreadId,
        Dictionary<string, string> environmentVariables,
        CancellationToken cancellationToken)
    {
        var launchOverride = await GetEffectiveSessionLaunchOverrideAsync(sessionId, toolId);
        var normalizedToolId = SessionLaunchOverrideHelper.NormalizeToolId(toolId);

        if (string.Equals(normalizedToolId, "codex", StringComparison.OrdinalIgnoreCase)
            && _ccSwitchService.IsManagedTool(normalizedToolId))
        {
            await PrepareManagedCodexLaunchConfigAsync(
                sessionWorkspace,
                environmentVariables,
                launchOverride,
                cancellationToken);
            await SyncCodexThreadArtifactsAsync(sessionWorkspace, cliThreadId, cancellationToken);

            ApplyManagedCodexLaunchEnvironment(sessionWorkspace, environmentVariables);
        }

        return new CliSessionContext
        {
            SessionId = sessionId,
            CliThreadId = cliThreadId,
            WorkingDirectory = sessionWorkspace,
            LaunchModelOverride = launchOverride?.Model,
            LaunchReasoningEffortOverride = launchOverride?.ReasoningEffort
        };
    }

    private async Task PrepareManagedCodexLaunchConfigAsync(
        string sessionWorkspace,
        Dictionary<string, string> environmentVariables,
        SessionToolLaunchOverride? launchOverride,
        CancellationToken cancellationToken)
    {
        var codexConfigPath = Path.Combine(sessionWorkspace, ".codex", "config.toml");
        var baseConfigPath = GetCodexLaunchBaseConfigPath(sessionWorkspace);
        var baseConfigDirectory = Path.GetDirectoryName(baseConfigPath);
        if (!string.IsNullOrWhiteSpace(baseConfigDirectory))
        {
            Directory.CreateDirectory(baseConfigDirectory);
        }

        string baseContent;
        if (File.Exists(baseConfigPath))
        {
            baseContent = await File.ReadAllTextAsync(baseConfigPath, cancellationToken);
        }
        else if (File.Exists(codexConfigPath))
        {
            baseContent = await File.ReadAllTextAsync(codexConfigPath, cancellationToken);
            await WriteFileIfChangedAsync(baseConfigPath, baseContent, cancellationToken);
        }
        else
        {
            baseContent = BuildCodexConfigContent(
                environmentVariables,
                enableGoalsFeature: await ShouldEnableCodexGoalsAsync(sessionWorkspace, cancellationToken));
        }

        baseContent = StripUnsupportedProjectCodexConfigSections(baseContent);
        if (await ShouldEnableCodexGoalsAsync(sessionWorkspace, cancellationToken))
        {
            baseContent = UpsertTomlBooleanSetting(baseContent, "features", "goals", true);
        }

        await WriteFileIfChangedAsync(baseConfigPath, baseContent, cancellationToken);

        var launchConfigContent = ApplyCodexLaunchOverride(baseContent, launchOverride);
        await WriteFileIfChangedAsync(codexConfigPath, launchConfigContent, cancellationToken);
    }

    private static string GetCodexLaunchBaseConfigPath(string sessionWorkspace)
    {
        return Path.Combine(sessionWorkspace, ".codex", CodexLaunchBaseConfigFileName);
    }

    private async Task SyncCodexAuthSnapshotAsync(
        string sessionWorkspace,
        string? sourceConfigPath,
        CancellationToken cancellationToken)
    {
        var snapshotAuthPath = GetCodexSessionAuthPath(sessionWorkspace);
        var sourceAuthPath = ResolveSourceCodexAuthPath(sourceConfigPath);
        if (string.IsNullOrWhiteSpace(sourceAuthPath) || !File.Exists(sourceAuthPath))
        {
            if (File.Exists(snapshotAuthPath))
            {
                File.Delete(snapshotAuthPath);
            }

            return;
        }

        var authContent = await File.ReadAllTextAsync(sourceAuthPath, cancellationToken);
        await WriteFileIfChangedAsync(snapshotAuthPath, authContent, cancellationToken);
    }

    private static async Task SyncCodexThreadArtifactsAsync(
        string sessionWorkspace,
        string? cliThreadId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cliThreadId))
        {
            return;
        }

        var sourceConfigDirectory = ResolveSourceCodexConfigDirectory();
        if (string.IsNullOrWhiteSpace(sourceConfigDirectory) || !Directory.Exists(sourceConfigDirectory))
        {
            return;
        }

        var targetConfigDirectory = GetCodexSessionConfigDirectory(sessionWorkspace);
        var sourceConfigFullPath = Path.GetFullPath(sourceConfigDirectory);
        var targetConfigFullPath = Path.GetFullPath(targetConfigDirectory);
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(sourceConfigFullPath, targetConfigFullPath, pathComparison))
        {
            return;
        }

        foreach (var rootDirectoryName in new[] { "sessions", "archived_sessions" })
        {
            var sourceRootDirectory = Path.Combine(sourceConfigDirectory, rootDirectoryName);
            if (!Directory.Exists(sourceRootDirectory))
            {
                continue;
            }

            foreach (var sourceArtifactPath in Directory.EnumerateFiles(
                         sourceRootDirectory,
                         $"*{cliThreadId}*.jsonl",
                         SearchOption.AllDirectories))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var relativeArtifactPath = Path.GetRelativePath(sourceConfigDirectory, sourceArtifactPath);
                var targetArtifactPath = Path.Combine(targetConfigDirectory, relativeArtifactPath);
                await CopyCodexThreadArtifactIfChangedAsync(sourceArtifactPath, targetArtifactPath, cancellationToken);
            }
        }
    }

    private static void ApplyManagedCodexLaunchEnvironment(
        string sessionWorkspace,
        Dictionary<string, string> environmentVariables)
    {
        environmentVariables["CODEX_HOME"] = GetCodexSessionConfigDirectory(sessionWorkspace);
        environmentVariables["HOME"] = sessionWorkspace;
        environmentVariables["USERPROFILE"] = sessionWorkspace;
    }

    private static string GetCodexSessionConfigDirectory(string sessionWorkspace)
    {
        return Path.Combine(sessionWorkspace, ".codex");
    }

    private static string GetCodexSessionAuthPath(string sessionWorkspace)
    {
        return Path.Combine(GetCodexSessionConfigDirectory(sessionWorkspace), "auth.json");
    }

    private static string ResolveSourceCodexAuthPath(string? sourceConfigPath = null)
    {
        if (!string.IsNullOrWhiteSpace(sourceConfigPath))
        {
            var sourceConfigDirectory = Path.GetDirectoryName(sourceConfigPath);
            if (!string.IsNullOrWhiteSpace(sourceConfigDirectory))
            {
                var colocatedAuthPath = Path.Combine(sourceConfigDirectory, "auth.json");
                if (File.Exists(colocatedAuthPath))
                {
                    return colocatedAuthPath;
                }
            }
        }

        var codexConfigDirectory = ResolveSourceCodexConfigDirectory();
        return string.IsNullOrWhiteSpace(codexConfigDirectory)
            ? string.Empty
            : Path.Combine(codexConfigDirectory, "auth.json");
    }

    private static string ResolveSourceCodexConfigDirectory()
    {
        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(codexHome))
        {
            return NormalizeCodexConfigDirectory(codexHome);
        }

        var homePath = Environment.GetEnvironmentVariable("HOME");
        if (string.IsNullOrWhiteSpace(homePath))
        {
            homePath = Environment.GetEnvironmentVariable("USERPROFILE");
        }

        if (string.IsNullOrWhiteSpace(homePath))
        {
            homePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return string.IsNullOrWhiteSpace(homePath)
            ? string.Empty
            : NormalizeCodexConfigDirectory(homePath);
    }

    private static string NormalizeCodexConfigDirectory(string basePath)
    {
        if (string.IsNullOrWhiteSpace(basePath))
        {
            return string.Empty;
        }

        var trimmedPath = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetFileName(trimmedPath), ".codex", StringComparison.OrdinalIgnoreCase)
            ? trimmedPath
            : Path.Combine(trimmedPath, ".codex");
    }

    private static string ApplyCodexLaunchOverride(string baseContent, SessionToolLaunchOverride? launchOverride)
    {
        var mergedContent = baseContent;
        if (launchOverride == null)
        {
            return mergedContent;
        }

        if (!string.IsNullOrWhiteSpace(launchOverride.Model))
        {
            mergedContent = UpsertTomlStringSetting(mergedContent, "model", launchOverride.Model);
        }

        if (!string.IsNullOrWhiteSpace(launchOverride.ReasoningEffort))
        {
            mergedContent = UpsertTomlStringSetting(mergedContent, "model_reasoning_effort", launchOverride.ReasoningEffort);
        }

        return mergedContent;
    }

    private static string StripUnsupportedProjectCodexConfigSections(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        var normalizedContent = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalizedContent.Split('\n');
        var keptLines = new List<string>(lines.Length);
        var skipCurrentSection = false;

        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (trimmedLine.StartsWith("[", StringComparison.Ordinal) && trimmedLine.EndsWith("]", StringComparison.Ordinal))
            {
                var sectionName = trimmedLine[1..^1].Trim();
                skipCurrentSection = string.Equals(sectionName, "mcp_servers.claude", StringComparison.OrdinalIgnoreCase);
            }

            if (skipCurrentSection)
            {
                continue;
            }

            keptLines.Add(line);
        }

        var sanitizedContent = string.Join("\n", keptLines);
        sanitizedContent = Regex.Replace(sanitizedContent, @"\n{3,}", "\n\n", RegexOptions.CultureInvariant);
        if (content.EndsWith("\r\n", StringComparison.Ordinal))
        {
            sanitizedContent = sanitizedContent.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        return sanitizedContent.TrimEnd('\r', '\n') + Environment.NewLine;
    }

    private static string UpsertTomlStringSetting(string content, string key, string value)
    {
        var pattern = $"(?m)^(?<prefix>\\s*{Regex.Escape(key)}\\s*=\\s*)\\\".*?\\\"\\s*$";
        var escapedValue = EscapeTomlString(value);
        if (Regex.IsMatch(content, pattern))
        {
            return Regex.Replace(content, pattern, match => $"{match.Groups["prefix"].Value}\"{escapedValue}\"");
        }

        var normalizedContent = content;
        if (!normalizedContent.EndsWith("\n", StringComparison.Ordinal))
        {
            normalizedContent += Environment.NewLine;
        }

        return normalizedContent + $"{key} = \"{escapedValue}\"{Environment.NewLine}";
    }

    private static string UpsertTomlBooleanSetting(string content, string sectionName, string key, bool value)
    {
        var normalizedContent = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalizedContent.Split('\n').ToList();
        var sectionHeader = $"[{sectionName}]";
        var assignment = $"{key} = {value.ToString().ToLowerInvariant()}";
        var sectionIndex = lines.FindIndex(line => string.Equals(line.Trim(), sectionHeader, StringComparison.OrdinalIgnoreCase));

        if (sectionIndex >= 0)
        {
            for (var i = sectionIndex + 1; i < lines.Count; i++)
            {
                var trimmedLine = lines[i].Trim();
                if (trimmedLine.StartsWith("[", StringComparison.Ordinal) && trimmedLine.EndsWith("]", StringComparison.Ordinal))
                {
                    lines.Insert(i, assignment);
                    return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
                }

                if (trimmedLine.StartsWith($"{key} =", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = assignment;
                    return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
                }
            }

            lines.Add(assignment);
            return string.Join(Environment.NewLine, lines).TrimEnd() + Environment.NewLine;
        }

        var builder = normalizedContent.TrimEnd('\n');
        if (!string.IsNullOrWhiteSpace(builder))
        {
            builder += "\n\n";
        }

        builder += $"{sectionHeader}\n{assignment}\n";
        return builder.Replace("\n", Environment.NewLine, StringComparison.Ordinal);
    }

    private static string EscapeTomlString(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static async Task WriteFileIfChangedAsync(string path, string content, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(path))
        {
            var existingContent = await File.ReadAllTextAsync(path, cancellationToken);
            if (string.Equals(existingContent, content, StringComparison.Ordinal))
            {
                return;
            }
        }

        await File.WriteAllTextAsync(path, content, cancellationToken);
    }

    private static async Task CopyCodexThreadArtifactIfChangedAsync(
        string sourcePath,
        string targetPath,
        CancellationToken cancellationToken)
    {
        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrWhiteSpace(targetDirectory))
        {
            Directory.CreateDirectory(targetDirectory);
        }

        var sourceContent = await File.ReadAllTextAsync(sourcePath, cancellationToken);
        if (File.Exists(targetPath))
        {
            var targetContent = await File.ReadAllTextAsync(targetPath, cancellationToken);
            if (string.Equals(sourceContent, targetContent, StringComparison.Ordinal))
            {
                return;
            }

            if (IsArtifactContentContinuation(targetContent, sourceContent))
            {
                return;
            }

            if (!IsArtifactContentContinuation(sourceContent, targetContent))
            {
                var sourceLastWriteUtc = File.GetLastWriteTimeUtc(sourcePath);
                var targetLastWriteUtc = File.GetLastWriteTimeUtc(targetPath);
                if (targetLastWriteUtc > sourceLastWriteUtc
                    || (targetLastWriteUtc == sourceLastWriteUtc && targetContent.Length >= sourceContent.Length))
                {
                    return;
                }
            }
        }

        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(target, cancellationToken);
    }

    private static bool IsArtifactContentContinuation(string candidateContent, string baselineContent)
    {
        if (string.IsNullOrEmpty(candidateContent) || string.IsNullOrEmpty(baselineContent))
        {
            return false;
        }

        return candidateContent.Length > baselineContent.Length
               && candidateContent.StartsWith(baselineContent, StringComparison.Ordinal);
    }

    private bool HasCcSwitchSnapshotForTool(ChatSessionEntity? session, string toolId)
    {
        return session != null
            && session.UsesCcSwitchSnapshot
            && string.Equals(NormalizeManagedToolId(session.CcSwitchSnapshotToolId), toolId, StringComparison.OrdinalIgnoreCase);
    }

    private string? GetCcSwitchSnapshotValidationError(ChatSessionEntity session, string toolId)
    {
        if (string.IsNullOrWhiteSpace(session.CcSwitchSnapshotRelativePath))
        {
            return BuildCcSwitchSnapshotSyncRequiredMessage(toolId);
        }

        var workspacePath = ResolveSessionWorkspacePath(session.SessionId, session.WorkspacePath);
        var snapshotFullPath = Path.Combine(workspacePath, session.CcSwitchSnapshotRelativePath);
        if (!File.Exists(snapshotFullPath))
        {
            return BuildCcSwitchSnapshotSyncRequiredMessage(toolId);
        }

        return null;
    }

    private string ResolveSessionWorkspacePath(string sessionId, string? workspacePath)
    {
        if (!string.IsNullOrWhiteSpace(workspacePath))
        {
            return workspacePath;
        }

        lock (_workspaceLock)
        {
            if (_sessionWorkspaces.TryGetValue(sessionId, out var cachedWorkspace))
            {
                return cachedWorkspace;
            }
        }

        return Path.Combine(GetEffectiveWorkspaceRoot(), sessionId);
    }

    private static string GetCcSwitchSnapshotRelativePath(string toolId)
    {
        return NormalizeManagedToolId(toolId) switch
        {
            "claude-code" => Path.Combine(".claude", "settings.json"),
            "codex" => Path.Combine(".codex", "config.toml"),
            "opencode" => "opencode.json",
            _ => throw new InvalidOperationException($"工具 '{toolId}' 不支持 cc-switch 会话快照。")
        };
    }

    private static string NormalizeManagedToolId(string? toolId)
    {
        return toolId?.Trim().ToLowerInvariant() switch
        {
            "claude" => "claude-code",
            "opencode-cli" => "opencode",
            var value => value ?? string.Empty
        };
    }

    private static string ResolveManagedToolDisplayName(string toolId)
    {
        return NormalizeManagedToolId(toolId) switch
        {
            "claude-code" => "Claude Code",
            "codex" => "Codex",
            "opencode" => "OpenCode",
            _ => toolId
        };
    }

    private string BuildCcSwitchSnapshotSyncRequiredMessage(string toolId)
    {
        var toolName = ResolveManagedToolDisplayName(toolId);
        return $"当前会话固定的 {toolName} Provider 快照已缺失或不可用。请点击“同步到当前 cc-switch Provider”后再继续。";
    }

    /// <summary>
    /// 获取指定工具的环境变量配置
    /// </summary>
    public async Task<Dictionary<string, string>> GetToolEnvironmentVariablesAsync(string toolId, string? username = null)
    {
        var normalizedToolId = NormalizeManagedToolId(toolId);
        if (_ccSwitchService.IsManagedTool(normalizedToolId))
        {
            try
            {
                var status = await _ccSwitchService.GetToolStatusAsync(normalizedToolId);
                if (!status.IsLaunchReady)
                {
                    _logger.LogWarning("cc-switch 受管工具 {ToolId} 当前未就绪，忽略 WebCode 本地环境变量。Reason={Reason}", normalizedToolId, status.StatusMessage);
                }
                else
                {
                    _logger.LogInformation("cc-switch 受管工具 {ToolId} 通过会话快照或 live 配置驱动，WebCode 不再注入本地环境变量。", normalizedToolId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取 cc-switch 状态失败，工具 {ToolId} 将不注入本地环境变量", normalizedToolId);
            }

            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var resolvedUsername = ResolveUsernameForToolOperation(username);
            using var scope = _serviceProvider.CreateScope();
            var envService = scope.ServiceProvider.GetRequiredService<ICliToolEnvironmentService>();
            var dbEnvVars = await envService.GetEnvironmentVariablesAsync(toolId, resolvedUsername);

            // 默认使用数据库配置
            _logger.LogInformation("获取工具 {ToolId} 的环境变量，共 {Count} 个", toolId, dbEnvVars.Count);
            foreach (var kvp in dbEnvVars)
            {
                var maskedValue = kvp.Value.Length > 8
                    ? kvp.Value.Substring(0, 4) + "****"
                    : "****";
                _logger.LogDebug("  环境变量: {Key} = {Value}", kvp.Key, maskedValue);
            }

            return dbEnvVars;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取工具 {ToolId} 的环境变量失败", toolId);

            // 降级到appsettings配置
            var tool = GetTool(toolId, username);
            return tool?.EnvironmentVariables ?? new Dictionary<string, string>();
        }
    }

    private void RewriteClaudeLaunchToNode(ProcessStartInfo startInfo, CliToolConfig tool, string commandPath, string originalArguments)
    {
        var isWindows = OperatingSystem.IsWindows();
        var isClaudeTool = IsClaudeTool(tool, commandPath);
        _logger.LogInformation("Claude 启动重写检查: IsWindows={IsWindows}, IsClaudeTool={IsClaudeTool}, CommandPath={CommandPath}", isWindows, isClaudeTool, commandPath);

        if (!isWindows || !isClaudeTool)
        {
            return;
        }

        var extension = Path.GetExtension(commandPath);
        if (!string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Claude 启动重写跳过：命令不是 .cmd，Extension={Extension}", extension);
            return;
        }

        var commandDirectory = Path.GetDirectoryName(commandPath);
        if (string.IsNullOrWhiteSpace(commandDirectory))
        {
            _logger.LogWarning("Claude 启动重写跳过：无法获取命令目录，CommandPath={CommandPath}", commandPath);
            return;
        }

        var cliJsPath = Path.Combine(commandDirectory, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
        _logger.LogInformation("Claude 启动重写检查 cli.js 路径: {CliJsPath}, Exists={Exists}", cliJsPath, File.Exists(cliJsPath));
        if (!File.Exists(cliJsPath))
        {
            return;
        }

        var preferredNodePath = GetPreferredNodeExecutablePath();
        _logger.LogInformation("Claude 启动重写检查 Node.js 路径: {NodePath}", preferredNodePath ?? "<null>");
        if (string.IsNullOrWhiteSpace(preferredNodePath) || !File.Exists(preferredNodePath))
        {
            _logger.LogWarning("Claude 启动重写跳过：未找到可用的 Node.js");
            return;
        }

        startInfo.FileName = preferredNodePath;
        startInfo.Arguments = $"\"{cliJsPath}\" {originalArguments}";
        _logger.LogInformation("Claude Code 改为直接使用 Node.js 启动: {NodePath}", preferredNodePath);
    }

    private void RewriteCodexLaunchToNode(ProcessStartInfo startInfo, CliToolConfig tool, string commandPath, string originalArguments)
    {
        var isWindows = OperatingSystem.IsWindows();
        var isCodexTool = IsCodexTool(tool, commandPath);
        _logger.LogInformation("Codex 启动重写检查: IsWindows={IsWindows}, IsCodexTool={IsCodexTool}, CommandPath={CommandPath}", isWindows, isCodexTool, commandPath);

        if (!isWindows || !isCodexTool)
        {
            return;
        }

        var extension = Path.GetExtension(commandPath);
        if (!string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Codex 启动重写跳过：命令不是 .cmd，Extension={Extension}", extension);
            return;
        }

        var commandDirectory = Path.GetDirectoryName(commandPath);
        if (string.IsNullOrWhiteSpace(commandDirectory))
        {
            _logger.LogWarning("Codex 启动重写跳过：无法获取命令目录，CommandPath={CommandPath}", commandPath);
            return;
        }

        var cliJsPath = Path.Combine(commandDirectory, "node_modules", "@openai", "codex", "bin", "codex.js");
        _logger.LogInformation("Codex 启动重写检查 codex.js 路径: {CliJsPath}, Exists={Exists}", cliJsPath, File.Exists(cliJsPath));
        if (!File.Exists(cliJsPath))
        {
            return;
        }

        var preferredNodePath = GetPreferredNodeExecutablePath();
        _logger.LogInformation("Codex 启动重写检查 Node.js 路径: {NodePath}", preferredNodePath ?? "<null>");
        if (string.IsNullOrWhiteSpace(preferredNodePath) || !File.Exists(preferredNodePath))
        {
            _logger.LogWarning("Codex 启动重写跳过：未找到可用的 Node.js");
            return;
        }

        startInfo.FileName = preferredNodePath;
        startInfo.Arguments = $"\"{cliJsPath}\" {originalArguments}";
        _logger.LogInformation("Codex 改为直接使用 Node.js 启动: {NodePath}", preferredNodePath);
    }

    private void EnsurePreferredNodeForClaude(ProcessStartInfo startInfo, CliToolConfig tool, string commandPath)
    {
        if (!OperatingSystem.IsWindows() || !IsClaudeTool(tool, commandPath))
        {
            return;
        }

        var preferredNodePath = GetPreferredNodeExecutablePath();
        if (string.IsNullOrWhiteSpace(preferredNodePath))
        {
            return;
        }

        var preferredNodeDirectory = Path.GetDirectoryName(preferredNodePath);
        if (string.IsNullOrWhiteSpace(preferredNodeDirectory))
        {
            return;
        }

        var currentPath = (startInfo.EnvironmentVariables.ContainsKey("PATH")
            ? startInfo.EnvironmentVariables["PATH"]
            : Environment.GetEnvironmentVariable("PATH")) ?? string.Empty;

        var reorderedEntries = currentPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(entry => !string.Equals(entry, preferredNodeDirectory, StringComparison.OrdinalIgnoreCase));

        startInfo.EnvironmentVariables["PATH"] = string.Join(
            Path.PathSeparator,
            new[] { preferredNodeDirectory }.Concat(reorderedEntries));

        _logger.LogInformation("Claude Code 优先使用 Node.js: {NodePath}", preferredNodePath);
    }

    private string? GetPreferredNodeExecutablePath()
    {
        lock (_preferredNodeExecutableLock)
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
                        _logger.LogInformation("优先选择 fnm Node.js: {NodePath}", _preferredNodeExecutablePath);
                        return _preferredNodeExecutablePath;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "扫描 fnm node.exe 路径失败");
            }

            var programFilesNode = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
            if (File.Exists(programFilesNode))
            {
                _preferredNodeExecutablePath = programFilesNode;
                _logger.LogInformation("回退使用系统 Node.js: {NodePath}", _preferredNodeExecutablePath);
                return _preferredNodeExecutablePath;
            }

            _logger.LogWarning("未找到可用的 node.exe");
            return null;
        }
    }

    private static bool IsClaudeTool(CliToolConfig tool, string commandPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(commandPath);
        return string.Equals(tool.Id, "claude-code", StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool.Id, "claude", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "claude", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCodexTool(CliToolConfig tool, string commandPath)
    {
        var fileName = Path.GetFileNameWithoutExtension(commandPath);
        return string.Equals(tool.Id, "codex", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fileName, "codex", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 保存指定工具的环境变量配置到数据库
    /// </summary>
    public async Task<bool> SaveToolEnvironmentVariablesAsync(string toolId, Dictionary<string, string> envVars, string? username = null)
    {
        var normalizedToolId = NormalizeManagedToolId(toolId);
        if (_ccSwitchService.IsManagedTool(normalizedToolId))
        {
            _logger.LogWarning("工具 {ToolId} 受 cc-switch 管理，拒绝保存 WebCode 本地环境变量", normalizedToolId);
            return false;
        }

        try
        {
            var resolvedUsername = ResolveUsernameForToolOperation(username);
            using var scope = _serviceProvider.CreateScope();
            var envService = scope.ServiceProvider.GetRequiredService<ICliToolEnvironmentService>();
            return await envService.SaveEnvironmentVariablesAsync(toolId, envVars, resolvedUsername);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存工具 {ToolId} 的环境变量失败", toolId);
            return false;
        }
    }

    private string? ResolveUsernameForToolOperation(string? username, string? sessionId = null)
    {
        if (!string.IsNullOrWhiteSpace(username))
        {
            return username.Trim();
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                var sessionRepository = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
                var session = sessionRepository.GetByIdAsync(sessionId).GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(session?.Username))
                {
                    return session.Username;
                }
            }

            var userContextService = scope.ServiceProvider.GetService<IUserContextService>();
            return userContextService?.GetCurrentUsername();
        }
        catch
        {
            return username;
        }
    }

    /// <summary>
    /// 为 Codex CLI 动态生成配置文件
    /// Codex CLI 优先读取配置文件而非环境变量，因此需要在执行前根据数据库中的环境变量生成配置文件
    /// 使用哈希值缓存，只在配置变化时才重新生成文件
    /// </summary>
    private void GenerateCodexConfigFile(Dictionary<string, string> envVars)
    {
        try
        {
            var configContent = BuildCodexConfigContent(
                envVars,
                enableGoalsFeature: ShouldEnableCodexGoalsSync());
            var configHash = configContent.GetHashCode().ToString();
            
            // 检查配置是否变化
            lock (_codexConfigLock)
            {
                if (_lastCodexConfigHash == configHash)
                {
                    _logger.LogDebug("Codex 配置未变化，跳过生成配置文件");
                    return;
                }
                _lastCodexConfigHash = configHash;
            }
            
            // 确定 Codex 配置目录
            var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                // 在 Docker 中可能需要使用 /home/appuser
                var appUserHome = "/home/appuser";
                if (Directory.Exists(appUserHome))
                {
                    homeDir = appUserHome;
                }
            }
            
            var codexConfigDir = Path.Combine(homeDir, ".codex");
            var codexConfigFile = Path.Combine(codexConfigDir, "config.toml");
            
            // 确保目录存在
            if (!Directory.Exists(codexConfigDir))
            {
                Directory.CreateDirectory(codexConfigDir);
            }
            
            // 写入配置文件
            File.WriteAllText(codexConfigFile, configContent);
            _logger.LogInformation("已生成 Codex 配置文件: {Path}", codexConfigFile);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "生成 Codex 配置文件失败");
        }
    }

    private async Task<string?> GetLaunchBlockingMessageAsync(string toolId, string sessionId, CancellationToken cancellationToken = default)
    {
        var normalizedToolId = NormalizeManagedToolId(toolId);
        if (_ccSwitchService.IsManagedTool(normalizedToolId))
        {
            var session = await TryGetChatSessionAsync(sessionId);
            if (HasCcSwitchSnapshotForTool(session, normalizedToolId))
            {
                var snapshotValidationError = GetCcSwitchSnapshotValidationError(session!, normalizedToolId);
                return snapshotValidationError ?? null;
            }
        }

        return await GetLaunchBlockingMessageAsync(normalizedToolId, cancellationToken);
    }

    private async Task<bool> HasValidCcSwitchSnapshotAsync(string sessionId, string toolId)
    {
        var normalizedToolId = NormalizeManagedToolId(toolId);
        if (!_ccSwitchService.IsManagedTool(normalizedToolId))
        {
            return false;
        }

        var session = await TryGetChatSessionAsync(sessionId);
        return HasCcSwitchSnapshotForTool(session, normalizedToolId)
            && GetCcSwitchSnapshotValidationError(session!, normalizedToolId) == null;
    }

    private async Task<string?> GetLaunchBlockingMessageAsync(string toolId, CancellationToken cancellationToken = default)
    {
        var normalizedToolId = NormalizeManagedToolId(toolId);
        if (!_ccSwitchService.IsManagedTool(normalizedToolId))
        {
            return null;
        }

        try
        {
            var status = await _ccSwitchService.GetToolStatusAsync(normalizedToolId, cancellationToken);
            if (status.IsLaunchReady)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(status.StatusMessage)
                ? $"CLI 工具 '{status.ToolName}' 依赖 cc-switch，但当前未就绪。"
                : status.StatusMessage;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取 cc-switch 工具状态失败: {ToolId}", normalizedToolId);
            return $"CLI 工具 '{normalizedToolId}' 依赖 cc-switch，但当前状态无法读取。";
        }
    }

    private List<CliToolConfig> FilterCcSwitchLaunchReadyTools(IEnumerable<CliToolConfig> tools)
    {
        var toolList = tools.ToList();
        var managedToolIds = toolList
            .Select(tool => NormalizeManagedToolId(tool.Id))
            .Where(toolId => _ccSwitchService.IsManagedTool(toolId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (managedToolIds.Length == 0)
        {
            return toolList;
        }

        try
        {
            var statuses = _ccSwitchService
                .GetToolStatusesAsync(managedToolIds)
                .GetAwaiter()
                .GetResult();

            return toolList
                .Where(tool =>
                {
                    var normalizedToolId = NormalizeManagedToolId(tool.Id);
                    if (!_ccSwitchService.IsManagedTool(normalizedToolId))
                    {
                        return true;
                    }

                    if (statuses.TryGetValue(normalizedToolId, out var status) && status.IsLaunchReady)
                    {
                        return true;
                    }

                    var message = statuses.TryGetValue(normalizedToolId, out var blockedStatus)
                        ? blockedStatus.StatusMessage
                        : "未能读取 cc-switch 状态。";
                    _logger.LogInformation("过滤未就绪的 cc-switch 受管工具: {ToolId}, Reason={Reason}", normalizedToolId, message);
                    return false;
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "批量读取 cc-switch 状态失败，所有受管工具将被视为不可用");
            return toolList
                .Where(tool => !_ccSwitchService.IsManagedTool(NormalizeManagedToolId(tool.Id)))
                .ToList();
        }
    }

    private async Task<bool> ShouldEnableCodexGoalsAsync(string? workspacePath, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _goalCapabilityService.ProbeAsync(
                new GoalCapabilityContext
                {
                    ToolId = "codex",
                    WorkspacePath = workspacePath
                },
                forceRefresh: false,
                cancellationToken: cancellationToken);

            return result.State == GoalCapabilityState.Available;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "检测 Codex /goal 能力失败，当前会话将不注入 goals feature");
            return false;
        }
    }

    private bool ShouldEnableCodexGoalsSync()
    {
        try
        {
            var result = _goalCapabilityService.ProbeAsync(
                new GoalCapabilityContext
                {
                    ToolId = "codex"
                },
                forceRefresh: false).GetAwaiter().GetResult();

            return result.State == GoalCapabilityState.Available;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "检测 Codex /goal 能力失败，全局配置将不注入 goals feature");
            return false;
        }
    }

    private static string BuildCodexConfigContent(Dictionary<string, string> envVars, bool enableGoalsFeature)
    {
        var baseUrl = envVars.GetValueOrDefault("CODEX_BASE_URL", "https://api.routin.ai/v1");
        var model = envVars.GetValueOrDefault("CODEX_MODEL", "gpt-5.4");
        var providerName = envVars.GetValueOrDefault("CODEX_PROVIDER_NAME", "meteor-ai");
        var providerId = envVars.GetValueOrDefault("CODEX_MODEL_PROVIDER", providerName);
        var wireApi = envVars.GetValueOrDefault("CODEX_WIRE_API", "responses");
        var approvalPolicy = envVars.GetValueOrDefault("CODEX_APPROVAL_POLICY", "never");
        var reasoningEffort = envVars.GetValueOrDefault("CODEX_MODEL_REASONING_EFFORT", "xhigh");
        var reasoningSummary = envVars.GetValueOrDefault("CODEX_MODEL_REASONING_SUMMARY", "detailed");
        var modelVerbosity = envVars.GetValueOrDefault("CODEX_MODEL_VERBOSITY", "high");
        var sandboxMode = envVars.GetValueOrDefault("CODEX_SANDBOX_MODE", "danger-full-access");
        var maxContext = envVars.GetValueOrDefault("CODEX_MAX_CONTEXT", "1000000");
        var contextCompactLimit = envVars.GetValueOrDefault("CODEX_CONTEXT_COMPACT_LIMIT", "800000");
        var featureFlags = enableGoalsFeature
            ? "[features]\ngoals = true\n\n"
            : string.Empty;

        return $@"# Codex CLI 配置文件（由 WebCode 动态生成）

model = ""{model}""
model_provider = ""{providerId}""
disable_response_storage = true
max_context = {maxContext}
context_compact_limit = {contextCompactLimit}
approval_policy = ""{approvalPolicy}""
sandbox_mode = ""{sandboxMode}""

model_reasoning_effort = ""{reasoningEffort}""
model_reasoning_summary = ""{reasoningSummary}""
model_verbosity = ""{modelVerbosity}""
model_supports_reasoning_summaries = true

[model_providers.""{providerId}""]
name = ""{providerName}""
base_url = ""{baseUrl}""
requires_openai_auth = true
wire_api = ""{wireApi}""

{featureFlags}

[windows]
sandbox = ""elevated""
";
    }

    /// <summary>
    /// 获取会话工作区的文件内容
    /// </summary>
    public byte[]? GetWorkspaceFile(string sessionId, string relativePath)
    {
        try
        {
            var workspacePath = GetSessionWorkspacePath(sessionId);
            var fullPath = Path.Combine(workspacePath, relativePath);

            // 安全检查：确保文件在工作区内
            var normalizedWorkspace = Path.GetFullPath(workspacePath);
            var normalizedFile = Path.GetFullPath(fullPath);

            if (!IsPathWithinDirectory(normalizedWorkspace, normalizedFile))
            {
                _logger.LogWarning("尝试访问工作区外的文件: {File}", relativePath);
                return null;
            }

            if (!File.Exists(fullPath))
            {
                _logger.LogWarning("文件不存在: {File}", relativePath);
                return null;
            }

            return File.ReadAllBytes(fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取工作区文件失败: {SessionId}/{File}", sessionId, relativePath);
            return null;
        }
    }

    /// <summary>
    /// 获取会话工作区的所有文件（打包为ZIP）
    /// </summary>
    public byte[]? GetWorkspaceZip(string sessionId)
    {
        try
        {
            var workspacePath = GetSessionWorkspacePath(sessionId);

            if (!Directory.Exists(workspacePath))
            {
                _logger.LogWarning("工作区不存在: {SessionId}", sessionId);
                return null;
            }

            using var memoryStream = new MemoryStream();
            System.IO.Compression.ZipFile.CreateFromDirectory(
                workspacePath, 
                memoryStream, 
                System.IO.Compression.CompressionLevel.Optimal, 
                false);
            
            return memoryStream.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "打包工作区失败: {SessionId}", sessionId);
            return null;
        }
    }

    /// <summary>
    /// 上传文件到会话工作区
    /// </summary>
    public async Task<bool> UploadFileToWorkspaceAsync(string sessionId, string fileName, byte[] fileContent, string? relativePath = null)
    {
        try
        {
            var workspacePath = GetSessionWorkspacePath(sessionId);

            // 确保工作区存在
            if (!Directory.Exists(workspacePath))
            {
                Directory.CreateDirectory(workspacePath);
                _logger.LogInformation("创建会话工作区: {SessionId}", sessionId);
            }

            // 构建目标路径
            string targetPath;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                // 直接放在工作区根目录
                targetPath = Path.Combine(workspacePath, fileName);
            }
            else
            {
                // 放在指定的子目录
                var targetDir = Path.Combine(workspacePath, relativePath);
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }
                targetPath = Path.Combine(targetDir, fileName);
            }

            // 安全检查：确保文件在工作区内
            var normalizedWorkspace = Path.GetFullPath(workspacePath);
            var normalizedTarget = Path.GetFullPath(targetPath);

            if (!IsPathWithinDirectory(normalizedWorkspace, normalizedTarget))
            {
                _logger.LogWarning("尝试上传文件到工作区外: {File}", targetPath);
                return false;
            }

            // 写入文件
            await File.WriteAllBytesAsync(targetPath, fileContent);
            _logger.LogInformation("文件上传成功: {SessionId}/{File}, 大小: {Size} bytes", sessionId, Path.GetRelativePath(workspacePath, targetPath), fileContent.Length);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "上传文件到工作区失败: {SessionId}/{File}", sessionId, fileName);
            return false;
        }
    }

    public async Task<bool> CreateFolderInWorkspaceAsync(string sessionId, string folderPath)
    {
        try
        {
            var workspacePath = GetSessionWorkspacePath(sessionId);

            // 确保工作区存在
            if (!Directory.Exists(workspacePath))
            {
                Directory.CreateDirectory(workspacePath);
                _logger.LogInformation("创建会话工作区: {SessionId}", sessionId);
            }

            // 移除前导和尾随斜杠
            folderPath = folderPath.Trim('/', '\\');

            if (string.IsNullOrWhiteSpace(folderPath))
            {
                _logger.LogWarning("文件夹路径为空");
                return false;
            }

            // 构建目标路径
            var targetPath = Path.Combine(workspacePath, folderPath);

            // 安全检查：确保文件夹在工作区内
            var normalizedWorkspace = Path.GetFullPath(workspacePath);
            var normalizedTarget = Path.GetFullPath(targetPath);

            if (!IsPathWithinDirectory(normalizedWorkspace, normalizedTarget))
            {
                _logger.LogWarning("尝试在工作区外创建文件夹: {Folder}", targetPath);
                return false;
            }

            // 检查文件夹是否已存在
            if (Directory.Exists(targetPath))
            {
                _logger.LogInformation("文件夹已存在: {SessionId}/{Folder}", sessionId, folderPath);
                return true;
            }

            // 创建文件夹
            Directory.CreateDirectory(targetPath);
            _logger.LogInformation("文件夹创建成功: {SessionId}/{Folder}", sessionId, folderPath);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建文件夹失败: {SessionId}/{Folder}", sessionId, folderPath);
            return false;
        }
    }

    /// <summary>
    /// 删除会话工作区中的文件或文件夹
    /// </summary>
    public async Task<bool> DeleteWorkspaceItemAsync(string sessionId, string relativePath, bool isDirectory)
    {
        try
        {
            var workspacePath = GetSessionWorkspacePath(sessionId);

            if (!Directory.Exists(workspacePath))
            {
                _logger.LogWarning("工作区不存在: {SessionId}", sessionId);
                return false;
            }

            // 移除前导和尾随斜杠
            relativePath = relativePath.Trim('/', '\\');

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                _logger.LogWarning("路径为空");
                return false;
            }

            // 构建目标路径
            var targetPath = Path.Combine(workspacePath, relativePath);

            // 安全检查：确保路径在工作区内
            var normalizedWorkspace = Path.GetFullPath(workspacePath);
            var normalizedTarget = Path.GetFullPath(targetPath);

            if (!IsPathWithinDirectory(normalizedWorkspace, normalizedTarget))
            {
                _logger.LogWarning("尝试删除工作区外的项: {Path}", targetPath);
                return false;
            }

            // 删除文件或文件夹
            if (isDirectory)
            {
                if (!Directory.Exists(targetPath))
                {
                    _logger.LogWarning("文件夹不存在: {SessionId}/{Path}", sessionId, relativePath);
                    return false;
                }

                Directory.Delete(targetPath, recursive: true);
                _logger.LogInformation("文件夹删除成功: {SessionId}/{Path}", sessionId, relativePath);
            }
            else
            {
                if (!File.Exists(targetPath))
                {
                    _logger.LogWarning("文件不存在: {SessionId}/{Path}", sessionId, relativePath);
                    return false;
                }

                File.Delete(targetPath);
                _logger.LogInformation("文件删除成功: {SessionId}/{Path}", sessionId, relativePath);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除失败: {SessionId}/{Path}, IsDirectory: {IsDirectory}", sessionId, relativePath, isDirectory);
            return false;
        }
    }

    /// <summary>
    /// 移动会话工作区中的文件或文件夹
    /// </summary>
    public async Task<bool> MoveFileInWorkspaceAsync(string sessionId, string sourcePath, string targetPath)
    {
        try
        {
            var workspacePath = GetSessionWorkspacePath(sessionId);

            if (!Directory.Exists(workspacePath))
            {
                _logger.LogWarning("工作区不存在: {SessionId}", sessionId);
                return false;
            }

            // 移除前导和尾随斜杠
            sourcePath = sourcePath.Trim('/', '\\');
            targetPath = targetPath.Trim('/', '\\');

            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetPath))
            {
                _logger.LogWarning("源路径或目标路径为空");
                return false;
            }

            // 构建完整路径
            var fullSourcePath = Path.Combine(workspacePath, sourcePath);
            var fullTargetPath = Path.Combine(workspacePath, targetPath);

            // 安全检查：确保路径在工作区内
            var normalizedWorkspace = Path.GetFullPath(workspacePath);
            var normalizedSource = Path.GetFullPath(fullSourcePath);
            var normalizedTarget = Path.GetFullPath(fullTargetPath);

            if (!IsPathWithinDirectory(normalizedWorkspace, normalizedSource) ||
                !IsPathWithinDirectory(normalizedWorkspace, normalizedTarget))
            {
                _logger.LogWarning("尝试移动工作区外的项");
                return false;
            }

            // 检查源是否存在
            bool isDirectory = Directory.Exists(fullSourcePath);
            bool isFile = File.Exists(fullSourcePath);

            if (!isDirectory && !isFile)
            {
                _logger.LogWarning("源不存在: {SessionId}/{Source}", sessionId, sourcePath);
                return false;
            }

            // 确保目标目录存在
            var targetDirectory = Path.GetDirectoryName(fullTargetPath);
            if (!string.IsNullOrEmpty(targetDirectory) && !Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            // 移动文件或文件夹
            if (isDirectory)
            {
                Directory.Move(fullSourcePath, fullTargetPath);
            }
            else
            {
                File.Move(fullSourcePath, fullTargetPath, overwrite: true);
            }

            _logger.LogInformation("移动成功: {SessionId}/{Source} -> {Target}", sessionId, sourcePath, targetPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "移动失败: {SessionId}/{Source} -> {Target}", sessionId, sourcePath, targetPath);
            return false;
        }
    }

    /// <summary>
    /// 复制会话工作区中的文件或文件夹
    /// </summary>
    public async Task<bool> CopyFileInWorkspaceAsync(string sessionId, string sourcePath, string targetPath)
    {
        try
        {
            var workspacePath = GetSessionWorkspacePath(sessionId);

            if (!Directory.Exists(workspacePath))
            {
                _logger.LogWarning("工作区不存在: {SessionId}", sessionId);
                return false;
            }

            // 移除前导和尾随斜杠
            sourcePath = sourcePath.Trim('/', '\\');
            targetPath = targetPath.Trim('/', '\\');

            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetPath))
            {
                _logger.LogWarning("源路径或目标路径为空");
                return false;
            }

            // 构建完整路径
            var fullSourcePath = Path.Combine(workspacePath, sourcePath);
            var fullTargetPath = Path.Combine(workspacePath, targetPath);

            // 安全检查：确保路径在工作区内
            var normalizedWorkspace = Path.GetFullPath(workspacePath);
            var normalizedSource = Path.GetFullPath(fullSourcePath);
            var normalizedTarget = Path.GetFullPath(fullTargetPath);

            if (!IsPathWithinDirectory(normalizedWorkspace, normalizedSource) ||
                !IsPathWithinDirectory(normalizedWorkspace, normalizedTarget))
            {
                _logger.LogWarning("尝试复制工作区外的项");
                return false;
            }

            // 检查源是否存在
            bool isDirectory = Directory.Exists(fullSourcePath);
            bool isFile = File.Exists(fullSourcePath);

            if (!isDirectory && !isFile)
            {
                _logger.LogWarning("源不存在: {SessionId}/{Source}", sessionId, sourcePath);
                return false;
            }

            // 确保目标目录存在
            var targetDirectory = Path.GetDirectoryName(fullTargetPath);
            if (!string.IsNullOrEmpty(targetDirectory) && !Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            // 复制文件或文件夹
            if (isDirectory)
            {
                CopyDirectory(fullSourcePath, fullTargetPath);
            }
            else
            {
                File.Copy(fullSourcePath, fullTargetPath, overwrite: true);
            }

            _logger.LogInformation("复制成功: {SessionId}/{Source} -> {Target}", sessionId, sourcePath, targetPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "复制失败: {SessionId}/{Source} -> {Target}", sessionId, sourcePath, targetPath);
            return false;
        }
    }

    /// <summary>
    /// 重命名会话工作区中的文件或文件夹
    /// </summary>
    public async Task<bool> RenameFileInWorkspaceAsync(string sessionId, string oldPath, string newName)
    {
        try
        {
            var workspacePath = GetSessionWorkspacePath(sessionId);

            if (!Directory.Exists(workspacePath))
            {
                _logger.LogWarning("工作区不存在: {SessionId}", sessionId);
                return false;
            }

            // 移除前导和尾随斜杠
            oldPath = oldPath.Trim('/', '\\');
            newName = newName.Trim('/', '\\');

            if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newName))
            {
                _logger.LogWarning("旧路径或新名称为空");
                return false;
            }

            // 检查新名称是否包含路径分隔符（应该只是文件名）
            if (newName.Contains('/') || newName.Contains('\\'))
            {
                _logger.LogWarning("新名称不应包含路径分隔符: {NewName}", newName);
                return false;
            }

            // 构建完整路径
            var fullOldPath = Path.Combine(workspacePath, oldPath);
            var directory = Path.GetDirectoryName(fullOldPath);
            var fullNewPath = directory != null ? Path.Combine(directory, newName) : Path.Combine(workspacePath, newName);

            // 安全检查：确保路径在工作区内
            var normalizedWorkspace = Path.GetFullPath(workspacePath);
            var normalizedOld = Path.GetFullPath(fullOldPath);
            var normalizedNew = Path.GetFullPath(fullNewPath);

            if (!IsPathWithinDirectory(normalizedWorkspace, normalizedOld) ||
                !IsPathWithinDirectory(normalizedWorkspace, normalizedNew))
            {
                _logger.LogWarning("尝试重命名工作区外的项");
                return false;
            }

            // 检查源是否存在
            bool isDirectory = Directory.Exists(fullOldPath);
            bool isFile = File.Exists(fullOldPath);

            if (!isDirectory && !isFile)
            {
                _logger.LogWarning("源不存在: {SessionId}/{OldPath}", sessionId, oldPath);
                return false;
            }

            // 重命名文件或文件夹
            if (isDirectory)
            {
                Directory.Move(fullOldPath, fullNewPath);
            }
            else
            {
                File.Move(fullOldPath, fullNewPath, overwrite: true);
            }

            _logger.LogInformation("重命名成功: {SessionId}/{OldPath} -> {NewName}", sessionId, oldPath, newName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重命名失败: {SessionId}/{OldPath} -> {NewName}", sessionId, oldPath, newName);
            return false;
        }
    }

    /// <summary>
    /// 批量删除会话工作区中的文件
    /// </summary>
    public async Task<int> BatchDeleteFilesAsync(string sessionId, List<string> relativePaths)
    {
        int successCount = 0;

        try
        {
            var workspacePath = GetSessionWorkspacePath(sessionId);

            if (!Directory.Exists(workspacePath))
            {
                _logger.LogWarning("工作区不存在: {SessionId}", sessionId);
                return 0;
            }

            foreach (var relativePath in relativePaths)
            {
                try
                {
                    var cleanPath = relativePath.Trim('/', '\\');
                    if (string.IsNullOrWhiteSpace(cleanPath))
                    {
                        continue;
                    }

                    var fullPath = Path.Combine(workspacePath, cleanPath);

                    // 安全检查：确保路径在工作区内
                    var normalizedWorkspace = Path.GetFullPath(workspacePath);
                    var normalizedPath = Path.GetFullPath(fullPath);

                    if (!IsPathWithinDirectory(normalizedWorkspace, normalizedPath))
                    {
                        _logger.LogWarning("尝试删除工作区外的项: {Path}", fullPath);
                        continue;
                    }

                    // 判断是文件还是文件夹
                    bool isDirectory = Directory.Exists(fullPath);
                    bool isFile = File.Exists(fullPath);

                    if (isDirectory)
                    {
                        Directory.Delete(fullPath, recursive: true);
                        successCount++;
                    }
                    else if (isFile)
                    {
                        File.Delete(fullPath);
                        successCount++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "批量删除单个文件失败: {SessionId}/{Path}", sessionId, relativePath);
                }
            }

            _logger.LogInformation("批量删除完成: {SessionId}, 成功 {Count}/{Total}", sessionId, successCount, relativePaths.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "批量删除失败: {SessionId}", sessionId);
        }

        return successCount;
    }

    /// <summary>
    /// 递归复制目录
    /// </summary>
    private void CopyDirectory(string sourceDir, string targetDir)
    {
        // 创建目标目录
        Directory.CreateDirectory(targetDir);

        // 复制所有文件
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            var targetFile = Path.Combine(targetDir, fileName);
            File.Copy(file, targetFile, overwrite: true);
        }

        // 递归复制所有子目录
        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(subDir);
            var targetSubDir = Path.Combine(targetDir, dirName);
            CopyDirectory(subDir, targetSubDir);
        }
    }
}

