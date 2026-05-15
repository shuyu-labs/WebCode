using FeishuNetSdk.Im.Dtos;

namespace WebCodeCli.Domain.Domain.Service.Channels;

public class FeishuAttachmentDraftCardBuilder
{
    public ElementsCardV2Dto BuildCard(FeishuAttachmentDraftState draft)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var elements = new List<object>
        {
            new
            {
                tag = "div",
                text = new
                {
                    tag = "lark_md",
                    content = $"当前会话：`{draft.SessionId}`\n工具：`{draft.ToolId}`\n草稿 ID：`{draft.DraftId}`"
                }
            },
            new
            {
                tag = "div",
                text = new
                {
                    tag = "lark_md",
                    content = string.IsNullOrWhiteSpace(draft.Text)
                        ? "请继续发送文本或附件，整理完成后点击“提交到 CLI”。"
                        : $"**文本内容**\n{draft.Text}"
                }
            },
            new { tag = "hr" }
        };

        if (draft.Attachments.Count == 0)
        {
            elements.Add(new
            {
                tag = "div",
                text = new
                {
                    tag = "lark_md",
                    content = "当前还没有已暂存的附件。"
                }
            });
        }
        else
        {
            foreach (var attachment in draft.Attachments)
            {
                elements.Add(new
                {
                    tag = "div",
                    text = new
                    {
                        tag = "lark_md",
                        content = $"**附件** `{attachment.DisplayName}`\n类型：`{attachment.MimeType}`\n路径：`{attachment.WorkspaceRelativePath}`"
                    }
                });
            }
        }

        elements.Add(new { tag = "hr" });
        elements.Add(BuildActionButton(
            "提交到 CLI",
            "primary",
            new
            {
                action = "submit_attachment_draft"
            }));
        elements.Add(BuildActionButton(
            "清空草稿",
            "default",
            new
            {
                action = "clear_attachment_draft"
            }));

        return new ElementsCardV2Dto
        {
            Header = new ElementsCardV2Dto.HeaderSuffix
            {
                Template = "turquoise",
                Title = new HeaderTitleElement
                {
                    Content = "📎 附件草稿"
                }
            },
            Config = new ElementsCardV2Dto.ConfigSuffix
            {
                EnableForward = true,
                UpdateMulti = true
            },
            Body = new ElementsCardV2Dto.BodySuffix
            {
                Elements = elements.ToArray()
            }
        };
    }

    private static object BuildActionButton(string text, string type, object value)
    {
        return new
        {
            tag = "button",
            text = new
            {
                tag = "plain_text",
                content = text
            },
            type,
            behaviors = new[]
            {
                new
                {
                    type = "callback",
                    value
                }
            }
        };
    }
}
