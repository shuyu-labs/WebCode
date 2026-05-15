using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service;

public interface IMessageSubmissionService
{
    Task<PreparedMessageSubmission> PrepareAsync(
        MessageDraft draft,
        CancellationToken cancellationToken = default);
}
