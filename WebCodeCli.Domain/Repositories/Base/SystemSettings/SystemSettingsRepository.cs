using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Domain.Model;
using AntSK.Domain.Repositories.Base;

namespace WebCodeCli.Domain.Repositories.Base.SystemSettings;

/// <summary>
/// 系统设置仓储接口
/// </summary>
public interface ISystemSettingsRepository
{
    /// <summary>
    /// 获取设置值
    /// </summary>
    Task<string?> GetAsync(string key);

    /// <summary>
    /// 获取设置值（带默认值）
    /// </summary>
    Task<string> GetAsync(string key, string defaultValue);

    /// <summary>
    /// 获取布尔类型设置值
    /// </summary>
    Task<bool> GetBoolAsync(string key, bool defaultValue = false);

    /// <summary>
    /// 设置值
    /// </summary>
    Task<bool> SetAsync(string key, string? value, string? description = null);

    /// <summary>
    /// 设置布尔值
    /// </summary>
    Task<bool> SetBoolAsync(string key, bool value, string? description = null);

    /// <summary>
    /// 删除设置
    /// </summary>
    Task<bool> DeleteAsync(string key);

    /// <summary>
    /// 获取所有设置
    /// </summary>
    Task<Dictionary<string, string>> GetAllAsync();

    /// <summary>
    /// 检查系统是否已初始化
    /// </summary>
    Task<bool> IsSystemInitializedAsync();
}

/// <summary>
/// 系统设置仓储实现
/// </summary>
[ServiceDescription(typeof(ISystemSettingsRepository), ServiceLifetime.Scoped)]
public class SystemSettingsRepository : Repository<Domain.Model.SystemSettings>, ISystemSettingsRepository
{
    private readonly ILogger<SystemSettingsRepository> _logger;

    public SystemSettingsRepository(ILogger<SystemSettingsRepository> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// 获取设置值
    /// </summary>
    public async Task<string?> GetAsync(string key)
    {
        try
        {
            var setting = await GetFirstAsync(x => x.Key == key);
            return setting?.Value;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取设置 {Key} 失败", key);
            return null;
        }
    }

    /// <summary>
    /// 获取设置值（带默认值）
    /// </summary>
    public async Task<string> GetAsync(string key, string defaultValue)
    {
        var value = await GetAsync(key);
        return value ?? defaultValue;
    }

    /// <summary>
    /// 获取布尔类型设置值
    /// </summary>
    public async Task<bool> GetBoolAsync(string key, bool defaultValue = false)
    {
        var value = await GetAsync(key);
        if (string.IsNullOrEmpty(value))
            return defaultValue;
        
        return bool.TryParse(value, out var result) ? result : defaultValue;
    }

    /// <summary>
    /// 设置值
    /// </summary>
    public async Task<bool> SetAsync(string key, string? value, string? description = null)
    {
        try
        {
            var existing = await GetFirstAsync(x => x.Key == key);
            
            if (existing != null)
            {
                existing.Value = value;
                existing.UpdatedAt = DateTime.Now;
                if (description != null)
                    existing.Description = description;
                
                return await UpdateAsync(existing);
            }
            else
            {
                var setting = new Domain.Model.SystemSettings
                {
                    Key = key,
                    Value = value,
                    Description = description,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now
                };
                
                return await InsertAsync(setting);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "设置 {Key} 失败", key);
            return false;
        }
    }

    /// <summary>
    /// 设置布尔值
    /// </summary>
    public async Task<bool> SetBoolAsync(string key, bool value, string? description = null)
    {
        return await SetAsync(key, value.ToString(), description);
    }

    /// <summary>
    /// 删除设置
    /// </summary>
    public new async Task<bool> DeleteAsync(string key)
    {
        try
        {
            return await base.DeleteAsync(x => x.Key == key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除设置 {Key} 失败", key);
            return false;
        }
    }

    /// <summary>
    /// 获取所有设置
    /// </summary>
    public async Task<Dictionary<string, string>> GetAllAsync()
    {
        try
        {
            var list = await GetListAsync();
            return list.ToDictionary(x => x.Key, x => x.Value ?? string.Empty);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取所有设置失败");
            return new Dictionary<string, string>();
        }
    }

    /// <summary>
    /// 检查系统是否已初始化
    /// </summary>
    public async Task<bool> IsSystemInitializedAsync()
    {
        return await GetBoolAsync(SystemSettingsKeys.SystemInitialized, false);
    }
}
