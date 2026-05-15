using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service.Channels;

namespace WebCodeCli.Domain.Tests;

public class FeishuAttachmentDraftServiceTests
{
    [Fact]
    public void OpenDraft_CreatesIsolatedDraftPerChatAndSender()
    {
        var service = new FeishuAttachmentDraftService();

        var firstDraft = service.OpenDraft(
            "cli-app",
            "chat-alpha",
            "sender-a",
            "session-a",
            "codex");
        var secondDraft = service.OpenDraft(
            "cli-app",
            "chat-alpha",
            "sender-b",
            "session-b",
            "claude-code");

        firstDraft.Text = "mutated snapshot";
        firstDraft.Attachments.Add(new MessageAttachment
        {
            Id = "snapshot-only",
            DisplayName = "snapshot.txt",
            MimeType = "text/plain",
            Kind = MessageAttachmentKind.Text,
            WorkspaceRelativePath = ".webcode/message-inputs/snapshot.txt"
        });

        Assert.NotSame(firstDraft, secondDraft);
        Assert.Equal("sender-a", firstDraft.SenderId);
        Assert.Equal("sender-b", secondDraft.SenderId);

        var retrievedFirst = service.GetDraft("cli-app", "chat-alpha", "sender-a");
        var retrievedSecond = service.GetDraft("cli-app", "chat-alpha", "sender-b");

        Assert.NotNull(retrievedFirst);
        Assert.NotNull(retrievedSecond);
        Assert.Equal("session-a", retrievedFirst!.SessionId);
        Assert.Equal("codex", retrievedFirst.ToolId);
        Assert.Equal("session-b", retrievedSecond!.SessionId);
        Assert.Equal("claude-code", retrievedSecond.ToolId);
        Assert.Empty(retrievedFirst.Attachments);
        Assert.Empty(retrievedSecond.Attachments);
        Assert.Equal(string.Empty, retrievedFirst.Text);
    }

    [Fact]
    public void UpdateTextAndAddStagedAttachment_PreservesDraftUntilExplicitClear()
    {
        var service = new FeishuAttachmentDraftService();
        service.OpenDraft(
            "cli-app",
            "chat-alpha",
            "sender-a",
            "session-a",
            "codex");

        service.UpdateText("cli-app", "chat-alpha", "sender-a", "Review both staged files together.");
        service.AddStagedAttachment(
            "cli-app",
            "chat-alpha",
            "sender-a",
            new MessageAttachment
            {
                Id = "attachment-1",
                DisplayName = "diagram.png",
                MimeType = "image/png",
                Extension = ".png",
                SizeBytes = 128,
                Kind = MessageAttachmentKind.Image,
                WorkspaceRelativePath = ".webcode/message-inputs/draft-1/diagram.png"
            });
        service.AddStagedAttachment(
            "cli-app",
            "chat-alpha",
            "sender-a",
            new MessageAttachment
            {
                Id = "attachment-2",
                DisplayName = "requirements.pdf",
                MimeType = "application/pdf",
                Extension = ".pdf",
                SizeBytes = 256,
                Kind = MessageAttachmentKind.Pdf,
                WorkspaceRelativePath = ".webcode/message-inputs/draft-1/requirements.pdf"
            });

        var persisted = service.GetDraft("cli-app", "chat-alpha", "sender-a");

        Assert.NotNull(persisted);
        Assert.Equal("Review both staged files together.", persisted!.Text);
        Assert.Equal(2, persisted.Attachments.Count);
        Assert.Equal(
            [".webcode/message-inputs/draft-1/diagram.png", ".webcode/message-inputs/draft-1/requirements.pdf"],
            persisted.Attachments.Select(attachment => attachment.WorkspaceRelativePath).ToArray());

        persisted.Text = "mutated snapshot";
        persisted.Attachments.Clear();

        var refreshed = service.GetDraft("cli-app", "chat-alpha", "sender-a");

        Assert.NotSame(persisted, refreshed);
        Assert.Equal("Review both staged files together.", refreshed!.Text);
        Assert.Equal(2, refreshed.Attachments.Count);

        service.ClearDraft("cli-app", "chat-alpha", "sender-a");

        Assert.Null(service.GetDraft("cli-app", "chat-alpha", "sender-a"));
    }

    [Fact]
    public void UpdateOperations_WithoutOpenDraft_ReturnNullAndDoNotCreateDraft()
    {
        var service = new FeishuAttachmentDraftService();

        var updatedText = service.UpdateText("cli-app", "chat-alpha", "sender-a", "ignored");
        var updatedAttachment = service.AddStagedAttachment(
            "cli-app",
            "chat-alpha",
            "sender-a",
            new MessageAttachment
            {
                Id = "attachment-1",
                DisplayName = "notes.txt",
                MimeType = "text/plain",
                Kind = MessageAttachmentKind.Text,
                WorkspaceRelativePath = ".webcode/message-inputs/draft-1/notes.txt"
            });

        Assert.Null(updatedText);
        Assert.Null(updatedAttachment);
        Assert.Null(service.GetDraft("cli-app", "chat-alpha", "sender-a"));
    }
}
