using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Repositories.Base.SessionShare;

namespace WebCodeCli.Domain.Domain.Service;

/// <summary>
/// 会话分享服务实现
/// </summary>
[ServiceDescription(typeof(ISessionShareService), ServiceLifetime.Scoped)]
public class SessionShareService : ISessionShareService
{
    private readonly ISessionShareRepository _repository;
    private readonly ILogger<SessionShareService> _logger;
    
    // 访问令牌缓存 (shareCode -> (token, sessionId, expireTime))
    private static readonly ConcurrentDictionary<string, (string Token, string SessionId, DateTime ExpireTime)> _accessTokenCache = new();
    
    // 访问令牌有效期（小时）
    private const int AccessTokenExpirationHours = 24;
    
    // 分享码长度
    private const int ShareCodeLength = 8;
    
    public SessionShareService(
        ISessionShareRepository repository,
        ILogger<SessionShareService> logger)
    {
        _repository = repository;
        _logger = logger;
    }
    
    /// <summary>
    /// 创建分享
    /// </summary>
    public async Task<ShareInfoResponse> CreateShareAsync(
        string sessionId, 
        string password, 
        string? title = null,
        DateTime? expiresAt = null, 
        string? createdBy = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new ArgumentException("会话ID不能为空", nameof(sessionId));
        }
        
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("密码不能为空", nameof(password));
        }
        
        // 生成唯一分享码
        var shareCode = await GenerateUniqueShareCodeAsync();
        
        // 生成密码哈希
        var salt = GenerateSalt();
        var passwordHash = HashPassword(password, salt);
        
        var share = new SessionShare
        {
            SessionId = sessionId,
            ShareCode = shareCode,
            PasswordHash = passwordHash,
            PasswordSalt = salt,
            Title = title,
            CreatedBy = createdBy,
            CreatedAt = DateTime.Now,
            ExpiresAt = expiresAt,
            IsActive = true,
            ViewCount = 0
        };
        
        var success = await _repository.InsertAsync(share);
        
        if (!success)
        {
            _logger.LogError("创建分享失败: SessionId={SessionId}", sessionId);
            throw new InvalidOperationException("创建分享失败");
        }
        
        _logger.LogInformation("分享创建成功: ShareCode={ShareCode}, SessionId={SessionId}", shareCode, sessionId);
        
        return new ShareInfoResponse
        {
            ShareCode = shareCode,
            SessionId = sessionId,
            Title = title,
            CreatedBy = createdBy,
            CreatedAt = share.CreatedAt,
            ExpiresAt = expiresAt,
            IsActive = true,
            ViewCount = 0
        };
    }
    
    /// <summary>
    /// 验证分享访问
    /// </summary>
    public async Task<ShareValidationResult> ValidateShareAsync(string shareCode, string password)
    {
        if (string.IsNullOrWhiteSpace(shareCode))
        {
            return new ShareValidationResult
            {
                IsValid = false,
                ErrorMessage = "分享码不能为空"
            };
        }
        
        if (string.IsNullOrWhiteSpace(password))
        {
            return new ShareValidationResult
            {
                IsValid = false,
                ErrorMessage = "密码不能为空"
            };
        }
        
        var share = await _repository.GetByShareCodeAsync(shareCode);
        
        if (share == null)
        {
            _logger.LogWarning("分享不存在: ShareCode={ShareCode}", shareCode);
            return new ShareValidationResult
            {
                IsValid = false,
                ErrorMessage = "分享不存在或已失效"
            };
        }
        
        if (!share.IsActive)
        {
            return new ShareValidationResult
            {
                IsValid = false,
                ErrorMessage = "分享已被停用"
            };
        }
        
        if (share.ExpiresAt.HasValue && share.ExpiresAt.Value < DateTime.Now)
        {
            return new ShareValidationResult
            {
                IsValid = false,
                ErrorMessage = "分享已过期"
            };
        }
        
        // 验证密码
        var inputHash = HashPassword(password, share.PasswordSalt);
        if (inputHash != share.PasswordHash)
        {
            _logger.LogWarning("密码验证失败: ShareCode={ShareCode}", shareCode);
            return new ShareValidationResult
            {
                IsValid = false,
                ErrorMessage = "密码错误"
            };
        }
        
        // 增加访问次数
        await _repository.IncrementViewCountAsync(shareCode);
        
        // 生成访问令牌
        var accessToken = GenerateAccessToken();
        var expireTime = DateTime.Now.AddHours(AccessTokenExpirationHours);
        
        // 缓存令牌
        _accessTokenCache[GetTokenCacheKey(shareCode, accessToken)] = (accessToken, share.SessionId, expireTime);
        
        _logger.LogInformation("分享验证成功: ShareCode={ShareCode}, SessionId={SessionId}", shareCode, share.SessionId);
        
        return new ShareValidationResult
        {
            IsValid = true,
            SessionId = share.SessionId,
            AccessToken = accessToken
        };
    }
    
    /// <summary>
    /// 获取分享信息
    /// </summary>
    public async Task<ShareInfoResponse?> GetShareInfoAsync(string shareCode)
    {
        var share = await _repository.GetByShareCodeAsync(shareCode);
        
        if (share == null)
        {
            return null;
        }
        
        return new ShareInfoResponse
        {
            ShareCode = share.ShareCode,
            SessionId = share.SessionId,
            Title = share.Title,
            CreatedBy = share.CreatedBy,
            CreatedAt = share.CreatedAt,
            ExpiresAt = share.ExpiresAt,
            IsActive = share.IsActive,
            ViewCount = share.ViewCount
        };
    }
    
    /// <summary>
    /// 获取会话的所有分享
    /// </summary>
    public async Task<List<ShareInfoResponse>> GetSessionSharesAsync(string sessionId)
    {
        var shares = await _repository.GetBySessionIdAsync(sessionId);
        
        return shares.Select(s => new ShareInfoResponse
        {
            ShareCode = s.ShareCode,
            SessionId = s.SessionId,
            Title = s.Title,
            CreatedBy = s.CreatedBy,
            CreatedAt = s.CreatedAt,
            ExpiresAt = s.ExpiresAt,
            IsActive = s.IsActive,
            ViewCount = s.ViewCount
        }).ToList();
    }
    
    /// <summary>
    /// 停用分享
    /// </summary>
    public async Task<bool> DeactivateShareAsync(string shareCode)
    {
        var result = await _repository.DeactivateAsync(shareCode);
        
        if (result)
        {
            _logger.LogInformation("分享已停用: ShareCode={ShareCode}", shareCode);
            
            // 清理相关的访问令牌
            CleanupTokensForShare(shareCode);
        }
        
        return result;
    }
    
    /// <summary>
    /// 删除分享
    /// </summary>
    public async Task<bool> DeleteShareAsync(string shareCode)
    {
        var share = await _repository.GetByShareCodeAsync(shareCode);
        
        if (share == null)
        {
            return false;
        }
        
        var result = await _repository.DeleteAsync(share);
        
        if (result)
        {
            _logger.LogInformation("分享已删除: ShareCode={ShareCode}", shareCode);
            
            // 清理相关的访问令牌
            CleanupTokensForShare(shareCode);
        }
        
        return result;
    }
    
    /// <summary>
    /// 删除会话的所有分享
    /// </summary>
    public async Task<int> DeleteSessionSharesAsync(string sessionId)
    {
        var count = await _repository.DeleteBySessionIdAsync(sessionId);
        
        if (count > 0)
        {
            _logger.LogInformation("会话分享已全部删除: SessionId={SessionId}, Count={Count}", sessionId, count);
        }
        
        return count;
    }
    
    /// <summary>
    /// 验证访问令牌
    /// </summary>
    public Task<bool> ValidateAccessTokenAsync(string shareCode, string accessToken)
    {
        var cacheKey = GetTokenCacheKey(shareCode, accessToken);
        
        if (_accessTokenCache.TryGetValue(cacheKey, out var cached))
        {
            if (cached.ExpireTime > DateTime.Now)
            {
                return Task.FromResult(true);
            }
            
            // 令牌已过期，移除
            _accessTokenCache.TryRemove(cacheKey, out _);
        }
        
        return Task.FromResult(false);
    }
    
    /// <summary>
    /// 根据访问令牌获取会话ID
    /// </summary>
    public Task<string?> GetSessionIdByTokenAsync(string shareCode, string accessToken)
    {
        var cacheKey = GetTokenCacheKey(shareCode, accessToken);
        
        if (_accessTokenCache.TryGetValue(cacheKey, out var cached))
        {
            if (cached.ExpireTime > DateTime.Now)
            {
                return Task.FromResult<string?>(cached.SessionId);
            }
            
            // 令牌已过期，移除
            _accessTokenCache.TryRemove(cacheKey, out _);
        }
        
        return Task.FromResult<string?>(null);
    }
    
    /// <summary>
    /// 生成分享链接
    /// </summary>
    public string GenerateShareUrl(string shareCode, string baseUrl)
    {
        baseUrl = baseUrl.TrimEnd('/');
        return $"{baseUrl}/share/{shareCode}";
    }
    
    /// <summary>
    /// 清理过期分享
    /// </summary>
    public async Task<int> CleanupExpiredSharesAsync()
    {
        var count = await _repository.CleanupExpiredAsync();
        
        if (count > 0)
        {
            _logger.LogInformation("已清理 {Count} 个过期分享", count);
        }
        
        // 同时清理过期的访问令牌
        CleanupExpiredTokens();
        
        return count;
    }
    
    #region 私有方法
    
    /// <summary>
    /// 生成唯一分享码
    /// </summary>
    private async Task<string> GenerateUniqueShareCodeAsync()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // 排除容易混淆的字符
        var random = new Random();
        
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var code = new string(Enumerable.Range(0, ShareCodeLength)
                .Select(_ => chars[random.Next(chars.Length)])
                .ToArray());
            
            if (!await _repository.ShareCodeExistsAsync(code))
            {
                return code;
            }
        }
        
        // 如果多次尝试都冲突，使用更长的随机码
        return Guid.NewGuid().ToString("N").Substring(0, 12).ToUpper();
    }
    
    /// <summary>
    /// 生成盐值
    /// </summary>
    private static string GenerateSalt()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }
    
    /// <summary>
    /// 哈希密码
    /// </summary>
    private static string HashPassword(string password, string salt)
    {
        using var sha256 = SHA256.Create();
        var combined = Encoding.UTF8.GetBytes(password + salt);
        var hash = sha256.ComputeHash(combined);
        return Convert.ToBase64String(hash);
    }
    
    /// <summary>
    /// 生成访问令牌
    /// </summary>
    private static string GenerateAccessToken()
    {
        var bytes = new byte[32];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return Convert.ToBase64String(bytes);
    }
    
    /// <summary>
    /// 获取令牌缓存键
    /// </summary>
    private static string GetTokenCacheKey(string shareCode, string accessToken)
    {
        return $"{shareCode}:{accessToken}";
    }
    
    /// <summary>
    /// 清理指定分享的所有令牌
    /// </summary>
    private void CleanupTokensForShare(string shareCode)
    {
        var keysToRemove = _accessTokenCache.Keys
            .Where(k => k.StartsWith($"{shareCode}:"))
            .ToList();
        
        foreach (var key in keysToRemove)
        {
            _accessTokenCache.TryRemove(key, out _);
        }
    }
    
    /// <summary>
    /// 清理过期的访问令牌
    /// </summary>
    private void CleanupExpiredTokens()
    {
        var now = DateTime.Now;
        var keysToRemove = _accessTokenCache
            .Where(kvp => kvp.Value.ExpireTime < now)
            .Select(kvp => kvp.Key)
            .ToList();
        
        foreach (var key in keysToRemove)
        {
            _accessTokenCache.TryRemove(key, out _);
        }
        
        if (keysToRemove.Count > 0)
        {
            _logger.LogDebug("已清理 {Count} 个过期访问令牌", keysToRemove.Count);
        }
    }
    
    #endregion
}
