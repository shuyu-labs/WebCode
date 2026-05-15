using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Helpers;

public sealed class MessageAttachmentComposerState
{
    private readonly List<MessageDraftAttachmentInput> _pendingAttachments = [];

    public IReadOnlyList<MessageDraftAttachmentInput> PendingAttachments => _pendingAttachments;

    public bool HasAttachments => _pendingAttachments.Count > 0;

    public void Replace(IEnumerable<MessageDraftAttachmentInput>? attachments)
    {
        _pendingAttachments.Clear();

        if (attachments == null)
        {
            return;
        }

        foreach (var attachment in attachments)
        {
            if (attachment == null)
            {
                continue;
            }

            _pendingAttachments.Add(CloneAttachment(attachment));
        }
    }

    public bool Remove(string attachmentId)
    {
        if (string.IsNullOrWhiteSpace(attachmentId))
        {
            return false;
        }

        var index = _pendingAttachments.FindIndex(attachment => string.Equals(attachment.Id, attachmentId, StringComparison.Ordinal));
        if (index < 0)
        {
            return false;
        }

        _pendingAttachments.RemoveAt(index);
        return true;
    }

    public void Clear()
    {
        _pendingAttachments.Clear();
    }

    private static MessageDraftAttachmentInput CloneAttachment(MessageDraftAttachmentInput attachment)
    {
        return new MessageDraftAttachmentInput
        {
            Id = attachment.Id,
            FileName = attachment.FileName,
            ContentType = attachment.ContentType,
            Content = attachment.Content is { Length: > 0 }
                ? [.. attachment.Content]
                : []
        };
    }
}
