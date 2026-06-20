namespace WebCodeCli.Domain.Domain.Model.Channels;

public static class ReplyTtsModes
{
    public const string Off = "Off";
    public const string FullReply = "FullReply";
    public const string FinalOnly = "FinalOnly";

    public static string Resolve(string? mode, bool legacyEnabled)
    {
        var normalizedMode = Normalize(mode);
        if (normalizedMode != null)
        {
            return normalizedMode;
        }

        return legacyEnabled ? FullReply : Off;
    }

    public static string? Normalize(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return null;
        }

        var trimmed = mode.Trim();
        if (string.Equals(trimmed, Off, StringComparison.OrdinalIgnoreCase))
        {
            return Off;
        }

        if (string.Equals(trimmed, FullReply, StringComparison.OrdinalIgnoreCase))
        {
            return FullReply;
        }

        if (string.Equals(trimmed, FinalOnly, StringComparison.OrdinalIgnoreCase))
        {
            return FinalOnly;
        }

        return Off;
    }

    public static bool IsEnabled(string? mode)
    {
        return !string.Equals(mode, Off, StringComparison.OrdinalIgnoreCase);
    }
}
