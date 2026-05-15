using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Helpers;
using Xunit;

namespace WebCodeCli.Tests;

public sealed class MessageAttachmentComposerStateTests
{
    [Fact]
    public void Replace_SetsPendingAttachmentsAndHasAttachments()
    {
        var state = new MessageAttachmentComposerState();
        var first = CreateAttachment("first.txt");
        var second = CreateAttachment("second.png");

        state.Replace([first, second]);

        Assert.True(state.HasAttachments);
        Assert.Collection(
            state.PendingAttachments,
            attachment => Assert.Equal(first.Id, attachment.Id),
            attachment => Assert.Equal(second.Id, attachment.Id));
    }

    [Fact]
    public void Replace_ReplacesExistingAttachments()
    {
        var state = new MessageAttachmentComposerState();
        var original = CreateAttachment("original.txt");
        var replacement = CreateAttachment("replacement.pdf");

        state.Replace([original]);
        state.Replace([replacement]);

        Assert.True(state.HasAttachments);
        Assert.Single(state.PendingAttachments);
        Assert.Equal(replacement.Id, state.PendingAttachments[0].Id);
    }

    [Fact]
    public void Remove_DeletesMatchingAttachmentAndUpdatesHasAttachments()
    {
        var state = new MessageAttachmentComposerState();
        var first = CreateAttachment("first.txt");
        var second = CreateAttachment("second.png");

        state.Replace([first, second]);

        var removed = state.Remove(first.Id);

        Assert.True(removed);
        Assert.True(state.HasAttachments);
        Assert.Single(state.PendingAttachments);
        Assert.Equal(second.Id, state.PendingAttachments[0].Id);
    }

    [Fact]
    public void Clear_RemovesAllAttachments()
    {
        var state = new MessageAttachmentComposerState();

        state.Replace([CreateAttachment("first.txt")]);
        state.Clear();

        Assert.False(state.HasAttachments);
        Assert.Empty(state.PendingAttachments);
    }

    private static MessageDraftAttachmentInput CreateAttachment(string fileName)
    {
        return new MessageDraftAttachmentInput
        {
            FileName = fileName,
            ContentType = "text/plain",
            Content = [1, 2, 3]
        };
    }
}
