using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service;

public interface IAttachmentStagingService
{
    Task<List<StagedMessageAttachment>> StageAsync(
        string sessionId,
        string submissionId,
        IReadOnlyCollection<MessageDraftAttachmentInput> attachments,
        CancellationToken cancellationToken = default);
}
