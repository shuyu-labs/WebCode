using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Markdig;
using System.Linq;
using System.Text;
using System.Text.Json;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service;
using WebCodeCli.Domain.Domain.Service.Adapters;
using WebCodeCli.Components;

namespace WebCodeCli.Pages;

// 工作区文件节点
public class WorkspaceFileNode
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty; // "file" or "folder"
    public long Size { get; set; }
    public string Extension { get; set; } = string.Empty;
    public bool IsHtml { get; set; }
    public List<WorkspaceFileNode>? Children { get; set; }
}

public partial class CodeAssistant : ComponentBase, IAsyncDisposable
{
    [Inject] private ICliExecutorService CliExecutorService { get; set; } = default!;
    [Inject] private IChatSessionService ChatSessionService { get; set; } = default!;
    [Inject] private ICliToolEnvironmentService CliToolEnvironmentService { get; set; } = default!;
    [Inject] private IAuthenticationService AuthenticationService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ISessionHistoryManager SessionHistoryManager { get; set; } = default!;
    [Inject] private IContextManagerService ContextManagerService { get; set; } = default!;
    [Inject] private IFrontendProjectDetector FrontendProjectDetector { get; set; } = default!;
    [Inject] private IDevServerManager DevServerManager { get; set; } = default!;
    [Inject] private WebCodeCli.Domain.Domain.Service.ISkillService SkillService { get; set; } = default!;
    [Inject] private ILocalizationService L { get; set; } = default!;
    [Inject] private ISystemSettingsService SystemSettingsService { get; set; } = default!;
    
    // 本地化翻译缓存
    private Dictionary<string, string> _translations = new();
    private string _currentLanguage = "zh-CN";
    
    private List<CliToolConfig> _availableTools = new();
    private List<CliToolConfig> _allTools = new();
    private List<string> _enabledAssistants = new();
    private string _selectedToolId = string.Empty;
    private List<ChatMessage> _messages = new();
    private string _inputMessage = string.Empty;
    private bool _isLoading = false;
    private string _currentAssistantMessage = string.Empty;
    private string _sessionId = Guid.NewGuid().ToString();

    private string _rawOutput = string.Empty;
    private bool _disposed = false;
    private readonly List<JsonlDisplayItem> _jsonlEvents = new();
    private bool _isJsonlOutputActive = false;
    private string _jsonlPendingBuffer = string.Empty;
    private string _activeThreadId = string.Empty;
    private StringBuilder? _jsonlAssistantMessageBuilder;

    // 输出结果（Tab=输出结果）持久化
    private System.Threading.Timer? _outputStateSaveTimer;
    private readonly object _outputStateSaveLock = new object();
    private bool _hasPendingOutputStateSave = false;
    private const int OutputStateSaveDebounceMs = 800;

    // Markdown 渲染（输出结果区域）
    private static readonly MarkdownPipeline _outputMarkdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    // JSONL 事件分组折叠状态（用于“命令执行/工具调用”气泡折叠）
    private readonly Dictionary<string, bool> _jsonlGroupOpenState = new();

    // JSONL 事件懒加载
    private const int InitialDisplayCount = 20; // 初始显示20条
    private const int LoadMoreCount = 10; // 每次加载10条
    private int _displayedEventCount = InitialDisplayCount;
    private bool _hasMoreEvents => _jsonlEvents.Count > _displayedEventCount;

    // 输出结果区“向上加载更多”时的滚动位置保持
    private bool _pendingOutputPrependScrollAdjust = false;
    private ScrollInfo _outputScrollBeforePrepend = new();

    private sealed class ScrollInfo
    {
        public double ScrollTop { get; set; }
        public double ScrollHeight { get; set; }
        public double ClientHeight { get; set; }
    }

    // Markdown 渲染缓存
    private readonly Dictionary<string, MarkupString> _markdownCache = new();

    private MarkupString RenderMarkdown(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return new MarkupString(string.Empty);
        }

        // 使用缓存避免重复渲染
        if (_markdownCache.TryGetValue(markdown, out var cached))
        {
            return cached;
        }

        var html = Markdown.ToHtml(markdown, _outputMarkdownPipeline);
        var result = new MarkupString(html);
        
        // 限制缓存大小，避免内存占用过大
        if (_markdownCache.Count > 100)
        {
            _markdownCache.Clear();
        }
        _markdownCache[markdown] = result;
        return result;
    }

    private List<WorkspaceFileNode> _workspaceFiles = new();
    private HashSet<string> _expandedFolders = new(); // 展开的文件夹路径
    private string _selectedHtmlFile = string.Empty;
    private string _htmlPreviewUrl = string.Empty; // 改用URL代替内容
    private System.Threading.Timer? _workspaceRefreshTimer;
    private string _activeTabKey = "1"; // 当前激活的Tab
    private string _workspaceSubTab = "files"; // 工作区子标签页
    
    // 文件树性能优化
    private readonly Dictionary<string, List<WorkspaceFileNode>> _lazyLoadedChildren = new(); // 懒加载的子节点缓存
    private const int MaxVisibleNodes = 100; // 初始最大显示节点数
    private int _currentVisibleNodes = MaxVisibleNodes;
    private bool _hasMoreNodes = false;
    
    // Diff 对比参数
    private string _diffFilePath = string.Empty;
    private string _diffFromCommit = string.Empty;
    private string _diffToCommit = "HEAD";
    
    // 用于防抖的变量
    private System.Threading.Timer? _updateTimer;
    private readonly object _updateLock = new object();
    private bool _hasPendingUpdate = false;
    
    // 代码预览模态框
    private CodePreviewModal _codePreviewModal = default!;
    
    // 上下文预览面板
    private ContextPreviewPanel _contextPreviewPanel = default!;
    private bool _showContextPanel = false;

    // 环境变量配置模态框
    private EnvironmentVariableConfigModal _envConfigModal = default!;
    
    // 会话分享模态框
    private ShareSessionModal _shareSessionModal = default!;
    
    // 文件上传
    private bool _isUploading = false;
    private const long MaxFileSize = 100 * 1024 * 1024; // 100MB
    private string _selectedUploadFolder = string.Empty; // 选中的上传文件夹
    private List<string> _availableFolders = new(); // 可用的文件夹列表
    
    // 创建文件夹
    private bool _showCreateFolderDialog = false;
    private string _newFolderName = string.Empty;
    private bool _isCreatingFolder = false;
    private string _createFolderError = string.Empty;
    
    // 用户信息
    private bool _showUserInfo = false;
    private string _currentUsername = string.Empty;
    private bool _showUserDropdown = false; // 用户头像下拉菜单
    
    // 键盘事件状态
    private bool _isKeyDownEnterWithoutShift = false;
    
    // 移动端预览区折叠状态
    private bool _isPreviewCollapsed = false;
    
    // 删除确认对话框（工作区文件）
    private bool _showDeleteConfirmDialog = false;
    private WorkspaceFileNode? _nodeToDelete = null;
    private bool _isDeleting = false;
    private string _deleteError = string.Empty;
    
    // 删除确认对话框（会话）
    private bool _showSessionDeleteDialog = false;
    private SessionHistory? _sessionToDelete = null;
    private bool _isDeletingSession = false;
    private string _sessionDeleteError = string.Empty;
    
    // 重命名对话框（会话）
    private bool _showRenameDialog = false;
    private SessionHistory? _sessionToRename = null;
    private string _newSessionTitle = string.Empty;
    private bool _isRenamingSession = false;
    private string _renameError = string.Empty;
    
    // 会话历史管理
    private List<SessionHistory> _sessions = new();
    private SessionHistory? _currentSession = null;
    private bool _showSessionList = false;
    private bool _isLoadingSessions = false;
    private bool _isLoadingSession = false;
    
    // 会话多选批量删除
    private bool _isSessionMultiSelectMode = false;
    private HashSet<string> _selectedSessionIds = new();
    private bool _showBatchSessionDeleteDialog = false;
    private bool _isDeletingBatchSessions = false;
    private string _batchSessionDeleteError = string.Empty;
    
    // 批量操作
    private HashSet<string> _selectedFiles = new();
    
    // 技能选择
    private List<WebCodeCli.Domain.Domain.Model.SkillItem> _skills = new();
    private bool _showSkillPicker = false;
    private string _selectedSkillName = string.Empty;
    private string _skillFilter = string.Empty;
    private bool _showBatchOperationDialog = false;
    private string _batchOperation = ""; // "move", "copy", "delete"
    private string _batchTargetFolder = "";
    private bool _isBatchOperating = false;
    private string _batchOperationError = string.Empty;
    
    // 文件树虚拟滚动和懒加载
    private ElementReference _fileTreeScrollContainer;

    // 新增组件引用
    private ProgressTracker _progressTracker = default!;
    private QuickActionsPanel _quickActionsPanel = default!;
    private TemplateLibraryModal _templateLibraryModal = default!;
    private AutoCompleteDropdown _autoCompleteDropdown = default!;
    
    // 自动补全相关
    private System.Threading.Timer? _autoCompleteDebounceTimer;
    private const int AutoCompleteDebounceMs = 300;
    
    // 模板变量输入
    private bool _showVariableInputDialog = false;
    private PromptTemplate? _templateWithVariables = null;
    private Dictionary<string, string> _variableValues = new();

    // 前端项目预览相关
    private List<FrontendProjectInfo> _detectedFrontendProjects = new();
    private string _selectedPreviewMode = "static"; // static/dev/build
    private string _selectedFrontendProject = "";
    private DevServerInfo? _currentDevServer = null;
    private bool _isServerStarting = false;
    private string _previewRootPath = ""; // 预览根目录（相对于工作区的路径）
    private List<string> _availablePreviewRoots = new(); // 可用的预览根目录列表
    private bool _showPreviewRootSelector = false; // 是否显示预览根目录选择器
    private DependencyInstallProgress _installProgressModal = default!;
    private bool _showInstallProgress = false;
    private string _installStatusMessage = "";
    private List<string> _installLogs = new();

    protected override async Task OnInitializedAsync()
    {
        // 初始化本地化
        try
        {
            _currentLanguage = await L.GetCurrentLanguageAsync();
            await LoadTranslationsAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"初始化本地化失败: {ex.Message}");
        }
        
        // 检查认证状态
        if (AuthenticationService.IsAuthenticationEnabled())
        {
            try
            {
                var isAuthenticated = await JSRuntime.InvokeAsync<string>("sessionStorage.getItem", "isAuthenticated");
                if (isAuthenticated != "true")
                {
                    NavigationManager.NavigateTo("/login");
                    return;
                }
                
                // 获取当前用户名
                _currentUsername = await JSRuntime.InvokeAsync<string>("sessionStorage.getItem", "username") ?? "用户";
                _showUserInfo = true;
            }
            catch
            {
                NavigationManager.NavigateTo("/login");
                return;
            }
        }

        await LoadAvailableTools();
        
        // 加载技能列表
        await LoadSkillsAsync();
        
        // 加载会话列表并执行自动清理
        await LoadSessionsAsync();

        // 如果存在历史会话，优先恢复最近一次；否则创建全新会话
        if (_sessions.Any())
        {
            var latestSessionId = _sessions.First().SessionId;
            Console.WriteLine($"[初始化] 恢复最近会话: {latestSessionId}");
            await LoadSession(latestSessionId);
        }
        else
        {
            Console.WriteLine("[初始化] 首次使用，自动创建新会话");
            await CreateNewSessionAsync();
        }
        
        // 启动工作区文件刷新定时器（每2秒刷新一次）
        _workspaceRefreshTimer = new System.Threading.Timer(async _ =>
        {
            await LoadWorkspaceFiles();
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(2));
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                // 首次渲染后确保本地化资源已加载（避免 JS 互操作时机问题）
                if (_translations.Count == 0)
                {
                    // 等待 JS 端国际化初始化完成（最多重试 3 次）
                    int retryCount = 0;
                    const int maxRetries = 3;
                    while (retryCount < maxRetries)
                    {
                        try
                        {
                            _currentLanguage = await L.GetCurrentLanguageAsync();
                            await LoadTranslationsAsync();
                            
                            if (_translations.Count > 0)
                            {
                                Console.WriteLine($"[国际化] 翻译资源加载成功，共 {_translations.Count} 条");
                                break;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine($"[国际化] 第 {retryCount + 1} 次加载失败: {ex.Message}");
                        }
                        
                        retryCount++;
                        if (retryCount < maxRetries)
                        {
                            // 等待一小段时间再重试
                            await Task.Delay(500);
                        }
                    }
                    
                    StateHasChanged();
                }

                // 设置iframe自动调整高度
                await JSRuntime.InvokeVoidAsync("setupIframeAutoResize");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"设置iframe自动调整失败: {ex.Message}");
            }
        }

        if (_pendingOutputPrependScrollAdjust)
        {
            _pendingOutputPrependScrollAdjust = false;

            try
            {
                await JSRuntime.InvokeVoidAsync(
                    "restoreScrollAfterPrepend",
                    "output-container",
                    _outputScrollBeforePrepend.ScrollHeight,
                    _outputScrollBeforePrepend.ScrollTop);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"恢复输出区滚动位置失败: {ex.Message}");
            }
        }
    }

    private async Task LoadAvailableTools()
    {
        try
        {
            // 获取所有可用的 CLI 工具
            _allTools = CliExecutorService.GetAvailableTools();
            
            // 获取启用的助手列表
            _enabledAssistants = await SystemSettingsService.GetEnabledAssistantsAsync();
            
            // 根据启用的助手过滤工具
            if (_enabledAssistants.Any())
            {
                _availableTools = _allTools.Where(tool => 
                    _enabledAssistants.Any(assistant => 
                        tool.Id.Contains(assistant.Replace("-", ""), StringComparison.OrdinalIgnoreCase) ||
                        tool.Id.Equals(assistant, StringComparison.OrdinalIgnoreCase) ||
                        (assistant == "claude-code" && tool.Id.Contains("claude", StringComparison.OrdinalIgnoreCase)) ||
                        (assistant == "codex" && tool.Id.Contains("codex", StringComparison.OrdinalIgnoreCase)) ||
                        (assistant == "opencode" && tool.Id.Contains("opencode", StringComparison.OrdinalIgnoreCase))
                    )).ToList();
            }
            else
            {
                // 如果没有配置任何助手，显示所有工具（兼容旧配置）
                _availableTools = _allTools;
            }
            
            if (_availableTools.Any())
            {
                _selectedToolId = _availableTools.First().Id;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载 CLI 工具列表失败: {ex.Message}");
            // 出错时显示所有工具
            _availableTools = _allTools;
            if (_availableTools.Any() && string.IsNullOrEmpty(_selectedToolId))
            {
                _selectedToolId = _availableTools.First().Id;
            }
        }
    }

    // Codex JSONL 解析与渲染
    private void InitializeJsonlState(bool enableJsonl)
    {
        _isJsonlOutputActive = enableJsonl;
        _jsonlPendingBuffer = string.Empty;
        _activeThreadId = string.Empty;
        _jsonlEvents.Clear();
        _jsonlAssistantMessageBuilder = enableJsonl ? new StringBuilder() : null;

        // 重置懒加载计数器
        ResetEventDisplayCount();

        QueueSaveOutputState();
    }

    /// <summary>
    /// 检查工具是否支持流式JSON解析（使用适配器工厂）
    /// </summary>
    private bool IsJsonlTool(CliToolConfig? tool)
    {
        if (tool == null)
        {
            return false;
        }

        // 使用CliExecutorService的适配器检查
        return CliExecutorService.SupportsStreamParsing(tool);
    }
    
    /// <summary>
    /// 获取当前选中工具的适配器
    /// </summary>
    private ICliToolAdapter? GetCurrentAdapter()
    {
        var tool = _availableTools.FirstOrDefault(t => t.Id == _selectedToolId);
        return tool != null ? CliExecutorService.GetAdapter(tool) : null;
    }

    private void ProcessJsonlChunk(string content, bool flush)
    {
        if (!_isJsonlOutputActive)
        {
            return;
        }

        if (!string.IsNullOrEmpty(content))
        {
            _jsonlPendingBuffer += content;
        }

        while (true)
        {
            var newlineIndex = _jsonlPendingBuffer.IndexOf('\n');
            if (newlineIndex < 0)
            {
                break;
            }

            var line = _jsonlPendingBuffer.Substring(0, newlineIndex).TrimEnd('\r');
            _jsonlPendingBuffer = _jsonlPendingBuffer[(newlineIndex + 1)..];
            HandleJsonlLine(line);
        }

        if (flush && !string.IsNullOrWhiteSpace(_jsonlPendingBuffer))
        {
            var remaining = _jsonlPendingBuffer.Trim();
            _jsonlPendingBuffer = string.Empty;
            HandleJsonlLine(remaining);
        }

        QueueSaveOutputState();
    }

    private void HandleJsonlLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        // 尝试使用适配器解析
        var adapter = GetCurrentAdapter();
        if (adapter != null)
        {
            HandleJsonlLineWithAdapter(line, adapter);
            return;
        }

        // 回退到原有的硬编码解析逻辑（向后兼容）
        HandleJsonlLineLegacy(line);

        QueueSaveOutputState();
    }

    /// <summary>
    /// 使用适配器处理JSONL行
    /// </summary>
    private void HandleJsonlLineWithAdapter(string line, ICliToolAdapter adapter)
    {
        try
        {
            var outputEvent = adapter.ParseOutputLine(line);
            if (outputEvent == null)
            {
                return;
            }

            // 提取会话ID
            var sessionId = adapter.ExtractSessionId(outputEvent);
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                _activeThreadId = sessionId;
                // 更新CliExecutorService中的会话ID
                CliExecutorService.SetCliThreadId(_sessionId, sessionId);
            }

            // 提取助手消息
            var assistantMessage = adapter.ExtractAssistantMessage(outputEvent);
            if (!string.IsNullOrEmpty(assistantMessage))
            {
                _jsonlAssistantMessageBuilder?.Append(assistantMessage);
            }

            // 转换为JsonlDisplayItem
            var displayItem = new JsonlDisplayItem
            {
                Type = outputEvent.EventType,
                Title = adapter.GetEventTitle(outputEvent),
                Content = GetEventDisplayContent(outputEvent, outputEvent.Content),
                ItemType = outputEvent.ItemType,
                IsUnknown = outputEvent.IsUnknown
            };

            // 转换使用统计
            if (outputEvent.Usage != null)
            {
                displayItem.Usage = new JsonlUsageDetail
                {
                    InputTokens = outputEvent.Usage.InputTokens,
                    CachedInputTokens = outputEvent.Usage.CachedInputTokens,
                    OutputTokens = outputEvent.Usage.OutputTokens
                };
            }

            _jsonlEvents.Add(displayItem);

            QueueSaveOutputState();

            // 更新进度追踪器
            UpdateProgressTracker(outputEvent.EventType);
        }
        catch (Exception ex)
        {
            AddUnknownJsonlEvent($"适配器处理失败: {ex.Message}", line);
        }
    }

    /// <summary>
    /// 根据事件类型更新进度追踪器
    /// </summary>
    private void UpdateProgressTracker(string eventType)
    {
        switch (eventType)
        {
            case "thread.started":
            case "init":
                _progressTracker?.UpdateStage("thread.started", ProgressTracker.StageStatus.Completed);
                _progressTracker?.UpdateStage("turn.started", ProgressTracker.StageStatus.Active);
                break;
            case "turn.started":
                _progressTracker?.UpdateStage("turn.started", ProgressTracker.StageStatus.Completed);
                _progressTracker?.UpdateStage("item.started", ProgressTracker.StageStatus.Active);
                break;
            case "item.started":
            case "tool_use":
                _progressTracker?.UpdateStage("item.started", ProgressTracker.StageStatus.Completed);
                _progressTracker?.UpdateStage("item.updated", ProgressTracker.StageStatus.Active);
                break;
            case "item.updated":
            case "message":
            case "tool_result":
                _progressTracker?.UpdateStage("item.updated", ProgressTracker.StageStatus.Active);
                break;
            case "item.completed":
                _progressTracker?.UpdateStage("item.updated", ProgressTracker.StageStatus.Completed);
                break;
            case "turn.completed":
            case "result":
                _progressTracker?.UpdateStage("turn.completed", ProgressTracker.StageStatus.Completed);
                break;
        }
    }

    /// <summary>
    /// 原有的硬编码解析逻辑（向后兼容）
    /// </summary>
    private void HandleJsonlLineLegacy(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var typeElement))
            {
                AddUnknownJsonlEvent("缺少 type 字段", line);
                return;
            }

            var eventType = typeElement.GetString() ?? string.Empty;
            switch (eventType)
            {
                case "thread.started":
                    HandleThreadStarted(root);
                    break;
                case "turn.started":
                    HandleTurnStarted();
                    break;
                case "turn.completed":
                    HandleTurnCompleted(root);
                    break;
                case "turn.failed":
                    HandleTurnFailed(root);
                    break;
                case "item.started":
                    HandleItemStarted(root);
                    break;
                case "item.updated":
                    HandleItemUpdated(root);
                    break;
                case "item.completed":
                    HandleItemCompleted(root);
                    break;
                case "error":
                    HandleError(root);
                    break;
                default:
                    AddUnknownJsonlEvent($"未识别的事件类型: {eventType}", line);
                    break;
            }
        }
        catch (JsonException jsonEx)
        {
            AddUnknownJsonlEvent($"解析 JSONL 失败: {jsonEx.Message}", line);
        }
        catch (Exception ex)
        {
            AddUnknownJsonlEvent($"处理 JSONL 失败: {ex.Message}", line);
        }
    }

    private void HandleThreadStarted(JsonElement root)
    {
        string? threadId = null;
        if (root.TryGetProperty("thread_id", out var threadIdElement))
        {
            threadId = threadIdElement.GetString();
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                _activeThreadId = threadId;
            }
        }

        var contentBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(threadId))
        {
            contentBuilder.AppendLine(T("cliEvent.content.threadId", ("id", threadId)));
        }
        else
        {
            contentBuilder.AppendLine(T("cliEvent.content.threadCreated"));
        }

        _jsonlEvents.Add(new JsonlDisplayItem
        {
            Type = "thread.started",
            Title = T("cliEvent.title.threadStarted"),
            Content = contentBuilder.ToString().TrimEnd()
        });

        // 更新进度追踪器
        _progressTracker?.UpdateStage("thread.started", ProgressTracker.StageStatus.Completed);
        _progressTracker?.UpdateStage("turn.started", ProgressTracker.StageStatus.Active);
    }

    private void HandleTurnStarted()
    {
        _jsonlEvents.Add(new JsonlDisplayItem
        {
            Type = "turn.started",
            Title = T("cliEvent.title.turnStarted"),
            Content = T("cliEvent.content.turnStarted")
        });

        // 更新进度追踪器
        _progressTracker?.UpdateStage("turn.started", ProgressTracker.StageStatus.Completed);
        _progressTracker?.UpdateStage("item.started", ProgressTracker.StageStatus.Active);
    }

    private void HandleTurnCompleted(JsonElement root)
    {
        JsonlUsageDetail? usage = null;
        if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
        {
            usage = new JsonlUsageDetail
            {
                InputTokens = GetLongProperty(usageElement, "input_tokens"),
                CachedInputTokens = GetLongProperty(usageElement, "cached_input_tokens"),
                OutputTokens = GetLongProperty(usageElement, "output_tokens")
            };
        }

        _jsonlEvents.Add(new JsonlDisplayItem
        {
            Type = "turn.completed",
            Title = T("cliEvent.title.turnCompleted"),
            Content = usage is null
                ? T("cliEvent.content.turnCompleted")
                : T("cliEvent.content.turnCompletedWithUsage"),
            Usage = usage
        });
    }

    private void HandleTurnFailed(JsonElement root)
    {
        var contentBuilder = new StringBuilder();
        contentBuilder.AppendLine("本轮交互失败。");

        if (root.TryGetProperty("error", out var errorElement))
        {
            if (errorElement.ValueKind == JsonValueKind.String)
            {
                var errorMsg = errorElement.GetString();
                if (!string.IsNullOrWhiteSpace(errorMsg))
                {
                    contentBuilder.AppendLine();
                    contentBuilder.AppendLine("错误信息:");
                    contentBuilder.AppendLine(errorMsg);
                }
            }
            else if (errorElement.ValueKind == JsonValueKind.Object)
            {
                if (errorElement.TryGetProperty("message", out var msgElement) && msgElement.ValueKind == JsonValueKind.String)
                {
                    var errorMsg = msgElement.GetString();
                    if (!string.IsNullOrWhiteSpace(errorMsg))
                    {
                        contentBuilder.AppendLine();
                        contentBuilder.AppendLine("错误信息:");
                        contentBuilder.AppendLine(errorMsg);
                    }
                }

                if (errorElement.TryGetProperty("code", out var codeElement))
                {
                    var code = codeElement.ValueKind == JsonValueKind.String
                        ? codeElement.GetString()
                        : codeElement.GetRawText();
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        contentBuilder.AppendLine($"错误代码: {code}");
                    }
                }
            }
        }

        _jsonlEvents.Add(new JsonlDisplayItem
        {
            Type = "turn.failed",
            Title = "交互失败",
            Content = contentBuilder.ToString().TrimEnd(),
            IsUnknown = true
        });
    }

    private void HandleError(JsonElement root)
    {
        var contentBuilder = new StringBuilder();
        
        if (root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String)
        {
            var errorMsg = messageElement.GetString();
            if (!string.IsNullOrWhiteSpace(errorMsg))
            {
                contentBuilder.AppendLine(errorMsg);
            }
        }

        if (root.TryGetProperty("code", out var codeElement))
        {
            var code = codeElement.ValueKind == JsonValueKind.String
                ? codeElement.GetString()
                : codeElement.GetRawText();
            if (!string.IsNullOrWhiteSpace(code))
            {
                contentBuilder.AppendLine($"错误代码: {code}");
            }
        }

        if (root.TryGetProperty("details", out var detailsElement) && detailsElement.ValueKind == JsonValueKind.String)
        {
            var details = detailsElement.GetString();
            if (!string.IsNullOrWhiteSpace(details))
            {
                contentBuilder.AppendLine();
                contentBuilder.AppendLine("详细信息:");
                contentBuilder.AppendLine(details);
            }
        }

        if (contentBuilder.Length == 0)
        {
            contentBuilder.Append("发生未知错误。");
        }

        _jsonlEvents.Add(new JsonlDisplayItem
        {
            Type = "error",
            Title = "错误",
            Content = contentBuilder.ToString().TrimEnd(),
            IsUnknown = true
        });
    }

    private void HandleItemStarted(JsonElement root)
    {
        if (!root.TryGetProperty("item", out var itemElement) || itemElement.ValueKind != JsonValueKind.Object)
        {
            AddUnknownJsonlEvent("item.started 缺少 item 字段", root.GetRawText());
            return;
        }

        string? itemType = null;
        if (itemElement.TryGetProperty("type", out var itemTypeElement) && itemTypeElement.ValueKind == JsonValueKind.String)
        {
            itemType = itemTypeElement.GetString();
        }

        if (itemElement.TryGetProperty("thread_id", out var itemThread) && itemThread.ValueKind == JsonValueKind.String)
        {
            var threadId = itemThread.GetString();
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                _activeThreadId = threadId;
            }
        }

        var contentBuilder = new StringBuilder();

        if (itemElement.TryGetProperty("command", out var commandElement) && commandElement.ValueKind == JsonValueKind.String)
        {
            var commandText = commandElement.GetString();
            if (!string.IsNullOrWhiteSpace(commandText))
            {
                contentBuilder.AppendLine($"执行命令: {commandText}");
            }
        }

        if (itemElement.TryGetProperty("status", out var statusElement))
        {
            var statusText = statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString()
                : statusElement.GetRawText();

            if (!string.IsNullOrWhiteSpace(statusText))
            {
                contentBuilder.AppendLine($"状态: {statusText}");
            }
        }

        if (contentBuilder.Length == 0)
        {
            contentBuilder.Append("已开始处理该节点。");
        }

        var title = itemType switch
        {
            "command_execution" => "执行命令开始",
            "agent_message" => "助手消息生成",
            "reasoning" => "推理节点开始",
            "mcp_tool_call" => "调用 MCP 工具",
            "web_search" => "执行网络搜索",
            _ => $"节点开始（{itemType ?? "未知类型"}）"
        };

        _jsonlEvents.Add(new JsonlDisplayItem
        {
            Type = "item.started",
            ItemType = itemType,
            Title = title,
            Content = contentBuilder.ToString().TrimEnd()
        });
    }

    private void HandleItemUpdated(JsonElement root)
    {
        if (!root.TryGetProperty("item", out var itemElement) || itemElement.ValueKind != JsonValueKind.Object)
        {
            AddUnknownJsonlEvent("item.updated 缺少 item 字段", root.GetRawText());
            return;
        }

        string? itemType = null;
        if (itemElement.TryGetProperty("type", out var itemTypeElement) && itemTypeElement.ValueKind == JsonValueKind.String)
        {
            itemType = itemTypeElement.GetString();
        }

        if (itemElement.TryGetProperty("thread_id", out var itemThread) && itemThread.ValueKind == JsonValueKind.String)
        {
            var threadId = itemThread.GetString();
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                _activeThreadId = threadId;
            }
        }

        var content = itemType switch
        {
            "todo_list" => FormatTodoListContent(itemElement),
            "command_execution" => FormatCommandExecutionContent(itemElement),
            _ => ExtractItemText(itemElement)
        };

        var title = itemType switch
        {
            "todo_list" => "待办列表更新",
            "command_execution" => "命令执行中",
            "agent_message" => "助手消息更新",
            "reasoning" => "推理过程更新",
            "mcp_tool_call" => "MCP 工具调用中",
            "web_search" => "网络搜索中",
            _ => $"节点更新（{itemType ?? "未知类型"}）"
        };

        _jsonlEvents.Add(new JsonlDisplayItem
        {
            Type = "item.updated",
            ItemType = itemType,
            Title = title,
            Content = content
        });
    }

    private void HandleItemCompleted(JsonElement root)
    {
        if (!root.TryGetProperty("item", out var itemElement) || itemElement.ValueKind != JsonValueKind.Object)
        {
            AddUnknownJsonlEvent("item.completed 缺少 item 字段", root.GetRawText());
            return;
        }

        string? itemType = null;
        if (itemElement.TryGetProperty("type", out var itemTypeElement))
        {
            itemType = itemTypeElement.GetString();
        }

        if (itemElement.TryGetProperty("thread_id", out var itemThread) && itemThread.ValueKind == JsonValueKind.String)
        {
            var threadId = itemThread.GetString();
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                _activeThreadId = threadId;
            }
        }

        var content = itemType switch
        {
            "file_change" => FormatFileChangeContent(itemElement),
            "command_execution" => FormatCommandExecutionContent(itemElement),
            "todo_list" => FormatTodoListContent(itemElement),
            "mcp_tool_call" => FormatMcpToolCallContent(itemElement),
            "web_search" => FormatWebSearchContent(itemElement),
            _ => ExtractItemText(itemElement)
        };

        var title = itemType switch
        {
            "reasoning" => "推理过程",
            "agent_message" => "助手回复",
            "file_change" => "文件已更新",
            "command_execution" => "命令执行完成",
            "todo_list" => "待办列表完成",
            "mcp_tool_call" => "MCP 工具调用完成",
            "web_search" => "网络搜索完成",
            _ => $"节点完成（{itemType ?? "未知类型"}）"
        };

        _jsonlEvents.Add(new JsonlDisplayItem
        {
            Type = "item.completed",
            ItemType = itemType,
            Title = title,
            Content = content
        });

        if (itemType == "agent_message" && _jsonlAssistantMessageBuilder != null)
        {
            if (_jsonlAssistantMessageBuilder.Length > 0)
            {
                _jsonlAssistantMessageBuilder.AppendLine().AppendLine();
            }
            _jsonlAssistantMessageBuilder.Append(content.TrimEnd());
        }
    }

    private string FormatTodoListContent(JsonElement itemElement)
    {
        var builder = new StringBuilder();

        if (itemElement.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                // 提取标题（兼容 title/text 字段）
                string? title = null;
                if (item.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String)
                {
                    title = titleElement.GetString();
                }
                if (string.IsNullOrWhiteSpace(title) && 
                    item.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                {
                    title = textElement.GetString();
                }

                // 提取状态（兼容 status 字符串和 completed 布尔值）
                string status = "pending";
                if (item.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String)
                {
                    status = statusElement.GetString() ?? "pending";
                }
                else if (item.TryGetProperty("completed", out var completedElement) &&
                         (completedElement.ValueKind == JsonValueKind.True || completedElement.ValueKind == JsonValueKind.False))
                {
                    status = completedElement.GetBoolean() ? "completed" : "pending";
                }

                if (!string.IsNullOrWhiteSpace(title))
                {
                    // 根据状态显示不同图标（与 Codex 保持一致）
                    var statusIcon = status switch
                    {
                        "completed" => "✓",
                        "in_progress" => "◐",
                        "pending" => "○",
                        _ => "○"
                    };
                    
                    // 每项独占一行，使用双换行确保Markdown渲染为段落
                    builder.AppendLine($"{statusIcon} {title}");
                    builder.AppendLine(); // 空行分隔
                }
            }
        }

        if (builder.Length == 0)
        {
            return "待办列表已更新";
        }

        return builder.ToString().TrimEnd();
    }

    private string FormatCommandExecutionContent(JsonElement itemElement)
    {
        var builder = new StringBuilder();

        if (itemElement.TryGetProperty("command", out var commandElement) && commandElement.ValueKind == JsonValueKind.String)
        {
            var commandText = commandElement.GetString();
            if (!string.IsNullOrWhiteSpace(commandText))
            {
                builder.AppendLine($"命令: {commandText}");
            }
        }

        if (itemElement.TryGetProperty("exit_code", out var exitCodeElement))
        {
            var exitCode = exitCodeElement.ValueKind == JsonValueKind.Number
                ? exitCodeElement.GetInt32().ToString()
                : exitCodeElement.GetRawText();
            builder.AppendLine($"退出码: {exitCode}");
        }

        if (itemElement.TryGetProperty("status", out var statusElement))
        {
            var statusText = statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString()
                : statusElement.GetRawText();

            if (!string.IsNullOrWhiteSpace(statusText))
            {
                builder.AppendLine($"状态: {statusText}");
            }
        }

        if (itemElement.TryGetProperty("aggregated_output", out var outputElement) && outputElement.ValueKind == JsonValueKind.String)
        {
            var output = outputElement.GetString();
            if (!string.IsNullOrWhiteSpace(output))
            {
                builder.AppendLine();
                builder.AppendLine("输出:");
                builder.AppendLine("---");
                builder.AppendLine(output.TrimEnd());
            }
        }

        if (builder.Length == 0)
        {
            return itemElement.GetRawText();
        }

        return builder.ToString().TrimEnd();
    }

    private string FormatMcpToolCallContent(JsonElement itemElement)
    {
        var builder = new StringBuilder();

        if (itemElement.TryGetProperty("tool_name", out var toolNameElement) && toolNameElement.ValueKind == JsonValueKind.String)
        {
            var toolName = toolNameElement.GetString();
            if (!string.IsNullOrWhiteSpace(toolName))
            {
                builder.AppendLine($"工具名称: {toolName}");
            }
        }

        if (itemElement.TryGetProperty("status", out var statusElement))
        {
            var statusText = statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString()
                : statusElement.GetRawText();

            if (!string.IsNullOrWhiteSpace(statusText))
            {
                builder.AppendLine($"状态: {statusText}");
            }
        }

        if (itemElement.TryGetProperty("result", out var resultElement) && resultElement.ValueKind == JsonValueKind.String)
        {
            var result = resultElement.GetString();
            if (!string.IsNullOrWhiteSpace(result))
            {
                builder.AppendLine();
                builder.AppendLine("结果:");
                builder.AppendLine("---");
                builder.AppendLine(result.TrimEnd());
            }
        }

        if (builder.Length == 0)
        {
            return itemElement.GetRawText();
        }

        return builder.ToString().TrimEnd();
    }

    private string FormatWebSearchContent(JsonElement itemElement)
    {
        var builder = new StringBuilder();

        if (itemElement.TryGetProperty("query", out var queryElement) && queryElement.ValueKind == JsonValueKind.String)
        {
            var query = queryElement.GetString();
            if (!string.IsNullOrWhiteSpace(query))
            {
                builder.AppendLine($"搜索关键词: {query}");
            }
        }

        if (itemElement.TryGetProperty("status", out var statusElement))
        {
            var statusText = statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString()
                : statusElement.GetRawText();

            if (!string.IsNullOrWhiteSpace(statusText))
            {
                builder.AppendLine($"状态: {statusText}");
            }
        }

        if (itemElement.TryGetProperty("results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array)
        {
            var count = resultsElement.GetArrayLength();
            if (count > 0)
            {
                builder.AppendLine($"找到 {count} 个结果");
            }
        }

        if (itemElement.TryGetProperty("summary", out var summaryElement) && summaryElement.ValueKind == JsonValueKind.String)
        {
            var summary = summaryElement.GetString();
            if (!string.IsNullOrWhiteSpace(summary))
            {
                builder.AppendLine();
                builder.AppendLine("摘要:");
                builder.AppendLine("---");
                builder.AppendLine(summary.TrimEnd());
            }
        }

        if (builder.Length == 0)
        {
            return itemElement.GetRawText();
        }

        return builder.ToString().TrimEnd();
    }

    private string FormatFileChangeContent(JsonElement itemElement)
    {
        var builder = new StringBuilder();

        if (itemElement.TryGetProperty("status", out var statusElement))
        {
            var statusText = statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString()
                : statusElement.GetRawText();

            if (!string.IsNullOrWhiteSpace(statusText))
            {
                builder.AppendLine($"状态: {statusText}");
            }
        }

        if (itemElement.TryGetProperty("changes", out var changesElement) && changesElement.ValueKind == JsonValueKind.Array)
        {
            var workspaceRoot = string.Empty;
            try
            {
                workspaceRoot = CliExecutorService.GetSessionWorkspacePath(_sessionId);
            }
            catch
            {
                workspaceRoot = string.Empty;
            }

            var index = 1;
            foreach (var change in changesElement.EnumerateArray())
            {
                if (change.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                string? path = null;
                string? kind = null;

                if (change.TryGetProperty("path", out var pathElement) && pathElement.ValueKind == JsonValueKind.String)
                {
                    path = pathElement.GetString();
                }

                if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(workspaceRoot))
                {
                    try
                    {
                        var relative = Path.GetRelativePath(workspaceRoot, path);
                        if (!relative.StartsWith("..", StringComparison.Ordinal))
                        {
                            path = relative;
                        }
                    }
                    catch
                    {
                        // ignore path normalization errors
                    }
                }

                if (change.TryGetProperty("kind", out var kindElement) && kindElement.ValueKind == JsonValueKind.String)
                {
                    kind = kindElement.GetString();
                }

                var label = kind switch
                {
                    "create" => "新增",
                    "update" => "更新",
                    "delete" => "删除",
                    _ => kind ?? "变更"
                };

                var displayPath = path ?? "(未知路径)";
                builder.AppendLine($"{index}. {label}: {displayPath}");
                index++;
            }
        }

        if (builder.Length == 0)
        {
            return itemElement.GetRawText();
        }

        return builder.ToString().TrimEnd();
    }

    private void AddUnknownJsonlEvent(string reason, string rawLine)
    {
        var detail = string.IsNullOrWhiteSpace(rawLine) ? reason : $"{reason}\n原始数据: {rawLine}";
        _jsonlEvents.Add(new JsonlDisplayItem
        {
            Type = "unknown",
            Title = "未识别的事件",
            Content = detail,
            IsUnknown = true
        });

        Console.WriteLine($"[JSONL] {reason}: {rawLine}");
    }

    private static string ExtractItemText(JsonElement itemElement)
    {
        if (itemElement.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
        {
            return textElement.GetString() ?? string.Empty;
        }

        if (itemElement.TryGetProperty("content", out var contentElement))
        {
            if (contentElement.ValueKind == JsonValueKind.String)
            {
                return contentElement.GetString() ?? string.Empty;
            }

            if (contentElement.ValueKind == JsonValueKind.Array)
            {
                var builder = new StringBuilder();
                foreach (var part in contentElement.EnumerateArray())
                {
                    if (part.ValueKind == JsonValueKind.String)
                    {
                        builder.AppendLine(part.GetString());
                        continue;
                    }

                    if (part.ValueKind == JsonValueKind.Object)
                    {
                        if (part.TryGetProperty("text", out var partText) && partText.ValueKind == JsonValueKind.String)
                        {
                            builder.AppendLine(partText.GetString());
                        }
                        else if (part.TryGetProperty("content", out var nestedContent) && nestedContent.ValueKind == JsonValueKind.String)
                        {
                            builder.AppendLine(nestedContent.GetString());
                        }
                    }
                }

                var combined = builder.ToString().Trim();
                if (!string.IsNullOrEmpty(combined))
                {
                    return combined;
                }
            }
        }

        return itemElement.GetRawText();
    }

    private static long? GetLongProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number)
        {
            if (property.TryGetInt64(out var longValue))
            {
                return longValue;
            }

            if (property.TryGetDouble(out var doubleValue))
            {
                return (long)doubleValue;
            }
        }

        return null;
    }

    private static string FormatTokenValue(long? value)
    {
        return value.HasValue ? value.Value.ToString("N0") : "--";
    }

    private string GetJsonlAssistantMessage()
    {
        if (_jsonlAssistantMessageBuilder == null)
        {
            return string.Empty;
        }

        return _jsonlAssistantMessageBuilder.ToString().TrimEnd();
    }

    private string GetEventContainerAccent(JsonlDisplayItem item)
    {
        if (item.IsUnknown)
        {
            return "border-l-4 border-red-400 bg-red-50";
        }

        if (item.ItemType == "todo_list")
        {
            return "border-l-4 border-primary-500 bg-primary-50";
        }

        if (item.ItemType == "reasoning")
        {
            return "border-l-4 border-gray-500 bg-gray-50";
        }

        if (item.ItemType == "agent_message")
        {
            return "border-l-4 border-amber-400 bg-white";
        }

        return item.Type switch
        {
            // Codex事件类型
            "thread.started" => "border-l-4 border-primary-500 bg-primary-50",
            "turn.started" => "border-l-4 border-sky-400 bg-sky-50",
            "turn.completed" => "border-l-4 border-emerald-400 bg-emerald-50",
            "turn.failed" => "border-l-4 border-red-500 bg-red-50",
            "item.started" => "border-l-4 border-amber-300 bg-amber-50",
            "item.updated" => "border-l-4 border-blue-300 bg-blue-50",
            "error" => "border-l-4 border-red-500 bg-red-50",
            // Claude Code事件类型
            "init" => "border-l-4 border-primary-500 bg-primary-50",
            "message" or "assistant" or "assistant:message" => "border-l-4 border-emerald-400 bg-emerald-50",
            "tool_use" => "border-l-4 border-sky-400 bg-sky-50",
            "tool_result" => "border-l-4 border-blue-300 bg-blue-50",
            "result" => "border-l-4 border-emerald-400 bg-emerald-50",
            "system" => "border-l-4 border-gray-400 bg-gray-50",
            "user" => "border-l-4 border-blue-400 bg-blue-50",
            "raw" => "border-l-4 border-gray-200 bg-white",
            // OpenCode事件类型
            "session_start" => "border-l-4 border-primary-500 bg-primary-50",
            "step_start" => "border-l-4 border-sky-400 bg-sky-50",
            "step_finish" => "border-l-4 border-emerald-400 bg-emerald-50",
            "text" => "border-l-4 border-emerald-400 bg-emerald-50",
            "tool_start" => "border-l-4 border-sky-400 bg-sky-50",
            "tool_finish" => "border-l-4 border-blue-300 bg-blue-50",
            "session_end" or "complete" => "border-l-4 border-emerald-400 bg-emerald-50",
            _ => "border-l-4 border-gray-200 bg-white"
        };
    }

    private string GetEventBadgeClass(JsonlDisplayItem item)
    {
        if (item.IsUnknown)
        {
            return "bg-red-100 text-red-700";
        }

        if (item.ItemType == "todo_list")
        {
            return "bg-primary-100 text-primary-700";
        }

        if (item.ItemType == "reasoning")
        {
            return "bg-gray-200 text-gray-800";
        }

        if (item.ItemType == "agent_message")
        {
            return "bg-amber-100 text-amber-700";
        }

        return item.Type switch
        {
            // Codex事件类型
            "thread.started" => "bg-primary-100 text-primary-700",
            "turn.started" => "bg-sky-100 text-sky-700",
            "turn.completed" => "bg-emerald-100 text-emerald-700",
            "turn.failed" => "bg-red-100 text-red-700",
            "item.started" => "bg-amber-100 text-amber-700",
            "item.updated" => "bg-blue-100 text-blue-700",
            "error" => "bg-red-100 text-red-700",
            // Claude Code事件类型
            "init" => "bg-primary-100 text-primary-700",
            "message" or "assistant" or "assistant:message" => "bg-emerald-100 text-emerald-700",
            "tool_use" => "bg-sky-100 text-sky-700",
            "tool_result" => "bg-blue-100 text-blue-700",
            "result" => "bg-emerald-100 text-emerald-700",
            "system" => "bg-gray-100 text-gray-700",
            "user" => "bg-blue-100 text-blue-700",
            "raw" => "bg-gray-200 text-gray-700",
            // OpenCode事件类型
            "session_start" => "bg-primary-100 text-primary-700",
            "step_start" => "bg-sky-100 text-sky-700",
            "step_finish" => "bg-emerald-100 text-emerald-700",
            "text" => "bg-emerald-100 text-emerald-700",
            "tool_start" => "bg-sky-100 text-sky-700",
            "tool_finish" => "bg-blue-100 text-blue-700",
            "session_end" or "complete" => "bg-emerald-100 text-emerald-700",
            _ => "bg-gray-200 text-gray-700"
        };
    }

    private string GetEventBadgeLabel(JsonlDisplayItem item)
    {
        if (item.IsUnknown)
        {
            return T("cliEvent.badge.unknown");
        }

        if (item.ItemType == "todo_list")
        {
            return T("cliEvent.badge.todo");
        }

        if (item.ItemType == "reasoning")
        {
            return T("cliEvent.badge.reasoning");
        }

        if (item.ItemType == "agent_message")
        {
            return T("cliEvent.badge.reply");
        }

        return item.Type switch
        {
            // Codex事件类型
            "thread.started" => T("cliEvent.badge.thread"),
            "turn.started" => T("cliEvent.badge.turnStart"),
            "turn.completed" => T("cliEvent.badge.turnEnd"),
            "turn.failed" => T("cliEvent.badge.turnFailed"),
            "item.started" => T("cliEvent.badge.itemStart"),
            "item.updated" => T("cliEvent.badge.itemUpdate"),
            "error" => T("cliEvent.badge.error"),
            // Claude Code事件类型
            "init" => T("cliEvent.badge.init"),
            "message" or "assistant" or "assistant:message" => T("cliEvent.badge.reply"),
            "tool_use" => T("cliEvent.badge.toolUse"),
            "tool_result" => T("cliEvent.badge.toolResult"),
            "result" => T("cliEvent.badge.result"),
            "system" => T("cliEvent.badge.system"),
            "user" => T("cliEvent.badge.input"),
            "raw" => T("cliEvent.badge.output"),
            // OpenCode事件类型
            "session_start" => T("cliEvent.badge.sessionStart"),
            "step_start" => T("cliEvent.badge.stepStart"),
            "step_finish" => T("cliEvent.badge.turnEnd"),
            "text" => T("cliEvent.badge.reply"),
            "tool_start" => T("cliEvent.badge.toolUse"),
            "tool_finish" => T("cliEvent.badge.toolResult"),
            "session_end" or "complete" => T("cliEvent.badge.result"),
            _ => T("cliEvent.badge.event")
        };
    }

    private string GetEventDisplayTitle(JsonlDisplayItem item)
    {
        if (item.ItemType == "todo_list" && item.Type == "tool_use")
        {
            return T("cliEvent.title.todoListUpdate");
        }

        var actionKey = item.Type switch
        {
            "item.started" => "cliEvent.action.start",
            "item.updated" => "cliEvent.action.update",
            "item.completed" => "cliEvent.action.complete",
            _ => string.Empty
        };

        if (!string.IsNullOrWhiteSpace(actionKey))
        {
            return T("cliEvent.title.itemAction",
                ("item", GetItemTypeLabel(item.ItemType)),
                ("action", T(actionKey)));
        }

        return item.Type switch
        {
            // Codex 事件类型
            "thread.started" => T("cliEvent.title.threadStarted"),
            "turn.started" => T("cliEvent.title.turnStarted"),
            "turn.completed" => T("cliEvent.title.turnCompleted"),
            "turn.failed" => T("cliEvent.title.turnFailed"),
            "error" => T("cliEvent.title.error"),
            // Claude Code 事件类型
            "init" => T("cliEvent.title.init"),
            "message" or "assistant" or "assistant:message" => T("cliEvent.title.message"),
            "tool_use" => T("cliEvent.title.toolUse"),
            "tool_result" => T("cliEvent.title.toolResult"),
            "result" => T("cliEvent.title.result"),
            "system" => T("cliEvent.title.system"),
            "user" => T("cliEvent.title.user"),
            "raw" => T("cliEvent.title.raw"),
            _ => string.IsNullOrWhiteSpace(item.Title) ? T("cliEvent.title.event", ("type", item.Type)) : item.Title
        };
    }

    private string GetEventDisplayContent(CliOutputEvent outputEvent, string? fallbackContent)
    {
        if (string.Equals(outputEvent.EventType, "turn.completed", StringComparison.OrdinalIgnoreCase))
        {
            return outputEvent.Usage is null
                ? T("cliEvent.content.turnCompleted")
                : T("cliEvent.content.turnCompletedWithUsage");
        }

        if (string.Equals(outputEvent.EventType, "turn.started", StringComparison.OrdinalIgnoreCase))
        {
            return T("cliEvent.content.turnStarted");
        }

        if (string.Equals(outputEvent.EventType, "thread.started", StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(outputEvent.SessionId)
                ? T("cliEvent.content.threadId", ("id", outputEvent.SessionId))
                : T("cliEvent.content.threadCreated");
        }

        return fallbackContent ?? string.Empty;
    }

    private string GetItemTypeLabel(string? itemType)
    {
        return itemType switch
        {
            "command_execution" => T("cliEvent.itemType.commandExecution"),
            "agent_message" => T("cliEvent.itemType.agentMessage"),
            "reasoning" => T("cliEvent.itemType.reasoning"),
            "mcp_tool_call" => T("cliEvent.itemType.mcpToolCall"),
            "web_search" => T("cliEvent.itemType.webSearch"),
            "todo_list" => T("cliEvent.itemType.todoList"),
            "file_change" => T("cliEvent.itemType.fileChange"),
            "tool_call" => T("cliEvent.itemType.toolCall"),
            _ => string.IsNullOrWhiteSpace(itemType) ? T("cliEvent.itemType.unknown") : itemType
        };
    }

    private async Task SendMessage()
    {
        if (string.IsNullOrWhiteSpace(_inputMessage) || _isLoading)
        {
            return;
        }

        var message = _inputMessage.Trim();
        _inputMessage = string.Empty;
        _isLoading = true;
        _currentAssistantMessage = string.Empty;

        var selectedTool = _availableTools.FirstOrDefault(t => t.Id == _selectedToolId);
        InitializeJsonlState(IsJsonlTool(selectedTool));

        // 启动进度追踪器
        if (_isJsonlOutputActive && _progressTracker != null)
        {
            _progressTracker.Start();
        }

        // 保存输入历史
        try
        {
            await JSRuntime.InvokeVoidAsync("webCliIndexedDB.saveInputHistory", message);
        }
        catch
        {
            // 忽略保存历史错误
        }

        // 添加用户消息到会话
        var userMessage = new ChatMessage
        {
            Role = "user",
            Content = message,
            CliToolId = _selectedToolId,
            IsCompleted = true
        };
        _messages.Add(userMessage);
        ChatSessionService.AddMessage(_sessionId, userMessage);

        StateHasChanged();
        await ScrollToBottom();

        // 创建助手消息
        var assistantMessage = new ChatMessage
        {
            Role = "assistant",
            Content = string.Empty,
            CliToolId = _selectedToolId,
            IsCompleted = false
        };

        var contentBuilder = new StringBuilder();

        try
        {
            // 直接调用服务执行 CLI，使用流式处理
            await foreach (var chunk in CliExecutorService.ExecuteStreamAsync(
                _sessionId, _selectedToolId, message, default))
            {
                if (chunk.IsError)
                {
                    assistantMessage.HasError = true;
                    assistantMessage.ErrorMessage = chunk.ErrorMessage;
                    assistantMessage.Content = chunk.ErrorMessage ?? "发生错误";
                    assistantMessage.IsCompleted = true;
                    
                    _messages.Add(assistantMessage);
                    ChatSessionService.AddMessage(_sessionId, assistantMessage);
                    
                    await UpdatePreview(assistantMessage.Content);
                    StateHasChanged();
                    break;
                }
                else if (chunk.IsCompleted)
                {
                    if (_isJsonlOutputActive)
                    {
                        ProcessJsonlChunk(string.Empty, flush: true);
                        var finalContent = GetJsonlAssistantMessage();
                        assistantMessage.Content = finalContent;
                        _currentAssistantMessage = finalContent;
                        contentBuilder.Clear();
                        contentBuilder.Append(finalContent);
                    }

                    // 完成
                    assistantMessage.IsCompleted = true;
                    if (!_isJsonlOutputActive)
                    {
                        assistantMessage.Content = contentBuilder.ToString();
                    }
                    
                    _messages.Add(assistantMessage);
                    ChatSessionService.AddMessage(_sessionId, assistantMessage);
                    
                    await UpdatePreview(assistantMessage.Content);
                    StateHasChanged();
                    break;
                }
                else
                {
                    // 流式内容
                    var chunkContent = chunk.Content ?? string.Empty;
                    if (_isJsonlOutputActive)
                    {
                        ProcessJsonlChunk(chunkContent, flush: false);
                        var liveContent = GetJsonlAssistantMessage();
                        _currentAssistantMessage = liveContent;
                        assistantMessage.Content = liveContent;
                    }
                    else
                    {
                        contentBuilder.Append(chunkContent);
                        _currentAssistantMessage = contentBuilder.ToString();
                        assistantMessage.Content = _currentAssistantMessage;
                    }
                    
                    // 实时更新预览
                    await UpdatePreview(_currentAssistantMessage);
                    StateHasChanged();
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"执行消息失败: {ex.Message}");
            
            assistantMessage.HasError = true;
            assistantMessage.ErrorMessage = ex.Message;
            assistantMessage.Content = $"执行失败: {ex.Message}";
            assistantMessage.IsCompleted = true;
            
            _messages.Add(assistantMessage);
            ChatSessionService.AddMessage(_sessionId, assistantMessage);
            
            StateHasChanged();
        }
        finally
        {
            if (_isJsonlOutputActive)
            {
                ProcessJsonlChunk(string.Empty, flush: true);
                _currentAssistantMessage = GetJsonlAssistantMessage();
                
                // 完成进度追踪
                if (_progressTracker != null)
                {
                    if (assistantMessage.HasError)
                    {
                        _progressTracker.Fail(assistantMessage.ErrorMessage ?? "执行失败");
                    }
                    else
                    {
                        _progressTracker.Complete();
                    }
                }
            }
            
            _isLoading = false;
            _currentAssistantMessage = string.Empty;
            StateHasChanged();
            await ScrollToBottom();
            
            // 自动保存当前会话
            await SaveCurrentSessionAsync();
        }
    }

    private Task UpdatePreview(string content)
    {
        if (_disposed) return Task.CompletedTask;

        _rawOutput = content;

        QueueSaveOutputState();
        
        // 使用防抖机制，避免过于频繁的更新
        lock (_updateLock)
        {
            _hasPendingUpdate = true;
            
            // 如果定时器已存在，重置它
            _updateTimer?.Dispose();
            
            // 创建新的定时器，50ms后执行更新
            _updateTimer = new System.Threading.Timer(async _ =>
            {
                if (_disposed || !_hasPendingUpdate) return;
                
                lock (_updateLock)
                {
                    _hasPendingUpdate = false;
                }
                
                await InvokeAsync(() =>
                {
                    StateHasChanged();
                    // 延迟滚动，等待DOM更新完成
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(10);
                        await ScrollOutputToBottom();
                    });
                });
            }, null, 50, Timeout.Infinite);
        }
        
        return Task.CompletedTask;
    }

    private async Task<bool> IsIndexedDbReadyAsync()
    {
        try
        {
            // 快速路径
            if (await JSRuntime.InvokeAsync<bool>("webCliIndexedDB.isReady"))
            {
                return true;
            }

            // 页面刚加载时，IndexedDB autoInit 可能还没完成，最多等待 3 秒
            var retries = 30;
            while (retries-- > 0)
            {
                await Task.Delay(100);
                if (await JSRuntime.InvokeAsync<bool>("webCliIndexedDB.isReady"))
                {
                    return true;
                }
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private void QueueSaveOutputState(bool forceImmediate = false)
    {
        if (_disposed)
        {
            return;
        }

        lock (_outputStateSaveLock)
        {
            _hasPendingOutputStateSave = true;

            _outputStateSaveTimer?.Dispose();

            var dueTime = forceImmediate ? 1 : OutputStateSaveDebounceMs;
            _outputStateSaveTimer = new System.Threading.Timer(async _ =>
            {
                if (_disposed) return;

                lock (_outputStateSaveLock)
                {
                    if (!_hasPendingOutputStateSave)
                    {
                        return;
                    }
                    _hasPendingOutputStateSave = false;
                }

                await InvokeAsync(async () => await SaveOutputStateAsync());
            }, null, dueTime, Timeout.Infinite);
        }
    }

    private OutputPanelState BuildOutputPanelStateSnapshot(string sessionId)
    {
        var state = new OutputPanelState
        {
            SessionId = sessionId,
            RawOutput = _rawOutput ?? string.Empty,
            IsJsonlOutputActive = _isJsonlOutputActive,
            ActiveThreadId = _activeThreadId ?? string.Empty,
            UpdatedAt = DateTime.Now,
            JsonlEvents = new List<OutputJsonlEvent>()
        };

        foreach (var evt in _jsonlEvents)
        {
            state.JsonlEvents.Add(new OutputJsonlEvent
            {
                Type = evt.Type,
                Title = evt.Title,
                Content = evt.Content,
                ItemType = evt.ItemType,
                IsUnknown = evt.IsUnknown,
                Usage = evt.Usage == null
                    ? null
                    : new OutputJsonlUsageDetail
                    {
                        InputTokens = evt.Usage.InputTokens,
                        CachedInputTokens = evt.Usage.CachedInputTokens,
                        OutputTokens = evt.Usage.OutputTokens
                    }
            });
        }

        return state;
    }

    private async Task SaveOutputStateAsync()
    {
        if (_disposed)
        {
            return;
        }

        var sessionId = _sessionId;

        try
        {
            if (!await IsIndexedDbReadyAsync())
            {
                return;
            }

            var state = BuildOutputPanelStateSnapshot(sessionId);
            await JSRuntime.InvokeVoidAsync("webCliIndexedDB.saveSessionOutput", state);
        }
        catch
        {
            // 持久化失败不影响主流程
        }
    }

    private async Task LoadOutputStateAsync(string sessionId)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!await IsIndexedDbReadyAsync())
            {
                return;
            }

            var state = await JSRuntime.InvokeAsync<OutputPanelState?>("webCliIndexedDB.getSessionOutput", sessionId);
            if (state == null)
            {
                return;
            }

            _rawOutput = state.RawOutput ?? string.Empty;
            _isJsonlOutputActive = state.IsJsonlOutputActive;
            _activeThreadId = state.ActiveThreadId ?? string.Empty;

            _jsonlEvents.Clear();
            _jsonlGroupOpenState.Clear();
            _jsonlPendingBuffer = string.Empty;
            _jsonlAssistantMessageBuilder = _isJsonlOutputActive ? new StringBuilder() : null;

            if (state.JsonlEvents != null)
            {
                foreach (var evt in state.JsonlEvents)
                {
                    _jsonlEvents.Add(new JsonlDisplayItem
                    {
                        Type = evt.Type ?? string.Empty,
                        Title = evt.Title ?? string.Empty,
                        Content = evt.Content ?? string.Empty,
                        ItemType = evt.ItemType,
                        IsUnknown = evt.IsUnknown,
                        Usage = evt.Usage == null
                            ? null
                            : new JsonlUsageDetail
                            {
                                InputTokens = evt.Usage.InputTokens,
                                CachedInputTokens = evt.Usage.CachedInputTokens,
                                OutputTokens = evt.Usage.OutputTokens
                            }
                    });
                }
            }

            StateHasChanged();
        }
        catch
        {
            // 恢复失败不影响主流程
        }
    }

    private async Task DeleteOutputStateAsync(string sessionId)
    {
        try
        {
            if (!await IsIndexedDbReadyAsync())
            {
                return;
            }

            await JSRuntime.InvokeVoidAsync("webCliIndexedDB.deleteSessionOutput", sessionId);
        }
        catch
        {
            // 删除失败不阻塞会话删除
        }
    }

    private async Task ScrollOutputToBottom()
    {
        if (_disposed) return;

        try
        {
            await JSRuntime.InvokeVoidAsync("scrollOutputToBottom");
        }
        catch (JSDisconnectedException)
        {
            // 连接已断开,忽略
        }
        catch (Exception ex)
        {
            // 忽略其他滚动错误
            Console.WriteLine($"滚动输出区域失败: {ex.Message}");
        }
    }

    private async Task ClearChat()
    {
        if (_disposed) return;

        _messages.Clear();
        _currentAssistantMessage = string.Empty;
        _rawOutput = string.Empty;
    _jsonlEvents.Clear();
    _jsonlPendingBuffer = string.Empty;
    _activeThreadId = string.Empty;
    _isJsonlOutputActive = false;
    _jsonlAssistantMessageBuilder = null;

        try
        {
            // 直接调用服务清空会话
            ChatSessionService.ClearSession(_sessionId);
            CliExecutorService.CleanupSessionWorkspace(_sessionId);
            
            // 创建新会话
            await CreateNewSessionAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"清空会话失败: {ex.Message}");
        }

        StateHasChanged();
        await Task.CompletedTask;
    }

    private async Task HandleKeyDown(KeyboardEventArgs e)
    {
        // 回车发送（不按Shift）
        if (e.Key == "Enter" && !e.ShiftKey)
        {
            _isKeyDownEnterWithoutShift = true;
            await SendMessage();
            _isKeyDownEnterWithoutShift = false;
        }
        else
        {
            _isKeyDownEnterWithoutShift = false;
        }
        // Shift+回车换行（默认行为，不需要额外处理）
    }

    private async Task ScrollToBottom()
    {
        if (_disposed) return;

        try
        {
            await JSRuntime.InvokeVoidAsync("scrollChatToBottom");
        }
        catch (JSDisconnectedException)
        {
            // 连接已断开,忽略
        }
        catch (Exception ex)
        {
            // 忽略其他滚动错误
            Console.WriteLine($"滚动聊天区域失败: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        
        // 停止所有定时器
        if (_workspaceRefreshTimer != null)
        {
            await _workspaceRefreshTimer.DisposeAsync();
        }
        
        if (_updateTimer != null)
        {
            await _updateTimer.DisposeAsync();
        }

        if (_outputStateSaveTimer != null)
        {
            await _outputStateSaveTimer.DisposeAsync();
        }
        
        // 停止当前会话的所有开发服务器
        try
        {
            await DevServerManager.StopAllSessionServersAsync(_sessionId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"停止开发服务器失败: {ex.Message}");
        }
        
        // 注意：不在此处清理工作区，原因如下：
        // 1. 页面刷新会触发 Dispose，但用户可能只是误操作或想重新加载
        // 2. 工作区文件应该保留，以便刷新后继续使用
        // 3. 后台服务 WorkspaceCleanupBackgroundService 会在24小时后自动清理过期工作区
        // 4. 用户手动删除会话时，DeleteSession 方法会显式调用 CleanupSessionWorkspace
        
        // 仅清理内存中的会话缓存（不删除文件）
        try
        {
            ChatSessionService.ClearSession(_sessionId);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"清理会话缓存失败: {ex.Message}");
        }
    }

    private async Task LoadWorkspaceFiles()
    {
        if (_disposed) return;

        try
        {
            // 直接调用服务获取工作区文件
            var workspacePath = CliExecutorService.GetSessionWorkspacePath(_sessionId);
            
            if (!Directory.Exists(workspacePath))
            {
                return;
            }

            // 使用懒加载，只加载第一级目录
            var files = GetDirectoryStructure(workspacePath, workspacePath, false);
            
            // 只有在文件列表真正改变时才更新UI
            var hasChanges = !AreFileListsEqual(_workspaceFiles, files);
            if (hasChanges)
            {
                _workspaceFiles = files;
                
                // 重置可见节点计数
                _currentVisibleNodes = MaxVisibleNodes;
                
                // 清除懒加载缓存（因为文件结构已改变）
                _lazyLoadedChildren.Clear();
                
                // 更新可用文件夹列表
                UpdateAvailableFolders(files, "");
                
                // 恢复已展开文件夹的子节点
                RestoreExpandedFolderChildren(files);
                
                // 检测前端项目
                await DetectFrontendProjects();
                
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载工作区文件失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 恢复已展开文件夹的子节点
    /// </summary>
    private void RestoreExpandedFolderChildren(List<WorkspaceFileNode> nodes)
    {
        if (nodes == null || !_expandedFolders.Any())
        {
            return;
        }
        
        foreach (var node in nodes)
        {
            var normalizedPath = NormalizePath(node.Path);
            if (node.Type == "folder" && _expandedFolders.Contains(normalizedPath))
            {
                // 重新加载已展开文件夹的子节点
                LoadFolderChildren(node);
                
                // 递归处理子节点
                if (node.Children != null)
                {
                    RestoreExpandedFolderChildren(node.Children);
                }
            }
        }
    }

    private void UpdateAvailableFolders(List<WorkspaceFileNode> nodes, string parentPath)
    {
        _availableFolders.Clear();
        CollectFolders(nodes, parentPath);
    }

    private void CollectFolders(List<WorkspaceFileNode> nodes, string parentPath)
    {
        foreach (var node in nodes)
        {
            if (node.Type == "folder")
            {
                var folderPath = string.IsNullOrEmpty(parentPath) 
                    ? node.Name 
                    : $"{parentPath}/{node.Name}";
                
                _availableFolders.Add(folderPath);
                
                if (node.Children != null && node.Children.Any())
                {
                    CollectFolders(node.Children, folderPath);
                }
            }
        }
    }

    private List<WorkspaceFileNode> GetDirectoryStructure(string path, string rootPath, bool loadChildrenRecursively = false)
    {
        var result = new List<WorkspaceFileNode>();
        var folders = new List<WorkspaceFileNode>();
        var files = new List<WorkspaceFileNode>();

        try
        {
            // 获取所有子目录
            foreach (var dir in Directory.GetDirectories(path))
            {
                var dirInfo = new DirectoryInfo(dir);
                var relativePath = Path.GetRelativePath(rootPath, dir);
                
                // 跳过隐藏目录
                if (dirInfo.Name.StartsWith("."))
                    continue;

                // 统一使用正斜杠作为路径分隔符，避免Windows反斜杠导致的问题
                var normalizedPath = relativePath.Replace("\\", "/");
                
                var folderNode = new WorkspaceFileNode
                {
                    Name = dirInfo.Name,
                    Path = normalizedPath,
                    Type = "folder",
                    // 懒加载：只有loadChildrenRecursively=true时才递归加载子目录
                    Children = loadChildrenRecursively ? GetDirectoryStructure(dir, rootPath, true) : null
                };
                folders.Add(folderNode);
            }

            // 获取所有文件
            foreach (var file in Directory.GetFiles(path))
            {
                var fileInfo = new FileInfo(file);
                var relativePath = Path.GetRelativePath(rootPath, file);
                
                // 跳过隐藏文件
                if (fileInfo.Name.StartsWith("."))
                    continue;

                // 统一使用正斜杠作为路径分隔符
                var normalizedPath = relativePath.Replace("\\", "/");

                files.Add(new WorkspaceFileNode
                {
                    Name = fileInfo.Name,
                    Path = normalizedPath,
                    Type = "file",
                    Size = fileInfo.Length,
                    Extension = fileInfo.Extension,
                    IsHtml = fileInfo.Extension.Equals(".html", StringComparison.OrdinalIgnoreCase) ||
                             fileInfo.Extension.Equals(".htm", StringComparison.OrdinalIgnoreCase)
                });
            }

            // 先添加文件夹，再添加文件
            result.AddRange(folders.OrderBy(f => f.Name));
            result.AddRange(files.OrderBy(f => f.Name));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"读取目录结构失败: {path}, {ex.Message}");
        }

        return result;
    }

    private bool AreFileListsEqual(List<WorkspaceFileNode> list1, List<WorkspaceFileNode> list2)
    {
        if (list1.Count != list2.Count) return false;
        
        // 简单比较：只比较文件数量和路径
        var paths1 = GetAllFilePaths(list1).OrderBy(p => p).ToList();
        var paths2 = GetAllFilePaths(list2).OrderBy(p => p).ToList();
        
        return paths1.SequenceEqual(paths2);
    }

    private List<string> GetAllFilePaths(List<WorkspaceFileNode> nodes)
    {
        var paths = new List<string>();
        foreach (var node in nodes)
        {
            paths.Add(node.Path);
            if (node.Children != null && node.Children.Any())
            {
                paths.AddRange(GetAllFilePaths(node.Children));
            }
        }
        return paths;
    }

    private async Task OnFileNodeClick(WorkspaceFileNode node)
    {
        if (node.Type == "file")
        {
            if (node.IsHtml)
            {
                // 如果是HTML文件,加载并预览（切换到HTML预览标签页）
                _selectedHtmlFile = node.Path;
                _activeTabKey = "3"; // 切换到HTML预览Tab
                await LoadHtmlPreview(node.Path);
            }
            else
            {
                // 其他文件，打开预览模态框（和眼睛按钮效果一样）
                await PreviewFile(node);
            }
        }
    }

    private void ToggleFolder(WorkspaceFileNode node)
    {
        if (node.Type == "folder")
        {
            // 统一路径格式（使用正斜杠）
            var normalizedPath = node.Path.Replace("\\", "/");
            
            Console.WriteLine($"[ToggleFolder] ========= 点击事件 =========");
            Console.WriteLine($"[ToggleFolder] node对象HashCode: {node.GetHashCode()}");
            Console.WriteLine($"[ToggleFolder] node.Name: {node.Name}");
            Console.WriteLine($"[ToggleFolder] node.Path: {node.Path}");
            Console.WriteLine($"[ToggleFolder] normalizedPath: {normalizedPath}");
            
            if (_expandedFolders.Contains(normalizedPath))
            {
                Console.WriteLine($"[ToggleFolder] 折叠文件夹: {normalizedPath}");
                _expandedFolders.Remove(normalizedPath);
            }
            else
            {
                Console.WriteLine($"[ToggleFolder] 展开文件夹: {normalizedPath}");
                _expandedFolders.Add(normalizedPath);
                
                // 懒加载：如果子节点未加载或为空，则加载
                if ((node.Children == null || node.Children.Count == 0) && !string.IsNullOrEmpty(_sessionId))
                {
                    Console.WriteLine($"[ToggleFolder] 准备加载子节点，node.Path={normalizedPath}, sessionId={_sessionId}");
                    LoadFolderChildren(node);
                    Console.WriteLine($"[ToggleFolder] 加载完成，node.Children?.Count={node.Children?.Count ?? 0}");
                }
                else if (node.Children != null)
                {
                    Console.WriteLine($"[ToggleFolder] 子节点已存在，直接展开，count={node.Children.Count}");
                }
            }
            StateHasChanged();
        }
    }
    
    private void LoadFolderChildren(WorkspaceFileNode node)
    {
        try
        {
            Console.WriteLine($"[LoadFolderChildren] 开始加载文件夹: {node.Path}");
            
            // 如果已经从缓存加载过，直接使用缓存
            if (_lazyLoadedChildren.TryGetValue(node.Path, out var cachedChildren))
            {
                Console.WriteLine($"[LoadFolderChildren] 从缓存加载，子节点数: {cachedChildren.Count}");
                node.Children = cachedChildren;
                return;
            }
            
            // 使用服务获取工作区的绝对路径
            var workspaceRoot = CliExecutorService.GetSessionWorkspacePath(_sessionId);
            var fullPath = Path.Combine(workspaceRoot, node.Path);
            
            Console.WriteLine($"[LoadFolderChildren] 工作区根路径: {workspaceRoot}");
            Console.WriteLine($"[LoadFolderChildren] 完整路径: {fullPath}");
            Console.WriteLine($"[LoadFolderChildren] 目录是否存在: {Directory.Exists(fullPath)}");
            
            if (Directory.Exists(fullPath))
            {
                // 只加载一级子目录，不递归
                node.Children = GetDirectoryStructure(fullPath, workspaceRoot, false);
                _lazyLoadedChildren[node.Path] = node.Children;
                Console.WriteLine($"[LoadFolderChildren] 加载成功，子节点数: {node.Children?.Count ?? 0}");
            }
            else
            {
                Console.WriteLine($"[LoadFolderChildren] 目录不存在，设置空子节点列表");
                node.Children = new List<WorkspaceFileNode>();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LoadFolderChildren] 加载文件夹子节点失败: {ex.Message}");
            Console.WriteLine($"[LoadFolderChildren] 堆栈跟踪: {ex.StackTrace}");
            node.Children = new List<WorkspaceFileNode>();
        }
    }
    
    // 获取可见的节点（用于增量渲染）
    // 注意：只返回根级节点，子节点由 RenderFileNode 递归渲染
    private List<WorkspaceFileNode> GetVisibleNodes(List<WorkspaceFileNode> nodes)
    {
        // 只计算根级节点数量用于分页
        var rootCount = nodes.Count;
        
        _hasMoreNodes = rootCount > _currentVisibleNodes;
        
        // 只返回前 N 个根级节点
        return nodes.Take(_currentVisibleNodes).ToList();
    }
    
    private void AddVisibleNodesRecursive(List<WorkspaceFileNode> nodes, List<WorkspaceFileNode> result, ref int remaining)
    {
        foreach (var node in nodes)
        {
            if (remaining <= 0) break;
            
            result.Add(node);
            remaining--;
            
            // 如果文件夹展开且有子节点，递归添加
            var normalizedPath = NormalizePath(node.Path);
            if (node.Type == "folder" && _expandedFolders.Contains(normalizedPath) && node.Children != null)
            {
                AddVisibleNodesRecursive(node.Children, result, ref remaining);
            }
        }
    }
    
    private int CountAllNodes(List<WorkspaceFileNode> nodes)
    {
        // 只计算根级节点数量
        return nodes.Count;
    }
    
    private void LoadMoreNodes()
    {
        _currentVisibleNodes += MaxVisibleNodes;
        StateHasChanged();
    }
    
    private async Task OnFileTreeScroll()
    {
        // 可选：实现自动加载更多（当滚动到底部时）
        // 这里暂时留空，用户需要手动点击"加载更多"按钮
        await Task.CompletedTask;
    }

    private async Task RefreshHtmlPreview()
    {
        if (!string.IsNullOrEmpty(_selectedHtmlFile))
        {
            await LoadHtmlPreview(_selectedHtmlFile);
        }
    }

    private async Task OpenHtmlInNewWindow()
    {
        if (!string.IsNullOrEmpty(_htmlPreviewUrl))
        {
            try
            {
                await JSRuntime.InvokeVoidAsync("open", _htmlPreviewUrl, "_blank");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"打开新窗口失败: {ex.Message}");
            }
        }
    }

    private async Task LoadHtmlPreview(string filePath)
    {
        try
        {
            // 验证文件存在
            var workspacePath = CliExecutorService.GetSessionWorkspacePath(_sessionId);
            var fullPath = Path.Combine(workspacePath, filePath);

            // 安全检查:确保文件在工作区内
            var normalizedWorkspace = Path.GetFullPath(workspacePath);
            var normalizedFile = Path.GetFullPath(fullPath);
            
            if (!normalizedFile.StartsWith(normalizedWorkspace))
            {
                Console.WriteLine("无效的文件路径");
                return;
            }

            if (File.Exists(fullPath))
            {
                // 使用API URL代替直接读取内容，这样相对路径的CSS/JS可以正常加载
                // URL格式: /api/workspace/{sessionId}/files/{filePath}
                var encodedPath = Uri.EscapeDataString(filePath).Replace("%2F", "/");
                _htmlPreviewUrl = $"/api/workspace/{_sessionId}/files/{encodedPath}";
                
                // 添加时间戳参数强制刷新
                _htmlPreviewUrl += $"?_t={DateTime.Now.Ticks}";
                
                StateHasChanged();
                
                // 调整iframe高度
                await Task.Delay(100); // 等待DOM更新
                try
                {
                    await JSRuntime.InvokeVoidAsync("adjustIframeHeight");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"调整iframe高度失败: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载HTML文件失败: {ex.Message}");
        }
    }

    private async Task LoadFileContent(string filePath)
    {
        try
        {
            // 直接读取文件内容
            var workspacePath = CliExecutorService.GetSessionWorkspacePath(_sessionId);
            var fullPath = Path.Combine(workspacePath, filePath);

            // 安全检查:确保文件在工作区内
            var normalizedWorkspace = Path.GetFullPath(workspacePath);
            var normalizedFile = Path.GetFullPath(fullPath);
            
            if (!normalizedFile.StartsWith(normalizedWorkspace))
            {
                _rawOutput = "无效的文件路径";
                StateHasChanged();
                return;
            }

            if (File.Exists(fullPath))
            {
                var content = await File.ReadAllTextAsync(fullPath);
                _rawOutput = $"文件: {filePath}\n\n{content}";
                StateHasChanged();
            }
            else
            {
                _rawOutput = $"文件不存在: {filePath}";
                StateHasChanged();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载文件内容失败: {ex.Message}");
            _rawOutput = $"加载文件失败: {ex.Message}";
            StateHasChanged();
        }
    }

    private async Task OpenEnvConfig()
    {
        var selectedTool = _availableTools.FirstOrDefault(t => t.Id == _selectedToolId);
        if (selectedTool != null && _envConfigModal != null)
        {
            await _envConfigModal.ShowAsync(selectedTool);
        }
    }
    
    /// <summary>
    /// 切换上下文预览面板
    /// </summary>
    private async Task ToggleContextPanel()
    {
        if (_contextPreviewPanel != null)
        {
            if (_showContextPanel)
            {
                _contextPreviewPanel.Close();
                _showContextPanel = false;
            }
            else
            {
                // 构建上下文
                await ContextManagerService.BuildContextFromMessagesAsync(_sessionId, _messages);
                
                // 显示面板
                await _contextPreviewPanel.ShowAsync(_sessionId);
                _showContextPanel = true;
            }
            
            StateHasChanged();
        }
    }
    
    /// <summary>
    /// 上下文变更回调
    /// </summary>
    private async Task OnContextChanged()
    {
        // 当上下文发生变化时，可以在这里执行相关操作
        Console.WriteLine("[上下文管理] 上下文已更新");
        
        // 获取统计信息
        var stats = ContextManagerService.GetContextStatistics(_sessionId);
        Console.WriteLine($"[上下文管理] Token 使用: {stats.UsedTokens:N0} / {stats.TotalTokens:N0} ({stats.UsagePercentage:F1}%)");
        
        await Task.CompletedTask;
    }

    private async Task DownloadFile(WorkspaceFileNode node)
    {
        if (node.Type != "file") return;

        try
        {
            var fileBytes = CliExecutorService.GetWorkspaceFile(_sessionId, node.Path);
            if (fileBytes == null || fileBytes.Length == 0)
            {
                Console.WriteLine("文件为空或不存在");
                return;
            }

            // 转换为Base64并调用JavaScript下载
            var base64 = Convert.ToBase64String(fileBytes);
            var mimeType = GetMimeType(node.Extension);
            await JSRuntime.InvokeVoidAsync("downloadBase64File", node.Name, base64, mimeType);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"下载文件失败: {ex.Message}");
        }
    }

    private async Task DownloadAllFiles()
    {
        try
        {
            var zipBytes = CliExecutorService.GetWorkspaceZip(_sessionId);
            if (zipBytes == null || zipBytes.Length == 0)
            {
                Console.WriteLine("工作区为空或不存在");
                return;
            }

            // 转换为Base64并调用JavaScript下载
            var base64 = Convert.ToBase64String(zipBytes);
            var fileName = $"workspace_{_sessionId}_{DateTime.Now:yyyyMMddHHmmss}.zip";
            await JSRuntime.InvokeVoidAsync("downloadBase64File", fileName, base64, "application/zip");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"下载所有文件失败: {ex.Message}");
        }
    }

    private async Task PreviewFile(WorkspaceFileNode node)
    {
        if (node.Type != "file") return;

        try
        {
            // 读取文件内容
            var workspacePath = CliExecutorService.GetSessionWorkspacePath(_sessionId);
            var fullPath = Path.Combine(workspacePath, node.Path);

            // 安全检查：确保文件在工作区内
            var normalizedWorkspace = Path.GetFullPath(workspacePath);
            var normalizedFile = Path.GetFullPath(fullPath);
            
            if (!normalizedFile.StartsWith(normalizedWorkspace))
            {
                Console.WriteLine("无效的文件路径");
                return;
            }

            if (File.Exists(fullPath))
            {
                // 判断文件类型，决定是否读取文本内容
                var extension = Path.GetExtension(node.Name).ToLower();
                var isBinaryFile = IsBinaryFileExtension(extension);
                
                var fileBytes = await File.ReadAllBytesAsync(fullPath);
                var content = isBinaryFile ? string.Empty : await File.ReadAllTextAsync(fullPath);
                
                // 使用模态框显示，传递sessionId用于生成文件访问URL
                if (_codePreviewModal != null)
                {
                    await _codePreviewModal.ShowAsync(node.Name, node.Path, content, fileBytes, _sessionId);
                    StateHasChanged(); // 确保UI更新
                }
            }
            else
            {
                Console.WriteLine($"文件不存在: {fullPath}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"预览文件失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 判断是否为二进制文件扩展名
    /// </summary>
    private static bool IsBinaryFileExtension(string extension)
    {
        var binaryExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // Office文档
            ".doc", ".docx", ".docm", ".dotx", ".dotm", ".odt",
            ".xls", ".xlsx", ".xlsm", ".xlsb", ".xltx", ".xltm", ".ods",
            ".ppt", ".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm", ".odp",
            // PDF
            ".pdf",
            // 图片
            ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".tiff", ".tif",
            // 压缩文件
            ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2",
            // 可执行文件
            ".exe", ".dll", ".so", ".dylib",
            // 其他二进制
            ".bin", ".dat", ".db", ".sqlite", ".mdb"
        };
        
        return binaryExtensions.Contains(extension);
    }

    private string GetMimeType(string extension)
    {
        return extension.ToLower() switch
        {
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "text/javascript",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }

    private void ShowCreateFolderDialog()
    {
        _showCreateFolderDialog = true;
        _newFolderName = string.Empty;
        _createFolderError = string.Empty;
        StateHasChanged();
    }

    private void CloseCreateFolderDialog()
    {
        _showCreateFolderDialog = false;
        _newFolderName = string.Empty;
        _createFolderError = string.Empty;
        _isCreatingFolder = false;
        StateHasChanged();
    }

    private async Task HandleCreateFolderKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !string.IsNullOrWhiteSpace(_newFolderName))
        {
            await CreateFolder();
        }
        else if (e.Key == "Escape")
        {
            CloseCreateFolderDialog();
        }
    }

    private async Task CreateFolder()
    {
        if (string.IsNullOrWhiteSpace(_newFolderName) || _isCreatingFolder)
        {
            return;
        }

        try
        {
            _isCreatingFolder = true;
            _createFolderError = string.Empty;
            StateHasChanged();

            // 清理文件夹名称
            var folderPath = _newFolderName.Trim().Replace("\\", "/");

            // 验证文件夹名称
            if (folderPath.Contains("..") || folderPath.StartsWith("/") || folderPath.EndsWith("/"))
            {
                _createFolderError = "文件夹名称格式不正确";
                return;
            }

            var invalidChars = Path.GetInvalidPathChars();
            if (folderPath.Any(c => invalidChars.Contains(c)))
            {
                _createFolderError = "文件夹名称包含非法字符";
                return;
            }

            // 创建文件夹
            var success = await CliExecutorService.CreateFolderInWorkspaceAsync(_sessionId, folderPath);

            if (success)
            {
                Console.WriteLine($"文件夹创建成功: {folderPath}");
                
                // 刷新工作区文件列表
                await LoadWorkspaceFiles();
                
                // 关闭对话框
                CloseCreateFolderDialog();
            }
            else
            {
                _createFolderError = "创建文件夹失败，请稍后重试";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"创建文件夹失败: {ex.Message}");
            _createFolderError = $"创建失败: {ex.Message}";
        }
        finally
        {
            _isCreatingFolder = false;
            StateHasChanged();
        }
    }

    private async Task HandleFileUpload(InputFileChangeEventArgs e)
    {
        if (_isUploading) return;

        try
        {
            _isUploading = true;
            StateHasChanged();

            var file = e.File;
            
            // 检查文件大小
            if (file.Size > MaxFileSize)
            {
                Console.WriteLine($"文件太大: {file.Name} ({FormatFileSize(file.Size)}), 最大允许 {FormatFileSize(MaxFileSize)}");
                return;
            }

            // 使用流式读取,避免一次性加载整个文件到内存
            using var stream = file.OpenReadStream(MaxFileSize);
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream);
            var fileBytes = memoryStream.ToArray();

            // 确定上传路径（根据选中的文件夹）
            var uploadPath = string.IsNullOrEmpty(_selectedUploadFolder) ? null : _selectedUploadFolder;

            // 上传到工作区
            var success = await CliExecutorService.UploadFileToWorkspaceAsync(
                _sessionId, 
                file.Name, 
                fileBytes,
                uploadPath);

            if (success)
            {
                var location = string.IsNullOrEmpty(uploadPath) ? "根目录" : uploadPath;
                Console.WriteLine($"文件上传成功: {file.Name} ({FormatFileSize(file.Size)}) -> {location}");
                
                // 立即刷新工作区文件列表
                await LoadWorkspaceFiles();
                
                // 切换到工作区文件Tab
                _activeTabKey = "2";
                StateHasChanged();
            }
            else
            {
                Console.WriteLine($"文件上传失败: {file.Name}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"处理文件上传失败: {ex.Message}");
        }
        finally
        {
            _isUploading = false;
            StateHasChanged();
        }
    }

    private string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len = len / 1024;
        }
        return $"{len:0.##} {sizes[order]}";
    }

    private async Task HandleLogout()
    {
        try
        {
            // 清除会话存储
            await JSRuntime.InvokeVoidAsync("sessionStorage.removeItem", "isAuthenticated");
            await JSRuntime.InvokeVoidAsync("sessionStorage.removeItem", "username");
            
            // 跳转到登录页
            NavigationManager.NavigateTo("/login", forceLoad: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"退出登录失败: {ex.Message}");
        }
    }
    
    // 用户头像下拉菜单控制方法
    private void ToggleUserDropdown()
    {
        _showUserDropdown = !_showUserDropdown;
        StateHasChanged();
    }
    
    private void CloseUserDropdown()
    {
        _showUserDropdown = false;
        StateHasChanged();
    }
    
    private void OpenEnvConfigFromDropdown()
    {
        _showUserDropdown = false;
        OpenEnvConfig();
    }
    
    private async Task HandleLogoutFromDropdown()
    {
        _showUserDropdown = false;
        await HandleLogout();
    }
    
    private async Task OnLanguageChangedFromDropdown(string languageCode)
    {
        _showUserDropdown = false;
        await OnLanguageChanged(languageCode);
    }

    private void TogglePreviewPanel()
    {
        _isPreviewCollapsed = !_isPreviewCollapsed;
        StateHasChanged();
    }

    private void ShowDeleteConfirmDialog(WorkspaceFileNode node)
    {
        _nodeToDelete = node;
        _showDeleteConfirmDialog = true;
        _deleteError = string.Empty;
        StateHasChanged();
    }

    private void CloseDeleteConfirmDialog()
    {
        _showDeleteConfirmDialog = false;
        _nodeToDelete = null;
        _deleteError = string.Empty;
        _isDeleting = false;
        StateHasChanged();
    }

    private async Task DeleteWorkspaceItem()
    {
        if (_nodeToDelete == null || _isDeleting)
        {
            return;
        }

        try
        {
            _isDeleting = true;
            _deleteError = string.Empty;
            StateHasChanged();

            var isDirectory = _nodeToDelete.Type == "folder";
            var success = await CliExecutorService.DeleteWorkspaceItemAsync(
                _sessionId, 
                _nodeToDelete.Path, 
                isDirectory);

            if (success)
            {
                var itemType = isDirectory ? "文件夹" : "文件";
                Console.WriteLine($"{itemType}删除成功: {_nodeToDelete.Path}");

                // 如果删除的是当前预览的HTML文件，清空预览
                if (!isDirectory && _selectedHtmlFile == _nodeToDelete.Path)
                {
                    _selectedHtmlFile = string.Empty;
                    _htmlPreviewUrl = string.Empty;
                }

                // 刷新工作区文件列表
                await LoadWorkspaceFiles();

                // 关闭对话框
                CloseDeleteConfirmDialog();
            }
            else
            {
                _deleteError = "删除失败，请稍后重试";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"删除失败: {ex.Message}");
            _deleteError = $"删除失败: {ex.Message}";
        }
        finally
        {
            _isDeleting = false;
            StateHasChanged();
        }
    }

    private sealed class JsonlUsageDetail
    {
        public long? InputTokens { get; set; }
        public long? CachedInputTokens { get; set; }
        public long? OutputTokens { get; set; }

        public long? TotalTokens
        {
            get
            {
                long total = 0;
                var hasValue = false;
                if (InputTokens.HasValue)
                {
                    total += InputTokens.Value;
                    hasValue = true;
                }
                if (CachedInputTokens.HasValue)
                {
                    total += CachedInputTokens.Value;
                    hasValue = true;
                }
                if (OutputTokens.HasValue)
                {
                    total += OutputTokens.Value;
                    hasValue = true;
                }
                return hasValue ? total : null;
            }
        }
    }

    private sealed class JsonlDisplayItem
    {
        public string Type { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string? ItemType { get; set; }
        public JsonlUsageDetail? Usage { get; set; }
        public bool IsUnknown { get; set; }
    }

    private sealed class JsonlEventGroup
    {
        public string Id { get; set; } = string.Empty;
        public string Kind { get; set; } = string.Empty; // "command_execution" | "tool_call" | "single"
        public string Title { get; set; } = string.Empty;
        public bool IsCompleted { get; set; }
        public bool IsCollapsible { get; set; }
        public List<JsonlDisplayItem> Items { get; } = new();
    }

    private bool IsJsonlGroupOpen(JsonlEventGroup group)
    {
        if (_jsonlGroupOpenState.TryGetValue(group.Id, out var open))
        {
            return open;
        }

        // 默认行为：执行中展开，完成后折叠（可手动展开）
        return !group.IsCompleted;
    }

    private void ToggleJsonlGroup(string groupId, bool defaultOpen)
    {
        // 如果之前未设置状态，则从默认状态开始反转
        var current = _jsonlGroupOpenState.TryGetValue(groupId, out var open) ? open : defaultOpen;
        _jsonlGroupOpenState[groupId] = !current;
        StateHasChanged();
    }

    private static string ExtractFirstLine(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return lines.Length > 0 ? lines[0] : string.Empty;
    }

    private static string? ExtractLineValue(IEnumerable<JsonlDisplayItem> items, string prefix)
    {
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.Content))
            {
                continue;
            }

            var lines = item.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var line in lines)
            {
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return line.Substring(prefix.Length).Trim();
                }
            }
        }
        return null;
    }

    private static bool IsCodexCommandExecutionEvent(JsonlDisplayItem evt)
    {
        // Codex: item.started/item.updated/item.completed + ItemType=command_execution
        return (evt.Type == "item.started" || evt.Type == "item.updated" || evt.Type == "item.completed")
               && string.Equals(evt.ItemType, "command_execution", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsClaudeToolEvent(JsonlDisplayItem evt)
    {
        // Claude Code: tool_use / tool_result（EventType）
        // 但如果适配器已将其标记为 todo_list（例如 TodoWrite），则以“单条卡片”展示，避免被折叠进工具调用分组。
        if (string.Equals(evt.ItemType, "todo_list", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return evt.Type == "tool_use" || evt.Type == "tool_result";
    }

    private List<JsonlEventGroup> GetJsonlEventGroups()
    {
        // 将“命令执行（Codex）”与“工具调用（Claude Code）”聚合为一个可折叠气泡
        var groups = new List<JsonlEventGroup>();

        JsonlEventGroup? activeCommandGroup = null;
        JsonlEventGroup? activeToolGroup = null;

        for (var i = 0; i < _jsonlEvents.Count; i++)
        {
            var evt = _jsonlEvents[i];

            // 1) Codex 命令执行分组：item.started -> item.updated... -> item.completed
            if (IsCodexCommandExecutionEvent(evt))
            {
                if (evt.Type == "item.started")
                {
                    // 如果上一个命令执行未正常闭合，先结束它（避免异常输出导致串组）
                    if (activeCommandGroup != null && !activeCommandGroup.IsCompleted)
                    {
                        activeCommandGroup.IsCompleted = true;
                    }

                    activeCommandGroup = new JsonlEventGroup
                    {
                        Id = $"cmd-{i}",
                        Kind = "command_execution",
                        IsCollapsible = true,
                        IsCompleted = false
                    };
                    activeCommandGroup.Items.Add(evt);
                    activeCommandGroup.Title = BuildGroupTitle(activeCommandGroup);
                    groups.Add(activeCommandGroup);
                    continue;
                }

                if (activeCommandGroup != null)
                {
                    activeCommandGroup.Items.Add(evt);
                    if (evt.Type == "item.completed")
                    {
                        activeCommandGroup.IsCompleted = true;
                        activeCommandGroup.Title = BuildGroupTitle(activeCommandGroup);
                        activeCommandGroup = null;
                    }
                    else
                    {
                        activeCommandGroup.Title = BuildGroupTitle(activeCommandGroup);
                    }
                    continue;
                }

                // 没有 active group 兜底：作为单条显示
                groups.Add(new JsonlEventGroup
                {
                    Id = $"evt-{i}",
                    Kind = "single",
                    IsCollapsible = false,
                    IsCompleted = true,
                    Title = evt.Title,
                    Items = { evt }
                });
                continue;
            }

            // 2) Claude Code 工具调用分组：tool_use -> tool_result（可能中间夹杂 raw/tool_result 等）
            if (IsClaudeToolEvent(evt))
            {
                if (evt.Type == "tool_use")
                {
                    // 如果上一个 tool_use 未闭合，先结束（避免串组）
                    if (activeToolGroup != null && !activeToolGroup.IsCompleted)
                    {
                        activeToolGroup.IsCompleted = true;
                    }

                    activeToolGroup = new JsonlEventGroup
                    {
                        Id = $"tool-{i}",
                        Kind = "tool_call",
                        IsCollapsible = true,
                        IsCompleted = false
                    };
                    activeToolGroup.Items.Add(evt);
                    activeToolGroup.Title = BuildGroupTitle(activeToolGroup);
                    groups.Add(activeToolGroup);
                    continue;
                }

                if (activeToolGroup != null)
                {
                    activeToolGroup.Items.Add(evt);
                    if (evt.Type == "tool_result")
                    {
                        activeToolGroup.IsCompleted = true;
                        activeToolGroup.Title = BuildGroupTitle(activeToolGroup);
                        activeToolGroup = null;
                    }
                    else
                    {
                        activeToolGroup.Title = BuildGroupTitle(activeToolGroup);
                    }
                    continue;
                }

                // 没有 active group 兜底：作为单条显示
                groups.Add(new JsonlEventGroup
                {
                    Id = $"evt-{i}",
                    Kind = "single",
                    IsCollapsible = false,
                    IsCompleted = true,
                    Title = evt.Title,
                    Items = { evt }
                });
                continue;
            }

            // 3) 其他事件：保持现状（单条卡片）
            groups.Add(new JsonlEventGroup
            {
                Id = $"evt-{i}",
                Kind = "single",
                IsCollapsible = false,
                IsCompleted = true,
                Title = evt.Title,
                Items = { evt }
            });
        }

        return groups;
    }

    /// <summary>
    /// 获取分页的JSONL事件组（用于懒加载）
    /// </summary>
    private List<JsonlEventGroup> GetPagedJsonlEventGroups()
    {
        var allGroups = GetJsonlEventGroups();

        // 目标：初始显示最新 N 条（在底部），点击“加载更多”时把更早的记录补到上方
        // GetJsonlEventGroups 默认是按产生顺序（旧 -> 新）。这里取最后 N 条即可。
        var takeCount = Math.Min(_displayedEventCount, allGroups.Count);
        var skipCount = Math.Max(0, allGroups.Count - takeCount);
        return allGroups.Skip(skipCount).Take(takeCount).ToList();
    }

    /// <summary>
    /// 加载更多JSONL事件
    /// </summary>
    private async Task LoadMoreEvents()
    {
        if (!_hasMoreEvents)
        {
            return;
        }

        try
        {
            _outputScrollBeforePrepend = await JSRuntime.InvokeAsync<ScrollInfo>("getScrollInfo", "output-container");
        }
        catch
        {
            _outputScrollBeforePrepend = new ScrollInfo();
        }

        _displayedEventCount += LoadMoreCount;
        _pendingOutputPrependScrollAdjust = true;
        StateHasChanged();
    }

    /// <summary>
    /// 重置事件显示计数（在新会话或清空时调用）
    /// </summary>
    private void ResetEventDisplayCount()
    {
        _displayedEventCount = InitialDisplayCount;
    }

    private string BuildGroupTitle(JsonlEventGroup group)
    {
        if (group.Kind == "command_execution")
        {
            // 优先取 “命令:” 行，取不到就退化到第一行
            var cmd = ExtractLineValue(group.Items, "命令:");
            if (!string.IsNullOrWhiteSpace(cmd))
            {
                return T("cliEvent.group.withName",
                    ("group", T("cliEvent.group.commandExecution")),
                    ("name", cmd));
            }

            var first = ExtractFirstLine(group.Items.FirstOrDefault()?.Content);
            return string.IsNullOrWhiteSpace(first)
                ? T("cliEvent.group.commandExecution")
                : T("cliEvent.group.withName",
                    ("group", T("cliEvent.group.commandExecution")),
                    ("name", first));
        }

        if (group.Kind == "tool_call")
        {
            // 优先取 “工具:” 行
            var tool = ExtractLineValue(group.Items, "工具:");
            if (!string.IsNullOrWhiteSpace(tool))
            {
                return T("cliEvent.group.withName",
                    ("group", T("cliEvent.group.toolCall")),
                    ("name", tool));
            }

            var first = ExtractFirstLine(group.Items.FirstOrDefault()?.Content);
            return string.IsNullOrWhiteSpace(first)
                ? T("cliEvent.group.toolCall")
                : T("cliEvent.group.withName",
                    ("group", T("cliEvent.group.toolCall")),
                    ("name", first));
        }

        return group.Items.FirstOrDefault() is { } firstItem
            ? GetEventDisplayTitle(firstItem)
            : T("cliEvent.badge.event");
    }

    // 会话历史管理方法
    
    /// <summary>
    /// 加载会话列表（仅加载元数据，不加载消息内容）
    /// </summary>
    private async Task LoadSessionsAsync()
    {
        if (_isLoadingSessions)
        {
            return;
        }

        try
        {
            _isLoadingSessions = true;
            StateHasChanged();

            var startTime = DateTime.Now;

            // 使用 SessionHistoryManager 加载会话
            Console.WriteLine("[加载会话] 开始加载会话列表");
            _sessions = await SessionHistoryManager.LoadSessionsAsync();

            // 更新工作区有效性标记，但不自动删除历史记录
            foreach (var session in _sessions)
            {
                session.IsWorkspaceValid = SessionHistoryManager.ValidateWorkspacePath(session.WorkspacePath);
            }

            var invalidCount = _sessions.Count(s => !s.IsWorkspaceValid);
            if (invalidCount > 0)
            {
                Console.WriteLine($"[加载会话] 检测到 {invalidCount} 个工作区已失效的会话，将保留记录并在打开时尝试自动修复");
            }

            // 按更新时间降序排列
            _sessions = _sessions.OrderByDescending(s => s.UpdatedAt).ToList();

            var loadTime = (DateTime.Now - startTime).TotalMilliseconds;
            Console.WriteLine($"[加载会话] 会话列表加载完成，耗时: {loadTime:F2}ms，共 {_sessions.Count} 个会话");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[加载会话] 加载会话列表失败: {ex.Message}");
            Console.WriteLine($"[加载会话] 错误堆栈: {ex.StackTrace}");
            _sessions = new List<SessionHistory>();
        }
        finally
        {
            _isLoadingSessions = false;
            StateHasChanged();
        }
    }
    
    /// <summary>
    /// 保存当前会话
    /// </summary>
    private async Task SaveCurrentSessionAsync()
    {
        try
        {
            Console.WriteLine($"[保存会话] 开始保存会话，消息数量: {_messages.Count}");
            
            // 如果没有消息，不保存
            if (!_messages.Any())
            {
                Console.WriteLine("[保存会话] 没有消息，跳过保存");
                QueueSaveOutputState(forceImmediate: true);
                return;
            }
            
            // 获取工作区路径
            var workspacePath = CliExecutorService.GetSessionWorkspacePath(_sessionId);
            Console.WriteLine($"[保存会话] 工作区路径: {workspacePath}");
            
            // 如果当前会话不存在，创建新会话
            if (_currentSession == null)
            {
                _currentSession = new SessionHistory
                {
                    SessionId = _sessionId,
                    Title = "新会话",
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                    WorkspacePath = workspacePath,
                    ToolId = _selectedToolId,
                    Messages = new List<ChatMessage>(_messages),
                    IsWorkspaceValid = true
                };
                
                // 如果有用户消息，生成标题
                var firstUserMessage = _messages.FirstOrDefault(m => m.Role == "user");
                if (firstUserMessage != null)
                {
                    _currentSession.Title = SessionHistoryManager.GenerateSessionTitle(firstUserMessage.Content);
                }
            }
            else
            {
                // 更新现有会话
                _currentSession.Messages = new List<ChatMessage>(_messages);
                _currentSession.UpdatedAt = DateTime.Now;
                _currentSession.ToolId = _selectedToolId;
                _currentSession.WorkspacePath = workspacePath;
                
                // 如果标题仍然是"新会话"且有用户消息，自动生成标题
                if (_currentSession.Title == "新会话")
                {
                    var firstUserMessage = _messages.FirstOrDefault(m => m.Role == "user");
                    if (firstUserMessage != null)
                    {
                        _currentSession.Title = SessionHistoryManager.GenerateSessionTitle(firstUserMessage.Content);
                    }
                }
            }
            
            // 立即保存会话（不使用防抖）
            Console.WriteLine($"[保存会话] 准备立即保存会话: {_currentSession.SessionId}, 标题: {_currentSession.Title}, 消息数: {_currentSession.Messages.Count}");
            await SessionHistoryManager.SaveSessionImmediateAsync(_currentSession);
            Console.WriteLine($"[保存会话] 会话保存完成");

            // 会话保存完成后，立即同步保存输出结果区域状态
            QueueSaveOutputState(forceImmediate: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[保存会话] 保存当前会话失败: {ex.Message}");
            Console.WriteLine($"[保存会话] 错误堆栈: {ex.StackTrace}");
        }
    }
    
    /// <summary>
    /// 创建新会话
    /// </summary>
    private async Task CreateNewSessionAsync()
    {
        try
        {
            // 生成新的会话ID
            _sessionId = Guid.NewGuid().ToString();
            
            // 清空当前会话
            _currentSession = null;
            _messages.Clear();
            _currentAssistantMessage = string.Empty;
            _rawOutput = string.Empty;
            _jsonlEvents.Clear();
            _jsonlPendingBuffer = string.Empty;
            _activeThreadId = string.Empty;
            _isJsonlOutputActive = false;
            _jsonlAssistantMessageBuilder = null;
            
            // 清空工作区文件
            _workspaceFiles.Clear();
            _selectedHtmlFile = string.Empty;
            _htmlPreviewUrl = string.Empty;
            
            // 创建新的工作区目录
            var workspacePath = CliExecutorService.GetSessionWorkspacePath(_sessionId);
            if (!Directory.Exists(workspacePath))
            {
                Directory.CreateDirectory(workspacePath);
            }
            
            // 创建新的会话对象
            _currentSession = new SessionHistory
            {
                SessionId = _sessionId,
                Title = "新会话",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                WorkspacePath = workspacePath,
                ToolId = _selectedToolId,
                Messages = new List<ChatMessage>(),
                IsWorkspaceValid = true
            };
            
            // 自动保存新会话到 localStorage
            await SessionHistoryManager.SaveSessionImmediateAsync(_currentSession);
            
            // 重新加载会话列表
            await LoadSessionsAsync();
            
            Console.WriteLine($"✓ 已创建新会话: {_sessionId}");
            Console.WriteLine($"  - 工作区路径: {workspacePath}");
            Console.WriteLine($"  - CLI 工具: {_selectedToolId}");
            
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"创建新会话失败: {ex.Message}");
            Console.WriteLine($"错误详情: {ex.StackTrace}");
        }
    }
    
    /// <summary>
    /// 切换会话列表显示状态
    /// </summary>
    private void ToggleSessionList()
    {
        _showSessionList = !_showSessionList;
        StateHasChanged();
    }
    
    /// <summary>
    /// 创建新会话（按钮点击）
    /// </summary>
    private async Task CreateNewSession()
    {
        await CreateNewSessionAsync();
        _showSessionList = false;
        StateHasChanged();
    }
    
    /// <summary>
    /// 加载会话
    /// </summary>
    private async Task LoadSession(string sessionId)
    {
        if (_isLoadingSession)
        {
            return;
        }

        try
        {
            _isLoadingSession = true;
            StateHasChanged();

            var startTime = DateTime.Now;

            // 获取会话数据
            var session = await SessionHistoryManager.GetSessionAsync(sessionId);
            if (session == null)
            {
                Console.WriteLine($"会话不存在: {sessionId}");
                return;
            }
            
            // 验证工作区路径是否存在，不存在时尝试自动修复
            session.IsWorkspaceValid = SessionHistoryManager.ValidateWorkspacePath(session.WorkspacePath);
            
            if (!session.IsWorkspaceValid)
            {
                Console.WriteLine($"⚠️ 警告: 会话 '{session.Title}' 的工作区已被清理，尝试自动重建...");
                var restored = await TryRestoreWorkspaceAsync(session);
                if (!restored)
                {
                    Console.WriteLine($"   自动重建失败，部分功能可能不可用。原路径: {session.WorkspacePath}");
                }
            }
            
            // 加载会话数据
            _sessionId = session.SessionId;
            _currentSession = session;
            _messages = new List<ChatMessage>(session.Messages);
            
            // 恢复 CLI 工具选择（只有当工具在可用列表中时才恢复）
            if (!string.IsNullOrEmpty(session.ToolId) && 
                _availableTools.Any(t => t.Id == session.ToolId))
            {
                _selectedToolId = session.ToolId;
            }
            else if (_availableTools.Any() && string.IsNullOrEmpty(_selectedToolId))
            {
                // 如果会话的工具不可用或未选择，默认选择第一个可用工具
                _selectedToolId = _availableTools.First().Id;
            }
            
            // 清空输出区域和其他会话状态
            _rawOutput = string.Empty;
            _jsonlEvents.Clear();
            _jsonlPendingBuffer = string.Empty;
            _activeThreadId = string.Empty;
            _isJsonlOutputActive = false;
            _jsonlAssistantMessageBuilder = null;
            _currentAssistantMessage = string.Empty;

            // 从 IndexedDB 恢复输出结果区域（如果存在）
            await LoadOutputStateAsync(_sessionId);
            
            // 关闭会话列表（立即关闭以提升响应速度）
            _showSessionList = false;
            
            // 更新UI（第一次更新，显示消息）
            StateHasChanged();
            
            // 加载工作区文件（同步执行确保UI正确显示）
            try
            {
                if (session.IsWorkspaceValid)
                {
                    await LoadWorkspaceFiles();
                }
                else
                {
                    // 工作区无效，清空文件列表
                    _workspaceFiles.Clear();
                    _selectedHtmlFile = string.Empty;
                    _htmlPreviewUrl = string.Empty;
                }
            }
            catch (Exception fileEx)
            {
                Console.WriteLine($"加载工作区文件失败: {fileEx.Message}");
            }
            
            // 自动滚动聊天区域到最新消息
            await Task.Delay(50); // 减少等待时间
            try
            {
                await JSRuntime.InvokeVoidAsync("scrollChatToBottom");
            }
            catch (JSDisconnectedException)
            {
                // 连接已断开，忽略
            }
            catch (Exception scrollEx)
            {
                Console.WriteLine($"滚动到最新消息失败: {scrollEx.Message}");
            }
            
            var loadTime = (DateTime.Now - startTime).TotalMilliseconds;
            Console.WriteLine($"✓ 会话已恢复: {session.Title} (ID: {sessionId})，耗时: {loadTime:F2}ms");
            Console.WriteLine($"  - 消息数量: {_messages.Count}");
            Console.WriteLine($"  - CLI 工具: {_selectedToolId}");
            Console.WriteLine($"  - 工作区状态: {(session.IsWorkspaceValid ? "有效" : "无效")}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载会话失败: {ex.Message}");
            Console.WriteLine($"错误详情: {ex.StackTrace}");
        }
        finally
        {
            _isLoadingSession = false;
            StateHasChanged();
        }
    }
    
    /// <summary>
    /// 显示分享对话框
    /// </summary>
    private async Task ShowShareDialog(SessionHistory session)
    {
        if (_shareSessionModal != null)
        {
            await _shareSessionModal.ShowAsync(session.SessionId);
        }
    }
    
    /// <summary>
    /// 显示删除确认对话框
    /// </summary>
    private void ShowDeleteDialog(SessionHistory session)
    {
        _sessionToDelete = session;
        _showSessionDeleteDialog = true;
        _sessionDeleteError = string.Empty;
        StateHasChanged();
    }
    
    /// <summary>
    /// 关闭会话删除确认对话框
    /// </summary>
    private void CloseSessionDeleteDialog()
    {
        _showSessionDeleteDialog = false;
        _sessionToDelete = null;
        _sessionDeleteError = string.Empty;
        _isDeletingSession = false;
        StateHasChanged();
    }
    
    /// <summary>
    /// 删除会话
    /// </summary>
    private async Task DeleteSession()
    {
        if (_sessionToDelete == null || _isDeletingSession)
        {
            return;
        }

        try
        {
            _isDeletingSession = true;
            _sessionDeleteError = string.Empty;
            StateHasChanged();

            var deletedSessionId = _sessionToDelete.SessionId;
            var deletedSessionTitle = _sessionToDelete.Title;
            var deletedCurrentSession = _currentSession?.SessionId == deletedSessionId;

            // 调用 SessionHistoryManager 删除会话数据
            await SessionHistoryManager.DeleteSessionAsync(deletedSessionId);

            // 删除输出结果区域持久化数据
            await DeleteOutputStateAsync(deletedSessionId);
            
            // 清理工作区目录
            try
            {
                // 先停止该会话可能启动的开发服务器，避免占用文件导致删除失败
                try
                {
                    await DevServerManager.StopAllSessionServersAsync(deletedSessionId);
                }
                catch (Exception stopServerEx)
                {
                    Console.WriteLine($"⚠️ 停止开发服务器失败: {stopServerEx.Message}");
                }

                CliExecutorService.CleanupSessionWorkspace(deletedSessionId);
                Console.WriteLine($"✓ 已清理工作区: {deletedSessionId}");
            }
            catch (Exception cleanupEx)
            {
                Console.WriteLine($"⚠️ 清理工作区失败: {cleanupEx.Message}");
                // 工作区清理失败不影响会话删除
            }
            
            Console.WriteLine($"✓ 会话已删除: {deletedSessionTitle} (ID: {deletedSessionId})");

            // 重新加载会话列表，保证 UI 与存储同步
            await LoadSessionsAsync();

            if (deletedCurrentSession)
            {
                if (_sessions.Any())
                {
                    var nextSessionId = _sessions.First().SessionId;
                    Console.WriteLine($"删除当前会话后，自动恢复最近会话: {nextSessionId}");
                    await LoadSession(nextSessionId);
                }
                else
                {
                    Console.WriteLine("删除后没有剩余会话，自动创建空白会话");
                    await CreateNewSessionAsync();
                }
            }

            // 关闭对话框
            CloseSessionDeleteDialog();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"删除会话失败: {ex.Message}");
            _sessionDeleteError = $"删除失败: {ex.Message}";
        }
        finally
        {
            _isDeletingSession = false;
            StateHasChanged();
        }
    }
    
    #region 会话多选批量删除
    
    /// <summary>
    /// 切换多选模式
    /// </summary>
    private void ToggleSessionMultiSelectMode()
    {
        _isSessionMultiSelectMode = !_isSessionMultiSelectMode;
        if (!_isSessionMultiSelectMode)
        {
            // 退出多选模式时清空选择
            _selectedSessionIds.Clear();
        }
        StateHasChanged();
    }
    
    /// <summary>
    /// 切换单个会话的选择状态
    /// </summary>
    private void ToggleSessionSelection(string sessionId)
    {
        if (_selectedSessionIds.Contains(sessionId))
        {
            _selectedSessionIds.Remove(sessionId);
        }
        else
        {
            _selectedSessionIds.Add(sessionId);
        }
        StateHasChanged();
    }
    
    /// <summary>
    /// 全选/取消全选会话
    /// </summary>
    private void ToggleSelectAllSessions()
    {
        if (_selectedSessionIds.Count == _sessions.Count)
        {
            // 全部已选中，则取消全选
            _selectedSessionIds.Clear();
        }
        else
        {
            // 全选
            _selectedSessionIds = _sessions.Select(s => s.SessionId).ToHashSet();
        }
        StateHasChanged();
    }
    
    /// <summary>
    /// 显示批量删除确认对话框
    /// </summary>
    private void ShowBatchSessionDeleteDialog()
    {
        if (_selectedSessionIds.Count == 0)
        {
            return;
        }
        _showBatchSessionDeleteDialog = true;
        _batchSessionDeleteError = string.Empty;
        StateHasChanged();
    }
    
    /// <summary>
    /// 关闭批量删除确认对话框
    /// </summary>
    private void CloseBatchSessionDeleteDialog()
    {
        _showBatchSessionDeleteDialog = false;
        _batchSessionDeleteError = string.Empty;
        _isDeletingBatchSessions = false;
        StateHasChanged();
    }
    
    /// <summary>
    /// 批量删除选中的会话
    /// </summary>
    private async Task DeleteSelectedSessions()
    {
        if (_selectedSessionIds.Count == 0 || _isDeletingBatchSessions)
        {
            return;
        }

        try
        {
            _isDeletingBatchSessions = true;
            _batchSessionDeleteError = string.Empty;
            StateHasChanged();

            var sessionIdsToDelete = _selectedSessionIds.ToList();
            var deletedCurrentSession = _currentSession != null && sessionIdsToDelete.Contains(_currentSession.SessionId);
            var deletedCount = 0;

            foreach (var sessionId in sessionIdsToDelete)
            {
                try
                {
                    // 调用 SessionHistoryManager 删除会话数据
                    await SessionHistoryManager.DeleteSessionAsync(sessionId);

                    // 删除输出结果区域持久化数据
                    await DeleteOutputStateAsync(sessionId);
                    
                    // 清理工作区目录
                    try
                    {
                        // 先停止该会话可能启动的开发服务器
                        try
                        {
                            await DevServerManager.StopAllSessionServersAsync(sessionId);
                        }
                        catch (Exception stopServerEx)
                        {
                            Console.WriteLine($"⚠️ 停止开发服务器失败: {stopServerEx.Message}");
                        }

                        CliExecutorService.CleanupSessionWorkspace(sessionId);
                        Console.WriteLine($"✓ 已清理工作区: {sessionId}");
                    }
                    catch (Exception cleanupEx)
                    {
                        Console.WriteLine($"⚠️ 清理工作区失败: {cleanupEx.Message}");
                    }
                    
                    deletedCount++;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"删除会话失败 {sessionId}: {ex.Message}");
                }
            }
            
            Console.WriteLine($"✓ 批量删除完成: 成功删除 {deletedCount}/{sessionIdsToDelete.Count} 个会话");

            // 清空选择并退出多选模式
            _selectedSessionIds.Clear();
            _isSessionMultiSelectMode = false;

            // 重新加载会话列表
            await LoadSessionsAsync();

            if (deletedCurrentSession)
            {
                if (_sessions.Any())
                {
                    var nextSessionId = _sessions.First().SessionId;
                    Console.WriteLine($"删除当前会话后，自动恢复最近会话: {nextSessionId}");
                    await LoadSession(nextSessionId);
                }
                else
                {
                    Console.WriteLine("删除后没有剩余会话，自动创建空白会话");
                    await CreateNewSessionAsync();
                }
            }

            // 关闭对话框
            CloseBatchSessionDeleteDialog();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"批量删除会话失败: {ex.Message}");
            _batchSessionDeleteError = $"删除失败: {ex.Message}";
        }
        finally
        {
            _isDeletingBatchSessions = false;
            StateHasChanged();
        }
    }
    
    /// <summary>
    /// 获取选中的会话列表
    /// </summary>
    private List<SessionHistory> GetSelectedSessions()
    {
        return _sessions.Where(s => _selectedSessionIds.Contains(s.SessionId)).ToList();
    }
    
    #endregion
    
    /// <summary>
    /// 显示重命名对话框
    /// </summary>
    private void ShowRenameDialog(SessionHistory session)
    {
        _sessionToRename = session;
        _newSessionTitle = session.Title;
        _showRenameDialog = true;
        _renameError = string.Empty;
        StateHasChanged();
    }
    
    /// <summary>
    /// 关闭重命名对话框
    /// </summary>
    private void CloseRenameDialog()
    {
        _showRenameDialog = false;
        _sessionToRename = null;
        _newSessionTitle = string.Empty;
        _renameError = string.Empty;
        _isRenamingSession = false;
        StateHasChanged();
    }
    
    /// <summary>
    /// 处理重命名输入框的键盘事件
    /// </summary>
    private async Task HandleRenameKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Enter" && !string.IsNullOrWhiteSpace(_newSessionTitle))
        {
            await RenameSession();
        }
        else if (e.Key == "Escape")
        {
            CloseRenameDialog();
        }
    }
    
    /// <summary>
    /// 重命名会话
    /// </summary>
    private async Task RenameSession()
    {
        if (_sessionToRename == null || _isRenamingSession)
        {
            return;
        }

        try
        {
            _isRenamingSession = true;
            _renameError = string.Empty;
            StateHasChanged();

            // 验证标题
            var newTitle = _newSessionTitle.Trim();
            if (string.IsNullOrWhiteSpace(newTitle))
            {
                _renameError = "标题不能为空";
                return;
            }

            // 限制标题长度
            const int MaxTitleLength = 100;
            if (newTitle.Length > MaxTitleLength)
            {
                _renameError = $"标题长度不能超过 {MaxTitleLength} 个字符";
                return;
            }

            // 更新标题
            _sessionToRename.Title = newTitle;
            _sessionToRename.UpdatedAt = DateTime.Now;

            // 保存到 localStorage
            await SessionHistoryManager.SaveSessionImmediateAsync(_sessionToRename);

            // 如果重命名的是当前会话，更新当前会话对象
            if (_currentSession?.SessionId == _sessionToRename.SessionId)
            {
                _currentSession.Title = newTitle;
            }

            // 重新加载会话列表
            await LoadSessionsAsync();

            Console.WriteLine($"✓ 会话已重命名: {newTitle} (ID: {_sessionToRename.SessionId})");

            // 关闭对话框
            CloseRenameDialog();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"重命名会话失败: {ex.Message}");
            _renameError = $"重命名失败: {ex.Message}";
        }
        finally
        {
            _isRenamingSession = false;
            StateHasChanged();
        }
    }
    
    /// <summary>
    /// 获取会话项的CSS类
    /// </summary>
    private string GetSessionItemClass(SessionHistory session)
    {
        var baseClass = "flex items-center gap-3 p-3 rounded-lg cursor-pointer transition-all";
        var isActive = _currentSession?.SessionId == session.SessionId;
        
        if (isActive)
        {
            return $"{baseClass} bg-gradient-to-r from-primary-100 to-secondary-100 border-2 border-primary-400 shadow-md";
        }
        else
        {
            return $"{baseClass} hover:bg-gray-50 border border-gray-200 hover:border-primary-300 hover:shadow-sm";
        }
    }
    
    /// <summary>
    /// 格式化日期时间
    /// </summary>
    private string FormatDateTime(DateTime dateTime)
    {
        var now = DateTime.Now;
        var diff = now - dateTime;
        
        if (diff.TotalMinutes < 1)
        {
            return "刚刚";
        }
        else if (diff.TotalMinutes < 60)
        {
            return $"{(int)diff.TotalMinutes} 分钟前";
        }
        else if (diff.TotalHours < 24)
        {
            return $"{(int)diff.TotalHours} 小时前";
        }
        else if (diff.TotalDays < 7)
        {
            return $"{(int)diff.TotalDays} 天前";
        }
        else if (dateTime.Year == now.Year)
        {
            return dateTime.ToString("MM-dd HH:mm");
        }
        else
        {
            return dateTime.ToString("yyyy-MM-dd");
        }
    }
    
    /// <summary>
    /// 检测内容是否为 JSONL 格式
    /// </summary>
    private bool IsJsonlContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }
        
        // 检查是否包含多行 JSON 对象
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
        {
            return false;
        }
        
        // 检查第一行是否为有效的 JSON 对象
        var firstLine = lines[0].Trim();
        return firstLine.StartsWith("{") && firstLine.Contains("\"type\":");
    }
    
    /// <summary>
    /// 渲染 JSONL 内容为 HTML
    /// </summary>
    private string RenderJsonlContent(string content)
    {
        var html = new StringBuilder();
        html.Append("<div class=\"space-y-2\">");
        
        var lines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            var trimmedLine = line.Trim();
            if (string.IsNullOrEmpty(trimmedLine))
            {
                continue;
            }
            
            try
            {
                using var document = JsonDocument.Parse(trimmedLine);
                var root = document.RootElement;
                
                if (!root.TryGetProperty("type", out var typeElement))
                {
                    continue;
                }
                
                var eventType = typeElement.GetString() ?? string.Empty;
                var badgeClass = GetJsonlBadgeClass(eventType);
                var badgeLabel = GetJsonlBadgeLabel(eventType);
                
                html.Append($"<div class=\"border border-gray-200 rounded-lg p-3 bg-white\">");
                html.Append($"<span class=\"{badgeClass} text-xs font-semibold px-2 py-0.5 rounded-full\">{badgeLabel}</span>");
                
                // 提取并显示内容
                if (root.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String)
                {
                    var eventContent = contentElement.GetString();
                    if (!string.IsNullOrEmpty(eventContent))
                    {
                        html.Append($"<div class=\"mt-2 text-sm text-gray-700 whitespace-pre-wrap\">{System.Web.HttpUtility.HtmlEncode(eventContent)}</div>");
                    }
                }
                
                html.Append("</div>");
            }
            catch (JsonException)
            {
                // 忽略无效的 JSON 行
                continue;
            }
        }
        
        html.Append("</div>");
        return html.ToString();
    }
    
    /// <summary>
    /// 获取 JSONL 事件的徽章样式
    /// </summary>
    private string GetJsonlBadgeClass(string eventType)
    {
        return eventType switch
        {
            "thread.started" => "bg-primary-100 text-primary-700",
            "turn.started" => "bg-sky-100 text-sky-700",
            "turn.completed" => "bg-emerald-100 text-emerald-700",
            "turn.failed" => "bg-red-100 text-red-700",
            "item.started" => "bg-amber-100 text-amber-700",
            "item.updated" => "bg-blue-100 text-blue-700",
            "item.completed" => "bg-green-100 text-green-700",
            "error" => "bg-red-100 text-red-700",
            _ => "bg-gray-200 text-gray-700"
        };
    }
    
    /// <summary>
    /// 获取 JSONL 事件的徽章标签
    /// </summary>
    private string GetJsonlBadgeLabel(string eventType)
    {
        return eventType switch
        {
            "thread.started" => "线程启动",
            "turn.started" => "交互开始",
            "turn.completed" => "交互完成",
            "turn.failed" => "交互失败",
            "item.started" => "节点开始",
            "item.updated" => "节点更新",
            "item.completed" => "节点完成",
            "error" => "错误",
            _ => eventType
        };
    }

    /// <summary>
    /// 尝试为指定会话重新创建工作区目录
    /// </summary>
    private async Task<bool> TryRestoreWorkspaceAsync(SessionHistory session)
    {
        try
        {
            var restoredPath = CliExecutorService.GetSessionWorkspacePath(session.SessionId);
            var pathExists = Directory.Exists(restoredPath);

            if (!pathExists)
            {
                Directory.CreateDirectory(restoredPath);
            }

            var needSave = !string.Equals(session.WorkspacePath, restoredPath, StringComparison.OrdinalIgnoreCase) ||
                           !session.IsWorkspaceValid;

            session.WorkspacePath = restoredPath;
            session.IsWorkspaceValid = true;

            if (needSave)
            {
                await SessionHistoryManager.SaveSessionImmediateAsync(session);
            }

            var cachedSession = _sessions.FirstOrDefault(s => s.SessionId == session.SessionId);
            if (cachedSession != null)
            {
                cachedSession.WorkspacePath = restoredPath;
                cachedSession.IsWorkspaceValid = true;
            }

            Console.WriteLine($"✓ 已为会话 {session.Title} 重建工作区: {restoredPath}");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"自动重建工作区失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 获取工作区子标签页的CSS类
    /// </summary>
    private string GetWorkspaceSubTabClass(string subTabKey)
    {
        return _workspaceSubTab == subTabKey
            ? "bg-gray-800 text-white shadow-md"
            : "bg-white text-gray-600 hover:bg-gray-100 hover:text-gray-800";
    }
    
    /// <summary>
    /// 处理版本对比请求
    /// </summary>
    private async Task HandleCompareRequest((string filePath, string commitHash) data)
    {
        _diffFilePath = data.filePath;
        _diffFromCommit = data.commitHash;
        _diffToCommit = "HEAD";
        
        // 切换到 Diff 视图
        _workspaceSubTab = "diff";
        
        await InvokeAsync(StateHasChanged);
    }
    
    // 批量操作方法
    
    /// <summary>
    /// 切换文件选中状态
    /// </summary>
    private void ToggleFileSelection(string filePath)
    {
        if (_selectedFiles.Contains(filePath))
        {
            _selectedFiles.Remove(filePath);
        }
        else
        {
            _selectedFiles.Add(filePath);
        }
        StateHasChanged();
    }
    
    /// <summary>
    /// 全选/取消全选
    /// </summary>
    private void ToggleSelectAll()
    {
        var allFiles = GetAllFilePaths(_workspaceFiles);
        
        if (_selectedFiles.Count == allFiles.Count)
        {
            _selectedFiles.Clear();
        }
        else
        {
            _selectedFiles = new HashSet<string>(allFiles);
        }
        
        StateHasChanged();
    }
    
    /// <summary>
    /// 显示批量操作对话框
    /// </summary>
    private void ShowBatchOperationDialog(string operation)
    {
        if (!_selectedFiles.Any())
        {
            return;
        }
        
        _batchOperation = operation;
        _showBatchOperationDialog = true;
        _batchTargetFolder = string.Empty;
        _batchOperationError = string.Empty;
        StateHasChanged();
    }
    
    /// <summary>
    /// 关闭批量操作对话框
    /// </summary>
    private void CloseBatchOperationDialog()
    {
        _showBatchOperationDialog = false;
        _batchOperation = string.Empty;
        _batchTargetFolder = string.Empty;
        _batchOperationError = string.Empty;
        _isBatchOperating = false;
        StateHasChanged();
    }
    
    /// <summary>
    /// 执行批量操作
    /// </summary>
    private async Task ExecuteBatchOperation()
    {
        if (!_selectedFiles.Any() || _isBatchOperating)
        {
            return;
        }
        
        try
        {
            _isBatchOperating = true;
            _batchOperationError = string.Empty;
            StateHasChanged();
            
            var successCount = 0;
            var failCount = 0;
            
            switch (_batchOperation)
            {
                case "delete":
                    successCount = await CliExecutorService.BatchDeleteFilesAsync(_sessionId, _selectedFiles.ToList());
                    failCount = _selectedFiles.Count - successCount;
                    break;
                    
                case "move":
                    if (string.IsNullOrWhiteSpace(_batchTargetFolder))
                    {
                        _batchOperationError = "请输入目标文件夹";
                        return;
                    }
                    
                    foreach (var file in _selectedFiles)
                    {
                        var fileName = Path.GetFileName(file);
                        var targetPath = Path.Combine(_batchTargetFolder, fileName);
                        var success = await CliExecutorService.MoveFileInWorkspaceAsync(_sessionId, file, targetPath);
                        if (success) successCount++;
                        else failCount++;
                    }
                    break;
                    
                case "copy":
                    if (string.IsNullOrWhiteSpace(_batchTargetFolder))
                    {
                        _batchOperationError = "请输入目标文件夹";
                        return;
                    }
                    
                    foreach (var file in _selectedFiles)
                    {
                        var fileName = Path.GetFileName(file);
                        var targetPath = Path.Combine(_batchTargetFolder, fileName);
                        var success = await CliExecutorService.CopyFileInWorkspaceAsync(_sessionId, file, targetPath);
                        if (success) successCount++;
                        else failCount++;
                    }
                    break;
            }
            
            Console.WriteLine($"批量操作完成: 成功 {successCount}, 失败 {failCount}");
            
            // 刷新文件列表
            await LoadWorkspaceFiles();
            
            // 清空选择
            _selectedFiles.Clear();
            
            // 关闭对话框
            CloseBatchOperationDialog();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"批量操作失败: {ex.Message}");
            _batchOperationError = $"操作失败: {ex.Message}";
        }
        finally
        {
            _isBatchOperating = false;
            StateHasChanged();
        }
    }
    
    /// <summary>
    /// 获取批量操作标题
    /// </summary>
    private string GetBatchOperationTitle()
    {
        return _batchOperation switch
        {
            "delete" => "批量删除",
            "move" => "批量移动",
            "copy" => "批量复制",
            _ => "批量操作"
        };
    }

    // ==================== 新增功能方法 ====================

    /// <summary>
    /// 快捷操作选中事件
    /// </summary>
    private async Task OnQuickActionSelected(string actionContent)
    {
        // 将快捷操作内容插入到输入框
        _inputMessage = string.IsNullOrWhiteSpace(_inputMessage) 
            ? actionContent 
            : _inputMessage + "\n\n" + actionContent;
        
        StateHasChanged();
        
        // 聚焦到输入框
        await JSRuntime.InvokeVoidAsync("eval", "document.getElementById('input-message')?.focus()");
    }

    /// <summary>
    /// 模板选中事件
    /// </summary>
    private async Task OnTemplateSelected(PromptTemplate template)
    {
        var content = template.Content;
        
        // 如果模板包含变量，显示变量输入对话框
        if (template.Variables.Any())
        {
            await ShowVariableInputDialog(template);
        }
        else
        {
            // 没有变量，直接插入内容
            _inputMessage = string.IsNullOrWhiteSpace(_inputMessage) 
                ? content 
                : _inputMessage + "\n\n" + content;
            
            StateHasChanged();
            
            // 聚焦到输入框
            await JSRuntime.InvokeVoidAsync("eval", "document.getElementById('input-message')?.focus()");
        }
    }

    /// <summary>
    /// 显示变量输入对话框
    /// </summary>
    private async Task ShowVariableInputDialog(PromptTemplate template)
    {
        _templateWithVariables = template;
        _variableValues = template.Variables.ToDictionary(v => v, v => string.Empty);
        _showVariableInputDialog = true;
        StateHasChanged();
        
        await Task.CompletedTask;
    }

    /// <summary>
    /// 应用模板变量
    /// </summary>
    private async Task ApplyTemplateWithVariables()
    {
        if (_templateWithVariables == null) return;

        var content = _templateWithVariables.Content;
        
        // 替换所有变量
        foreach (var kvp in _variableValues)
        {
            var placeholder = $"{{{{{kvp.Key}}}}}";
            content = content.Replace(placeholder, kvp.Value);
        }
        
        // 插入到输入框
        _inputMessage = string.IsNullOrWhiteSpace(_inputMessage) 
            ? content 
            : _inputMessage + "\n\n" + content;
        
        // 关闭对话框
        CloseVariableInputDialog();
        
        StateHasChanged();
        
        // 聚焦到输入框
        await JSRuntime.InvokeVoidAsync("eval", "document.getElementById('input-message')?.focus()");
    }

    /// <summary>
    /// 关闭变量输入对话框
    /// </summary>
    private void CloseVariableInputDialog()
    {
        _showVariableInputDialog = false;
        _templateWithVariables = null;
        _variableValues.Clear();
        StateHasChanged();
    }

    /// <summary>
    /// 输入框内容变化事件（用于自动补全）
    /// </summary>
    private void HandleInput()
    {
        // 检查是否触发技能选择器（/ 符号）
        var skillFilterText = GetSkillFilterFromInput();
        if (skillFilterText != null && _skills.Any())
        {
            // 显示技能选择器并根据 / 后的内容进行筛选
            if (!_showSkillPicker)
            {
                ShowSkillPicker();
            }
            // 更新筛选条件为 / 后面的内容
            _skillFilter = skillFilterText;
        }
        else if (_showSkillPicker)
        {
            CloseSkillPicker();
        }

        // 使用防抖避免频繁触发
        _autoCompleteDebounceTimer?.Dispose();
        _autoCompleteDebounceTimer = new System.Threading.Timer(async _ =>
        {
            await InvokeAsync(async () =>
            {
                await ShowAutoComplete();
            });
        }, null, AutoCompleteDebounceMs, Timeout.Infinite);
    }

    /// <summary>
    /// 显示自动补全建议
    /// </summary>
    private async Task ShowAutoComplete()
    {
        if (string.IsNullOrWhiteSpace(_inputMessage) || _inputMessage.Length < 2)
        {
            _autoCompleteDropdown?.Hide();
            return;
        }

        try
        {
            var suggestions = new List<Suggestion>();

            // 检查 IndexedDB 是否准备就绪
            bool isReady = false;
            try
            {
                isReady = await JSRuntime.InvokeAsync<bool>("webCliIndexedDB.isReady");
            }
            catch
            {
                // IndexedDB 未就绪
            }

            if (!isReady)
            {
                _autoCompleteDropdown?.Hide();
                return;
            }

            // 检查是否触发模板快速插入（@ 符号）
            if (_inputMessage.Contains("@"))
            {
                try
                {
                    // 从 IndexedDB 获取模板
                    var templates = await JSRuntime.InvokeAsync<PromptTemplate[]>("webCliIndexedDB.getAllTemplates");
                    if (templates != null)
                    {
                        suggestions.AddRange(templates
                            .Where(t => !string.IsNullOrEmpty(t.Title))
                            .Select(t => new Suggestion
                            {
                                Text = t.Content,
                                Description = t.Title,
                                Type = SuggestionType.Template,
                                UsageCount = t.UsageCount
                            }));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"获取模板失败: {ex.Message}");
                }
            }
            else
            {
                // 从历史记录获取建议
                try
                {
                    var history = await JSRuntime.InvokeAsync<object[]>("webCliIndexedDB.searchInputHistory", _inputMessage, 5);
                    if (history != null && history.Length > 0)
                    {
                        foreach (var item in history)
                        {
                            try
                            {
                                var json = JsonSerializer.Serialize(item);
                                var historyItem = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                                if (historyItem != null && historyItem.ContainsKey("text"))
                                {
                                    var text = historyItem["text"].ToString();
                                    if (!string.IsNullOrEmpty(text))
                                    {
                                        suggestions.Add(new Suggestion
                                        {
                                            Text = text,
                                            Description = "历史记录",
                                            Type = SuggestionType.History,
                                            UsageCount = 0
                                        });
                                    }
                                }
                            }
                            catch
                            {
                                // 忽略解析错误
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"获取历史记录失败: {ex.Message}");
                }
            }

            if (suggestions.Any())
            {
                await _autoCompleteDropdown.ShowSuggestions(_inputMessage, suggestions);
            }
            else
            {
                _autoCompleteDropdown?.Hide();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"显示自动补全失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 自动补全建议选中事件
    /// </summary>
    private async Task OnSuggestionSelected(string text)
    {
        _inputMessage = text;
        StateHasChanged();
        
        // 聚焦到输入框
        await JSRuntime.InvokeVoidAsync("eval", "document.getElementById('input-message')?.focus()");
    }

    /// <summary>
    /// 取消执行
    /// </summary>
    private Task CancelExecution()
    {
        // 这里可以添加取消执行的逻辑
        // 目前只是隐藏进度追踪器
        _progressTracker?.Hide();
        _isLoading = false;
        StateHasChanged();
        return Task.CompletedTask;
    }

    #region 前端项目预览功能

    /// <summary>
    /// 检测工作区中的前端项目
    /// </summary>
    private async Task DetectFrontendProjects()
    {
        try
        {
            var workspacePath = CliExecutorService.GetSessionWorkspacePath(_sessionId);
            
            // 扫描可用的预览根目录（包含 package.json 的目录）
            await ScanAvailablePreviewRoots(workspacePath);
            
            // 如果设置了自定义预览根目录，使用它
            var searchPath = workspacePath;
            if (!string.IsNullOrEmpty(_previewRootPath))
            {
                var customPath = Path.Combine(workspacePath, _previewRootPath);
                if (Directory.Exists(customPath))
                {
                    searchPath = customPath;
                }
            }
            
            _detectedFrontendProjects = await FrontendProjectDetector.DetectProjectsAsync(searchPath);
            
            // 如果使用自定义根目录，需要调整相对路径
            if (!string.IsNullOrEmpty(_previewRootPath) && _detectedFrontendProjects.Any())
            {
                foreach (var proj in _detectedFrontendProjects)
                {
                    // 将相对路径调整为相对于工作区根目录
                    if (proj.RelativePath == ".")
                    {
                        proj.RelativePath = _previewRootPath;
                        proj.Key = _previewRootPath.Replace("\\", "/");
                    }
                    else
                    {
                        proj.RelativePath = Path.Combine(_previewRootPath, proj.RelativePath);
                        proj.Key = proj.RelativePath.Replace("\\", "/");
                    }
                }
            }
            
            if (_detectedFrontendProjects.Any() && string.IsNullOrEmpty(_selectedFrontendProject))
            {
                _selectedFrontendProject = _detectedFrontendProjects.First().Key;
            }

            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"检测前端项目失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 扫描可用的预览根目录
    /// </summary>
    private async Task ScanAvailablePreviewRoots(string workspacePath)
    {
        _availablePreviewRoots.Clear();
        _availablePreviewRoots.Add(""); // 空字符串表示工作区根目录
        
        try
        {
            // 查找所有包含 package.json 的目录
            var packageJsonFiles = await Task.Run(() => 
                Directory.GetFiles(workspacePath, "package.json", SearchOption.AllDirectories)
                    .Where(f => !f.Contains("node_modules"))
                    .ToList());
            
            foreach (var packageJson in packageJsonFiles)
            {
                var dir = Path.GetDirectoryName(packageJson);
                if (!string.IsNullOrEmpty(dir))
                {
                    var relativePath = Path.GetRelativePath(workspacePath, dir);
                    if (relativePath != "." && !_availablePreviewRoots.Contains(relativePath))
                    {
                        _availablePreviewRoots.Add(relativePath);
                    }
                }
            }
            
            // 同时查找常见的前端目录结构
            var commonFrontendDirs = new[] { "web", "Web", "frontend", "Frontend", "client", "Client", "app", "App", "ui", "UI", "src", "packages" };
            foreach (var dirName in commonFrontendDirs)
            {
                var dirPath = Path.Combine(workspacePath, dirName);
                if (Directory.Exists(dirPath) && !_availablePreviewRoots.Contains(dirName))
                {
                    _availablePreviewRoots.Add(dirName);
                }
            }
            
            // 排序
            _availablePreviewRoots = _availablePreviewRoots.OrderBy(x => x).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"扫描预览根目录失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 切换预览根目录选择器显示状态
    /// </summary>
    private void TogglePreviewRootSelector()
    {
        _showPreviewRootSelector = !_showPreviewRootSelector;
    }
    
    /// <summary>
    /// 设置预览根目录并重新检测前端项目
    /// </summary>
    private async Task SetPreviewRootPath(string path)
    {
        _previewRootPath = path;
        _selectedFrontendProject = ""; // 重置选中的项目
        _showPreviewRootSelector = false;
        await DetectFrontendProjects();
    }

    /// <summary>
    /// 启动预览（根据模式选择开发服务器或构建预览）
    /// </summary>
    private async Task StartPreview()
    {
        // 如果服务器已经运行，则停止它
        if (_currentDevServer != null)
        {
            await StopCurrentServer();
            return;
        }

        if (string.IsNullOrEmpty(_selectedFrontendProject))
            return;

        var project = _detectedFrontendProjects.FirstOrDefault(p => p.Key == _selectedFrontendProject);
        if (project == null)
            return;

        try
        {
            _isServerStarting = true;
            StateHasChanged();

            if (_selectedPreviewMode == "dev")
            {
                await StartDevServer(project);
            }
            else if (_selectedPreviewMode == "build")
            {
                await StartBuildPreview(project);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"启动预览失败: {ex.Message}");
            // TODO: 显示错误消息给用户
        }
        finally
        {
            _isServerStarting = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// 启动开发服务器
    /// </summary>
    private async Task StartDevServer(FrontendProjectInfo project)
    {
        try
        {
            _currentDevServer = await DevServerManager.StartDevServerAsync(_sessionId, project);
            
            // 切换到预览标签页
            _activeTabKey = "3";
            _selectedHtmlFile = project.Key; // 用于标识
            _htmlPreviewUrl = _currentDevServer.ProxyUrl;
            
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"启动开发服务器失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 启动构建预览
    /// </summary>
    private async Task StartBuildPreview(FrontendProjectInfo project)
    {
        try
        {
            _currentDevServer = await DevServerManager.StartBuildPreviewAsync(_sessionId, project);
            
            // 切换到预览标签页
            _activeTabKey = "3";
            _selectedHtmlFile = project.Key;
            
            // 使用代理URL（由DevServerManager提供）
            _htmlPreviewUrl = _currentDevServer.ProxyUrl;
            
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"启动构建预览失败: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 停止当前服务器
    /// </summary>
    private async Task StopCurrentServer()
    {
        if (_currentDevServer == null)
            return;

        try
        {
            await DevServerManager.StopDevServerAsync(_sessionId, _currentDevServer.ServerKey);
            _currentDevServer = null;
            _htmlPreviewUrl = string.Empty;
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"停止服务器失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取代理预览URL
    /// </summary>
    private string GetProxyPreviewUrl()
    {
        if (_currentDevServer != null && !string.IsNullOrEmpty(_currentDevServer.ProxyUrl))
        {
            return _currentDevServer.ProxyUrl;
        }

        return _htmlPreviewUrl;
    }

    // ==================== 技能相关方法 ====================

    /// <summary>
    /// 加载技能列表
    /// </summary>
    private async Task LoadSkillsAsync()
    {
        try
        {
            _skills = await SkillService.GetSkillsAsync();
            Console.WriteLine($"已加载 {_skills.Count} 个技能");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载技能列表失败: {ex.Message}");
            _skills = new List<WebCodeCli.Domain.Domain.Model.SkillItem>();
        }
    }

    /// <summary>
    /// 工具选择改变事件
    /// </summary>
    private void OnToolChanged()
    {
        // 关闭技能选择器并重新过滤
        if (_showSkillPicker)
        {
            UpdateSkillFilterByTool();
            StateHasChanged();
        }
    }

    /// <summary>
    /// 显示技能选择器
    /// </summary>
    private void ShowSkillPicker()
    {
        _showSkillPicker = true;
        // 不再清空 _skillFilter，由 HandleInput 根据输入内容设置
        
        StateHasChanged();
    }
    
    /// <summary>
    /// 根据选择的工具更新技能过滤（已简化：过滤逻辑在 GetFilteredSkills 中根据工具自动处理）
    /// </summary>
    private void UpdateSkillFilterByTool()
    {
        // 过滤逻辑已移至 GetFilteredSkills 方法中
        // 不再需要在这里处理
    }

    /// <summary>
    /// 关闭技能选择器
    /// </summary>
    private void CloseSkillPicker()
    {
        _showSkillPicker = false;
        _skillFilter = string.Empty;
        StateHasChanged();
    }

    /// <summary>
    /// 从输入消息中提取技能筛选文本（/ 后面的内容）
    /// 返回 null 表示没有触发技能选择器
    /// </summary>
    private string? GetSkillFilterFromInput()
    {
        if (string.IsNullOrEmpty(_inputMessage))
            return null;
            
        // 查找最后一个 / 的位置
        var lastSlashIndex = _inputMessage.LastIndexOf('/');
        if (lastSlashIndex < 0)
            return null;
            
        // 检查 / 前面是否是空格或者在开头（确保是技能触发符）
        if (lastSlashIndex > 0 && !char.IsWhiteSpace(_inputMessage[lastSlashIndex - 1]))
            return null;
            
        // 获取 / 后面的内容（可能为空，表示刚输入 /）
        var filterText = _inputMessage.Substring(lastSlashIndex + 1);
        
        // 如果 / 后面包含空格，说明技能输入已结束
        if (filterText.Contains(' '))
            return null;
            
        return filterText;
    }

    /// <summary>
    /// 选择技能
    /// </summary>
    private void SelectSkill(WebCodeCli.Domain.Domain.Model.SkillItem skill)
    {
        var skillCommand = $"/{skill.Name} ";
        
        // 将技能命令插入到输入框，替换当前的 /xxx 部分
        if (string.IsNullOrEmpty(_inputMessage))
        {
            _inputMessage = skillCommand;
        }
        else
        {
            // 查找最后一个 / 的位置并替换 / 及其后面的内容
            var lastSlashIndex = _inputMessage.LastIndexOf('/');
            if (lastSlashIndex >= 0)
            {
                _inputMessage = _inputMessage.Substring(0, lastSlashIndex) + skillCommand;
            }
            else
            {
                _inputMessage += skillCommand;
            }
        }
        
        CloseSkillPicker();
        
        // 聚焦到输入框
        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            await JSRuntime.InvokeVoidAsync("eval", "document.getElementById('input-message')?.focus()");
        });
    }

    /// <summary>
    /// 获取过滤后的技能列表
    /// </summary>
    private List<WebCodeCli.Domain.Domain.Model.SkillItem> GetFilteredSkills()
    {
        var filtered = _skills.AsEnumerable();
        
        // 根据右上角选择的工具自动过滤技能来源
        var selectedTool = _availableTools.FirstOrDefault(t => t.Id == _selectedToolId);
        if (selectedTool != null)
        {
            if (selectedTool.Id.Contains("claude", StringComparison.OrdinalIgnoreCase))
            {
                filtered = filtered.Where(s => s.Source.Equals("claude", StringComparison.OrdinalIgnoreCase));
            }
            else if (selectedTool.Id.Contains("codex", StringComparison.OrdinalIgnoreCase))
            {
                filtered = filtered.Where(s => s.Source.Equals("codex", StringComparison.OrdinalIgnoreCase));
            }
        }
        
        // 用户输入的搜索词过滤（仅搜索名称和描述）
        if (!string.IsNullOrWhiteSpace(_skillFilter))
        {
            filtered = filtered.Where(s => 
                s.Name.Contains(_skillFilter, StringComparison.OrdinalIgnoreCase) ||
                s.Description.Contains(_skillFilter, StringComparison.OrdinalIgnoreCase));
        }

        return filtered.ToList();
    }

    /// <summary>
    /// 获取技能图标颜色
    /// </summary>
    private string GetSkillIconColor(string source)
    {
        return source.ToLower() switch
        {
            "claude" => "text-orange-500",
            "codex" => "text-blue-500",
            _ => "text-gray-500"
        };
    }

    /// <summary>
    /// 获取技能徽章样式
    /// </summary>
    private string GetSkillBadgeClass(string source)
    {
        return source.ToLower() switch
        {
            "claude" => "bg-orange-100 text-orange-700",
            "codex" => "bg-blue-100 text-blue-700",
            _ => "bg-gray-100 text-gray-700"
        };
    }

    /// <summary>
    /// 获取当前选择的工具名称
    /// </summary>
    private string GetCurrentToolName()
    {
        var selectedTool = _availableTools.FirstOrDefault(t => t.Id == _selectedToolId);
        if (selectedTool == null)
        {
            return "全部";
        }

        if (selectedTool.Id.Contains("claude", StringComparison.OrdinalIgnoreCase))
        {
            return "Claude";
        }
        else if (selectedTool.Id.Contains("codex", StringComparison.OrdinalIgnoreCase))
        {
            return "Codex";
        }
        else
        {
            return selectedTool.Name;
        }
    }

    /// <summary>
    /// 获取当前工具徽章样式
    /// </summary>
    private string GetCurrentToolBadgeClass()
    {
        var selectedTool = _availableTools.FirstOrDefault(t => t.Id == _selectedToolId);
        if (selectedTool == null)
        {
            return "bg-gray-100 text-gray-700";
        }

        if (selectedTool.Id.Contains("claude", StringComparison.OrdinalIgnoreCase))
        {
            return "bg-orange-100 text-orange-700";
        }
        else if (selectedTool.Id.Contains("codex", StringComparison.OrdinalIgnoreCase))
        {
            return "bg-blue-100 text-blue-700";
        }
        else
        {
            return "bg-gray-100 text-gray-700";
        }
    }
    
    /// <summary>
    /// 统一路径格式（使用正斜杠）
    /// </summary>
    private string NormalizePath(string path)
    {
        return path?.Replace("\\", "/") ?? string.Empty;
    }

    /// <summary>
    /// 处理语言切换事件
    /// </summary>
    private async Task OnLanguageChanged(string languageCode)
    {
        _currentLanguage = languageCode;
        try
        {
            await L.ReloadTranslationsAsync();
            await LoadTranslationsAsync();
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"语言切换后刷新翻译失败: {ex.Message}");
        }
    }

    #region 本地化辅助方法

    /// <summary>
    /// 加载翻译资源
    /// </summary>
    private async Task LoadTranslationsAsync()
    {
        try
        {
            var allTranslations = await L.GetAllTranslationsAsync(_currentLanguage);
            _translations = FlattenTranslations(allTranslations);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载翻译资源失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 将嵌套的翻译字典展平为点分隔的键
    /// </summary>
    private Dictionary<string, string> FlattenTranslations(Dictionary<string, object> source, string prefix = "")
    {
        var result = new Dictionary<string, string>();
        
        foreach (var kvp in source)
        {
            var key = string.IsNullOrEmpty(prefix) ? kvp.Key : $"{prefix}.{kvp.Key}";
            
            if (kvp.Value is JsonElement jsonElement)
            {
                if (jsonElement.ValueKind == JsonValueKind.Object)
                {
                    var nested = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonElement.GetRawText());
                    if (nested != null)
                    {
                        foreach (var item in FlattenTranslations(nested, key))
                        {
                            result[item.Key] = item.Value;
                        }
                    }
                }
                else if (jsonElement.ValueKind == JsonValueKind.String)
                {
                    result[key] = jsonElement.GetString() ?? key;
                }
            }
            else if (kvp.Value is Dictionary<string, object> dict)
            {
                foreach (var item in FlattenTranslations(dict, key))
                {
                    result[item.Key] = item.Value;
                }
            }
            else if (kvp.Value is string str)
            {
                result[key] = str;
            }
        }
        
        return result;
    }

    /// <summary>
    /// 获取翻译文本
    /// </summary>
    private string T(string key)
    {
        if (_translations.TryGetValue(key, out var translation))
        {
            return translation;
        }
        
        // 返回键的最后一部分作为默认值
        var parts = key.Split('.');
        return parts.Length > 0 ? parts[^1] : key;
    }

    /// <summary>
    /// 获取翻译文本（带参数）
    /// </summary>
    private string T(string key, params (string name, string value)[] parameters)
    {
        var text = T(key);
        foreach (var (name, value) in parameters)
        {
            text = text.Replace($"{{{name}}}", value);
        }
        return text;
    }

    #endregion

    #endregion
}

