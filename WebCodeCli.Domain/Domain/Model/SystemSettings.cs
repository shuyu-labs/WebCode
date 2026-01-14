using SqlSugar;

namespace WebCodeCli.Domain.Domain.Model;

/// <summary>
/// 系统设置实体（数据库存储）
/// </summary>
[SugarTable("system_settings")]
public class SystemSettings
{
    /// <summary>
    /// 设置键名（主键）
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, Length = 100)]
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// 设置值（JSON格式存储复杂对象）
    /// </summary>
    [SugarColumn(Length = 4000, IsNullable = true, ColumnDescription = "设置值")]
    public string? Value { get; set; }

    /// <summary>
    /// 设置描述
    /// </summary>
    [SugarColumn(Length = 500, IsNullable = true, ColumnDescription = "设置描述")]
    public string? Description { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    [SugarColumn(IsNullable = false, ColumnDescription = "创建时间")]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 更新时间
    /// </summary>
    [SugarColumn(IsNullable = false, ColumnDescription = "更新时间")]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>
/// 系统设置键名常量
/// </summary>
public static class SystemSettingsKeys
{
    /// <summary>
    /// 系统是否已完成初始化
    /// </summary>
    public const string SystemInitialized = "system_initialized";
    
    /// <summary>
    /// 工作区根目录
    /// </summary>
    public const string WorkspaceRoot = "workspace_root";
    
    /// <summary>
    /// 管理员用户名
    /// </summary>
    public const string AdminUsername = "admin_username";
    
    /// <summary>
    /// 管理员密码（加密存储）
    /// </summary>
    public const string AdminPassword = "admin_password";
    
    /// <summary>
    /// 是否启用认证
    /// </summary>
    public const string AuthEnabled = "auth_enabled";
    
    /// <summary>
    /// Claude Code 环境变量
    /// </summary>
    public const string ClaudeCodeEnvVars = "claude_code_env_vars";
    
    /// <summary>
    /// Codex 环境变量
    /// </summary>
    public const string CodexEnvVars = "codex_env_vars";
}
