namespace WebCodeCli.Domain.Domain.Model.Channels;

public sealed class FeishuReplyTtsHealthStatus
{
    public bool IsAvailable { get; set; }

    public string? StorageRoot { get; set; }

    public string Message { get; set; } = string.Empty;

    public string? ModelsRoot { get; set; }

    public string? CacheRoot { get; set; }

    public string? TempRoot { get; set; }

    public string? LogsRoot { get; set; }

    public string? VenvRoot { get; set; }

    public string? ServiceStatus { get; set; }

    public string? Device { get; set; }

    public string? DefaultVoiceId { get; set; }
}
