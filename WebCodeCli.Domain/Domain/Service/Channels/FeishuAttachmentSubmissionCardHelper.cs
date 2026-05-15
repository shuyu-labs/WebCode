using System.Text;
using System.Text.Json;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Domain.Domain.Service.Channels;

internal static class FeishuAttachmentSubmissionCardHelper
{
    public static string BuildSubmissionCardJson(
        string sessionId,
        string chatKey,
        string? toolId,
        string attachmentType,
        string attachmentName,
        string attachmentPath,
        string mimeType,
        string? defaultInstruction = null)
    {
        var attachmentLabel = GetAttachmentLabel(attachmentType);
        var actionValue = new
        {
            action = FeishuHelpCardAction.SubmitAttachmentPromptAction,
            session_id = sessionId,
            chat_key = chatKey,
            tool_id = toolId,
            attachment_type = attachmentType,
            attachment_name = attachmentName,
            attachment_path = attachmentPath,
            attachment_mime_type = mimeType
        };

        var card = new
        {
            schema = "2.0",
            config = new
            {
                wide_screen_mode = true
            },
            header = new
            {
                template = "blue",
                title = new
                {
                    tag = "plain_text",
                    content = $"待提交{attachmentLabel}"
                }
            },
            body = new
            {
                elements = new object[]
                {
                    BuildMarkdownBlock($"已收到{attachmentLabel}，请先在下方补充说明，再一起发送给 CLI。普通文本消息仍可直接发送。"),
                    BuildMarkdownBlock($"绑定会话：`{EscapeInlineCode(sessionId)}`"),
                    BuildMarkdownBlock($"{attachmentLabel}名称：`{EscapeInlineCode(attachmentName)}`"),
                    BuildMarkdownBlock($"本地路径：`{EscapeInlineCode(attachmentPath)}`"),
                    BuildMarkdownBlock($"MIME 类型：`{EscapeInlineCode(mimeType)}`"),
                    new
                    {
                        tag = "form",
                        name = FeishuAttachmentSubmissionDefaults.FormName,
                        elements = new object[]
                        {
                            new
                            {
                                tag = "column_set",
                                flex_mode = "none",
                                horizontal_spacing = "8px",
                                columns = new object[]
                                {
                                    new
                                    {
                                        tag = "column",
                                        width = "weighted",
                                        weight = 5,
                                        vertical_align = "top",
                                        elements = new object[]
                                        {
                                            new
                                            {
                                                tag = "input",
                                                input_type = "text",
                                                name = FeishuAttachmentSubmissionDefaults.PromptFieldName,
                                                label = new { tag = "plain_text", content = FeishuAttachmentSubmissionDefaults.PromptLabel },
                                                placeholder = new { tag = "plain_text", content = FeishuAttachmentSubmissionDefaults.PromptPlaceholder },
                                                default_value = NormalizePromptDefaultValue(defaultInstruction),
                                                is_multiline = true,
                                                max_length = FeishuAttachmentSubmissionDefaults.PromptMaxLength,
                                                max_lines = 6
                                            }
                                        }
                                    },
                                    new
                                    {
                                        tag = "column",
                                        width = "auto",
                                        vertical_align = "bottom",
                                        elements = new object[]
                                        {
                                            new
                                            {
                                                tag = "button",
                                                text = new { tag = "plain_text", content = FeishuAttachmentSubmissionDefaults.SubmitButtonText },
                                                type = "primary",
                                                action_type = "form_submit",
                                                name = FeishuAttachmentSubmissionDefaults.SubmitButtonName,
                                                value = actionValue
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        return JsonSerializer.Serialize(card);
    }

    public static string BuildCliPrompt(
        string attachmentType,
        string attachmentName,
        string attachmentPath,
        string mimeType,
        string userInstruction)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"[Feishu {attachmentType} attached]");
        builder.AppendLine($"File name: {attachmentName}");
        builder.AppendLine($"Local file path: {attachmentPath}");
        builder.AppendLine($"MIME type: {mimeType}");
        builder.AppendLine("Use the local file directly from this workspace.");
        builder.AppendLine();
        builder.Append(userInstruction.Trim());
        return builder.ToString().Trim();
    }

    private static object BuildMarkdownBlock(string content)
    {
        return new
        {
            tag = "div",
            text = new
            {
                tag = "lark_md",
                content
            }
        };
    }

    private static string GetAttachmentLabel(string attachmentType)
    {
        return string.Equals(attachmentType, "image", StringComparison.OrdinalIgnoreCase)
            ? "图片"
            : "文件";
    }

    private static string EscapeInlineCode(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace("`", "'");
    }

    private static string NormalizePromptDefaultValue(string? defaultInstruction)
    {
        if (string.IsNullOrEmpty(defaultInstruction))
        {
            return string.Empty;
        }

        return defaultInstruction.Length <= FeishuAttachmentSubmissionDefaults.PromptMaxLength
            ? defaultInstruction
            : defaultInstruction[..FeishuAttachmentSubmissionDefaults.PromptMaxLength];
    }
}
