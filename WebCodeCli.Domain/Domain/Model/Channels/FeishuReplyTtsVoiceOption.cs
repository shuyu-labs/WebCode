namespace WebCodeCli.Domain.Domain.Model.Channels;

public sealed class FeishuReplyTtsVoiceOption
{
    public string VoiceId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string? Language { get; set; }

    public string? Gender { get; set; }
}
