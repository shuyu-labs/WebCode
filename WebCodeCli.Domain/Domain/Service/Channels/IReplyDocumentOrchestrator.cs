using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Domain.Domain.Service.Channels;

public interface IReplyDocumentOrchestrator
{
    Task QueueCompletedReplyAsync(FeishuCompletedReplyDocumentRequest request);
}
