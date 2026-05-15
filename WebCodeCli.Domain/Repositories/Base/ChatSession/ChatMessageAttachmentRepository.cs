using AntSK.Domain.Repositories.Base;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using WebCodeCli.Domain.Common.Extensions;

namespace WebCodeCli.Domain.Repositories.Base.ChatSession;

[ServiceDescription(typeof(IChatMessageAttachmentRepository), ServiceLifetime.Scoped)]
public class ChatMessageAttachmentRepository : Repository<ChatMessageAttachmentEntity>, IChatMessageAttachmentRepository
{
    public ChatMessageAttachmentRepository(ISqlSugarClient context = null) : base(context)
    {
    }

    public async Task<List<ChatMessageAttachmentEntity>> GetBySessionIdAndUsernameAsync(string sessionId, string username)
    {
        return await GetDB().Queryable<ChatMessageAttachmentEntity>()
            .Where(x => x.SessionId == sessionId && x.Username == username)
            .OrderBy("CreatedAt asc, Id asc")
            .ToListAsync();
    }

    public async Task<bool> DeleteBySessionIdAndUsernameAsync(string sessionId, string username)
    {
        return await DeleteAsync(x => x.SessionId == sessionId && x.Username == username);
    }

    public async Task<bool> InsertAttachmentsAsync(List<ChatMessageAttachmentEntity> attachments)
    {
        if (attachments == null || attachments.Count == 0)
        {
            return true;
        }

        return await InsertRangeAsync(attachments);
    }
}
