using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Domain.Domain.Service.Channels;

public interface IReplyTtsLocalServiceManager
{
    Task<FeishuReplyTtsHealthStatus> EnsureStartedAsync(
        FeishuReplyTtsHealthStatus storageHealth,
        CancellationToken cancellationToken = default);
}
