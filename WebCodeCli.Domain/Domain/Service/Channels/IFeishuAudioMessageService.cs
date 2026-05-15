namespace WebCodeCli.Domain.Domain.Service.Channels;

public interface IFeishuAudioMessageService
{
    Task<string> SendAudioMessageAsync(
        string chatId,
        string filePath,
        int durationMs,
        string? username = null,
        string? appId = null,
        CancellationToken cancellationToken = default);
}
