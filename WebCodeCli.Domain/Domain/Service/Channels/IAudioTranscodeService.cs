namespace WebCodeCli.Domain.Domain.Service.Channels;

public interface IAudioTranscodeService
{
    Task<string> TranscodeChunkAsync(
        string jobId,
        string inputWavPath,
        int chunkIndex,
        CancellationToken cancellationToken = default);
}
