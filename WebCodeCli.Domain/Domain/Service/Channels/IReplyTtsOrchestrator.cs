using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Domain.Domain.Service.Channels;

public interface IReplyTtsOrchestrator
{
    Task QueueCompletedReplyAsync(FeishuCompletedReplyTtsRequest request);
}
