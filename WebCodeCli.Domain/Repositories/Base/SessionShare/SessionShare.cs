using SqlSugar;

namespace WebCodeCli.Domain.Repositories.Base.SessionShare;

/// <summary>
/// 会话分享记录
/// </summary>
[SugarTable("SessionShare")]
public class SessionShare
{
    /// <summary>
    /// 主键ID
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    
    /// <summary>
    /// 关联的会话ID
    /// </summary>
    [SugarColumn(Length = 64, IsNullable = false)]
    public string SessionId { get; set; } = string.Empty;
    
    /// <summary>
    /// 分享码 (短链接标识)
    /// </summary>
    [SugarColumn(Length = 32, IsNullable = false)]
    public string ShareCode { get; set; } = string.Empty;
    
    /// <summary>
    /// 密码哈希 (SHA256 + Salt)
    /// </summary>
    [SugarColumn(Length = 256, IsNullable = false)]
    public string PasswordHash { get; set; } = string.Empty;
    
    /// <summary>
    /// 密码盐值
    /// </summary>
    [SugarColumn(Length = 64, IsNullable = false)]
    public string PasswordSalt { get; set; } = string.Empty;
    
    /// <summary>
    /// 创建者用户名
    /// </summary>
    [SugarColumn(Length = 128, IsNullable = true)]
    public string? CreatedBy { get; set; }
    
    /// <summary>
    /// 分享标题/备注
    /// </summary>
    [SugarColumn(Length = 256, IsNullable = true)]
    public string? Title { get; set; }
    
    /// <summary>
    /// 创建时间
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    
    /// <summary>
    /// 过期时间 (null表示永不过期)
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? ExpiresAt { get; set; }
    
    /// <summary>
    /// 是否有效
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool IsActive { get; set; } = true;
    
    /// <summary>
    /// 访问次数
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public int ViewCount { get; set; } = 0;
    
    /// <summary>
    /// 最后访问时间
    /// </summary>
    [SugarColumn(IsNullable = true)]
    public DateTime? LastAccessedAt { get; set; }
    
    /// <summary>
    /// 是否允许编辑 (预留字段，当前版本只支持只读)
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public bool AllowEdit { get; set; } = false;
}

/// <summary>
/// 创建分享请求DTO
/// </summary>
public class CreateShareRequest
{
    /// <summary>
    /// 会话ID
    /// </summary>
    public string SessionId { get; set; } = string.Empty;
    
    /// <summary>
    /// 分享密码
    /// </summary>
    public string Password { get; set; } = string.Empty;
    
    /// <summary>
    /// 分享标题/备注
    /// </summary>
    public string? Title { get; set; }
    
    /// <summary>
    /// 过期时间 (可选)
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
}

/// <summary>
/// 验证分享请求DTO
/// </summary>
public class ValidateShareRequest
{
    /// <summary>
    /// 分享码
    /// </summary>
    public string ShareCode { get; set; } = string.Empty;
    
    /// <summary>
    /// 密码
    /// </summary>
    public string Password { get; set; } = string.Empty;
}

/// <summary>
/// 分享信息响应DTO
/// </summary>
public class ShareInfoResponse
{
    /// <summary>
    /// 分享码
    /// </summary>
    public string ShareCode { get; set; } = string.Empty;
    
    /// <summary>
    /// 会话ID
    /// </summary>
    public string SessionId { get; set; } = string.Empty;
    
    /// <summary>
    /// 分享标题
    /// </summary>
    public string? Title { get; set; }
    
    /// <summary>
    /// 创建者
    /// </summary>
    public string? CreatedBy { get; set; }
    
    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// 过期时间
    /// </summary>
    public DateTime? ExpiresAt { get; set; }
    
    /// <summary>
    /// 是否有效
    /// </summary>
    public bool IsActive { get; set; }
    
    /// <summary>
    /// 访问次数
    /// </summary>
    public int ViewCount { get; set; }
    
    /// <summary>
    /// 是否已过期
    /// </summary>
    public bool IsExpired => ExpiresAt.HasValue && ExpiresAt.Value < DateTime.Now;
    
    /// <summary>
    /// 分享链接
    /// </summary>
    public string ShareUrl { get; set; } = string.Empty;
}

/// <summary>
/// 验证结果DTO
/// </summary>
public class ShareValidationResult
{
    /// <summary>
    /// 是否验证成功
    /// </summary>
    public bool IsValid { get; set; }
    
    /// <summary>
    /// 会话ID (验证成功时返回)
    /// </summary>
    public string? SessionId { get; set; }
    
    /// <summary>
    /// 错误消息
    /// </summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>
    /// 访问令牌 (用于后续请求验证)
    /// </summary>
    public string? AccessToken { get; set; }
}
