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
    
    #endregion
    
    #region 聊天功能
    
    private List<ChatMessage> _messages = new();
    private string _inputMessage = string.Empty;
    private bool _isLoading = false;
    private string _currentAssistantMessage = string.Empty;
    private string _sessionId = Guid.NewGuid().ToString();
    private bool _showQuickActions = false;
    
    // 快捷操作项
    private readonly List<QuickActionItem> _quickActionItems = new()
    {
        new("generate", "生成代码", "💻"),
        new("explain", "解释代码", "📖"),
        new("optimize", "优化代码", "⚡"),
        new("debug", "调试帮助", "🔧"),
        new("test", "生成测试", "🧪"),
        new("docs", "生成文档", "📝"),
        new("refactor", "重构代码", "🔄"),
        new("review", "代码审查", "👀")
    };
    
    private record QuickActionItem(string Id, string Title, string Icon);
    
    private void ToggleQuickActions()
    {
        _showQuickActions = !_showQuickActions;
    }
    
    private void OnQuickActionClick(QuickActionItem action)
    {
        _inputMessage = $"请帮我{action.Title}: ";
        _showQuickActions = false;
        StateHasChanged();
    }
    
    private async Task SendMessage()
    {
        if (string.IsNullOrWhiteSpace(_inputMessage) || _isLoading)
            return;
            
        var userMessage = _inputMessage.Trim();
        _inputMessage = string.Empty;
        _showQuickActions = false;
        
        // 添加用户消息
        _messages.Add(new ChatMessage
        {
            Role = "user",
            Content = userMessage,
            CreatedAt = DateTime.Now
        });
        
        _isLoading = true;
        _currentAssistantMessage = string.Empty;
        StateHasChanged();
        
        // 滚动到底部
        await ScrollToBottom();
        
        try
        {
            var contentBuilder = new StringBuilder();
            
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
                        CreatedAt = DateTime.Now
                    });
                    break;
                }
                else if (chunk.IsCompleted)
                {
                    // 完成后添加助手消息
                    var finalContent = contentBuilder.ToString();
                    if (!string.IsNullOrEmpty(finalContent))
                    {
                        _messages.Add(new ChatMessage
                        {
                            Role = "assistant",
                            Content = finalContent,
                            CreatedAt = DateTime.Now,
                            IsCompleted = true
                        });
                    }
                    break;
                }
                else
                {
                    // 流式内容
                    var chunkContent = chunk.Content ?? string.Empty;
                    contentBuilder.Append(chunkContent);
                    _currentAssistantMessage = contentBuilder.ToString();
                    
                    // 尝试解析JSONL事件
                    ProcessJsonlContent(chunkContent);
                    
                    await InvokeAsync(StateHasChanged);
                }
            }
            
            // 保存会话
            await SaveCurrentSession();
        }
        catch (Exception ex)
        {
            _messages.Add(new ChatMessage
            {
                Role = "assistant",
                Content = string.Empty,
                HasError = true,
                ErrorMessage = $"发生错误: {ex.Message}",
                CreatedAt = DateTime.Now
            });
        }
        finally
        {
            _isLoading = false;
            _currentAssistantMessage = string.Empty;
            StateHasChanged();
            await ScrollToBottom();
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
    
    private const int InitialDisplayCount = 20;
    private int _displayedEventCount = InitialDisplayCount;
    private bool _hasMoreEvents => _jsonlEvents.Count > _displayedEventCount;
    
    private readonly Dictionary<string, bool> _jsonlGroupOpenState = new();
    
    private StringBuilder _jsonlBuffer = new();
    
    private void ProcessJsonlContent(string content)
    {
        if (string.IsNullOrEmpty(content)) return;
        
        _jsonlBuffer.Append(content);
        var bufferContent = _jsonlBuffer.ToString();
        
        // 尝试按行解析JSONL
        var lines = bufferContent.Split('\n');
        _jsonlBuffer.Clear();
        
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            
            // 最后一行可能是不完整的，保存到缓冲区
            if (i == lines.Length - 1 && !bufferContent.EndsWith("\n"))
            {
                _jsonlBuffer.Append(lines[i]);
                continue;
            }
            
            if (string.IsNullOrEmpty(line)) continue;
            
            try
            {
                var jsonDoc = JsonDocument.Parse(line);
                var root = jsonDoc.RootElement;
                
                var eventType = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "" : "";
                var eventContent = root.TryGetProperty("content", out var contentProp) ? contentProp.GetString() ?? "" : "";
                var itemType = root.TryGetProperty("item_type", out var itemTypeProp) ? itemTypeProp.GetString() : null;
                
                if (!string.IsNullOrEmpty(eventType))
                {
                    OnJsonlEvent(new JsonlDisplayItem
                    {
                        Type = eventType,
                        Title = eventType,
                        Content = eventContent,
                        ItemType = itemType
                    });
                }
            }
            catch
            {
                // 不是有效的JSON，忽略
            }
        }
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
        
        foreach (var evt in events)
        {
            // 检查是否为命令执行事件 (Codex)
            if (IsCodexCommandExecutionEvent(evt))
            {
                if (activeToolGroup != null)
                {
                    groups.Add(activeToolGroup);
                    activeToolGroup = null;
                }
                
                if (activeCommandGroup == null)
                {
                    activeCommandGroup = new JsonlEventGroup
                    {
                        Id = Guid.NewGuid().ToString(),
                        Kind = "command_execution",
                        Title = "命令执行",
                        IsCollapsible = true
                    };
                }
                activeCommandGroup.Items.Add(evt);
                
                if (evt.Type == "item.completed")
                {
                    activeCommandGroup.IsCompleted = true;
                    groups.Add(activeCommandGroup);
                    activeCommandGroup = null;
                }
            }
            // 检查是否为工具调用事件 (Claude Code)
            else if (IsClaudeToolEvent(evt))
            {
                if (activeCommandGroup != null)
                {
                    groups.Add(activeCommandGroup);
                    activeCommandGroup = null;
                }
                
                if (activeToolGroup == null)
                {
                    activeToolGroup = new JsonlEventGroup
                    {
                        Id = Guid.NewGuid().ToString(),
                        Kind = "tool_call",
                        Title = "工具调用",
                        IsCollapsible = true
                    };
                }
                activeToolGroup.Items.Add(evt);
                
                if (evt.Type == "tool_result")
                {
                    activeToolGroup.IsCompleted = true;
                    groups.Add(activeToolGroup);
                    activeToolGroup = null;
                }
            }
            else
            {
                // 其他事件作为单独的卡片
                if (activeCommandGroup != null)
                {
                    groups.Add(activeCommandGroup);
                    activeCommandGroup = null;
                }
                if (activeToolGroup != null)
                {
                    groups.Add(activeToolGroup);
                    activeToolGroup = null;
                }
                
                groups.Add(new JsonlEventGroup
                {
                    Id = Guid.NewGuid().ToString(),
                    Kind = "single",
                    Title = evt.Title,
                    IsCompleted = true,
                    IsCollapsible = false,
                    Items = { evt }
                });
            }
        }
        
        // 添加未完成的组
        if (activeCommandGroup != null) groups.Add(activeCommandGroup);
        if (activeToolGroup != null) groups.Add(activeToolGroup);
        
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
        _isLoadingSessions = true;
        StateHasChanged();
        
        try
        {
            _sessions = await SessionHistoryManager.LoadSessionsAsync();
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
        _workspaceFiles.Clear();
        _currentFolderItems.Clear();
        _breadcrumbs.Clear();
        _selectedHtmlFile = string.Empty;
        _htmlPreviewUrl = string.Empty;
        
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
                _currentSession = session;
                _sessionId = session.SessionId;
                _messages = session.Messages.ToList();
                
                // 加载工作区文件
                await LoadWorkspaceFiles();
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
            await SessionHistoryManager.DeleteSessionAsync(_sessionToDelete.SessionId);
            _sessions.RemoveAll(s => s.SessionId == _sessionToDelete.SessionId);
            
            if (_currentSession?.SessionId == _sessionToDelete.SessionId)
            {
                await CreateNewSession();
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
            var session = new SessionHistory
            {
                SessionId = _sessionId,
                Title = _messages.FirstOrDefault()?.Content?.Take(50).ToString() ?? "新会话",
                Messages = _messages,
                CreatedAt = _currentSession?.CreatedAt ?? DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            
            await SessionHistoryManager.SaveSessionAsync(session);
            _currentSession = session;
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
                await _codePreviewModal.ShowAsync(_selectedFileNode.Name, content, _selectedFileNode.Extension);
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
        _htmlPreviewUrl = $"/api/preview/{_sessionId}/{_selectedFileNode.Path}";
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
            _htmlPreviewUrl = $"/api/preview/{_sessionId}/{_selectedHtmlFile}?t={DateTime.Now.Ticks}";
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
    
    private CodePreviewModal _codePreviewModal = default!;
    private EnvironmentVariableConfigModal _envConfigModal = default!;
    private ProgressTracker _progressTracker = default!;
    
    private async Task OpenEnvConfig()
    {
        var selectedTool = _availableTools.FirstOrDefault(t => t.Id == _selectedToolId);
        if (selectedTool != null && _envConfigModal != null)
        {
            await _envConfigModal.ShowAsync(selectedTool);
        }
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
        
        // 加载工具列表
        LoadAvailableTools();
        
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
