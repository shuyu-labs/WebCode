namespace WebCodeCli.Domain.Common.Options;

public sealed class FeishuReplyTtsOptions
{
    public string? TtsStorageRoot { get; set; }

    public string TtsServiceBaseUrl { get; set; } = "http://127.0.0.1:5058";

    public int TtsServiceTimeoutSeconds { get; set; } = 180;

    public string TtsPreferredDevice { get; set; } = "cpu";

    public string? TtsDefaultVoiceId { get; set; }

    public int TtsChunkMaxChars { get; set; } = 160;

    public string? FfmpegExecutablePath { get; set; }

    public string? TtsServiceStartScriptPath { get; set; }

    public string? TtsServicePythonPath { get; set; }

    public int TtsServiceStartupTimeoutSeconds { get; set; } = 30;
}
