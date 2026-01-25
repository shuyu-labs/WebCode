using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using Markdig;
using System.IO;
using System.Text;
using System.Text.Json;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service;
using WebCodeCli.Domain.Domain.Service.Adapters;
using WebCodeCli.Components;
using WebCodeCli.Helpers;

namespace WebCodeCli.Pages;

/// <summary>
/// 移动端代码助手页面
/// </summary>
public partial class CodeAssistantMobile : ComponentBase, IAsyncDisposable
{
    #region 服务注入
    
    [Inject] private ICliExecutorService CliExecutorService { get; set; } = default!;
    [Inject] private IChatSessionService ChatSessionService { get; set; } = default!;
    [Inject] private ICliToolEnvironmentService CliToolEnvironmentService { get; set; } = default!;
    [Inject] private IAuthenticationService AuthenticationService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ISessionHistoryManager SessionHistoryManager { get; set; } = default!;
    [Inject] private ILocalizationService L { get; set; } = default!;
    [Inject] private WebCodeCli.Domain.Domain.Service.ISkillService SkillService { get; set; } = default!;
    [Inject] private ISessionOutputService SessionOutputService { get; set; } = default!;
    [Inject] private IUserContextService UserContextService { get; set; } = default!;
    
    #endregion
    
    #region Tab导航
    
    private string _activeTab = "chat";
    
    private readonly record struct TabItem(string Key, string Label, string Icon);
    
    private List<TabItem> _tabs = new();
    
    private void InitializeTabs()
    {
        _tabs = new List<TabItem>
        {
            new("chat", T("codeAssistant.chat"), @"<svg class=""w-6 h-6"" fill=""none"" stroke=""currentColor"" viewBox=""0 0 24 24""><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M8 12h.01M12 12h.01M16 12h.01M21 12c0 4.418-4.03 8-9 8a9.863 9.863 0 01-4.255-.949L3 20l1.395-3.72C3.512 15.042 3 13.574 3 12c0-4.418 4.03-8 9-8s9 3.582 9 8z""></path></svg>"),
            new("output", T("codeAssistant.output"), @"<svg class=""w-6 h-6"" fill=""none"" stroke=""currentColor"" viewBox=""0 0 24 24""><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M9 17v-2m3 2v-4m3 4v-6m2 10H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z""></path></svg>"),
            new("files", T("codeAssistant.files"), @"<svg class=""w-6 h-6"" fill=""none"" stroke=""currentColor"" viewBox=""0 0 24 24""><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M3 7v10a2 2 0 002 2h14a2 2 0 002-2V9a2 2 0 00-2-2h-6l-2-2H5a2 2 0 00-2 2z""></path></svg>"),
            new("preview", T("codeAssistant.preview"), @"<svg class=""w-6 h-6"" fill=""none"" stroke=""currentColor"" viewBox=""0 0 24 24""><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M10 20l4-16m4 4l4 4-4 4M6 16l-4-4 4-4""></path></svg>"),
            new("settings", T("codeAssistant.settings"), @"<svg class=""w-6 h-6"" fill=""none"" stroke=""currentColor"" viewBox=""0 0 24 24""><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z""></path><path stroke-linecap=""round"" stroke-linejoin=""round"" stroke-width=""2"" d=""M15 12a3 3 0 11-6 0 3 3 0 016 0z""></path></svg>")
        };
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
    
    #endregion
    
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
                    groups.Add(activeToolGroup);
                    continue;
                }

                if (activeToolGroup != null)
                {
                    activeToolGroup.Items.Add(evt);
                    if (evt.Type == "tool_result")
                    {
                        activeToolGroup.IsCompleted = true;
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
                } : null
            }).ToList()
        }).ToList();
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

        return _jsonlAssistantMessageBuilder.ToString();
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
    
    // 删除会话
    private bool _showDeleteSessionDialog = false;
    private SessionHistory? _sessionToDelete = null;
    private bool _isDeletingSession = false;
    
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
        _sessionId = Guid.NewGuid().ToString();
        _messages.Clear();
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

        var workspacePath = CliExecutorService.GetSessionWorkspacePath(_sessionId);
        if (!Directory.Exists(workspacePath))
        {
            Directory.CreateDirectory(workspacePath);
        }

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

        try
        {
            await SessionHistoryManager.SaveSessionImmediateAsync(_currentSession);
            await LoadSessions();
        }
        catch { }

        StateHasChanged();
    }
    
    private async Task CreateNewSessionFromDrawer()
    {
        await CreateNewSession();
        CloseSessionDrawer();
    }
    
    private async Task LoadSessionFromDrawer(string sessionId)
    {
        _isLoadingSession = true;
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
    private string _selectedToolId = string.Empty;
    
    private void LoadAvailableTools()
    {
        try
        {
            _availableTools = CliExecutorService.GetAvailableTools();
            if (_availableTools.Any() && string.IsNullOrEmpty(_selectedToolId))
            {
                _selectedToolId = _availableTools.First().Id;
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
        // 工具切换后可以执行一些操作
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
    
    private void ShowCreateFolderDialog()
    {
        _newFolderName = string.Empty;
        _showCreateFolderDialog = true;
    }
    
    private void CloseCreateFolderDialog()
    {
        _showCreateFolderDialog = false;
        _newFolderName = string.Empty;
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
    
    private string FormatFileSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F1} GB";
    }
    
    #endregion
    
    #region HTML预览
    
    private string _selectedHtmlFile = string.Empty;
    private string _htmlPreviewUrl = string.Empty;
    
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
    
    #endregion
    
    #region 设置
    
    private bool _showUserInfo = false;
    private string _currentUsername = string.Empty;

    // 设备类型检测（用于PC/移动端路由跳转）
    private bool _hasCheckedDevice = false;
    
    private CodePreviewModal _codePreviewModal = default!;
    private EnvironmentVariableConfigModal _envConfigModal = default!;
    private ProgressTracker _progressTracker = default!;
    private QuickActionsPanel _quickActionsPanel = default!;

    // 设置页选择器
    private bool _showToolPicker = false;
    private bool _showLanguagePicker = false;
    
    private async Task OpenEnvConfig()
    {
        var selectedTool = _availableTools.FirstOrDefault(t => t.Id == _selectedToolId);
        if (selectedTool != null && _envConfigModal != null)
        {
            await _envConfigModal.ShowAsync(selectedTool);
        }
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
            await JSRuntime.InvokeVoidAsync("sessionStorage.removeItem", "isAuthenticated");
            await JSRuntime.InvokeVoidAsync("sessionStorage.removeItem", "username");
            NavigationManager.NavigateTo("/login");
        }
        catch { }
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
                var isAuthenticated = await JSRuntime.InvokeAsync<string>("sessionStorage.getItem", "isAuthenticated");
                if (isAuthenticated != "true")
                {
                    NavigationManager.NavigateTo("/login");
                    return;
                }
                
                _currentUsername = await JSRuntime.InvokeAsync<string>("sessionStorage.getItem", "username") ?? "用户";
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
            // 尝试从 sessionStorage 获取用户名
            var storedUsername = await JSRuntime.InvokeAsync<string>("sessionStorage.getItem", "username");
            if (!string.IsNullOrWhiteSpace(storedUsername))
            {
                _currentUsername = storedUsername;
                UserContextService.SetCurrentUsername(storedUsername);
                Console.WriteLine($"[用户上下文] 从sessionStorage设置当前用户: {storedUsername}");
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
        LoadAvailableTools();
        
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
    }
    
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
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
