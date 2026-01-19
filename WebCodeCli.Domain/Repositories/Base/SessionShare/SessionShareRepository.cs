using AntSK.Domain.Repositories.Base;
using Microsoft.Extensions.DependencyInjection;
using WebCodeCli.Domain.Common.Extensions;

namespace WebCodeCli.Domain.Repositories.Base.SessionShare;

/// <summary>
/// 会话分享仓储实现
/// </summary>
[ServiceDescription(typeof(ISessionShareRepository), ServiceLifetime.Scoped)]
public class SessionShareRepository : Repository<SessionShare>, ISessionShareRepository
{
    /// <summary>
    /// 根据分享码获取分享记录
    /// </summary>
    public async Task<SessionShare?> GetByShareCodeAsync(string shareCode)
    {
        if (string.IsNullOrWhiteSpace(shareCode))
        {
            return null;
        }
        
        return await GetFirstAsync(x => x.ShareCode == shareCode);
    }
    
    /// <summary>
    /// 获取会话的所有分享记录
    /// </summary>
    public async Task<List<SessionShare>> GetBySessionIdAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new List<SessionShare>();
        }
        
        return await GetListAsync(x => x.SessionId == sessionId);
    }
    
    /// <summary>
    /// 获取会话的有效分享记录
    /// </summary>
    public async Task<List<SessionShare>> GetActiveBySessionIdAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return new List<SessionShare>();
        }
        
        var now = DateTime.Now;
        return await GetListAsync(x => 
            x.SessionId == sessionId && 
            x.IsActive && 
            (x.ExpiresAt == null || x.ExpiresAt > now));
    }
    
    /// <summary>
    /// 检查分享码是否存在
    /// </summary>
    public async Task<bool> ShareCodeExistsAsync(string shareCode)
    {
        if (string.IsNullOrWhiteSpace(shareCode))
        {
            return false;
        }
        
        return await IsAnyAsync(x => x.ShareCode == shareCode);
    }
    
    /// <summary>
    /// 增加访问次数
    /// </summary>
    public async Task IncrementViewCountAsync(string shareCode)
    {
        var share = await GetByShareCodeAsync(shareCode);
        if (share != null)
        {
            share.ViewCount++;
            share.LastAccessedAt = DateTime.Now;
            await UpdateAsync(share);
        }
    }
    
    /// <summary>
    /// 停用分享
    /// </summary>
    public async Task<bool> DeactivateAsync(string shareCode)
    {
        var share = await GetByShareCodeAsync(shareCode);
        if (share != null)
        {
            share.IsActive = false;
            return await UpdateAsync(share);
        }
        return false;
    }
    
    /// <summary>
    /// 删除会话的所有分享
    /// </summary>
    public async Task<int> DeleteBySessionIdAsync(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return 0;
        }
        
        var shares = await GetBySessionIdAsync(sessionId);
        if (shares.Count > 0)
        {
            var ids = shares.Select(x => x.Id).Cast<dynamic>().ToArray();
            await DeleteByIdsAsync(ids);
            return shares.Count;
        }
        return 0;
    }
    
    /// <summary>
    /// 清理过期的分享记录
    /// </summary>
    public async Task<int> CleanupExpiredAsync()
    {
        var now = DateTime.Now;
        var expiredShares = await GetListAsync(x => 
            x.ExpiresAt != null && 
            x.ExpiresAt < now && 
            x.IsActive);
        
        foreach (var share in expiredShares)
        {
            share.IsActive = false;
        }
        
        if (expiredShares.Count > 0)
        {
            await UpdateRangeAsync(expiredShares);
        }
        
        return expiredShares.Count;
    }
}
