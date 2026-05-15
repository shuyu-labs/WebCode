using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Domain.Domain.Service.Channels;

public static class ReplyTtsFfmpegPathResolver
{
    public static ReplyTtsExecutableResolutionResult Resolve(
        FeishuReplyTtsOptions? options,
        FeishuReplyTtsHealthStatus storageHealth)
    {
        var configuredPath = options?.FfmpegExecutablePath?.Trim();

        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            if (LooksLikeCommandName(configuredPath))
            {
                return new ReplyTtsExecutableResolutionResult(true, configuredPath, "Using configured ffmpeg command.");
            }

            if (File.Exists(configuredPath))
            {
                return new ReplyTtsExecutableResolutionResult(true, configuredPath, "Using configured ffmpeg executable path.");
            }
        }

        var bundledPath = BuildBundledPath(storageHealth.StorageRoot);
        if (!string.IsNullOrWhiteSpace(bundledPath) && File.Exists(bundledPath))
        {
            return new ReplyTtsExecutableResolutionResult(true, bundledPath, "Using ffmpeg from Feishu reply TTS storage root.");
        }

        return new ReplyTtsExecutableResolutionResult(
            false,
            null,
            BuildFailureMessage(configuredPath, bundledPath));
    }

    private static string BuildFailureMessage(string? configuredPath, string? bundledPath)
    {
        var details = new List<string>();
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            details.Add($"configured path '{configuredPath}' was not found");
        }

        if (!string.IsNullOrWhiteSpace(bundledPath))
        {
            details.Add($"bundled path '{bundledPath}' was not found");
        }

        return $"Feishu reply TTS ffmpeg executable is unavailable; {string.Join("; ", details)}. Configure FeishuReplyTts:FfmpegExecutablePath or place ffmpeg under the TTS storage root.";
    }

    private static string? BuildBundledPath(string? storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
        {
            return null;
        }

        return Path.Combine(storageRoot, "ffmpeg", "bin", GetExecutableFileName());
    }

    private static string GetExecutableFileName()
    {
        return OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
    }

    private static bool LooksLikeCommandName(string configuredPath)
    {
        return !Path.IsPathRooted(configuredPath) &&
               configuredPath.IndexOfAny(['\\', '/']) < 0;
    }

    public sealed record ReplyTtsExecutableResolutionResult(
        bool IsAvailable,
        string? ExecutablePath,
        string Message);
}
