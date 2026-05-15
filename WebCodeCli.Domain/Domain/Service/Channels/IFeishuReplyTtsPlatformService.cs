using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Domain.Domain.Service.Channels;

public interface IFeishuReplyTtsPlatformService
{
    Task<FeishuReplyTtsHealthStatus> GetHealthAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeishuReplyTtsVoiceOption>> GetVoicesAsync(CancellationToken cancellationToken = default);

    Task<FeishuReplyTtsVoiceResolutionResult> ResolveVoiceOrFallbackAsync(string? savedVoiceId, CancellationToken cancellationToken = default);

    Task<FeishuReplyTtsHealthStatus> EnsureServiceStartedAsync(CancellationToken cancellationToken = default);
}

public sealed class FeishuReplyTtsVoiceResolutionResult
{
    public bool Success { get; set; }

    public string? VoiceId { get; set; }

    public FeishuReplyTtsVoiceOption? Voice { get; set; }

    public bool UsedFallback { get; set; }

    public string Message { get; set; } = string.Empty;
}
