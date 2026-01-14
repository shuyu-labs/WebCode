using Microsoft.AspNetCore.Components;
using WebCodeCli.Domain.Domain.Service;

namespace WebCodeCli.Pages;

public partial class Setup : ComponentBase
{
    [Inject] private ISystemSettingsService SystemSettingsService { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    private int _currentStep = 1;
    private bool _isLoading = false;
    private bool _isCompleted = false;
    private string _errorMessage = string.Empty;
    private string _defaultWorkspaceRoot = string.Empty;

    private SystemInitConfig _config = new()
    {
        EnableAuth = true,
        AdminUsername = "admin",
        AdminPassword = string.Empty
    };

    // 环境变量列表
    private List<EnvVarItem> _claudeEnvVars = new();
    private List<EnvVarItem> _codexEnvVars = new();

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
    }

    private void InitializeDefaultEnvVars()
    {
        // Claude Code 默认环境变量
        _claudeEnvVars = new List<EnvVarItem>
        {
            new() { Key = "ANTHROPIC_API_KEY", Value = "" },
            new() { Key = "ANTHROPIC_BASE_URL", Value = "" }
        };

        // Codex 默认环境变量
        _codexEnvVars = new List<EnvVarItem>
        {
            new() { Key = "NEW_API_KEY", Value = "" },
            new() { Key = "CODEX_BASE_URL", Value = "" }
        };
    }

    private string GetStepClass(int step)
    {
        if (step < _currentStep)
            return "w-8 h-8 rounded-full bg-gray-800 text-white flex items-center justify-center text-sm font-bold";
        if (step == _currentStep)
            return "w-8 h-8 rounded-full bg-gray-800 text-white flex items-center justify-center text-sm font-bold ring-4 ring-gray-300";
        return "w-8 h-8 rounded-full bg-gray-200 text-gray-500 flex items-center justify-center text-sm font-bold";
    }

    private string GetStepTitle(int step)
    {
        return step switch
        {
            1 => "账户设置",
            2 => "Claude Code",
            3 => "Codex",
            _ => ""
        };
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
                    _errorMessage = "请输入管理员用户名";
                    return;
                }
                if (string.IsNullOrWhiteSpace(_config.AdminPassword))
                {
                    _errorMessage = "请输入管理员密码";
                    return;
                }
                if (_config.AdminPassword.Length < 6)
                {
                    _errorMessage = "密码长度至少为6位";
                    return;
                }
            }
        }

        if (_currentStep < 3)
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
            _errorMessage = string.Empty;
            StateHasChanged();
        }
    }

    private void SkipStep()
    {
        if (_currentStep == 2)
        {
            _claudeEnvVars.Clear();
        }
        NextStep();
    }

    private async Task SkipAndComplete()
    {
        _codexEnvVars.Clear();
        await CompleteSetup();
    }

    private async Task CompleteSetup()
    {
        _isLoading = true;
        _errorMessage = string.Empty;
        StateHasChanged();

        try
        {
            // 构建配置
            _config.ClaudeCodeEnvVars = _claudeEnvVars
                .Where(e => !string.IsNullOrWhiteSpace(e.Key) && !string.IsNullOrWhiteSpace(e.Value))
                .ToDictionary(e => e.Key, e => e.Value);

            _config.CodexEnvVars = _codexEnvVars
                .Where(e => !string.IsNullOrWhiteSpace(e.Key) && !string.IsNullOrWhiteSpace(e.Value))
                .ToDictionary(e => e.Key, e => e.Value);

            // 保存配置
            var result = await SystemSettingsService.CompleteInitializationAsync(_config);

            if (result)
            {
                _isCompleted = true;
            }
            else
            {
                _errorMessage = "保存配置失败，请重试";
            }
        }
        catch (Exception ex)
        {
            _errorMessage = $"配置失败: {ex.Message}";
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
}
