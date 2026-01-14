using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service.Adapters;
using WebCodeCli.Domain.Repositories.Base.SystemSettings;

namespace WebCodeCli.Domain.Domain.Service;

/// <summary>
/// CLI 执行服务实现
/// </summary>
[ServiceDescription(typeof(ICliExecutorService), ServiceLifetime.Singleton)]
public class CliExecutorService : ICliExecutorService
{
    private readonly ILogger<CliExecutorService> _logger;
    private readonly CliToolsOption _options;
    private readonly SemaphoreSlim _concurrencyLimiter;
    private readonly Dictionary<string, string> _sessionWorkspaces = new();
    private readonly object _workspaceLock = new();
    private readonly PersistentProcessManager _processManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly IChatSessionService _chatSessionService;
    private readonly ICliAdapterFactory _adapterFactory;
    
    // 缓存的有效工作区根目录
    private string? _effectiveWorkspaceRoot;
    private readonly object _workspaceRootLock = new();
    
    // 存储每个会话的CLI Thread ID（适用于所有CLI工具）
    private readonly Dictionary<string, string> _cliThreadIds = new();
    private readonly object _cliSessionLock = new();

    public CliExecutorService(
        ILogger<CliExecutorService> logger,
        IOptions<CliToolsOption> options,
        ILogger<PersistentProcessManager> processManagerLogger,
        IServiceProvider serviceProvider,
        IChatSessionService chatSessionService,
        ICliAdapterFactory adapterFactory)
    {
        _logger = logger;
        _options = options.Value;
        _concurrencyLimiter = new SemaphoreSlim(_options.MaxConcurrentExecutions);
        _processManager = new PersistentProcessManager(processManagerLogger);
        _serviceProvider = serviceProvider;
        _chatSessionService = chatSessionService;
        _adapterFactory = adapterFactory;
        
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
        lock (_cliSessionLock)
        {
            _cliThreadIds.TryGetValue(sessionId, out var threadId);
            return threadId;
        }
    }

    public void SetCliThreadId(string sessionId, string threadId)
    {
        if (string.IsNullOrEmpty(threadId)) return;
        
        lock (_cliSessionLock)
        {
            _cliThreadIds[sessionId] = threadId;
            _logger.LogInformation("设置会话 {SessionId} 的CLI线程ID: {ThreadId}", sessionId, threadId);
        }
    }

    #endregion

    public async IAsyncEnumerable<StreamOutputChunk> ExecuteStreamAsync(
        string sessionId,
        string toolId,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var tool = GetTool(toolId);
        if (tool == null)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = $"CLI 工具 '{toolId}' 不存在"
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
            await foreach (var chunk in ExecuteOneTimeProcessAsync(sessionId, tool, userPrompt, cancellationToken))
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
        var sessionWorkspace = GetOrCreateSessionWorkspace(sessionId);
        
        // 解析命令路径
        var resolvedCommand = ResolveCommandPath(tool.Command);
        
        // 获取环境变量(优先从数据库)
        var environmentVariables = await GetToolEnvironmentVariablesAsync(tool.Id);
        
        // 获取适配器
        var adapter = _adapterFactory.GetAdapter(tool);
        bool hasAdapter = adapter != null;
        
        // 获取CLI线程ID（用于会话恢复）
        string? cliThreadId = GetCliThreadId(sessionId);
        
        // 构建会话上下文
        var sessionContext = new CliSessionContext
        {
            SessionId = sessionId,
            CliThreadId = cliThreadId,
            WorkingDirectory = sessionWorkspace
        };
        
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
                
                yield return chunk;
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
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = "执行已取消或超时"
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
        var buffer = new char[4096];
        var outputBuilder = new StringBuilder();
        var lastOutputTime = DateTime.UtcNow;
        var noOutputTimeout = TimeSpan.FromSeconds(2); // 2秒无新输出则认为结束

        while (!cancellationToken.IsCancellationRequested)
        {
            bool hasNewOutput = false;
            
            // 尝试读取标准输出
            if (outputReader.Peek() >= 0)
            {
                int bytesRead = 0;
                try
                {
                    bytesRead = await outputReader.ReadAsync(buffer, 0, buffer.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "读取标准输出时发生错误");
                    break;
                }
                
                if (bytesRead > 0)
                {
                    var content = new string(buffer, 0, bytesRead);
                    outputBuilder.Append(content);
                    lastOutputTime = DateTime.UtcNow;
                    hasNewOutput = true;
                    
                    yield return new StreamOutputChunk
                    {
                        Content = content,
                        IsError = false,
                        IsCompleted = false
                    };
                }
            }

            // 尝试读取错误输出
            if (errorReader.Peek() >= 0)
            {
                int bytesRead = 0;
                try
                {
                    bytesRead = await errorReader.ReadAsync(buffer, 0, buffer.Length);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "读取错误输出时发生错误");
                    break;
                }
                
                if (bytesRead > 0)
                {
                    var content = new string(buffer, 0, bytesRead);
                    outputBuilder.Append(content);
                    lastOutputTime = DateTime.UtcNow;
                    hasNewOutput = true;
                    
                    yield return new StreamOutputChunk
                    {
                        Content = content,
                        IsError = false, // Codex 输出到 stderr 也是正常内容
                        IsCompleted = false
                    };
                }
            }

            // 检查是否超时无输出
            if (!hasNewOutput && (DateTime.UtcNow - lastOutputTime) > noOutputTimeout && outputBuilder.Length > 0)
            {
                _logger.LogInformation("检测到输出结束（无新输出超过{Timeout}秒）", noOutputTimeout.TotalSeconds);
                break;
            }

            // 短暂等待，避免CPU占用过高
            try
            {
                await Task.Delay(50, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // 取消时优雅退出枚举器，避免将异常抛到上层
                yield break;
            }
        }
    }

    /// <summary>
    /// 使用一次性进程执行（原有逻辑）
    /// </summary>
    private async IAsyncEnumerable<StreamOutputChunk> ExecuteOneTimeProcessAsync(
        string sessionId,
        CliToolConfig tool,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Process? process = null;
        
        // 获取适配器
        var adapter = _adapterFactory.GetAdapter(tool);
        bool hasAdapter = adapter != null;
        
        // 获取CLI线程ID（用于会话恢复）
        string? cliThreadId = GetCliThreadId(sessionId);
        
        // 获取或创建会话专属的工作目录
        var sessionWorkspace = GetOrCreateSessionWorkspace(sessionId);
        
        // 构建会话上下文
        var sessionContext = new CliSessionContext
        {
            SessionId = sessionId,
            CliThreadId = cliThreadId,
            WorkingDirectory = sessionWorkspace
        };

        // 构建参数，使用适配器（如果有）
        string arguments;
        if (hasAdapter)
        {
            arguments = adapter!.BuildArguments(tool, userPrompt, sessionContext);
            _logger.LogInformation("使用适配器 {Adapter} 构建命令, IsResume={IsResume}", adapter.GetType().Name, sessionContext.IsResume);
        }
        else
        {
            // 无适配器时，使用传统方式构建参数
            var escapedPrompt = EscapeArgument(userPrompt);
            arguments = tool.ArgumentTemplate.Replace("{prompt}", escapedPrompt);
        }
        
        // 解析命令路径(如果配置了npm目录且命令是相对路径)
        var commandPath = ResolveCommandPath(tool.Command);
        
        // 获取环境变量(优先从数据库)
        var environmentVariables = await GetToolEnvironmentVariablesAsync(tool.Id);
        
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

        // 设置环境变量 - 只有在有实际变量需要设置时才修改(避免覆盖默认继承)
        if (environmentVariables != null && environmentVariables.Count > 0)
        {
            foreach (var kvp in environmentVariables)
            {
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

        // 关闭标准输入
        process.StandardInput.Close();
        
        _logger.LogInformation("进程已启动，PID: {ProcessId}，开始读取输出流", process.Id);

        // 创建超时取消令牌
        using var timeoutCts = new CancellationTokenSource();
        if (tool.TimeoutSeconds > 0)
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(tool.TimeoutSeconds));
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);

        // 同时读取标准输出和错误输出
        // 注意：某些 CLI 工具（如 Codex）会将正常输出也输出到 stderr
        _logger.LogInformation("创建标准输出读取任务");
        var outputTask = ReadStreamAsync(process.StandardOutput, false, linkedCts.Token);
        _logger.LogInformation("创建错误输出读取任务");
        var errorTask = ReadStreamAsync(process.StandardError, false, linkedCts.Token); // 不标记为错误

        _logger.LogInformation("开始合并流输出");
        int chunkCount = 0;
    var fullOutput = new StringBuilder(); // 用于解析thread id
        
        // 合并两个流的输出
        await foreach (var chunk in MergeStreamsAsync(outputTask, errorTask, linkedCts.Token))
        {
            chunkCount++;
            
            // 收集输出用于后续解析session id
            if (!chunk.IsError && !string.IsNullOrEmpty(chunk.Content))
            {
                fullOutput.Append(chunk.Content);
            }
            
            yield return chunk;
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

    private async IAsyncEnumerable<(string content, bool isError)> ReadStreamAsync(
        StreamReader reader,
        bool isErrorStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("开始读取流，isErrorStream: {IsError}", isErrorStream);
        int lineCount = 0;
        
        while (true)
        {
            string? line;
            
            try
            {
                line = await reader.ReadLineAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("读取流被取消，isErrorStream: {IsError}, 已读取 {Count} 行", isErrorStream, lineCount);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取流时发生错误，isErrorStream: {IsError}", isErrorStream);
                break;
            }

            if (line == null)
            {
                _logger.LogInformation("流结束，isErrorStream: {IsError}, 共读取 {Count} 行", isErrorStream, lineCount);
                break;
            }
            
            lineCount++;
            // 添加换行符，保持原始格式
            var content = line + Environment.NewLine;
            _logger.LogInformation(content);
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

    public List<CliToolConfig> GetAvailableTools()
    {
        return _options.Tools.Where(t => t.Enabled).ToList();
    }

    public CliToolConfig? GetTool(string toolId)
    {
        return _options.Tools.FirstOrDefault(t => t.Id == toolId);
    }

    public bool ValidateTool(string toolId)
    {
        var tool = GetTool(toolId);
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
                return existingWorkspace;
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
                
                return workspacePath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "创建会话工作目录失败: {SessionId}", sessionId);
                // 如果创建失败,返回临时目录根路径
                return workspaceRoot;
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
        
        string? workspacePathFromCache = null;

        lock (_workspaceLock)
        {
            if (_sessionWorkspaces.TryGetValue(sessionId, out var workspacePath))
            {
                workspacePathFromCache = workspacePath;
                _sessionWorkspaces.Remove(sessionId);
            }
        }

        // 注意：即使内存缓存中不存在该会话，也应该尝试清理默认路径下的目录。
        // 典型场景：服务重启后 _sessionWorkspaces 被清空，但磁盘目录仍然存在。
        var workspacePathToDelete = workspacePathFromCache;
        var workspaceRoot = GetEffectiveWorkspaceRoot();
        if (string.IsNullOrWhiteSpace(workspacePathToDelete))
        {
            workspacePathToDelete = Path.Combine(workspaceRoot, sessionId);
        }

        try
        {
            var rootFullPath = Path.GetFullPath(workspaceRoot);
            var workspaceFullPath = Path.GetFullPath(workspacePathToDelete);

            // 防御：只允许删除 TempWorkspaceRoot 下的子目录，避免误删。
            if (!workspaceFullPath.StartsWith(rootFullPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(workspaceFullPath, rootFullPath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("跳过清理会话工作目录（路径异常）: {SessionId}, {Path}", sessionId, workspaceFullPath);
                return;
            }

            if (Directory.Exists(workspaceFullPath))
            {
                try
                {
                    Directory.Delete(workspaceFullPath, recursive: true);
                    _logger.LogInformation("已清理会话 {SessionId} 的工作目录: {Path}", sessionId, workspaceFullPath);
                }
                catch (Exception ex)
                {
                    // Windows 上常见原因：只读属性、被占用。
                    try
                    {
                        NormalizeDirectoryAttributes(workspaceFullPath);
                        Directory.Delete(workspaceFullPath, recursive: true);
                        _logger.LogInformation("已清理会话 {SessionId} 的工作目录(重试成功): {Path}", sessionId, workspaceFullPath);
                    }
                    catch
                    {
                        _logger.LogWarning(ex, "清理会话工作目录失败: {SessionId}, {Path}", sessionId, workspaceFullPath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理会话工作目录失败(路径解析异常): {SessionId}", sessionId);
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

            // 如果不存在,创建一个
            return GetOrCreateSessionWorkspace(sessionId);
        }
    }

    /// <summary>
    /// 解析命令路径,如果配置了npm目录且命令是相对路径,则拼接完整路径
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

        // 否则返回原始命令(假设是系统PATH中的命令)
        return command;
    }

    /// <summary>
    /// 获取NPM全局安装路径（优先使用配置的路径，如果未配置则自动检测）
    /// </summary>
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

    /// <summary>
    /// 获取指定工具的环境变量配置（优先从数据库读取）
    /// </summary>
    public async Task<Dictionary<string, string>> GetToolEnvironmentVariablesAsync(string toolId)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var envService = scope.ServiceProvider.GetRequiredService<ICliToolEnvironmentService>();
            return await envService.GetEnvironmentVariablesAsync(toolId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取工具 {ToolId} 的环境变量失败", toolId);
            
            // 降级到appsettings配置
            var tool = GetTool(toolId);
            return tool?.EnvironmentVariables ?? new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// 保存指定工具的环境变量配置到数据库
    /// </summary>
    public async Task<bool> SaveToolEnvironmentVariablesAsync(string toolId, Dictionary<string, string> envVars)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var envService = scope.ServiceProvider.GetRequiredService<ICliToolEnvironmentService>();
            return await envService.SaveEnvironmentVariablesAsync(toolId, envVars);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存工具 {ToolId} 的环境变量失败", toolId);
            return false;
        }
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

            if (!normalizedFile.StartsWith(normalizedWorkspace))
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

            if (!normalizedTarget.StartsWith(normalizedWorkspace))
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

            if (!normalizedTarget.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
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

            if (!normalizedTarget.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
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

            if (!normalizedSource.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase) ||
                !normalizedTarget.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
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

            if (!normalizedSource.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase) ||
                !normalizedTarget.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
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

            if (!normalizedOld.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase) ||
                !normalizedNew.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
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

                    if (!normalizedPath.StartsWith(normalizedWorkspace, StringComparison.OrdinalIgnoreCase))
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

