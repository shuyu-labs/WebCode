using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Service;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service.Channels;
using WebCodeCli.Domain.Repositories.Base.UserFeishuBotConfig;
using FeishuNetSdk.Im.Dtos;

namespace WebCodeCli.Domain.Tests;

public sealed class FeishuAudioMessageServiceTests
{
    [Fact]
    public async Task SendAudioMessageAsync_UsesUsernameOptionsAndSendsInOrder()
    {
        var cardKit = new StubFeishuCardKitClient();
        var configService = new StubUserFeishuBotConfigService
        {
            UsernameOptions = new FeishuOptions
            {
                AppId = "user-app",
                AppSecret = "user-secret"
            }
        };
        var service = new FeishuAudioMessageService(cardKit, configService);

        var messageId = await service.SendAudioMessageAsync(
            "oc_chat",
            @"D:\audio\chunk-001.opus",
            3200,
            username: "alice",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("om_audio_success", messageId);
        Assert.Equal(["upload", "send"], cardKit.CallOrder);
        Assert.Equal("user-app", cardKit.LastUploadOptionsOverride?.AppId);
        Assert.Equal("user-app", cardKit.LastSendOptionsOverride?.AppId);
    }

    [Fact]
    public async Task SendAudioMessageAsync_FallsBackToAppIdLookup_WhenUsernameIsMissing()
    {
        var cardKit = new StubFeishuCardKitClient();
        var configService = new StubUserFeishuBotConfigService
        {
            AppOptions = new FeishuOptions
            {
                AppId = "resolved-app",
                AppSecret = "resolved-secret"
            }
        };
        var service = new FeishuAudioMessageService(cardKit, configService);

        await service.SendAudioMessageAsync(
            "oc_chat",
            @"D:\audio\chunk-001.opus",
            3200,
            appId: "cli_app",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("resolved-app", cardKit.LastUploadOptionsOverride?.AppId);
        Assert.Equal("resolved-app", cardKit.LastSendOptionsOverride?.AppId);
    }

    [Fact]
    public async Task SendAudioMessageAsync_PrefersAppIdOptions_WhenBothAppIdAndUsernameAreProvided()
    {
        var cardKit = new StubFeishuCardKitClient();
        var configService = new StubUserFeishuBotConfigService
        {
            UsernameOptions = new FeishuOptions
            {
                AppId = "user-app",
                AppSecret = "user-secret"
            },
            AppOptions = new FeishuOptions
            {
                AppId = "resolved-app",
                AppSecret = "resolved-secret"
            }
        };
        var service = new FeishuAudioMessageService(cardKit, configService);

        await service.SendAudioMessageAsync(
            "oc_chat",
            @"D:\audio\chunk-001.opus",
            3200,
            username: "alice",
            appId: "cli_app",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("resolved-app", cardKit.LastUploadOptionsOverride?.AppId);
        Assert.Equal("resolved-app", cardKit.LastSendOptionsOverride?.AppId);
    }

    [Fact]
    public async Task SendAudioMessageAsync_UsesSharedDefaults_WhenNoUsernameOrAppIdIsProvided()
    {
        var cardKit = new StubFeishuCardKitClient();
        var configService = new StubUserFeishuBotConfigService();
        var service = new FeishuAudioMessageService(cardKit, configService);

        await service.SendAudioMessageAsync(
            "oc_chat",
            @"D:\audio\chunk-001.opus",
            3200,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("shared-app", cardKit.LastUploadOptionsOverride?.AppId);
        Assert.Equal("shared-app", cardKit.LastSendOptionsOverride?.AppId);
    }

    private sealed class StubFeishuCardKitClient : IFeishuCardKitClient
    {
        public List<string> CallOrder { get; } = [];

        public FeishuOptions? LastUploadOptionsOverride { get; private set; }

        public FeishuOptions? LastSendOptionsOverride { get; private set; }

        public Task<string> UploadAudioFileAsync(string filePath, int durationMs, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            CallOrder.Add("upload");
            LastUploadOptionsOverride = optionsOverride;
            return Task.FromResult("file_v2_123");
        }

        public Task<string> SendAudioMessageAsync(string chatId, string fileKey, int durationMs, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            CallOrder.Add("send");
            LastSendOptionsOverride = optionsOverride;
            return Task.FromResult("om_audio_success");
        }

        public Task<string> CreateCardAsync(string initialContent, string? title = null, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        public Task<bool> UpdateCardAsync(string cardId, string content, int sequence, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        public Task<string> SendCardMessageAsync(string chatId, string cardId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        public Task<string> SendTextMessageAsync(string chatId, string content, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        public Task<string> ReplyCardMessageAsync(string replyMessageId, string cardId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        public Task<string> ReplyTextMessageAsync(string replyMessageId, string content, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        public Task<FeishuStreamingHandle> CreateStreamingHandleAsync(string chatId, string? replyMessageId, string initialContent, string? title = null, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null, FeishuStreamingCardChrome? chrome = null)
            => throw new NotSupportedException();

        public Task<string> SendRawCardAsync(string chatId, string cardJson, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        public Task<string> ReplyElementsCardAsync(string replyMessageId, ElementsCardV2Dto card, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        public Task<string> ReplyRawCardAsync(string replyMessageId, string cardJson, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        public Task<(byte[] Content, string FileName, string MimeType)> DownloadMessageResourceAsync(
            string messageId,
            string fileKey,
            string resourceType,
            CancellationToken cancellationToken = default,
            FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();
    }

    private sealed class StubUserFeishuBotConfigService : IUserFeishuBotConfigService
    {
        public FeishuOptions UsernameOptions { get; set; } = new();

        public FeishuOptions? AppOptions { get; set; }

        public Task<UserFeishuBotConfigEntity?> GetByUsernameAsync(string username) => Task.FromResult<UserFeishuBotConfigEntity?>(null);

        public Task<UserFeishuBotConfigEntity?> GetByAppIdAsync(string appId) => Task.FromResult<UserFeishuBotConfigEntity?>(null);

        public Task<UserFeishuBotConfigSaveResult> SaveAsync(UserFeishuBotConfigEntity config) => Task.FromResult(UserFeishuBotConfigSaveResult.Saved());

        public Task<bool> DeleteAsync(string username) => Task.FromResult(true);

        public Task<string?> FindConflictingUsernameByAppIdAsync(string username, string? appId) => Task.FromResult<string?>(null);

        public Task<List<UserFeishuBotConfigEntity>> GetAutoStartCandidatesAsync() => Task.FromResult(new List<UserFeishuBotConfigEntity>());

        public Task<bool> UpdateRuntimePreferenceAsync(string username, bool autoStartEnabled, DateTime? lastStartedAt = null) => Task.FromResult(true);

        public FeishuOptions GetSharedDefaults() => new()
        {
            AppId = "shared-app",
            AppSecret = "shared-secret"
        };

        public Task<FeishuOptions> GetEffectiveOptionsAsync(string? username) => Task.FromResult(UsernameOptions);

        public Task<FeishuOptions?> GetEffectiveOptionsByAppIdAsync(string? appId) => Task.FromResult(AppOptions);
    }
}
