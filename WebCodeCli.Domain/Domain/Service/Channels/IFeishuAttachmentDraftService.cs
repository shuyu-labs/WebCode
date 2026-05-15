using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service.Channels;

public interface IFeishuAttachmentDraftService
{
    FeishuAttachmentDraftState OpenDraft(string? appId, string chatId, string senderId, string sessionId, string toolId);

    FeishuAttachmentDraftState? GetDraft(string? appId, string chatId, string senderId);

    FeishuAttachmentDraftState? UpdateText(string? appId, string chatId, string senderId, string text);

    FeishuAttachmentDraftState? AddStagedAttachment(string? appId, string chatId, string senderId, MessageAttachment attachment);

    FeishuAttachmentDraftState? RemoveAttachment(string? appId, string chatId, string senderId, string attachmentId);

    void ClearDraft(string? appId, string chatId, string senderId);
}
