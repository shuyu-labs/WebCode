using System.Text.Json;
using Microsoft.AspNetCore.Components;
using WebCodeCli.Domain.Domain.Service;

namespace WebCodeCli.Pages;

public partial class Setup : ComponentBase
{
    [Inject] private ISystemSettingsService SystemSettingsService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ILocalizationService L { get; set; } = default!;

    private int _currentStep = 1;
    private int _currentAssistantIndex = 0;
    private bool _isLoading = false;
    private bool _isCompleted = false;
    private string _errorMessage = string.Empty;
    private string _defaultWorkspaceRoot = string.Empty;

    // 助手选择状态
    private bool _enableClaudeCode = false;
    private bool _enableCodex = false;
    private bool _enableOpenCode = false;

    // 本地化相关
    private Dictionary<string, string> _translations = new();
    private string _currentLanguage = "zh-CN";

    private SystemInitConfig _config = new()
    {
        EnableAuth = true,
        AdminUsername = "admin",
        AdminPassword = string.Empty,
        EnabledAssistants = new List<string>()
    };

    // 环境变量列表
    private List<EnvVarItem> _claudeEnvVars = new();
    private List<EnvVarItem> _codexEnvVars = new();
    private List<EnvVarItem> _openCodeEnvVars = new();

    private class EnvVarItem
    {
        public string Key { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
    }

    protected override async Task OnInitializedAsync()
    {
        // 检查是否已初始化，如果已初始化则跳转到首页
        var isInitialized = await SystemSettingsService.IsSystemInitializedAsync();
        if (isInitialized)
        {
            NavigationManager.NavigateTo("/", forceLoad: true);
            return;
        }

        // 设置默认工作区路径
        _defaultWorkspaceRoot = await SystemSettingsService.GetWorkspaceRootAsync();

        // 初始化默认环境变量
        InitializeDefaultEnvVars();

        // 初始化本地化
        try
        {
            _currentLanguage = await L.GetCurrentLanguageAsync();
            await LoadTranslationsAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Setup] 初始化本地化失败: {ex.Message}");
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            return;
        }

        try
        {
            if (_translations.Count == 0)
            {
                _currentLanguage = await L.GetCurrentLanguageAsync();
                await LoadTranslationsAsync();
                StateHasChanged();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Setup] 首次渲染后刷新本地化失败: {ex.Message}");
        }
    }

    private void InitializeDefaultEnvVars()
    {
        // Claude Code 默认环境变量
        _claudeEnvVars = new List<EnvVarItem>
        {
            new() { Key = "ANTHROPIC_API_KEY", Value = "" },
            new() { Key = "ANTHROPIC_BASE_URL", Value = "" },
            new() { Key = "ANTHROPIC_MODEL", Value = "" },
            new() { Key = "ANTHROPIC_SMALL_FAST_MODEL", Value = "" }
        };

        // Codex 默认环境变量
        _codexEnvVars = new List<EnvVarItem>
        {
            new() { Key = "NEW_API_KEY", Value = "" },
            new() { Key = "CODEX_BASE_URL", Value = "" },
            new() { Key = "CODEX_MODEL", Value = "" },
            new() { Key = "CODEX_PROFILE", Value = "" },
            new() { Key = "CODEX_PROVIDER_NAME", Value = "" },
            new() { Key = "CODEX_WIRE_API", Value = "" },
            new() { Key = "CODEX_APPROVAL_POLICY", Value = "" },
            new() { Key = "CODEX_MODEL_REASONING_EFFORT", Value = "" },
            new() { Key = "CODEX_SANDBOX_MODE", Value = "" }
        };

        // OpenCode 默认环境变量
        _openCodeEnvVars = new List<EnvVarItem>
        {
            new() { Key = "OPENCODE_CONFIG_CONTENT", Value = "" },
            new() { Key = "OPENCODE_AUTO_SHARE", Value = "" },
            new() { Key = "OPENCODE_GIT_BASH_PATH", Value = "" },
            new() { Key = "OPENCODE_CONFIG", Value = "" },
            new() { Key = "OPENCODE_CONFIG_DIR", Value = "" },
            new() { Key = "OPENCODE_DISABLE_AUTOUPDATE", Value = "" },
            new() { Key = "OPENCODE_DISABLE_PRUNE", Value = "" },
            new() { Key = "OPENCODE_DISABLE_TERMINAL_TITLE", Value = "" },
            new() { Key = "OPENCODE_PERMISSION", Value = "" },
            new() { Key = "OPENCODE_DISABLE_DEFAULT_PLUGINS", Value = "" },
            new() { Key = "OPENCODE_DISABLE_LSP_DOWNLOAD", Value = "" },
            new() { Key = "OPENCODE_ENABLE_EXPERIMENTAL_MODELS", Value = "" },
            new() { Key = "OPENCODE_DISABLE_AUTOCOMPACT", Value = "" },
            new() { Key = "OPENCODE_DISABLE_CLAUDE_CODE", Value = "" },
            new() { Key = "OPENCODE_DISABLE_CLAUDE_CODE_PROMPT", Value = "" },
            new() { Key = "OPENCODE_DISABLE_CLAUDE_CODE_SKILLS", Value = "" },
            new() { Key = "OPENCODE_CLIENT", Value = "" },
            new() { Key = "OPENCODE_ENABLE_EXA", Value = "" },
            new() { Key = "OPENCODE_SERVER_PASSWORD", Value = "" },
            new() { Key = "OPENCODE_SERVER_USERNAME", Value = "" }
        };
    }

    private string GetStepClass(int step)
    {
        var displayStep = GetDisplayStep();
        if (step < displayStep)
            return "w-8 h-8 rounded-full bg-gray-800 text-white flex items-center justify-center text-sm font-bold";
        if (step == displayStep)
            return "w-8 h-8 rounded-full bg-gray-800 text-white flex items-center justify-center text-sm font-bold ring-4 ring-gray-300";
        return "w-8 h-8 rounded-full bg-gray-200 text-gray-500 flex items-center justify-center text-sm font-bold";
    }

    private string GetStepTitle(int step)
    {
        if (step == 1)
        {
            return T("setup.step1Title");
        }
        if (step == 2)
        {
            return T("setup.selectAssistants");
        }
        // 步骤3及以后，根据启用的助手动态生成标题
        var enabledAssistants = GetEnabledAssistants();
        var assistantIndex = step - 3;
        if (assistantIndex >= 0 && assistantIndex < enabledAssistants.Count)
        {
            var assistant = enabledAssistants[assistantIndex];
            return assistant switch
            {
                "claude-code" => T("setup.step2Title"),
                "codex" => T("setup.step3Title"),
                "opencode" => T("setup.step4Title"),
                _ => ""
            };
        }
        return "";
    }

    private int GetDisplayStep()
    {
        if (_currentStep <= 2) return _currentStep;
        // 步骤3及以后，根据当前配置的助手索引计算
        return 2 + _currentAssistantIndex + 1;
    }

    private int GetTotalSteps()
    {
        var enabledCount = GetEnabledAssistants().Count;
        // 步骤1：基础设置 + 步骤2：选择助手 + 每个助手配置一个步骤
        return 2 + enabledCount;
    }

    private void NextStep()
    {
        _errorMessage = string.Empty;

        // 步骤1验证
        if (_currentStep == 1)
        {
            if (_config.EnableAuth)
            {
                if (string.IsNullOrWhiteSpace(_config.AdminUsername))
                {
                    _errorMessage = T("setup.validation.adminUsernameRequired");
                    return;
                }
                if (string.IsNullOrWhiteSpace(_config.AdminPassword))
                {
                    _errorMessage = T("setup.validation.adminPasswordRequired");
                    return;
                }
                if (_config.AdminPassword.Length < 6)
                {
                    _errorMessage = T("setup.validation.passwordTooShort", ("min", "6"));
                    return;
                }
            }
        }

        if (_currentStep < 2)
        {
            _currentStep++;
            StateHasChanged();
        }
    }

    private void PrevStep()
    {
        if (_currentStep > 1)
        {
            _currentStep--;
            _currentAssistantIndex = 0;
            _errorMessage = string.Empty;
            StateHasChanged();
        }
    }

    private async Task CompleteSetup()
    {
        _isLoading = true;
        _errorMessage = string.Empty;
        StateHasChanged();

        try
        {
            // 设置启用的助手列表
            _config.EnabledAssistants = GetEnabledAssistants();

            // 构建配置 - 只保存启用的助手的环境变量
            if (_enableClaudeCode)
            {
                _config.ClaudeCodeEnvVars = _claudeEnvVars
                    .Where(e => !string.IsNullOrWhiteSpace(e.Key) && !string.IsNullOrWhiteSpace(e.Value))
                    .ToDictionary(e => e.Key, e => e.Value);
            }

            if (_enableCodex)
            {
                _config.CodexEnvVars = _codexEnvVars
                    .Where(e => !string.IsNullOrWhiteSpace(e.Key) && !string.IsNullOrWhiteSpace(e.Value))
                    .ToDictionary(e => e.Key, e => e.Value);
            }

            if (_enableOpenCode)
            {
                _config.OpenCodeEnvVars = _openCodeEnvVars
                    .Where(e => !string.IsNullOrWhiteSpace(e.Key) && !string.IsNullOrWhiteSpace(e.Value))
                    .ToDictionary(e => e.Key, e => e.Value);
            }

            // 保存配置
            var result = await SystemSettingsService.CompleteInitializationAsync(_config);

            if (result)
            {
                _isCompleted = true;
            }
            else
            {
                _errorMessage = T("setup.error.saveFailed");
            }
        }
        catch (Exception ex)
        {
            _errorMessage = T("setup.error.configFailed", ("message", ex.Message));
        }
        finally
        {
            _isLoading = false;
            StateHasChanged();
        }
    }

    private void GoToHome()
    {
        NavigationManager.NavigateTo("/", forceLoad: true);
    }

    private async Task HandleLanguageChanged(string languageCode)
    {
        _currentLanguage = languageCode;
        await LoadTranslationsAsync();
        StateHasChanged();
    }

    // Claude Code 环境变量操作
    private void AddClaudeEnvVar()
    {
        _claudeEnvVars.Add(new EnvVarItem());
        StateHasChanged();
    }

    private void RemoveClaudeEnvVar(EnvVarItem item)
    {
        _claudeEnvVars.Remove(item);
        StateHasChanged();
    }

    // Codex 环境变量操作
    private void AddCodexEnvVar()
    {
        _codexEnvVars.Add(new EnvVarItem());
        StateHasChanged();
    }

    private void RemoveCodexEnvVar(EnvVarItem item)
    {
        _codexEnvVars.Remove(item);
        StateHasChanged();
    }

    // OpenCode 环境变量操作
    private void AddOpenCodeEnvVar()
    {
        _openCodeEnvVars.Add(new EnvVarItem());
        StateHasChanged();
    }

    private void RemoveOpenCodeEnvVar(EnvVarItem item)
    {
        _openCodeEnvVars.Remove(item);
        StateHasChanged();
    }

    // 获取启用的助手列表
    private List<string> GetEnabledAssistants()
    {
        var list = new List<string>();
        if (_enableClaudeCode) list.Add("claude-code");
        if (_enableCodex) list.Add("codex");
        if (_enableOpenCode) list.Add("opencode");
        return list;
    }

    // 获取环境变量的提示文本
    private string GetEnvVarPlaceholder(string key)
    {
        return key switch
        {
            // Claude Code 相关
            "ANTHROPIC_API_KEY" => T("setup.placeholders.anthropicApiKey"),
            "ANTHROPIC_BASE_URL" => T("setup.placeholders.anthropicBaseUrl"),
            "ANTHROPIC_MODEL" => T("setup.placeholders.anthropicModel"),
            "ANTHROPIC_SMALL_FAST_MODEL" => T("setup.placeholders.anthropicSmallFastModel"),
            
            // Codex 相关
            "NEW_API_KEY" => T("setup.placeholders.newApiKey"),
            "CODEX_BASE_URL" => T("setup.placeholders.codexBaseUrl"),
            "CODEX_MODEL" => T("setup.placeholders.codexModel"),
            "CODEX_PROFILE" => T("setup.placeholders.codexProfile"),
            "CODEX_PROVIDER_NAME" => T("setup.placeholders.codexProviderName"),
            "CODEX_WIRE_API" => T("setup.placeholders.codexWireApi"),
            "CODEX_APPROVAL_POLICY" => T("setup.placeholders.codexApprovalPolicy"),
            "CODEX_MODEL_REASONING_EFFORT" => T("setup.placeholders.codexModelReasoningEffort"),
            "CODEX_SANDBOX_MODE" => T("setup.placeholders.codexSandboxMode"),

            // OpenCode 相关
            "OPENCODE_AUTO_SHARE" => T("setup.placeholders.opencodeAutoShare"),
            "OPENCODE_GIT_BASH_PATH" => T("setup.placeholders.opencodeGitBashPath"),
            "OPENCODE_CONFIG" => T("setup.placeholders.opencodeConfig"),
            "OPENCODE_CONFIG_DIR" => T("setup.placeholders.opencodeConfigDir"),
            "OPENCODE_CONFIG_CONTENT" => T("setup.placeholders.opencodeConfigContent"),
            "OPENCODE_DISABLE_AUTOUPDATE" => T("setup.placeholders.opencodeDisableAutoupdate"),
            "OPENCODE_DISABLE_PRUNE" => T("setup.placeholders.opencodeDisablePrune"),
            "OPENCODE_DISABLE_TERMINAL_TITLE" => T("setup.placeholders.opencodeDisableTerminalTitle"),
            "OPENCODE_PERMISSION" => T("setup.placeholders.opencodePermission"),
            "OPENCODE_DISABLE_DEFAULT_PLUGINS" => T("setup.placeholders.opencodeDisableDefaultPlugins"),
            "OPENCODE_DISABLE_LSP_DOWNLOAD" => T("setup.placeholders.opencodeDisableLspDownload"),
            "OPENCODE_ENABLE_EXPERIMENTAL_MODELS" => T("setup.placeholders.opencodeEnableExperimentalModels"),
            "OPENCODE_DISABLE_AUTOCOMPACT" => T("setup.placeholders.opencodeDisableAutocompact"),
            "OPENCODE_DISABLE_CLAUDE_CODE" => T("setup.placeholders.opencodeDisableClaudeCode"),
            "OPENCODE_DISABLE_CLAUDE_CODE_PROMPT" => T("setup.placeholders.opencodeDisableClaudeCodePrompt"),
            "OPENCODE_DISABLE_CLAUDE_CODE_SKILLS" => T("setup.placeholders.opencodeDisableClaudeCodeSkills"),
            "OPENCODE_CLIENT" => T("setup.placeholders.opencodeClient"),
            "OPENCODE_ENABLE_EXA" => T("setup.placeholders.opencodeEnableExa"),
            "OPENCODE_SERVER_PASSWORD" => T("setup.placeholders.opencodeServerPassword"),
            "OPENCODE_SERVER_USERNAME" => T("setup.placeholders.opencodeServerUsername"),
            
            _ => T("setup.placeholders.default")
        };
    }

    #region 本地化辅助方法

    private async Task LoadTranslationsAsync()
    {
        try
        {
            var allTranslations = await L.GetAllTranslationsAsync(_currentLanguage);
            _translations = FlattenTranslations(allTranslations);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Setup] 加载翻译资源失败: {ex.Message}");
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

    private string T(string key)
    {
        if (_translations.TryGetValue(key, out var translation))
        {
            return translation;
        }

        var parts = key.Split('.');
        return parts.Length > 0 ? parts[^1] : key;
    }

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
}
