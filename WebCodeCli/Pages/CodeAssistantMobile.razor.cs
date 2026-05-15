using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Markdig;
using System.IO;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service;
using WebCodeCli.Domain.Domain.Service.Adapters;
using WebCodeCli.Components;
using WebCodeCli.Components.Dialogs;
using WebCodeCli.Helpers;
using WebCodeCli.Models;

namespace WebCodeCli.Pages;

/// <summary>
/// 移动端代码助手页面
/// </summary>
public partial class CodeAssistantMobile : ComponentBase, IAsyncDisposable
{
    #region 服务注入
    
    [Inject] private ICliExecutorService CliExecutorService { get; set; } = default!;
    [Inject] private IMessageSubmissionService MessageSubmissionService { get; set; } = default!;
    [Inject] private IChatSessionService ChatSessionService { get; set; } = default!;
    [Inject] private ICliToolEnvironmentService CliToolEnvironmentService { get; set; } = default!;
    [Inject] private IAuthenticationService AuthenticationService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ISessionHistoryManager SessionHistoryManager { get; set; } = default!;
    [Inject] private IExternalCliSessionHistoryService ExternalCliSessionHistoryService { get; set; } = default!;
    [Inject] private ILocalizationService L { get; set; } = default!;
    [Inject] private WebCodeCli.Domain.Domain.Service.ISkillService SkillService { get; set; } = default!;
    [Inject] private ISessionOutputService SessionOutputService { get; set; } = default!;
    [Inject] private ISystemSettingsService SystemSettingsService { get; set; } = default!;
    [Inject] private IUserContextService UserContextService { get; set; } = default!;
    [Inject] private ICcSwitchService CcSwitchService { get; set; } = default!;
    [Inject] private ISuperpowersCapabilityService SuperpowersCapabilityService { get; set; } = default!;
    [Inject] private IGoalCapabilityService GoalCapabilityService { get; set; } = default!;
    [Inject] private IVersionService VersionService { get; set; } = default!;
    [Inject] private HttpClient Http { get; set; } = default!;
    [Inject] private IFrontendProjectDetector FrontendProjectDetector { get; set; } = default!;
    [Inject] private IDevServerManager DevServerManager { get; set; } = default!;
    
    #endregion
    
    #region Tab导航
    
    private string _activeTab = "chat";
    
    private readonly record struct TabItem(string Key, string Label, string Icon);
    
    private List<TabItem> _tabs = new();
    private bool _tabsInitialized = false;
    
    private void InitializeTabs()
    {
        _tabs = new List<TabItem>
        {
            new("chat", T("codeAssistant.chat"), @"<svg class=""w-6 h-6"" fill=""none"" stroke=""currentColor"" viewBox=""0 0 24 24""><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M8 12h.01M12 12h.01M16 12h.01M21 12c0 4.418-4.03 8-9 8a9.863 9.863 0 01-4.255-.949L3 20l1.395-3.72C3.512 15.042 3 13.574 3 12c0-4.418 4.03-8 9-8s9 3.582 9 8z""></path></svg>"),
            new("files", T("codeAssistant.files"), @"<svg class=""w-6 h-6"" fill=""none"" stroke=""currentColor"" viewBox=""0 0 24 24""><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M3 7v10a2 2 0 002 2h14a2 2 0 002-2V9a2 2 0 00-2-2h-6l-2-2H5a2 2 0 00-2 2z""></path></svg>"),
            new("tasks", T("activityBar.tasks"), @"<svg class=""w-6 h-6"" fill=""none"" stroke=""currentColor"" viewBox=""0 0 24 24""><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2m-6 9l2 2 4-4""></path></svg>"),
            new("settings", T("codeAssistant.settings"), @"<svg class=""w-6 h-6"" fill=""none"" stroke=""currentColor"" viewBox=""0 0 24 24""><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z""></path><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M15 12a3 3 0 11-6 0 3 3 0 016 0z""></path></svg>")
        };
        _tabsInitialized = true;
    }
    
    private void SwitchTab(string tabKey)
    {
        _activeTab = tabKey;
        StateHasChanged();
    }
    
    #endregion
    
    #region 本地化
    
    private Dictionary<string, string> _translations = new();
    private string _currentLanguage = "zh-CN";
    private List<WebCodeCli.Domain.Domain.Service.LanguageInfo> _supportedLanguages = new();
    
    private string T(string key, params (string key, string value)[] args)
    {
        if (_translations.TryGetValue(key, out var value))
        {
            foreach (var (argKey, argValue) in args)
            {
                value = value.Replace($"{{{argKey}}}", argValue);
            }
            return value;
        }
        return key;
    }
    
    private async Task LoadTranslationsAsync()
    {
        try
        {
            var allTranslations = await L.GetAllTranslationsAsync(_currentLanguage);
            _translations = FlattenTranslations(allTranslations);
        }
        catch
        {
            _translations = new Dictionary<string, string>();
        }
    }
    
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
    
    private async Task OnLanguageChanged(string language)
    {
        _currentLanguage = language;
        await LoadTranslationsAsync();
        InitializeTabs();
        StateHasChanged();
    }
    
    /// <summary>
    /// 移动端语言下拉框变化事件
    /// </summary>
    private async Task OnMobileLanguageChanged()
    {
        try
        {
            await L.SetCurrentLanguageAsync(_currentLanguage);
            await L.ReloadTranslationsAsync();
            await LoadTranslationsAsync();
            InitializeTabs();
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"切换语言失败: {ex.Message}");
        }
    }
    
    #endregion
    
    #region 聊天功能
    
    private List<ChatMessage> _messages = new();
    private string _inputMessage = string.Empty;
    private bool _isLoading = false;
    private string _currentAssistantMessage = string.Empty;
    private string _sessionId = Guid.NewGuid().ToString();
    private bool _showQuickActions = false;
    private bool _isMessageAttachmentUploading = false;
    private readonly MessageAttachmentComposerState _messageAttachmentComposer = new();
    private const int MaxMessageAttachmentCount = 10;
    private const long MaxMessageAttachmentSizeBytes = 100 * 1024 * 1024;
    
    // 消息详情展开状态（内嵌输出）
    private HashSet<int> _expandedMessageIndices = new();
    
    /// <summary>
    /// 切换消息详情展开/折叠状态
    /// </summary>
    private void ToggleMessageDetails(int messageIndex)
    {
        if (_expandedMessageIndices.Contains(messageIndex))
        {
            _expandedMessageIndices.Remove(messageIndex);
        }
        else
        {
            _expandedMessageIndices.Add(messageIndex);
        }
        StateHasChanged();
    }
    
    /// <summary>
    /// 检查消息是否展开
    /// </summary>
    private bool IsMessageExpanded(int messageIndex) => _expandedMessageIndices.Contains(messageIndex);
    
    // Skill技能选择器相关
    private List<WebCodeCli.Domain.Domain.Model.SkillItem> _skills = new();
    private bool _showSkillPicker = false;
    private string _skillFilter = string.Empty;
    
    // 快捷操作项
    
    private void ToggleQuickActions()
    {
        _showQuickActions = !_showQuickActions;
    }
    
    private async Task OnQuickActionSelected(string actionContent)
    {
        _inputMessage = string.IsNullOrWhiteSpace(_inputMessage)
            ? actionContent
            : _inputMessage + "\n\n" + actionContent;

        _showQuickActions = false;
        StateHasChanged();

        try
        {
            await JSRuntime.InvokeVoidAsync("eval", "document.getElementById('mobile-input-message')?.focus()");
        }
        catch { }
    }
    
    #region Skill技能选择器
    
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
            Console.WriteLine($"加载技能失败: {ex.Message}");
            _skills = new List<WebCodeCli.Domain.Domain.Model.SkillItem>();
        }
    }
    
    /// <summary>
    /// 输入框内容变化事件（用于触发技能选择器）
    /// </summary>
    private void HandleInputChange()
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
    /// 显示技能选择器
    /// </summary>
    private void ShowSkillPicker()
    {
        _showSkillPicker = true;
        _showQuickActions = false; // 关闭快捷操作面板
        StateHasChanged();
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
            await JSRuntime.InvokeVoidAsync("eval", "document.getElementById('mobile-input-message')?.focus()");
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
            else if (selectedTool.Id.Contains("opencode", StringComparison.OrdinalIgnoreCase))
            {
                filtered = filtered.Where(s => s.Source.Equals("opencode", StringComparison.OrdinalIgnoreCase));
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
            "opencode" => "text-emerald-500",
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
            "opencode" => "bg-emerald-100 text-emerald-700",
            _ => "bg-gray-100 text-gray-700"
        };
    }

    private string _superpowersQuickInput = string.Empty;
    private SuperpowersCapabilityPresentationState _superpowersCapabilityPresentation = SuperpowersCapabilityPresentationState.Unknown;
    private string _superpowersCapabilityPresentationContextKey = string.Empty;
    private string _goalQuickInput = string.Empty;
    private GoalCapabilityPresentationState _goalCapabilityPresentation = GoalCapabilityPresentationState.Unknown;
    private string _goalCapabilityPresentationContextKey = string.Empty;

    private const string SuperpowersCapabilityCheckingText = "正在检测 superpowers 能力...";
    private const string SuperpowersCapabilityUnavailableText = "当前 Provider 缺少 superpowers 能力";
    private const string SuperpowersCapabilityProbeFailedText = "检测 superpowers 能力失败，请重试";
    private const string SuperpowersCapabilityRetryText = "重新检测";
    private const string GoalCapabilityCheckingText = GoalQuickActionDefaults.CapabilityCheckingText;
    private const string GoalCapabilityUnavailableText = GoalQuickActionDefaults.CapabilityUnavailableText;
    private const string GoalCapabilityProbeFailedText = GoalQuickActionDefaults.CapabilityProbeFailedText;
    private const string GoalCapabilityRetryText = GoalQuickActionDefaults.CapabilityRetryButtonText;

    private SuperpowersQuickActionEligibility CurrentSuperpowersQuickActionEligibility =>
        SuperpowersQuickActionHelper.Evaluate(
            _messages,
            hasSuperpowersPlanFiles: HasSuperpowersPlanFiles(),
            isProcessRunning: _isLoading);

    private SuperpowersQuickActionViewState CurrentSuperpowersQuickActionViewState
    {
        get
        {
            var eligibility = CurrentSuperpowersQuickActionEligibility;
            return new SuperpowersQuickActionViewState(
                MessageId: eligibility.MessageId,
                ShowQuickInput: eligibility.ShowQuickInput,
                ShowPlanActions: eligibility.ShowPlanActions,
                ContinueActionDisabled: eligibility.IsDisabled,
                IsDisabled: eligibility.IsDisabled
                            || _superpowersCapabilityPresentation.IsChecking
                            || _superpowersCapabilityPresentation.State == SuperpowersCapabilityState.Unavailable,
                StatusMessage: _superpowersCapabilityPresentation.StatusMessage,
                ShowRetryAction: _superpowersCapabilityPresentation.ShowRetryAction,
                RetryActionDisabled: eligibility.IsDisabled || _superpowersCapabilityPresentation.IsChecking,
                RetryActionText: SuperpowersCapabilityRetryText);
        }
    }

    private SuperpowersQuickActionEligibility CurrentGoalQuickActionEligibility =>
        IsGoalQuickActionToolSupported()
            ? CurrentSuperpowersQuickActionEligibility with { ShowPlanActions = false }
            : SuperpowersQuickActionEligibility.Hidden;

    private GoalQuickActionViewState CurrentGoalQuickActionViewState
    {
        get
        {
            var eligibility = CurrentGoalQuickActionEligibility;
            return new GoalQuickActionViewState(
                MessageId: eligibility.MessageId,
                IsDisabled: eligibility.IsDisabled
                            || _goalCapabilityPresentation.IsChecking
                            || _goalCapabilityPresentation.State == GoalCapabilityState.Unavailable,
                StatusMessage: _goalCapabilityPresentation.StatusMessage,
                ShowRetryAction: _goalCapabilityPresentation.ShowRetryAction,
                RetryActionDisabled: eligibility.IsDisabled || _goalCapabilityPresentation.IsChecking,
                RetryActionText: GoalCapabilityRetryText);
        }
    }

    private bool HasSuperpowersPlanFiles()
    {
        try
        {
            var workspacePath = CliExecutorService.GetSessionWorkspacePath(_sessionId)
                               ?? _currentSession?.WorkspacePath;

            if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            {
                return false;
            }

            var superpowersPlanPath = Path.Combine(workspacePath, "docs", "superpowers", "plans");
            return Directory.Exists(superpowersPlanPath)
                && Directory.EnumerateFiles(superpowersPlanPath, "*.md").Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSuperpowersQuickActionEligible(ChatMessage message, SuperpowersQuickActionEligibility eligibility)
    {
        return SuperpowersQuickActionHelper.IsMessageEligible(message, eligibility);
    }
    
    #endregion
    
    #if false
    private async Task SendMessage()
    {
        if (string.IsNullOrWhiteSpace(_inputMessage) || _isLoading)
            return;
            
        var userMessage = _inputMessage.Trim();
        _inputMessage = string.Empty;
        _showQuickActions = false;
        _showSkillPicker = false; // 关闭技能选择器

        var selectedTool = _availableTools.FirstOrDefault(t => t.Id == _selectedToolId);
        InitializeJsonlState(IsJsonlTool(selectedTool));

        if (_isJsonlOutputActive && _progressTracker != null)
        {
            _progressTracker.Start();
        }
        
        // 添加用户消息
        _messages.Add(new ChatMessage
        {
            Role = "user",
            Content = userMessage,
            CreatedAt = DateTime.Now,
            CliToolId = _selectedToolId,
            IsCompleted = true
        });
        
        _isLoading = true;
        _currentAssistantMessage = string.Empty;
        StateHasChanged();
        
        // 滚动到底部
        await ScrollToBottom();

        if (await TryHandleHistoryCommandAsync(userMessage))
        {
            _isLoading = false;
            _currentAssistantMessage = string.Empty;
            StateHasChanged();
            await ScrollToBottom();
            await SaveCurrentSession();
            return;
        }
        
        var contentBuilder = new StringBuilder();

        try
        {
            // 调用CLI执行服务
            await foreach (var chunk in CliExecutorService.ExecuteStreamAsync(
                _sessionId,
                _selectedToolId, 
                userMessage))
            {
                if (chunk.IsError)
                {
                    _messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = string.Empty,
                        HasError = true,
                        ErrorMessage = chunk.ErrorMessage ?? chunk.Content,
                        CreatedAt = DateTime.Now,
                        CliToolId = _selectedToolId,
                        IsCompleted = true
                    });
                    break;
                }
                else if (chunk.IsCompleted)
                {
                    if (_isJsonlOutputActive)
                    {
                        ProcessJsonlChunk(string.Empty, flush: true);
                        var finalJsonlContent = GetJsonlAssistantMessage();
                        _currentAssistantMessage = finalJsonlContent;
                        contentBuilder.Clear();
                        contentBuilder.Append(finalJsonlContent);
                        UpdateOutputRaw(finalJsonlContent);
                    }

                    // 完成后添加助手消息
                    var finalContent = contentBuilder.ToString();
                    if (!string.IsNullOrEmpty(finalContent))
                    {
                        _messages.Add(new ChatMessage
                        {
                            Role = "assistant",
                            Content = finalContent,
                            CreatedAt = DateTime.Now,
                            CliToolId = _selectedToolId,
                            IsCompleted = true
                        });
                    }
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
                        UpdateOutputRaw(liveContent);
                    }
                    else
                    {
                        contentBuilder.Append(chunkContent);
                        _currentAssistantMessage = contentBuilder.ToString();
                        UpdateOutputRaw(_currentAssistantMessage);
                    }

                    await InvokeAsync(StateHasChanged);
                }
            }
        }
        catch (Exception ex)
        {
            _messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = string.Empty,
                HasError = true,
                ErrorMessage = $"{T("codeAssistant.errorOccurred")}: {ex.Message}",
                CreatedAt = DateTime.Now,
                CliToolId = _selectedToolId,
                IsCompleted = true
            });
        }
        finally
        {
            if (_isJsonlOutputActive)
            {
                ProcessJsonlChunk(string.Empty, flush: true);
                _currentAssistantMessage = GetJsonlAssistantMessage();

                if (_progressTracker != null)
                {
                    if (_messages.LastOrDefault()?.HasError == true)
                    {
                        _progressTracker.Fail(_messages.LastOrDefault()?.ErrorMessage ?? T("codeAssistant.errorOccurred"));
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
            await SaveCurrentSession();
        }
    }

    private async Task StartLowInterruptionContinueAsync(ChatMessage sourceMessage)
    {
        var eligibility = CurrentLowInterruptionContinueEligibility;
        if (_isLoading
            || eligibility.IsDisabled
            || !LowInterruptionContinueHelper.IsMessageEligible(sourceMessage, eligibility))
        {
            return;
        }

        var selectedTool = _availableTools.FirstOrDefault(t => t.Id == _selectedToolId);
        CaptureLatestCompletedAssistantStructuredTodoList();
        InitializeJsonlState(IsJsonlTool(selectedTool));

        if (_isJsonlOutputActive && _progressTracker != null)
        {
            _progressTracker.Start();
        }

        _isLoading = true;
        _currentAssistantMessage = string.Empty;
        StateHasChanged();
        await ScrollToBottom();

        var contentBuilder = new StringBuilder();

        try
        {
            await foreach (var chunk in CliExecutorService.ExecuteLowInterruptionContinueStreamAsync(
                _sessionId,
                _selectedToolId,
                _lowInterruptionContinuePrompt,
                default))
            {
                if (chunk.IsError)
                {
                    _messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = string.Empty,
                        HasError = true,
                        ErrorMessage = chunk.ErrorMessage ?? chunk.Content,
                        CreatedAt = DateTime.Now,
                        CliToolId = _selectedToolId,
                        IsCompleted = true
                    });
                    break;
                }
                else if (chunk.IsCompleted)
                {
                    if (_isJsonlOutputActive)
                    {
                        ProcessJsonlChunk(string.Empty, flush: true);
                        var finalJsonlContent = GetJsonlAssistantMessage();
                        _currentAssistantMessage = finalJsonlContent;
                        contentBuilder.Clear();
                        contentBuilder.Append(finalJsonlContent);
                        UpdateOutputRaw(finalJsonlContent);
                    }

                    var finalContent = contentBuilder.ToString();
                    if (!string.IsNullOrEmpty(finalContent))
                    {
                        _messages.Add(new ChatMessage
                        {
                            Role = "assistant",
                            Content = finalContent,
                            CreatedAt = DateTime.Now,
                            CliToolId = _selectedToolId,
                            IsCompleted = true
                        });
                    }
                    break;
                }
                else
                {
                    var chunkContent = chunk.Content ?? string.Empty;
                    if (_isJsonlOutputActive)
                    {
                        ProcessJsonlChunk(chunkContent, flush: false);
                        var liveContent = GetJsonlAssistantMessage();
                        _currentAssistantMessage = liveContent;
                        UpdateOutputRaw(liveContent);
                    }
                    else
                    {
                        contentBuilder.Append(chunkContent);
                        _currentAssistantMessage = contentBuilder.ToString();
                        UpdateOutputRaw(_currentAssistantMessage);
                    }

                    await InvokeAsync(StateHasChanged);
                }
            }
        }
        catch (Exception ex)
        {
            _messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = string.Empty,
                HasError = true,
                ErrorMessage = $"{T("codeAssistant.errorOccurred")}: {ex.Message}",
                CreatedAt = DateTime.Now,
                CliToolId = _selectedToolId,
                IsCompleted = true
            });
        }
        finally
        {
            if (_isJsonlOutputActive)
            {
                ProcessJsonlChunk(string.Empty, flush: true);
                _currentAssistantMessage = GetJsonlAssistantMessage();
                CaptureLatestCompletedAssistantStructuredTodoList();

                if (_progressTracker != null)
                {
                    if (_messages.LastOrDefault()?.HasError == true)
                    {
                        _progressTracker.Fail(_messages.LastOrDefault()?.ErrorMessage ?? T("codeAssistant.errorOccurred"));
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
            await SaveCurrentSession();
        }
    }

    #endif

    private Task SendMessage()
    {
        return SendMessageCoreAsync(_inputMessage, clearComposerInput: true, closeTransientPanels: true);
    }

    private async Task SendMessageCoreAsync(
        string? rawMessage,
        bool clearComposerInput,
        bool closeTransientPanels)
    {
        if (string.IsNullOrWhiteSpace(rawMessage) || _isLoading || _isMessageAttachmentUploading)
            return;

        var userMessage = rawMessage.Trim();
        if (clearComposerInput)
        {
            _inputMessage = string.Empty;
        }

        if (closeTransientPanels)
        {
            _showQuickActions = false;
            _showSkillPicker = false;
        }

        var selectedTool = _availableTools.FirstOrDefault(t => t.Id == _selectedToolId);
        InitializeJsonlState(IsJsonlTool(selectedTool));

        if (_isJsonlOutputActive && _progressTracker != null)
        {
            _progressTracker.Start();
        }

        var userMessageModel = new ChatMessage
        {
            Role = "user",
            Content = userMessage,
            CreatedAt = DateTime.Now,
            CliToolId = _selectedToolId,
            IsCompleted = true
        };
        _messages.Add(userMessageModel);

        _isLoading = true;
        _currentAssistantMessage = string.Empty;
        StateHasChanged();
        await ScrollToBottom();

        if (await TryHandleHistoryCommandAsync(userMessage))
        {
            _isLoading = false;
            _currentAssistantMessage = string.Empty;
            StateHasChanged();
            await ScrollToBottom();
            await SaveCurrentSession();
            return;
        }

        var contentBuilder = new StringBuilder();
        var shouldClearPendingAttachments = false;

        try
        {
            var preparedSubmission = await MessageSubmissionService.PrepareAsync(
                new MessageDraft
                {
                    SessionId = _sessionId,
                    ToolId = _selectedToolId,
                    Channel = MessageSubmissionChannel.Mobile,
                    Text = userMessage,
                    Attachments = [.. _messageAttachmentComposer.PendingAttachments],
                    SubmittedBy = ResolveSubmittedBy()
                });

            userMessageModel.Content = preparedSubmission.UserMessage.Content;
            userMessageModel.Attachments = preparedSubmission.UserMessage.Attachments;
            userMessageModel.CliToolId = preparedSubmission.UserMessage.CliToolId;
            StateHasChanged();

            await foreach (var chunk in CliExecutorService.ExecuteStreamAsync(
                preparedSubmission.ExecutionRequest))
            {
                if (chunk.IsError)
                {
                    _messages.Add(new ChatMessage
                    {
                        Role = "assistant",
                        Content = string.Empty,
                        HasError = true,
                        ErrorMessage = chunk.ErrorMessage ?? chunk.Content,
                        CreatedAt = DateTime.Now,
                        CliToolId = _selectedToolId,
                        IsCompleted = true
                    });
                    break;
                }
                else if (chunk.IsCompleted)
                {
                    if (_isJsonlOutputActive)
                    {
                        ProcessJsonlChunk(string.Empty, flush: true);
                        var finalJsonlContent = GetJsonlAssistantMessage();
                        _currentAssistantMessage = finalJsonlContent;
                        contentBuilder.Clear();
                        contentBuilder.Append(finalJsonlContent);
                        UpdateOutputRaw(finalJsonlContent);
                    }

                    var finalContent = contentBuilder.ToString();
                    if (!string.IsNullOrEmpty(finalContent))
                    {
                        _messages.Add(new ChatMessage
                        {
                            Role = "assistant",
                            Content = finalContent,
                            CreatedAt = DateTime.Now,
                            CliToolId = _selectedToolId,
                            IsCompleted = true
                        });
                    }

                    shouldClearPendingAttachments = true;
                    break;
                }
                else
                {
                    var chunkContent = chunk.Content ?? string.Empty;
                    if (_isJsonlOutputActive)
                    {
                        ProcessJsonlChunk(chunkContent, flush: false);
                        var liveContent = GetJsonlAssistantMessage();
                        _currentAssistantMessage = liveContent;
                        UpdateOutputRaw(liveContent);
                    }
                    else
                    {
                        contentBuilder.Append(chunkContent);
                        _currentAssistantMessage = contentBuilder.ToString();
                        UpdateOutputRaw(_currentAssistantMessage);
                    }

                    await InvokeAsync(StateHasChanged);
                }
            }
        }
        catch (Exception ex)
        {
            _messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = string.Empty,
                HasError = true,
                ErrorMessage = $"{T("codeAssistant.errorOccurred")}: {ex.Message}",
                CreatedAt = DateTime.Now,
                CliToolId = _selectedToolId,
                IsCompleted = true
            });
        }
        finally
        {
            if (_isJsonlOutputActive)
            {
                ProcessJsonlChunk(string.Empty, flush: true);
                _currentAssistantMessage = GetJsonlAssistantMessage();

                if (_progressTracker != null)
                {
                    if (_messages.LastOrDefault()?.HasError == true)
                    {
                        _progressTracker.Fail(_messages.LastOrDefault()?.ErrorMessage ?? T("codeAssistant.errorOccurred"));
                    }
                    else
                    {
                        _progressTracker.Complete();
                    }
                }
            }

            _isLoading = false;
            _currentAssistantMessage = string.Empty;
            if (shouldClearPendingAttachments)
            {
                _messageAttachmentComposer.Clear();
            }
            StateHasChanged();
            await ScrollToBottom();
            await SaveCurrentSession();
        }
    }

    private async Task OnSubmitSuperpowersQuickInputAsync(ChatMessage sourceMessage)
    {
        await SubmitSuperpowersQuickActionAsync(sourceMessage, SuperpowersQuickActionRequestType.QuickInput);
    }

    private async Task OnContinueSuperpowersActionAsync(ChatMessage sourceMessage)
    {
        await SubmitSuperpowersQuickActionAsync(sourceMessage, SuperpowersQuickActionRequestType.Continue);
    }

    private async Task OnExecuteSuperpowersPlanAsync(ChatMessage sourceMessage)
    {
        await SubmitSuperpowersQuickActionAsync(sourceMessage, SuperpowersQuickActionRequestType.ExecutePlan);
    }

    private async Task OnExecuteSuperpowersSubagentPlanAsync(ChatMessage sourceMessage)
    {
        await SubmitSuperpowersQuickActionAsync(sourceMessage, SuperpowersQuickActionRequestType.ExecuteSubagentPlan);
    }

    private async Task OnStopSuperpowersActionAsync(ChatMessage sourceMessage)
    {
        var eligibility = CurrentSuperpowersQuickActionEligibility;
        if (!_isLoading || !IsSuperpowersQuickActionEligible(sourceMessage, eligibility))
        {
            return;
        }

        CancelExecution();
        await Task.CompletedTask;
    }

    private async Task SubmitSuperpowersQuickActionAsync(
        ChatMessage sourceMessage,
        SuperpowersQuickActionRequestType requestType)
    {
        var eligibility = CurrentSuperpowersQuickActionEligibility;
        var viewState = CurrentSuperpowersQuickActionViewState;
        if (_isLoading
            || viewState.IsDisabled
            || !IsSuperpowersQuickActionEligible(sourceMessage, eligibility))
        {
            return;
        }

        var message = SuperpowersQuickActionSubmissionHelper.BuildMessage(requestType, _superpowersQuickInput);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        if (requestType != SuperpowersQuickActionRequestType.Continue)
        {
            var capabilityAvailable = await EnsureSuperpowersCapabilityAvailableAsync(forceRefresh: false);
            if (!capabilityAvailable)
            {
                return;
            }
        }

        if (requestType == SuperpowersQuickActionRequestType.QuickInput)
        {
            _superpowersQuickInput = string.Empty;
        }

        await SendMessageCoreAsync(message, clearComposerInput: false, closeTransientPanels: true);
    }

    private async Task RetrySuperpowersCapabilityAsync(ChatMessage sourceMessage)
    {
        var eligibility = CurrentSuperpowersQuickActionEligibility;
        if (_isLoading || !IsSuperpowersQuickActionEligible(sourceMessage, eligibility))
        {
            return;
        }

        await EnsureSuperpowersCapabilityAvailableAsync(forceRefresh: true);
    }

    private async Task OnSubmitGoalQuickInputAsync(ChatMessage sourceMessage)
    {
        var eligibility = CurrentGoalQuickActionEligibility;
        var viewState = CurrentGoalQuickActionViewState;
        if (_isLoading
            || viewState.IsDisabled
            || !IsSuperpowersQuickActionEligible(sourceMessage, eligibility))
        {
            return;
        }

        var message = GoalQuickActionSubmissionHelper.BuildMessage(_goalQuickInput);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var capabilityAvailable = await EnsureGoalCapabilityAvailableAsync(forceRefresh: false);
        if (!capabilityAvailable)
        {
            return;
        }

        _goalQuickInput = string.Empty;
        await SendMessageCoreAsync(message, clearComposerInput: false, closeTransientPanels: true);
    }

    private Task OnGoalStatusActionAsync(ChatMessage sourceMessage)
        => SendGoalActionAsync(sourceMessage, FeishuHelpCardAction.StatusGoalAction);

    private Task OnGoalPauseActionAsync(ChatMessage sourceMessage)
    {
        var eligibility = CurrentGoalQuickActionEligibility;
        if (!_isLoading || !IsSuperpowersQuickActionEligible(sourceMessage, eligibility))
        {
            return Task.CompletedTask;
        }

        CancelExecution();
        return Task.CompletedTask;
    }

    private Task OnGoalClearActionAsync(ChatMessage sourceMessage)
        => SendGoalActionAsync(sourceMessage, FeishuHelpCardAction.ClearGoalAction);

    private Task OnGoalResumeActionAsync(ChatMessage sourceMessage)
        => SendGoalActionAsync(sourceMessage, FeishuHelpCardAction.ResumeGoalAction);

    private async Task SendGoalActionAsync(ChatMessage sourceMessage, string action)
    {
        var eligibility = CurrentGoalQuickActionEligibility;
        var viewState = CurrentGoalQuickActionViewState;
        if (_isLoading
            || viewState.IsDisabled
            || !IsSuperpowersQuickActionEligible(sourceMessage, eligibility))
        {
            return;
        }

        var message = GoalPromptBuilder.BuildPromptForAction(action, input: null);
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var capabilityAvailable = await EnsureGoalCapabilityAvailableAsync(forceRefresh: false);
        if (!capabilityAvailable)
        {
            return;
        }

        await SendMessageCoreAsync(message, clearComposerInput: false, closeTransientPanels: true);
    }

    private async Task RetryGoalCapabilityAsync(ChatMessage sourceMessage)
    {
        var eligibility = CurrentGoalQuickActionEligibility;
        if (_isLoading || !IsSuperpowersQuickActionEligible(sourceMessage, eligibility))
        {
            return;
        }

        await EnsureGoalCapabilityAvailableAsync(forceRefresh: true);
    }

    private async Task<bool> EnsureSuperpowersCapabilityAvailableAsync(bool forceRefresh)
    {
        await RefreshSuperpowersCapabilityPresentationContextAsync();

        if (_superpowersCapabilityPresentation.IsChecking)
        {
            return false;
        }

        if (!forceRefresh && _superpowersCapabilityPresentation.State == SuperpowersCapabilityState.Available)
        {
            return true;
        }

        _superpowersCapabilityPresentation = SuperpowersCapabilityPresentationState.Checking(SuperpowersCapabilityCheckingText);
        await InvokeAsync(StateHasChanged);

        var probeResult = await SuperpowersCapabilityService.ProbeAsync(
            BuildSuperpowersCapabilityContext(),
            forceRefresh: forceRefresh || _superpowersCapabilityPresentation.State != SuperpowersCapabilityState.Available);

        _superpowersCapabilityPresentation = probeResult.Outcome switch
        {
            SuperpowersCapabilityProbeOutcome.Available => SuperpowersCapabilityPresentationState.Available,
            SuperpowersCapabilityProbeOutcome.MissingCapability => SuperpowersCapabilityPresentationState.Unavailable(
                string.IsNullOrWhiteSpace(probeResult.Message)
                    ? SuperpowersCapabilityUnavailableText
                    : probeResult.Message),
            _ => SuperpowersCapabilityPresentationState.ProbeFailed(
                string.IsNullOrWhiteSpace(probeResult.Message)
                    ? SuperpowersCapabilityProbeFailedText
                    : probeResult.Message)
        };

        await InvokeAsync(StateHasChanged);
        return _superpowersCapabilityPresentation.State == SuperpowersCapabilityState.Available;
    }

    private async Task<bool> EnsureGoalCapabilityAvailableAsync(bool forceRefresh)
    {
        await RefreshGoalCapabilityPresentationContextAsync();

        if (_goalCapabilityPresentation.IsChecking)
        {
            return false;
        }

        if (!forceRefresh && _goalCapabilityPresentation.State == GoalCapabilityState.Available)
        {
            return true;
        }

        _goalCapabilityPresentation = GoalCapabilityPresentationState.Checking(GoalCapabilityCheckingText);
        await InvokeAsync(StateHasChanged);

        var probeResult = await GoalCapabilityService.ProbeAsync(
            BuildGoalCapabilityContext(),
            forceRefresh: forceRefresh);

        _goalCapabilityPresentation = probeResult.Outcome switch
        {
            GoalCapabilityProbeOutcome.Available => GoalCapabilityPresentationState.Available,
            GoalCapabilityProbeOutcome.UnsupportedTool or
            GoalCapabilityProbeOutcome.UnsupportedVersion or
            GoalCapabilityProbeOutcome.MissingFeature => GoalCapabilityPresentationState.Unavailable(
                string.IsNullOrWhiteSpace(probeResult.Message)
                    ? GoalCapabilityUnavailableText
                    : probeResult.Message),
            _ => GoalCapabilityPresentationState.ProbeFailed(
                string.IsNullOrWhiteSpace(probeResult.Message)
                    ? GoalCapabilityProbeFailedText
                    : probeResult.Message)
        };

        await InvokeAsync(StateHasChanged);
        return _goalCapabilityPresentation.State == GoalCapabilityState.Available;
    }

    private SuperpowersCapabilityContext BuildSuperpowersCapabilityContext()
    {
        return new SuperpowersCapabilityContext
        {
            ToolId = _selectedToolId,
            ProviderId = GetCurrentPinnedProviderIdForSuperpowers(),
            WorkspacePath = GetCurrentSuperpowersWorkspacePath()
        };
    }

    private GoalCapabilityContext BuildGoalCapabilityContext()
    {
        return new GoalCapabilityContext
        {
            ToolId = _selectedToolId,
            ProviderId = GetCurrentPinnedProviderIdForGoal(),
            WorkspacePath = GetCurrentGoalWorkspacePath()
        };
    }

    private string? GetCurrentPinnedProviderIdForSuperpowers()
    {
        if (_currentSession == null || string.IsNullOrWhiteSpace(_currentSession.CcSwitchProviderId))
        {
            return null;
        }

        var selectedToolId = NormalizeSuperpowersCapabilityToolId(_selectedToolId);
        var sessionToolId = NormalizeSuperpowersCapabilityToolId(_currentSession.CcSwitchSnapshotToolId ?? _currentSession.ToolId);
        if (!string.Equals(selectedToolId, sessionToolId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return _currentSession.CcSwitchProviderId;
    }

    private string? GetCurrentSuperpowersWorkspacePath()
    {
        try
        {
            return CliExecutorService.GetSessionWorkspacePath(_sessionId)
                   ?? _currentSession?.WorkspacePath;
        }
        catch
        {
            return _currentSession?.WorkspacePath;
        }
    }

    private string? GetCurrentPinnedProviderIdForGoal()
    {
        if (_currentSession == null || string.IsNullOrWhiteSpace(_currentSession.CcSwitchProviderId))
        {
            return null;
        }

        var selectedToolId = NormalizeSuperpowersCapabilityToolId(_selectedToolId);
        var sessionToolId = NormalizeSuperpowersCapabilityToolId(_currentSession.CcSwitchSnapshotToolId ?? _currentSession.ToolId);
        if (!string.Equals(selectedToolId, sessionToolId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return _currentSession.CcSwitchProviderId;
    }

    private string? GetCurrentGoalWorkspacePath()
    {
        try
        {
            return CliExecutorService.GetSessionWorkspacePath(_sessionId)
                   ?? _currentSession?.WorkspacePath;
        }
        catch
        {
            return _currentSession?.WorkspacePath;
        }
    }

    private async Task RefreshSuperpowersCapabilityPresentationContextAsync()
    {
        var nextContextKey = await ResolveSuperpowersCapabilityPresentationContextKeyAsync();
        if (string.Equals(_superpowersCapabilityPresentationContextKey, nextContextKey, StringComparison.Ordinal))
        {
            return;
        }

        _superpowersCapabilityPresentationContextKey = nextContextKey;
        _superpowersCapabilityPresentation = SuperpowersCapabilityPresentationState.Unknown;
    }

    private async Task<string> ResolveSuperpowersCapabilityPresentationContextKeyAsync()
    {
        try
        {
            var snapshot = await SuperpowersCapabilityService.GetStateAsync(BuildSuperpowersCapabilityContext());
            if (!string.IsNullOrWhiteSpace(snapshot.CacheKey))
            {
                return snapshot.CacheKey;
            }
        }
        catch
        {
        }

        return BuildFallbackSuperpowersCapabilityPresentationContextKey();
    }

    private async Task RefreshGoalCapabilityPresentationContextAsync()
    {
        var nextContextKey = await ResolveGoalCapabilityPresentationContextKeyAsync();
        if (string.Equals(_goalCapabilityPresentationContextKey, nextContextKey, StringComparison.Ordinal))
        {
            return;
        }

        _goalCapabilityPresentationContextKey = nextContextKey;
        _goalCapabilityPresentation = GoalCapabilityPresentationState.Unknown;
    }

    private async Task<string> ResolveGoalCapabilityPresentationContextKeyAsync()
    {
        try
        {
            var snapshot = await GoalCapabilityService.GetStateAsync(BuildGoalCapabilityContext());
            if (!string.IsNullOrWhiteSpace(snapshot.CacheKey))
            {
                return snapshot.CacheKey;
            }
        }
        catch
        {
        }

        return BuildFallbackGoalCapabilityPresentationContextKey();
    }

    private void InvalidateSuperpowersCapabilityPresentation()
    {
        _superpowersCapabilityPresentationContextKey = string.Empty;
        _superpowersCapabilityPresentation = SuperpowersCapabilityPresentationState.Unknown;
        _goalCapabilityPresentationContextKey = string.Empty;
        _goalCapabilityPresentation = GoalCapabilityPresentationState.Unknown;
    }

    private string BuildFallbackSuperpowersCapabilityPresentationContextKey()
    {
        return $"{NormalizeSuperpowersCapabilityToolId(_selectedToolId) ?? string.Empty}::{GetCurrentPinnedProviderIdForSuperpowers() ?? string.Empty}";
    }

    private string BuildFallbackGoalCapabilityPresentationContextKey()
    {
        return $"{NormalizeSuperpowersCapabilityToolId(_selectedToolId) ?? string.Empty}::{GetCurrentPinnedProviderIdForGoal() ?? string.Empty}";
    }

    private bool IsGoalQuickActionToolSupported()
    {
        var selectedToolId = NormalizeSuperpowersCapabilityToolId(_selectedToolId);
        if (string.Equals(selectedToolId, "codex", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var sessionToolId = NormalizeSuperpowersCapabilityToolId(_currentSession?.CcSwitchSnapshotToolId ?? _currentSession?.ToolId);
        return string.Equals(sessionToolId, "codex", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizeSuperpowersCapabilityToolId(string? toolId)
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

    private readonly record struct SuperpowersQuickActionViewState(
        string? MessageId,
        bool ShowQuickInput,
        bool ShowPlanActions,
        bool ContinueActionDisabled,
        bool IsDisabled,
        string? StatusMessage,
        bool ShowRetryAction,
        bool RetryActionDisabled,
        string RetryActionText);

    private readonly record struct SuperpowersCapabilityPresentationState(
        SuperpowersCapabilityState State,
        bool IsChecking,
        string? StatusMessage,
        bool ShowRetryAction)
    {
        public static SuperpowersCapabilityPresentationState Unknown => new(
            SuperpowersCapabilityState.Unknown,
            IsChecking: false,
            StatusMessage: null,
            ShowRetryAction: false);

        public static SuperpowersCapabilityPresentationState Available => new(
            SuperpowersCapabilityState.Available,
            IsChecking: false,
            StatusMessage: null,
            ShowRetryAction: false);

        public static SuperpowersCapabilityPresentationState Checking(string statusMessage) => new(
            SuperpowersCapabilityState.Unknown,
            IsChecking: true,
            StatusMessage: statusMessage,
            ShowRetryAction: false);

        public static SuperpowersCapabilityPresentationState Unavailable(string statusMessage) => new(
            SuperpowersCapabilityState.Unavailable,
            IsChecking: false,
            StatusMessage: statusMessage,
            ShowRetryAction: true);

        public static SuperpowersCapabilityPresentationState ProbeFailed(string statusMessage) => new(
            SuperpowersCapabilityState.Unknown,
            IsChecking: false,
            StatusMessage: statusMessage,
            ShowRetryAction: true);
    }

    private readonly record struct GoalQuickActionViewState(
        string? MessageId,
        bool IsDisabled,
        string? StatusMessage,
        bool ShowRetryAction,
        bool RetryActionDisabled,
        string RetryActionText);

    private readonly record struct GoalCapabilityPresentationState(
        GoalCapabilityState State,
        bool IsChecking,
        string? StatusMessage,
        bool ShowRetryAction)
    {
        public static GoalCapabilityPresentationState Unknown => new(
            GoalCapabilityState.Unknown,
            IsChecking: false,
            StatusMessage: null,
            ShowRetryAction: false);

        public static GoalCapabilityPresentationState Available => new(
            GoalCapabilityState.Available,
            IsChecking: false,
            StatusMessage: null,
            ShowRetryAction: false);

        public static GoalCapabilityPresentationState Checking(string statusMessage) => new(
            GoalCapabilityState.Unknown,
            IsChecking: true,
            StatusMessage: statusMessage,
            ShowRetryAction: false);

        public static GoalCapabilityPresentationState Unavailable(string statusMessage) => new(
            GoalCapabilityState.Unavailable,
            IsChecking: false,
            StatusMessage: statusMessage,
            ShowRetryAction: true);

        public static GoalCapabilityPresentationState ProbeFailed(string statusMessage) => new(
            GoalCapabilityState.Unknown,
            IsChecking: false,
            StatusMessage: statusMessage,
            ShowRetryAction: true);
    }

    private async Task HandleSuperpowersQuickInputKeyDown(ChatMessage message, KeyboardEventArgs args)
    {
        if (!string.Equals(args.Key, "Enter", StringComparison.Ordinal))
        {
            return;
        }

        await OnSubmitSuperpowersQuickInputAsync(message);
    }

    private async Task HandleGoalQuickInputKeyDown(ChatMessage message, KeyboardEventArgs args)
    {
        if (!string.Equals(args.Key, "Enter", StringComparison.Ordinal))
        {
            return;
        }

        await OnSubmitGoalQuickInputAsync(message);
    }

    private async Task<bool> TryHandleHistoryCommandAsync(string message)
    {
        if (!IsHistoryCommand(message))
        {
            return false;
        }

        var assistantMessage = new ChatMessage
        {
            Role = "assistant",
            CliToolId = _selectedToolId,
            CreatedAt = DateTime.Now,
            IsCompleted = true
        };

        try
        {
            var session = _currentSession ?? await SessionHistoryManager.GetSessionAsync(_sessionId);
            var cliThreadId = CliExecutorService.GetCliThreadId(_sessionId)
                              ?? session?.CliThreadId
                              ?? _activeThreadId;
            var toolId = string.IsNullOrWhiteSpace(_selectedToolId) ? session?.ToolId : _selectedToolId;
            var workspacePath = session?.WorkspacePath ?? GetSafeWorkspacePath();
            var toolLabel = _availableTools.FirstOrDefault(tool => tool.Id == toolId)?.Name ?? toolId ?? "CLI";
            var historyLimit = ResolveHistoryCommandLimit(message);

            if (string.IsNullOrWhiteSpace(toolId) || string.IsNullOrWhiteSpace(cliThreadId))
            {
                assistantMessage.Content = "当前会话尚未绑定 CLI 原生会话 ID，暂时无法读取历史消息。请先在该会话中执行一次 CLI 对话。";
            }
            else
            {
                var history = await ExternalCliSessionHistoryService.GetRecentHistoryAsync(
                    toolId,
                    cliThreadId,
                    maxCount: historyLimit,
                    workspacePath: workspacePath);
                assistantMessage.Content = ExternalCliHistoryTextBuilder.Build(
                    "当前 CLI 会话历史",
                    history.Messages,
                    toolLabel,
                    workspacePath,
                    cliThreadId,
                    history.SourcePath);
            }
        }
        catch (Exception ex)
        {
            assistantMessage.HasError = true;
            assistantMessage.ErrorMessage = ex.Message;
            assistantMessage.Content = $"读取 CLI 原生历史失败: {ex.Message}";
        }

        _messages.Add(assistantMessage);
        UpdateOutputRaw(assistantMessage.Content);
        StateHasChanged();
        return true;
    }

    private string GetSafeWorkspacePath()
    {
        try
        {
            return CliExecutorService.GetSessionWorkspacePath(_sessionId);
        }
        catch
        {
            return _currentSession?.WorkspacePath ?? "(工作区未初始化或已失效)";
        }
    }

    private static bool IsHistoryCommand(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        var trimmed = message.Trim();
        return string.Equals(trimmed, "/history", StringComparison.OrdinalIgnoreCase)
               || trimmed.StartsWith("/history ", StringComparison.OrdinalIgnoreCase);
    }

    private static int ResolveHistoryCommandLimit(string? message)
    {
        const int defaultLimit = 50;
        const int maxLimit = 200;

        if (string.IsNullOrWhiteSpace(message))
        {
            return defaultLimit;
        }

        var segments = message
            .Trim()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2)
        {
            return defaultLimit;
        }

        var requestedLimit = segments[1];
        if (string.Equals(requestedLimit, "all", StringComparison.OrdinalIgnoreCase))
        {
            return maxLimit;
        }

        return int.TryParse(requestedLimit, out var parsedLimit)
            ? Math.Clamp(parsedLimit, 1, maxLimit)
            : defaultLimit;
    }

    private async Task ScrollToBottom()
    {
        try
        {
            await JSRuntime.InvokeVoidAsync("eval", @"
                const el = document.getElementById('mobile-chat-messages');
                if (el) el.scrollTop = el.scrollHeight;
            ");
        }
        catch { }
    }
    
    private async Task FocusInputAndScroll()
    {
        try
        {
            await JSRuntime.InvokeVoidAsync("eval", @"
                const input = document.getElementById('mobile-input-message');
                if (input) input.focus();
            ");
        }
        catch { }
    }
    
    private void HandleMobileKeyDown(KeyboardEventArgs e)
    {
        // 移动端不需要回车发送，使用按钮
    }
    
    /// <summary>
    /// 处理用户回答问题（移动端）
    /// </summary>
    private async Task HandleAnswerQuestion((string toolUseId, string answer) args)
    {
        var (toolUseId, answer) = args;
        
        if (string.IsNullOrEmpty(toolUseId) || string.IsNullOrEmpty(answer))
        {
            return;
        }

        // 更新状态显示
        Console.WriteLine($"[Mobile HandleAnswerQuestion] toolUseId={toolUseId}, answer={answer}");
        
        // 将用户回答作为新消息发送
        await SendUserAnswerToSession(answer);
    }

    /// <summary>
    /// 将用户回答发送到会话（移动端）
    /// </summary>
    private async Task SendUserAnswerToSession(string answer)
    {
        if (_isLoading)
        {
            Console.WriteLine("[Mobile SendUserAnswerToSession] 当前正在加载中，跳过发送");
            return;
        }

        // 设置输入框内容为用户的回答
        _inputMessage = answer;
        
        // 触发发送
        await SendMessage();
    }
    
    #endregion
    
    #region JSONL事件处理
    
    private readonly List<JsonlDisplayItem> _jsonlEvents = new();
    private bool _isJsonlOutputActive = false;
    private string _activeThreadId = string.Empty;
    private string _rawOutput = string.Empty;
    private bool _disposed = false;
    private string _jsonlPendingBuffer = string.Empty;
    private StringBuilder? _jsonlAssistantMessageBuilder;

    // 输出结果（Tab=输出结果）持久化
    private System.Threading.Timer? _outputStateSaveTimer;
    private readonly object _outputStateSaveLock = new object();
    private bool _hasPendingOutputStateSave = false;
    private const int OutputStateSaveDebounceMs = 800;
    
    private const int InitialDisplayCount = 20;
    private int _displayedEventCount = InitialDisplayCount;
    private bool _hasMoreEvents => _jsonlEvents.Count > _displayedEventCount;
    
    private readonly Dictionary<string, bool> _jsonlGroupOpenState = new();
    
    private void InitializeJsonlState(bool enableJsonl)
    {
        _isJsonlOutputActive = enableJsonl;
        _jsonlPendingBuffer = string.Empty;
        _activeThreadId = string.Empty;
        _jsonlEvents.Clear();
        _jsonlAssistantMessageBuilder = enableJsonl ? new StringBuilder() : null;
        ResetEventDisplayCount();
    }

    private void ResetEventDisplayCount()
    {
        _displayedEventCount = InitialDisplayCount;
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
    }

    private void HandleJsonlLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var adapter = GetCurrentAdapter();
        if (adapter != null)
        {
            HandleJsonlLineWithAdapter(line, adapter);
            return;
        }

        HandleJsonlLineLegacy(line);
    }

    private void HandleJsonlLineWithAdapter(string line, ICliToolAdapter adapter)
    {
        try
        {
            var outputEvent = adapter.ParseOutputLine(line);
            if (outputEvent == null)
            {
                return;
            }

            var sessionId = adapter.ExtractSessionId(outputEvent);
            if (!string.IsNullOrWhiteSpace(sessionId))
            {
                _activeThreadId = sessionId;
                CliExecutorService.SetCliThreadId(_sessionId, sessionId);
            }

            var assistantMessage = adapter.ExtractAssistantMessage(outputEvent);
            if (!string.IsNullOrEmpty(assistantMessage))
            {
                _jsonlAssistantMessageBuilder?.Append(assistantMessage);
            }

            var displayItem = new JsonlDisplayItem
            {
                Type = outputEvent.EventType,
                Title = adapter.GetEventTitle(outputEvent),
                Content = GetEventDisplayContent(outputEvent, outputEvent.Content),
                ItemType = outputEvent.ItemType,
                IsUnknown = outputEvent.IsUnknown
            };

            if (outputEvent.Usage != null)
            {
                displayItem.Usage = new JsonlUsageDetail
                {
                    InputTokens = outputEvent.Usage.InputTokens,
                    CachedInputTokens = outputEvent.Usage.CachedInputTokens,
                    OutputTokens = outputEvent.Usage.OutputTokens
                };
            }

            // 转换用户问题（用于 AskUserQuestion 工具）
            if (outputEvent.UserQuestion != null)
            {
                displayItem.UserQuestion = ConvertToUserQuestion(outputEvent.UserQuestion);
            }

            _jsonlEvents.Add(displayItem);

            UpdateProgressTracker(outputEvent.EventType);
        }
        catch (Exception ex)
        {
            AddUnknownJsonlEvent($"适配器处理失败: {ex.Message}", line);
        }
    }

    private void HandleJsonlLineLegacy(string line)
    {
        try
        {
            using var jsonDoc = JsonDocument.Parse(line);
            var root = jsonDoc.RootElement;

            var eventType = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? string.Empty : string.Empty;
            var itemType = root.TryGetProperty("item_type", out var itemTypeProp) ? itemTypeProp.GetString() : null;

            var eventContent = ExtractEventContent(root, eventType);
            var eventTitle = GetEventTitle(eventType, itemType);

            if (!string.IsNullOrEmpty(eventType) && ShouldDisplayEvent(eventType, eventContent))
            {
                OnJsonlEvent(new JsonlDisplayItem
                {
                    Type = eventType,
                    Title = eventTitle,
                    Content = eventContent,
                    ItemType = itemType
                });
            }
        }
        catch (Exception ex)
        {
            AddUnknownJsonlEvent($"解析 JSONL 失败: {ex.Message}", line);
        }
    }

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

    private string GetEventDisplayContent(CliOutputEvent outputEvent, string? fallbackContent)
    {
        if (string.Equals(outputEvent.EventType, "turn.completed", StringComparison.OrdinalIgnoreCase))
        {
            // 当有 Usage 信息时，Content 设为空，只显示 Token 统计，避免与最后一条消息重复
            return outputEvent.Usage is null
                ? T("cliEvent.content.turnCompleted")
                : string.Empty;
        }

        // result 类型事件保留最终文本，便于在缺少 assistant 事件时兜底展示
        if (string.Equals(outputEvent.EventType, "result", StringComparison.OrdinalIgnoreCase))
        {
            return fallbackContent ?? T("cliEvent.content.turnCompleted");
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

    private void AddUnknownJsonlEvent(string reason, string rawLine)
    {
        _jsonlEvents.Add(new JsonlDisplayItem
        {
            Type = "unknown",
            Title = T("cliEvent.title.unknown"),
            Content = $"{reason}\n{rawLine}",
            IsUnknown = true
        });
    }
    
    /// <summary>
    /// 根据事件类型提取内容
    /// </summary>
    private string ExtractEventContent(JsonElement root, string eventType)
    {
        try
        {
            switch (eventType)
            {
                case "assistant":
                    // 助手消息: message.content[0].text
                    if (root.TryGetProperty("message", out var messageElement) &&
                        messageElement.TryGetProperty("content", out var contentArray) &&
                        contentArray.ValueKind == JsonValueKind.Array)
                    {
                        var textParts = new List<string>();
                        foreach (var item in contentArray.EnumerateArray())
                        {
                            if (item.TryGetProperty("type", out var typeEl) && 
                                typeEl.GetString() == "text" &&
                                item.TryGetProperty("text", out var textEl))
                            {
                                textParts.Add(textEl.GetString() ?? "");
                            }
                        }
                        return string.Join("\n", textParts);
                    }
                    break;
                    
                case "result":
                    // 执行结果: result 字段
                    if (root.TryGetProperty("result", out var resultElement))
                    {
                        return resultElement.GetString() ?? "";
                    }
                    break;
                    
                case "tool_use":
                    // 工具调用：显示工具名称和输入
                    var sb = new StringBuilder();
                    if (root.TryGetProperty("name", out var nameElement))
                    {
                        sb.AppendLine($"工具: {nameElement.GetString()}");
                    }
                    if (root.TryGetProperty("input", out var inputElement))
                    {
                        var inputStr = inputElement.ValueKind == JsonValueKind.String 
                            ? inputElement.GetString() 
                            : inputElement.GetRawText();
                        if (!string.IsNullOrEmpty(inputStr) && inputStr.Length < 500)
                        {
                            sb.AppendLine($"输入: {inputStr}");
                        }
                    }
                    return sb.ToString().TrimEnd();
                    
                case "tool_result":
                    // 工具结果
                    if (root.TryGetProperty("content", out var toolContent))
                    {
                        var contentStr = toolContent.ValueKind == JsonValueKind.String 
                            ? toolContent.GetString() 
                            : toolContent.GetRawText();
                        if (!string.IsNullOrEmpty(contentStr) && contentStr.Length < 1000)
                        {
                            return contentStr;
                        }
                        return "[结果内容过长...]";
                    }
                    break;
                    
                case "error":
                    // 错误消息
                    if (root.TryGetProperty("message", out var errMsgElement))
                    {
                        return errMsgElement.GetString() ?? "发生错误";
                    }
                    break;
            }
            
            // 默认尝试获取 content 字段
            if (root.TryGetProperty("content", out var defaultContent))
            {
                if (defaultContent.ValueKind == JsonValueKind.String)
                {
                    return defaultContent.GetString() ?? "";
                }
            }
            
            return "";
        }
        catch
        {
            return "";
        }
    }
    
    /// <summary>
    /// 获取事件标题
    /// </summary>
    private string GetEventTitle(string eventType, string? itemType)
    {
        return eventType switch
        {
            "assistant" => T("cliEvent.badge.reply"),
            "result" => T("cliEvent.badge.result"),
            "tool_use" => T("cliEvent.badge.toolUse"),
            "tool_result" => T("cliEvent.badge.toolResult"),
            "error" => T("cliEvent.badge.error"),
            "system" => T("cliEvent.badge.system"),
            "user" => T("cliEvent.badge.input"),
            _ => eventType
        };
    }
    
    /// <summary>
    /// 判断事件是否应该显示
    /// </summary>
    private bool ShouldDisplayEvent(string eventType, string content)
    {
        // 忽略系统初始化事件
        if (eventType == "system") return false;
        
        // 只显示有内容的事件
        if (eventType == "assistant" || eventType == "result")
        {
            return !string.IsNullOrWhiteSpace(content);
        }
        
        // 工具调用和结果始终显示
        if (eventType == "tool_use" || eventType == "tool_result")
        {
            return true;
        }
        
        // 错误始终显示
        if (eventType == "error")
        {
            return true;
        }
        
        return !string.IsNullOrWhiteSpace(content);
    }
    
    private void OnJsonlEvent(JsonlDisplayItem item)
    {
        _isJsonlOutputActive = true;
        _jsonlEvents.Add(item);
        InvokeAsync(StateHasChanged);
    }
    
    private void LoadMoreEvents()
    {
        _displayedEventCount += 10;
        StateHasChanged();
    }
    
    private List<JsonlEventGroup> GetPagedJsonlEventGroups()
    {
        var pagedEvents = _jsonlEvents.Take(_displayedEventCount).ToList();
        return GetJsonlEventGroups(pagedEvents);
    }
    
    private List<JsonlEventGroup> GetJsonlEventGroups(List<JsonlDisplayItem> events)
    {
        var groups = new List<JsonlEventGroup>();
        JsonlEventGroup? activeCommandGroup = null;
        JsonlEventGroup? activeToolGroup = null;

        for (var i = 0; i < events.Count; i++)
        {
            var evt = events[i];

            // 检查是否为命令执行事件 (Codex)
            if (IsCodexCommandExecutionEvent(evt))
            {
                if (evt.Type == "item.started")
                {
                    if (activeCommandGroup != null && !activeCommandGroup.IsCompleted)
                    {
                        activeCommandGroup.IsCompleted = true;
                    }

                    activeCommandGroup = new JsonlEventGroup
                    {
                        Id = $"cmd-{i}",
                        Kind = "command_execution",
                        Title = "命令执行",
                        IsCollapsible = true,
                        IsCompleted = false
                    };
                    activeCommandGroup.Items.Add(evt);
                    groups.Add(activeCommandGroup);
                    continue;
                }

                if (activeCommandGroup != null)
                {
                    activeCommandGroup.Items.Add(evt);
                    if (evt.Type == "item.completed")
                    {
                        activeCommandGroup.IsCompleted = true;
                        activeCommandGroup = null;
                    }
                    continue;
                }

                groups.Add(new JsonlEventGroup
                {
                    Id = $"evt-{i}",
                    Kind = "single",
                    Title = evt.Title,
                    IsCompleted = true,
                    IsCollapsible = false,
                    Items = { evt }
                });
                continue;
            }

            // 检查是否为工具调用事件 (Claude Code)
            if (IsClaudeToolEvent(evt))
            {
                if (evt.Type == "tool_use")
                {
                    if (activeToolGroup != null && !activeToolGroup.IsCompleted)
                    {
                        activeToolGroup.IsCompleted = true;
                    }

                    activeToolGroup = new JsonlEventGroup
                    {
                        Id = $"tool-{i}",
                        Kind = "tool_call",
                        Title = "工具调用",
                        IsCollapsible = true,
                        IsCompleted = false
                    };
                    activeToolGroup.Items.Add(evt);
                    if (IsUserQuestionEvent(evt))
                    {
                        activeToolGroup.IsCollapsible = false;
                        activeToolGroup.IsCompleted = false;
                    }
                    groups.Add(activeToolGroup);
                    continue;
                }

                if (activeToolGroup != null)
                {
                    activeToolGroup.Items.Add(evt);
                    if (IsUserQuestionEvent(evt))
                    {
                        activeToolGroup.IsCollapsible = false;
                        activeToolGroup.IsCompleted = false;
                    }
                    if (evt.Type == "tool_result")
                    {
                        if (activeToolGroup.IsCollapsible)
                        {
                            activeToolGroup.IsCompleted = true;
                        }
                        activeToolGroup = null;
                    }
                    continue;
                }

                groups.Add(new JsonlEventGroup
                {
                    Id = $"evt-{i}",
                    Kind = "single",
                    Title = evt.Title,
                    IsCompleted = true,
                    IsCollapsible = false,
                    Items = { evt }
                });
                continue;
            }

            // 完成类型事件：设置为可折叠（默认折叠）
            if (IsCompletionEvent(evt))
            {
                groups.Add(new JsonlEventGroup
                {
                    Id = $"evt-{i}",
                    Kind = "completion",
                    Title = evt.Title,
                    IsCompleted = true,
                    IsCollapsible = true,
                    Items = { evt }
                });
                continue;
            }

            // 其他事件作为单独的卡片
            groups.Add(new JsonlEventGroup
            {
                Id = $"evt-{i}",
                Kind = "single",
                Title = evt.Title,
                IsCompleted = true,
                IsCollapsible = false,
                Items = { evt }
            });
        }

        return groups;
    }
    
    private static bool IsCodexCommandExecutionEvent(JsonlDisplayItem evt)
    {
        return (evt.Type == "item.started" || evt.Type == "item.updated" || evt.Type == "item.completed")
               && string.Equals(evt.ItemType, "command_execution", StringComparison.OrdinalIgnoreCase);
    }
    
    private static bool IsClaudeToolEvent(JsonlDisplayItem evt)
    {
        if (string.Equals(evt.ItemType, "todo_list", StringComparison.OrdinalIgnoreCase))
            return false;
        return evt.Type == "tool_use" || evt.Type == "tool_result";
    }

    private static bool IsUserQuestionEvent(JsonlDisplayItem evt)
    {
        return string.Equals(evt.ItemType, "user_question", StringComparison.OrdinalIgnoreCase);
    }
    
    private static bool IsCompletionEvent(JsonlDisplayItem evt)
    {
        // 判断是否为完成类型的事件（这些事件默认折叠起来）
        // 但助手消息（agent_message）的 item.completed 不折叠，直接显示内容
        if (evt.Type == "item.completed" && 
            string.Equals(evt.ItemType, "agent_message", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        
        return evt.Type == "turn.completed" || 
               evt.Type == "thread.completed" || 
               evt.Type == "item.completed" || 
               evt.Type == "session_end" || 
               evt.Type == "complete" || 
               evt.Type == "step_finish" ||
               evt.Type == "result";
    }
    
    private List<OutputEventGroup> ConvertToOutputEventGroups(List<JsonlEventGroup> jsonlGroups)
    {
        return jsonlGroups.Select(g => new OutputEventGroup
        {
            Id = g.Id,
            Kind = g.Kind,
            Title = g.Title,
            IsCompleted = g.IsCompleted,
            IsCollapsible = g.IsCollapsible,
            Items = g.Items.Select(i => new OutputEvent
            {
                Type = i.Type,
                Title = i.Title,
                Content = i.Content,
                Name = null,
                ItemType = i.ItemType,
                Usage = i.Usage != null ? new TokenUsage
                {
                    InputTokens = (int?)i.Usage.InputTokens,
                    CachedInputTokens = (int?)i.Usage.CachedInputTokens,
                    OutputTokens = (int?)i.Usage.OutputTokens,
                    TotalTokens = (int?)i.Usage.TotalTokens
                } : null,
                UserQuestion = i.UserQuestion
            }).ToList()
        }).ToList();
    }

    /// <summary>
    /// 将 CliUserQuestion 转换为 UserQuestion
    /// </summary>
    private static UserQuestion ConvertToUserQuestion(CliUserQuestion cliQuestion)
    {
        return new UserQuestion
        {
            ToolUseId = cliQuestion.ToolUseId,
            IsAnswered = false,
            Questions = cliQuestion.Questions.Select(q => new QuestionItem
            {
                Header = q.Header,
                Question = q.Question,
                MultiSelect = q.MultiSelect,
                Options = q.Options.Select(o => new QuestionOption
                {
                    Label = o.Label,
                    Description = o.Description
                }).ToList(),
                SelectedIndexes = new List<int>()
            }).ToList()
        };
    }
    
    private void HandleToggleGroupCallback((string groupId, bool defaultOpen) args)
    {
        ToggleJsonlGroup(args.groupId, args.defaultOpen);
    }
    
    private void ToggleJsonlGroup(string groupId, bool defaultOpen)
    {
        var current = _jsonlGroupOpenState.TryGetValue(groupId, out var open) ? open : defaultOpen;
        _jsonlGroupOpenState[groupId] = !current;
        StateHasChanged();
    }
    
    private bool IsOutputGroupOpen(OutputEventGroup group)
    {
        if (_jsonlGroupOpenState.TryGetValue(group.Id, out var open))
            return open;
        return !group.IsCompleted;
    }
    
    private bool IsJsonlGroupOpen(JsonlEventGroup? group)
    {
        if (group == null) return false;
        if (_jsonlGroupOpenState.TryGetValue(group.Id, out var open))
            return open;
        return !group.IsCompleted;
    }
    
    private JsonlEventGroup ConvertToJsonlGroup(OutputEventGroup outputGroup)
    {
        return new JsonlEventGroup
        {
            Id = outputGroup.Id,
            Kind = outputGroup.Kind,
            Title = outputGroup.Title,
            IsCompleted = outputGroup.IsCompleted,
            IsCollapsible = outputGroup.IsCollapsible
        };
    }

    private string GetJsonlAssistantMessage()
    {
        if (_jsonlAssistantMessageBuilder == null)
        {
            return string.Empty;
        }

        var content = _jsonlAssistantMessageBuilder.ToString();
        if (!string.IsNullOrWhiteSpace(content))
        {
            return content;
        }

        return GetJsonlFallbackMessage();
    }

    private string GetJsonlFallbackMessage()
    {
        for (var i = _jsonlEvents.Count - 1; i >= 0; i--)
        {
            var evt = _jsonlEvents[i];
            if (string.IsNullOrWhiteSpace(evt.Content))
            {
                continue;
            }

            if (evt.Type is "result" or "error" or "raw" or "assistant" or "assistant:message" or "stream_event")
            {
                return evt.Content;
            }
        }

        return string.Empty;
    }

    private void UpdateOutputRaw(string content)
    {
        _rawOutput = content;
        QueueSaveOutputState();
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
            var state = BuildOutputPanelStateSnapshot(sessionId);
            await SessionOutputService.SaveAsync(state);
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
            var state = await SessionOutputService.GetBySessionIdAsync(sessionId);
            if (state == null)
            {
                return;
            }

            _rawOutput = state.RawOutput ?? string.Empty;
            _isJsonlOutputActive = state.IsJsonlOutputActive;
            _activeThreadId = state.ActiveThreadId ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(_activeThreadId))
            {
                // 刷新页面/重连后恢复 CLI thread id，保证后续可以 resume
                CliExecutorService.SetCliThreadId(sessionId, _activeThreadId);
            }

            _jsonlEvents.Clear();
            if (state.JsonlEvents != null)
            {
                foreach (var evt in state.JsonlEvents)
                {
                    _jsonlEvents.Add(new JsonlDisplayItem
                    {
                        Type = evt.Type,
                        Title = evt.Title,
                        Content = evt.Content,
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

            ResetEventDisplayCount();
        }
        catch
        {
            // 恢复失败不影响主流程
        }
    }

    private async Task DeleteOutputStateAsync(string sessionId)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await SessionOutputService.DeleteBySessionIdAsync(sessionId);
        }
        catch
        {
            // 删除失败不影响主流程
        }
    }
    
    private CancellationTokenSource? _cancellationTokenSource;
    
    private void CancelExecution()
    {
        try
        {
            _cancellationTokenSource?.Cancel();
            _ = CliExecutorService.StopSessionExecutionAsync(_sessionId, _selectedToolId);
            _isLoading = false;
            StateHasChanged();
        }
        catch { }
    }
    
    #endregion
    
    #region 会话管理
    
    private List<SessionHistory> _sessions = new();
    private SessionHistory? _currentSession = null;
    private bool _showSessionDrawer = false;
    private bool _isLoadingSessions = false;
    private bool _isLoadingSession = false;
    private string _syncingSessionId = string.Empty;
    
    // 删除会话
    private bool _showDeleteSessionDialog = false;
    private SessionHistory? _sessionToDelete = null;
    private bool _isDeletingSession = false;

    // 重命名会话
    private bool _showRenameDialog = false;
    private SessionHistory? _sessionToRename = null;
    private string _newSessionTitle = string.Empty;
    private bool _isRenamingSession = false;
    private string _renameError = string.Empty;

    // 会话启动设置
    private bool _showSessionLaunchOverrideDialog = false;
    private SessionHistory? _sessionToConfigureLaunch = null;
    private string _sessionLaunchOverrideModel = string.Empty;
    private string _sessionLaunchReasoningEffort = string.Empty;
    private List<CcSwitchModelOption> _sessionLaunchModelOptions = [];
    private bool _isSavingSessionLaunchOverride = false;
    private string _sessionLaunchOverrideError = string.Empty;

    // 目录授权
    private bool _showAuthorizeDialog = false;
    private SessionHistory? _sessionToAuthorize = null;
    private List<WorkspaceAuthorizationDto> _authorizedUsers = new();
    private string _newAuthUsername = string.Empty;
    private string _newAuthPermission = "read";
    private DateTime? _newAuthExpireTime = DateTime.Now.AddDays(7);
    private string _authorizationError = string.Empty;
    private bool _isLoadingAuthorization = false;
    
    private void ToggleSessionDrawer()
    {
        _showSessionDrawer = !_showSessionDrawer;
        if (_showSessionDrawer)
        {
            _ = LoadSessions();
        }
    }
    
    private void CloseSessionDrawer()
    {
        _showSessionDrawer = false;
    }
    
    private async Task LoadSessions()
    {
        if (_isLoadingSessions)
        {
            return;
        }

        _isLoadingSessions = true;
        StateHasChanged();
        
        try
        {
            _sessions = await SessionHistoryManager.LoadSessionsAsync();

            foreach (var session in _sessions)
            {
                session.IsWorkspaceValid = SessionHistoryManager.ValidateWorkspacePath(session.WorkspacePath);
            }

            _sessions = _sessions.OrderByDescending(s => s.UpdatedAt).ToList();
        }
        catch { }
        finally
        {
            _isLoadingSessions = false;
            StateHasChanged();
        }
    }
    
    private async Task CreateNewSession()
    {
        Console.WriteLine("【调试】CreateNewSession方法被调用");
        // 显示工作区选择对话框
        await _createSessionModal.ShowAsync();
    }

    /// <summary>
    /// 处理项目选择结果（移动端）
    /// </summary>
    private async Task OnProjectSelected(ProjectSelectionResult selection)
    {
        await CreateNewSessionWithProjectAsync(selection.ProjectId, selection.IncludeGit);
        StateHasChanged();
    }

    /// <summary>
    /// 处理新建会话（支持自定义工作区）
    /// </summary>
    private async Task HandleSessionCreated(CreateSessionOptions options)
    {
        // 创建新会话
        var newSession = await CreateNewSessionAsync(options.UseDefaultDirectory, options.WorkspacePath);

        // 加载新会话
        await LoadSessionFromDrawer(newSession.SessionId);
    }

    /// <summary>
    /// 关闭新建会话模态框
    /// </summary>
    private void CloseCreateSessionModal()
    {
        StateHasChanged();
    }

    /// <summary>
    /// 显示项目管理模态框
    /// </summary>
    private async Task ShowProjectManageModal()
    {
        if (_projectManageModal != null)
        {
            await _projectManageModal.ShowAsync();
        }
    }

    private async Task ShowAdminUserManagementModal()
    {
        if (_adminUserManagementModal != null)
        {
            await _adminUserManagementModal.ShowAsync();
        }
    }

    private void ShowRenameDialog(SessionHistory session)
    {
        _sessionToRename = session;
        _newSessionTitle = session.Title ?? string.Empty;
        _showRenameDialog = true;
        _renameError = string.Empty;
        StateHasChanged();
    }

    private void CloseRenameDialog()
    {
        _showRenameDialog = false;
        _sessionToRename = null;
        _newSessionTitle = string.Empty;
        _renameError = string.Empty;
        _isRenamingSession = false;
        StateHasChanged();
    }

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

            var newTitle = _newSessionTitle.Trim();
            if (string.IsNullOrWhiteSpace(newTitle))
            {
                _renameError = T("codeAssistant.renameErrorEmptyTitle");
                return;
            }

            const int maxTitleLength = 100;
            if (newTitle.Length > maxTitleLength)
            {
                _renameError = T("codeAssistant.renameErrorTooLong", ("max", maxTitleLength.ToString()));
                return;
            }

            _sessionToRename.Title = newTitle;
            _sessionToRename.UpdatedAt = DateTime.Now;

            await SessionHistoryManager.SaveSessionImmediateAsync(_sessionToRename);

            if (_currentSession?.SessionId == _sessionToRename.SessionId)
            {
                _currentSession.Title = newTitle;
            }

            await LoadSessions();
            CloseRenameDialog();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"重命名会话失败: {ex.Message}");
            _renameError = T("codeAssistant.renameErrorFailed", ("error", ex.Message));
        }
        finally
        {
            _isRenamingSession = false;
            StateHasChanged();
        }
    }

    /// <summary>
    /// 项目列表变更回调
    /// </summary>
    private void OnProjectsChanged()
    {
        // 项目列表有变化时的处理（如需要可刷新相关UI）
        StateHasChanged();
    }

    /// <summary>
    /// 创建新会话（带项目关联，移动端）
    /// </summary>
    private async Task CreateNewSessionWithProjectAsync(string? projectId, bool includeGit)
    {
        try
        {
            _sessionId = Guid.NewGuid().ToString();
            _messageAttachmentComposer.Clear();
            _messages.Clear();
            _expandedMessageIndices.Clear(); // 重置展开状态
            _currentSession = null;
            _jsonlEvents.Clear();
            _rawOutput = string.Empty;
            _isJsonlOutputActive = false;
            _jsonlPendingBuffer = string.Empty;
            _jsonlAssistantMessageBuilder = null;
            ResetEventDisplayCount();
            _workspaceFiles.Clear();
            _currentFolderItems.Clear();
            _breadcrumbs.Clear();
            _selectedHtmlFile = string.Empty;
            _htmlPreviewUrl = string.Empty;

            var workspacePath = await CliExecutorService.InitializeSessionWorkspaceAsync(_sessionId, projectId, includeGit);

            string? projectName = null;
            if (!string.IsNullOrEmpty(projectId))
            {
                try
                {
                    var response = await Http.GetAsync($"/api/project/{projectId}");
                    if (response.IsSuccessStatusCode)
                    {
                        var project = await response.Content.ReadFromJsonAsync<ProjectInfo>();
                        projectName = project?.Name;
                    }
                }
                catch
                {
                    // 忽略获取项目名称失败
                }
            }

            _currentSession = new SessionHistory
            {
                SessionId = _sessionId,
                Title = string.IsNullOrEmpty(projectName) ? "新会话" : $"新会话 - {projectName}",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                WorkspacePath = workspacePath,
                ToolId = _selectedToolId,
                Messages = new List<ChatMessage>(),
                IsWorkspaceValid = true,
                ProjectId = projectId,
                ProjectName = projectName
            };
            InvalidateSuperpowersCapabilityPresentation();

            await SessionHistoryManager.SaveSessionImmediateAsync(_currentSession);
            await LoadSessions();
            await LoadWorkspaceFiles();
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"创建新会话失败: {ex.Message}");
            Console.WriteLine($"错误详情: {ex.StackTrace}");
        }
    }

    /// <summary>
    /// 创建新会话（支持自定义工作区，移动端）
    /// </summary>
    private async Task<SessionHistory> CreateNewSessionAsync(bool useDefaultDirectory, string workspacePath)
    {
        // 创建新会话
        var newSession = new SessionHistory
        {
            SessionId = Guid.NewGuid().ToString(),
            Title = "新会话",
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
            ToolId = _selectedToolId,
            WorkspacePath = useDefaultDirectory ? string.Empty : workspacePath,
            IsCustomWorkspace = !useDefaultDirectory
        };

        // 保存会话
        await SessionHistoryManager.SaveSessionImmediateAsync(newSession);

        // 重新加载会话列表
        await LoadSessions();

        return newSession;
    }

    private async Task CreateNewSessionFromDrawer()
    {
        CloseSessionDrawer();
        await CreateNewSession();
    }
    
    private async Task LoadSessionFromDrawer(string sessionId)
    {
        _isLoadingSession = true;
        _messageAttachmentComposer.Clear();
        StateHasChanged();
        
        try
        {
            var session = await SessionHistoryManager.GetSessionAsync(sessionId);
            if (session != null)
            {
                session.IsWorkspaceValid = SessionHistoryManager.ValidateWorkspacePath(session.WorkspacePath);

                _currentSession = session;
                _sessionId = session.SessionId;
                _messages = new List<ChatMessage>(session.Messages);
                _expandedMessageIndices.Clear(); // 重置展开状态

                if (!string.IsNullOrEmpty(session.ToolId) &&
                    _availableTools.Any(t => t.Id == session.ToolId))
                {
                    _selectedToolId = session.ToolId;
                }
                else if (_availableTools.Any() && string.IsNullOrEmpty(_selectedToolId))
                {
                    _selectedToolId = _availableTools.First().Id;
                }

                _rawOutput = string.Empty;
                _jsonlEvents.Clear();
                _jsonlPendingBuffer = string.Empty;
                _activeThreadId = string.Empty;
                _isJsonlOutputActive = false;
                _jsonlAssistantMessageBuilder = null;
                _currentAssistantMessage = string.Empty;
                InvalidateSuperpowersCapabilityPresentation();

                await LoadOutputStateAsync(_sessionId);

                if (session.IsWorkspaceValid)
                {
                    await LoadWorkspaceFiles();
                }
                else
                {
                    _workspaceFiles.Clear();
                    _currentFolderItems.Clear();
                    _breadcrumbs.Clear();
                    _selectedHtmlFile = string.Empty;
                    _htmlPreviewUrl = string.Empty;
                }
            }
        }
        catch { }
        finally
        {
            _isLoadingSession = false;
            CloseSessionDrawer();
            StateHasChanged();
        }
    }

    private async Task SyncSessionProviderAsync(SessionHistory session)
    {
        if (session == null || string.IsNullOrWhiteSpace(session.SessionId) || _syncingSessionId == session.SessionId)
        {
            return;
        }

        try
        {
            _syncingSessionId = session.SessionId;
            StateHasChanged();

            var effectiveToolId = string.IsNullOrWhiteSpace(session.CcSwitchSnapshotToolId)
                ? session.ToolId
                : session.CcSwitchSnapshotToolId;

            var syncResult = await CliExecutorService.SyncCodexThreadProviderAsync(session.SessionId, effectiveToolId);
            await LoadSessions();

            var refreshedSession = _sessions.FirstOrDefault(x => x.SessionId == session.SessionId);
            if (refreshedSession != null && _currentSession?.SessionId == refreshedSession.SessionId)
            {
                MergeCcSwitchSnapshotState(_currentSession, refreshedSession);
                InvalidateSuperpowersCapabilityPresentation();
            }

            var syncMessage = string.IsNullOrWhiteSpace(syncResult.Message)
                ? "Codex thread 同步完成"
                : syncResult.Message;
            await JSRuntime.InvokeVoidAsync("alert", syncResult.HasWarnings ? $"⚠️ {syncMessage}" : $"✅ {syncMessage}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[移动端会话同步] 同步会话 Provider 失败: {ex.Message}");
            await JSRuntime.InvokeVoidAsync("alert", T("codeAssistant.providerSyncFailed", ("message", ex.Message)));
        }
        finally
        {
            _syncingSessionId = string.Empty;
            StateHasChanged();
        }
    }

    private bool CurrentSessionLaunchOverrideSupportsReasoning
        => string.Equals(
            GetSessionLaunchOverrideToolId(_sessionToConfigureLaunch),
            "codex",
            StringComparison.OrdinalIgnoreCase);

    private string? TryGetSessionLaunchOverrideSummary(SessionHistory session)
    {
        var launchOverride = SessionLaunchOverrideHelper.GetEffectiveOverride(session);
        if (launchOverride == null)
        {
            return null;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(launchOverride.Model))
        {
            parts.Add($"{T("codeAssistant.sessionLaunchSettingsModel")}: {launchOverride.Model}");
        }

        if (!string.IsNullOrWhiteSpace(launchOverride.ReasoningEffort))
        {
            parts.Add($"{T("codeAssistant.sessionLaunchSettingsReasoning")}: {launchOverride.ReasoningEffort}");
        }

        return parts.Count == 0
            ? null
            : string.Join(" · ", parts);
    }

    private async Task ShowSessionLaunchOverrideDialog(SessionHistory session)
    {
        if (session == null)
        {
            return;
        }

        var toolId = GetSessionLaunchOverrideToolId(session);
        if (string.IsNullOrWhiteSpace(toolId))
        {
            await JSRuntime.InvokeVoidAsync("alert", T("codeAssistant.sessionLaunchSettingsUnsupportedTool"));
            return;
        }

        var launchOverride = SessionLaunchOverrideHelper.GetEffectiveOverride(session, toolId);
        _sessionToConfigureLaunch = session;
        _sessionLaunchOverrideModel = launchOverride?.Model ?? string.Empty;
        _sessionLaunchReasoningEffort = string.Equals(toolId, "codex", StringComparison.OrdinalIgnoreCase)
            ? launchOverride?.ReasoningEffort ?? string.Empty
            : string.Empty;
        _sessionLaunchModelOptions = await LoadSessionLaunchModelOptionsAsync(session, toolId, _sessionLaunchOverrideModel);
        _sessionLaunchOverrideError = string.Empty;
        _showSessionLaunchOverrideDialog = true;
        StateHasChanged();
    }

    private void CloseSessionLaunchOverrideDialog()
    {
        _showSessionLaunchOverrideDialog = false;
        _sessionToConfigureLaunch = null;
        _sessionLaunchOverrideModel = string.Empty;
        _sessionLaunchReasoningEffort = string.Empty;
        _sessionLaunchModelOptions = [];
        _sessionLaunchOverrideError = string.Empty;
        _isSavingSessionLaunchOverride = false;
        StateHasChanged();
    }

    private Task SaveSessionLaunchOverrideAsync()
    {
        return PersistSessionLaunchOverrideAsync(clearOverride: false);
    }

    private Task ClearSessionLaunchOverrideAsync()
    {
        return PersistSessionLaunchOverrideAsync(clearOverride: true);
    }

    private async Task PersistSessionLaunchOverrideAsync(bool clearOverride)
    {
        if (_sessionToConfigureLaunch == null || _isSavingSessionLaunchOverride)
        {
            return;
        }

        var toolId = GetSessionLaunchOverrideToolId(_sessionToConfigureLaunch);
        if (string.IsNullOrWhiteSpace(toolId))
        {
            _sessionLaunchOverrideError = T("codeAssistant.sessionLaunchSettingsUnsupportedTool");
            StateHasChanged();
            return;
        }

        try
        {
            _isSavingSessionLaunchOverride = true;
            _sessionLaunchOverrideError = string.Empty;
            StateHasChanged();

            var session = await SessionHistoryManager.GetSessionAsync(_sessionToConfigureLaunch.SessionId) ?? _sessionToConfigureLaunch;
            session.ToolLaunchOverrides = clearOverride
                ? SessionLaunchOverrideHelper.RemoveOverride(session.ToolLaunchOverrides, toolId)
                : SessionLaunchOverrideHelper.ApplyOverride(
                    session.ToolLaunchOverrides,
                    toolId,
                    _sessionLaunchOverrideModel,
                    _sessionLaunchReasoningEffort);

            await SessionHistoryManager.SaveSessionImmediateAsync(session);
            await CliExecutorService.ResetSessionRuntimeAsync(session.SessionId, clearCliThreadId: false);

            if (_currentSession?.SessionId == session.SessionId)
            {
                await SaveOutputStateAsync();
            }

            await LoadSessions();

            var refreshedSession = _sessions.FirstOrDefault(x => x.SessionId == session.SessionId) ?? session;
            if (_currentSession?.SessionId == refreshedSession.SessionId)
            {
                MergeCcSwitchSnapshotState(_currentSession, refreshedSession);
                _currentSession.ToolLaunchOverrides = new Dictionary<string, SessionToolLaunchOverride>(
                    refreshedSession.ToolLaunchOverrides,
                    StringComparer.OrdinalIgnoreCase);
            }

            CloseSessionLaunchOverrideDialog();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[移动端会话启动设置] 保存失败: {ex.Message}");
            _sessionLaunchOverrideError = ex.Message;
            StateHasChanged();
        }
        finally
        {
            _isSavingSessionLaunchOverride = false;
            StateHasChanged();
        }
    }

    private static string? GetSessionLaunchOverrideToolId(SessionHistory? session)
    {
        return session == null
            ? null
            : SessionLaunchOverrideHelper.ResolveEffectiveToolId(session.ToolId, session.CcSwitchSnapshotToolId);
    }

    private string GetSessionLaunchOverrideToolDisplayName(SessionHistory? session)
    {
        var toolId = GetSessionLaunchOverrideToolId(session);
        if (string.IsNullOrWhiteSpace(toolId))
        {
            return T("codeAssistant.selectTool");
        }

        return _availableTools.FirstOrDefault(tool => string.Equals(tool.Id, toolId, StringComparison.OrdinalIgnoreCase))?.Name
               ?? toolId switch
               {
                   "claude-code" => "Claude Code",
                   "codex" => "Codex",
                   "opencode" => "OpenCode",
                   _ => toolId
               };
    }

    private async Task<List<CcSwitchModelOption>> LoadSessionLaunchModelOptionsAsync(SessionHistory session, string toolId, string? currentModel)
    {
        try
        {
            var catalog = await CcSwitchService.GetModelCatalogAsync(toolId, session.CcSwitchProviderId);
            return MergeSessionLaunchModelOptions(catalog.Models, currentModel);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[移动端会话启动设置] 读取模型列表失败: {ex.Message}");
            return MergeSessionLaunchModelOptions([], currentModel);
        }
    }

    private static List<CcSwitchModelOption> MergeSessionLaunchModelOptions(IEnumerable<CcSwitchModelOption> options, string? currentModel)
    {
        var merged = new List<CcSwitchModelOption>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var option in options)
        {
            if (option == null || string.IsNullOrWhiteSpace(option.Id))
            {
                continue;
            }

            var id = option.Id.Trim();
            if (!seen.Add(id))
            {
                continue;
            }

            merged.Add(new CcSwitchModelOption
            {
                Id = id,
                DisplayName = string.IsNullOrWhiteSpace(option.DisplayName) ? id : option.DisplayName.Trim()
            });
        }

        if (!string.IsNullOrWhiteSpace(currentModel) && seen.Add(currentModel.Trim()))
        {
            merged.Insert(0, new CcSwitchModelOption
            {
                Id = currentModel.Trim(),
                DisplayName = currentModel.Trim()
            });
        }

        return merged;
    }

    private static bool IsManagedTool(SessionHistory session)
    {
        return NormalizeManagedToolId(session.CcSwitchSnapshotToolId ?? session.ToolId) is "claude-code" or "codex" or "opencode";
    }

    private string GetPinnedProviderDisplay(SessionHistory session)
    {
        if (!string.IsNullOrWhiteSpace(session.CcSwitchProviderName))
        {
            return session.CcSwitchProviderName!;
        }

        if (!string.IsNullOrWhiteSpace(session.CcSwitchProviderId))
        {
            return session.CcSwitchProviderId!;
        }

        return T("codeAssistant.providerNotSynced");
    }

    private static string? NormalizeManagedToolId(string? toolId)
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

    private static void MergeCcSwitchSnapshotState(SessionHistory target, SessionHistory source)
    {
        target.ToolId = source.ToolId;
        target.WorkspacePath = source.WorkspacePath;
        target.UpdatedAt = source.UpdatedAt;
        target.UsesCcSwitchSnapshot = source.UsesCcSwitchSnapshot;
        target.CcSwitchSnapshotToolId = source.CcSwitchSnapshotToolId;
        target.CcSwitchProviderId = source.CcSwitchProviderId;
        target.CcSwitchProviderName = source.CcSwitchProviderName;
        target.CcSwitchProviderCategory = source.CcSwitchProviderCategory;
        target.CcSwitchLiveConfigPath = source.CcSwitchLiveConfigPath;
        target.CcSwitchSnapshotRelativePath = source.CcSwitchSnapshotRelativePath;
        target.CcSwitchSnapshotSyncedAt = source.CcSwitchSnapshotSyncedAt;
    }
    
    private void ShowDeleteSessionConfirm(SessionHistory session)
    {
        _sessionToDelete = session;
        _showDeleteSessionDialog = true;
    }
    
    private void CloseDeleteSessionDialog()
    {
        _showDeleteSessionDialog = false;
        _sessionToDelete = null;
    }
    
    private async Task DeleteSessionConfirmed()
    {
        if (_sessionToDelete == null) return;
        
        _isDeletingSession = true;
        StateHasChanged();
        
        try
        {
            var deletedSessionId = _sessionToDelete.SessionId;
            var deletedCurrentSession = _currentSession?.SessionId == deletedSessionId;

            await SessionHistoryManager.DeleteSessionAsync(deletedSessionId);
            await DeleteOutputStateAsync(deletedSessionId);

            try
            {
                CliExecutorService.CleanupSessionWorkspace(deletedSessionId);
            }
            catch { }

            await LoadSessions();

            if (deletedCurrentSession)
            {
                if (_sessions.Any())
                {
                    await LoadSessionFromDrawer(_sessions.First().SessionId);
                }
                else
                {
                    await CreateNewSession();
                }
            }
        }
        catch { }
        finally
        {
            _isDeletingSession = false;
            CloseDeleteSessionDialog();
            StateHasChanged();
        }
    }

    private async void ShowAuthorizeDialog(SessionHistory session)
    {
        _sessionToAuthorize = session;
        _showAuthorizeDialog = true;
        _authorizationError = string.Empty;
        _newAuthUsername = string.Empty;
        _newAuthPermission = "read";
        _newAuthExpireTime = DateTime.Now.AddDays(7);

        await LoadAuthorizedUsers();
        StateHasChanged();
    }

    private void CloseAuthorizeDialog()
    {
        _showAuthorizeDialog = false;
        _sessionToAuthorize = null;
        _authorizedUsers.Clear();
        _newAuthUsername = string.Empty;
        _authorizationError = string.Empty;
        _isLoadingAuthorization = false;
        StateHasChanged();
    }

    private async Task LoadAuthorizedUsers()
    {
        if (_sessionToAuthorize == null || string.IsNullOrEmpty(_sessionToAuthorize.WorkspacePath))
            return;

        _isLoadingAuthorization = true;
        StateHasChanged();

        try
        {
            var response = await Http.GetAsync($"/api/workspace/directory-authorizations?directoryPath={Uri.EscapeDataString(_sessionToAuthorize.WorkspacePath)}");
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<ApiResponse<List<WorkspaceAuthorizationDto>>>();
                _authorizedUsers = result?.Data ?? new List<WorkspaceAuthorizationDto>();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"加载授权用户失败: {ex.Message}");
        }
        finally
        {
            _isLoadingAuthorization = false;
            StateHasChanged();
        }
    }

    private async Task AddAuthorization()
    {
        if (_sessionToAuthorize == null || string.IsNullOrEmpty(_newAuthUsername))
        {
            _authorizationError = "请输入用户名";
            StateHasChanged();
            return;
        }

        _isLoadingAuthorization = true;
        _authorizationError = string.Empty;
        StateHasChanged();

        try
        {
            var response = await Http.PostAsJsonAsync("/api/workspace/authorize", new AuthorizeDirectoryRequest
            {
                DirectoryPath = _sessionToAuthorize.WorkspacePath,
                AuthorizedUsername = _newAuthUsername,
                Permission = _newAuthPermission,
                ExpiresAt = _newAuthExpireTime
            });

            if (response.IsSuccessStatusCode)
            {
                _newAuthUsername = string.Empty;
                await LoadAuthorizedUsers();
            }
            else
            {
                _authorizationError = $"授权失败: {await ExtractErrorMessageAsync(response)}";
            }
        }
        catch (Exception ex)
        {
            _authorizationError = $"授权失败: {ex.Message}";
        }
        finally
        {
            _isLoadingAuthorization = false;
            StateHasChanged();
        }
    }

    private async Task RevokeAuthorization(string username)
    {
        if (_sessionToAuthorize == null)
            return;

        _isLoadingAuthorization = true;
        StateHasChanged();

        try
        {
            var response = await Http.PostAsJsonAsync("/api/workspace/revoke-authorization", new RevokeAuthorizationRequest
            {
                DirectoryPath = _sessionToAuthorize.WorkspacePath,
                AuthorizedUsername = username
            });
            if (response.IsSuccessStatusCode)
            {
                await LoadAuthorizedUsers();
            }
            else
            {
                var error = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"撤销授权失败: {error}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"撤销授权失败: {ex.Message}");
        }
        finally
        {
            _isLoadingAuthorization = false;
            StateHasChanged();
        }
    }

    private static async Task<string> ExtractErrorMessageAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(content))
        {
            return $"HTTP {(int)response.StatusCode}";
        }

        try
        {
            using var json = JsonDocument.Parse(content);
            if (json.RootElement.TryGetProperty("error", out var errorElement))
            {
                return errorElement.GetString() ?? content;
            }

            if (json.RootElement.TryGetProperty("message", out var messageElement))
            {
                return messageElement.GetString() ?? content;
            }
        }
        catch
        {
        }

        return content;
    }

    private string GetPermissionLabel(string permission)
    {
        return permission switch
        {
            "read" => "只读",
            "write" => "读写",
            "admin" => "管理员",
            _ => permission
        };
    }

    private string FormatDate(DateTime date)
    {
        var now = DateTime.Now;
        var diff = now - date;

        if (diff.TotalSeconds < 60)
            return "刚刚";
        if (diff.TotalMinutes < 60)
            return $"{(int)diff.TotalMinutes} 分钟前";
        if (diff.TotalHours < 24)
            return $"{(int)diff.TotalHours} 小时前";
        if (diff.TotalDays < 30)
            return $"{(int)diff.TotalDays} 天前";

        return date.ToString("yyyy-MM-dd");
    }
    
    private async Task SaveCurrentSession()
    {
        try
        {
            if (!_messages.Any())
            {
                QueueSaveOutputState(forceImmediate: true);
                return;
            }

            var workspacePath = CliExecutorService.GetSessionWorkspacePath(_sessionId);

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

                var firstUserMessage = _messages.FirstOrDefault(m => m.Role == "user");
                if (firstUserMessage != null)
                {
                    _currentSession.Title = SessionHistoryManager.GenerateSessionTitle(firstUserMessage.Content);
                }
            }
            else
            {
                _currentSession.Messages = new List<ChatMessage>(_messages);
                _currentSession.UpdatedAt = DateTime.Now;
                _currentSession.ToolId = _selectedToolId;
                _currentSession.WorkspacePath = workspacePath;
                _currentSession.IsWorkspaceValid = true;

                if (_currentSession.Title == "新会话")
                {
                    var firstUserMessage = _messages.FirstOrDefault(m => m.Role == "user");
                    if (firstUserMessage != null)
                    {
                        _currentSession.Title = SessionHistoryManager.GenerateSessionTitle(firstUserMessage.Content);
                    }
                }
            }
            
            await SessionHistoryManager.SaveSessionImmediateAsync(_currentSession);
            QueueSaveOutputState(forceImmediate: true);
        }
        catch { }
    }
    
    private string FormatDateTime(DateTime dateTime)
    {
        var now = DateTime.Now;
        var diff = now - dateTime;
        
        if (diff.TotalMinutes < 1) return T("common.justNow");
        if (diff.TotalHours < 1) return $"{(int)diff.TotalMinutes} {T("common.minutesAgo")}";
        if (diff.TotalDays < 1) return $"{(int)diff.TotalHours} {T("common.hoursAgo")}";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays} {T("common.daysAgo")}";
        
        return dateTime.ToString("yyyy-MM-dd HH:mm");
    }
    
    #endregion
    
    #region 工具选择
    
    private List<CliToolConfig> _availableTools = new();
    private List<CliToolConfig> _allTools = new();
    private List<string> _enabledAssistants = new();
    private string _selectedToolId = string.Empty;
    
    private async Task LoadAvailableTools()
    {
        try
        {
            _allTools = CliExecutorService.GetAvailableTools(_currentUsername);
            _enabledAssistants = AssistantCatalogHelper.NormalizeEnabledAssistants(
                await SystemSettingsService.GetEnabledAssistantsAsync());
            _availableTools = AssistantCatalogHelper.FilterAvailableTools(_allTools, _enabledAssistants);

            if (_availableTools.Any())
            {
                if (!_availableTools.Any(tool => tool.Id == _selectedToolId))
                {
                    _selectedToolId = _availableTools.First().Id;
                }
            }
            else
            {
                _selectedToolId = string.Empty;
            }
        }
        catch { }
    }
    
    private string GetCurrentToolName()
    {
        var tool = _availableTools.FirstOrDefault(t => t.Id == _selectedToolId);
        return tool?.Name ?? T("codeAssistant.selectTool");
    }
    
    private async Task OnToolChanged()
    {
        InvalidateSuperpowersCapabilityPresentation();
        await Task.CompletedTask;
    }
    
    #endregion
    
    #region 文件管理
    
    private List<WorkspaceFileNode> _workspaceFiles = new();
    private List<WorkspaceFileNode> _currentFolderItems = new();
    private List<BreadcrumbItem> _breadcrumbs = new();
    private string _currentFolderPath = string.Empty;
    
    // 文件操作
    private bool _showFileActionSheet = false;
    private WorkspaceFileNode? _selectedFileNode = null;
    
    // 创建文件夹
    private bool _showCreateFolderDialog = false;
    private string _newFolderName = string.Empty;
    private bool _isCreatingFolder = false;
    private bool _isComposingFolderName = false;
    private DotNetObjectReference<CodeAssistantMobile>? _createFolderDotNetRef;
    
    // 文件上传
    private bool _isUploading = false;
    
    private record BreadcrumbItem(string Name, string Path);
    
    private async Task LoadWorkspaceFiles()
    {
        try
        {
            var workspacePath = CliExecutorService.GetSessionWorkspacePath(_sessionId);
            
            if (!Directory.Exists(workspacePath))
            {
                _workspaceFiles = new List<WorkspaceFileNode>();
                UpdateCurrentFolderItems();
                return;
            }

            _workspaceFiles = GetDirectoryStructure(workspacePath, workspacePath);
            UpdateCurrentFolderItems();
        }
        catch
        {
            _workspaceFiles = new List<WorkspaceFileNode>();
        }
    }
    
    private List<WorkspaceFileNode> GetDirectoryStructure(string basePath, string currentPath)
    {
        var result = new List<WorkspaceFileNode>();
        
        try
        {
            // 获取子目录
            foreach (var dir in Directory.GetDirectories(currentPath))
            {
                var dirInfo = new DirectoryInfo(dir);
                if (dirInfo.Name.StartsWith(".")) continue; // 跳过隐藏文件夹
                
                var relativePath = Path.GetRelativePath(basePath, dir).Replace("\\", "/");
                result.Add(new WorkspaceFileNode
                {
                    Name = dirInfo.Name,
                    Path = relativePath,
                    Type = "folder",
                    Children = GetDirectoryStructure(basePath, dir)
                });
            }
            
            // 获取文件
            foreach (var file in Directory.GetFiles(currentPath))
            {
                var fileInfo = new FileInfo(file);
                if (fileInfo.Name.StartsWith(".")) continue; // 跳过隐藏文件
                
                var relativePath = Path.GetRelativePath(basePath, file).Replace("\\", "/");
                var ext = fileInfo.Extension.ToLowerInvariant();
                result.Add(new WorkspaceFileNode
                {
                    Name = fileInfo.Name,
                    Path = relativePath,
                    Type = "file",
                    Size = fileInfo.Length,
                    Extension = ext,
                    IsHtml = ext == ".html" || ext == ".htm"
                });
            }
        }
        catch { }
        
        return result;
    }
    
    private async Task RefreshWorkspaceFiles()
    {
        await LoadWorkspaceFiles();
        StateHasChanged();
    }
    
    private void UpdateCurrentFolderItems()
    {
        if (string.IsNullOrEmpty(_currentFolderPath))
        {
            _currentFolderItems = _workspaceFiles.ToList();
        }
        else
        {
            var folder = FindFolder(_workspaceFiles, _currentFolderPath);
            _currentFolderItems = folder?.Children?.ToList() ?? new List<WorkspaceFileNode>();
        }
        
        // 文件夹排在前面
        _currentFolderItems = _currentFolderItems
            .OrderByDescending(f => f.Type == "folder")
            .ThenBy(f => f.Name)
            .ToList();
    }
    
    private WorkspaceFileNode? FindFolder(List<WorkspaceFileNode> nodes, string path)
    {
        foreach (var node in nodes)
        {
            if (node.Path == path && node.Type == "folder")
                return node;
            
            if (node.Children != null)
            {
                var found = FindFolder(node.Children, path);
                if (found != null) return found;
            }
        }
        return null;
    }
    
    private void OnFileItemClick(WorkspaceFileNode item)
    {
        if (item.Type == "folder")
        {
            NavigateToFolder(item);
        }
        else
        {
            ShowFileActionSheet(item);
        }
    }
    
    private void NavigateToFolder(WorkspaceFileNode folder)
    {
        _currentFolderPath = folder.Path;
        _breadcrumbs.Add(new BreadcrumbItem(folder.Name, folder.Path));
        UpdateCurrentFolderItems();
        StateHasChanged();
    }
    
    private void NavigateToRoot()
    {
        _currentFolderPath = string.Empty;
        _breadcrumbs.Clear();
        UpdateCurrentFolderItems();
        StateHasChanged();
    }
    
    private void NavigateToCrumb(BreadcrumbItem crumb)
    {
        var index = _breadcrumbs.FindIndex(b => b.Path == crumb.Path);
        if (index >= 0)
        {
            _breadcrumbs = _breadcrumbs.Take(index + 1).ToList();
            _currentFolderPath = crumb.Path;
            UpdateCurrentFolderItems();
            StateHasChanged();
        }
    }
    
    private void ShowFileActionSheet(WorkspaceFileNode node)
    {
        _selectedFileNode = node;
        _showFileActionSheet = true;
    }
    
    private void CloseFileActionSheet()
    {
        _showFileActionSheet = false;
        _selectedFileNode = null;
    }
    
    private async Task PreviewSelectedFile()
    {
        if (_selectedFileNode == null) return;
        
        try
        {
            var fileBytes = CliExecutorService.GetWorkspaceFile(_sessionId, _selectedFileNode.Path);
            if (fileBytes != null)
            {
                var content = Encoding.UTF8.GetString(fileBytes);
                // 正确的参数顺序: fileName, filePath, content, fileBytes, sessionId
                await _codePreviewModal.ShowAsync(_selectedFileNode.Name, _selectedFileNode.Path, content, fileBytes, _sessionId);
            }
        }
        catch { }
        finally
        {
            CloseFileActionSheet();
        }
    }
    
    private async Task DownloadSelectedFile()
    {
        if (_selectedFileNode == null) return;
        
        try
        {
            var fileBytes = CliExecutorService.GetWorkspaceFile(_sessionId, _selectedFileNode.Path);
            if (fileBytes != null)
            {
                var base64 = Convert.ToBase64String(fileBytes);
                var fileName = _selectedFileNode.Name.Replace("'", "\\'");
                
                await JSRuntime.InvokeVoidAsync("eval", $@"
                    const link = document.createElement('a');
                    link.href = 'data:application/octet-stream;base64,{base64}';
                    link.download = '{fileName}';
                    link.click();
                ");
            }
        }
        catch { }
        finally
        {
            CloseFileActionSheet();
        }
    }
    
    private void PreviewHtmlFile()
    {
        if (_selectedFileNode == null) return;
        
        _selectedHtmlFile = _selectedFileNode.Path;
        // 使用与PC端一致的API路径格式: /api/workspace/{sessionId}/files/{filePath}
        var encodedPath = Uri.EscapeDataString(_selectedFileNode.Path.Replace("\\", "/"));
        _htmlPreviewUrl = $"/api/workspace/{_sessionId}/files/{encodedPath}";
        SwitchTab("preview");
        CloseFileActionSheet();
    }
    
    private async Task DeleteSelectedFileNode()
    {
        if (_selectedFileNode == null) return;
        
        try
        {
            var isDirectory = _selectedFileNode.Type == "folder";
            await CliExecutorService.DeleteWorkspaceItemAsync(_sessionId, _selectedFileNode.Path, isDirectory);
            await LoadWorkspaceFiles();
        }
        catch { }
        finally
        {
            CloseFileActionSheet();
        }
    }
    
    private async Task ShowCreateFolderDialogAsync()
    {
        _newFolderName = string.Empty;
        _showCreateFolderDialog = true;
        StateHasChanged();
        
        // 等待DOM渲染后设置组合事件监听
        await Task.Delay(50);
        try
        {
            _createFolderDotNetRef = DotNetObjectReference.Create(this);
            await JSRuntime.InvokeVoidAsync("setupCompositionEvents", "mobile-create-folder-input", _createFolderDotNetRef);
        }
        catch
        {
            // 忽略JS互操作错误
        }
    }
    
    private void ShowCreateFolderDialog()
    {
        // 使用 InvokeAsync 确保在 Blazor 渲染上下文中执行
        _ = InvokeAsync(async () =>
        {
            try
            {
                await ShowCreateFolderDialogAsync();
            }
            catch
            {
                // 忽略错误，避免未处理的异常导致应用崩溃
            }
        });
    }
    
    private async Task CloseCreateFolderDialogAsync()
    {
        _showCreateFolderDialog = false;
        _newFolderName = string.Empty;
        
        // 清理组合事件监听
        try
        {
            await JSRuntime.InvokeVoidAsync("disposeCompositionEvents", "mobile-create-folder-input");
        }
        catch
        {
            // 忽略JS互操作错误
        }
    }
    
    private void CloseCreateFolderDialog()
    {
        // 使用 InvokeAsync 确保在 Blazor 渲染上下文中执行
        _ = InvokeAsync(async () =>
        {
            try
            {
                await CloseCreateFolderDialogAsync();
            }
            catch
            {
                // 忽略错误，避免未处理的异常导致应用崩溃
            }
        });
    }
    
    [JSInvokable]
    public void OnCompositionStart()
    {
        _isComposingFolderName = true;
    }

    [JSInvokable]
    public Task OnCompositionEnd(string finalValue)
    {
        _isComposingFolderName = false;
        // 组合结束时同步最终值
        if (finalValue != _newFolderName)
        {
            _newFolderName = finalValue;
            StateHasChanged();
        }
        return Task.CompletedTask;
    }

    private Task HandleNewFolderNameAfterBind()
    {
        // 组合期间（中文输入法候选阶段）不触发更新，避免闪烁
        if (_isComposingFolderName)
        {
            return Task.CompletedTask;
        }
        
        StateHasChanged();
        return Task.CompletedTask;
    }
    
    private async Task CreateFolder()
    {
        if (string.IsNullOrWhiteSpace(_newFolderName)) return;
        
        _isCreatingFolder = true;
        StateHasChanged();
        
        try
        {
            var folderPath = string.IsNullOrEmpty(_currentFolderPath)
                ? _newFolderName
                : $"{_currentFolderPath}/{_newFolderName}";
            
            await CliExecutorService.CreateFolderInWorkspaceAsync(_sessionId, folderPath);
            await LoadWorkspaceFiles();
        }
        catch { }
        finally
        {
            _isCreatingFolder = false;
            CloseCreateFolderDialog();
            StateHasChanged();
        }
    }
    
    private async Task HandleFileUpload(InputFileChangeEventArgs e)
    {
        _isUploading = true;
        StateHasChanged();
        
        try
        {
            var file = e.File;
            using var stream = file.OpenReadStream(100 * 1024 * 1024); // 100MB max
            using var memoryStream = new MemoryStream();
            await stream.CopyToAsync(memoryStream);
            
            var uploadPath = string.IsNullOrEmpty(_currentFolderPath) ? null : _currentFolderPath;
            
            await CliExecutorService.UploadFileToWorkspaceAsync(
                _sessionId, 
                file.Name, 
                memoryStream.ToArray(),
                uploadPath);
            await LoadWorkspaceFiles();
        }
        catch { }
        finally
        {
            _isUploading = false;
            StateHasChanged();
        }
    }

    private async Task HandleMessageAttachmentUpload(InputFileChangeEventArgs e)
    {
        if (_isMessageAttachmentUploading)
        {
            return;
        }

        var remainingSlots = MaxMessageAttachmentCount - _messageAttachmentComposer.PendingAttachments.Count;
        if (remainingSlots <= 0)
        {
            return;
        }

        _isMessageAttachmentUploading = true;
        StateHasChanged();

        try
        {
            var files = e.GetMultipleFiles(remainingSlots);
            var updatedAttachments = _messageAttachmentComposer.PendingAttachments.ToList();

            foreach (var file in files)
            {
                if (file.Size > MaxMessageAttachmentSizeBytes)
                {
                    Console.WriteLine($"消息附件过大: {file.Name} ({FormatFileSize(file.Size)})");
                    continue;
                }

                using var stream = file.OpenReadStream(MaxMessageAttachmentSizeBytes);
                using var memoryStream = new MemoryStream();
                await stream.CopyToAsync(memoryStream);

                updatedAttachments.Add(new MessageDraftAttachmentInput
                {
                    FileName = file.Name,
                    ContentType = string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType,
                    Content = memoryStream.ToArray()
                });
            }

            _messageAttachmentComposer.Replace(updatedAttachments);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"处理消息附件失败: {ex.Message}");
        }
        finally
        {
            _isMessageAttachmentUploading = false;
            StateHasChanged();
        }
    }

    private Task RemovePendingMessageAttachment(string attachmentId)
    {
        _messageAttachmentComposer.Remove(attachmentId);
        StateHasChanged();
        return Task.CompletedTask;
    }
    
    private string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }

    private string ResolveSubmittedBy()
    {
        if (!string.IsNullOrWhiteSpace(_currentUsername))
        {
            return _currentUsername;
        }

        return UserContextService.GetCurrentUsername();
    }
    
    #endregion
    
    #region HTML预览与前端项目
    
    private string _selectedHtmlFile = string.Empty;
    private string _htmlPreviewUrl = string.Empty;
    
    // 前端项目检测相关
    private List<FrontendProjectInfo> _detectedFrontendProjects = new();
    private List<string> _availablePreviewRoots = new();
    private string _previewRootPath = string.Empty;
    private bool _showPreviewRootSelector = false;
    private string _selectedPreviewMode = "static";
    private string _selectedFrontendProject = string.Empty;
    private bool _isServerStarting = false;
    private DevServerInfo? _currentDevServer = null;
    
    private async Task RefreshHtmlPreview()
    {
        if (!string.IsNullOrEmpty(_selectedHtmlFile))
        {
            // 使用与PC端一致的API路径格式
            var encodedPath = Uri.EscapeDataString(_selectedHtmlFile.Replace("\\", "/"));
            _htmlPreviewUrl = $"/api/workspace/{_sessionId}/files/{encodedPath}?_t={DateTime.Now.Ticks}";
            StateHasChanged();
        }
    }
    
    private async Task OpenHtmlInNewWindow()
    {
        if (!string.IsNullOrEmpty(_htmlPreviewUrl))
        {
            await JSRuntime.InvokeVoidAsync("open", _htmlPreviewUrl, "_blank");
        }
    }
    
    /// <summary>
    /// 打开预览页面
    /// </summary>
    private async Task OpenPreviewTab()
    {
        await DetectFrontendProjects();
        SwitchTab("preview");
    }
    
    /// <summary>
    /// 检测工作区中的前端项目
    /// </summary>
    private async Task DetectFrontendProjects()
    {
        try
        {
            var workspacePath = CliExecutorService.GetSessionWorkspacePath(_sessionId);
            
            // 扫描可用的预览根目录
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
        }
        catch { }
    }
    
    private void TogglePreviewRootSelector()
    {
        _showPreviewRootSelector = !_showPreviewRootSelector;
        StateHasChanged();
    }
    
    private async Task SetPreviewRootPath(string path)
    {
        _previewRootPath = path;
        _showPreviewRootSelector = false;
        await DetectFrontendProjects();
    }
    
    private async Task StartPreview()
    {
        if (_currentDevServer != null)
        {
            await StopCurrentServer();
            return;
        }
        
        var project = _detectedFrontendProjects.FirstOrDefault(p => p.Key == _selectedFrontendProject);
        if (project == null) return;
        
        _isServerStarting = true;
        StateHasChanged();
        
        try
        {
            if (_selectedPreviewMode == "dev")
            {
                _currentDevServer = await DevServerManager.StartDevServerAsync(_sessionId, project);
            }
            else if (_selectedPreviewMode == "build")
            {
                _currentDevServer = await DevServerManager.StartBuildPreviewAsync(_sessionId, project);
            }
            
            if (_currentDevServer != null)
            {
                _htmlPreviewUrl = _currentDevServer.ProxyUrl;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"启动预览服务器失败: {ex.Message}");
        }
        finally
        {
            _isServerStarting = false;
            StateHasChanged();
        }
    }
    
    private async Task StopCurrentServer()
    {
        if (_currentDevServer != null)
        {
            try
            {
                await DevServerManager.StopDevServerAsync(_sessionId, _currentDevServer.ServerKey);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"停止服务器失败: {ex.Message}");
            }
            _currentDevServer = null;
            _htmlPreviewUrl = string.Empty;
            StateHasChanged();
        }
    }
    
    #endregion
    
    #region 定时任务
    
    /// <summary>
    /// 处理定时任务组件的错误
    /// </summary>
    private async Task HandleTaskError(string error)
    {
        // 可以在这里添加错误提示逻辑，如 Toast 通知
        await JSRuntime.InvokeVoidAsync("console.error", $"[定时任务错误] {error}");
    }
    
    #endregion
    
    #region 设置
    
    private bool _showUserInfo = false;
    private string _currentUsername = string.Empty;
    private bool _isAdmin = false;

    // 设备类型检测（用于PC/移动端路由跳转）
    private bool _hasCheckedDevice = false;
    
    private CodePreviewModal _codePreviewModal = default!;
    private EnvironmentVariableConfigModal _envConfigModal = default!;
    private AssistantManagementModal _assistantManagementModal = default!;
    private ExternalCliSessionImportModal _externalCliSessionImportModal = default!;
    private ProgressTracker _progressTracker = default!;
    private QuickActionsPanel _quickActionsPanel = default!;
    private UpdateNotificationModal _updateNotificationModal = default!;
    private ProjectSelectModal _projectSelectModal = default!;
    private ProjectManageModal _projectManageModal = default!;
    private AdminUserManagementModal _adminUserManagementModal = default!;
    private CreateSessionModal _createSessionModal = default!;
    
    // 版本相关
    private string _currentVersion = string.Empty;
    private bool _hasUpdate = false;
    private VersionCheckResult? _versionCheckResult;

    // 设置页选择器
    private bool _showToolPicker = false;
    private bool _showLanguagePicker = false;
    
    // PWA 安装相关
    private bool _showPwaInstallPrompt = false;
    private bool _isPwaInstalled = false;
    private bool _isIosDevice = false;
    private bool _showIosPwaGuide = false;
    private bool _showManualInstallGuide = false;
    
    /// <summary>
    /// 检查 PWA 安装状态
    /// </summary>
    private async Task CheckPwaInstallState()
    {
        try
        {
            // 检测设备类型
            _isIosDevice = await JSRuntime.InvokeAsync<bool>("PWA.isIosDevice");
            
            var isAndroid = await JSRuntime.InvokeAsync<bool>("PWA.isAndroidDevice");
            
            // 检查是否已以独立模式运行（已安装）
            _isPwaInstalled = await JSRuntime.InvokeAsync<bool>("PWA.isStandalone");
            
            Console.WriteLine($"[PWA] iOS: {_isIosDevice}, Android: {isAndroid}, Installed: {_isPwaInstalled}");
            
            if (_isPwaInstalled)
            {
                _showPwaInstallPrompt = false;
                _showIosPwaGuide = false;
                _showManualInstallGuide = false;
                return;
            }
            
            if (_isIosDevice)
            {
                // iOS: 检查是否已经关闭过引导提示
                var dismissed = await JSRuntime.InvokeAsync<string>("localStorage.getItem", "pwa-ios-guide-dismissed");
                _showIosPwaGuide = string.IsNullOrEmpty(dismissed) || dismissed != "true";
                _showPwaInstallPrompt = false;
                _showManualInstallGuide = false;
                Console.WriteLine($"[PWA] iOS guide dismissed: {dismissed}, showing: {_showIosPwaGuide}");
            }
            else if (isAndroid)
            {
                // Android: 检查是否已经关闭过安装提示
                var dismissed = await JSRuntime.InvokeAsync<string>("localStorage.getItem", "pwa-android-prompt-dismissed");
                
                // 检查是否有延迟的安装提示（beforeinstallprompt 事件触发）
                var hasInstallPrompt = await JSRuntime.InvokeAsync<bool>("PWA.hasInstallPrompt");
                
                // 如果有安装提示且用户没有关闭过，显示安装按钮
                // 如果没有安装提示，也显示引导（用户可能需要手动添加）
                if (hasInstallPrompt)
                {
                    _showPwaInstallPrompt = string.IsNullOrEmpty(dismissed) || dismissed != "true";
                    _showManualInstallGuide = false;
                }
                else
                {
                    // Android 没有触发 beforeinstallprompt，也显示手动引导
                    _showPwaInstallPrompt = false;
                    _showManualInstallGuide = string.IsNullOrEmpty(dismissed) || dismissed != "true";
                }
                _showIosPwaGuide = false;
                Console.WriteLine($"[PWA] Android hasPrompt: {hasInstallPrompt}, showPrompt: {_showPwaInstallPrompt}, showManualGuide: {_showManualInstallGuide}");
            }
            else
            {
                // 其他设备（桌面等）
                var hasInstallPrompt = await JSRuntime.InvokeAsync<bool>("PWA.hasInstallPrompt");
                _showPwaInstallPrompt = hasInstallPrompt;
                _showIosPwaGuide = false;
                _showManualInstallGuide = false;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PWA] Error checking state: {ex.Message}");
            _showPwaInstallPrompt = false;
            _showIosPwaGuide = false;
        }
    }
    
    /// <summary>
    /// 触发 PWA 安装 (Android)
    /// </summary>
    private async Task TriggerPwaInstall()
    {
        try
        {
            await JSRuntime.InvokeVoidAsync("PWA.promptInstall");
            
            // 安装后隐藏提示
            _showPwaInstallPrompt = false;
            StateHasChanged();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[PWA] 安装失败: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 关闭 PWA 引导提示（iOS 和 Android 共用）
    /// </summary>
    private async Task DismissIosPwaGuide()
    {
        try
        {
            if (_isIosDevice)
            {
                await JSRuntime.InvokeVoidAsync("localStorage.setItem", "pwa-ios-guide-dismissed", "true");
            }
            else
            {
                await JSRuntime.InvokeVoidAsync("localStorage.setItem", "pwa-android-prompt-dismissed", "true");

            }
            _showIosPwaGuide = false;
            _showPwaInstallPrompt = false;
            _showManualInstallGuide = false;
            StateHasChanged();
        }
        catch { }
    }
    
    private async Task OpenEnvConfig()
    {
        var selectedTool = _availableTools.FirstOrDefault(t => t.Id == _selectedToolId);
        if (selectedTool != null && _envConfigModal != null)
        {
            await _envConfigModal.ShowAsync(selectedTool, _currentUsername);
        }
    }

    private async Task OpenAssistantManagement()
    {
        if (_assistantManagementModal != null)
        {
            await _assistantManagementModal.ShowAsync();
        }
    }

    private async Task HandleAssistantManagementSaved(List<string> enabledAssistants)
    {
        _enabledAssistants = AssistantCatalogHelper.NormalizeEnabledAssistants(enabledAssistants);
        await LoadAvailableTools();
        StateHasChanged();
    }

    private async Task OpenExternalCliSessionImportModalAsync()
    {
        if (_externalCliSessionImportModal == null)
        {
            return;
        }

        await _externalCliSessionImportModal.ShowAsync(_currentUsername);
    }

    private async Task ReloadSessionsAfterExternalImportAsync(string sessionId)
    {
        await LoadSessions();
        StateHasChanged();
    }

    private async Task OpenSessionFromExternalImportAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        await LoadSessions();
        await LoadSessionFromDrawer(sessionId);
    }

    private void OpenToolPicker()
    {
        _showToolPicker = true;
    }

    private void CloseToolPicker()
    {
        _showToolPicker = false;
    }

    private async Task SelectTool(CliToolConfig tool)
    {
        _selectedToolId = tool.Id;
        CloseToolPicker();
        await OnToolChanged();
        StateHasChanged();
    }

    private void OpenLanguagePicker()
    {
        _showLanguagePicker = true;
    }

    private void CloseLanguagePicker()
    {
        _showLanguagePicker = false;
    }

    private async Task SelectLanguage(WebCodeCli.Domain.Domain.Service.LanguageInfo lang)
    {
        _currentLanguage = lang.Code;
        CloseLanguagePicker();
        await OnMobileLanguageChanged();
        StateHasChanged();
    }

    private string GetSelectedToolLabel()
    {
        var tool = _availableTools.FirstOrDefault(t => t.Id == _selectedToolId);
        return tool?.Name ?? T("codeAssistant.selectTool");
    }

    private string GetSelectedToolDescription()
    {
        var tool = _availableTools.FirstOrDefault(t => t.Id == _selectedToolId);
        return tool?.Description ?? string.Empty;
    }

    private string GetSelectedLanguageLabel()
    {
        var lang = _supportedLanguages.FirstOrDefault(l => l.Code == _currentLanguage);
        return lang == null ? T("codeAssistant.language") : $"{lang.NativeName} ({lang.Name})";
    }
    
    private async Task DownloadAllFiles()
    {
        try
        {
            var zipBytes = CliExecutorService.GetWorkspaceZip(_sessionId);
            if (zipBytes != null)
            {
                var base64 = Convert.ToBase64String(zipBytes);
                
                await JSRuntime.InvokeVoidAsync("eval", $@"
                    const link = document.createElement('a');
                    link.href = 'data:application/zip;base64,{base64}';
                    link.download = 'workspace.zip';
                    link.click();
                ");
            }
        }
        catch { }
    }
    
    private async Task HandleLogout()
    {
        try
        {
            await JSRuntime.InvokeVoidAsync("authHelper.logout");
            NavigationManager.NavigateTo("/login", forceLoad: true);
        }
        catch { }
    }

    private sealed class ClientAuthState
    {
        public bool IsAuthenticated { get; set; }
        public string? Username { get; set; }
        public string? Role { get; set; }
    }
    
    #endregion
    
    #region Markdown渲染
    
    private static readonly MarkdownPipeline _markdownPipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();
    
    private readonly Dictionary<string, MarkupString> _markdownCache = new();
    
    private MarkupString RenderMarkdown(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return new MarkupString(string.Empty);
        
        if (_markdownCache.TryGetValue(markdown, out var cached))
            return cached;
        
        var html = Markdown.ToHtml(markdown, _markdownPipeline);
        var result = new MarkupString(html);
        
        if (_markdownCache.Count > 100)
            _markdownCache.Clear();
        
        _markdownCache[markdown] = result;
        return result;
    }
    
    #endregion
    
    #region 生命周期
    
    protected override async Task OnInitializedAsync()
    {
        // 初始化本地化
        try
        {
            _supportedLanguages = L.GetSupportedLanguages();
            _currentLanguage = await L.GetCurrentLanguageAsync();
            await LoadTranslationsAsync();
        }
        catch { }
        
        InitializeTabs();
        
        // 检查认证状态
        if (AuthenticationService.IsAuthenticationEnabled())
        {
            try
            {
                var authState = await JSRuntime.InvokeAsync<ClientAuthState>("authHelper.getCurrentUser");
                if (!authState.IsAuthenticated || string.IsNullOrWhiteSpace(authState.Username))
                {
                    NavigationManager.NavigateTo("/login");
                    return;
                }
                
                _currentUsername = authState.Username;
                _isAdmin = string.Equals(authState.Role, UserAccessConstants.AdminRole, StringComparison.OrdinalIgnoreCase);
                _showUserInfo = true;
            }
            catch
            {
                NavigationManager.NavigateTo("/login");
                return;
            }
        }
        
        // 设置用户上下文（用于后端服务按用户隔离数据）
        // 无论认证是否启用，都需要设置用户上下文
        try
        {
            var authState = await JSRuntime.InvokeAsync<ClientAuthState>("authHelper.getCurrentUser");
            if (authState.IsAuthenticated && !string.IsNullOrWhiteSpace(authState.Username))
            {
                _currentUsername = authState.Username;
                _isAdmin = string.Equals(authState.Role, UserAccessConstants.AdminRole, StringComparison.OrdinalIgnoreCase);
                UserContextService.SetCurrentUsername(authState.Username);
                Console.WriteLine($"[用户上下文] 从认证状态设置当前用户: {authState.Username}");
            }
            else
            {
                // 如果没有存储的用户名，使用 UserContextService 的默认值
                var defaultUsername = UserContextService.GetCurrentUsername();
                Console.WriteLine($"[用户上下文] 使用默认用户: {defaultUsername}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[用户上下文] 设置用户上下文失败: {ex.Message}");
        }
        
        // 加载工具列表
        await LoadAvailableTools();
        
        // 加载技能列表
        await LoadSkillsAsync();
        
        // 加载最近会话
        await LoadSessions();
        if (_sessions.Any())
        {
            var latestSession = _sessions.OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
            if (latestSession != null)
            {
                await LoadSessionFromDrawer(latestSession.SessionId);
            }
        }
        
        // 异步检查版本更新（不阻塞页面加载）
        _ = CheckVersionUpdateAsync();
    }
    
    /// <summary>
    /// 异步检查版本更新
    /// </summary>
    private async Task CheckVersionUpdateAsync()
    {
        try
        {
            _currentVersion = VersionService.GetCurrentVersion();
            
            // 静默检查更新
            _versionCheckResult = await VersionService.CheckForUpdateAsync();
            _hasUpdate = _versionCheckResult?.HasUpdate ?? false;
            
            // 如果有更新，在控制台输出提示
            if (_hasUpdate && _versionCheckResult != null)
            {
                Console.WriteLine($"[版本检查] 发现新版本: v{_versionCheckResult.LatestVersion} (当前: v{_currentVersion})");
            }
            
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[版本检查] 检查更新失败: {ex.Message}");
            _currentVersion = VersionService.GetCurrentVersion();
        }
    }
    
    /// <summary>
    /// 手动检查更新并显示模态框
    /// </summary>
    private async Task CheckForUpdate()
    {
        if (_updateNotificationModal != null)
        {
            await _updateNotificationModal.ShowAndCheckAsync(VersionService);
        }
    }
    
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // 确保 tabs 已初始化，如果没有则重新初始化并刷新
            if (!_tabsInitialized || _tabs.Count == 0)
            {
                InitializeTabs();
                StateHasChanged();
            }
            
            if (!_hasCheckedDevice)
            {
                _hasCheckedDevice = true;
                try
                {
                    var isMobile = await JSRuntime.InvokeAsync<bool>("isMobileDevice");
                    if (!isMobile)
                    {
                        NavigationManager.NavigateTo("/code-assistant", true);
                        return;
                    }
                }
                catch
                {
                    // 忽略设备检测异常，保持当前页面
                }
            }

            // 设置移动端视口
            await SetupMobileViewport();
            
            // 立即检查 PWA 安装状态
            await CheckPwaInstallState();
            StateHasChanged();
            
            // 延迟再次检查（等待 beforeinstallprompt 事件）
            _ = Task.Run(async () =>
            {
                await Task.Delay(3000); // 等待 PWA 脚本初始化和事件触发
                await InvokeAsync(async () =>
                {
                    await CheckPwaInstallState();
                    StateHasChanged();
                });
            });
        }
    }
    
    private async Task SetupMobileViewport()
    {
        try
        {
            // 禁用双击缩放，优化触控体验
            await JSRuntime.InvokeVoidAsync("eval", @"
                // 设置视口元标签
                let viewport = document.querySelector('meta[name=viewport]');
                if (viewport) {
                    viewport.content = 'width=device-width, initial-scale=1.0, maximum-scale=1.0, user-scalable=no, viewport-fit=cover';
                }

                // 禁止页面整体滚动，防止拖拽页面导致输入区脱离
                document.documentElement.style.height = '100%';
                document.documentElement.style.overflow = 'hidden';
                document.body.style.height = '100%';
                document.body.style.overflow = 'hidden';
                
                // 处理软键盘弹出时的视口调整
                if ('visualViewport' in window) {
                    window.visualViewport.addEventListener('resize', () => {
                        document.documentElement.style.setProperty('--viewport-height', window.visualViewport.height + 'px');
                    });
                }
                
                // 阻止iOS橡皮筋效果
                document.body.style.overscrollBehavior = 'none';
            ");
        }
        catch { }
    }
    
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _outputStateSaveTimer?.Dispose();
        // 清理资源
        _createFolderDotNetRef?.Dispose();
    }
    
    #endregion
}

/// <summary>
/// JSONL使用详情
/// </summary>
public sealed class JsonlUsageDetail
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
            if (InputTokens.HasValue) { total += InputTokens.Value; hasValue = true; }
            if (CachedInputTokens.HasValue) { total += CachedInputTokens.Value; hasValue = true; }
            if (OutputTokens.HasValue) { total += OutputTokens.Value; hasValue = true; }
            return hasValue ? total : null;
        }
    }
}

/// <summary>
/// JSONL显示项
/// </summary>
public sealed class JsonlDisplayItem
{
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ItemType { get; set; }
    public JsonlUsageDetail? Usage { get; set; }
    public bool IsUnknown { get; set; }

    /// <summary>
    /// 用户问题（用于 AskUserQuestion 工具）
    /// </summary>
    public UserQuestion? UserQuestion { get; set; }
}

/// <summary>
/// JSONL事件分组
/// </summary>
public sealed class JsonlEventGroup
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty; // "command_execution" | "tool_call" | "single"
    public string Title { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public bool IsCollapsible { get; set; }
    public List<JsonlDisplayItem> Items { get; } = new();
}
