using WebCodeCli.Domain.Domain.Service.Channels;

namespace WebCodeCli.Domain.Tests;

public class FeishuIncomingAttachmentParserTests
{
    [Fact]
    public void Parse_ImagePayload_ReturnsStructuredAttachment()
    {
        var parser = new FeishuIncomingAttachmentParser();

        var attachments = parser.Parse(
            "image",
            """
            {
              "image_key": "img_v3_02f2d1d1-structured",
              "file_name": "design-review.png"
            }
            """);

        var attachment = Assert.Single(attachments);
        Assert.Equal("image", attachment.MessageType);
        Assert.Equal("img_v3_02f2d1d1-structured", attachment.AttachmentKey);
        Assert.Equal("design-review.png", attachment.DisplayName);
        Assert.Equal("image/png", attachment.MimeType);
    }

    [Fact]
    public void Parse_FilePayload_ReturnsStructuredAttachment()
    {
        var parser = new FeishuIncomingAttachmentParser();

        var attachments = parser.Parse(
            "file",
            """
            {
              "file_key": "file_v3_9f0d4a",
              "file_name": "requirements.pdf",
              "mime_type": "application/pdf",
              "file_size": 12345
            }
            """);

        var attachment = Assert.Single(attachments);
        Assert.Equal("file", attachment.MessageType);
        Assert.Equal("file_v3_9f0d4a", attachment.AttachmentKey);
        Assert.Equal("requirements.pdf", attachment.DisplayName);
        Assert.Equal("application/pdf", attachment.MimeType);
        Assert.Equal(12345, attachment.SizeBytes);
    }

    [Fact]
    public void Parse_PostPayloadWithInlineImage_ReturnsStructuredImageAttachment()
    {
        var parser = new FeishuIncomingAttachmentParser();

        var attachments = parser.Parse(
            "post",
            """
            {
              "title": "",
              "content": [
                [
                  {
                    "tag": "img",
                    "image_key": "img_v3_inline_001",
                    "width": 432,
                    "height": 908
                  }
                ],
                [
                  {
                    "tag": "text",
                    "text": "这是啥？"
                  }
                ]
              ]
            }
            """);

        var attachment = Assert.Single(attachments);
        Assert.Equal("image", attachment.MessageType);
        Assert.Equal("img_v3_inline_001", attachment.AttachmentKey);
        Assert.Equal("img_v3_inline_001", attachment.DisplayName);
        Assert.Equal("image/png", attachment.MimeType);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"x\"")]
    [InlineData("1")]
    public void Parse_NonObjectValidJson_ReturnsEmpty(string rawContent)
    {
        var parser = new FeishuIncomingAttachmentParser();

        var attachments = parser.Parse("image", rawContent);

        Assert.Empty(attachments);
    }
}
