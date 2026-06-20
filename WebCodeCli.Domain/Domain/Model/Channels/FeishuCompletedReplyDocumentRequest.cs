namespace WebCodeCli.Domain.Domain.Model.Channels;

public sealed class FeishuCompletedReplyDocumentRequest
{
    public string ChatId { get; set; } = string.Empty;

    public string? SessionId { get; set; }

    public string? CliThreadId { get; set; }

    public string Output { get; set; } = string.Empty;

    public string? FinalAnswerOutput { get; set; }

    public string? OriginalUserQuestion { get; set; }

    public string? Username { get; set; }

    public string? AppId { get; set; }
}
