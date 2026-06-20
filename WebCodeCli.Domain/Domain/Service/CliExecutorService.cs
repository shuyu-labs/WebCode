using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
/// CLI 鎵ц鏈嶅姟瀹炵幇
/// </summary>
[ServiceDescription(typeof(ICliExecutorService), ServiceLifetime.Singleton)]
public class CliExecutorService : ICliExecutorService
{
    private const string CodexLaunchBaseConfigFileName = "config.webcode.base.toml";
    private const string GoalRuntimeContinuationPromptPrefix = "Continue working toward the current active goal";
    private static readonly Encoding CodexTransportEncoding = new UTF8Encoding(false);

    private readonly ILogger<CliExecutorService> _logger;
    private readonly CliToolsOption _options;
    private readonly SemaphoreSlim _concurrencyLimiter;
    private readonly Dictionary<string, string> _sessionWorkspaces = new();
    private readonly object _workspaceLock = new();
    private readonly PersistentProcessManager _processManager;
    private readonly ICodexAppServerSessionManager _codexAppServerSessionManager;
    private readonly IServiceProvider _serviceProvider;
    private readonly IChatSessionService _chatSessionService;
    private readonly ICliAdapterFactory _adapterFactory;
    private readonly ICcSwitchService _ccSwitchService;
    private readonly IGoalCapabilityService _goalCapabilityService;
    private readonly Func<string> _userProfileResolver;
    
    // 缂撳瓨鐨勬湁鏁堝伐浣滃尯鏍圭洰褰?
    private string? _effectiveWorkspaceRoot;
    private readonly object _workspaceRootLock = new();

    private readonly Dictionary<string, string> _cliThreadIds = new();
    private readonly object _cliSessionLock = new();

    // 璁板綍褰撳墠浼氳瘽鐨勬椿璺冭繘绋嬶紝渚夸簬鏄惧紡鍋滄褰撳墠鎵ц
    private readonly Dictionary<string, Process> _activeSessionProcesses = new();
    private readonly object _activeProcessLock = new();

    // Windows 涓嬩负 Claude Code 閫夋嫨鍙敤鐨勮緝楂樼増鏈?Node.js
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
        IGoalCapabilityService? goalCapabilityService = null,
        ICodexAppServerSessionManager? codexAppServerSessionManager = null,
        Func<string>? userProfileResolver = null)
    {
        _logger = logger;
        _options = options.Value;
        _concurrencyLimiter = new SemaphoreSlim(_options.MaxConcurrentExecutions);
        _processManager = new PersistentProcessManager(processManagerLogger);
        _codexAppServerSessionManager = codexAppServerSessionManager
            ?? new CodexAppServerSessionManager(
                serviceProvider.GetService<ILogger<CodexAppServerSessionManager>>()
                ?? NullLogger<CodexAppServerSessionManager>.Instance);
        _serviceProvider = serviceProvider;
        _chatSessionService = chatSessionService;
        _adapterFactory = adapterFactory;
        _ccSwitchService = ccSwitchService;
        _goalCapabilityService = goalCapabilityService ?? NullGoalCapabilityService.Instance;
        _userProfileResolver = userProfileResolver ?? ResolveUserProfilePath;
        
        // 鍒濆鍖栧伐浣滃尯鏍圭洰褰曪紙寤惰繜鍔犺浇锛岄娆′娇鐢ㄦ椂浠庢暟鎹簱鑾峰彇锛?
        InitializeWorkspaceRoot();
    }
    
    /// <summary>
    /// 鍒濆鍖栧伐浣滃尯鏍圭洰褰?
    /// </summary>
    private void InitializeWorkspaceRoot()
    {
        var workspaceRoot = GetEffectiveWorkspaceRoot();
        
        // 纭繚涓存椂宸ヤ綔鍖烘牴鐩綍瀛樺湪
        if (!Directory.Exists(workspaceRoot))
        {
            Directory.CreateDirectory(workspaceRoot);
            _logger.LogInformation("鍒涘缓涓存椂宸ヤ綔鍖烘牴鐩綍: {Root}", workspaceRoot);
        }
    }
    
    /// <summary>
    /// 鑾峰彇鏈夋晥鐨勫伐浣滃尯鏍圭洰褰曪紙浼樺厛鏁版嵁搴撻厤缃紝鍚﹀垯浣跨敤閰嶇疆鏂囦欢锛屾渶鍚庝娇鐢ㄩ粯璁ゅ€硷級
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
                // 灏濊瘯浠庢暟鎹簱鑾峰彇
                using var scope = _serviceProvider.CreateScope();
                var repository = scope.ServiceProvider.GetService<ISystemSettingsRepository>();
                if (repository != null)
                {
                    var dbValue = repository.GetAsync(SystemSettingsKeys.WorkspaceRoot).GetAwaiter().GetResult();
                    if (!string.IsNullOrWhiteSpace(dbValue))
                    {
                        _effectiveWorkspaceRoot = dbValue;
                        _logger.LogInformation("浠庢暟鎹簱鍔犺浇宸ヤ綔鍖烘牴鐩綍: {Root}", dbValue);
                        return _effectiveWorkspaceRoot;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load workspace root from database; using configured fallback.");
            }
            
            // 浣跨敤閰嶇疆鏂囦欢涓殑鍊?
            if (!string.IsNullOrWhiteSpace(_options.TempWorkspaceRoot))
            {
                _effectiveWorkspaceRoot = _options.TempWorkspaceRoot;
                return _effectiveWorkspaceRoot;
            }
            
            // 浣跨敤榛樿鍊?
            _effectiveWorkspaceRoot = GetDefaultWorkspaceRoot();
            _logger.LogWarning("TempWorkspaceRoot 閰嶇疆涓虹┖锛屼娇鐢ㄩ粯璁よ矾寰? {Root}", _effectiveWorkspaceRoot);
            return _effectiveWorkspaceRoot;
        }
    }
    
    /// <summary>
    /// 鑾峰彇榛樿宸ヤ綔鍖烘牴鐩綍
    /// </summary>
    private static string GetDefaultWorkspaceRoot()
    {
        // Docker 鐜浣跨敤鍥哄畾璺緞
        if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
        {
            return "/app/workspaces";
        }
        
        // 闈?Docker 鐜浣跨敤搴旂敤鏍圭洰褰曚笅鐨?workspaces 鏂囦欢澶?
        var appRoot = AppContext.BaseDirectory;
        return Path.Combine(appRoot, "workspaces");
    }
    
    /// <summary>
    /// 鍒锋柊宸ヤ綔鍖烘牴鐩綍缂撳瓨锛堝綋鏁版嵁搴撻厤缃洿鏂版椂璋冪敤锛?
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

        var liveThreadId = _codexAppServerSessionManager.GetRunningThreadId(sessionId);
        if (!string.IsNullOrWhiteSpace(liveThreadId))
        {
            RepairCliThreadIdCache(sessionId, liveThreadId, logRepair: true);
            return liveThreadId;
        }

        lock (_cliSessionLock)
        {
            if (_cliThreadIds.TryGetValue(sessionId, out var cached) && !string.IsNullOrWhiteSpace(cached))
            {
                return cached;
            }
        }

        // 缂撳瓨鏈懡涓椂锛屼粠鏁版嵁搴撳洖閫€锛圕hatSession.CliThreadId / SessionOutput.ActiveThreadId锛?
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
                    _logger.LogInformation("浠庡鍏ユ爣棰樻仮澶嶄細璇?{SessionId} 鐨?CLI ThreadId: {ThreadId}", sessionId, threadId);

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
            _logger.LogDebug(ex, "浠庢暟鎹簱鍥為€€ CLI ThreadId 澶辫触: {SessionId}", sessionId);
        }

        return null;
    }

    public void SetCliThreadId(string sessionId, string threadId)
    {
        if (string.IsNullOrEmpty(threadId)) return;

        var liveThreadId = _codexAppServerSessionManager.GetRunningThreadId(sessionId);
        if (!string.IsNullOrWhiteSpace(liveThreadId)
            && !string.Equals(liveThreadId, threadId, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "忽略与活动 Codex goal runtime 主线程不一致的 CLI ThreadId: Session={SessionId}, ObservedThread={ObservedThread}, LiveThread={LiveThread}",
                sessionId,
                threadId,
                liveThreadId);
            RepairCliThreadIdCache(sessionId, liveThreadId, logRepair: false);
            return;
        }

        lock (_cliSessionLock)
        {
            _cliThreadIds[sessionId] = threadId;
            _logger.LogInformation("璁剧疆浼氳瘽 {SessionId} 鐨凜LI绾跨▼ID: {ThreadId}", sessionId, threadId);
        }

        // 鏈€浣冲姫鍔涳細鎸佷箙鍖栧埌鏁版嵁搴擄紝淇濊瘉鏈嶅姟閲嶅惎/椤甸潰鍒锋柊鍚庝粛鍙仮澶嶄細璇?
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
            _ = repo.UpdateCliThreadIdAsync(sessionId, threadId).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "鎸佷箙鍖?CLI ThreadId 澶辫触: {SessionId}", sessionId);
        }
    }

    private void RepairCliThreadIdCache(string sessionId, string authoritativeThreadId, bool logRepair)
    {
        string? cachedThreadId;
        lock (_cliSessionLock)
        {
            _cliThreadIds.TryGetValue(sessionId, out cachedThreadId);
            _cliThreadIds[sessionId] = authoritativeThreadId;
        }

        if (string.Equals(cachedThreadId, authoritativeThreadId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (logRepair)
        {
            _logger.LogDebug(
                "使用活动 Codex goal runtime 主线程修正会话线程绑定: Session={SessionId}, PreviousThread={PreviousThread}, LiveThread={LiveThread}",
                sessionId,
                cachedThreadId,
                authoritativeThreadId);
        }

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var repo = scope.ServiceProvider.GetService<IChatSessionRepository>();
            if (repo != null)
            {
                _ = repo.UpdateCliThreadIdAsync(sessionId, authoritativeThreadId).GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "修正 CLI ThreadId 持久化失败: {SessionId}", sessionId);
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
        _codexAppServerSessionManager.CleanupSession(sessionId);

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

            _logger.LogInformation("宸查噸缃細璇濊繍琛屾€? {SessionId}", sessionId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "閲嶇疆浼氳瘽杩愯鎬佸け璐?宸插畬鎴愬唴瀛樻竻鐞?: {SessionId}", sessionId);
        }
    }

    public async Task StopSessionExecutionAsync(
        string sessionId,
        string? toolId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var currentSession = await TryGetChatSessionAsync(sessionId);
        var resolvedToolId = toolId ?? currentSession?.ToolId ?? string.Empty;
        var sessionLaunchOverride = await GetEffectiveSessionLaunchOverrideAsync(sessionId, resolvedToolId);
        var isGoalRuntimeSession = sessionLaunchOverride?.UseGoalRuntime == true;

        if (isGoalRuntimeSession)
        {
            try
            {
                if (await _codexAppServerSessionManager.InterruptActiveTurnAsync(sessionId, cancellationToken))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "中断 Codex app-server 当前 turn 失败: Session={SessionId}", sessionId);
            }
        }

        await TerminateActiveSessionProcessAsync(sessionId);

        if (!string.IsNullOrWhiteSpace(toolId))
        {
            _processManager.CleanupProcess(sessionId, toolId);
        }
        else
        {
            _processManager.CleanupSessionProcesses(sessionId);
        }

        _codexAppServerSessionManager.CleanupSession(sessionId);

    }

    #endregion

    public async IAsyncEnumerable<StreamOutputChunk> ExecuteStreamAsync(
        string sessionId,
        string toolId,
        string userPrompt,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var chunk in ExecuteStreamAsync(
            new CliExecutionRequest
            {
                SessionId = sessionId,
                ToolId = toolId,
                PromptText = userPrompt
            },
            cancellationToken))
        {
            yield return chunk;
        }
    }

    public async IAsyncEnumerable<StreamOutputChunk> ExecuteStreamAsync(
        CliExecutionRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var sessionId = request.SessionId;
        var toolId = request.ToolId;
        var userPrompt = request.PromptText;
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
                ErrorMessage = $"CLI tool {tool.Name} is disabled."
            };
            yield break;
        }

        var sessionLaunchOverride = await GetEffectiveSessionLaunchOverrideAsync(sessionId, toolId);
        tool = ApplySessionLaunchOverride(tool, sessionLaunchOverride);
        var goalRuntimeEnabled = sessionLaunchOverride?.UseGoalRuntime == true;

        if (IsGoalCommand(userPrompt))
        {
            await EnsureGoalExecutionRuntimeAsync(
                sessionId,
                toolId,
                cancellationToken);
            goalRuntimeEnabled = true;
        }

        // 闄愬埗骞跺彂鎵ц鏁伴噺
        await _concurrencyLimiter.WaitAsync(cancellationToken);

        try
        {
            await foreach (var chunk in ExecuteProcessStreamAsync(
                request,
                tool,
                goalRuntimeEnabled,
                cancellationToken))
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
                ErrorMessage = launchBlockingMessage ?? $"CLI 瀹搞儱鍙?'{toolId}' 娑撳秴鐡ㄩ崷銊﹀灗瑜版挸澧犻悽銊﹀煕閺冪姵娼堟担璺ㄦ暏"
            };
            yield break;
        }

        if (!tool.Enabled)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = $"CLI 瀹搞儱鍙?'{tool.Name}' 瀹歌尙顩﹂悽?"
            };
            yield break;
        }

        if (!SupportsLowInterruptionContinue(tool))
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = $"CLI tool {tool.Name} does not support low-interruption continue."
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
            await foreach (var chunk in ExecuteOneTimeProcessAsync(
                new CliExecutionRequest
                {
                    SessionId = sessionId,
                    ToolId = toolId,
                    PromptText = string.Empty
                },
                tool,
                true,
                prompt,
                cancellationToken))
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
        CliExecutionRequest request,
        CliToolConfig tool,
        bool goalRuntimeEnabled,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sessionId = request.SessionId;
        var userPrompt = request.PromptText;
        var adapter = _adapterFactory.GetAdapter(tool);
        if (goalRuntimeEnabled && IsNativeCodexGoalRuntimeTool(tool))
        {
            await foreach (var chunk in ExecuteCodexGoalRuntimeStreamAsync(
                sessionId,
                tool,
                userPrompt,
                cancellationToken))
            {
                yield return chunk;
            }

            yield break;
        }

        var usePersistentProcess = tool.UsePersistentProcess && !goalRuntimeEnabled;
        if (goalRuntimeEnabled && IsNativeCodexGoalRuntimeTool(tool))
        {
            _logger.LogInformation(
                "【Goal 会话运行态】工具: {Tool}, Session={Session}, UsePersistentProcess={Flag}",
                tool.Name,
                sessionId,
                tool.UsePersistentProcess);
        }

        // 鏍规嵁宸ュ叿閰嶇疆閫夋嫨鎵ц妯″紡
        if (usePersistentProcess && !ShouldUseOneTimeExecutionDespitePersistentMode(tool, adapter))
        {
            _logger.LogInformation("【持久化进程模式】工具: {Tool}, UsePersistentProcess={Flag}", tool.Name, tool.UsePersistentProcess);
            await foreach (var chunk in ExecutePersistentProcessAsync(request, tool, cancellationToken))
            {
                yield return chunk;
            }
        }
        else
        {
            if (tool.UsePersistentProcess)
            {
                _logger.LogInformation(
                    goalRuntimeEnabled
                        ? "【Goal 会话运行态】工具: {Tool}, UsePersistentProcess={Flag}。为避免不支持的 stdin 复用，切换到一次性 exec/resume。"
                        : "【兼容一次性进程模式】工具: {Tool}, UsePersistentProcess={Flag}。由于不支持基于 stdin 的持久复用，回退到一次性 exec/resume。",
                    tool.Name,
                    tool.UsePersistentProcess);
            }
            else
            {
                _logger.LogInformation(
                    goalRuntimeEnabled
                        ? "【Goal 会话运行态】工具: {Tool}, UsePersistentProcess={Flag}。使用一次性 exec/resume。"
                        : "【一次性进程模式】工具: {Tool}, UsePersistentProcess={Flag}",
                    tool.Name,
                    tool.UsePersistentProcess);
            }

            await foreach (var chunk in ExecuteOneTimeProcessAsync(request, tool, false, null, cancellationToken))
            {
                yield return chunk;
            }
        }
    }

    /// <summary>
    /// 浣跨敤鎸佷箙鍖栬繘绋嬫墽琛?
    /// </summary>
    private async IAsyncEnumerable<StreamOutputChunk> ExecutePersistentProcessAsync(
        CliExecutionRequest request,
        CliToolConfig tool,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sessionId = request.SessionId;
        var userPrompt = request.PromptText;
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
        
        // 瑙ｆ瀽鍛戒护璺緞
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
        
        // 鑾峰彇鐜鍙橀噺(浼樺厛浠庢暟鎹簱)
        var environmentVariables = await GetToolEnvironmentVariablesAsync(tool.Id);

        // 鑾峰彇閫傞厤鍣?
        var adapter = _adapterFactory.GetAdapter(tool);
        bool hasAdapter = adapter != null;
        
        // 鑾峰彇CLI绾跨▼ID锛堢敤浜庝細璇濇仮澶嶏級
        string? cliThreadId = GetCliThreadId(sessionId);
        
        // 鏋勫缓浼氳瘽涓婁笅鏂?
        var sessionContext = await BuildCliSessionContextAsync(
            sessionId,
            tool.Id,
            sessionWorkspace,
            cliThreadId,
            environmentVariables,
            cancellationToken);
        var requestWithContext = request.WithSessionContext(sessionContext);
        
        _logger.LogInformation("浣跨敤鎸佷箙鍖栬繘绋嬫ā寮忔墽琛?CLI 宸ュ叿: {Tool}, 浼氳瘽: {Session}, 宸ヤ綔鐩綍: {Workspace}, 鍛戒护: {Command}, CLI Thread: {CliThread}, 閫傞厤鍣? {Adapter}", 
            tool.Name, sessionId, sessionWorkspace, resolvedCommand, cliThreadId ?? "New Session", adapter?.GetType().Name ?? "None");

        PersistentProcessInfo? processInfo = null;
        bool hasError = false;
        string? errorMessage = null;
        
        // 鍒涘缓甯︽湁瑙ｆ瀽鍚庡懡浠よ矾寰勫拰鐜鍙橀噺鐨則ool鍓湰
        var toolWithResolvedCommand = new CliToolConfig
        {
            Id = tool.Id,
            Name = tool.Name,
            Description = tool.Description,
            Command = resolvedCommand, // 浣跨敤瑙ｆ瀽鍚庣殑鍛戒护璺緞
            ArgumentTemplate = tool.ArgumentTemplate,
            LowInterruptionArgumentTemplate = tool.LowInterruptionArgumentTemplate,
            WorkingDirectory = tool.WorkingDirectory,
            Enabled = tool.Enabled,
            TimeoutSeconds = tool.TimeoutSeconds,
            EnvironmentVariables = environmentVariables, // 浣跨敤浠庢暟鎹簱鎴栭厤缃枃浠惰幏鍙栫殑鐜鍙橀噺
            UsePersistentProcess = tool.UsePersistentProcess,
            PersistentModeArguments = tool.PersistentModeArguments
        };
        
        // 鑾峰彇鎴栧垱寤烘寔涔呭寲杩涚▼
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
            _logger.LogError(ex, "创建持久化进程失败。");
            hasError = true;
            errorMessage = $"鍒涘缓杩涚▼澶辫触: {ex.Message}";
        }

        if (hasError || processInfo == null)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = errorMessage ?? "鍒涘缓杩涚▼澶辫触"
            };
            yield break;
        }

        RegisterActiveSessionProcess(sessionId, processInfo.Process);

        if (!processInfo.IsRunning)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = "进程未运行。"
            };
            yield break;
        }

        // 鍚戣繘绋嬪彂閫佺敤鎴疯緭鍏?
        // 浣跨敤閫傞厤鍣ㄦ瀯寤哄懡浠わ紙濡傛灉鏈夐€傞厤鍣級
        string actualInput;
        if (hasAdapter)
        {
            actualInput = adapter!.BuildArguments(tool, requestWithContext);
            _logger.LogInformation("浣跨敤閫傞厤鍣?{Adapter} 鏋勫缓鍛戒护: PID={ProcessId}, IsResume={IsResume}, Prompt闀垮害={Length}", 
                adapter.GetType().Name, processInfo.Process.Id, sessionContext.IsResume, requestWithContext.PromptText.Length);
        }
        else
        {
            // 鏃犻€傞厤鍣ㄦ椂锛岀洿鎺ュ彂閫佺敤鎴疯緭鍏?
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
            _logger.LogError(ex, "发送输入到进程失败。");
            sendError = true;
            sendErrorMessage = $"发送输入失败: {ex.Message}";
        }
        
        if (sendError)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = sendErrorMessage ?? "发送输入失败。"
            };
            yield break;
        }

        // 璇诲彇杈撳嚭
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
        var fullOutput = new StringBuilder(); // 鐢ㄤ簬瑙ｆ瀽thread id

        var noOutputTimeout = GetPersistentProcessNoOutputTimeout(tool, userPrompt, adapter);

        await using (var enumerator = ReadPersistentProcessOutputAsync(processInfo, noOutputTimeout, linkedCts.Token)
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
                
                // 鏀堕泦杈撳嚭鍐呭鐢ㄤ簬瑙ｆ瀽session id
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
                        "妫€娴嬪埌閫傞厤鍣ㄧ粓姝簨浠讹紝鎻愬墠缁撴潫褰撳墠杞緭鍑鸿鍙? Tool={ToolId}, Session={SessionId}, IsError={IsError}",
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
        
        // 濡傛灉鏈夐€傞厤鍣ㄤ笖杩樻病鏈塁LI绾跨▼ID锛屽皾璇曚粠杈撳嚭涓В鏋?
        if (hasAdapter && string.IsNullOrEmpty(cliThreadId))
        {
            var output = fullOutput.ToString();
            var parsedThreadId = ParseCliThreadId(output, adapter!);
            if (!string.IsNullOrEmpty(parsedThreadId))
            {
                SetCliThreadId(sessionId, parsedThreadId);
                _logger.LogInformation("瑙ｆ瀽鍒癈LI Thread ID: {CliThread} for 浼氳瘽: {Session}", parsedThreadId, sessionId);
            }
        }

        if (cancelled)
        {
            _processManager.CleanupSessionProcesses(sessionId);

            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = "鎵ц宸插彇娑堟垨瓒呮椂"
            };
        }
        else if (terminalEventDetected && terminalEventIsError)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = terminalEventErrorMessage ?? "鎵ц澶辫触"
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

        UnregisterActiveSessionProcess(sessionId, processInfo.Process);
    }

    /// <summary>
    /// 璇诲彇鎸佷箙鍖栬繘绋嬬殑杈撳嚭
    /// </summary>
    private async IAsyncEnumerable<StreamOutputChunk> ReadPersistentProcessOutputAsync(
        PersistentProcessInfo processInfo,
        TimeSpan? noOutputTimeout,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var outputReader = processInfo.Process.StandardOutput;
        var errorReader = processInfo.Process.StandardError;
        var outputBuffer = new char[4096];
        var errorBuffer = new char[4096];
        var lastOutputTime = DateTime.UtcNow;
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

                    if (noOutputTimeout is { } idleTimeout
                        && hasObservedOutput
                        && (DateTime.UtcNow - lastOutputTime) > idleTimeout)
                    {
                                                _logger.LogInformation("检测到输出结束（无新输出超时 {Timeout} 秒）", idleTimeout.TotalSeconds);
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
                        _logger.LogWarning(ex, "Failed while reading standard output.");
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
                        _logger.LogWarning(ex, "Failed while reading standard error.");
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
                            IsError = false, // Codex 杈撳嚭鍒?stderr 涔熸槸姝ｅ父鍐呭
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

            await ObservePendingPersistentReadTaskAsync(outputReadTask, "鏍囧噯杈撳嚭");
            await ObservePendingPersistentReadTaskAsync(errorReadTask, "閿欒杈撳嚭");
        }
    }

    private void RegisterActiveSessionProcess(string sessionId, Process process)
    {
        lock (_activeProcessLock)
        {
            _activeSessionProcesses[sessionId] = process;
        }
    }

    private void UnregisterActiveSessionProcess(string sessionId, Process? process)
    {
        lock (_activeProcessLock)
        {
            if (!_activeSessionProcesses.TryGetValue(sessionId, out var current))
            {
                return;
            }

            if (process == null || ReferenceEquals(current, process))
            {
                _activeSessionProcesses.Remove(sessionId);
            }
        }
    }

    private async Task TerminateActiveSessionProcessAsync(string sessionId)
    {
        Process? process = null;

        lock (_activeProcessLock)
        {
            if (_activeSessionProcesses.TryGetValue(sessionId, out var current))
            {
                process = current;
                _activeSessionProcesses.Remove(sessionId);
            }
        }

        if (process == null)
        {
            return;
        }

        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (await TryRequestGracefulStopAsync(process, sessionId))
            {
                return;
            }

            if (!process.HasExited)
            {
                process.Kill(true);
                await WaitForProcessExitWithinAsync(
                    process,
                    TimeSpan.FromSeconds(5),
                    "stop",
                    sessionId,
                    "等待强制结束会话进程时发生异常");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "鍋滄浼氳瘽鎵ц鏃剁粓姝㈡椿璺冭繘绋嬪け璐? Session={SessionId}, PID={ProcessId}", sessionId, process.Id);
        }
    }

    private async Task<bool> TryRequestGracefulStopAsync(Process process, string sessionId)
    {
        if (process.HasExited)
        {
            return true;
        }

        if (TryCloseStandardInput(process, sessionId)
            && await WaitForProcessExitWithinAsync(
                process,
                TimeSpan.FromSeconds(3),
                "stop",
                sessionId,
                "绛夊緟鍏抽棴鏍囧噯杈撳叆鍚庣殑杩涚▼閫€鍑烘椂鍙戠敓寮傚父"))
        {
            return true;
        }

        if (await TryRequestGracefulProcessExitAsync(process, "stop", sessionId))
        {
            return true;
        }

        return false;
    }

    private bool TryCloseStandardInput(Process process, string sessionId)
    {
        try
        {
            if (process.HasExited)
            {
                return false;
            }

            process.StandardInput.Close();
            _logger.LogInformation("鍋滄浼氳瘽鎵ц鏃跺凡鍏抽棴鏍囧噯杈撳叆: Session={SessionId}, PID={ProcessId}", sessionId, process.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "鍋滄浼氳瘽鎵ц鏃跺叧闂爣鍑嗚緭鍏ュけ璐? Session={SessionId}, PID={ProcessId}", sessionId, process.Id);
            return false;
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
            _logger.LogDebug("{StreamName} read task was canceled.", streamName);
        }
        catch (ObjectDisposedException)
        {
            _logger.LogDebug("{StreamName} stream was already disposed.", streamName);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "{StreamName} read task failed during shutdown.", streamName);
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
                _logger.LogDebug(ex, "瑙ｆ瀽閫傞厤鍣ㄨ緭鍑鸿澶辫触: {Line}", trimmedLine.Length > 120 ? trimmedLine[..120] : trimmedLine);
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
                    ?? "Current interaction failed.");
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
    /// 浣跨敤涓€娆℃€ц繘绋嬫墽琛岋紙鍘熸湁閫昏緫锛?
    /// </summary>
    private async IAsyncEnumerable<StreamOutputChunk> ExecuteOneTimeProcessAsync(
        CliExecutionRequest request,
        CliToolConfig tool,
        bool useLowInterruption,
        string? lowInterruptionPrompt = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var sessionId = request.SessionId;
        var userPrompt = request.PromptText;
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
        
        // 鑾峰彇閫傞厤鍣?
        var adapter = _adapterFactory.GetAdapter(tool);
        bool hasAdapter = adapter != null;
        
        // 鑾峰彇CLI绾跨▼ID锛堢敤浜庝細璇濇仮澶嶏級
        string? cliThreadId = GetCliThreadId(sessionId);
        
        // 鑾峰彇鎴栧垱寤轰細璇濅笓灞炵殑宸ヤ綔鐩綍
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

        // 鏋勫缓浼氳瘽涓婁笅鏂?
        var sessionContext = await BuildCliSessionContextAsync(
            sessionId,
            tool.Id,
            sessionWorkspace,
            cliThreadId,
            environmentVariables,
            cancellationToken);
        var requestWithContext = request.WithSessionContext(sessionContext);

        // 鏋勫缓鍙傛暟锛屼娇鐢ㄩ€傞厤鍣紙濡傛灉鏈夛級
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
            arguments = adapter!.BuildArguments(tool, requestWithContext);
            _logger.LogInformation("使用适配器 {Adapter} 构建命令, IsResume={IsResume}", adapter.GetType().Name, sessionContext.IsResume);
        }
        else
        {
            var escapedPrompt = EscapeArgument(userPrompt);
            arguments = tool.ArgumentTemplate.Replace("{prompt}", escapedPrompt);
        }
        
        // 瑙ｆ瀽鍛戒护璺緞(濡傛灉閰嶇疆浜唍pm鐩綍涓斿懡浠ゆ槸鐩稿璺緞)
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
            StandardInputEncoding = IsCodexExecution(tool, adapter) ? GetCodexTransportEncoding() : Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        // 璁剧疆宸ヤ綔鐩綍
        if (!string.IsNullOrWhiteSpace(tool.WorkingDirectory))
        {
            startInfo.WorkingDirectory = tool.WorkingDirectory;
        }
        else
        {
            // 浣跨敤浼氳瘽涓撳睘鐨勫伐浣滅洰褰?
            startInfo.WorkingDirectory = sessionWorkspace;
        }

        var workingDirectoryError = GetWorkingDirectoryValidationError(startInfo.WorkingDirectory, sessionId);
        if (workingDirectoryError != null)
        {
            _logger.LogWarning("鍚姩 CLI 杩涚▼鍓嶅彂鐜版棤鏁堝伐浣滅洰褰? {WorkingDirectory}", startInfo.WorkingDirectory);
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = workingDirectoryError
            };
            yield break;
        }

        // 璁剧疆鐜鍙橀噺
        if (environmentVariables != null && environmentVariables.Count > 0)
        {
            foreach (var kvp in environmentVariables)
            {
                // 绌哄瓧绗︿覆琛ㄧず鏄惧紡绉婚櫎鐜鍙橀噺锛堢敤浜庡彇娑堢户鎵跨埗杩涚▼鐨勭幆澧冨彉閲忥級
                // 杩欏浜庡儚 CLAUDECODE 杩欐牱鐨勫彉閲忓緢閲嶈锛岄渶瑕佸畬鍏ㄦ湭璁剧疆鑰屼笉鏄┖瀛楃涓?
                if (string.IsNullOrEmpty(kvp.Value))
                {
                    if (startInfo.EnvironmentVariables.ContainsKey(kvp.Key))
                    {
                        startInfo.EnvironmentVariables.Remove(kvp.Key);
                        _logger.LogDebug("移除环境变量: {Key}", kvp.Key);
                    }
                    continue;
                }
                // 闈炵┖鍊兼甯歌缃?
                startInfo.EnvironmentVariables[kvp.Key] = kvp.Value;
                _logger.LogDebug("设置环境变量: {Key} = {Value}", kvp.Key, kvp.Value);
            }
            
            // 鍦?Windows 涓婇澶栬缃紪鐮佺浉鍏崇幆澧冨彉閲?浠呭湪宸蹭慨鏀圭幆澧冨彉閲忔椂)
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
                // 璁剧疆鎺у埗鍙拌緭鍑轰唬鐮侀〉涓?UTF-8
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

        // 鍚姩杩涚▼
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

        // 妫€鏌ュ惎鍔ㄩ敊璇?
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

        RegisterActiveSessionProcess(sessionId, process);

        WriteStandardInput(process, BuildStandardInput(tool, adapter, requestWithContext, sessionContext, useLowInterruption, lowInterruptionPrompt));
        
        _logger.LogInformation("进程已启动，PID: {ProcessId}，开始读取输出流", process.Id);

        // 鍒涘缓瓒呮椂鍙栨秷浠ょ墝
        using var timeoutCts = new CancellationTokenSource();
        if (tool.TimeoutSeconds > 0)
        {
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(tool.TimeoutSeconds));
        }

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, timeoutCts.Token);
        using var streamReadCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token);

        // 鍚屾椂璇诲彇鏍囧噯杈撳嚭鍜岄敊璇緭鍑?
        // 娉ㄦ剰锛氭煇浜?CLI 宸ュ叿锛堝 Codex锛変細灏嗘甯歌緭鍑轰篃杈撳嚭鍒?stderr
        _logger.LogInformation("鍒涘缓鏍囧噯杈撳嚭璇诲彇浠诲姟");
        var outputTask = ReadStreamAsync(process.StandardOutput, false, streamReadCts.Token);
        _logger.LogInformation("鍒涘缓閿欒杈撳嚭璇诲彇浠诲姟");
        var errorTask = ReadStreamAsync(process.StandardError, false, streamReadCts.Token); // 涓嶆爣璁颁负閿欒

        _logger.LogInformation("寮€濮嬪悎骞舵祦杈撳嚭");
        int chunkCount = 0;
        bool terminalEventDetected = false;
        bool terminalEventIsError = false;
        string? terminalEventErrorMessage = null;
        var terminalEventBuffer = hasAdapter && adapter!.SupportsStreamParsing ? new StringBuilder() : null;
        var fullOutput = new StringBuilder(); // 鐢ㄤ簬瑙ｆ瀽thread id

        // 鍚堝苟涓や釜娴佺殑杈撳嚭
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
                    "娴佽緭鍑哄悎骞跺凡鍙栨秷锛岀瓑寰呭悗缁秴鏃?鍙栨秷鏀跺熬閫昏緫: Tool={Tool}, Session={Session}, TimeoutTriggered={TimeoutTriggered}, CallerCancelled={CallerCancelled}",
                    tool.Name,
                    sessionId,
                    timeoutCts.IsCancellationRequested,
                    cancellationToken.IsCancellationRequested);
                break;
            }

            chunkCount++;

            // 鏀堕泦杈撳嚭鐢ㄤ簬鍚庣画瑙ｆ瀽session id
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
                    "妫€娴嬪埌涓€娆℃€ц繘绋嬮€傞厤鍣ㄧ粓姝簨浠讹紝鎻愬墠缁撴潫褰撳墠杞緭鍑鸿鍙? Tool={ToolId}, Session={SessionId}, IsError={IsError}",
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
        
        // 濡傛灉鏈夐€傞厤鍣ㄤ笖杩樻病鏈塁LI绾跨▼ID锛屽皾璇曚粠杈撳嚭涓В鏋?
        if (hasAdapter && string.IsNullOrEmpty(cliThreadId))
        {
            var output = fullOutput.ToString();
            var parsedThreadId = ParseCliThreadId(output, adapter!);
            if (!string.IsNullOrEmpty(parsedThreadId))
            {
                SetCliThreadId(sessionId, parsedThreadId);
                _logger.LogInformation("瑙ｆ瀽鍒癈LI Thread ID: {CliThread} for 浼氳瘽: {Session}", parsedThreadId, sessionId);
            }
        }
        

        // 绛夊緟杩涚▼閫€鍑?
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
                        _logger.LogWarning(ex, "缁堟杩涚▼澶辫触");
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
                        _logger.LogWarning(ex, "Failed to terminate process after cancellation.");
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
                ErrorMessage = $"执行超时 ({tool.TimeoutSeconds} 秒)。"
            };
        }
        else if (processCancelled)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = "执行已取消。"
            };
        }
        else if (terminalEventDetected && terminalEventIsError)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = terminalEventErrorMessage ?? "鎵ц澶辫触"
            };
        }
        else if (terminalEventDetected)
        {
            yield return new StreamOutputChunk
            {
                IsCompleted = true,
                Content = string.Empty
            };

            _logger.LogInformation("CLI 宸ュ叿閫氳繃璇箟缁堟浜嬩欢瀹屾垚: {Tool}", tool.Name);
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

            _logger.LogWarning("CLI 宸ュ叿鎵ц澶辫触: {Tool}, 閫€鍑轰唬鐮? {ExitCode}, 閿欒淇℃伅: {ErrorMessage}",
                tool.Name, process.ExitCode, failureMessage);
        }
        else
        {
            // 杩斿洖瀹屾垚鏍囪
            yield return new StreamOutputChunk
            {
                IsCompleted = true,
                Content = string.Empty
            };

            _logger.LogInformation("CLI 宸ュ叿鎵ц瀹屾垚: {Tool}, 閫€鍑轰唬鐮? {ExitCode}", 
                tool.Name, process.ExitCode);
        }

        UnregisterActiveSessionProcess(sessionId, process);
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
                "绛夊緟璇箟缁堟鍚庣殑杩涚▼鑷劧閫€鍑烘椂鍙戠敓寮傚父"))
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
                "璇箟缁堟浜嬩欢宸插埌杈撅紝浼橀泤閫€鍑轰粛鏈畬鎴愶紝寮€濮嬪己鍒剁粨鏉熶竴娆℃€ц繘绋? Tool={ToolId}, Session={SessionId}, PID={ProcessId}",
                toolId,
                sessionId,
                process.Id);
            process.Kill(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "涓诲姩缁撴潫涓€娆℃€ц繘绋嬪け璐? Tool={ToolId}, Session={SessionId}", toolId, sessionId);
            return;
        }

        await WaitForProcessExitWithinAsync(
            process,
            TimeSpan.FromSeconds(2),
            toolId,
            sessionId,
            "绛夊緟琚粓姝㈢殑涓€娆℃€ц繘绋嬮€€鍑烘椂鍙戠敓寮傚父");
    }

    /// <summary>
    /// 璇箟瀹屾垚鍚庝紭鍏堝皾璇曟俯鍜岀粨鏉?CLI锛屽敖閲忕粰鍘熺敓 history/rollout 鍒风洏鐣欐椂闂达紝鍐嶅洖閫€鍒板己鍒剁粨鏉熴€?
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
                "绛夊緟涓荤獥鍙ｅ叧闂悗杩涚▼閫€鍑烘椂鍙戠敓寮傚父"))
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
                "璇箟缁堟浜嬩欢宸插埌杈撅紝宸茶姹備竴娆℃€ц繘绋嬮€氳繃涓荤獥鍙ｅ叧闂€€鍑? Tool={ToolId}, Session={SessionId}, PID={ProcessId}",
                toolId,
                sessionId,
                process.Id);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "璇锋眰涓€娆℃€ц繘绋嬮€氳繃涓荤獥鍙ｅ叧闂€€鍑哄け璐? Tool={ToolId}, Session={SessionId}", toolId, sessionId);
            return false;
        }
    }

    private async IAsyncEnumerable<(string content, bool isError)> ReadStreamAsync(
        StreamReader reader,
        bool isErrorStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("寮€濮嬭鍙栨祦锛宨sErrorStream: {IsError}", isErrorStream);
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
                _logger.LogInformation("Stream read canceled, isErrorStream: {IsError}, chunksRead: {Count}", isErrorStream, chunkCount);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "璇诲彇娴佹椂鍙戠敓閿欒锛宨sErrorStream: {IsError}", isErrorStream);
                break;
            }

            if (charsRead <= 0)
            {
                _logger.LogInformation("Stream ended, isErrorStream: {IsError}, totalChunks: {Count}", isErrorStream, chunkCount);
                break;
            }

            chunkCount++;
            var content = new string(buffer, 0, charsRead);
            var trimmedPreview = content.TrimStart();
            if (!trimmedPreview.StartsWith("{\"type\":\"system\""))
            {
                var preview = content.Length > 100 ? content[..100] + "..." : content;
                _logger.LogDebug("CLI杈撳嚭鍧? {Preview}", preview.Replace("\r", "\\r").Replace("\n", "\\n"));
            }

            yield return (content, isErrorStream);
        }
    }

    private async IAsyncEnumerable<StreamOutputChunk> MergeStreamsAsync(
        IAsyncEnumerable<(string content, bool isError)> outputStream,
        IAsyncEnumerable<(string content, bool isError)> errorStream,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // 浣跨敤 Channel 鏉ユ洿楂樻晥鍦板悎骞朵袱涓祦
        var channel = System.Threading.Channels.Channel.CreateUnbounded<StreamOutputChunk>();
        var writer = channel.Writer;

        // 璇诲彇鏍囧噯杈撳嚭娴佺殑浠诲姟
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
                _logger.LogDebug("鏍囧噯杈撳嚭娴佽鍙栬鍙栨秷");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "璇诲彇鏍囧噯杈撳嚭娴佹椂鍙戠敓閿欒");
            }
        }, cancellationToken);

        // 璇诲彇閿欒杈撳嚭娴佺殑浠诲姟
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
                _logger.LogDebug("閿欒杈撳嚭娴佽鍙栬鍙栨秷");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "璇诲彇閿欒杈撳嚭娴佹椂鍙戠敓閿欒");
            }
        }, cancellationToken);

        // 绛夊緟鎵€鏈夎鍙栦换鍔″畬鎴愬悗鍏抽棴 writer
        _ = Task.WhenAll(outputTask, errorTask).ContinueWith(_ =>
        {
            writer.Complete();
            _logger.LogDebug("鎵€鏈夋祦璇诲彇瀹屾垚锛屽叧闂?channel");
        }, cancellationToken);

        // 浠?channel 涓鍙栧苟杩斿洖缁撴灉
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
            _logger.LogWarning(ex, "鎸夌敤鎴疯繃婊?CLI 宸ュ叿澶辫触锛屽洖閫€鍒板叏灞€宸ュ叿鍒楄〃");
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

        // 楠岃瘉鍛戒护鏄惁瀛樺湪锛堢畝鍗曟鏌ワ級
        try
        {
            // 瀵逛簬 Windows 绯荤粺锛屾鏌ュ懡浠ゆ槸鍚﹀彲鎵ц
            if (OperatingSystem.IsWindows())
            {
                // 濡傛灉鏄畬鏁磋矾寰勶紝妫€鏌ユ枃浠舵槸鍚﹀瓨鍦?
                if (Path.IsPathRooted(tool.Command))
                {
                    return File.Exists(tool.Command);
                }
                // 鍚﹀垯鍋囪鏄郴缁熷懡浠わ紝杩斿洖 true
                return true;
            }
            else
            {
                // 瀵逛簬 Linux/Mac锛屽彲浠ヤ娇鐢?which 鍛戒护妫€鏌?
                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 杞箟鍛戒护琛屽弬鏁颁互闃叉娉ㄥ叆鏀诲嚮
    /// </summary>
    private string EscapeArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }

        // 瀵逛簬 Windows 绯荤粺
        if (OperatingSystem.IsWindows())
        {
            // 鏇挎崲鍙屽紩鍙峰苟鐢ㄥ弻寮曞彿鍖呰９
            return $"\"{argument.Replace("\"", "\\\"")}\"";
        }
        else
        {
            // 瀵逛簬 Linux/Mac 绯荤粺
            return $"'{argument.Replace("'", "'\\''")}'";
        }
    }

    /// <summary>
    /// 鑾峰彇鎴栧垱寤轰細璇濅笓灞炵殑宸ヤ綔鐩綍
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

                _logger.LogWarning("缂撳瓨涓殑浼氳瘽宸ヤ綔鐩綍宸蹭笉瀛樺湪锛屽皢閲嶆柊瑙ｆ瀽: {SessionId}, {Workspace}", sessionId, existingWorkspace);
                _sessionWorkspaces.Remove(sessionId);
            }

            // 浼樺厛浣跨敤鏁版嵁搴撲腑宸茬粦瀹氱殑宸ヤ綔鐩綍锛堥€傜敤浜庯細鑷畾涔夌洰褰?/ 澶栭儴浼氳瘽瀵煎叆 / 杩涚▼閲嶅惎鍚庢仮澶嶏級
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

                    // 鑷畾涔夌洰褰曚絾涓嶅瓨鍦ㄦ椂锛岄伩鍏嶆倓鎮勫垱寤轰复鏃剁洰褰曞鑷磋鐢?
                    if (session.IsCustomWorkspace && !string.IsNullOrWhiteSpace(session.WorkspacePath))
                    {
                        throw new InvalidOperationException(
                            $"浼氳瘽 {sessionId} 宸ヤ綔鐩綍涓嶅瓨鍦ㄦ垨宸茶娓呯悊锛岃閲嶆柊鍒涘缓浼氳瘽");
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // 鑷畾涔夌洰褰曚笉瀛樺湪鏃讹細鐩存帴澶辫触锛岄伩鍏嶉潤榛樺垱寤轰复鏃剁洰褰曞鑷磋鐢?
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "浠庢暟鎹簱鎭㈠浼氳瘽宸ヤ綔鐩綍澶辫触锛屽皢鍒涘缓涓存椂鐩綍: {SessionId}", sessionId);
            }

            // 鍒涘缓鏂扮殑浼氳瘽宸ヤ綔鐩綍
            var workspaceRoot = GetEffectiveWorkspaceRoot();
            var workspacePath = Path.Combine(workspaceRoot, sessionId);
            
            try
            {
                if (!Directory.Exists(workspacePath))
                {
                    Directory.CreateDirectory(workspacePath);
                    _logger.LogInformation("涓轰細璇?{SessionId} 鍒涘缓宸ヤ綔鐩綍: {Path}", sessionId, workspacePath);
                }

                _sessionWorkspaces[sessionId] = workspacePath;
                
                // 鍦ㄥ伐浣滅洰褰曚腑鍒涘缓涓€涓爣璁版枃浠?璁板綍鍒涘缓鏃堕棿
                var markerFile = Path.Combine(workspacePath, ".workspace_info");
                File.WriteAllText(markerFile, $"Created: {DateTime.UtcNow:O}\nSessionId: {sessionId}");

                // 鏈€浣冲姫鍔涳細鎶婃柊鍒涘缓鐨勪复鏃剁洰褰曠粦瀹氬啓鍥炴暟鎹簱锛岄伩鍏嶅悗缁?GetSessionWorkspacePath 鏌ヨ涓嶅埌
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
                    _logger.LogDebug(ex, "鍐欏洖浼氳瘽宸ヤ綔鐩綍缁戝畾澶辫触(鍙拷鐣?: {SessionId}", sessionId);
                }
                
                return workspacePath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "鍒涘缓浼氳瘽宸ヤ綔鐩綍澶辫触: {SessionId}", sessionId);
                // 鍒涘缓澶辫触鐩存帴鎶涘嚭寮傚父锛屼笉闄嶇骇浣跨敤鏍圭洰褰?
                throw new InvalidOperationException($"鍒涘缓浼氳瘽 {sessionId} 宸ヤ綔鐩綍澶辫触: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// 娓呯悊鎸囧畾浼氳瘽鐨勫伐浣滃尯
    /// </summary>
    public void CleanupSessionWorkspace(string sessionId)
    {
        // 娓呯悊鎸佷箙鍖栬繘绋?
        _processManager.CleanupSessionProcesses(sessionId);

        // 娓呯悊CLI thread id
        lock (_cliSessionLock)
        {
            _cliThreadIds.Remove(sessionId);
        }

        string? workspacePath = null;
        bool isCustomWorkspace = false;

        // 鏌ヨ浼氳瘽淇℃伅鍒ゆ柇鐩綍绫诲瀷
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
            _logger.LogWarning(ex, "Failed to query session info for {SessionId}; falling back to temp workspace handling.", sessionId);
        }

        // 娓呯悊鍐呭瓨缂撳瓨
        lock (_workspaceLock)
        {
            if (_sessionWorkspaces.TryGetValue(sessionId, out var cachedPath))
            {
                workspacePath ??= cachedPath;
                _sessionWorkspaces.Remove(sessionId);
            }
        }

        // 鑷畾涔夌洰褰曪細鍙В闄ょ粦瀹氾紝涓嶅垹闄ゅ唴瀹?
        if (isCustomWorkspace)
        {
            _logger.LogInformation("宸茶В闄よ嚜瀹氫箟鐩綍浼氳瘽 {SessionId} 鐨勭粦瀹氾紝淇濈暀鐩綍鍐呭: {Path}", sessionId, workspacePath);
            return;
        }

        // 涓存椂鐩綍锛氭墽琛屽垹闄ら€昏緫
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

            // 闃插尽锛氬彧鍏佽鍒犻櫎 TempWorkspaceRoot 涓嬬殑涓存椂鐩綍
            if (!IsPathWithinDirectory(rootFullPath, workspaceFullPath) ||
                !string.Equals(workspaceFullPath, expectedTempWorkspacePath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("璺宠繃娓呯悊闈炰复鏃剁洰褰? {SessionId}, {Path}", sessionId, workspaceFullPath);
                return;
            }

            if (Directory.Exists(workspaceFullPath))
            {
                try
                {
                    Directory.Delete(workspaceFullPath, recursive: true);
                    _logger.LogInformation("宸叉竻鐞嗕复鏃朵細璇?{SessionId} 鐨勫伐浣滅洰褰? {Path}", sessionId, workspaceFullPath);
                }
                catch (Exception ex)
                {
                    // Windows 涓婂父瑙佸師鍥狅細鍙灞炴€с€佽鍗犵敤銆?
                    try
                    {
                        NormalizeDirectoryAttributes(workspaceFullPath);
                        Directory.Delete(workspaceFullPath, recursive: true);
                        _logger.LogInformation("宸叉竻鐞嗕复鏃朵細璇?{SessionId} 鐨勫伐浣滅洰褰?閲嶈瘯鎴愬姛): {Path}", sessionId, workspaceFullPath);
                    }
                    catch
                    {
                        _logger.LogWarning(ex, "娓呯悊涓存椂浼氳瘽宸ヤ綔鐩綍澶辫触: {SessionId}, {Path}", sessionId, workspaceFullPath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "娓呯悊涓存椂浼氳瘽宸ヤ綔鐩綍澶辫触(璺緞瑙ｆ瀽寮傚父): {SessionId}", sessionId);
        }
    }

    private static void NormalizeDirectoryAttributes(string directoryPath)
    {
        try
        {
            // 鍏堝鐞嗘枃浠?
            foreach (var file in Directory.EnumerateFiles(directoryPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                    // 蹇界暐鍗曚釜鏂囦欢澶辫触
                }
            }

            // 鍐嶅鐞嗙洰褰?
            foreach (var dir in Directory.EnumerateDirectories(directoryPath, "*", SearchOption.AllDirectories))
            {
                try
                {
                    new DirectoryInfo(dir).Attributes = FileAttributes.Normal;
                }
                catch
                {
                    // 蹇界暐鍗曚釜鐩綍澶辫触
                }
            }

            // 鏈€鍚庡鐞嗘牴鐩綍
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
    /// 浣跨敤閫傞厤鍣ㄤ粠CLI杈撳嚭涓В鏋恡hread id
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

                // 浣跨敤閫傞厤鍣ㄨВ鏋愯緭鍑鸿
                var outputEvent = adapter.ParseOutputLine(trimmedLine);
                if (outputEvent != null)
                {
                    var sessionId = adapter.ExtractSessionId(outputEvent);
                    if (!string.IsNullOrWhiteSpace(sessionId))
                    {
                        _logger.LogDebug("浠庤緭鍑轰腑瑙ｆ瀽鍒癈LI thread id: {ThreadId}", sessionId);
                        return sessionId;
                    }
                }
            }

            _logger.LogDebug("鏈兘浠嶤LI杈撳嚭涓В鏋愬埌thread id");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "瑙ｆ瀽CLI thread id澶辫触");
            return null;
        }
    }

    /// <summary>
    /// 浠嶤LI杈撳嚭涓瀯寤烘洿鍏蜂綋鐨勫け璐ヤ俊鎭紝浼樺厛杩斿洖閫傞厤鍣ㄨВ鏋愬嚭鐨勪笂娓搁敊璇?
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

        return $"Execution failed (exit code {exitCode}).";
    }

    /// <summary>
    /// 浣跨敤閫傞厤鍣ㄤ粠CLI杈撳嚭涓В鏋愬け璐ュ師鍥?
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
            _logger.LogDebug(ex, "瑙ｆ瀽CLI澶辫触淇℃伅澶辫触");
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
               string.Equals(message, "Current interaction failed.", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(message, "An unknown error occurred.", StringComparison.OrdinalIgnoreCase);
    }
    
    /// <summary>
    /// 浠嶤odex杈撳嚭涓В鏋恡hread id锛堝吋瀹规棫鐗坰ession id鏍煎紡锛?
    /// 宸插簾寮冿紝淇濈暀鐢ㄤ簬鍚戝悗鍏煎
    /// </summary>
    [Obsolete("璇蜂娇鐢?ParseCliThreadId 鏂规硶")]
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

                // 浼樺厛灏濊瘯瑙ｆ瀽JSONL鏍煎紡
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
                                _logger.LogDebug("浠嶫SONL杈撳嚭涓В鏋愬埌thread id: {ThreadId}", threadId);
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
                                _logger.LogDebug("浠嶫SONL item涓В鏋愬埌thread id: {ThreadId}", threadId);
                                return threadId;
                            }
                        }
                    }
                    catch (JsonException jsonEx)
                    {
                        _logger.LogDebug(jsonEx, "瑙ｆ瀽Codex JSONL琛屽け璐ワ紝灏嗗皾璇曟棫鏍煎紡");
                    }
                }

                // 鍏煎鏃х増鏈瑂ession id鏂囨湰鏍煎紡
                if (trimmedLine.StartsWith("session id:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = trimmedLine.Split(':', 2);
                    if (parts.Length == 2)
                    {
                        var legacyId = parts[1].Trim();
                        if (!string.IsNullOrWhiteSpace(legacyId))
                        {
                            _logger.LogDebug("浠庢棫鏍煎紡杈撳嚭涓В鏋愬埌thread id: {ThreadId}", legacyId);
                            return legacyId;
                        }
                    }
                }
            }

            _logger.LogWarning("鏈兘浠嶤odex杈撳嚭涓В鏋愬埌thread id");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "瑙ｆ瀽Codex thread id澶辫触");
            return null;
        }
    }
    
    /// <summary>
    /// 杞箟JSON瀛楃涓蹭腑鐨勭壒娈婂瓧绗?
    /// </summary>
    private string EscapeJsonString(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }
        
        return input
            .Replace("\\", "\\\\")  // 鍙嶆枩鏉?
            .Replace("\"", "\\\"")  // 鍙屽紩鍙?
            .Replace("\n", "\\n")   // 鎹㈣
            .Replace("\r", "\\r")   // 鍥炶溅
            .Replace("\t", "\\t");  // 鍒惰〃绗?
    }

    /// <summary>
    /// 娓呯悊鎵€鏈夎繃鏈熺殑浼氳瘽宸ヤ綔鍖?
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
            
            _logger.LogInformation("Starting cleanup of expired workspaces. Total directories: {Count}", directories.Length);

            foreach (var dir in directories)
            {
                try
                {
                    var markerFile = Path.Combine(dir, ".workspace_info");
                    
                    // 妫€鏌ユ爣璁版枃浠剁殑鏈€鍚庝慨鏀规椂闂?
                    DateTime lastAccessTime;
                    if (File.Exists(markerFile))
                    {
                        lastAccessTime = File.GetLastWriteTimeUtc(markerFile);
                    }
                    else
                    {
                        // 濡傛灉娌℃湁鏍囪鏂囦欢,浣跨敤鐩綍鐨勬渶鍚庤闂椂闂?
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
                        _logger.LogInformation("宸叉竻鐞嗚繃鏈熷伐浣滃尯: {Path}, 鏈€鍚庤闂椂闂? {Time}", dir, lastAccessTime);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "娓呯悊鐩綍澶辫触: {Dir}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clean up expired workspaces.");
        }
    }

    /// <summary>
    /// 鑾峰彇浼氳瘽宸ヤ綔鍖鸿矾寰?
    /// </summary>
    public string GetSessionWorkspacePath(string sessionId)
    {
        lock (_workspaceLock)
        {
            if (_sessionWorkspaces.TryGetValue(sessionId, out var path))
            {
                return path;
            }

            // 缂撳瓨涓㈠け鏃舵煡璇㈡暟鎹簱鑾峰彇浼氳瘽缁戝畾鐨勫伐浣滅洰褰?
            using var scope = _serviceProvider.CreateScope();
            var chatSessionRepository = scope.ServiceProvider.GetRequiredService<IChatSessionRepository>();
            var session = chatSessionRepository.GetByIdAsync(sessionId).GetAwaiter().GetResult();

            if (session != null && !string.IsNullOrEmpty(session.WorkspacePath) && Directory.Exists(session.WorkspacePath))
            {
                _sessionWorkspaces[sessionId] = session.WorkspacePath;
                return session.WorkspacePath;
            }

            // 浼氳瘽涓嶅瓨鍦ㄦ垨宸ヤ綔鐩綍鏃犳晥锛屾姏鍑哄紓甯革紙涓嶈嚜鍔ㄥ垱寤轰复鏃剁洰褰曪級
            throw new InvalidOperationException($"浼氳瘽 {sessionId} 宸ヤ綔鐩綍涓嶅瓨鍦ㄦ垨宸茶娓呯悊锛岃閲嶆柊鍒涘缓浼氳瘽");
        }
    }
    
    /// <summary>
    /// 鍒濆鍖栦細璇濆伐浣滃尯锛堝彲閫夋嫨鍏宠仈椤圭洰锛?
    /// </summary>
    public async Task<string> InitializeSessionWorkspaceAsync(string sessionId, string? projectId = null, bool includeGit = false)
    {
        // 鍏堝垱寤哄熀鏈伐浣滃尯
        var workspacePath = GetOrCreateSessionWorkspace(sessionId);
        
        // 濡傛灉鎸囧畾浜嗛」鐩甀D锛屼粠椤圭洰澶嶅埗浠ｇ爜
        if (!string.IsNullOrEmpty(projectId))
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var projectService = scope.ServiceProvider.GetService<IProjectService>();
                
                if (projectService != null)
                {
                    // 妫€鏌ュ伐浣滃尯鏄惁涓虹┖锛堝彧鏈夌┖宸ヤ綔鍖烘墠澶嶅埗椤圭洰浠ｇ爜锛?
                    var workspaceIsEmpty = !Directory.Exists(workspacePath) || 
                                           !Directory.EnumerateFileSystemEntries(workspacePath)
                                               .Any(e => !Path.GetFileName(e).StartsWith(".workspace"));
                    
                    if (workspaceIsEmpty)
                    {
                        var (success, errorMessage) = await projectService.CopyProjectToWorkspaceAsync(projectId, workspacePath, includeGit);
                        
                        if (success)
                        {
                            _logger.LogInformation("宸蹭粠椤圭洰 {ProjectId} 澶嶅埗浠ｇ爜鍒颁細璇濆伐浣滃尯 {SessionId}", projectId, sessionId);
                        }
                        else
                        {
                            _logger.LogWarning("浠庨」鐩鍒朵唬鐮佸け璐? {Error}", errorMessage);
                        }
                    }
                    else
                    {
                        _logger.LogInformation("浼氳瘽宸ヤ綔鍖哄凡鏈夊唴瀹癸紝璺宠繃椤圭洰浠ｇ爜澶嶅埗: {SessionId}", sessionId);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "鍒濆鍖栭」鐩唬鐮佸埌宸ヤ綔鍖哄け璐? {SessionId}, {ProjectId}", sessionId, projectId);
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
        CliExecutionRequest request,
        CliSessionContext sessionContext,
        bool useLowInterruption,
        string? lowInterruptionPrompt)
    {
        if (!useLowInterruption && IsCodexExecution(tool, adapter))
        {
            return request.BuildPromptText();
        }

        if (!useLowInterruption)
        {
            return null;
        }

        if (IsCodexExecution(tool, adapter))
        {
            return null;
        }

        return null;
    }

    private async IAsyncEnumerable<StreamOutputChunk> ExecuteCodexGoalRuntimeStreamAsync(
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
        var sessionContext = await BuildCliSessionContextAsync(
            sessionId,
            tool.Id,
            sessionWorkspace,
            GetCliThreadId(sessionId),
            environmentVariables,
            cancellationToken);
        sessionContext.WorkingDirectory = workingDirectory;

        var resolvedCommand = ResolveCommandPath(tool.Command);
        var existingThreadId = GetCliThreadId(sessionId);
        string threadId = string.Empty;
        StreamOutputChunk? terminalChunk = null;
        AppServerTurnRun? turnRun = null;

        await BestEffortSyncCodexGoalRuntimeThreadProviderAsync(
            sessionId,
            sessionWorkspace,
            existingThreadId,
            cancellationToken);

        try
        {
            threadId = await _codexAppServerSessionManager.EnsureThreadAsync(
                sessionId,
                resolvedCommand,
                tool,
                workingDirectory,
                environmentVariables,
                sessionContext,
                existingThreadId,
                cancellationToken);
            SetCliThreadId(sessionId, threadId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "启动 Codex app-server goal runtime 失败: Session={SessionId}", sessionId);
            terminalChunk = new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = $"启动 Codex goal runtime 失败: {ex.Message}"
            };
        }

        if (terminalChunk != null)
        {
            yield return terminalChunk;
            yield break;
        }

        if (TryParseGoalRuntimeCommand(userPrompt, out var commandKind, out var goalObjective))
        {
            switch (commandKind)
            {
                case GoalRuntimeCommandKind.Status:
                {
                    AppServerGoalSnapshot? goal = null;

                    try
                    {
                        goal = await _codexAppServerSessionManager.GetGoalAsync(
                            sessionId,
                            resolvedCommand,
                            tool,
                            workingDirectory,
                            environmentVariables,
                            sessionContext,
                            threadId,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "查询 goal 状态失败: Session={SessionId}, Thread={ThreadId}", sessionId, threadId);
                        terminalChunk = new StreamOutputChunk
                        {
                            IsError = true,
                            IsCompleted = true,
                            ErrorMessage = $"查询 goal 状态失败: {ex.Message}"
                        };
                    }

                    if (terminalChunk != null)
                    {
                        yield return terminalChunk;
                        yield break;
                    }

                    yield return new StreamOutputChunk
                    {
                        Content = FormatGoalStatusMarkdown(goal),
                        IsCompleted = true
                    };
                    yield break;
                }
                case GoalRuntimeCommandKind.Pause:
                {
                    AppServerGoalSnapshot? goal = null;

                    try
                    {
                        goal = await _codexAppServerSessionManager.GetGoalAsync(
                            sessionId,
                            resolvedCommand,
                            tool,
                            workingDirectory,
                            environmentVariables,
                            sessionContext,
                            threadId,
                            cancellationToken);

                        await _codexAppServerSessionManager.InterruptActiveTurnAsync(
                            sessionId,
                            resolvedCommand,
                            tool,
                            workingDirectory,
                            environmentVariables,
                            sessionContext,
                            threadId,
                            cancellationToken);

                        if (goal != null)
                        {
                            await _codexAppServerSessionManager.SetGoalAsync(
                                sessionId,
                                resolvedCommand,
                                tool,
                                workingDirectory,
                                environmentVariables,
                                sessionContext,
                                goal.Objective,
                                "paused",
                                goal.TokenBudget,
                                threadId,
                                cancellationToken);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "暂停 goal 失败: Session={SessionId}, Thread={ThreadId}", sessionId, threadId);
                        terminalChunk = new StreamOutputChunk
                        {
                            IsError = true,
                            IsCompleted = true,
                            ErrorMessage = $"暂停 goal 失败: {ex.Message}"
                        };
                    }

                    if (terminalChunk != null)
                    {
                        yield return terminalChunk;
                        yield break;
                    }

                    yield return new StreamOutputChunk
                    {
                        Content = goal == null
                            ? "已停止当前 turn。当前线程没有活动 goal。"
                            : "已暂停当前 goal，并中断正在执行的 turn。",
                        IsCompleted = true
                    };
                    yield break;
                }
                case GoalRuntimeCommandKind.Clear:
                {
                    bool cleared = false;

                    try
                    {
                        await _codexAppServerSessionManager.InterruptActiveTurnAsync(
                            sessionId,
                            resolvedCommand,
                            tool,
                            workingDirectory,
                            environmentVariables,
                            sessionContext,
                            threadId,
                            cancellationToken);

                        cleared = await _codexAppServerSessionManager.ClearGoalAsync(
                            sessionId,
                            resolvedCommand,
                            tool,
                            workingDirectory,
                            environmentVariables,
                            sessionContext,
                            threadId,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "清除 goal 失败: Session={SessionId}, Thread={ThreadId}", sessionId, threadId);
                        terminalChunk = new StreamOutputChunk
                        {
                            IsError = true,
                            IsCompleted = true,
                            ErrorMessage = $"清除 goal 失败: {ex.Message}"
                        };
                    }

                    if (terminalChunk != null)
                    {
                        yield return terminalChunk;
                        yield break;
                    }

                    yield return new StreamOutputChunk
                    {
                        Content = cleared
                            ? "已清除当前 goal。"
                            : "当前线程没有可清除的 goal。",
                        IsCompleted = true
                    };
                    yield break;
                }
                case GoalRuntimeCommandKind.Resume:
                {
                    AppServerGoalSnapshot? goal = null;

                    try
                    {
                        goal = await _codexAppServerSessionManager.GetGoalAsync(
                            sessionId,
                            resolvedCommand,
                            tool,
                            workingDirectory,
                            environmentVariables,
                            sessionContext,
                            threadId,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "恢复 goal 前查询状态失败: Session={SessionId}, Thread={ThreadId}", sessionId, threadId);
                        terminalChunk = new StreamOutputChunk
                        {
                            IsError = true,
                            IsCompleted = true,
                            ErrorMessage = $"恢复 goal 失败: {ex.Message}"
                        };
                    }

                    if (terminalChunk != null)
                    {
                        yield return terminalChunk;
                        yield break;
                    }

                    if (goal == null)
                    {
                        yield return new StreamOutputChunk
                        {
                            Content = "当前线程没有可恢复的 goal。",
                            IsCompleted = true
                        };
                        yield break;
                    }

                    try
                    {
                        await _codexAppServerSessionManager.SetGoalAsync(
                            sessionId,
                            resolvedCommand,
                            tool,
                            workingDirectory,
                            environmentVariables,
                            sessionContext,
                            goal.Objective,
                            "active",
                            goal.TokenBudget,
                            threadId,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "恢复 goal 失败: Session={SessionId}, Thread={ThreadId}", sessionId, threadId);
                        terminalChunk = new StreamOutputChunk
                        {
                            IsError = true,
                            IsCompleted = true,
                            ErrorMessage = $"恢复 goal 失败: {ex.Message}"
                        };
                    }

                    if (terminalChunk != null)
                    {
                        yield return terminalChunk;
                        yield break;
                    }

                    yield return new StreamOutputChunk
                    {
                        Content = "已恢复 goal，正在继续推进...",
                        IsCompleted = false
                    };

                    await foreach (var chunk in StreamGoalRuntimeTurnsWhileActiveAsync(
                                       sessionId,
                                       resolvedCommand,
                                       tool,
                                       workingDirectory,
                                       environmentVariables,
                                       sessionContext,
                                       threadId,
                                       goal.Objective,
                                       goal.Objective,
                                       cancellationToken))
                    {
                        yield return chunk;
                    }

                    yield break;
                }
                case GoalRuntimeCommandKind.SetGoal:
                {
                    if (string.IsNullOrWhiteSpace(goalObjective))
                    {
                        yield return new StreamOutputChunk
                        {
                            Content = "goal 内容不能为空。",
                            IsCompleted = true
                        };
                        yield break;
                    }

                    try
                    {
                        await _codexAppServerSessionManager.SetGoalAsync(
                            sessionId,
                            resolvedCommand,
                            tool,
                            workingDirectory,
                            environmentVariables,
                            sessionContext,
                            goalObjective,
                            "active",
                            null,
                            threadId,
                            cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "提交 goal 失败: Session={SessionId}, Thread={ThreadId}", sessionId, threadId);
                        terminalChunk = new StreamOutputChunk
                        {
                            IsError = true,
                            IsCompleted = true,
                            ErrorMessage = $"提交 goal 失败: {ex.Message}"
                        };
                    }

                    if (terminalChunk != null)
                    {
                        yield return terminalChunk;
                        yield break;
                    }

                    yield return new StreamOutputChunk
                    {
                        Content = $"已提交 goal：{goalObjective}{Environment.NewLine}正在围绕该目标持续推进...",
                        IsCompleted = false
                    };

                    await foreach (var chunk in StreamGoalRuntimeTurnsWhileActiveAsync(
                                       sessionId,
                                       resolvedCommand,
                                       tool,
                                       workingDirectory,
                                       environmentVariables,
                                       sessionContext,
                                       threadId,
                                       goalObjective,
                                       goalObjective,
                                       cancellationToken))
                    {
                        yield return chunk;
                    }

                    yield break;
                }
            }
        }

        try
        {
            turnRun = await _codexAppServerSessionManager.StartTurnAsync(
                sessionId,
                resolvedCommand,
                tool,
                workingDirectory,
                environmentVariables,
                sessionContext,
                userPrompt,
                threadId,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "通过 Codex app-server 执行 turn 失败: Session={SessionId}, Thread={ThreadId}", sessionId, threadId);
            terminalChunk = new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = $"Codex goal runtime 执行失败: {ex.Message}"
            };
        }

        if (terminalChunk != null)
        {
            yield return terminalChunk;
            yield break;
        }

        if (turnRun != null)
        {
            yield return new StreamOutputChunk
            {
                Content = "已启动 goal runtime，正在执行...",
                IsCompleted = false
            };

            await foreach (var chunk in turnRun.Output.WithCancellation(cancellationToken))
            {
                yield return chunk;
            }
        }
    }

    private async IAsyncEnumerable<StreamOutputChunk> StreamGoalRuntimeTurnsWhileActiveAsync(
        string sessionId,
        string commandPath,
        CliToolConfig tool,
        string workingDirectory,
        Dictionary<string, string>? environmentVariables,
        CliSessionContext sessionContext,
        string threadId,
        string initialTurnPrompt,
        string goalObjective,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var nextTurnPrompt = initialTurnPrompt;
        var currentGoalObjective = goalObjective;

        while (!cancellationToken.IsCancellationRequested)
        {
            AppServerTurnRun? turnRun = null;
            StreamOutputChunk? terminalChunk = null;
            try
            {
                turnRun = await StartGoalTurnWithRetryAsync(
                    sessionId,
                    commandPath,
                    tool,
                    workingDirectory,
                    environmentVariables,
                    sessionContext,
                    nextTurnPrompt,
                    threadId,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "启动下一轮 goal runtime turn 失败: Session={SessionId}, Thread={ThreadId}", sessionId, threadId);
                terminalChunk = new StreamOutputChunk
                {
                    IsError = true,
                    IsCompleted = true,
                    ErrorMessage = $"Codex goal runtime 执行失败: {ex.Message}"
                };
            }

            if (terminalChunk != null)
            {
                yield return terminalChunk;
                yield break;
            }

            await foreach (var chunk in turnRun!.Output.WithCancellation(cancellationToken))
            {
                if (chunk.IsError)
                {
                    yield return chunk;
                    yield break;
                }

                if (chunk.IsCompleted)
                {
                    break;
                }

                yield return chunk;
            }

            AppServerGoalSnapshot? goalSnapshot = null;
            try
            {
                goalSnapshot = await _codexAppServerSessionManager.GetGoalAsync(
                    sessionId,
                    commandPath,
                    tool,
                    workingDirectory,
                    environmentVariables,
                    sessionContext,
                    threadId,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "读取 goal runtime 后续状态失败: Session={SessionId}, Thread={ThreadId}", sessionId, threadId);
                terminalChunk = new StreamOutputChunk
                {
                    IsError = true,
                    IsCompleted = true,
                    ErrorMessage = $"查询 goal 状态失败: {ex.Message}"
                };
            }

            if (terminalChunk != null)
            {
                yield return terminalChunk;
                yield break;
            }

            if (!string.IsNullOrWhiteSpace(goalSnapshot?.Objective))
            {
                currentGoalObjective = goalSnapshot.Objective;
            }

            if (!string.Equals(goalSnapshot?.Status, "active", StringComparison.OrdinalIgnoreCase))
            {
                yield return new StreamOutputChunk
                {
                    IsCompleted = true
                };
                yield break;
            }

            yield return new StreamOutputChunk
            {
                IsTurnBoundary = true
            };

            nextTurnPrompt = BuildGoalRuntimeContinuationPrompt(currentGoalObjective);
        }
    }

    private static string BuildGoalRuntimeContinuationPrompt(string goalObjective)
    {
        var trimmedObjective = goalObjective?.Trim();
        return string.IsNullOrWhiteSpace(trimmedObjective)
            ? $"{GoalRuntimeContinuationPromptPrefix}. Reuse the current thread context, do not restart from scratch, and keep going until the goal is complete or blocked on user input."
            : $"{GoalRuntimeContinuationPromptPrefix}: {trimmedObjective}. Reuse the current thread context, do not restart from scratch, and keep going until the goal is complete or blocked on user input.";
    }

    private async Task<AppServerTurnRun> StartGoalTurnWithRetryAsync(
        string sessionId,
        string commandPath,
        CliToolConfig tool,
        string workingDirectory,
        Dictionary<string, string>? environmentVariables,
        CliSessionContext sessionContext,
        string userPrompt,
        string? existingThreadId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _codexAppServerSessionManager.StartTurnAsync(
                sessionId,
                commandPath,
                tool,
                workingDirectory,
                environmentVariables,
                sessionContext,
                userPrompt,
                existingThreadId,
                cancellationToken);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("已有一个 turn", StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "检测到 goal runtime 线程仍有旧 turn，先尝试中断后重试: Session={SessionId}, Thread={ThreadId}",
                sessionId,
                existingThreadId);

            await _codexAppServerSessionManager.InterruptActiveTurnAsync(
                sessionId,
                commandPath,
                tool,
                workingDirectory,
                environmentVariables,
                sessionContext,
                existingThreadId,
                cancellationToken);

            return await _codexAppServerSessionManager.StartTurnAsync(
                sessionId,
                commandPath,
                tool,
                workingDirectory,
                environmentVariables,
                sessionContext,
                userPrompt,
                existingThreadId,
                cancellationToken);
        }
    }

    private static bool TryParseGoalRuntimeCommand(
        string? userPrompt,
        out GoalRuntimeCommandKind commandKind,
        out string? objective)
    {
        commandKind = GoalRuntimeCommandKind.None;
        objective = null;

        if (!IsGoalCommand(userPrompt))
        {
            return false;
        }

        var trimmed = userPrompt!.Trim();
        if (string.Equals(trimmed, "/goal", StringComparison.OrdinalIgnoreCase))
        {
            commandKind = GoalRuntimeCommandKind.Status;
            return true;
        }

        var suffix = trimmed["/goal".Length..].Trim();
        if (string.IsNullOrWhiteSpace(suffix))
        {
            commandKind = GoalRuntimeCommandKind.Status;
            return true;
        }

        if (string.Equals(suffix, "pause", StringComparison.OrdinalIgnoreCase))
        {
            commandKind = GoalRuntimeCommandKind.Pause;
            return true;
        }

        if (string.Equals(suffix, "clear", StringComparison.OrdinalIgnoreCase))
        {
            commandKind = GoalRuntimeCommandKind.Clear;
            return true;
        }

        if (string.Equals(suffix, "resume", StringComparison.OrdinalIgnoreCase))
        {
            commandKind = GoalRuntimeCommandKind.Resume;
            return true;
        }

        if (string.Equals(suffix, "status", StringComparison.OrdinalIgnoreCase)
            || string.Equals(suffix, "get", StringComparison.OrdinalIgnoreCase))
        {
            commandKind = GoalRuntimeCommandKind.Status;
            return true;
        }

        commandKind = GoalRuntimeCommandKind.SetGoal;
        objective = suffix;
        return true;
    }

    private static string FormatGoalStatusMarkdown(AppServerGoalSnapshot? goal)
    {
        if (goal == null)
        {
            return "Current thread has no active goal.";
        }

        var tokenText = goal.TokenBudget.HasValue
            ? $"{goal.TokensUsed}/{goal.TokenBudget.Value}"
            : goal.TokensUsed.ToString();

        return string.Join(
            Environment.NewLine,
            $"Current goal: {goal.Objective}",
            $"Status: {TranslateGoalStatus(goal.Status)}",
            $"Tokens used: {tokenText}",
            $"Time used: {goal.TimeUsedSeconds} seconds");
    }

    private static string TranslateGoalStatus(string? status)
    {
        return status?.Trim().ToLowerInvariant() switch
        {
            "active" => "running",
            "paused" => "paused",
            "budgetlimited" => "杈惧埌棰勭畻涓婇檺",
            "complete" => "completed",
            _ => string.IsNullOrWhiteSpace(status) ? "鏈煡" : status
        };
    }

    private enum GoalRuntimeCommandKind
    {
        None,
        Status,
        Pause,
        Clear,
        Resume,
        SetGoal
    }

    private static bool IsGoalCommand(string? userPrompt)
    {
        return !string.IsNullOrWhiteSpace(userPrompt)
               && userPrompt.TrimStart().StartsWith("/goal", StringComparison.OrdinalIgnoreCase);
    }

    private static TimeSpan? GetPersistentProcessNoOutputTimeout(
        CliToolConfig tool,
        string userPrompt,
        ICliToolAdapter? adapter)
    {
        // /goal may intentionally stay quiet for a while; keep Codex streams open until turn.completed/failed or explicit stop.
        if (IsGoalCommand(userPrompt) && IsCodexExecution(tool, adapter))
        {
            return null;
        }

        return TimeSpan.FromSeconds(2);
    }

    private static bool ShouldUseOneTimeExecutionDespitePersistentMode(
        CliToolConfig tool,
        ICliToolAdapter? adapter)
    {
        // Codex is driven by single exec/resume; stdin-based persistent reuse is unsupported.
        return tool.UsePersistentProcess && adapter is CodexAdapter;
    }

    private async Task EnsureGoalExecutionRuntimeAsync(
        string sessionId,
        string toolId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var session = await TryGetChatSessionAsync(sessionId);
        if (session == null)
        {
            return;
        }

        var effectiveToolId = SessionLaunchOverrideHelper.ResolveEffectiveToolId(toolId, session.CcSwitchSnapshotToolId);
        if (!string.Equals(effectiveToolId, "codex", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var currentOverrides = SessionLaunchOverrideHelper.Deserialize(session.ToolLaunchOverridesJson);
        var updatedOverrides = SessionLaunchOverrideHelper.ApplyGoalRuntimeOverride(
            currentOverrides,
            effectiveToolId,
            true);

        session.ToolLaunchOverridesJson = SessionLaunchOverrideHelper.Serialize(updatedOverrides);
        session.UpdatedAt = DateTime.Now;

        using var scope = _serviceProvider.CreateScope();
        var sessionRepository = scope.ServiceProvider.GetService<IChatSessionRepository>();
        if (sessionRepository == null)
        {
            _logger.LogDebug("鏃犳硶淇濆瓨 goal runtime 浼氳瘽瑕嗙洊: Session={SessionId}, Tool={ToolId}", sessionId, effectiveToolId);
            return;
        }

        try
        {
            await sessionRepository.InsertOrUpdateAsync(session);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "淇濆瓨 goal runtime 浼氳瘽瑕嗙洊澶辫触: Session={SessionId}, Tool={ToolId}", sessionId, effectiveToolId);
        }
    }

    private static bool IsNativeCodexGoalRuntimeTool(CliToolConfig tool)
    {
        return string.Equals(NormalizeManagedToolId(tool.Id), "codex", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCodexExecution(CliToolConfig tool, ICliToolAdapter? adapter)
    {
        return string.Equals(tool.Id, "codex", StringComparison.OrdinalIgnoreCase)
               || adapter is CodexAdapter
               || (adapter?.SupportedToolIds.Any(static id =>
                   string.Equals(id, "codex", StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    internal static Encoding GetCodexTransportEncoding()
    {
        return CodexTransportEncoding;
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
        // 濡傛灉鍛戒护宸茬粡鏄粷瀵硅矾寰?鐩存帴杩斿洖
        if (Path.IsPathRooted(command))
        {
            return command;
        }

        // Windows绯荤粺涓?灏濊瘯瑙ｆ瀽npm瀹夎鐨凜LI宸ュ叿
        if (OperatingSystem.IsWindows() && 
            (command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || 
             command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
             !command.Contains("."))) // 娌℃湁鎵╁睍鍚嶇殑,涔熷彲鑳芥槸npm宸ュ叿
        {
            // 纭繚鍛戒护鏈?cmd鎵╁睍鍚?
            var cmdFileName = command.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || 
                              command.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                ? command 
                : command + ".cmd";
            
            // 灏濊瘯浠庨厤缃垨鑷姩妫€娴嬭幏鍙杗pm鍏ㄥ眬璺緞
            var npmGlobalPath = GetNpmGlobalPath();
            
            if (!string.IsNullOrWhiteSpace(npmGlobalPath))
            {
                var fullPath = Path.Combine(npmGlobalPath, cmdFileName);
                
                // 妫€鏌ユ枃浠舵槸鍚﹀瓨鍦?濡傛灉瀛樺湪鍒欎娇鐢ㄥ畬鏁磋矾寰?
                if (File.Exists(fullPath))
                {
                    _logger.LogDebug("将相对命令 {Command} 解析为完整路径: {FullPath}", command, fullPath);
                    return fullPath;
                }
                
                _logger.LogDebug("npm 目录中未找到命令: {FullPath}, 尝试使用系统 PATH", fullPath);
            }
        }

        var resolvedFromPath = ResolveCommandFromPathEnvironment(command);
        if (!string.IsNullOrWhiteSpace(resolvedFromPath))
        {
            _logger.LogDebug("将系统 PATH 中的命令 {Command} 解析为完整路径: {FullPath}", command, resolvedFromPath);
            return resolvedFromPath;
        }

        // 鍚﹀垯杩斿洖鍘熷鍛戒护(鍋囪鏄郴缁烶ATH涓殑鍛戒护)
        if (OperatingSystem.IsWindows())
        {
            var pathResolvedCommand = TryResolveWindowsCommandPathFromPath(command);
            if (!string.IsNullOrWhiteSpace(pathResolvedCommand))
            {
                _logger.LogDebug("Windows PATH 解析到命令 {Command}: {FullPath}", command, pathResolvedCommand);
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
            return "Working directory is empty; cannot start the CLI process.";
        }

        if (Directory.Exists(workingDirectory))
        {
            return null;
        }

        return $"工作目录不存在: {workingDirectory} (Session={sessionId})。请检查目录或重新创建会话。";
    }

    /// <summary>
    /// 鑾峰彇NPM鍏ㄥ眬瀹夎璺緞锛堜紭鍏堜娇鐢ㄩ厤缃殑璺緞锛屽鏋滄湭閰嶇疆鍒欒嚜鍔ㄦ娴嬶級
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
        // 濡傛灉閰嶇疆涓寚瀹氫簡璺緞,鐩存帴浣跨敤
        if (!string.IsNullOrWhiteSpace(_options.NpmGlobalPath))
        {
            _logger.LogDebug("使用配置的 NPM 全局路径: {Path}", _options.NpmGlobalPath);
            return _options.NpmGlobalPath;
        }

        // Try to detect the global NPM path automatically.
        try
        {
            // Method 1: resolve via `npm config get prefix`.
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
                process.WaitForExit(5000); // 5-second timeout.
                if (process.ExitCode == 0)
                {
                    var prefix = process.StandardOutput.ReadToEnd().Trim();
                    if (!string.IsNullOrWhiteSpace(prefix) && Directory.Exists(prefix))
                    {
                        _logger.LogInformation("自动检测到 NPM 全局路径: {Path}", prefix);
                        return prefix;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "自动检测 NPM 全局路径失败，尝试使用环境变量。");
        }

        // Method 2: try common NPM paths from environment values.
        if (OperatingSystem.IsWindows())
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var npmPath = Path.Combine(appDataPath, "npm");
            
            if (Directory.Exists(npmPath))
            {
                _logger.LogInformation("通过 AppData 路径检测到 NPM 全局路径: {Path}", npmPath);
                return npmPath;
            }
        }
        else
        {
            // Linux/Mac commonly use /usr/local/bin or ~/.npm-global/bin.
            var possiblePaths = new[] 
            { 
                "/usr/local/bin", 
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".npm-global", "bin")
            };
            
            foreach (var path in possiblePaths)
            {
                if (Directory.Exists(path))
                {
                    _logger.LogInformation("检测到 NPM 全局路径: {Path}", path);
                    return path;
                }
            }
        }

        _logger.LogWarning("无法检测到 NPM 全局路径，将依赖 PATH 环境变量。");
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

    private static string ResolveUserProfilePath()
    {
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    }

    /// <summary>
    /// 灏嗕細璇濆浐瀹氱殑 Provider 蹇収鍚屾鍒板綋鍓?cc-switch 婵€娲?Provider
    /// </summary>
    public async Task<CcSwitchSessionSnapshot?> SyncSessionCcSwitchSnapshotAsync(string sessionId, string? toolId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var session = await TryGetChatSessionAsync(sessionId);
        var effectiveToolId = NormalizeManagedToolId(toolId ?? session?.ToolId);
        if (string.IsNullOrWhiteSpace(effectiveToolId))
        {
            throw new InvalidOperationException("Current session is not bound to a synchronizable CLI tool.");
        }

        if (!_ccSwitchService.IsManagedTool(effectiveToolId))
        {
            throw new InvalidOperationException("Current session is not bound to a cc-switch managed CLI tool.");
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
            throw new InvalidOperationException("Current session has no CLI thread; cannot sync the Codex provider.");
        }

        var session = await TryGetChatSessionAsync(sessionId);
        var effectiveToolId = NormalizeManagedToolId(toolId ?? session?.ToolId);
        if (string.IsNullOrWhiteSpace(effectiveToolId))
        {
            throw new InvalidOperationException("Current session is not bound to a synchronizable CLI tool.");
        }

        if (!_ccSwitchService.IsManagedTool(effectiveToolId))
        {
            throw new InvalidOperationException("Current session is not bound to a cc-switch managed CLI tool.");
        }

        var snapshot = await SyncSessionCcSwitchSnapshotAsync(sessionId, effectiveToolId, cancellationToken);
        if (string.IsNullOrWhiteSpace(snapshot?.ProviderId))
        {
            throw new InvalidOperationException("Current cc-switch provider is not available for synchronization.");
        }

        var sessionWorkspace = GetSessionWorkspacePath(sessionId);
        var targetProviderId = string.Equals(effectiveToolId, "codex", StringComparison.OrdinalIgnoreCase)
            ? await ResolveCodexModelProviderNameAsync(sessionWorkspace, snapshot.SourceLiveConfigPath, cancellationToken)
                ?? snapshot.ProviderId
            : snapshot.ProviderId;

        return await SyncCodexThreadProviderCoreAsync(
            sessionId,
            sessionWorkspace,
            cliThreadId,
            targetProviderId,
            includeSkillsSync: true,
            cancellationToken);
    }

    public async Task<AppServerGoalSnapshot?> TryGetGoalRuntimeGoalAsync(
        string sessionId,
        string? toolId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var session = await TryGetChatSessionAsync(sessionId);
        if (session == null)
        {
            return null;
        }

        var effectiveToolId = SessionLaunchOverrideHelper.ResolveEffectiveToolId(
            toolId ?? session.ToolId,
            session.CcSwitchSnapshotToolId);
        if (!string.Equals(effectiveToolId, "codex", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var launchOverride = await GetEffectiveSessionLaunchOverrideAsync(sessionId, effectiveToolId);
        if (launchOverride?.UseGoalRuntime != true)
        {
            return null;
        }

        var cliThreadId = GetCliThreadId(sessionId);
        if (string.IsNullOrWhiteSpace(cliThreadId)
            || !_codexAppServerSessionManager.HasRunningSession(sessionId, cliThreadId))
        {
            return null;
        }

        var username = ResolveUsernameForToolOperation(null, sessionId);
        var tool = GetTool(effectiveToolId, username);
        if (tool == null)
        {
            return null;
        }

        tool = ApplySessionLaunchOverride(tool, launchOverride);

        var sessionWorkspace = !string.IsNullOrWhiteSpace(session.WorkspacePath)
            ? session.WorkspacePath
            : GetOrCreateSessionWorkspace(sessionId);
        var workingDirectory = !string.IsNullOrWhiteSpace(tool.WorkingDirectory)
            ? tool.WorkingDirectory
            : sessionWorkspace;
        var environmentVariables = await GetToolEnvironmentVariablesAsync(tool.Id, username);
        var sessionContext = await BuildCliSessionContextAsync(
            sessionId,
            tool.Id,
            sessionWorkspace,
            cliThreadId,
            environmentVariables,
            cancellationToken);
        sessionContext.WorkingDirectory = workingDirectory;

        try
        {
            return await _codexAppServerSessionManager.GetGoalAsync(
                sessionId,
                ResolveCommandPath(tool.Command),
                tool,
                workingDirectory,
                environmentVariables,
                sessionContext,
                cliThreadId,
                cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "读取 Goal runtime goal 状态失败: Session={SessionId}", sessionId);
            return null;
        }
    }

    private async Task BestEffortSyncCodexGoalRuntimeThreadProviderAsync(
        string sessionId,
        string sessionWorkspace,
        string? cliThreadId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(cliThreadId))
        {
            return;
        }

        if (_codexAppServerSessionManager.HasRunningSession(sessionId, cliThreadId))
        {
            _logger.LogDebug(
                "复用现有 Codex goal runtime 会话，跳过线程 Provider 同步: Session={SessionId}, Thread={ThreadId}",
                sessionId,
                cliThreadId);
            return;
        }

        var targetProviderId = await ResolvePinnedCodexProviderIdAsync(sessionId, sessionWorkspace, cancellationToken);
        if (string.IsNullOrWhiteSpace(targetProviderId))
        {
            return;
        }

        try
        {
            var result = await SyncCodexThreadProviderCoreAsync(
                sessionId,
                sessionWorkspace,
                cliThreadId,
                targetProviderId,
                includeSkillsSync: false,
                cancellationToken);

            if (result.HasWarnings)
            {
                _logger.LogWarning(
                    "进入 Codex goal runtime 前同步线程 Provider 完成但有警告: Session={SessionId}, Thread={ThreadId}, Provider={ProviderId}, Message={Message}",
                    sessionId,
                    cliThreadId,
                    targetProviderId,
                    result.Message);
            }
            else
            {
                _logger.LogInformation(
                    "进入 Codex goal runtime 前已同步线程 Provider: Session={SessionId}, Thread={ThreadId}, Provider={ProviderId}",
                    sessionId,
                    cliThreadId,
                    targetProviderId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "进入 Codex goal runtime 前同步线程 Provider 失败，将继续尝试恢复线程: Session={SessionId}, Thread={ThreadId}, Provider={ProviderId}",
                sessionId,
                cliThreadId,
                targetProviderId);
        }
    }

    private async Task<CodexThreadProviderSyncResult> SyncCodexThreadProviderCoreAsync(
        string sessionId,
        string sessionWorkspace,
        string cliThreadId,
        string targetProviderId,
        bool includeSkillsSync,
        CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var threadSyncService = scope.ServiceProvider.GetRequiredService<ICodexThreadProviderSyncService>();
        var result = await threadSyncService.SyncThreadProviderAsync(
            new CodexThreadProviderSyncRequest
            {
                SessionWorkspacePath = sessionWorkspace,
                ThreadId = cliThreadId,
                TargetProviderId = targetProviderId
            },
            cancellationToken);

        if (!includeSkillsSync)
        {
            return result;
        }

        var skillsSyncMessage = SyncGlobalCodexSkillsToWorkspace(sessionWorkspace, sessionId);
        if (!string.IsNullOrWhiteSpace(skillsSyncMessage))
        {
            if (skillsSyncMessage.Contains("失败", StringComparison.Ordinal))
            {
                result.HasWarnings = true;
            }

            result.Message = string.IsNullOrWhiteSpace(result.Message)
                ? skillsSyncMessage
                : $"{result.Message}；{skillsSyncMessage}";
        }

        return result;
    }

    private async Task<string?> ResolvePinnedCodexProviderIdAsync(
        string sessionId,
        string sessionWorkspace,
        CancellationToken cancellationToken)
    {
        var session = await TryGetChatSessionAsync(sessionId);
        var configProviderId = await ResolveCodexModelProviderNameAsync(
            sessionWorkspace,
            session?.CcSwitchLiveConfigPath,
            cancellationToken);
        if (!string.IsNullOrWhiteSpace(configProviderId))
        {
            return configProviderId;
        }

        if (!string.IsNullOrWhiteSpace(session?.CcSwitchProviderId))
        {
            return session.CcSwitchProviderId;
        }

        return null;
    }

    private async Task<string?> ResolveCodexModelProviderNameAsync(
        string sessionWorkspace,
        string? sourceLiveConfigPath,
        CancellationToken cancellationToken)
    {
        var candidatePaths = new[]
        {
            Path.Combine(sessionWorkspace, GetCcSwitchSnapshotRelativePath("codex")),
            Path.Combine(GetCodexSessionConfigDirectory(sessionWorkspace), "config.toml"),
            GetCodexLaunchBaseConfigPath(sessionWorkspace),
            sourceLiveConfigPath
        };

        foreach (var candidatePath in candidatePaths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidatePath))
            {
                continue;
            }

            var content = await File.ReadAllTextAsync(candidatePath, cancellationToken);
            var providerId = ReadTomlStringSetting(content, "model_provider")
                             ?? ReadTomlStringSetting(content, "provider");
            if (!string.IsNullOrWhiteSpace(providerId))
            {
                return providerId;
            }
        }

        return null;
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
            _logger.LogWarning(ex, "鍚屾浼氳瘽 {SessionId} 鐨?cc-switch 蹇収澶辫触: {ToolId}", sessionId, normalizedToolId);
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
                    ? $"CLI tool {ResolveManagedToolDisplayName(toolId)} depends on cc-switch, but the current provider is not ready."
                    : status.StatusMessage);
        }

        if (string.IsNullOrWhiteSpace(status.LiveConfigPath) || !File.Exists(status.LiveConfigPath))
        {
            throw new InvalidOperationException(
                $"Could not find the live config file for the active provider of {ResolveManagedToolDisplayName(toolId)}. Complete the sync in cc-switch first.");
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
            Title = "New Session",
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

    private static CliToolConfig ApplySessionLaunchOverride(
        CliToolConfig tool,
        SessionToolLaunchOverride? launchOverride)
    {
        if (launchOverride?.UsePersistentProcess == null
            || tool.UsePersistentProcess == launchOverride.UsePersistentProcess.Value)
        {
            return tool;
        }

        return new CliToolConfig
        {
            Id = tool.Id,
            Name = tool.Name,
            Description = tool.Description,
            Command = tool.Command,
            ArgumentTemplate = tool.ArgumentTemplate,
            LowInterruptionArgumentTemplate = tool.LowInterruptionArgumentTemplate,
            PersistentModeArguments = tool.PersistentModeArguments,
            UsePersistentProcess = launchOverride.UsePersistentProcess.Value,
            WorkingDirectory = tool.WorkingDirectory,
            Enabled = tool.Enabled,
            TimeoutSeconds = tool.TimeoutSeconds,
            EnvironmentVariables = tool.EnvironmentVariables == null
                ? null
                : new Dictionary<string, string>(tool.EnvironmentVariables, StringComparer.OrdinalIgnoreCase)
        };
    }

    private async Task PrepareManagedCodexLaunchConfigAsync(
        string sessionWorkspace,
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
            throw new InvalidOperationException(
                "Managed Codex launch requires a synchronized session config snapshot. WebCode will not generate config.toml.");
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

    private static string? ReadTomlStringSetting(string content, string key)
    {
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(key))
        {
            return null;
        }

        var match = Regex.Match(
            content,
            $@"(?m)^\s*{Regex.Escape(key)}\s*=\s*""(?<value>(?:\\.|[^""])*)""\s*$");

        return match.Success
            ? Regex.Unescape(match.Groups["value"].Value)
            : null;
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
            _ => throw new InvalidOperationException($"Tool {toolId} does not support cc-switch session snapshots.")
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
        return $"当前会话固定的 {toolName} Provider 快照已缺失或不可用。请同步到当前 cc-switch Provider 后再继续。";
    }

    /// <summary>
    /// 鑾峰彇鎸囧畾宸ュ叿鐨勭幆澧冨彉閲忛厤缃?
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
                    _logger.LogWarning("cc-switch 托管工具 {ToolId} 当前未就绪，忽略 WebCode 本地环境变量。Reason={Reason}", normalizedToolId, status.StatusMessage);
                }
                else
                {
                    _logger.LogInformation("cc-switch managed tool {ToolId} is driven by a session snapshot or live config; WebCode will not inject local environment variables.", normalizedToolId);
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

            // 榛樿浣跨敤鏁版嵁搴撻厤缃?
            _logger.LogInformation("Loaded environment variables for tool {ToolId}; count={Count}", toolId, dbEnvVars.Count);
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
            _logger.LogError(ex, "Failed to load environment variables for tool {ToolId}.", toolId);

            // 闄嶇骇鍒癮ppsettings閰嶇疆
            var tool = GetTool(toolId, username);
            return tool?.EnvironmentVariables ?? new Dictionary<string, string>();
        }
    }

    private void RewriteClaudeLaunchToNode(ProcessStartInfo startInfo, CliToolConfig tool, string commandPath, string originalArguments)
    {
        var isWindows = OperatingSystem.IsWindows();
        var isClaudeTool = IsClaudeTool(tool, commandPath);
        _logger.LogInformation("Claude 鍚姩閲嶅啓妫€鏌? IsWindows={IsWindows}, IsClaudeTool={IsClaudeTool}, CommandPath={CommandPath}", isWindows, isClaudeTool, commandPath);

        if (!isWindows || !isClaudeTool)
        {
            return;
        }

        var extension = Path.GetExtension(commandPath);
        if (!string.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Claude 鍚姩閲嶅啓璺宠繃锛氬懡浠や笉鏄?.cmd锛孍xtension={Extension}", extension);
            return;
        }

        var commandDirectory = Path.GetDirectoryName(commandPath);
        if (string.IsNullOrWhiteSpace(commandDirectory))
        {
            _logger.LogWarning("Claude 鍚姩閲嶅啓璺宠繃锛氭棤娉曡幏鍙栧懡浠ょ洰褰曪紝CommandPath={CommandPath}", commandPath);
            return;
        }

        var cliJsPath = Path.Combine(commandDirectory, "node_modules", "@anthropic-ai", "claude-code", "cli.js");
        _logger.LogInformation("Claude 鍚姩閲嶅啓妫€鏌?cli.js 璺緞: {CliJsPath}, Exists={Exists}", cliJsPath, File.Exists(cliJsPath));
        if (!File.Exists(cliJsPath))
        {
            return;
        }

        var preferredNodePath = GetPreferredNodeExecutablePath();
        _logger.LogInformation("Claude 鍚姩閲嶅啓妫€鏌?Node.js 璺緞: {NodePath}", preferredNodePath ?? "<null>");
        if (string.IsNullOrWhiteSpace(preferredNodePath) || !File.Exists(preferredNodePath))
        {
            _logger.LogWarning("Claude 鍚姩閲嶅啓璺宠繃锛氭湭鎵惧埌鍙敤鐨?Node.js");
            return;
        }

        startInfo.FileName = preferredNodePath;
        startInfo.Arguments = $"\"{cliJsPath}\" {originalArguments}";
        _logger.LogInformation("Claude Code 鏀逛负鐩存帴浣跨敤 Node.js 鍚姩: {NodePath}", preferredNodePath);
    }

    private void RewriteCodexLaunchToNode(ProcessStartInfo startInfo, CliToolConfig tool, string commandPath, string originalArguments)
    {
        if (!IsCodexTool(tool, commandPath))
        {
            return;
        }

        CodexLaunchCommandHelper.TryRewriteWindowsCodexCmdToNode(
            startInfo,
            commandPath,
            originalArguments,
            GetPreferredNodeExecutablePath,
            _logger);
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

        _logger.LogInformation("Claude Code 浼樺厛浣跨敤 Node.js: {NodePath}", preferredNodePath);
    }

    private string? GetPreferredNodeExecutablePath()
    {
        lock (_preferredNodeExecutableLock)
        {
            if (!string.IsNullOrWhiteSpace(_preferredNodeExecutablePath) && File.Exists(_preferredNodeExecutablePath))
            {
                return _preferredNodeExecutablePath;
            }
        }

        var resolved = CodexLaunchCommandHelper.ResolvePreferredNodeExecutablePath(_logger);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            return null;
        }

        lock (_preferredNodeExecutableLock)
        {
            _preferredNodeExecutablePath ??= resolved;
            return _preferredNodeExecutablePath;
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
    /// 淇濆瓨鎸囧畾宸ュ叿鐨勭幆澧冨彉閲忛厤缃埌鏁版嵁搴?
    /// </summary>
    public async Task<bool> SaveToolEnvironmentVariablesAsync(string toolId, Dictionary<string, string> envVars, string? username = null)
    {
        var normalizedToolId = NormalizeManagedToolId(toolId);
        if (_ccSwitchService.IsManagedTool(normalizedToolId))
        {
            _logger.LogWarning("工具 {ToolId} 由 cc-switch 管理，拒绝注入 WebCode 本地环境变量", normalizedToolId);
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
            _logger.LogError(ex, "Failed to save environment variables for tool {ToolId}.", toolId);
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
                ? $"CLI tool {status.ToolName} depends on cc-switch, but the current provider is not ready."
                : status.StatusMessage;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "璇诲彇 cc-switch 宸ュ叿鐘舵€佸け璐? {ToolId}", normalizedToolId);
            return $"CLI tool {normalizedToolId} depends on cc-switch, but the current status could not be read.";
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
                        : "Unable to read cc-switch status.";
                    _logger.LogInformation("过滤未就绪的 cc-switch 托管工具: {ToolId}, Reason={Reason}", normalizedToolId, message);
                    return false;
                })
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "鎵归噺璇诲彇 cc-switch 鐘舵€佸け璐ワ紝鎵€鏈夊彈绠″伐鍏峰皢琚涓轰笉鍙敤");
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
            _logger.LogDebug(ex, "妫€娴?Codex /goal 鑳藉姏澶辫触锛屽綋鍓嶄細璇濆皢涓嶆敞鍏?goals feature");
            return false;
        }
    }

    /// <summary>
    /// 鑾峰彇浼氳瘽宸ヤ綔鍖虹殑鏂囦欢鍐呭
    /// </summary>
    public byte[]? GetWorkspaceFile(string sessionId, string relativePath)
    {
        try
        {
            var workspacePath = GetSessionWorkspacePath(sessionId);
            var fullPath = Path.Combine(workspacePath, relativePath);

            // 瀹夊叏妫€鏌ワ細纭繚鏂囦欢鍦ㄥ伐浣滃尯鍐?
            var normalizedWorkspace = Path.GetFullPath(workspacePath);
            var normalizedFile = Path.GetFullPath(fullPath);

            if (!IsPathWithinDirectory(normalizedWorkspace, normalizedFile))
            {
                _logger.LogWarning("灏濊瘯璁块棶宸ヤ綔鍖哄鐨勬枃浠? {File}", relativePath);
                return null;
            }

            if (!File.Exists(fullPath))
            {
                _logger.LogWarning("鏂囦欢涓嶅瓨鍦? {File}", relativePath);
                return null;
            }

            return File.ReadAllBytes(fullPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "璇诲彇宸ヤ綔鍖烘枃浠跺け璐? {SessionId}/{File}", sessionId, relativePath);
            return null;
        }
    }

    /// <summary>
    /// 鑾峰彇浼氳瘽宸ヤ綔鍖虹殑鎵€鏈夋枃浠讹紙鎵撳寘涓篫IP锛?
    /// </summary>
    public byte[]? GetWorkspaceZip(string sessionId)
    {
        try
        {
            var workspacePath = GetSessionWorkspacePath(sessionId);

            if (!Directory.Exists(workspacePath))
            {
                _logger.LogWarning("宸ヤ綔鍖轰笉瀛樺湪: {SessionId}", sessionId);
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
            _logger.LogError(ex, "鎵撳寘宸ヤ綔鍖哄け璐? {SessionId}", sessionId);
            return null;
        }
    }

    /// <summary>
    /// 涓婁紶鏂囦欢鍒颁細璇濆伐浣滃尯
    /// </summary>
    public async Task<bool> UploadFileToWorkspaceAsync(string sessionId, string fileName, byte[] fileContent, string? relativePath = null)
    {
        try
        {
            var workspacePath = GetSessionWorkspacePath(sessionId);

            // 纭繚宸ヤ綔鍖哄瓨鍦?
            if (!Directory.Exists(workspacePath))
            {
                Directory.CreateDirectory(workspacePath);
                _logger.LogInformation("鍒涘缓浼氳瘽宸ヤ綔鍖? {SessionId}", sessionId);
            }

            // 鏋勫缓鐩爣璺緞
            string targetPath;
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                // 鐩存帴鏀惧湪宸ヤ綔鍖烘牴鐩綍
                targetPath = Path.Combine(workspacePath, fileName);
            }
            else
            {
                // 鏀惧湪鎸囧畾鐨勫瓙鐩綍
                var targetDir = Path.Combine(workspacePath, relativePath);
                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }
                targetPath = Path.Combine(targetDir, fileName);
            }

            // 瀹夊叏妫€鏌ワ細纭繚鏂囦欢鍦ㄥ伐浣滃尯鍐?
            var normalizedWorkspace = Path.GetFullPath(workspacePath);
            var normalizedTarget = Path.GetFullPath(targetPath);

            if (!IsPathWithinDirectory(normalizedWorkspace, normalizedTarget))
            {
                _logger.LogWarning("灏濊瘯涓婁紶鏂囦欢鍒板伐浣滃尯澶? {File}", targetPath);
                return false;
            }

            // 鍐欏叆鏂囦欢
            await File.WriteAllBytesAsync(targetPath, fileContent);
            _logger.LogInformation("鏂囦欢涓婁紶鎴愬姛: {SessionId}/{File}, 澶у皬: {Size} bytes", sessionId, Path.GetRelativePath(workspacePath, targetPath), fileContent.Length);
            
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "涓婁紶鏂囦欢鍒板伐浣滃尯澶辫触: {SessionId}/{File}", sessionId, fileName);
            return false;
        }
    }

    public async Task<bool> CreateFolderInWorkspaceAsync(string sessionId, string folderPath)
    {
        try
        {
            var workspacePath = GetSessionWorkspacePath(sessionId);

            // 纭繚宸ヤ綔鍖哄瓨鍦?
            if (!Directory.Exists(workspacePath))
            {
                Directory.CreateDirectory(workspacePath);
                _logger.LogInformation("鍒涘缓浼氳瘽宸ヤ綔鍖? {SessionId}", sessionId);
            }

            // 绉婚櫎鍓嶅鍜屽熬闅忔枩鏉?
            folderPath = folderPath.Trim('/', '\\');

            if (string.IsNullOrWhiteSpace(folderPath))
            {
                _logger.LogWarning("Folder path is empty.");
                return false;
            }

            // 鏋勫缓鐩爣璺緞
            var targetPath = Path.Combine(workspacePath, folderPath);

            // 瀹夊叏妫€鏌ワ細纭繚鏂囦欢澶瑰湪宸ヤ綔鍖哄唴
            var normalizedWorkspace = Path.GetFullPath(workspacePath);
            var normalizedTarget = Path.GetFullPath(targetPath);

            if (!IsPathWithinDirectory(normalizedWorkspace, normalizedTarget))
            {
                _logger.LogWarning("灏濊瘯鍦ㄥ伐浣滃尯澶栧垱寤烘枃浠跺す: {Folder}", targetPath);
                return false;
            }

            // 妫€鏌ユ枃浠跺す鏄惁宸插瓨鍦?
            if (Directory.Exists(targetPath))
            {
                _logger.LogInformation("鏂囦欢澶瑰凡瀛樺湪: {SessionId}/{Folder}", sessionId, folderPath);
                return true;
            }

            // 鍒涘缓鏂囦欢澶?
            Directory.CreateDirectory(targetPath);
            _logger.LogInformation("鏂囦欢澶瑰垱寤烘垚鍔? {SessionId}/{Folder}", sessionId, folderPath);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "鍒涘缓鏂囦欢澶瑰け璐? {SessionId}/{Folder}", sessionId, folderPath);
            return false;
        }
    }

    /// <summary>
    /// 鍒犻櫎浼氳瘽宸ヤ綔鍖轰腑鐨勬枃浠舵垨鏂囦欢澶?
    /// </summary>
    public async Task<bool> DeleteWorkspaceItemAsync(string sessionId, string relativePath, bool isDirectory)
    {
        try
        {
            var workspacePath = GetSessionWorkspacePath(sessionId);

            if (!Directory.Exists(workspacePath))
            {
                _logger.LogWarning("宸ヤ綔鍖轰笉瀛樺湪: {SessionId}", sessionId);
                return false;
            }

            // 绉婚櫎鍓嶅鍜屽熬闅忔枩鏉?
            relativePath = relativePath.Trim('/', '\\');

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                _logger.LogWarning("璺緞涓虹┖");
                return false;
            }

            // 鏋勫缓鐩爣璺緞
            var targetPath = Path.Combine(workspacePath, relativePath);

            // 瀹夊叏妫€鏌ワ細纭繚璺緞鍦ㄥ伐浣滃尯鍐?
            var normalizedWorkspace = Path.GetFullPath(workspacePath);
            var normalizedTarget = Path.GetFullPath(targetPath);

            if (!IsPathWithinDirectory(normalizedWorkspace, normalizedTarget))
            {
                _logger.LogWarning("灏濊瘯鍒犻櫎宸ヤ綔鍖哄鐨勯」: {Path}", targetPath);
                return false;
            }

            // 鍒犻櫎鏂囦欢鎴栨枃浠跺す
            if (isDirectory)
            {
                if (!Directory.Exists(targetPath))
                {
                    _logger.LogWarning("鏂囦欢澶逛笉瀛樺湪: {SessionId}/{Path}", sessionId, relativePath);
                    return false;
                }

                Directory.Delete(targetPath, recursive: true);
                _logger.LogInformation("鏂囦欢澶瑰垹闄ゆ垚鍔? {SessionId}/{Path}", sessionId, relativePath);
            }
            else
            {
                if (!File.Exists(targetPath))
                {
                    _logger.LogWarning("鏂囦欢涓嶅瓨鍦? {SessionId}/{Path}", sessionId, relativePath);
                    return false;
                }

                File.Delete(targetPath);
                _logger.LogInformation("鏂囦欢鍒犻櫎鎴愬姛: {SessionId}/{Path}", sessionId, relativePath);
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "鍒犻櫎澶辫触: {SessionId}/{Path}, IsDirectory: {IsDirectory}", sessionId, relativePath, isDirectory);
            return false;
        }
    }

    /// <summary>
    /// 绉诲姩浼氳瘽宸ヤ綔鍖轰腑鐨勬枃浠舵垨鏂囦欢澶?
    /// </summary>
    public async Task<bool> MoveFileInWorkspaceAsync(string sessionId, string sourcePath, string targetPath)
    {
        try
        {
            var workspacePath = GetSessionWorkspacePath(sessionId);

            if (!Directory.Exists(workspacePath))
            {
                _logger.LogWarning("宸ヤ綔鍖轰笉瀛樺湪: {SessionId}", sessionId);
                return false;
            }

            // 绉婚櫎鍓嶅鍜屽熬闅忔枩鏉?
            sourcePath = sourcePath.Trim('/', '\\');
            targetPath = targetPath.Trim('/', '\\');

            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetPath))
            {
                _logger.LogWarning("婧愯矾寰勬垨鐩爣璺緞涓虹┖");
                return false;
            }

            // 鏋勫缓瀹屾暣璺緞
            var fullSourcePath = Path.Combine(workspacePath, sourcePath);
            var fullTargetPath = Path.Combine(workspacePath, targetPath);

            // 瀹夊叏妫€鏌ワ細纭繚璺緞鍦ㄥ伐浣滃尯鍐?
            var normalizedWorkspace = Path.GetFullPath(workspacePath);
            var normalizedSource = Path.GetFullPath(fullSourcePath);
            var normalizedTarget = Path.GetFullPath(fullTargetPath);

            if (!IsPathWithinDirectory(normalizedWorkspace, normalizedSource) ||
                !IsPathWithinDirectory(normalizedWorkspace, normalizedTarget))
            {
                _logger.LogWarning("灏濊瘯绉诲姩宸ヤ綔鍖哄鐨勯」");
                return false;
            }

            // 妫€鏌ユ簮鏄惁瀛樺湪
            bool isDirectory = Directory.Exists(fullSourcePath);
            bool isFile = File.Exists(fullSourcePath);

            if (!isDirectory && !isFile)
            {
                _logger.LogWarning("婧愪笉瀛樺湪: {SessionId}/{Source}", sessionId, sourcePath);
                return false;
            }

            // 纭繚鐩爣鐩綍瀛樺湪
            var targetDirectory = Path.GetDirectoryName(fullTargetPath);
            if (!string.IsNullOrEmpty(targetDirectory) && !Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            // 绉诲姩鏂囦欢鎴栨枃浠跺す
            if (isDirectory)
            {
                Directory.Move(fullSourcePath, fullTargetPath);
            }
            else
            {
                File.Move(fullSourcePath, fullTargetPath, overwrite: true);
            }

            _logger.LogInformation("绉诲姩鎴愬姛: {SessionId}/{Source} -> {Target}", sessionId, sourcePath, targetPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "绉诲姩澶辫触: {SessionId}/{Source} -> {Target}", sessionId, sourcePath, targetPath);
            return false;
        }
    }

    /// <summary>
    /// 澶嶅埗浼氳瘽宸ヤ綔鍖轰腑鐨勬枃浠舵垨鏂囦欢澶?
    /// </summary>
    public async Task<bool> CopyFileInWorkspaceAsync(string sessionId, string sourcePath, string targetPath)
    {
        try
        {
            var workspacePath = GetSessionWorkspacePath(sessionId);

            if (!Directory.Exists(workspacePath))
            {
                _logger.LogWarning("宸ヤ綔鍖轰笉瀛樺湪: {SessionId}", sessionId);
                return false;
            }

            // 绉婚櫎鍓嶅鍜屽熬闅忔枩鏉?
            sourcePath = sourcePath.Trim('/', '\\');
            targetPath = targetPath.Trim('/', '\\');

            if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(targetPath))
            {
                _logger.LogWarning("婧愯矾寰勬垨鐩爣璺緞涓虹┖");
                return false;
            }

            // 鏋勫缓瀹屾暣璺緞
            var fullSourcePath = Path.Combine(workspacePath, sourcePath);
            var fullTargetPath = Path.Combine(workspacePath, targetPath);

            // 瀹夊叏妫€鏌ワ細纭繚璺緞鍦ㄥ伐浣滃尯鍐?
            var normalizedWorkspace = Path.GetFullPath(workspacePath);
            var normalizedSource = Path.GetFullPath(fullSourcePath);
            var normalizedTarget = Path.GetFullPath(fullTargetPath);

            if (!IsPathWithinDirectory(normalizedWorkspace, normalizedSource) ||
                !IsPathWithinDirectory(normalizedWorkspace, normalizedTarget))
            {
                _logger.LogWarning("灏濊瘯澶嶅埗宸ヤ綔鍖哄鐨勯」");
                return false;
            }

            // 妫€鏌ユ簮鏄惁瀛樺湪
            bool isDirectory = Directory.Exists(fullSourcePath);
            bool isFile = File.Exists(fullSourcePath);

            if (!isDirectory && !isFile)
            {
                _logger.LogWarning("婧愪笉瀛樺湪: {SessionId}/{Source}", sessionId, sourcePath);
                return false;
            }

            // 纭繚鐩爣鐩綍瀛樺湪
            var targetDirectory = Path.GetDirectoryName(fullTargetPath);
            if (!string.IsNullOrEmpty(targetDirectory) && !Directory.Exists(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            // 澶嶅埗鏂囦欢鎴栨枃浠跺す
            if (isDirectory)
            {
                CopyDirectory(fullSourcePath, fullTargetPath);
            }
            else
            {
                File.Copy(fullSourcePath, fullTargetPath, overwrite: true);
            }

            _logger.LogInformation("澶嶅埗鎴愬姛: {SessionId}/{Source} -> {Target}", sessionId, sourcePath, targetPath);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "澶嶅埗澶辫触: {SessionId}/{Source} -> {Target}", sessionId, sourcePath, targetPath);
            return false;
        }
    }

    /// <summary>
    /// 閲嶅懡鍚嶄細璇濆伐浣滃尯涓殑鏂囦欢鎴栨枃浠跺す
    /// </summary>
    public async Task<bool> RenameFileInWorkspaceAsync(string sessionId, string oldPath, string newName)
    {
        try
        {
            var workspacePath = GetSessionWorkspacePath(sessionId);

            if (!Directory.Exists(workspacePath))
            {
                _logger.LogWarning("宸ヤ綔鍖轰笉瀛樺湪: {SessionId}", sessionId);
                return false;
            }

            // 绉婚櫎鍓嶅鍜屽熬闅忔枩鏉?
            oldPath = oldPath.Trim('/', '\\');
            newName = newName.Trim('/', '\\');

            if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newName))
            {
                _logger.LogWarning("Old path or new name is empty.");
                return false;
            }

            // 妫€鏌ユ柊鍚嶇О鏄惁鍖呭惈璺緞鍒嗛殧绗︼紙搴旇鍙槸鏂囦欢鍚嶏級
            if (newName.Contains('/') || newName.Contains('\\'))
            {
                _logger.LogWarning("鏂板悕绉颁笉搴斿寘鍚矾寰勫垎闅旂: {NewName}", newName);
                return false;
            }

            // 鏋勫缓瀹屾暣璺緞
            var fullOldPath = Path.Combine(workspacePath, oldPath);
            var directory = Path.GetDirectoryName(fullOldPath);
            var fullNewPath = directory != null ? Path.Combine(directory, newName) : Path.Combine(workspacePath, newName);

            // 瀹夊叏妫€鏌ワ細纭繚璺緞鍦ㄥ伐浣滃尯鍐?
            var normalizedWorkspace = Path.GetFullPath(workspacePath);
            var normalizedOld = Path.GetFullPath(fullOldPath);
            var normalizedNew = Path.GetFullPath(fullNewPath);

            if (!IsPathWithinDirectory(normalizedWorkspace, normalizedOld) ||
                !IsPathWithinDirectory(normalizedWorkspace, normalizedNew))
            {
                _logger.LogWarning("Attempted to rename an item outside the workspace.");
                return false;
            }

            // 妫€鏌ユ簮鏄惁瀛樺湪
            bool isDirectory = Directory.Exists(fullOldPath);
            bool isFile = File.Exists(fullOldPath);

            if (!isDirectory && !isFile)
            {
                _logger.LogWarning("婧愪笉瀛樺湪: {SessionId}/{OldPath}", sessionId, oldPath);
                return false;
            }

            // 閲嶅懡鍚嶆枃浠舵垨鏂囦欢澶?
            if (isDirectory)
            {
                Directory.Move(fullOldPath, fullNewPath);
            }
            else
            {
                File.Move(fullOldPath, fullNewPath, overwrite: true);
            }

            _logger.LogInformation("閲嶅懡鍚嶆垚鍔? {SessionId}/{OldPath} -> {NewName}", sessionId, oldPath, newName);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "閲嶅懡鍚嶅け璐? {SessionId}/{OldPath} -> {NewName}", sessionId, oldPath, newName);
            return false;
        }
    }

    /// <summary>
    /// 鎵归噺鍒犻櫎浼氳瘽宸ヤ綔鍖轰腑鐨勬枃浠?
    /// </summary>
    public async Task<int> BatchDeleteFilesAsync(string sessionId, List<string> relativePaths)
    {
        int successCount = 0;

        try
        {
            var workspacePath = GetSessionWorkspacePath(sessionId);

            if (!Directory.Exists(workspacePath))
            {
                _logger.LogWarning("宸ヤ綔鍖轰笉瀛樺湪: {SessionId}", sessionId);
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

                    // 瀹夊叏妫€鏌ワ細纭繚璺緞鍦ㄥ伐浣滃尯鍐?
                    var normalizedWorkspace = Path.GetFullPath(workspacePath);
                    var normalizedPath = Path.GetFullPath(fullPath);

                    if (!IsPathWithinDirectory(normalizedWorkspace, normalizedPath))
                    {
                        _logger.LogWarning("灏濊瘯鍒犻櫎宸ヤ綔鍖哄鐨勯」: {Path}", fullPath);
                        continue;
                    }

                    // 鍒ゆ柇鏄枃浠惰繕鏄枃浠跺す
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
                    _logger.LogWarning(ex, "鎵归噺鍒犻櫎鍗曚釜鏂囦欢澶辫触: {SessionId}/{Path}", sessionId, relativePath);
                }
            }

            _logger.LogInformation("鎵归噺鍒犻櫎瀹屾垚: {SessionId}, 鎴愬姛 {Count}/{Total}", sessionId, successCount, relativePaths.Count);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "鎵归噺鍒犻櫎澶辫触: {SessionId}", sessionId);
        }

        return successCount;
    }

    private string SyncGlobalCodexSkillsToWorkspace(string sessionWorkspace, string sessionId)
    {
        var sourceSkillsDirectory = GetCodexGlobalSkillsDirectory();
        if (!Directory.Exists(sourceSkillsDirectory))
        {
            return string.Empty;
        }

        var targetSkillsDirectory = ResolveWorkspaceCodexSkillsDirectory(sessionWorkspace);
        try
        {
            CopyDirectory(sourceSkillsDirectory, targetSkillsDirectory);
            _logger.LogInformation(
                "已将全局 Codex skills 同步到会话工作区: {SessionId}, Source={SourceSkillsDirectory}, Target={TargetSkillsDirectory}",
                sessionId,
                sourceSkillsDirectory,
                targetSkillsDirectory);
            return "已将全局 Codex skills 同步到工作区";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "同步全局 Codex skills 到会话工作区失败: {SessionId}, Source={SourceSkillsDirectory}, Target={TargetSkillsDirectory}",
                sessionId,
                sourceSkillsDirectory,
                targetSkillsDirectory);
            return $"全局 Codex skills 同步失败: {ex.Message}";
        }
    }

    private string GetCodexGlobalSkillsDirectory()
    {
        return Path.Combine(_userProfileResolver(), ".codex", "skills");
    }

    private static string ResolveWorkspaceCodexSkillsDirectory(string workspacePath)
    {
        var trimmedWorkspace = workspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(Path.GetFileName(trimmedWorkspace), ".codex", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(trimmedWorkspace, "skills")
            : Path.Combine(trimmedWorkspace, ".codex", "skills");
    }

    /// <summary>
    /// 閫掑綊澶嶅埗鐩綍
    /// </summary>
    private void CopyDirectory(string sourceDir, string targetDir)
    {
        // 鍒涘缓鐩爣鐩綍
        Directory.CreateDirectory(targetDir);

        // 澶嶅埗鎵€鏈夋枃浠?
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var fileName = Path.GetFileName(file);
            var targetFile = Path.Combine(targetDir, fileName);
            File.Copy(file, targetFile, overwrite: true);
        }

        // 閫掑綊澶嶅埗鎵€鏈夊瓙鐩綍
        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(subDir);
            var targetSubDir = Path.Combine(targetDir, dirName);
            CopyDirectory(subDir, targetSubDir);
        }
    }
}







