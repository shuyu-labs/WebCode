using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SqlSugar;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service;
using WebCodeCli.Domain.Domain.Service.Adapters;
using WebCodeCli.Domain.Domain.Service.Channels;
using WebCodeCli.Domain.Repositories.Base;
using WebCodeCli.Domain.Repositories.Base.ChatSession;
using WebCodeCli.Domain.Repositories.Base.UserFeishuBotConfig;

namespace WebCodeCli.Domain.Tests;

public class FeishuChannelServiceTests
{
    [Fact]
    public void CreateNewSession_WithCustomWorkspace_PersistsWorkspacePath()
    {
        var repository = CreateRepository(out var repositoryProxy);
        var sessionDirectoryService = new RecordingSessionDirectoryService(repositoryProxy);
        var serviceProvider = new TestServiceProvider(
            repository,
            sessionDirectoryService,
            new StubFeishuUserBindingService(),
            new StubUserFeishuBotConfigService(),
            new StubUserContextService());

        var service = new FeishuChannelService(
            Options.Create(new FeishuOptions
            {
                Enabled = true,
                AppId = "cli_test",
                AppSecret = "secret"
            }),
            NullLogger<FeishuChannelService>.Instance,
            CreateProxy<IFeishuCardKitClient>(),
            serviceProvider,
            CreateProxy<ICliExecutorService>(),
            CreateProxy<IChatSessionService>());

        var workspacePath = Path.Combine(Path.GetTempPath(), $"feishu-custom-session-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);

        try
        {
            var sessionId = service.CreateNewSession(
                new FeishuIncomingMessage
                {
                    ChatId = "oc_custom_workspace_chat",
                    SenderName = "luhaiyan"
                },
                workspacePath,
                "codex");

            var stored = repositoryProxy.GetStored(sessionId);

            Assert.Equal(workspacePath, stored.WorkspacePath);
            Assert.True(stored.IsCustomWorkspace);
            Assert.Equal("codex", stored.ToolId);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task SendMessageAsync_UsesPlainTextTransport()
    {
        var cardKit = new RecordingFeishuCardKitClient();
        var service = CreateService(cardKit);

        await service.SendMessageAsync("oc_text_chat", "已完成", "luhaiyan", "cli_text_bot");

        Assert.Equal(0, cardKit.CreateCardCallCount);
        Assert.Equal(0, cardKit.SendCardCallCount);
        Assert.Equal(1, cardKit.SendTextCallCount);
    }

    [Fact]
    public async Task ReplyMessageAsync_UsesPlainTextTransport()
    {
        var cardKit = new RecordingFeishuCardKitClient();
        var service = CreateService(cardKit);

        await service.ReplyMessageAsync("om_text_reply", "已完成", "luhaiyan", "cli_text_bot");

        Assert.Equal(0, cardKit.CreateCardCallCount);
        Assert.Equal(0, cardKit.ReplyCardCallCount);
        Assert.Equal(1, cardKit.ReplyTextCallCount);
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_SupersedesPreviousExecutionAndReusesCliThreadId()
    {
        var repository = CreateRepository(out var repositoryProxy);
        var sessionDirectoryService = new RecordingSessionDirectoryService(repositoryProxy);
        var cardKit = new StreamingRecordingFeishuCardKitClient();
        var chatSessionService = new RecordingChatSessionService();
        var workspacePath = Path.Combine(Path.GetTempPath(), $"feishu-takeover-session-{Guid.NewGuid():N}", "superpowers");
        var cliExecutor = new TakeoverCliExecutor(workspacePath);
        var serviceProvider = new TestServiceProvider(
            repository,
            sessionDirectoryService,
            new StubFeishuUserBindingService(),
            new StubUserFeishuBotConfigService(),
            new StubUserContextService());

        var service = new FeishuChannelService(
            Options.Create(new FeishuOptions
            {
                Enabled = true,
                AppId = "cli_test",
                AppSecret = "secret"
            }),
            NullLogger<FeishuChannelService>.Instance,
            cardKit,
            serviceProvider,
            cliExecutor,
            chatSessionService);

        Directory.CreateDirectory(workspacePath);

        try
        {
            var sessionId = service.CreateNewSession(
                new FeishuIncomingMessage
                {
                    ChatId = "oc_takeover_chat",
                    SenderName = "luhaiyan"
                },
                workspacePath,
                "codex");

            var firstTask = service.HandleIncomingMessageAsync(new FeishuIncomingMessage
            {
                ChatId = "oc_takeover_chat",
                SenderName = "luhaiyan",
                MessageId = "msg-1",
                Content = "鍏堟煡涓€涓?superpowers 璁″垝鏂囦欢"
            });

            await cliExecutor.ThreadIdPersisted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var secondTask = service.HandleIncomingMessageAsync(new FeishuIncomingMessage
            {
                ChatId = "oc_takeover_chat",
                SenderName = "luhaiyan",
                MessageId = "msg-2",
                Content = "琛ュ厖锛欴:\\MMIS\\Base\\Docs\\superpowers"
            });

            await Task.WhenAll(firstTask, secondTask);

            Assert.Collection(cliExecutor.ExecuteCalls,
                firstCall => Assert.Null(firstCall.ThreadIdAtStart),
                secondCall => Assert.Equal("thread-1", secondCall.ThreadIdAtStart));

            Assert.Equal("thread-1", cliExecutor.GetCliThreadId(sessionId));
            Assert.Null(cardKit.Handles[0].ReplyMessageId);
            Assert.Equal("当前回复已停止：同一会话收到了新的补充消息，请查看新卡片继续结果。", cardKit.Handles[0].FinalContent);
            Assert.Equal("补充完成", cardKit.Handles[1].FinalContent);
            Assert.Contains("已停止", cardKit.Handles[0].FinalStatusMarkdown);
            Assert.Contains("已完成", cardKit.Handles[1].FinalStatusMarkdown);
            Assert.Equal(1, cardKit.ReplyTextCallCount);
            Assert.Equal($"当前会话：superpowers  {sessionId[..8]}\n已完成", cardKit.LastReplyTextContent);
            Assert.Contains(chatSessionService.Messages[sessionId], message => message.Role == "user" && message.Content.Contains("琛ュ厖锛欴:\\MMIS\\Base\\Docs\\superpowers", StringComparison.Ordinal));
            Assert.Contains(chatSessionService.Messages[sessionId], message => message.Role == "assistant" && message.Content == "补充完成");
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_ForwardsRawPromptWithoutReplyPrefixInstructions()
    {
        var repository = CreateRepository(out var repositoryProxy);
        var sessionDirectoryService = new RecordingSessionDirectoryService(repositoryProxy);
        var cardKit = new StreamingRecordingFeishuCardKitClient();
        var chatSessionService = new RecordingChatSessionService();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-reply-prefix-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(workspaceRoot, "superpowers");
        Directory.CreateDirectory(workspacePath);
        var cliExecutor = new PromptCapturingCliExecutor(workspacePath);
        var serviceProvider = new TestServiceProvider(
            repository,
            sessionDirectoryService,
            new StubFeishuUserBindingService(),
            new StubUserFeishuBotConfigService(),
            new StubUserContextService());

        var service = new FeishuChannelService(
            Options.Create(new FeishuOptions
            {
                Enabled = true,
                AppId = "cli_test",
                AppSecret = "secret"
            }),
            NullLogger<FeishuChannelService>.Instance,
            cardKit,
            serviceProvider,
            cliExecutor,
            chatSessionService);

        try
        {
            var sessionId = service.CreateNewSession(
                new FeishuIncomingMessage
                {
                    ChatId = "oc_reply_prefix_chat",
                    SenderName = "luhaiyan"
                },
                workspacePath,
                "codex");

            await service.HandleIncomingMessageAsync(new FeishuIncomingMessage
            {
                ChatId = "oc_reply_prefix_chat",
                SenderName = "luhaiyan",
                MessageId = "msg-prefix",
                Content = @"D:\MMIS\Base\Docs\superpowers"
            });

            var call = Assert.Single(cliExecutor.ExecuteCalls);
            Assert.Equal(@"D:\MMIS\Base\Docs\superpowers", call.Prompt);
            var handle = Assert.Single(cardKit.Handles);
            Assert.Null(handle.ReplyMessageId);
            Assert.Equal("思考中...", handle.InitialContent);
            Assert.Equal("补充完成", handle.FinalContent);
            Assert.Contains("处理中", handle.InitialStatusMarkdown);
            Assert.Contains("superpowers", handle.InitialStatusMarkdown);
            Assert.Contains("已完成", handle.FinalStatusMarkdown);
            Assert.Equal(1, cardKit.ReplyTextCallCount);
            Assert.Equal($"当前会话：superpowers  {sessionId[..8]}\n已完成", cardKit.LastReplyTextContent);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_CreatesStreamingCardWithSessionOverflowMenu()
    {
        var repository = CreateRepository(out var repositoryProxy);
        var sessionDirectoryService = new RecordingSessionDirectoryService(repositoryProxy);
        var cardKit = new StreamingRecordingFeishuCardKitClient();
        var chatSessionService = new RecordingChatSessionService();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-streaming-menu-{Guid.NewGuid():N}");
        var currentWorkspace = Path.Combine(workspaceRoot, "superpowers");
        var otherWorkspace = Path.Combine(workspaceRoot, "backend");
        Directory.CreateDirectory(currentWorkspace);
        Directory.CreateDirectory(otherWorkspace);

        repositoryProxy.Store(new ChatSessionEntity
        {
            SessionId = "11111111-current",
            Username = "luhaiyan",
            Title = "MMIS-Server*",
            WorkspacePath = currentWorkspace,
            ToolId = "codex",
            FeishuChatKey = "oc_menu_chat",
            IsFeishuActive = true,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTime.UtcNow
        });

        repositoryProxy.Store(new ChatSessionEntity
        {
            SessionId = "22222222-other",
            Username = "luhaiyan",
            Title = "Backend API",
            WorkspacePath = otherWorkspace,
            ToolId = "claude-code",
            FeishuChatKey = "oc_menu_chat",
            IsFeishuActive = false,
            CreatedAt = DateTime.UtcNow.AddMinutes(-60),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
        });

        var cliExecutor = new PromptCapturingCliExecutor(currentWorkspace);
        var serviceProvider = new TestServiceProvider(
            repository,
            sessionDirectoryService,
            new StubFeishuUserBindingService(),
            new StubUserFeishuBotConfigService(),
            new StubUserContextService());

        var service = new FeishuChannelService(
            Options.Create(new FeishuOptions
            {
                Enabled = true,
                AppId = "cli_test",
                AppSecret = "secret"
            }),
            NullLogger<FeishuChannelService>.Instance,
            cardKit,
            serviceProvider,
            cliExecutor,
            chatSessionService);

        try
        {
            await service.HandleIncomingMessageAsync(new FeishuIncomingMessage
            {
                ChatId = "oc_menu_chat",
                SenderName = "luhaiyan",
                MessageId = "msg-menu",
                Content = "缁х画澶勭悊"
            });

            var handle = Assert.Single(cardKit.Handles);
            Assert.NotNull(handle.Chrome);
            var chrome = handle.Chrome!;
            Assert.Contains("当前会话：**superpowers**", chrome.StatusMarkdown);
            Assert.Contains("superpowers", chrome.StatusMarkdown);
            Assert.Contains("MMIS-Server*", chrome.StatusMarkdown);
            Assert.DoesNotContain("11111111", chrome.StatusMarkdown);
            Assert.Contains(chrome.OverflowOptions, option => option.Text.Contains("Backend API", StringComparison.Ordinal));
            Assert.Contains(chrome.OverflowOptions, option => option.Text == "模型/会话管理...");

            var switchOption = Assert.Single(chrome.OverflowOptions, option => option.Text.Contains("Backend API", StringComparison.Ordinal));
            Assert.DoesNotContain("22222222", switchOption.Text);
            var valueJson = JsonSerializer.Serialize(switchOption.Value);
            Assert.Contains("\"action\":\"switch_session\"", valueJson);
            Assert.Contains("\"session_id\":\"22222222-other\"", valueJson);
            Assert.Contains("\"chat_key\":\"oc_menu_chat\"", valueJson);

            var moreOption = Assert.Single(chrome.OverflowOptions, option => option.Text == "模型/会话管理...");
            var moreValueJson = JsonSerializer.Serialize(moreOption.Value);
            Assert.Contains("\"action\":\"open_session_manager\"", moreValueJson);
            Assert.Contains("\"send_as_new_card\":true", moreValueJson);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_NormalizesFeishuPostJsonPromptBeforePersistAndExecute()
    {
        var repository = CreateRepository(out var repositoryProxy);
        var sessionDirectoryService = new RecordingSessionDirectoryService(repositoryProxy);
        var cardKit = new StreamingRecordingFeishuCardKitClient();
        var chatSessionService = new RecordingChatSessionService();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-post-json-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(workspaceRoot, "superpowers");
        Directory.CreateDirectory(workspacePath);
        var cliExecutor = new PromptCapturingCliExecutor(workspacePath);
        var serviceProvider = new TestServiceProvider(
            repository,
            sessionDirectoryService,
            new StubFeishuUserBindingService(),
            new StubUserFeishuBotConfigService(),
            new StubUserContextService());

        var service = new FeishuChannelService(
            Options.Create(new FeishuOptions
            {
                Enabled = true,
                AppId = "cli_test",
                AppSecret = "secret"
            }),
            NullLogger<FeishuChannelService>.Instance,
            cardKit,
            serviceProvider,
            cliExecutor,
            chatSessionService);

        const string rawPostJson = """
{"zh_cn":{"title":"事务边界","content":[[{"tag":"text","text":"最终框架形态。"}],[{"tag":"text","text":"更合理的是把事务边界提升到 Application 命令层。"}],[{"tag":"text","text":"再用superpowers技能讨论下怎么实现这些内容。"}]]}}
""";
        const string expectedPrompt = """
# 事务边界

最终框架形态。
更合理的是把事务边界提升到 Application 命令层。
再用superpowers技能讨论下怎么实现这些内容。
""";

        try
        {
            var sessionId = service.CreateNewSession(
                new FeishuIncomingMessage
                {
                    ChatId = "oc_post_json_chat",
                    SenderName = "luhaiyan"
                },
                workspacePath,
                "codex");

            await service.HandleIncomingMessageAsync(new FeishuIncomingMessage
            {
                ChatId = "oc_post_json_chat",
                SenderName = "luhaiyan",
                MessageId = "msg-post-json",
                Content = rawPostJson
            });

            var call = Assert.Single(cliExecutor.ExecuteCalls);
            var normalizedExpectedPrompt = expectedPrompt
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\n", Environment.NewLine, StringComparison.Ordinal);
            Assert.Equal(normalizedExpectedPrompt, call.Prompt);
            Assert.Contains(chatSessionService.Messages[sessionId], message => message.Role == "user" && message.Content == call.Prompt);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_IncludesSessionLaunchOverridesInStatusChromeAndCompletionText()
    {
        var repository = CreateRepository(out var repositoryProxy);
        var sessionDirectoryService = new RecordingSessionDirectoryService(repositoryProxy);
        var cardKit = new StreamingRecordingFeishuCardKitClient();
        var chatSessionService = new RecordingChatSessionService();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-streaming-overrides-{Guid.NewGuid():N}");
        var currentWorkspace = Path.Combine(workspaceRoot, "superpowers");
        Directory.CreateDirectory(currentWorkspace);

        repositoryProxy.Store(new ChatSessionEntity
        {
            SessionId = "11111111-current",
            Username = "luhaiyan",
            Title = "MMIS-Server*",
            WorkspacePath = currentWorkspace,
            ToolId = "codex",
            ToolLaunchOverridesJson = SessionLaunchOverrideHelper.Serialize(
                new Dictionary<string, SessionToolLaunchOverride>(StringComparer.OrdinalIgnoreCase)
                {
                    ["codex"] = new SessionToolLaunchOverride
                    {
                        Model = "gpt-5.4",
                        ReasoningEffort = "high"
                    }
                }),
            FeishuChatKey = "oc_override_chat",
            IsFeishuActive = true,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTime.UtcNow
        });

        var cliExecutor = new PromptCapturingCliExecutor(currentWorkspace);
        var serviceProvider = new TestServiceProvider(
            repository,
            sessionDirectoryService,
            new StubFeishuUserBindingService(),
            new StubUserFeishuBotConfigService(),
            new StubUserContextService());

        var service = new FeishuChannelService(
            Options.Create(new FeishuOptions
            {
                Enabled = true,
                AppId = "cli_test",
                AppSecret = "secret"
            }),
            NullLogger<FeishuChannelService>.Instance,
            cardKit,
            serviceProvider,
            cliExecutor,
            chatSessionService);

        try
        {
            await service.HandleIncomingMessageAsync(new FeishuIncomingMessage
            {
                ChatId = "oc_override_chat",
                SenderName = "luhaiyan",
                MessageId = "msg-override",
                Content = "继续处理"
            });

            var handle = Assert.Single(cardKit.Handles);
            Assert.Contains("🤖 模型: `gpt-5.4`", handle.InitialStatusMarkdown);
            Assert.Contains("🧠 思考: `high`", handle.InitialStatusMarkdown);
            Assert.Contains("🤖 模型: `gpt-5.4`", handle.FinalStatusMarkdown);
            Assert.Contains("🧠 思考: `high`", handle.FinalStatusMarkdown);
            var initialChrome = Assert.IsType<FeishuStreamingCardChrome>(handle.InitialChromeSnapshot);
            Assert.Contains(initialChrome.OverflowOptions, item => item.Text == "模型：gpt-5.4");
            Assert.Collection(initialChrome.TopChipGroups,
                hintGroup =>
                {
                    Assert.Equal("switch_hint", hintGroup.Kind);
                    Assert.False(hintGroup.IsEnabled);
                    Assert.False(string.IsNullOrWhiteSpace(hintGroup.SummaryMarkdown));
                },
                reasoningGroup =>
                {
                    Assert.Equal("reasoning_effort", reasoningGroup.Kind);
                    Assert.False(reasoningGroup.IsEnabled);
                    Assert.All(reasoningGroup.Items, item => Assert.False(item.IsEnabled));
                    Assert.Contains(reasoningGroup.Items, item => item.Text == "high" && item.IsActive);
                });
            var finalChrome = Assert.IsType<FeishuStreamingCardChrome>(handle.FinalChromeSnapshot);
            Assert.All(finalChrome.TopChipGroups, group => Assert.True(group.IsEnabled));
            Assert.All(finalChrome.TopChipGroups.SelectMany(group => group.Items), item => Assert.True(item.IsEnabled));
            Assert.Contains("🤖 模型: `gpt-5.4`", cardKit.LastReplyTextContent);
            Assert.Contains("🧠 思考: `high`", cardKit.LastReplyTextContent);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_HighlightsCurrentLaunchStateFromCodexProjectConfig()
    {
        var repository = CreateRepository(out var repositoryProxy);
        var sessionDirectoryService = new RecordingSessionDirectoryService(repositoryProxy);
        var cardKit = new StreamingRecordingFeishuCardKitClient();
        var chatSessionService = new RecordingChatSessionService();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-streaming-config-state-{Guid.NewGuid():N}");
        var currentWorkspace = Path.Combine(workspaceRoot, "superpowers");
        var codexDirectory = Path.Combine(currentWorkspace, ".codex");
        Directory.CreateDirectory(codexDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(codexDirectory, "config.toml.base"),
            "model = \"gpt-5.4\"\nmodel_reasoning_effort = \"medium\"\n");

        repositoryProxy.Store(new ChatSessionEntity
        {
            SessionId = "11111111-current",
            Username = "luhaiyan",
            Title = "MMIS-Server*",
            WorkspacePath = currentWorkspace,
            ToolId = "codex",
            FeishuChatKey = "oc_config_state_chat",
            IsFeishuActive = true,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTime.UtcNow
        });

        var cliExecutor = new PromptCapturingCliExecutor(currentWorkspace);
        var serviceProvider = new TestServiceProvider(
            repository,
            sessionDirectoryService,
            new StubFeishuUserBindingService(),
            new StubUserFeishuBotConfigService(),
            new StubUserContextService());

        var service = new FeishuChannelService(
            Options.Create(new FeishuOptions
            {
                Enabled = true,
                AppId = "cli_test",
                AppSecret = "secret"
            }),
            NullLogger<FeishuChannelService>.Instance,
            cardKit,
            serviceProvider,
            cliExecutor,
            chatSessionService);

        try
        {
            await service.HandleIncomingMessageAsync(new FeishuIncomingMessage
            {
                ChatId = "oc_config_state_chat",
                SenderName = "luhaiyan",
                MessageId = "msg-config-state",
                Content = "继续处理"
            });

            var handle = Assert.Single(cardKit.Handles);
            var initialChrome = Assert.IsType<FeishuStreamingCardChrome>(handle.InitialChromeSnapshot);
            Assert.DoesNotContain(initialChrome.TopChipGroups, group => group.Kind == "model");
            Assert.Contains(initialChrome.TopChipGroups, group => group.Kind == "switch_hint" && !string.IsNullOrWhiteSpace(group.SummaryMarkdown));
            Assert.Contains(initialChrome.OverflowOptions, item => item.Text == "模型：gpt-5.4");
            Assert.Contains(initialChrome.TopChipGroups.SelectMany(group => group.Items), item => item.Text == "medium" && item.IsActive);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_WithSessionOverflowMenu_SuppressesPulseWithinQuietWindow()
    {
        var repository = CreateRepository(out var repositoryProxy);
        var sessionDirectoryService = new RecordingSessionDirectoryService(repositoryProxy);
        var cardKit = new StreamingRecordingFeishuCardKitClient();
        var chatSessionService = new RecordingChatSessionService();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-streaming-pulse-{Guid.NewGuid():N}");
        var currentWorkspace = Path.Combine(workspaceRoot, "superpowers");
        var otherWorkspace = Path.Combine(workspaceRoot, "backend");
        Directory.CreateDirectory(currentWorkspace);
        Directory.CreateDirectory(otherWorkspace);

        repositoryProxy.Store(new ChatSessionEntity
        {
            SessionId = "11111111-current",
            Username = "luhaiyan",
            Title = "MMIS-Server*",
            WorkspacePath = currentWorkspace,
            ToolId = "codex",
            FeishuChatKey = "oc_pulse_chat",
            IsFeishuActive = true,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTime.UtcNow
        });

        repositoryProxy.Store(new ChatSessionEntity
        {
            SessionId = "22222222-other",
            Username = "luhaiyan",
            Title = "Backend API",
            WorkspacePath = otherWorkspace,
            ToolId = "claude-code",
            FeishuChatKey = "oc_pulse_chat",
            IsFeishuActive = false,
            CreatedAt = DateTime.UtcNow.AddMinutes(-60),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
        });

        var cliExecutor = new TakeoverCliExecutor(currentWorkspace);
        var serviceProvider = new TestServiceProvider(
            repository,
            sessionDirectoryService,
            new StubFeishuUserBindingService(),
            new StubUserFeishuBotConfigService(),
            new StubUserContextService());

        var service = new FeishuChannelService(
            Options.Create(new FeishuOptions
            {
                Enabled = true,
                AppId = "cli_test",
                AppSecret = "secret"
            }),
            NullLogger<FeishuChannelService>.Instance,
            cardKit,
            serviceProvider,
            cliExecutor,
            chatSessionService);

        try
        {
            var firstTask = service.HandleIncomingMessageAsync(new FeishuIncomingMessage
            {
                ChatId = "oc_pulse_chat",
                SenderName = "luhaiyan",
                MessageId = "msg-pulse-1",
                Content = "先查一下 superpowers 计划文件"
            });

            await cliExecutor.ThreadIdPersisted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(1300, TestContext.Current.CancellationToken);

            var firstHandle = Assert.Single(cardKit.Handles);
            Assert.Single(firstHandle.Updates);
            Assert.Single(firstHandle.StatusMarkdownSnapshots.Distinct(StringComparer.Ordinal));

            var secondTask = service.HandleIncomingMessageAsync(new FeishuIncomingMessage
            {
                ChatId = "oc_pulse_chat",
                SenderName = "luhaiyan",
                MessageId = "msg-pulse-2",
                Content = @"补充：D:\MMIS\Base\Docs\superpowers"
            });

            await Task.WhenAll(firstTask, secondTask);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_WithSessionOverflowMenu_ResumesPulseAfterQuietWindow()
    {
        var repository = CreateRepository(out var repositoryProxy);
        var sessionDirectoryService = new RecordingSessionDirectoryService(repositoryProxy);
        var cardKit = new StreamingRecordingFeishuCardKitClient();
        var chatSessionService = new RecordingChatSessionService();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-streaming-pulse-resume-{Guid.NewGuid():N}");
        var currentWorkspace = Path.Combine(workspaceRoot, "superpowers");
        var otherWorkspace = Path.Combine(workspaceRoot, "backend");
        Directory.CreateDirectory(currentWorkspace);
        Directory.CreateDirectory(otherWorkspace);

        repositoryProxy.Store(new ChatSessionEntity
        {
            SessionId = "11111111-current",
            Username = "luhaiyan",
            Title = "MMIS-Server*",
            WorkspacePath = currentWorkspace,
            ToolId = "codex",
            FeishuChatKey = "oc_pulse_resume_chat",
            IsFeishuActive = true,
            CreatedAt = DateTime.UtcNow.AddMinutes(-30),
            UpdatedAt = DateTime.UtcNow
        });

        repositoryProxy.Store(new ChatSessionEntity
        {
            SessionId = "22222222-other",
            Username = "luhaiyan",
            Title = "Backend API",
            WorkspacePath = otherWorkspace,
            ToolId = "claude-code",
            FeishuChatKey = "oc_pulse_resume_chat",
            IsFeishuActive = false,
            CreatedAt = DateTime.UtcNow.AddMinutes(-60),
            UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
        });

        var cliExecutor = new TakeoverCliExecutor(currentWorkspace);
        var serviceProvider = new TestServiceProvider(
            repository,
            sessionDirectoryService,
            new StubFeishuUserBindingService(),
            new StubUserFeishuBotConfigService(),
            new StubUserContextService());

        var service = new FeishuChannelService(
            Options.Create(new FeishuOptions
            {
                Enabled = true,
                AppId = "cli_test",
                AppSecret = "secret"
            }),
            NullLogger<FeishuChannelService>.Instance,
            cardKit,
            serviceProvider,
            cliExecutor,
            chatSessionService);

        try
        {
            var firstTask = service.HandleIncomingMessageAsync(new FeishuIncomingMessage
            {
                ChatId = "oc_pulse_resume_chat",
                SenderName = "luhaiyan",
                MessageId = "msg-pulse-resume-1",
                Content = "先查一下 superpowers 计划文件"
            });

            await cliExecutor.ThreadIdPersisted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(4200, TestContext.Current.CancellationToken);

            var firstHandle = Assert.Single(cardKit.Handles);
            Assert.True(firstHandle.Updates.Count > 1);
            Assert.True(firstHandle.StatusMarkdownSnapshots.Distinct(StringComparer.Ordinal).Count() > 1);

            var secondTask = service.HandleIncomingMessageAsync(new FeishuIncomingMessage
            {
                ChatId = "oc_pulse_resume_chat",
                SenderName = "luhaiyan",
                MessageId = "msg-pulse-resume-2",
                Content = @"补充：D:\MMIS\Base\Docs\superpowers"
            });

            await Task.WhenAll(firstTask, secondTask);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_QueuesReplyTtsAfterSuccessfulCompletionAndAssistantPersistence()
    {
        var repository = CreateRepository(out var repositoryProxy);
        var sessionDirectoryService = new RecordingSessionDirectoryService(repositoryProxy);
        var cardKit = new StreamingRecordingFeishuCardKitClient();
        var chatSessionService = new RecordingChatSessionService();
        var replyTtsOrchestrator = new RecordingReplyTtsOrchestrator();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-reply-tts-success-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(workspaceRoot, "superpowers");
        Directory.CreateDirectory(workspacePath);
        var cliExecutor = new PromptCapturingCliExecutor(workspacePath);
        var serviceProvider = new TestServiceProvider(
            repository,
            sessionDirectoryService,
            new StubFeishuUserBindingService(),
            new StubUserFeishuBotConfigService(),
            new StubUserContextService());

        var service = new FeishuChannelService(
            Options.Create(new FeishuOptions
            {
                Enabled = true,
                AppId = "cli_test",
                AppSecret = "secret"
            }),
            NullLogger<FeishuChannelService>.Instance,
            cardKit,
            serviceProvider,
            cliExecutor,
            chatSessionService,
            replyTtsOrchestrator);

        try
        {
            var sessionId = service.CreateNewSession(
                new FeishuIncomingMessage
                {
                    ChatId = "oc_reply_tts_chat",
                    SenderName = "luhaiyan"
                },
                workspacePath,
                "codex");

            replyTtsOrchestrator.OnQueued = request =>
            {
                Assert.Equal("补充完成", request.Output);
                Assert.Contains(
                    chatSessionService.Messages[sessionId],
                    message => message.Role == "assistant" && message.Content == "补充完成" && message.IsCompleted);
                Assert.Equal("补充完成", Assert.Single(cardKit.Handles).FinalContent);
                return Task.CompletedTask;
            };

            await service.HandleIncomingMessageAsync(new FeishuIncomingMessage
            {
                ChatId = "oc_reply_tts_chat",
                SenderName = "luhaiyan",
                AppId = "cli_test",
                MessageId = "msg-reply-tts",
                Content = "继续"
            });

            var queued = await replyTtsOrchestrator.WhenQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("oc_reply_tts_chat", queued.ChatId);
            Assert.Equal("luhaiyan", queued.Username);
            Assert.Equal("cli_test", queued.AppId);
            Assert.Equal("补充完成", queued.Output);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_DoesNotQueueReplyTtsWhenExecutionErrors()
    {
        var repository = CreateRepository(out var repositoryProxy);
        var sessionDirectoryService = new RecordingSessionDirectoryService(repositoryProxy);
        var cardKit = new StreamingRecordingFeishuCardKitClient();
        var chatSessionService = new RecordingChatSessionService();
        var replyTtsOrchestrator = new RecordingReplyTtsOrchestrator();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-reply-tts-error-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(workspaceRoot, "superpowers");
        Directory.CreateDirectory(workspacePath);
        var cliExecutor = new ErrorCliExecutor(workspacePath);
        var serviceProvider = new TestServiceProvider(
            repository,
            sessionDirectoryService,
            new StubFeishuUserBindingService(),
            new StubUserFeishuBotConfigService(),
            new StubUserContextService());

        var service = new FeishuChannelService(
            Options.Create(new FeishuOptions
            {
                Enabled = true,
                AppId = "cli_test",
                AppSecret = "secret"
            }),
            NullLogger<FeishuChannelService>.Instance,
            cardKit,
            serviceProvider,
            cliExecutor,
            chatSessionService,
            replyTtsOrchestrator);

        try
        {
            service.CreateNewSession(
                new FeishuIncomingMessage
                {
                    ChatId = "oc_reply_tts_error_chat",
                    SenderName = "luhaiyan"
                },
                workspacePath,
                "codex");

            await service.HandleIncomingMessageAsync(new FeishuIncomingMessage
            {
                ChatId = "oc_reply_tts_error_chat",
                SenderName = "luhaiyan",
                MessageId = "msg-reply-tts-error",
                Content = "继续"
            });

            Assert.Empty(replyTtsOrchestrator.Requests);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_DoesNotQueueReplyTtsForSupersededExecution()
    {
        var repository = CreateRepository(out var repositoryProxy);
        var sessionDirectoryService = new RecordingSessionDirectoryService(repositoryProxy);
        var cardKit = new StreamingRecordingFeishuCardKitClient();
        var chatSessionService = new RecordingChatSessionService();
        var replyTtsOrchestrator = new RecordingReplyTtsOrchestrator();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-reply-tts-superseded-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(workspaceRoot, "superpowers");
        Directory.CreateDirectory(workspacePath);
        var cliExecutor = new TakeoverCliExecutor(workspacePath);
        var serviceProvider = new TestServiceProvider(
            repository,
            sessionDirectoryService,
            new StubFeishuUserBindingService(),
            new StubUserFeishuBotConfigService(),
            new StubUserContextService());

        var service = new FeishuChannelService(
            Options.Create(new FeishuOptions
            {
                Enabled = true,
                AppId = "cli_test",
                AppSecret = "secret"
            }),
            NullLogger<FeishuChannelService>.Instance,
            cardKit,
            serviceProvider,
            cliExecutor,
            chatSessionService,
            replyTtsOrchestrator);

        try
        {
            service.CreateNewSession(
                new FeishuIncomingMessage
                {
                    ChatId = "oc_reply_tts_superseded_chat",
                    SenderName = "luhaiyan"
                },
                workspacePath,
                "codex");

            var firstTask = service.HandleIncomingMessageAsync(new FeishuIncomingMessage
            {
                ChatId = "oc_reply_tts_superseded_chat",
                SenderName = "luhaiyan",
                MessageId = "msg-reply-tts-superseded-1",
                Content = "先查一下 superpowers 计划文件"
            });

            await cliExecutor.ThreadIdPersisted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var secondTask = service.HandleIncomingMessageAsync(new FeishuIncomingMessage
            {
                ChatId = "oc_reply_tts_superseded_chat",
                SenderName = "luhaiyan",
                MessageId = "msg-reply-tts-superseded-2",
                Content = @"补充：D:\MMIS\Base\Docs\superpowers"
            });

            await Task.WhenAll(firstTask, secondTask);

            var queued = Assert.Single(replyTtsOrchestrator.Requests);
            Assert.Equal("补充完成", queued.Output);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_AttachesSuperpowersQuickActions_WhenPlanFilesExistAndSessionHistoryContainsSuperpowers()
    {
        var repository = CreateRepository(out var repositoryProxy);
        var sessionDirectoryService = new RecordingSessionDirectoryService(repositoryProxy);
        var cardKit = new StreamingRecordingFeishuCardKitClient();
        var chatSessionService = new RecordingChatSessionService();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-superpowers-footer-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(workspaceRoot, "superpowers");
        Directory.CreateDirectory(Path.Combine(workspacePath, "docs", "superpowers", "plans"));
        await File.WriteAllTextAsync(
            Path.Combine(workspacePath, "docs", "superpowers", "plans", "approved-plan.md"),
            "# approved");

        const string sessionId = "33333333-low";
        repositoryProxy.Store(new ChatSessionEntity
        {
            SessionId = sessionId,
            Username = "luhaiyan",
            WorkspacePath = workspacePath,
            ToolId = "codex",
            FeishuChatKey = "oc_low_interrupt_chat",
            IsFeishuActive = true,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow
        });

        var cliExecutor = new PromptCapturingCliExecutor(workspacePath)
        {
            FinalContent = "计划已完成\n"
        };

        var serviceProvider = new TestServiceProvider(
            repository,
            sessionDirectoryService,
            new StubFeishuUserBindingService(),
            new StubUserFeishuBotConfigService(),
            new StubUserContextService());

        var service = new FeishuChannelService(
            Options.Create(new FeishuOptions
            {
                Enabled = true,
                AppId = "cli_test",
                AppSecret = "secret"
            }),
            NullLogger<FeishuChannelService>.Instance,
            cardKit,
            serviceProvider,
            cliExecutor,
            chatSessionService);

        try
        {
            chatSessionService.Messages[sessionId] =
            [
                new ChatMessage
                {
                    Role = "assistant",
                    Content = "可以继续用superpowers技能推进",
                    IsCompleted = true
                }
            ];

            await service.HandleIncomingMessageAsync(new FeishuIncomingMessage
            {
                ChatId = "oc_low_interrupt_chat",
                SenderName = "luhaiyan",
                MessageId = "msg-low-interrupt",
                Content = "继续"
            });

            var handle = Assert.Single(cardKit.Handles);
            Assert.NotNull(handle.Chrome);
            var chrome = handle.Chrome!;
            Assert.NotNull(chrome.BottomPrompt);
            Assert.Equal(SuperpowersQuickActionDefaults.QuickInputFieldName, chrome.BottomPrompt!.InputName);
            Assert.Equal(SuperpowersQuickActionDefaults.InstructionText, chrome.BottomPrompt.InputLabel);

            var quickInputJson = JsonSerializer.Serialize(chrome.BottomPrompt.Value);
            Assert.Contains($"\"action\":\"{FeishuHelpCardAction.SubmitSuperpowersQuickInputAction}\"", quickInputJson);
            Assert.Contains($"\"session_id\":\"{sessionId}\"", quickInputJson);
            Assert.Contains("\"chat_key\":\"oc_low_interrupt_chat\"", quickInputJson);
            Assert.Contains("\"tool_id\":\"codex\"", quickInputJson);

            var goalPrompt = Assert.Single(chrome.AdditionalBottomPrompts);
            Assert.Equal(GoalQuickActionDefaults.QuickInputFieldName, goalPrompt.InputName);
            Assert.Equal(GoalQuickActionDefaults.InstructionText, goalPrompt.InputLabel);
            var goalInputJson = JsonSerializer.Serialize(goalPrompt.Value);
            Assert.Contains($"\"action\":\"{FeishuHelpCardAction.SubmitGoalQuickInputAction}\"", goalInputJson);
            Assert.Contains($"\"session_id\":\"{sessionId}\"", goalInputJson);
            Assert.Contains("\"chat_key\":\"oc_low_interrupt_chat\"", goalInputJson);
            Assert.Contains("\"tool_id\":\"codex\"", goalInputJson);

            Assert.Equal(3, chrome.BottomActions.Count);
            Assert.Contains(chrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.ContinueButtonText);
            Assert.Contains(chrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.ExecutePlanButtonText);
            Assert.Contains(chrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.ExecuteSubagentPlanButtonText);
            Assert.Contains(
                $"\"action\":\"{FeishuHelpCardAction.ContinueSuperpowersAction}\"",
                JsonSerializer.Serialize(
                    Assert.Single(chrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.ContinueButtonText).Value),
                StringComparison.Ordinal);
            Assert.Contains(
                $"\"action\":\"{FeishuHelpCardAction.ExecuteSuperpowersPlanAction}\"",
                JsonSerializer.Serialize(
                    Assert.Single(chrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.ExecutePlanButtonText).Value),
                StringComparison.Ordinal);
            Assert.Contains(
                $"\"action\":\"{FeishuHelpCardAction.ExecuteSuperpowersSubagentPlanAction}\"",
                JsonSerializer.Serialize(
                    Assert.Single(chrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.ExecuteSubagentPlanButtonText).Value),
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_AttachesQuickInputAndKeepsContinueAction_WhenWorkspaceHasNoPlanFiles()
    {
        var repository = CreateRepository(out var repositoryProxy);
        var sessionDirectoryService = new RecordingSessionDirectoryService(repositoryProxy);
        var cardKit = new StreamingRecordingFeishuCardKitClient();
        var chatSessionService = new RecordingChatSessionService();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-superpowers-no-plan-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(workspaceRoot, "superpowers");
        Directory.CreateDirectory(workspacePath);

        const string sessionId = "33333333-no-plan";
        repositoryProxy.Store(new ChatSessionEntity
        {
            SessionId = sessionId,
            Username = "luhaiyan",
            WorkspacePath = workspacePath,
            ToolId = "codex",
            FeishuChatKey = "oc_low_interrupt_chat",
            IsFeishuActive = true,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow
        });

        var cliExecutor = new PromptCapturingCliExecutor(workspacePath)
        {
            FinalContent = "计划已完成\n"
        };

        var serviceProvider = new TestServiceProvider(
            repository,
            sessionDirectoryService,
            new StubFeishuUserBindingService(),
            new StubUserFeishuBotConfigService(),
            new StubUserContextService());

        var service = new FeishuChannelService(
            Options.Create(new FeishuOptions
            {
                Enabled = true,
                AppId = "cli_test",
                AppSecret = "secret"
            }),
            NullLogger<FeishuChannelService>.Instance,
            cardKit,
            serviceProvider,
            cliExecutor,
            chatSessionService);

        try
        {
            chatSessionService.Messages[sessionId] =
            [
                new ChatMessage
                {
                    Role = "assistant",
                    Content = "可以继续用superpowers技能推进",
                    IsCompleted = true
                }
            ];

            await service.HandleIncomingMessageAsync(new FeishuIncomingMessage
            {
                ChatId = "oc_low_interrupt_chat",
                SenderName = "luhaiyan",
                MessageId = "msg-no-plan",
                Content = "继续"
            });

            var handle = Assert.Single(cardKit.Handles);
            Assert.NotNull(handle.Chrome);
            Assert.NotNull(handle.Chrome!.BottomPrompt);
            Assert.Equal(SuperpowersQuickActionDefaults.QuickInputFieldName, handle.Chrome.BottomPrompt!.InputName);
            var continueAction = Assert.Single(handle.Chrome.BottomActions);
            Assert.Equal(SuperpowersQuickActionDefaults.ContinueButtonText, continueAction.Text);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleIncomingMessageAsync_ShowsRetryAction_WhenCachedSuperpowersCapabilityIsUnavailable()
    {
        var repository = CreateRepository(out var repositoryProxy);
        var sessionDirectoryService = new RecordingSessionDirectoryService(repositoryProxy);
        var cardKit = new StreamingRecordingFeishuCardKitClient();
        var chatSessionService = new RecordingChatSessionService();
        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-superpowers-unavailable-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(workspaceRoot, "superpowers");
        Directory.CreateDirectory(Path.Combine(workspacePath, "docs", "superpowers", "plans"));
        await File.WriteAllTextAsync(
            Path.Combine(workspacePath, "docs", "superpowers", "plans", "approved-plan.md"),
            "# approved");

        const string sessionId = "33333333-unavailable";
        repositoryProxy.Store(new ChatSessionEntity
        {
            SessionId = sessionId,
            Username = "luhaiyan",
            WorkspacePath = workspacePath,
            ToolId = "codex",
            FeishuChatKey = "oc_low_interrupt_chat",
            IsFeishuActive = true,
            CreatedAt = DateTime.UtcNow.AddMinutes(-10),
            UpdatedAt = DateTime.UtcNow
        });

        var cliExecutor = new PromptCapturingCliExecutor(workspacePath)
        {
            FinalContent = "计划已完成\n"
        };
        var capabilityService = new StubSuperpowersCapabilityService
        {
            CachedState = SuperpowersCapabilityState.Unavailable,
            CachedMessage = SuperpowersQuickActionDefaults.CapabilityUnavailableText
        };

        var serviceProvider = new TestServiceProvider(
            repository,
            sessionDirectoryService,
            new StubFeishuUserBindingService(),
            new StubUserFeishuBotConfigService(),
            new StubUserContextService(),
            capabilityService);

        var service = new FeishuChannelService(
            Options.Create(new FeishuOptions
            {
                Enabled = true,
                AppId = "cli_test",
                AppSecret = "secret"
            }),
            NullLogger<FeishuChannelService>.Instance,
            cardKit,
            serviceProvider,
            cliExecutor,
            chatSessionService);

        try
        {
            chatSessionService.Messages[sessionId] =
            [
                new ChatMessage
                {
                    Role = "assistant",
                    Content = "可以继续用superpowers技能推进",
                    IsCompleted = true
                }
            ];

            await service.HandleIncomingMessageAsync(new FeishuIncomingMessage
            {
                ChatId = "oc_low_interrupt_chat",
                SenderName = "luhaiyan",
                MessageId = "msg-low-unavailable",
                Content = "继续"
            });

            var handle = Assert.Single(cardKit.Handles);
            var chrome = Assert.IsType<FeishuStreamingCardChrome>(handle.Chrome);
            Assert.Null(chrome.BottomPrompt);
            var retryAction = Assert.Single(chrome.BottomActions);
            Assert.Equal(SuperpowersQuickActionDefaults.CapabilityRetryButtonText, retryAction.Text);
            Assert.Contains(SuperpowersQuickActionDefaults.CapabilityUnavailableText, chrome.StatusMarkdown, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    private static FeishuChannelService CreateService(IFeishuCardKitClient? cardKit = null)
    {
        var repository = CreateRepository(out _);
        var sessionDirectoryService = new RecordingSessionDirectoryService(new ChatSessionRepositoryProxy());
        var serviceProvider = new TestServiceProvider(
            repository,
            sessionDirectoryService,
            new StubFeishuUserBindingService(),
            new StubUserFeishuBotConfigService(),
            new StubUserContextService());

        return new FeishuChannelService(
            Options.Create(new FeishuOptions
            {
                Enabled = true,
                AppId = "cli_test",
                AppSecret = "secret"
            }),
            NullLogger<FeishuChannelService>.Instance,
            cardKit ?? new RecordingFeishuCardKitClient(),
            serviceProvider,
            CreateProxy<ICliExecutorService>(),
            CreateProxy<IChatSessionService>());
    }

    private static IChatSessionRepository CreateRepository(out ChatSessionRepositoryProxy proxy)
    {
        var repository = DispatchProxy.Create<IChatSessionRepository, ChatSessionRepositoryProxy>();
        proxy = (ChatSessionRepositoryProxy)(object)repository;
        return repository;
    }

    private static T CreateProxy<T>() where T : class
    {
        return DispatchProxy.Create<T, DefaultInterfaceProxy<T>>();
    }

    private sealed class TestServiceProvider(
        IChatSessionRepository chatSessionRepository,
        ISessionDirectoryService sessionDirectoryService,
        IFeishuUserBindingService feishuUserBindingService,
        IUserFeishuBotConfigService userFeishuBotConfigService,
        IUserContextService userContextService,
        ISuperpowersCapabilityService? superpowersCapabilityService = null,
        IGoalCapabilityService? goalCapabilityService = null) : IServiceProvider, IServiceScopeFactory, IServiceScope
    {
        private readonly ISuperpowersCapabilityService _superpowersCapabilityService = superpowersCapabilityService ?? new StubSuperpowersCapabilityService();
        private readonly IGoalCapabilityService _goalCapabilityService = goalCapabilityService ?? new StubGoalCapabilityService();

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IServiceScopeFactory))
            {
                return this;
            }

            if (serviceType == typeof(IChatSessionRepository))
            {
                return chatSessionRepository;
            }

            if (serviceType == typeof(ISessionDirectoryService))
            {
                return sessionDirectoryService;
            }

            if (serviceType == typeof(IFeishuUserBindingService))
            {
                return feishuUserBindingService;
            }

            if (serviceType == typeof(IUserFeishuBotConfigService))
            {
                return userFeishuBotConfigService;
            }

            if (serviceType == typeof(IUserContextService))
            {
                return userContextService;
            }

            if (serviceType == typeof(ISuperpowersCapabilityService))
            {
                return _superpowersCapabilityService;
            }

            if (serviceType == typeof(IGoalCapabilityService))
            {
                return _goalCapabilityService;
            }

            return null;
        }

        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public void Dispose()
        {
        }
    }

    private sealed class StubSuperpowersCapabilityService : ISuperpowersCapabilityService
    {
        public SuperpowersCapabilityState CachedState { get; set; } = SuperpowersCapabilityState.Unknown;

        public string? CachedMessage { get; set; }

        public Task<SuperpowersCapabilitySnapshot> GetStateAsync(
            SuperpowersCapabilityContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<SuperpowersCapabilitySnapshot>(new SuperpowersCapabilitySnapshot
            {
                ToolId = context.ToolId,
                ProviderId = context.ProviderId ?? SuperpowersCapabilityService.UnscopedProviderId,
                CacheKey = $"{context.ToolId}::{context.ProviderId ?? SuperpowersCapabilityService.UnscopedProviderId}",
                State = CachedState,
                Message = CachedMessage
            });
        }

        public Task<SuperpowersCapabilityProbeResult> ProbeAsync(
            SuperpowersCapabilityContext context,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SuperpowersCapabilityProbeResult
            {
                ToolId = context.ToolId,
                ProviderId = context.ProviderId ?? SuperpowersCapabilityService.UnscopedProviderId,
                CacheKey = $"{context.ToolId}::{context.ProviderId ?? SuperpowersCapabilityService.UnscopedProviderId}",
                State = SuperpowersCapabilityState.Available,
                Outcome = SuperpowersCapabilityProbeOutcome.Available
            });
        }
    }

    private sealed class StubGoalCapabilityService : IGoalCapabilityService
    {
        public GoalCapabilityState CachedState { get; set; } = GoalCapabilityState.Unknown;

        public string? CachedMessage { get; set; }

        public Task<GoalCapabilitySnapshot> GetStateAsync(
            GoalCapabilityContext context,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<GoalCapabilitySnapshot>(new GoalCapabilitySnapshot
            {
                ToolId = context.ToolId,
                ProviderId = context.ProviderId ?? GoalCapabilityService.UnscopedProviderId,
                CacheKey = $"{context.ToolId}::{context.ProviderId ?? GoalCapabilityService.UnscopedProviderId}",
                State = CachedState,
                Message = CachedMessage
            });
        }

        public Task<GoalCapabilityProbeResult> ProbeAsync(
            GoalCapabilityContext context,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new GoalCapabilityProbeResult
            {
                ToolId = context.ToolId,
                ProviderId = context.ProviderId ?? GoalCapabilityService.UnscopedProviderId,
                CacheKey = $"{context.ToolId}::{context.ProviderId ?? GoalCapabilityService.UnscopedProviderId}",
                State = GoalCapabilityState.Available,
                Outcome = GoalCapabilityProbeOutcome.Available
            });
        }
    }

    private sealed class StubUserContextService : IUserContextService
    {
        public string GetCurrentUsername() => "luhaiyan";

        public string GetCurrentRole() => "admin";

        public bool IsAuthenticated() => true;

        public void SetCurrentUsername(string username)
        {
        }
    }

    private sealed class StubFeishuUserBindingService : IFeishuUserBindingService
    {
        public Task<string?> GetBoundWebUsernameAsync(string feishuUserId) => Task.FromResult<string?>(null);

        public Task<bool> IsBoundAsync(string feishuUserId) => Task.FromResult(true);

        public Task<(bool Success, string? ErrorMessage, string? WebUsername)> BindAsync(string feishuUserId, string webUsername, string? appId = null)
            => Task.FromResult<(bool Success, string? ErrorMessage, string? WebUsername)>((true, null, webUsername));

        public Task<bool> UnbindAsync(string feishuUserId) => Task.FromResult(true);

        public Task<List<string>> GetBindableWebUsernamesAsync(string? appId = null)
            => Task.FromResult(new List<string> { "luhaiyan" });

        public Task<HashSet<string>> GetAllBoundWebUsernamesAsync()
            => Task.FromResult(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "luhaiyan" });
    }

    private sealed class StubUserFeishuBotConfigService : IUserFeishuBotConfigService
    {
        public Task<UserFeishuBotConfigEntity?> GetByUsernameAsync(string username) => Task.FromResult<UserFeishuBotConfigEntity?>(null);

        public Task<UserFeishuBotConfigEntity?> GetByAppIdAsync(string appId) => Task.FromResult<UserFeishuBotConfigEntity?>(null);

        public Task<UserFeishuBotConfigSaveResult> SaveAsync(UserFeishuBotConfigEntity config) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(string username) => Task.FromResult(true);

        public Task<string?> FindConflictingUsernameByAppIdAsync(string username, string? appId) => Task.FromResult<string?>(null);

        public Task<List<UserFeishuBotConfigEntity>> GetAutoStartCandidatesAsync()
            => Task.FromResult(new List<UserFeishuBotConfigEntity>());

        public Task<bool> UpdateRuntimePreferenceAsync(string username, bool autoStartEnabled, DateTime? lastStartedAt = null)
            => Task.FromResult(true);

        public FeishuOptions GetSharedDefaults() => new()
        {
            Enabled = true,
            AppId = "shared-app-id",
            AppSecret = "shared-secret",
            DefaultCardTitle = "AI助手",
            ThinkingMessage = "思考中..."
        };

        public Task<FeishuOptions> GetEffectiveOptionsAsync(string? username) => Task.FromResult(GetSharedDefaults());

        public Task<FeishuOptions?> GetEffectiveOptionsByAppIdAsync(string? appId)
            => Task.FromResult<FeishuOptions?>(string.IsNullOrWhiteSpace(appId)
                ? null
                : new FeishuOptions
                {
                    Enabled = true,
                    AppId = appId,
                    AppSecret = "bot-secret",
                    DefaultCardTitle = "AI助手",
                    ThinkingMessage = "思考中..."
                });
    }

    private sealed class RecordingReplyTtsOrchestrator : IReplyTtsOrchestrator
    {
        public List<FeishuCompletedReplyTtsRequest> Requests { get; } = new();

        public TaskCompletionSource<FeishuCompletedReplyTtsRequest> WhenQueued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<FeishuCompletedReplyTtsRequest, Task>? OnQueued { get; set; }

        public async Task QueueCompletedReplyAsync(FeishuCompletedReplyTtsRequest request)
        {
            Requests.Add(request);
            WhenQueued.TrySetResult(request);
            if (OnQueued != null)
            {
                await OnQueued(request);
            }
        }
    }

    private sealed class RecordingFeishuCardKitClient : IFeishuCardKitClient
    {
        public int CreateCardCallCount { get; private set; }

        public int SendCardCallCount { get; private set; }

        public int ReplyCardCallCount { get; private set; }

        public int SendTextCallCount { get; private set; }

        public int ReplyTextCallCount { get; private set; }

        public Task<string> CreateCardAsync(string initialContent, string? title = null, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            CreateCardCallCount++;
            return Task.FromResult("card-1");
        }

        public Task<bool> UpdateCardAsync(string cardId, string content, int sequence, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => Task.FromResult(true);

        public Task<string> SendCardMessageAsync(string chatId, string cardId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            SendCardCallCount++;
            return Task.FromResult("message-card");
        }

        public Task<string> SendTextMessageAsync(string chatId, string content, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            SendTextCallCount++;
            return Task.FromResult("message-text");
        }

        public Task<string> ReplyCardMessageAsync(string replyMessageId, string cardId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            ReplyCardCallCount++;
            return Task.FromResult("reply-card");
        }

        public Task<string> ReplyTextMessageAsync(string replyMessageId, string content, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            ReplyTextCallCount++;
            return Task.FromResult("reply-text");
        }

        public Task<FeishuStreamingHandle> CreateStreamingHandleAsync(string chatId, string? replyMessageId, string initialContent, string? title = null, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null, FeishuStreamingCardChrome? chrome = null)
            => throw new NotSupportedException();

        public Task<string> SendRawCardAsync(string chatId, string cardJson, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        public Task<string> ReplyElementsCardAsync(string replyMessageId, FeishuNetSdk.Im.Dtos.ElementsCardV2Dto card, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        public Task<string> ReplyRawCardAsync(string replyMessageId, string cardJson, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();
    }

    private sealed class StreamingRecordingFeishuCardKitClient : IFeishuCardKitClient
    {
        public List<StreamingHandleRecord> Handles { get; } = new();

        public int ReplyTextCallCount { get; private set; }

        public string? LastReplyTextContent { get; private set; }

        public Task<string> CreateCardAsync(string initialContent, string? title = null, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => Task.FromResult($"card-{Handles.Count + 1}");

        public Task<bool> UpdateCardAsync(string cardId, string content, int sequence, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => Task.FromResult(true);

        public Task<string> SendCardMessageAsync(string chatId, string cardId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => Task.FromResult($"message-{cardId}");

        public Task<string> SendTextMessageAsync(string chatId, string content, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => Task.FromResult("message-text");

        public Task<string> ReplyCardMessageAsync(string replyMessageId, string cardId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => Task.FromResult($"reply-{cardId}");

        public Task<string> ReplyTextMessageAsync(string replyMessageId, string content, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            ReplyTextCallCount++;
            LastReplyTextContent = content;
            return Task.FromResult($"reply-text-{ReplyTextCallCount}");
        }

        public Task<FeishuStreamingHandle> CreateStreamingHandleAsync(string chatId, string? replyMessageId, string initialContent, string? title = null, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null, FeishuStreamingCardChrome? chrome = null)
        {
            var record = new StreamingHandleRecord
            {
                CardId = $"card-{Handles.Count + 1}",
                MessageId = $"message-{Handles.Count + 1}",
                ReplyMessageId = replyMessageId,
                InitialContent = initialContent,
                Chrome = chrome,
                InitialStatusMarkdown = chrome?.StatusMarkdown,
                InitialChromeSnapshot = CloneChrome(chrome)
            };
            if (!string.IsNullOrWhiteSpace(record.InitialStatusMarkdown))
            {
                record.StatusMarkdownSnapshots.Add(record.InitialStatusMarkdown);
            }
            Handles.Add(record);

            return Task.FromResult(new FeishuStreamingHandle(
                record.CardId,
                record.MessageId,
                (content, _) =>
                {
                    record.Updates.Add(content);
                    if (!string.IsNullOrWhiteSpace(chrome?.StatusMarkdown))
                    {
                        record.StatusMarkdownSnapshots.Add(chrome.StatusMarkdown);
                    }
                    return Task.CompletedTask;
                },
                (content, _) =>
                {
                    record.FinalContent = content;
                    record.FinalStatusMarkdown = chrome?.StatusMarkdown;
                    record.FinalChromeSnapshot = CloneChrome(chrome);
                    if (!string.IsNullOrWhiteSpace(record.FinalStatusMarkdown))
                    {
                        record.StatusMarkdownSnapshots.Add(record.FinalStatusMarkdown);
                    }
                    return Task.CompletedTask;
                },
                throttleMs: 0));
        }

        public Task<string> SendRawCardAsync(string chatId, string cardJson, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        public Task<string> ReplyElementsCardAsync(string replyMessageId, FeishuNetSdk.Im.Dtos.ElementsCardV2Dto card, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        public Task<string> ReplyRawCardAsync(string replyMessageId, string cardJson, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        private static FeishuStreamingCardChrome? CloneChrome(FeishuStreamingCardChrome? chrome)
        {
            if (chrome == null)
            {
                return null;
            }

            return new FeishuStreamingCardChrome
            {
                StatusMarkdown = chrome.StatusMarkdown,
                OverflowOptions = chrome.OverflowOptions
                    .Select(option => new FeishuStreamingCardOverflowOption
                    {
                        Text = option.Text,
                        Type = option.Type,
                        Value = option.Value
                    })
                    .ToList(),
                TopChipGroups = chrome.TopChipGroups
                    .Select(group => new FeishuStreamingCardTopChipGroup
                    {
                        Kind = group.Kind,
                        IsEnabled = group.IsEnabled,
                        SummaryMarkdown = group.SummaryMarkdown,
                        OverflowOptions = group.OverflowOptions
                            .Select(option => new FeishuStreamingCardOverflowOption
                            {
                                Text = option.Text,
                                Type = option.Type,
                                Value = option.Value
                            })
                            .ToList(),
                        Items = group.Items
                            .Select(item => new FeishuStreamingCardTopChipItem
                            {
                                Text = item.Text,
                                IsActive = item.IsActive,
                                IsEnabled = item.IsEnabled,
                                PreferredWidthPx = item.PreferredWidthPx,
                                Value = item.Value
                            })
                            .ToList()
                    })
                    .ToList(),
                BottomActions = chrome.BottomActions
                    .Select(action => new FeishuStreamingCardBottomAction
                    {
                        Text = action.Text,
                        Type = action.Type,
                        Value = action.Value
                    })
                    .ToList(),
                BottomPrompt = chrome.BottomPrompt == null
                    ? null
                    : new FeishuStreamingCardBottomPrompt
                    {
                        FormName = chrome.BottomPrompt.FormName,
                        InputName = chrome.BottomPrompt.InputName,
                        InputLabel = chrome.BottomPrompt.InputLabel,
                        Placeholder = chrome.BottomPrompt.Placeholder,
                        DefaultValue = chrome.BottomPrompt.DefaultValue,
                        ButtonText = chrome.BottomPrompt.ButtonText,
                        ButtonType = chrome.BottomPrompt.ButtonType,
                        Value = chrome.BottomPrompt.Value
                    },
                AdditionalBottomPrompts = chrome.AdditionalBottomPrompts
                    .Select(prompt => new FeishuStreamingCardBottomPrompt
                    {
                        FormName = prompt.FormName,
                        InputName = prompt.InputName,
                        InputLabel = prompt.InputLabel,
                        Placeholder = prompt.Placeholder,
                        DefaultValue = prompt.DefaultValue,
                        ButtonText = prompt.ButtonText,
                        ButtonType = prompt.ButtonType,
                        Value = prompt.Value
                    })
                    .ToList()
            };
        }
    }

    private sealed class StreamingHandleRecord
    {
        public string CardId { get; set; } = string.Empty;

        public string MessageId { get; set; } = string.Empty;

        public string? ReplyMessageId { get; set; }

        public string InitialContent { get; set; } = string.Empty;

        public string? InitialStatusMarkdown { get; set; }

        public List<string> Updates { get; } = new();

        public string? FinalContent { get; set; }

        public string? FinalStatusMarkdown { get; set; }

        public FeishuStreamingCardChrome? InitialChromeSnapshot { get; set; }

        public FeishuStreamingCardChrome? FinalChromeSnapshot { get; set; }

        public List<string> StatusMarkdownSnapshots { get; } = new();

        public FeishuStreamingCardChrome? Chrome { get; set; }
    }

    private sealed class RecordingChatSessionService : IChatSessionService
    {
        public Dictionary<string, List<ChatMessage>> Messages { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void AddMessage(string sessionId, ChatMessage message)
        {
            if (!Messages.TryGetValue(sessionId, out var sessionMessages))
            {
                sessionMessages = new List<ChatMessage>();
                Messages[sessionId] = sessionMessages;
            }

            sessionMessages.Add(message);
        }

        public List<ChatMessage> GetMessages(string sessionId)
            => Messages.TryGetValue(sessionId, out var sessionMessages)
                ? new List<ChatMessage>(sessionMessages)
                : new List<ChatMessage>();

        public void ClearSession(string sessionId)
        {
            Messages.Remove(sessionId);
        }

        public void UpdateMessage(string sessionId, string messageId, Action<ChatMessage> updateAction)
        {
            var message = GetMessage(sessionId, messageId);
            if (message != null)
            {
                updateAction(message);
            }
        }

        public ChatMessage? GetMessage(string sessionId, string messageId)
        {
            return null;
        }
    }

    private sealed class TakeoverCliExecutor(string workspacePath) : ICliExecutorService
    {
        private readonly Dictionary<string, string> _cliThreadIds = new(StringComparer.OrdinalIgnoreCase);
        private int _callCount;

        public TaskCompletionSource<bool> ThreadIdPersisted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<ExecutionCall> ExecuteCalls { get; } = new();

        public bool BlockFirstCall { get; set; } = true;

        public ICliToolAdapter? GetAdapter(CliToolConfig tool) => new CodexAdapter();

        public ICliToolAdapter? GetAdapterById(string toolId) => new CodexAdapter();

        public bool SupportsStreamParsing(CliToolConfig tool) => true;

        public string? GetCliThreadId(string sessionId)
        {
            return _cliThreadIds.TryGetValue(sessionId, out var threadId) ? threadId : null;
        }

        public void SetCliThreadId(string sessionId, string threadId)
        {
            _cliThreadIds[sessionId] = threadId;
            if (string.Equals(threadId, "thread-1", StringComparison.Ordinal))
            {
                ThreadIdPersisted.TrySetResult(true);
            }
        }

        public Task ResetSessionRuntimeAsync(
            string sessionId,
            bool clearCliThreadId = true,
            CancellationToken cancellationToken = default)
        {
            if (clearCliThreadId)
            {
                _cliThreadIds.Remove(sessionId);
            }
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<StreamOutputChunk> ExecuteStreamAsync(
            string sessionId,
            string toolId,
            string userPrompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var callNumber = Interlocked.Increment(ref _callCount);
            ExecuteCalls.Add(new ExecutionCall
            {
                SessionId = sessionId,
                ToolId = toolId,
                Prompt = userPrompt,
                ThreadIdAtStart = GetCliThreadId(sessionId)
            });

            if (callNumber == 1 && BlockFirstCall)
            {
                yield return new StreamOutputChunk
                {
                    Content = "{\"type\":\"thread.started\",\"thread_id\":\"thread-1\"}\n",
                    IsCompleted = false
                };

                var wasCancelled = false;
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    wasCancelled = true;
                }

                if (wasCancelled)
                {
                    yield return new StreamOutputChunk
                    {
                        IsError = true,
                        IsCompleted = true,
                        ErrorMessage = "执行已取消"
                    };
                    yield break;
                }
            }

            yield return new StreamOutputChunk
            {
                Content = "\u8865\u5145\u5b8c\u6210\n",
                IsCompleted = false
            };

            yield return new StreamOutputChunk
            {
                Content = string.Empty,
                IsCompleted = true
            };
        }

        public bool SupportsLowInterruptionContinue(string toolId) => false;

        public bool CanStartLowInterruptionContinue(string sessionId, string toolId) => false;

        public async IAsyncEnumerable<StreamOutputChunk> ExecuteLowInterruptionContinueStreamAsync(
            string sessionId,
            string toolId,
            string? prompt = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = "not implemented in test double"
            };

            await Task.CompletedTask;
        }

        public List<CliToolConfig> GetAvailableTools(string? username = null)
            => new() { GetTool("codex", username)! };

        public CliToolConfig? GetTool(string toolId, string? username = null)
            => string.Equals(toolId, "codex", StringComparison.OrdinalIgnoreCase)
                ? new CliToolConfig
                {
                    Id = "codex",
                    Name = "Codex",
                    Description = "Codex",
                    Command = "codex",
                    Enabled = true
                }
                : null;

        public bool ValidateTool(string toolId, string? username = null) => true;

        public void CleanupSessionWorkspace(string sessionId)
        {
        }

        public void CleanupExpiredWorkspaces()
        {
        }

        public string GetSessionWorkspacePath(string sessionId) => workspacePath;

        public Task<Dictionary<string, string>> GetToolEnvironmentVariablesAsync(string toolId, string? username = null)
            => Task.FromResult(new Dictionary<string, string>());

        public Task<CcSwitchSessionSnapshot?> SyncSessionCcSwitchSnapshotAsync(string sessionId, string? toolId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<CcSwitchSessionSnapshot?>(null);

        public Task<CodexThreadProviderSyncResult> SyncCodexThreadProviderAsync(string sessionId, string? toolId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new CodexThreadProviderSyncResult
            {
                Message = "thread sync complete"
            });

        public Task<bool> SaveToolEnvironmentVariablesAsync(string toolId, Dictionary<string, string> envVars, string? username = null)
            => Task.FromResult(true);

        public byte[]? GetWorkspaceFile(string sessionId, string relativePath) => null;

        public byte[]? GetWorkspaceZip(string sessionId) => null;

        public Task<bool> UploadFileToWorkspaceAsync(string sessionId, string fileName, byte[] fileContent, string? relativePath = null)
            => Task.FromResult(true);

        public Task<bool> CreateFolderInWorkspaceAsync(string sessionId, string folderPath)
            => Task.FromResult(true);

        public Task<bool> DeleteWorkspaceItemAsync(string sessionId, string relativePath, bool isDirectory)
            => Task.FromResult(true);

        public Task<bool> MoveFileInWorkspaceAsync(string sessionId, string sourcePath, string targetPath)
            => Task.FromResult(true);

        public Task<bool> CopyFileInWorkspaceAsync(string sessionId, string sourcePath, string targetPath)
            => Task.FromResult(true);

        public Task<bool> RenameFileInWorkspaceAsync(string sessionId, string oldPath, string newName)
            => Task.FromResult(true);

        public Task<int> BatchDeleteFilesAsync(string sessionId, List<string> relativePaths)
            => Task.FromResult(0);

        public Task<string> InitializeSessionWorkspaceAsync(string sessionId, string? projectId = null, bool includeGit = false)
            => Task.FromResult(Path.Combine(Path.GetTempPath(), sessionId));

        public void RefreshWorkspaceRootCache()
        {
        }
    }

    private sealed class PromptCapturingCliExecutor(string workspacePath) : ICliExecutorService
    {
        public List<ExecutionCall> ExecuteCalls { get; } = new();

        public bool SupportsLowInterruption { get; set; }

        public string? ReusableCliThreadId { get; set; }

        public string FinalContent { get; set; } = "\u8865\u5145\u5b8c\u6210\n";

        public ICliToolAdapter? GetAdapter(CliToolConfig tool) => null;

        public ICliToolAdapter? GetAdapterById(string toolId) => null;

        public bool SupportsStreamParsing(CliToolConfig tool) => false;

        public string? GetCliThreadId(string sessionId) => ReusableCliThreadId;

        public void SetCliThreadId(string sessionId, string threadId)
        {
        }

        public Task ResetSessionRuntimeAsync(
            string sessionId,
            bool clearCliThreadId = true,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<StreamOutputChunk> ExecuteStreamAsync(
            string sessionId,
            string toolId,
            string userPrompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ExecuteCalls.Add(new ExecutionCall
            {
                SessionId = sessionId,
                ToolId = toolId,
                Prompt = userPrompt
            });

            yield return new StreamOutputChunk
            {
                Content = FinalContent,
                IsCompleted = false
            };

            yield return new StreamOutputChunk
            {
                Content = string.Empty,
                IsCompleted = true
            };

            await Task.CompletedTask;
        }

        public bool SupportsLowInterruptionContinue(string toolId) => SupportsLowInterruption;

        public bool CanStartLowInterruptionContinue(string sessionId, string toolId)
            => SupportsLowInterruption && !string.IsNullOrWhiteSpace(ReusableCliThreadId);

        public async IAsyncEnumerable<StreamOutputChunk> ExecuteLowInterruptionContinueStreamAsync(
            string sessionId,
            string toolId,
            string? prompt = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = "not implemented in test double"
            };

            await Task.CompletedTask;
        }

        public List<CliToolConfig> GetAvailableTools(string? username = null)
            => new() { GetTool("codex", username)! };

        public CliToolConfig? GetTool(string toolId, string? username = null)
            => string.Equals(toolId, "codex", StringComparison.OrdinalIgnoreCase)
                ? new CliToolConfig
                {
                    Id = "codex",
                    Name = "Codex",
                    Description = "Codex",
                    Command = "codex",
                    Enabled = true
                }
                : null;

        public bool ValidateTool(string toolId, string? username = null) => true;

        public void CleanupSessionWorkspace(string sessionId)
        {
        }

        public void CleanupExpiredWorkspaces()
        {
        }

        public string GetSessionWorkspacePath(string sessionId) => workspacePath;

        public Task<Dictionary<string, string>> GetToolEnvironmentVariablesAsync(string toolId, string? username = null)
            => Task.FromResult(new Dictionary<string, string>());

        public Task<CcSwitchSessionSnapshot?> SyncSessionCcSwitchSnapshotAsync(string sessionId, string? toolId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<CcSwitchSessionSnapshot?>(null);

        public Task<CodexThreadProviderSyncResult> SyncCodexThreadProviderAsync(string sessionId, string? toolId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new CodexThreadProviderSyncResult
            {
                Message = "thread sync complete"
            });

        public Task<bool> SaveToolEnvironmentVariablesAsync(string toolId, Dictionary<string, string> envVars, string? username = null)
            => Task.FromResult(true);

        public byte[]? GetWorkspaceFile(string sessionId, string relativePath) => null;

        public byte[]? GetWorkspaceZip(string sessionId) => null;

        public Task<bool> UploadFileToWorkspaceAsync(string sessionId, string fileName, byte[] fileContent, string? relativePath = null)
            => Task.FromResult(true);

        public Task<bool> CreateFolderInWorkspaceAsync(string sessionId, string folderPath)
            => Task.FromResult(true);

        public Task<bool> DeleteWorkspaceItemAsync(string sessionId, string relativePath, bool isDirectory)
            => Task.FromResult(true);

        public Task<bool> MoveFileInWorkspaceAsync(string sessionId, string sourcePath, string targetPath)
            => Task.FromResult(true);

        public Task<bool> CopyFileInWorkspaceAsync(string sessionId, string sourcePath, string targetPath)
            => Task.FromResult(true);

        public Task<bool> RenameFileInWorkspaceAsync(string sessionId, string oldPath, string newName)
            => Task.FromResult(true);

        public Task<int> BatchDeleteFilesAsync(string sessionId, List<string> relativePaths)
            => Task.FromResult(0);

        public Task<string> InitializeSessionWorkspaceAsync(string sessionId, string? projectId = null, bool includeGit = false)
            => Task.FromResult(workspacePath);

        public void RefreshWorkspaceRootCache()
        {
        }
    }

    private sealed class ErrorCliExecutor(string workspacePath) : ICliExecutorService
    {
        public ICliToolAdapter? GetAdapter(CliToolConfig tool) => null;

        public ICliToolAdapter? GetAdapterById(string toolId) => null;

        public bool SupportsStreamParsing(CliToolConfig tool) => false;

        public string? GetCliThreadId(string sessionId) => null;

        public void SetCliThreadId(string sessionId, string threadId)
        {
        }

        public Task ResetSessionRuntimeAsync(string sessionId, bool clearCliThreadId = true, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<StreamOutputChunk> ExecuteStreamAsync(
            string sessionId,
            string toolId,
            string userPrompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = "执行失败"
            };

            await Task.CompletedTask;
        }

        public bool SupportsLowInterruptionContinue(string toolId) => false;

        public bool CanStartLowInterruptionContinue(string sessionId, string toolId) => false;

        public async IAsyncEnumerable<StreamOutputChunk> ExecuteLowInterruptionContinueStreamAsync(
            string sessionId,
            string toolId,
            string? prompt = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new StreamOutputChunk
            {
                IsError = true,
                IsCompleted = true,
                ErrorMessage = "not implemented in test double"
            };

            await Task.CompletedTask;
        }

        public List<CliToolConfig> GetAvailableTools(string? username = null)
            => new() { GetTool("codex", username)! };

        public CliToolConfig? GetTool(string toolId, string? username = null)
            => string.Equals(toolId, "codex", StringComparison.OrdinalIgnoreCase)
                ? new CliToolConfig
                {
                    Id = "codex",
                    Name = "Codex",
                    Description = "Codex",
                    Command = "codex",
                    Enabled = true
                }
                : null;

        public bool ValidateTool(string toolId, string? username = null) => true;

        public void CleanupSessionWorkspace(string sessionId)
        {
        }

        public void CleanupExpiredWorkspaces()
        {
        }

        public string GetSessionWorkspacePath(string sessionId) => workspacePath;

        public Task<Dictionary<string, string>> GetToolEnvironmentVariablesAsync(string toolId, string? username = null)
            => Task.FromResult(new Dictionary<string, string>());

        public Task<CcSwitchSessionSnapshot?> SyncSessionCcSwitchSnapshotAsync(string sessionId, string? toolId = null, CancellationToken cancellationToken = default)
            => Task.FromResult<CcSwitchSessionSnapshot?>(null);

        public Task<CodexThreadProviderSyncResult> SyncCodexThreadProviderAsync(string sessionId, string? toolId = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new CodexThreadProviderSyncResult
            {
                Message = "thread sync complete"
            });

        public Task<bool> SaveToolEnvironmentVariablesAsync(string toolId, Dictionary<string, string> envVars, string? username = null)
            => Task.FromResult(true);

        public byte[]? GetWorkspaceFile(string sessionId, string relativePath) => null;

        public byte[]? GetWorkspaceZip(string sessionId) => null;

        public Task<bool> UploadFileToWorkspaceAsync(string sessionId, string fileName, byte[] fileContent, string? relativePath = null)
            => Task.FromResult(true);

        public Task<bool> CreateFolderInWorkspaceAsync(string sessionId, string folderPath)
            => Task.FromResult(true);

        public Task<bool> DeleteWorkspaceItemAsync(string sessionId, string relativePath, bool isDirectory)
            => Task.FromResult(true);

        public Task<bool> MoveFileInWorkspaceAsync(string sessionId, string sourcePath, string targetPath)
            => Task.FromResult(true);

        public Task<bool> CopyFileInWorkspaceAsync(string sessionId, string sourcePath, string targetPath)
            => Task.FromResult(true);

        public Task<bool> RenameFileInWorkspaceAsync(string sessionId, string oldPath, string newName)
            => Task.FromResult(true);

        public Task<int> BatchDeleteFilesAsync(string sessionId, List<string> relativePaths)
            => Task.FromResult(0);

        public Task<string> InitializeSessionWorkspaceAsync(string sessionId, string? projectId = null, bool includeGit = false)
            => Task.FromResult(Path.Combine(Path.GetTempPath(), sessionId));

        public void RefreshWorkspaceRootCache()
        {
        }
    }

    private sealed class ExecutionCall
    {
        public string SessionId { get; set; } = string.Empty;

        public string ToolId { get; set; } = string.Empty;

        public string Prompt { get; set; } = string.Empty;

        public string? ThreadIdAtStart { get; set; }
    }

    private sealed class RecordingSessionDirectoryService(ChatSessionRepositoryProxy repository) : ISessionDirectoryService
    {
        public Task SetSessionWorkspaceAsync(string sessionId, string username, string directoryPath, bool isCustom = true)
        {
            var session = repository.GetStored(sessionId);
            session.WorkspacePath = directoryPath;
            session.IsCustomWorkspace = isCustom;
            session.UpdatedAt = DateTime.Now;
            repository.Store(session);
            return Task.CompletedTask;
        }

        public Task<string?> GetSessionWorkspaceAsync(string sessionId, string username)
            => Task.FromResult<string?>(repository.GetStored(sessionId).WorkspacePath);

        public Task SwitchSessionWorkspaceAsync(string sessionId, string username, string newDirectoryPath)
            => SetSessionWorkspaceAsync(sessionId, username, newDirectoryPath);

        public Task<bool> VerifySessionWorkspacePermissionAsync(string sessionId, string username, string requiredPermission = "write")
            => Task.FromResult(true);

        public Task<List<object>> GetUserAccessibleDirectoriesAsync(string username)
            => Task.FromResult(new List<object>());

        public Task<AllowedDirectoryBrowseResult> BrowseAllowedDirectoriesAsync(string? path, string? username = null)
            => throw new NotSupportedException();
    }

    private class ChatSessionRepositoryProxy : DispatchProxy
    {
        private readonly Dictionary<string, ChatSessionEntity> _sessions = new(StringComparer.OrdinalIgnoreCase);

        public ChatSessionEntity GetStored(string sessionId)
        {
            return Clone(_sessions[sessionId]);
        }

        public void Store(ChatSessionEntity session)
        {
            _sessions[session.SessionId] = Clone(session);
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            switch (targetMethod?.Name)
            {
                case nameof(IChatSessionRepository.CreateFeishuSessionAsync):
                {
                    var chatKey = (string)args![0]!;
                    var username = (string)args[1]!;
                    var workspacePath = args[2] as string;
                    var toolId = args[3] as string;
                    var sessionId = Guid.NewGuid().ToString();

                    _sessions[sessionId] = new ChatSessionEntity
                    {
                        SessionId = sessionId,
                        Username = username,
                        FeishuChatKey = chatKey,
                        IsFeishuActive = true,
                        WorkspacePath = workspacePath,
                        ToolId = toolId,
                        CreatedAt = DateTime.Now,
                        UpdatedAt = DateTime.Now
                    };

                    return Task.FromResult(sessionId);
                }
                case nameof(IRepository<ChatSessionEntity>.GetByIdAsync):
                {
                    var sessionId = args![0]?.ToString() ?? string.Empty;
                    var session = _sessions.TryGetValue(sessionId, out var stored) ? Clone(stored) : null;
                    return Task.FromResult(session);
                }
                case nameof(IRepository<ChatSessionEntity>.UpdateAsync):
                {
                    var session = Clone((ChatSessionEntity)args![0]!);
                    _sessions[session.SessionId] = session;
                    return Task.FromResult(true);
                }
                case nameof(IChatSessionRepository.GetByIdAndUsernameAsync):
                {
                    var sessionId = args![0]?.ToString() ?? string.Empty;
                    var username = args[1]?.ToString() ?? string.Empty;
                    var session = _sessions.TryGetValue(sessionId, out var stored) &&
                                  string.Equals(stored.Username, username, StringComparison.OrdinalIgnoreCase)
                        ? Clone(stored)
                        : null;
                    return Task.FromResult(session);
                }
                case nameof(IChatSessionRepository.GetByUsernameOrderByUpdatedAtAsync):
                {
                    var username = args![0]?.ToString() ?? string.Empty;
                    var sessions = _sessions.Values
                        .Where(session => string.Equals(session.Username, username, StringComparison.OrdinalIgnoreCase))
                        .OrderByDescending(session => session.UpdatedAt)
                        .Select(Clone)
                        .ToList();
                    return Task.FromResult(sessions);
                }
                case nameof(IChatSessionRepository.GetByFeishuChatKeyAsync):
                {
                    var chatKey = args![0]?.ToString() ?? string.Empty;
                    var sessions = _sessions.Values
                        .Where(session => string.Equals(session.FeishuChatKey, chatKey, StringComparison.OrdinalIgnoreCase))
                        .Select(Clone)
                        .ToList();
                    return Task.FromResult(sessions);
                }
                case nameof(IRepository<ChatSessionEntity>.GetListAsync):
                {
                    if (args == null || args.Length == 0 || args[0] == null)
                    {
                        return Task.FromResult(_sessions.Values.Select(Clone).ToList());
                    }

                    var predicate = (Expression<Func<ChatSessionEntity, bool>>)args[0]!;
                    var compiled = predicate.Compile();
                    var sessions = _sessions.Values
                        .Where(compiled)
                        .Select(Clone)
                        .ToList();
                    return Task.FromResult(sessions);
                }
            }

            return GetDefaultReturnValue(targetMethod?.ReturnType);
        }

        private static object? GetDefaultReturnValue(Type? returnType)
        {
            if (returnType == null || returnType == typeof(void))
            {
                return null;
            }

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GenericTypeArguments[0];
                var defaultValue = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                return typeof(Task)
                    .GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(resultType)
                    .Invoke(null, [defaultValue]);
            }

            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }

        private static ChatSessionEntity Clone(ChatSessionEntity session)
        {
            return new ChatSessionEntity
            {
                SessionId = session.SessionId,
                Username = session.Username,
                Title = session.Title,
                WorkspacePath = session.WorkspacePath,
                ToolId = session.ToolId,
                CreatedAt = session.CreatedAt,
                UpdatedAt = session.UpdatedAt,
                IsWorkspaceValid = session.IsWorkspaceValid,
                ProjectId = session.ProjectId,
                FeishuChatKey = session.FeishuChatKey,
                IsFeishuActive = session.IsFeishuActive,
                IsCustomWorkspace = session.IsCustomWorkspace,
                CliThreadId = session.CliThreadId,
                ToolLaunchOverridesJson = session.ToolLaunchOverridesJson,
                UsesCcSwitchSnapshot = session.UsesCcSwitchSnapshot,
                CcSwitchSnapshotToolId = session.CcSwitchSnapshotToolId,
                CcSwitchProviderId = session.CcSwitchProviderId,
                CcSwitchProviderName = session.CcSwitchProviderName,
                CcSwitchProviderCategory = session.CcSwitchProviderCategory,
                CcSwitchLiveConfigPath = session.CcSwitchLiveConfigPath,
                CcSwitchSnapshotRelativePath = session.CcSwitchSnapshotRelativePath,
                CcSwitchSnapshotSyncedAt = session.CcSwitchSnapshotSyncedAt
            };
        }
    }

    private class DefaultInterfaceProxy<T> : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var returnType = targetMethod?.ReturnType ?? typeof(void);
            if (returnType == typeof(void))
            {
                return null;
            }

            if (returnType == typeof(Task))
            {
                return Task.CompletedTask;
            }

            if (returnType.IsGenericType && returnType.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = returnType.GenericTypeArguments[0];
                var defaultValue = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;
                return typeof(Task)
                    .GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(resultType)
                    .Invoke(null, [defaultValue]);
            }

            return returnType.IsValueType ? Activator.CreateInstance(returnType) : null;
        }
    }
}

