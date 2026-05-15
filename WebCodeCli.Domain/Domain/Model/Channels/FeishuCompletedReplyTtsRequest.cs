namespace WebCodeCli.Domain.Domain.Model.Channels;

public sealed class FeishuCompletedReplyTtsRequest
{
    public string ChatId { get; set; } = string.Empty;

    public string? SessionId { get; set; }

    public string Output { get; set; } = string.Empty;

    public string? Username { get; set; }

    public string? AppId { get; set; }
}
