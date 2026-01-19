using WebCodeCli.Domain.Repositories.Base.SessionShare;

namespace WebCodeCli.Domain.Domain.Service;

/// <summary>
/// 会话分享服务接口
/// </summary>
public interface ISessionShareService
{
    /// <summary>
    /// 创建分享
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <param name="password">分享密码</param>
    /// <param name="title">分享标题（可选）</param>
    /// <param name="expiresAt">过期时间（可选）</param>
    /// <param name="createdBy">创建者（可选）</param>
    /// <returns>分享信息</returns>
    Task<ShareInfoResponse> CreateShareAsync(
        string sessionId, 
        string password, 
        string? title = null,
        DateTime? expiresAt = null, 
        string? createdBy = null);
    
    /// <summary>
    /// 验证分享访问
    /// </summary>
    /// <param name="shareCode">分享码</param>
    /// <param name="password">密码</param>
    /// <returns>验证结果</returns>
    Task<ShareValidationResult> ValidateShareAsync(string shareCode, string password);
    
    /// <summary>
    /// 获取分享信息（不含敏感数据）
    /// </summary>
    /// <param name="shareCode">分享码</param>
    /// <returns>分享信息</returns>
    Task<ShareInfoResponse?> GetShareInfoAsync(string shareCode);
    
    /// <summary>
    /// 获取会话的所有分享
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <returns>分享列表</returns>
    Task<List<ShareInfoResponse>> GetSessionSharesAsync(string sessionId);
    
    /// <summary>
    /// 停用分享
    /// </summary>
    /// <param name="shareCode">分享码</param>
    /// <returns>是否成功</returns>
    Task<bool> DeactivateShareAsync(string shareCode);
    
    /// <summary>
    /// 删除分享
    /// </summary>
    /// <param name="shareCode">分享码</param>
    /// <returns>是否成功</returns>
    Task<bool> DeleteShareAsync(string shareCode);
    
    /// <summary>
    /// 删除会话的所有分享
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <returns>删除数量</returns>
    Task<int> DeleteSessionSharesAsync(string sessionId);
    
    /// <summary>
    /// 验证访问令牌
    /// </summary>
    /// <param name="shareCode">分享码</param>
    /// <param name="accessToken">访问令牌</param>
    /// <returns>是否有效</returns>
    Task<bool> ValidateAccessTokenAsync(string shareCode, string accessToken);
    
    /// <summary>
    /// 根据访问令牌获取会话ID
    /// </summary>
    /// <param name="shareCode">分享码</param>
    /// <param name="accessToken">访问令牌</param>
    /// <returns>会话ID</returns>
    Task<string?> GetSessionIdByTokenAsync(string shareCode, string accessToken);
    
    /// <summary>
    /// 生成分享链接
    /// </summary>
    /// <param name="shareCode">分享码</param>
    /// <param name="baseUrl">基础URL</param>
    /// <returns>完整分享链接</returns>
    string GenerateShareUrl(string shareCode, string baseUrl);
    
    /// <summary>
    /// 清理过期分享
    /// </summary>
    /// <returns>清理数量</returns>
    Task<int> CleanupExpiredSharesAsync();
}
