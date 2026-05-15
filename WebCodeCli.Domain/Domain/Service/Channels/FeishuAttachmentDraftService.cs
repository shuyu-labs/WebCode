using System.Collections.Concurrent;
using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service.Channels;

public class FeishuAttachmentDraftService : IFeishuAttachmentDraftService
{
    private readonly ConcurrentDictionary<string, DraftEntry> _drafts = new(StringComparer.Ordinal);

    public FeishuAttachmentDraftState OpenDraft(string? appId, string chatId, string senderId, string sessionId, string toolId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolId);

        var now = DateTime.UtcNow;
        var draft = new DraftEntry
        {
            DraftId = Guid.NewGuid().ToString("N"),
            AppId = NormalizeAppId(appId),
            ChatId = chatId,
            SenderId = senderId,
            SessionId = sessionId,
            ToolId = toolId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _drafts[BuildKey(appId, chatId, senderId)] = draft;
        return CreateSnapshot(draft);
    }

    public FeishuAttachmentDraftState? GetDraft(string? appId, string chatId, string senderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderId);

        return _drafts.TryGetValue(BuildKey(appId, chatId, senderId), out var draft)
            ? CreateSnapshot(draft)
            : null;
    }

    public FeishuAttachmentDraftState? UpdateText(string? appId, string chatId, string senderId, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderId);

        if (!_drafts.TryGetValue(BuildKey(appId, chatId, senderId), out var draft))
        {
            return null;
        }

        lock (draft)
        {
            draft.Text = text ?? string.Empty;
            draft.UpdatedAtUtc = DateTime.UtcNow;
            return CreateSnapshot(draft);
        }
    }

    public FeishuAttachmentDraftState? AddStagedAttachment(string? appId, string chatId, string senderId, MessageAttachment attachment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderId);
        ArgumentNullException.ThrowIfNull(attachment);

        if (!_drafts.TryGetValue(BuildKey(appId, chatId, senderId), out var draft))
        {
            return null;
        }

        lock (draft)
        {
            draft.Attachments.Add(CloneAttachment(attachment));
            draft.UpdatedAtUtc = DateTime.UtcNow;
            return CreateSnapshot(draft);
        }
    }

    public FeishuAttachmentDraftState? RemoveAttachment(string? appId, string chatId, string senderId, string attachmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderId);
        ArgumentException.ThrowIfNullOrWhiteSpace(attachmentId);

        if (!_drafts.TryGetValue(BuildKey(appId, chatId, senderId), out var draft))
        {
            return null;
        }

        lock (draft)
        {
            draft.Attachments.RemoveAll(attachment => string.Equals(attachment.Id, attachmentId, StringComparison.Ordinal));
            draft.UpdatedAtUtc = DateTime.UtcNow;
            return CreateSnapshot(draft);
        }
    }

    public void ClearDraft(string? appId, string chatId, string senderId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentException.ThrowIfNullOrWhiteSpace(senderId);

        _drafts.TryRemove(BuildKey(appId, chatId, senderId), out _);
    }

    private static FeishuAttachmentDraftState CreateSnapshot(DraftEntry draft)
    {
        lock (draft)
        {
            return new FeishuAttachmentDraftState
            {
                DraftId = draft.DraftId,
                AppId = draft.AppId,
                ChatId = draft.ChatId,
                SenderId = draft.SenderId,
                SessionId = draft.SessionId,
                ToolId = draft.ToolId,
                Text = draft.Text,
                Attachments = draft.Attachments.Select(CloneAttachment).ToList(),
                CreatedAtUtc = draft.CreatedAtUtc,
                UpdatedAtUtc = draft.UpdatedAtUtc
            };
        }
    }

    private static MessageAttachment CloneAttachment(MessageAttachment attachment)
    {
        return new MessageAttachment
        {
            Id = attachment.Id,
            DisplayName = attachment.DisplayName,
            MimeType = attachment.MimeType,
            Extension = attachment.Extension,
            SizeBytes = attachment.SizeBytes,
            Kind = attachment.Kind,
            WorkspaceRelativePath = attachment.WorkspaceRelativePath,
            CreatedAt = attachment.CreatedAt
        };
    }

    private static string BuildKey(string? appId, string chatId, string senderId)
    {
        return string.Join("::", NormalizeAppId(appId), chatId, senderId);
    }

    private static string NormalizeAppId(string? appId)
    {
        return appId ?? string.Empty;
    }

    private sealed class DraftEntry
    {
        public string DraftId { get; set; } = Guid.NewGuid().ToString("N");

        public string AppId { get; set; } = string.Empty;

        public string ChatId { get; set; } = string.Empty;

        public string SenderId { get; set; } = string.Empty;

        public string SessionId { get; set; } = string.Empty;

        public string ToolId { get; set; } = string.Empty;

        public string Text { get; set; } = string.Empty;

        public List<MessageAttachment> Attachments { get; set; } = new();

        public DateTime CreatedAtUtc { get; set; }

        public DateTime UpdatedAtUtc { get; set; }
    }
}
