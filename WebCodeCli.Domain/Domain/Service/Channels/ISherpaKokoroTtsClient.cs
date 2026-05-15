using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Domain.Domain.Service.Channels;

public interface ISherpaKokoroTtsClient
{
    Task<FeishuReplyTtsHealthStatus> GetHealthAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FeishuReplyTtsVoiceOption>> GetVoicesAsync(CancellationToken cancellationToken = default);

    Task<Stream> SynthesizeAsync(string text, string voiceId, CancellationToken cancellationToken = default);
}
