using Microsoft.Extensions.DependencyInjection;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Service;

namespace WebCodeCli.Domain.Domain.Service.Channels;

[ServiceDescription(typeof(IFeishuAudioMessageService), ServiceLifetime.Scoped)]
public sealed class FeishuAudioMessageService : IFeishuAudioMessageService
{
    private readonly IFeishuCardKitClient _feishuCardKitClient;
    private readonly IUserFeishuBotConfigService _userFeishuBotConfigService;

    public FeishuAudioMessageService(
        IFeishuCardKitClient feishuCardKitClient,
        IUserFeishuBotConfigService userFeishuBotConfigService)
    {
        _feishuCardKitClient = feishuCardKitClient ?? throw new ArgumentNullException(nameof(feishuCardKitClient));
        _userFeishuBotConfigService = userFeishuBotConfigService ?? throw new ArgumentNullException(nameof(userFeishuBotConfigService));
    }

    public async Task<string> SendAudioMessageAsync(
        string chatId,
        string filePath,
        int durationMs,
        string? username = null,
        string? appId = null,
        CancellationToken cancellationToken = default)
    {
        var effectiveOptions = await ResolveEffectiveOptionsAsync(username, appId);
        var fileKey = await _feishuCardKitClient.UploadAudioFileAsync(
            filePath,
            durationMs,
            cancellationToken,
            effectiveOptions);

        return await _feishuCardKitClient.SendAudioMessageAsync(
            chatId,
            fileKey,
            durationMs,
            cancellationToken,
            effectiveOptions);
    }

    private async Task<FeishuOptions> ResolveEffectiveOptionsAsync(string? username, string? appId)
    {
        if (!string.IsNullOrWhiteSpace(appId))
        {
            var appOptions = await _userFeishuBotConfigService.GetEffectiveOptionsByAppIdAsync(appId);
            if (appOptions != null)
            {
                return appOptions;
            }
        }

        if (!string.IsNullOrWhiteSpace(username))
        {
            return await _userFeishuBotConfigService.GetEffectiveOptionsAsync(username);
        }

        return _userFeishuBotConfigService.GetSharedDefaults();
    }
}
