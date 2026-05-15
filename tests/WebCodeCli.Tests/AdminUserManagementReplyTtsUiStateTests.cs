using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Helpers;
using Xunit;

namespace WebCodeCli.Tests;

public sealed class AdminUserManagementReplyTtsUiStateTests
{
    [Fact]
    public void Create_DisablesVoiceSelector_WhenReplyTtsIsOff()
    {
        var result = AdminUserManagementReplyTtsUiState.Create(
            replyTtsEnabled: false,
            savedVoiceId: "voice-a",
            availableVoices:
            [
                new FeishuReplyTtsVoiceOption
                {
                    VoiceId = "voice-a",
                    DisplayName = "Voice A"
                }
            ],
            platformIsAvailable: true,
            platformMessage: null);

        Assert.True(result.IsVoiceSelectorDisabled);
    }

    [Fact]
    public void Create_DisablesVoiceSelector_WhenPlatformHealthIsUnavailable()
    {
        var result = AdminUserManagementReplyTtsUiState.Create(
            replyTtsEnabled: true,
            savedVoiceId: "voice-a",
            availableVoices:
            [
                new FeishuReplyTtsVoiceOption
                {
                    VoiceId = "voice-a",
                    DisplayName = "Voice A"
                }
            ],
            platformIsAvailable: false,
            platformMessage: "Platform unavailable");

        Assert.True(result.IsVoiceSelectorDisabled);
    }

    [Fact]
    public void Create_ReturnsFallbackWarning_WhenSavedVoiceIsMissing()
    {
        var result = AdminUserManagementReplyTtsUiState.Create(
            replyTtsEnabled: true,
            savedVoiceId: "voice-missing",
            availableVoices:
            [
                new FeishuReplyTtsVoiceOption
                {
                    VoiceId = "voice-a",
                    DisplayName = "Voice A"
                }
            ],
            platformIsAvailable: true,
            platformMessage: null);

        Assert.Equal("Saved Feishu reply TTS voice 'voice-missing' is unavailable. Select a different voice before saving.", result.WarningMessage);
    }

    [Fact]
    public void Create_KeepsMissingSavedVoiceVisibleAsSyntheticOption()
    {
        var result = AdminUserManagementReplyTtsUiState.Create(
            replyTtsEnabled: true,
            savedVoiceId: "voice-missing",
            availableVoices:
            [
                new FeishuReplyTtsVoiceOption
                {
                    VoiceId = "voice-a",
                    DisplayName = "Voice A"
                }
            ],
            platformIsAvailable: true,
            platformMessage: null);

        Assert.Collection(
            result.VoiceOptions,
            voice =>
            {
                Assert.Equal("voice-missing", voice.VoiceId);
                Assert.Equal("voice-missing (saved)", voice.DisplayName);
            },
            voice =>
            {
                Assert.Equal("voice-a", voice.VoiceId);
                Assert.Equal("Voice A", voice.DisplayName);
            });
    }

    [Fact]
    public void Create_DoesNotWarnAboutMissingSavedVoice_WhenReplyTtsIsDisabled()
    {
        var result = AdminUserManagementReplyTtsUiState.Create(
            replyTtsEnabled: false,
            savedVoiceId: "voice-missing",
            availableVoices:
            [
                new FeishuReplyTtsVoiceOption
                {
                    VoiceId = "voice-a",
                    DisplayName = "Voice A"
                }
            ],
            platformIsAvailable: true,
            platformMessage: null);

        Assert.True(result.IsVoiceSelectorDisabled);
        Assert.Null(result.WarningMessage);
        Assert.Equal("voice-missing", result.VoiceOptions[0].VoiceId);
    }

    [Fact]
    public void Create_ReturnsNoWarning_WhenPlatformIsHealthyAndVoicesExist()
    {
        var result = AdminUserManagementReplyTtsUiState.Create(
            replyTtsEnabled: true,
            savedVoiceId: "voice-a",
            availableVoices:
            [
                new FeishuReplyTtsVoiceOption
                {
                    VoiceId = "voice-a",
                    DisplayName = "Voice A"
                }
            ],
            platformIsAvailable: true,
            platformMessage: "Healthy");

        Assert.Null(result.WarningMessage);
    }
}
