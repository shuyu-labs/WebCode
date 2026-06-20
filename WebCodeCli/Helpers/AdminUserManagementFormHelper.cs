namespace WebCodeCli.Helpers;

public static class AdminUserManagementFormHelper
{
    public static List<string> ParseAllowedDirectories(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new List<string>();
        }

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (seen.Add(line))
            {
                result.Add(line);
            }
        }

        return result;
    }

    public static string FormatAllowedDirectories(IEnumerable<string>? directories)
    {
        if (directories == null)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, directories
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .Select(static x => x.Trim()));
    }

    public static HashSet<string> GetAllowedToolIds(IReadOnlyDictionary<string, bool>? toolMap)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (toolMap == null)
        {
            return result;
        }

        foreach (var item in toolMap)
        {
            if (item.Value && !string.IsNullOrWhiteSpace(item.Key))
            {
                result.Add(item.Key);
            }
        }

        return result;
    }

    public static bool HasCustomFeishuConfig(
        bool isEnabled,
        string? appId,
        string? appSecret,
        string? encryptKey,
        string? verificationToken,
        string? defaultCardTitle,
        string? thinkingMessage,
        string? documentAdminOpenId,
        int? httpTimeoutSeconds,
        int? streamingThrottleMs)
    {
        return isEnabled ||
               !string.IsNullOrWhiteSpace(appId) ||
               !string.IsNullOrWhiteSpace(appSecret) ||
               !string.IsNullOrWhiteSpace(encryptKey) ||
               !string.IsNullOrWhiteSpace(verificationToken) ||
               !string.IsNullOrWhiteSpace(defaultCardTitle) ||
               !string.IsNullOrWhiteSpace(thinkingMessage) ||
               !string.IsNullOrWhiteSpace(documentAdminOpenId) ||
               httpTimeoutSeconds.HasValue ||
               streamingThrottleMs.HasValue;
    }
}
