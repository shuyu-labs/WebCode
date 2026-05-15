using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Domain.Domain.Service.Channels;

[ServiceDescription(typeof(IFeishuReplyTtsPlatformService), ServiceLifetime.Scoped)]
public sealed class FeishuReplyTtsPlatformService : IFeishuReplyTtsPlatformService
{
    private const string VoicesUnavailableMessage = "Feishu reply TTS voices are currently unavailable.";

    private readonly ReplyTtsStorageRootResolver _storageRootResolver;
    private readonly FeishuReplyTtsOptions _options;
    private readonly ISherpaKokoroTtsClient _ttsClient;
    private readonly IReplyTtsLocalServiceManager _localServiceManager;

    public FeishuReplyTtsPlatformService(
        ReplyTtsStorageRootResolver storageRootResolver,
        IOptions<FeishuReplyTtsOptions> options,
        ISherpaKokoroTtsClient ttsClient,
        IReplyTtsLocalServiceManager localServiceManager)
    {
        _storageRootResolver = storageRootResolver ?? throw new ArgumentNullException(nameof(storageRootResolver));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _ttsClient = ttsClient ?? throw new ArgumentNullException(nameof(ttsClient));
        _localServiceManager = localServiceManager ?? throw new ArgumentNullException(nameof(localServiceManager));
    }

    public async Task<FeishuReplyTtsHealthStatus> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var storageHealth = _storageRootResolver.Resolve();
        if (!storageHealth.IsAvailable)
        {
            return storageHealth;
        }

        var ffmpegResolution = ReplyTtsFfmpegPathResolver.Resolve(_options, storageHealth);
        if (!ffmpegResolution.IsAvailable)
        {
            return MergeHealth(
                storageHealth,
                new FeishuReplyTtsHealthStatus
                {
                    IsAvailable = false,
                    Message = ffmpegResolution.Message,
                    ServiceStatus = "ffmpeg-unavailable"
                });
        }

        try
        {
            var serviceHealth = await _ttsClient.GetHealthAsync(cancellationToken);
            return MergeHealth(storageHealth, serviceHealth);
        }
        catch (Exception ex) when (!IsCancellation(ex))
        {
            return MergeHealth(
                storageHealth,
                new FeishuReplyTtsHealthStatus
                {
                    IsAvailable = false,
                    Message = $"Local Kokoro/sherpa-onnx service is unavailable: {ex.Message}",
                    ServiceStatus = "unreachable"
                });
        }
    }

    public async Task<IReadOnlyList<FeishuReplyTtsVoiceOption>> GetVoicesAsync(CancellationToken cancellationToken = default)
    {
        var storageHealth = _storageRootResolver.Resolve();
        if (!storageHealth.IsAvailable)
        {
            return [];
        }

        try
        {
            return await _ttsClient.GetVoicesAsync(cancellationToken);
        }
        catch (Exception ex) when (!IsCancellation(ex))
        {
            return [];
        }
    }

    public async Task<FeishuReplyTtsVoiceResolutionResult> ResolveVoiceOrFallbackAsync(string? savedVoiceId, CancellationToken cancellationToken = default)
    {
        var health = await GetHealthAsync(cancellationToken);
        if (!health.IsAvailable)
        {
            return new FeishuReplyTtsVoiceResolutionResult
            {
                Success = false,
                UsedFallback = false,
                Message = string.IsNullOrWhiteSpace(health.Message)
                    ? VoicesUnavailableMessage
                    : health.Message
            };
        }

        var voices = await GetVoicesAsync(cancellationToken);
        if (voices.Count == 0)
        {
            return new FeishuReplyTtsVoiceResolutionResult
            {
                Success = false,
                UsedFallback = false,
                Message = VoicesUnavailableMessage
            };
        }

        var normalizedSavedVoiceId = Normalize(savedVoiceId);
        var normalizedDefaultVoiceId = await GetEffectiveDefaultVoiceIdAsync(cancellationToken);

        var savedVoice = FindVoice(voices, normalizedSavedVoiceId);
        if (savedVoice is not null)
        {
            return new FeishuReplyTtsVoiceResolutionResult
            {
                Success = true,
                VoiceId = savedVoice.VoiceId,
                Voice = savedVoice,
                UsedFallback = false
            };
        }

        var defaultVoice = FindVoice(voices, normalizedDefaultVoiceId);
        if (defaultVoice is not null)
        {
            return new FeishuReplyTtsVoiceResolutionResult
            {
                Success = true,
                VoiceId = defaultVoice.VoiceId,
                Voice = defaultVoice,
                UsedFallback = !string.IsNullOrWhiteSpace(normalizedSavedVoiceId),
                Message = !string.IsNullOrWhiteSpace(normalizedSavedVoiceId)
                    ? $"Saved Feishu reply TTS voice '{normalizedSavedVoiceId}' is unavailable. Falling back to '{defaultVoice.VoiceId}'."
                    : string.Empty
            };
        }

        return new FeishuReplyTtsVoiceResolutionResult
        {
            Success = false,
            UsedFallback = false,
            Message = "No Feishu reply TTS voice is available. Save a valid voice or configure a default voice."
        };
    }

    public async Task<FeishuReplyTtsHealthStatus> EnsureServiceStartedAsync(CancellationToken cancellationToken = default)
    {
        var storageHealth = _storageRootResolver.Resolve();
        if (!storageHealth.IsAvailable)
        {
            return storageHealth;
        }

        var ffmpegResolution = ReplyTtsFfmpegPathResolver.Resolve(_options, storageHealth);
        if (!ffmpegResolution.IsAvailable)
        {
            return MergeHealth(
                storageHealth,
                new FeishuReplyTtsHealthStatus
                {
                    IsAvailable = false,
                    Message = ffmpegResolution.Message,
                    ServiceStatus = "ffmpeg-unavailable"
                });
        }

        var startHealth = await _localServiceManager.EnsureStartedAsync(storageHealth, cancellationToken);
        if (!startHealth.IsAvailable)
        {
            return MergeHealth(storageHealth, startHealth);
        }

        return await GetHealthAsync(cancellationToken);
    }

    private FeishuReplyTtsHealthStatus MergeHealth(
        FeishuReplyTtsHealthStatus storageHealth,
        FeishuReplyTtsHealthStatus serviceHealth)
    {
        var defaultVoiceId = ResolveEffectiveDefaultVoiceId(serviceHealth.DefaultVoiceId);
        return new FeishuReplyTtsHealthStatus
        {
            IsAvailable = storageHealth.IsAvailable && serviceHealth.IsAvailable,
            StorageRoot = storageHealth.StorageRoot,
            Message = BuildMessage(storageHealth.Message, serviceHealth.Message, serviceHealth.IsAvailable),
            ModelsRoot = storageHealth.ModelsRoot,
            CacheRoot = storageHealth.CacheRoot,
            TempRoot = storageHealth.TempRoot,
            LogsRoot = storageHealth.LogsRoot,
            VenvRoot = storageHealth.VenvRoot,
            ServiceStatus = serviceHealth.ServiceStatus,
            Device = serviceHealth.Device,
            DefaultVoiceId = defaultVoiceId
        };
    }

    private static string BuildMessage(string storageMessage, string serviceMessage, bool isServiceAvailable)
    {
        if (isServiceAvailable || string.IsNullOrWhiteSpace(serviceMessage))
        {
            return storageMessage;
        }

        if (string.IsNullOrWhiteSpace(storageMessage))
        {
            return serviceMessage;
        }

        return $"{storageMessage} {serviceMessage}".Trim();
    }

    private static FeishuReplyTtsVoiceOption? FindVoice(
        IReadOnlyList<FeishuReplyTtsVoiceOption> voices,
        string? voiceId)
    {
        if (string.IsNullOrWhiteSpace(voiceId))
        {
            return null;
        }

        return voices.FirstOrDefault(voice => string.Equals(voice.VoiceId, voiceId, StringComparison.OrdinalIgnoreCase));
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private async Task<string?> GetEffectiveDefaultVoiceIdAsync(CancellationToken cancellationToken)
    {
        var configuredDefaultVoiceId = Normalize(_options.TtsDefaultVoiceId);
        if (!string.IsNullOrWhiteSpace(configuredDefaultVoiceId))
        {
            return configuredDefaultVoiceId;
        }

        var health = await GetHealthAsync(cancellationToken);
        return Normalize(health.DefaultVoiceId);
    }

    private string? ResolveEffectiveDefaultVoiceId(string? serviceDefaultVoiceId)
    {
        return Normalize(_options.TtsDefaultVoiceId) ?? Normalize(serviceDefaultVoiceId);
    }

    private static bool IsCancellation(Exception exception)
    {
        return exception is OperationCanceledException;
    }
}
