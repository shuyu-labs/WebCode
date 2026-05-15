using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Helpers;

public static class AdminUserManagementReplyTtsUiState
{
    public static AdminUserManagementReplyTtsUiStateResult Create(
        bool replyTtsEnabled,
        string? savedVoiceId,
        IReadOnlyList<FeishuReplyTtsVoiceOption>? availableVoices,
        bool platformIsAvailable,
        string? platformMessage)
    {
        var normalizedSavedVoiceId = Normalize(savedVoiceId);
        var voiceOptions = BuildVoiceOptions(availableVoices, normalizedSavedVoiceId, out var savedVoiceExistsInRuntimeList);

        return new AdminUserManagementReplyTtsUiStateResult
        {
            IsVoiceSelectorDisabled = !replyTtsEnabled || !platformIsAvailable,
            WarningMessage = BuildWarningMessage(
                replyTtsEnabled,
                normalizedSavedVoiceId,
                platformIsAvailable,
                platformMessage,
                voiceOptions.Count,
                savedVoiceExistsInRuntimeList),
            VoiceOptions = voiceOptions
        };
    }

    private static string? BuildWarningMessage(
        bool replyTtsEnabled,
        string? normalizedSavedVoiceId,
        bool platformIsAvailable,
        string? platformMessage,
        int voiceCount,
        bool savedVoiceExistsInRuntimeList)
    {
        if (!platformIsAvailable)
        {
            return string.IsNullOrWhiteSpace(platformMessage)
                ? "Feishu reply TTS is currently unavailable."
                : platformMessage.Trim();
        }

        if (!replyTtsEnabled)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(normalizedSavedVoiceId) && !savedVoiceExistsInRuntimeList)
        {
            return $"Saved Feishu reply TTS voice '{normalizedSavedVoiceId}' is unavailable. Select a different voice before saving.";
        }

        if (voiceCount == 0)
        {
            return "No Feishu reply TTS voices are available right now. Refresh to try again.";
        }

        return null;
    }

    private static List<AdminUserManagementReplyTtsVoiceOption> BuildVoiceOptions(
        IReadOnlyList<FeishuReplyTtsVoiceOption>? availableVoices,
        string? normalizedSavedVoiceId,
        out bool savedVoiceExistsInRuntimeList)
    {
        var voiceOptions = new List<AdminUserManagementReplyTtsVoiceOption>();
        var seenVoiceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        savedVoiceExistsInRuntimeList = false;

        if (availableVoices != null)
        {
            foreach (var voice in availableVoices)
            {
                var normalizedVoiceId = Normalize(voice?.VoiceId);
                if (string.IsNullOrWhiteSpace(normalizedVoiceId) || !seenVoiceIds.Add(normalizedVoiceId))
                {
                    continue;
                }

                if (string.Equals(normalizedVoiceId, normalizedSavedVoiceId, StringComparison.OrdinalIgnoreCase))
                {
                    savedVoiceExistsInRuntimeList = true;
                }

                var displayName = voice?.DisplayName;

                voiceOptions.Add(new AdminUserManagementReplyTtsVoiceOption
                {
                    VoiceId = normalizedVoiceId,
                    DisplayName = string.IsNullOrWhiteSpace(displayName)
                        ? normalizedVoiceId
                        : displayName.Trim()
                });
            }
        }

        if (!string.IsNullOrWhiteSpace(normalizedSavedVoiceId) && !savedVoiceExistsInRuntimeList)
        {
            voiceOptions.Insert(0, new AdminUserManagementReplyTtsVoiceOption
            {
                VoiceId = normalizedSavedVoiceId,
                DisplayName = $"{normalizedSavedVoiceId} (saved)"
            });
        }

        return voiceOptions;
    }

    private static string? Normalize(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }
}

public sealed class AdminUserManagementReplyTtsUiStateResult
{
    public bool IsVoiceSelectorDisabled { get; set; }

    public string? WarningMessage { get; set; }

    public IReadOnlyList<AdminUserManagementReplyTtsVoiceOption> VoiceOptions { get; set; } = [];
}

public sealed class AdminUserManagementReplyTtsVoiceOption
{
    public string VoiceId { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;
}
