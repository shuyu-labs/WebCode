namespace WebCodeCli.Domain.Domain.Model;

public static class FeishuAttachmentSubmissionDefaults
{
    public const string FormName = "feishu_attachment_submission_form";
    public const string PromptFieldName = "feishu_attachment_prompt";
    public const string SubmitButtonName = "submit_attachment_prompt_button";
    public const string PromptLabel = "补充说明";
    public const string PromptPlaceholder = "补充你希望 CLI 处理的内容，例如：提取文字、分析报错、检查 UI 现象、修改页面问题。";
    public const string SubmitButtonText = "发送给 CLI";
    public const int PromptMaxLength = 1000;
    public const string EmptyPromptWarning = "⚠️ 请先补充说明，再发送给 CLI";
    public const string PromptTooLongWarning = "⚠️ 补充说明不能超过 1000 个字符";
}
