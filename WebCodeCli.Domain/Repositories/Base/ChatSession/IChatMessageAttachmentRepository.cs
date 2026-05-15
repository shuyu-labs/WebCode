using WebCodeCli.Domain.Repositories.Base;

namespace WebCodeCli.Domain.Repositories.Base.ChatSession;

public interface IChatMessageAttachmentRepository : IRepository<ChatMessageAttachmentEntity>
{
    Task<List<ChatMessageAttachmentEntity>> GetBySessionIdAndUsernameAsync(string sessionId, string username);

    Task<bool> DeleteBySessionIdAndUsernameAsync(string sessionId, string username);

    Task<bool> InsertAttachmentsAsync(List<ChatMessageAttachmentEntity> attachments);
}
