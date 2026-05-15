namespace WebCodeCli.Domain.Domain.Service.Channels;

public interface IExternalProcessRunner
{
    Task<ExternalProcessResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        CancellationToken cancellationToken = default);
}

public sealed record ExternalProcessResult(int ExitCode, string StandardOutput, string StandardError);
