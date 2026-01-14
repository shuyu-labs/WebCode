using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Repositories.Base.CliToolEnv;
using WebCodeCli.Domain.Repositories.Base.SystemSettings;

namespace WebCodeCli.Domain.Domain.Service;

/// <summary>
/// 系统设置服务接口
/// </summary>
public interface ISystemSettingsService
{
    /// <summary>
    /// 检查系统是否已完成初始化配置
    /// </summary>
    Task<bool> IsSystemInitializedAsync();

    /// <summary>
    /// 完成系统初始化
    /// </summary>
    Task<bool> CompleteInitializationAsync(SystemInitConfig config);

    /// <summary>
    /// 获取工作区根目录（优先数据库配置，否则使用默认值）
    /// </summary>
    Task<string> GetWorkspaceRootAsync();

    /// <summary>
    /// 设置工作区根目录
    /// </summary>
    Task<bool> SetWorkspaceRootAsync(string path);

    /// <summary>
    /// 获取系统配置摘要
    /// </summary>
    Task<SystemConfigSummary> GetConfigSummaryAsync();

    /// <summary>
    /// 验证初始化密码
    /// </summary>
    Task<bool> ValidateInitPasswordAsync(string username, string password);

    /// <summary>
    /// 更新管理员凭据
    /// </summary>
    Task<bool> UpdateAdminCredentialsAsync(string username, string password);
}

/// <summary>
/// 系统初始化配置
/// </summary>
public class SystemInitConfig
{
    /// <summary>
    /// 管理员用户名
    /// </summary>
    public string AdminUsername { get; set; } = "admin";

    /// <summary>
    /// 管理员密码
    /// </summary>
    public string AdminPassword { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用认证
    /// </summary>
    public bool EnableAuth { get; set; } = true;

    /// <summary>
    /// 工作区根目录（留空则使用默认值）
    /// </summary>
    public string? WorkspaceRoot { get; set; }

    /// <summary>
    /// Claude Code 环境变量
    /// </summary>
    public Dictionary<string, string> ClaudeCodeEnvVars { get; set; } = new();

    /// <summary>
    /// Codex 环境变量
    /// </summary>
    public Dictionary<string, string> CodexEnvVars { get; set; } = new();
}

/// <summary>
/// 系统配置摘要
/// </summary>
public class SystemConfigSummary
{
    /// <summary>
    /// 是否已初始化
    /// </summary>
    public bool IsInitialized { get; set; }

    /// <summary>
    /// 工作区根目录
    /// </summary>
    public string WorkspaceRoot { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用认证
    /// </summary>
    public bool AuthEnabled { get; set; }

    /// <summary>
    /// 管理员用户名
    /// </summary>
    public string AdminUsername { get; set; } = string.Empty;

    /// <summary>
    /// Claude Code 是否已配置
    /// </summary>
    public bool ClaudeCodeConfigured { get; set; }

    /// <summary>
    /// Codex 是否已配置
    /// </summary>
    public bool CodexConfigured { get; set; }
}

/// <summary>
/// 系统设置服务实现
/// </summary>
[ServiceDescription(typeof(ISystemSettingsService), ServiceLifetime.Scoped)]
public class SystemSettingsService : ISystemSettingsService
{
    private readonly ILogger<SystemSettingsService> _logger;
    private readonly ISystemSettingsRepository _repository;
    private readonly ICliToolEnvironmentVariableRepository _envRepository;
    private readonly CliToolsOption _cliOptions;
    private readonly AuthenticationOption _authOptions;

    public SystemSettingsService(
        ILogger<SystemSettingsService> logger,
        ISystemSettingsRepository repository,
        ICliToolEnvironmentVariableRepository envRepository,
        IOptions<CliToolsOption> cliOptions,
        IOptions<AuthenticationOption> authOptions)
    {
        _logger = logger;
        _repository = repository;
        _envRepository = envRepository;
        _cliOptions = cliOptions.Value;
        _authOptions = authOptions.Value;
    }

    /// <summary>
    /// 检查系统是否已完成初始化配置
    /// </summary>
    public async Task<bool> IsSystemInitializedAsync()
    {
        try
        {
            return await _repository.IsSystemInitializedAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检查系统初始化状态失败");
            // 如果数据库查询失败，假设未初始化
            return false;
        }
    }

    /// <summary>
    /// 完成系统初始化
    /// </summary>
    public async Task<bool> CompleteInitializationAsync(SystemInitConfig config)
    {
        try
        {
            _logger.LogInformation("开始系统初始化配置...");

            // 1. 保存管理员凭据
            if (!string.IsNullOrWhiteSpace(config.AdminUsername) && !string.IsNullOrWhiteSpace(config.AdminPassword))
            {
                await _repository.SetAsync(SystemSettingsKeys.AdminUsername, config.AdminUsername, "管理员用户名");
                // 简单加密存储（生产环境建议使用更强的加密）
                var encryptedPassword = Convert.ToBase64String(
                    System.Text.Encoding.UTF8.GetBytes(config.AdminPassword));
                await _repository.SetAsync(SystemSettingsKeys.AdminPassword, encryptedPassword, "管理员密码（加密）");
            }

            // 2. 保存认证设置
            await _repository.SetBoolAsync(SystemSettingsKeys.AuthEnabled, config.EnableAuth, "是否启用认证");

            // 3. 保存工作区根目录
            var workspaceRoot = config.WorkspaceRoot;
            if (string.IsNullOrWhiteSpace(workspaceRoot))
            {
                workspaceRoot = GetDefaultWorkspaceRoot();
            }
            await _repository.SetAsync(SystemSettingsKeys.WorkspaceRoot, workspaceRoot, "工作区根目录");

            // 确保工作区目录存在
            if (!Directory.Exists(workspaceRoot))
            {
                Directory.CreateDirectory(workspaceRoot);
                _logger.LogInformation("创建工作区目录: {Path}", workspaceRoot);
            }

            // 4. 保存 Claude Code 环境变量
            if (config.ClaudeCodeEnvVars.Any())
            {
                await _envRepository.SaveEnvironmentVariablesAsync("claude-code", config.ClaudeCodeEnvVars);
                _logger.LogInformation("已保存 Claude Code 环境变量配置");
            }

            // 5. 保存 Codex 环境变量
            if (config.CodexEnvVars.Any())
            {
                await _envRepository.SaveEnvironmentVariablesAsync("codex", config.CodexEnvVars);
                _logger.LogInformation("已保存 Codex 环境变量配置");
            }

            // 6. 标记系统已初始化
            await _repository.SetBoolAsync(SystemSettingsKeys.SystemInitialized, true, "系统已完成初始化");

            _logger.LogInformation("系统初始化配置完成");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "系统初始化配置失败");
            return false;
        }
    }

    /// <summary>
    /// 获取工作区根目录（优先数据库配置，否则使用默认值）
    /// </summary>
    public async Task<string> GetWorkspaceRootAsync()
    {
        try
        {
            var dbValue = await _repository.GetAsync(SystemSettingsKeys.WorkspaceRoot);
            if (!string.IsNullOrWhiteSpace(dbValue))
            {
                return dbValue;
            }

            // 使用配置文件中的值
            if (!string.IsNullOrWhiteSpace(_cliOptions.TempWorkspaceRoot))
            {
                return _cliOptions.TempWorkspaceRoot;
            }

            // 使用默认值
            return GetDefaultWorkspaceRoot();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取工作区根目录失败");
            return GetDefaultWorkspaceRoot();
        }
    }

    /// <summary>
    /// 设置工作区根目录
    /// </summary>
    public async Task<bool> SetWorkspaceRootAsync(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                Directory.CreateDirectory(path);
            }
            return await _repository.SetAsync(SystemSettingsKeys.WorkspaceRoot, path, "工作区根目录");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设置工作区根目录失败");
            return false;
        }
    }

    /// <summary>
    /// 获取系统配置摘要
    /// </summary>
    public async Task<SystemConfigSummary> GetConfigSummaryAsync()
    {
        var summary = new SystemConfigSummary
        {
            IsInitialized = await IsSystemInitializedAsync(),
            WorkspaceRoot = await GetWorkspaceRootAsync(),
            AuthEnabled = await _repository.GetBoolAsync(SystemSettingsKeys.AuthEnabled, _authOptions.Enabled),
            AdminUsername = await _repository.GetAsync(SystemSettingsKeys.AdminUsername, "admin")
        };

        // 检查 Claude Code 配置
        var claudeEnvVars = await _envRepository.GetEnvironmentVariablesByToolIdAsync("claude-code");
        summary.ClaudeCodeConfigured = claudeEnvVars.Any();

        // 检查 Codex 配置
        var codexEnvVars = await _envRepository.GetEnvironmentVariablesByToolIdAsync("codex");
        summary.CodexConfigured = codexEnvVars.Any();

        return summary;
    }

    /// <summary>
    /// 验证初始化密码
    /// </summary>
    public async Task<bool> ValidateInitPasswordAsync(string username, string password)
    {
        try
        {
            var storedUsername = await _repository.GetAsync(SystemSettingsKeys.AdminUsername);
            var storedPassword = await _repository.GetAsync(SystemSettingsKeys.AdminPassword);

            if (string.IsNullOrWhiteSpace(storedUsername) || string.IsNullOrWhiteSpace(storedPassword))
            {
                // 未配置时，验证配置文件中的用户
                var user = _authOptions.Users?.FirstOrDefault(u => 
                    u.Username == username && u.Password == password);
                return user != null;
            }

            // 验证数据库中存储的凭据
            var decryptedPassword = System.Text.Encoding.UTF8.GetString(
                Convert.FromBase64String(storedPassword));
            return storedUsername == username && decryptedPassword == password;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "验证管理员凭据失败");
            return false;
        }
    }

    /// <summary>
    /// 更新管理员凭据
    /// </summary>
    public async Task<bool> UpdateAdminCredentialsAsync(string username, string password)
    {
        try
        {
            await _repository.SetAsync(SystemSettingsKeys.AdminUsername, username, "管理员用户名");
            var encryptedPassword = Convert.ToBase64String(
                System.Text.Encoding.UTF8.GetBytes(password));
            await _repository.SetAsync(SystemSettingsKeys.AdminPassword, encryptedPassword, "管理员密码（加密）");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新管理员凭据失败");
            return false;
        }
    }

    /// <summary>
    /// 获取默认工作区根目录
    /// </summary>
    private string GetDefaultWorkspaceRoot()
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
}
