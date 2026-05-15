using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Repositories.Base.UserFeishuBotConfig;

namespace WebCodeCli.Domain.Domain.Service;

[ServiceDescription(typeof(IUserFeishuBotConfigService), ServiceLifetime.Scoped)]
public class UserFeishuBotConfigService : IUserFeishuBotConfigService
{
    private readonly IUserFeishuBotConfigRepository _repository;
    private readonly FeishuOptions _globalOptions;

    public UserFeishuBotConfigService(
        IUserFeishuBotConfigRepository repository,
        IOptions<FeishuOptions> globalOptions)
    {
        _repository = repository;
        _globalOptions = globalOptions.Value;
    }

    public async Task<UserFeishuBotConfigEntity?> GetByUsernameAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return null;
        }

        return await _repository.GetByUsernameAsync(username.Trim());
    }

    public async Task<UserFeishuBotConfigEntity?> GetByAppIdAsync(string appId)
    {
        var normalizedAppId = NormalizeValue(appId);
        if (normalizedAppId == null)
        {
            return null;
        }

        var configs = await _repository.GetListAsync(x => x.AppId != null);
        return configs.FirstOrDefault(x => string.Equals(
            NormalizeValue(x.AppId),
            normalizedAppId,
            StringComparison.OrdinalIgnoreCase));
    }

    public async Task<UserFeishuBotConfigSaveResult> SaveAsync(UserFeishuBotConfigEntity config)
    {
        if (string.IsNullOrWhiteSpace(config.Username))
        {
            return UserFeishuBotConfigSaveResult.Failure("用户名不能为空。");
        }

        NormalizeConfig(config);

        var normalizedUsername = config.Username;
        var existing = await _repository.GetByUsernameAsync(normalizedUsername);
        var conflictingUsername = await FindConflictingUsernameByAppIdAsync(normalizedUsername, config.AppId);
        if (!string.IsNullOrWhiteSpace(conflictingUsername))
        {
            return UserFeishuBotConfigSaveResult.Conflict(conflictingUsername, config.AppId);
        }

        var now = DateTime.Now;

        if (existing == null)
        {
            config.CreatedAt = now;
            config.UpdatedAt = now;
            return await _repository.InsertAsync(config)
                ? UserFeishuBotConfigSaveResult.Saved()
                : UserFeishuBotConfigSaveResult.Failure("保存飞书机器人配置失败。");
        }

        existing.IsEnabled = config.IsEnabled;
        existing.AppId = config.AppId;
        existing.AppSecret = config.AppSecret;
        existing.EncryptKey = config.EncryptKey;
        existing.VerificationToken = config.VerificationToken;
        existing.DefaultCardTitle = config.DefaultCardTitle;
        existing.ThinkingMessage = config.ThinkingMessage;
        existing.HttpTimeoutSeconds = config.HttpTimeoutSeconds;
        existing.StreamingThrottleMs = config.StreamingThrottleMs;
        existing.ReplyTtsEnabled = config.ReplyTtsEnabled;
        existing.ReplyTtsVoiceId = config.ReplyTtsVoiceId;
        existing.UpdatedAt = now;

        return await _repository.UpdateAsync(existing)
            ? UserFeishuBotConfigSaveResult.Saved()
            : UserFeishuBotConfigSaveResult.Failure("保存飞书机器人配置失败。");
    }

    public async Task<bool> DeleteAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        return await _repository.DeleteAsync(x => x.Username == username.Trim());
    }

    public async Task<string?> FindConflictingUsernameByAppIdAsync(string username, string? appId)
    {
        var normalizedUsername = NormalizeValue(username);
        var normalizedAppId = NormalizeValue(appId);
        if (normalizedUsername == null || normalizedAppId == null)
        {
            return null;
        }

        var configs = await _repository.GetListAsync(x => x.AppId != null);
        return FeishuBotAppIdOwnershipHelper.FindConflictingUsername(normalizedUsername, normalizedAppId, configs);
    }

    public async Task<List<UserFeishuBotConfigEntity>> GetAutoStartCandidatesAsync()
    {
        var configs = await _repository.GetListAsync(x => x.AutoStartEnabled && x.IsEnabled && x.AppId != null && x.AppSecret != null);
        return configs
            .Where(UserFeishuBotOptionsFactory.HasUsableCredentials)
            .ToList();
    }

    public async Task<bool> UpdateRuntimePreferenceAsync(string username, bool autoStartEnabled, DateTime? lastStartedAt = null)
    {
        var normalizedUsername = NormalizeValue(username);
        if (normalizedUsername == null)
        {
            return false;
        }

        var existing = await _repository.GetByUsernameAsync(normalizedUsername);
        if (existing == null)
        {
            return false;
        }

        existing.AutoStartEnabled = autoStartEnabled;
        if (lastStartedAt.HasValue)
        {
            existing.LastStartedAt = lastStartedAt.Value;
        }

        existing.UpdatedAt = DateTime.Now;
        return await _repository.UpdateAsync(existing);
    }

    public async Task<FeishuOptions> GetEffectiveOptionsAsync(string? username)
    {
        var effective = GetSharedDefaults();
        if (string.IsNullOrWhiteSpace(username))
        {
            return effective;
        }

        var config = await _repository.GetByUsernameAsync(username.Trim());
        return UserFeishuBotOptionsFactory.CreateEffectiveOptions(effective, config) ?? effective;
    }

    public FeishuOptions GetSharedDefaults()
    {
        return UserFeishuBotOptionsFactory.CreateSharedDefaults(_globalOptions);
    }

    public async Task<FeishuOptions?> GetEffectiveOptionsByAppIdAsync(string? appId)
    {
        var config = await GetByAppIdAsync(appId ?? string.Empty);
        if (config == null)
        {
            return null;
        }

        return UserFeishuBotOptionsFactory.CreateEffectiveOptions(GetSharedDefaults(), config);
    }

    private static void NormalizeConfig(UserFeishuBotConfigEntity config)
    {
        config.Username = NormalizeValue(config.Username) ?? string.Empty;
        config.AppId = NormalizeValue(config.AppId);
        config.AppSecret = NormalizeValue(config.AppSecret);
        config.EncryptKey = NormalizeValue(config.EncryptKey);
        config.VerificationToken = NormalizeValue(config.VerificationToken);
        config.DefaultCardTitle = NormalizeValue(config.DefaultCardTitle);
        config.ThinkingMessage = NormalizeValue(config.ThinkingMessage);
        config.ReplyTtsVoiceId = NormalizeValue(config.ReplyTtsVoiceId);
    }

    private static string? NormalizeValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
