using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service;
using WebCodeCli.Helpers;

namespace WebCodeCli.Components;

public partial class AdminUserManagementModal : ComponentBase
{
    [Inject] private HttpClient Http { get; set; } = default!;
    [Inject] private ICliExecutorService CliExecutorService { get; set; } = default!;
    [Inject] private ILocalizationService L { get; set; } = default!;

    [Parameter] public EventCallback OnClose { get; set; }
    [Parameter] public EventCallback OnSaved { get; set; }

    private bool _isVisible;
    private bool _isLoadingUsers;
    private bool _isLoadingDetail;
    private bool _isSaving;
    private bool _isDeletingFeishuConfig;
    private bool _isRefreshingFeishuStatus;
    private bool _isStartingFeishuBot;
    private bool _isStoppingFeishuBot;
    private string _errorMessage = string.Empty;
    private string _successMessage = string.Empty;
    private string _userSearch = string.Empty;
    private string? _selectedUsername;
    private string _currentLanguage = "zh-CN";
    private Dictionary<string, string> _translations = new();
    private List<CliToolConfig> _allTools = new();
    private List<UserSummaryDto> _users = new();
    private EditableUserModel _editor = EditableUserModel.CreateNew();
    private UserFeishuBotRuntimeStatusModel _feishuBotStatus = new();

    private bool IsBusy => _isLoadingUsers || _isLoadingDetail || _isSaving || _isDeletingFeishuConfig || _isRefreshingFeishuStatus || _isStartingFeishuBot || _isStoppingFeishuBot;

    private IEnumerable<UserSummaryDto> FilteredUsers => _users.Where(user =>
        string.IsNullOrWhiteSpace(_userSearch) ||
        user.Username.Contains(_userSearch, StringComparison.OrdinalIgnoreCase) ||
        (!string.IsNullOrWhiteSpace(user.DisplayName) && user.DisplayName.Contains(_userSearch, StringComparison.OrdinalIgnoreCase)));

    private IEnumerable<CliToolConfig> OrderedTools => _allTools.OrderBy(static x => x.Name, StringComparer.OrdinalIgnoreCase);

    public async Task ShowAsync()
    {
        await EnsureLocalizationAsync();
        _isVisible = true;
        _errorMessage = string.Empty;
        _successMessage = string.Empty;
        _userSearch = string.Empty;
        _allTools = CliExecutorService.GetAvailableTools();
        var usersLoaded = await LoadUsersAsync();
        if (!usersLoaded)
        {
            return;
        }

        if (_users.Count == 0)
        {
            PrepareNewUser();
        }
        else if (!string.IsNullOrWhiteSpace(_selectedUsername) && _users.Any(x => string.Equals(x.Username, _selectedUsername, StringComparison.OrdinalIgnoreCase)))
        {
            await SelectUserAsync(_selectedUsername);
        }
        else
        {
            await SelectUserAsync(_users[0].Username);
        }

        StateHasChanged();
    }

    private async Task CloseAsync()
    {
        _isVisible = false;
        _errorMessage = string.Empty;
        _successMessage = string.Empty;
        await OnClose.InvokeAsync();
        StateHasChanged();
    }

    private async Task RefreshAsync()
    {
        _allTools = CliExecutorService.GetAvailableTools();
        var usersLoaded = await LoadUsersAsync();
        if (!usersLoaded)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(_selectedUsername) && _users.Any(x => string.Equals(x.Username, _selectedUsername, StringComparison.OrdinalIgnoreCase)))
        {
            await SelectUserAsync(_selectedUsername);
        }
        else if (_users.Count > 0)
        {
            await SelectUserAsync(_users[0].Username);
        }
        else
        {
            PrepareNewUser();
        }
    }

    private async Task<bool> LoadUsersAsync()
    {
        _isLoadingUsers = true;
        _errorMessage = string.Empty;
        StateHasChanged();

        try
        {
            _users = (await Http.GetFromJsonAsync<List<UserSummaryDto>>("/api/admin/users"))?
                .OrderByDescending(static x => IsAdminRole(x.Role))
                .ThenByDescending(static x => IsEnabled(x.Status))
                .ThenBy(static x => x.Username, StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<UserSummaryDto>();
            return true;
        }
        catch (Exception ex)
        {
            _errorMessage = Tx("adminUserManagement.loadUsersFailed", "加载用户列表失败", "Failed to load users") + $": {ex.Message}";
        }
        finally
        {
            _isLoadingUsers = false;
            StateHasChanged();
        }

        return false;
    }

    private void PrepareNewUser()
    {
        _selectedUsername = null;
        _editor = EditableUserModel.CreateNew();
        _feishuBotStatus = new UserFeishuBotRuntimeStatusModel();
        _errorMessage = string.Empty;
        _successMessage = string.Empty;
        StateHasChanged();
    }

    private async Task SelectUserAsync(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            PrepareNewUser();
            return;
        }

        var selectedUser = _users.FirstOrDefault(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase));
        if (selectedUser == null)
        {
            PrepareNewUser();
            return;
        }

        _selectedUsername = selectedUser.Username;
        _errorMessage = string.Empty;
        _successMessage = string.Empty;
        _isLoadingDetail = true;
        StateHasChanged();

        var nextEditor = CreateDetailEditorSeed(selectedUser, _editor);
        var nextFeishuStatus = CreateFeishuStatusSeed(selectedUser.Username, _feishuBotStatus);
        var detailErrors = new List<string>();

        try
        {
            try
            {
                var toolMap = await Http.GetFromJsonAsync<Dictionary<string, bool>>($"/api/admin/users/{Uri.EscapeDataString(selectedUser.Username)}/tools");
                nextEditor.AllowedToolIds = AdminUserManagementFormHelper.GetAllowedToolIds(toolMap ?? new Dictionary<string, bool>());
            }
            catch (Exception ex)
            {
                detailErrors.Add($"tools: {ex.Message}");
            }

            try
            {
                var directories = await Http.GetFromJsonAsync<List<string>>($"/api/admin/users/{Uri.EscapeDataString(selectedUser.Username)}/workspace-policies");
                nextEditor.AllowedDirectoriesText = AdminUserManagementFormHelper.FormatAllowedDirectories(directories ?? new List<string>());
            }
            catch (Exception ex)
            {
                detailErrors.Add($"workspace policies: {ex.Message}");
            }

            try
            {
                var feishuConfig = await Http.GetFromJsonAsync<UserFeishuBotConfigModel>($"/api/admin/users/{Uri.EscapeDataString(selectedUser.Username)}/feishu-bot")
                    ?? new UserFeishuBotConfigModel();
                nextEditor.FeishuBot = EditableFeishuBotConfigModel.From(feishuConfig);
                nextEditor.HasStoredFeishuConfig = HasCustomFeishuConfig(nextEditor.FeishuBot);
            }
            catch (Exception ex)
            {
                detailErrors.Add($"feishu config: {ex.Message}");
            }

            try
            {
                nextFeishuStatus = await Http.GetFromJsonAsync<UserFeishuBotRuntimeStatusModel>($"/api/admin/users/{Uri.EscapeDataString(selectedUser.Username)}/feishu-bot/status")
                    ?? new UserFeishuBotRuntimeStatusModel();
            }
            catch (Exception ex)
            {
                detailErrors.Add($"feishu status: {ex.Message}");
            }

            _editor = nextEditor;
            _feishuBotStatus = nextFeishuStatus;
        }
        catch (Exception ex)
        {
            _errorMessage = Tx("adminUserManagement.loadUserDetailFailed", "加载用户详情失败", "Failed to load user details") + $": {ex.Message}";
        }
        finally
        {
            if (detailErrors.Count > 0)
            {
                _errorMessage = Tx("adminUserManagement.loadUserDetailFailed", "鍔犺浇鐢ㄦ埛璇︽儏澶辫触", "Failed to load user details") + $": {string.Join("; ", detailErrors)}";
            }

            _isLoadingDetail = false;
            StateHasChanged();
        }
    }

    private void SetToolSelection(string toolId, bool isSelected)
    {
        if (isSelected)
        {
            _editor.AllowedToolIds.Add(toolId);
        }
        else
        {
            _editor.AllowedToolIds.Remove(toolId);
        }
    }

    private void AllowAllTools() => _editor.AllowedToolIds = new HashSet<string>(_allTools.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);

    private void ClearAllTools() => _editor.AllowedToolIds.Clear();

    private async Task SaveAsync()
    {
        var username = _editor.Username.Trim();
        _errorMessage = string.Empty;
        _successMessage = string.Empty;

        if (string.IsNullOrWhiteSpace(username))
        {
            _errorMessage = Tx("adminUserManagement.usernameRequired", "用户名不能为空。", "Username is required.");
            return;
        }

        if (!_editor.IsExistingUser && string.IsNullOrWhiteSpace(_editor.Password))
        {
            _errorMessage = Tx("adminUserManagement.passwordRequired", "新建用户时必须填写密码。", "A password is required for a new user.");
            return;
        }

        _isSaving = true;
        StateHasChanged();

        try
        {
            await SaveUserCoreAsync(username);
            _selectedUsername = username;
            var usersLoaded = await LoadUsersAsync();
            if (usersLoaded && _users.Any(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)))
            {
                await SelectUserAsync(username);
            }
            else
            {
                _editor.IsExistingUser = true;
            }
            _editor.Password = string.Empty;
            _successMessage = Tx("adminUserManagement.saveSuccess", "已保存用户配置。飞书机器人需要手动启动。", "User settings saved. Start the Feishu bot manually when ready.");
            await OnSaved.InvokeAsync();
        }
        catch (Exception ex)
        {
            _errorMessage = ex.Message;
        }
        finally
        {
            _isSaving = false;
            StateHasChanged();
        }
    }

    private async Task ResetFeishuConfigAsync()
    {
        if (!_editor.IsExistingUser || string.IsNullOrWhiteSpace(_editor.Username))
        {
            return;
        }

        _isDeletingFeishuConfig = true;
        _errorMessage = string.Empty;
        _successMessage = string.Empty;
        StateHasChanged();

        try
        {
            var response = await Http.DeleteAsync($"/api/admin/users/{Uri.EscapeDataString(_editor.Username)}/feishu-bot");
            await EnsureSuccessAsync(response);
            _editor.FeishuBot = new EditableFeishuBotConfigModel();
            _editor.HasStoredFeishuConfig = false;
            _feishuBotStatus = new UserFeishuBotRuntimeStatusModel
            {
                Username = _editor.Username,
                State = nameof(UserFeishuBotRuntimeState.NotConfigured),
                Message = Tx("adminUserManagement.feishuNotConfigured", "当前用户尚未配置可启动的飞书机器人。", "No runnable Feishu bot is configured for this user.")
            };
            _successMessage = Tx("adminUserManagement.resetFeishuSuccess", "已清空用户飞书配置。如需使用，请重新填写后手动启动。", "The user-specific Feishu configuration has been cleared. Save new credentials and start it manually when needed.");
        }
        catch (Exception ex)
        {
            _errorMessage = ex.Message;
        }
        finally
        {
            _isDeletingFeishuConfig = false;
            StateHasChanged();
        }
    }

    private async Task SaveUserCoreAsync(string username)
    {
        var saveUserResponse = await Http.PostAsJsonAsync("/api/admin/users", new SaveUserPayload
        {
            Username = username,
            DisplayName = string.IsNullOrWhiteSpace(_editor.DisplayName) ? username : _editor.DisplayName.Trim(),
            Password = string.IsNullOrWhiteSpace(_editor.Password) ? null : _editor.Password,
            Role = IsAdminRole(_editor.Role) ? UserAccessConstants.AdminRole : UserAccessConstants.UserRole,
            Status = _editor.Enabled ? UserAccessConstants.EnabledStatus : UserAccessConstants.DisabledStatus
        });
        await EnsureSuccessAsync(saveUserResponse);

        var saveToolsResponse = await Http.PutAsJsonAsync($"/api/admin/users/{Uri.EscapeDataString(username)}/tools", new SaveToolPolicyPayload
        {
            AllowedToolIds = _editor.AllowedToolIds.OrderBy(static x => x, StringComparer.OrdinalIgnoreCase).ToList()
        });
        await EnsureSuccessAsync(saveToolsResponse);

        var saveDirectoriesResponse = await Http.PutAsJsonAsync($"/api/admin/users/{Uri.EscapeDataString(username)}/workspace-policies", new SaveWorkspacePolicyPayload
        {
            AllowedDirectories = AdminUserManagementFormHelper.ParseAllowedDirectories(_editor.AllowedDirectoriesText)
        });
        await EnsureSuccessAsync(saveDirectoriesResponse);

        var hasCustomFeishuConfig = HasCustomFeishuConfig(_editor.FeishuBot);

        if (hasCustomFeishuConfig)
        {
            var saveFeishuResponse = await Http.PutAsJsonAsync($"/api/admin/users/{Uri.EscapeDataString(username)}/feishu-bot", _editor.FeishuBot.ToPayload(username));
            await EnsureSuccessAsync(saveFeishuResponse);
        }
        else if (_editor.IsExistingUser && _editor.HasStoredFeishuConfig)
        {
            var deleteFeishuResponse = await Http.DeleteAsync($"/api/admin/users/{Uri.EscapeDataString(username)}/feishu-bot");
            await EnsureSuccessAsync(deleteFeishuResponse);
        }
    }

    private async Task RefreshFeishuBotStatusAsync()
    {
        if (!_editor.IsExistingUser || string.IsNullOrWhiteSpace(_editor.Username))
        {
            return;
        }

        _isRefreshingFeishuStatus = true;
        _errorMessage = string.Empty;
        _successMessage = string.Empty;
        StateHasChanged();

        try
        {
            _feishuBotStatus = await Http.GetFromJsonAsync<UserFeishuBotRuntimeStatusModel>(
                $"/api/admin/users/{Uri.EscapeDataString(_editor.Username)}/feishu-bot/status")
                ?? new UserFeishuBotRuntimeStatusModel();
        }
        catch (Exception ex)
        {
            _errorMessage = ex.Message;
        }
        finally
        {
            _isRefreshingFeishuStatus = false;
            StateHasChanged();
        }
    }

    private async Task StartFeishuBotAsync()
    {
        if (!_editor.IsExistingUser || string.IsNullOrWhiteSpace(_editor.Username))
        {
            return;
        }

        _isStartingFeishuBot = true;
        _errorMessage = string.Empty;
        _successMessage = string.Empty;
        StateHasChanged();

        try
        {
            var response = await Http.PostAsJsonAsync<object?>(
                $"/api/admin/users/{Uri.EscapeDataString(_editor.Username)}/feishu-bot/start",
                null);
            await EnsureSuccessAsync(response);
            _feishuBotStatus = await response.Content.ReadFromJsonAsync<UserFeishuBotRuntimeStatusModel>() ?? new UserFeishuBotRuntimeStatusModel();

            if (string.Equals(_feishuBotStatus.State, nameof(UserFeishuBotRuntimeState.Connected), StringComparison.OrdinalIgnoreCase)
                || string.Equals(_feishuBotStatus.State, nameof(UserFeishuBotRuntimeState.Starting), StringComparison.OrdinalIgnoreCase))
            {
                _successMessage = _feishuBotStatus.Message ?? Tx("adminUserManagement.feishuStartSuccess", "飞书机器人已启动。", "Feishu bot started.");
            }
            else
            {
                _errorMessage = string.Empty;
            }
        }
        catch (Exception ex)
        {
            _errorMessage = ex.Message;
        }
        finally
        {
            _isStartingFeishuBot = false;
            StateHasChanged();
        }
    }

    private async Task StopFeishuBotAsync()
    {
        if (!_editor.IsExistingUser || string.IsNullOrWhiteSpace(_editor.Username))
        {
            return;
        }

        _isStoppingFeishuBot = true;
        _errorMessage = string.Empty;
        _successMessage = string.Empty;
        StateHasChanged();

        try
        {
            var response = await Http.PostAsJsonAsync<object?>(
                $"/api/admin/users/{Uri.EscapeDataString(_editor.Username)}/feishu-bot/stop",
                null);
            await EnsureSuccessAsync(response);
            _feishuBotStatus = await response.Content.ReadFromJsonAsync<UserFeishuBotRuntimeStatusModel>() ?? new UserFeishuBotRuntimeStatusModel();

            _successMessage = _feishuBotStatus.Message ?? Tx("adminUserManagement.feishuStopSuccess", "飞书机器人已停止。", "Feishu bot stopped.");
        }
        catch (Exception ex)
        {
            _errorMessage = ex.Message;
        }
        finally
        {
            _isStoppingFeishuBot = false;
            StateHasChanged();
        }
    }

    private async Task EnsureLocalizationAsync()
    {
        try
        {
            _currentLanguage = await L.GetCurrentLanguageAsync();
            _translations = FlattenTranslations(await L.GetAllTranslationsAsync(_currentLanguage));
        }
        catch
        {
            _translations = new Dictionary<string, string>();
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new InvalidOperationException(await ExtractErrorMessageAsync(response));
    }

    private static async Task<string> ExtractErrorMessageAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(content)) return $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}";
        try
        {
            using var document = JsonDocument.Parse(content);
            if (document.RootElement.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.String) return errorElement.GetString() ?? content.Trim();
            if (document.RootElement.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String) return messageElement.GetString() ?? content.Trim();
        }
        catch { }
        return content.Trim();
    }

    private static Dictionary<string, string> FlattenTranslations(Dictionary<string, object> source, string prefix = "")
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
                    if (nested != null) foreach (var item in FlattenTranslations(nested, key)) result[item.Key] = item.Value;
                }
                else if (jsonElement.ValueKind == JsonValueKind.String) result[key] = jsonElement.GetString() ?? key;
            }
            else if (kvp.Value is Dictionary<string, object> nestedDictionary)
            {
                foreach (var item in FlattenTranslations(nestedDictionary, key)) result[item.Key] = item.Value;
            }
            else if (kvp.Value is string value) result[key] = value;
        }
        return result;
    }

    private string Tx(string key, string zhFallback, string enFallback) => _translations.TryGetValue(key, out var value) ? value : (_currentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zhFallback : enFallback);
    private static string GetDisplayName(UserSummaryDto user) => string.IsNullOrWhiteSpace(user.DisplayName) ? user.Username : user.DisplayName;
    private static bool IsAdminRole(string? role) => string.Equals(role, UserAccessConstants.AdminRole, StringComparison.OrdinalIgnoreCase);
    private static bool IsEnabled(string? status) => string.Equals(status, UserAccessConstants.EnabledStatus, StringComparison.OrdinalIgnoreCase);
    private bool CanStartFeishuBot => _editor.IsExistingUser && _editor.Enabled && _feishuBotStatus.CanStart && !_isStartingFeishuBot && !_isLoadingDetail;
    private bool CanStopFeishuBot => _editor.IsExistingUser && (string.Equals(_feishuBotStatus.State, nameof(UserFeishuBotRuntimeState.Connected), StringComparison.OrdinalIgnoreCase) || string.Equals(_feishuBotStatus.State, nameof(UserFeishuBotRuntimeState.Starting), StringComparison.OrdinalIgnoreCase)) && !_isStoppingFeishuBot && !_isLoadingDetail;
    private string GetRoleLabel(string? role) => IsAdminRole(role) ? Tx("adminUserManagement.roleAdmin", "管理员", "Admin") : Tx("adminUserManagement.roleUser", "普通用户", "User");
    private string GetStatusLabel(string? status) => IsEnabled(status) ? Tx("adminUserManagement.statusEnabled", "启用", "Enabled") : Tx("adminUserManagement.statusDisabled", "禁用", "Disabled");
    private string GetFeishuRuntimeLabel(string? state) => state?.ToLowerInvariant() switch
    {
        "connected" => Tx("adminUserManagement.feishuStateConnected", "已连接", "Connected"),
        "starting" => Tx("adminUserManagement.feishuStateStarting", "启动中", "Starting"),
        "stopping" => Tx("adminUserManagement.feishuStateStopping", "停止中", "Stopping"),
        "stopped" => Tx("adminUserManagement.feishuStateStopped", "已停止", "Stopped"),
        "failed" => Tx("adminUserManagement.feishuStateFailed", "异常", "Failed"),
        _ => Tx("adminUserManagement.feishuStateNotConfigured", "未配置", "Not configured")
    };
    private static string GetRoleBadgeClass(string? role) => IsAdminRole(role) ? "inline-flex items-center rounded-full bg-amber-100 px-2 py-1 text-[11px] font-medium text-amber-800" : "inline-flex items-center rounded-full bg-blue-100 px-2 py-1 text-[11px] font-medium text-blue-800";
    private static string GetStatusBadgeClass(string? status) => IsEnabled(status) ? "inline-flex items-center rounded-full bg-emerald-100 px-2 py-1 text-[11px] font-medium text-emerald-800" : "inline-flex items-center rounded-full bg-red-100 px-2 py-1 text-[11px] font-medium text-red-700";
    private static string GetFeishuRuntimeBadgeClass(string? state) => state?.ToLowerInvariant() switch
    {
        "connected" => "inline-flex items-center rounded-full bg-emerald-100 px-2 py-1 text-[11px] font-medium text-emerald-800",
        "starting" => "inline-flex items-center rounded-full bg-blue-100 px-2 py-1 text-[11px] font-medium text-blue-800",
        "stopping" => "inline-flex items-center rounded-full bg-amber-100 px-2 py-1 text-[11px] font-medium text-amber-800",
        "failed" => "inline-flex items-center rounded-full bg-red-100 px-2 py-1 text-[11px] font-medium text-red-700",
        "stopped" => "inline-flex items-center rounded-full bg-slate-100 px-2 py-1 text-[11px] font-medium text-slate-700",
        _ => "inline-flex items-center rounded-full bg-gray-100 px-2 py-1 text-[11px] font-medium text-gray-600"
    };
    private static string GetMetaValue(DateTime? value) => value.HasValue ? value.Value.ToString("yyyy-MM-dd HH:mm") : "-";
    private static EditableUserModel CreateDetailEditorSeed(UserSummaryDto selectedUser, EditableUserModel currentEditor)
    {
        if (!string.Equals(currentEditor.Username, selectedUser.Username, StringComparison.OrdinalIgnoreCase))
        {
            return EditableUserModel.FromSummary(selectedUser);
        }

        return new EditableUserModel
        {
            Username = selectedUser.Username,
            DisplayName = selectedUser.DisplayName ?? string.Empty,
            Role = selectedUser.Role,
            Enabled = IsEnabled(selectedUser.Status),
            IsExistingUser = true,
            HasStoredFeishuConfig = currentEditor.HasStoredFeishuConfig,
            LastLoginAt = selectedUser.LastLoginAt,
            CreatedAt = selectedUser.CreatedAt,
            AllowedToolIds = new HashSet<string>(currentEditor.AllowedToolIds, StringComparer.OrdinalIgnoreCase),
            AllowedDirectoriesText = currentEditor.AllowedDirectoriesText,
            FeishuBot = currentEditor.FeishuBot.Clone()
        };
    }

    private static UserFeishuBotRuntimeStatusModel CreateFeishuStatusSeed(string username, UserFeishuBotRuntimeStatusModel currentStatus)
    {
        if (string.Equals(currentStatus.Username, username, StringComparison.OrdinalIgnoreCase))
        {
            return currentStatus.Clone();
        }

        return new UserFeishuBotRuntimeStatusModel
        {
            Username = username
        };
    }

    private static bool HasCustomFeishuConfig(EditableFeishuBotConfigModel config)
    {
        return AdminUserManagementFormHelper.HasCustomFeishuConfig(
                   config.IsEnabled,
                   config.AppId,
                   config.AppSecret,
                   config.EncryptKey,
                   config.VerificationToken,
                   config.DefaultCardTitle,
                   config.ThinkingMessage,
                   config.DocumentAdminOpenId,
                   config.HttpTimeoutSeconds,
                   config.StreamingThrottleMs)
               || config.FullReplyDocEnabled
               || config.FinalReplyDocEnabled
               || config.AudioFullReplyDocEnabled
               || config.AudioFinalReplyDocEnabled
               || config.ReferencedMarkdownDocImportEnabled;
    }

    private sealed class UserSummaryDto { public string Username { get; set; } = string.Empty; public string? DisplayName { get; set; } public string Role { get; set; } = UserAccessConstants.UserRole; public string Status { get; set; } = UserAccessConstants.EnabledStatus; public DateTime? LastLoginAt { get; set; } public DateTime CreatedAt { get; set; } }
    private sealed class SaveUserPayload { public string Username { get; set; } = string.Empty; public string? DisplayName { get; set; } public string? Password { get; set; } public string Role { get; set; } = UserAccessConstants.UserRole; public string Status { get; set; } = UserAccessConstants.EnabledStatus; }
    private sealed class SaveToolPolicyPayload { public List<string> AllowedToolIds { get; set; } = new(); }
    private sealed class SaveWorkspacePolicyPayload { public List<string> AllowedDirectories { get; set; } = new(); }
    private sealed class UserFeishuBotConfigModel
    {
        public string? Username { get; set; }
        public bool IsEnabled { get; set; }
        public string? AppId { get; set; }
        public string? AppSecret { get; set; }
        public string? EncryptKey { get; set; }
        public string? VerificationToken { get; set; }
        public string? DefaultCardTitle { get; set; }
        public string? ThinkingMessage { get; set; }
        public string? DocumentAdminOpenId { get; set; }
        public int? HttpTimeoutSeconds { get; set; }
        public int? StreamingThrottleMs { get; set; }
        public bool FullReplyDocEnabled { get; set; }
        public bool FinalReplyDocEnabled { get; set; }
        public bool AudioFullReplyDocEnabled { get; set; }
        public bool AudioFinalReplyDocEnabled { get; set; }
        public bool ReferencedMarkdownDocImportEnabled { get; set; }
    }
    private sealed class UserFeishuBotRuntimeStatusModel { public string Username { get; set; } = string.Empty; public string? AppId { get; set; } public string State { get; set; } = nameof(UserFeishuBotRuntimeState.NotConfigured); public bool IsConfigured { get; set; } public bool CanStart { get; set; } public bool ShouldAutoStart { get; set; } public string? Message { get; set; } public string? LastError { get; set; } public DateTime? LastStartedAt { get; set; } public DateTime UpdatedAt { get; set; } public UserFeishuBotRuntimeStatusModel Clone() => new() { Username = Username, AppId = AppId, State = State, IsConfigured = IsConfigured, CanStart = CanStart, ShouldAutoStart = ShouldAutoStart, Message = Message, LastError = LastError, LastStartedAt = LastStartedAt, UpdatedAt = UpdatedAt }; }
    private sealed class EditableFeishuBotConfigModel
    {
        public bool IsEnabled { get; set; }
        public string? AppId { get; set; }
        public string? AppSecret { get; set; }
        public string? EncryptKey { get; set; }
        public string? VerificationToken { get; set; }
        public string? DefaultCardTitle { get; set; }
        public string? ThinkingMessage { get; set; }
        public string? DocumentAdminOpenId { get; set; }
        public int? HttpTimeoutSeconds { get; set; }
        public int? StreamingThrottleMs { get; set; }
        public bool FullReplyDocEnabled { get; set; }
        public bool FinalReplyDocEnabled { get; set; }
        public bool AudioFullReplyDocEnabled { get; set; }
        public bool AudioFinalReplyDocEnabled { get; set; }
        public bool ReferencedMarkdownDocImportEnabled { get; set; }

        public EditableFeishuBotConfigModel Clone() => new()
        {
            IsEnabled = IsEnabled,
            AppId = AppId,
            AppSecret = AppSecret,
            EncryptKey = EncryptKey,
            VerificationToken = VerificationToken,
            DefaultCardTitle = DefaultCardTitle,
            ThinkingMessage = ThinkingMessage,
            DocumentAdminOpenId = DocumentAdminOpenId,
            HttpTimeoutSeconds = HttpTimeoutSeconds,
            StreamingThrottleMs = StreamingThrottleMs,
            FullReplyDocEnabled = FullReplyDocEnabled,
            FinalReplyDocEnabled = FinalReplyDocEnabled,
            AudioFullReplyDocEnabled = AudioFullReplyDocEnabled,
            AudioFinalReplyDocEnabled = AudioFinalReplyDocEnabled,
            ReferencedMarkdownDocImportEnabled = ReferencedMarkdownDocImportEnabled
        };

        public static EditableFeishuBotConfigModel From(UserFeishuBotConfigModel model) => new()
        {
            IsEnabled = model.IsEnabled,
            AppId = model.AppId,
            AppSecret = model.AppSecret,
            EncryptKey = model.EncryptKey,
            VerificationToken = model.VerificationToken,
            DefaultCardTitle = model.DefaultCardTitle,
            ThinkingMessage = model.ThinkingMessage,
            DocumentAdminOpenId = model.DocumentAdminOpenId,
            HttpTimeoutSeconds = model.HttpTimeoutSeconds,
            StreamingThrottleMs = model.StreamingThrottleMs,
            FullReplyDocEnabled = model.FullReplyDocEnabled,
            FinalReplyDocEnabled = model.FinalReplyDocEnabled,
            AudioFullReplyDocEnabled = model.AudioFullReplyDocEnabled,
            AudioFinalReplyDocEnabled = model.AudioFinalReplyDocEnabled,
            ReferencedMarkdownDocImportEnabled = model.ReferencedMarkdownDocImportEnabled
        };

        public UserFeishuBotConfigModel ToPayload(string username) => new()
        {
            Username = username,
            IsEnabled = IsEnabled,
            AppId = TrimToNull(AppId),
            AppSecret = TrimToNull(AppSecret),
            EncryptKey = TrimToNull(EncryptKey),
            VerificationToken = TrimToNull(VerificationToken),
            DefaultCardTitle = TrimToNull(DefaultCardTitle),
            ThinkingMessage = TrimToNull(ThinkingMessage),
            DocumentAdminOpenId = TrimToNull(DocumentAdminOpenId),
            HttpTimeoutSeconds = HttpTimeoutSeconds,
            StreamingThrottleMs = StreamingThrottleMs,
            FullReplyDocEnabled = FullReplyDocEnabled,
            FinalReplyDocEnabled = FinalReplyDocEnabled,
            AudioFullReplyDocEnabled = AudioFullReplyDocEnabled,
            AudioFinalReplyDocEnabled = AudioFinalReplyDocEnabled,
            ReferencedMarkdownDocImportEnabled = ReferencedMarkdownDocImportEnabled
        };
    }
    private sealed class EditableUserModel { public string Username { get; set; } = string.Empty; public string DisplayName { get; set; } = string.Empty; public string Password { get; set; } = string.Empty; public string Role { get; set; } = UserAccessConstants.UserRole; public bool Enabled { get; set; } = true; public bool IsExistingUser { get; set; } public bool HasStoredFeishuConfig { get; set; } public DateTime? LastLoginAt { get; set; } public DateTime? CreatedAt { get; set; } public HashSet<string> AllowedToolIds { get; set; } = new(StringComparer.OrdinalIgnoreCase); public string AllowedDirectoriesText { get; set; } = string.Empty; public EditableFeishuBotConfigModel FeishuBot { get; set; } = new(); public static EditableUserModel CreateNew() => new() { AllowedToolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) }; public static EditableUserModel FromSummary(UserSummaryDto user) => new() { Username = user.Username, DisplayName = user.DisplayName ?? string.Empty, Role = user.Role, Enabled = IsEnabled(user.Status), IsExistingUser = true, LastLoginAt = user.LastLoginAt, CreatedAt = user.CreatedAt, AllowedToolIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) }; }
    private static string? TrimToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
