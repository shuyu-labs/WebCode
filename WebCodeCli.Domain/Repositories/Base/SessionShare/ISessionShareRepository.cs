namespace WebCodeCli.Domain.Repositories.Base.SessionShare;

/// <summary>
/// 会话分享仓储接口
/// </summary>
public interface ISessionShareRepository : IRepository<SessionShare>
{
    /// <summary>
    /// 根据分享码获取分享记录
    /// </summary>
    Task<SessionShare?> GetByShareCodeAsync(string shareCode);
    
    /// <summary>
    /// 获取会话的所有分享记录
    /// </summary>
    Task<List<SessionShare>> GetBySessionIdAsync(string sessionId);
    
    /// <summary>
    /// 获取会话的有效分享记录
    /// </summary>
    Task<List<SessionShare>> GetActiveBySessionIdAsync(string sessionId);
    
    /// <summary>
    /// 检查分享码是否存在
    /// </summary>
    Task<bool> ShareCodeExistsAsync(string shareCode);
    
    /// <summary>
    /// 增加访问次数
    /// </summary>
    Task IncrementViewCountAsync(string shareCode);
    
    /// <summary>
    /// 停用分享
    /// </summary>
    Task<bool> DeactivateAsync(string shareCode);
    
    /// <summary>
    /// 删除会话的所有分享
    /// </summary>
    Task<int> DeleteBySessionIdAsync(string sessionId);
    
    /// <summary>
    /// 清理过期的分享记录
    /// </summary>
    Task<int> CleanupExpiredAsync();
}
