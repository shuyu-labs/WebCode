namespace WebCodeCli.Domain.Domain.Model;

/// <summary>
/// 项目信息（业务模型）
/// </summary>
public class ProjectInfo
{
    /// <summary>
    /// 项目ID
    /// </summary>
    public string ProjectId { get; set; } = string.Empty;
    
    /// <summary>
    /// 项目名称
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Git 仓库地址
    /// </summary>
    public string GitUrl { get; set; } = string.Empty;
    
    /// <summary>
    /// 认证方式：none、https、ssh
    /// </summary>
    public string AuthType { get; set; } = "none";
    
    /// <summary>
    /// 分支名称
    /// </summary>
    public string Branch { get; set; } = "main";
    
    /// <summary>
    /// 本地路径
    /// </summary>
    public string? LocalPath { get; set; }
    
    /// <summary>
    /// 最后同步时间
    /// </summary>
    public DateTime? LastSyncAt { get; set; }
    
    /// <summary>
    /// 项目状态
    /// </summary>
    public string Status { get; set; } = "pending";
    
    /// <summary>
    /// 错误信息
    /// </summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// 更新时间
    /// </summary>
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// 项目选择结果（用于创建会话时的参数）
/// </summary>
public record ProjectSelectionResult(string? ProjectId, bool IncludeGit);

/// <summary>
/// 创建项目请求
/// </summary>
public class CreateProjectRequest
{
    /// <summary>
    /// 项目名称
    /// </summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>
    /// Git 仓库地址
    /// </summary>
    public string GitUrl { get; set; } = string.Empty;
    
    /// <summary>
    /// 认证方式：none、https、ssh
    /// </summary>
    public string AuthType { get; set; } = "none";
    
    /// <summary>
    /// HTTPS 用户名
    /// </summary>
    public string? HttpsUsername { get; set; }
    
    /// <summary>
    /// HTTPS Token
    /// </summary>
    public string? HttpsToken { get; set; }
    
    /// <summary>
    /// SSH 私钥
    /// </summary>
    public string? SshPrivateKey { get; set; }
    
    /// <summary>
    /// SSH 私钥密码
    /// </summary>
    public string? SshPassphrase { get; set; }
    
    /// <summary>
    /// 分支名称
    /// </summary>
    public string Branch { get; set; } = "main";
}

/// <summary>
/// 从 ZIP 压缩包创建项目请求
/// </summary>
public class CreateProjectFromZipRequest
{
    /// <summary>
    /// 项目名称
    /// </summary>
    public string Name { get; set; } = string.Empty;
}

/// <summary>
/// 更新项目请求
/// </summary>
public class UpdateProjectRequest
{
    /// <summary>
    /// 项目名称
    /// </summary>
    public string? Name { get; set; }
    
    /// <summary>
    /// Git 仓库地址
    /// </summary>
    public string? GitUrl { get; set; }
    
    /// <summary>
    /// 认证方式：none、https、ssh
    /// </summary>
    public string? AuthType { get; set; }
    
    /// <summary>
    /// HTTPS 用户名
    /// </summary>
    public string? HttpsUsername { get; set; }
    
    /// <summary>
    /// HTTPS Token
    /// </summary>
    public string? HttpsToken { get; set; }
    
    /// <summary>
    /// SSH 私钥
    /// </summary>
    public string? SshPrivateKey { get; set; }
    
    /// <summary>
    /// SSH 私钥密码
    /// </summary>
    public string? SshPassphrase { get; set; }
    
    /// <summary>
    /// 分支名称
    /// </summary>
    public string? Branch { get; set; }
}

/// <summary>
/// 获取远程分支请求
/// </summary>
public class GetBranchesRequest
{
    /// <summary>
    /// Git 仓库地址
    /// </summary>
    public string GitUrl { get; set; } = string.Empty;
    
    /// <summary>
    /// 认证方式：none、https、ssh
    /// </summary>
    public string AuthType { get; set; } = "none";
    
    /// <summary>
    /// HTTPS 用户名
    /// </summary>
    public string? HttpsUsername { get; set; }
    
    /// <summary>
    /// HTTPS Token
    /// </summary>
    public string? HttpsToken { get; set; }
    
    /// <summary>
    /// SSH 私钥
    /// </summary>
    public string? SshPrivateKey { get; set; }
    
    /// <summary>
    /// SSH 私钥密码
    /// </summary>
    public string? SshPassphrase { get; set; }
}

/// <summary>
/// Git 凭据信息
/// </summary>
public class GitCredentials
{
    /// <summary>
    /// 认证方式
    /// </summary>
    public string AuthType { get; set; } = "none";
    
    /// <summary>
    /// HTTPS 用户名
    /// </summary>
    public string? HttpsUsername { get; set; }
    
    /// <summary>
    /// HTTPS Token
    /// </summary>
    public string? HttpsToken { get; set; }
    
    /// <summary>
    /// SSH 私钥
    /// </summary>
    public string? SshPrivateKey { get; set; }
    
    /// <summary>
    /// SSH 私钥密码
    /// </summary>
    public string? SshPassphrase { get; set; }
}

/// <summary>
/// 克隆进度信息
/// </summary>
public class CloneProgress
{
    /// <summary>
    /// 进度百分比 (0-100)
    /// </summary>
    public int Percentage { get; set; }
    
    /// <summary>
    /// 当前阶段描述
    /// </summary>
    public string Stage { get; set; } = string.Empty;
    
    /// <summary>
    /// 详细信息
    /// </summary>
    public string? Details { get; set; }
}
