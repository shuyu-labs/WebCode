using FeishuNetSdk.CallbackEvents;
using FeishuNetSdk.Im.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using System.Linq.Expressions;
using System.Text.Json;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service;
using WebCodeCli.Domain.Domain.Service.Adapters;
using WebCodeCli.Domain.Domain.Service.Channels;
using WebCodeCli.Domain.Model;
using WebCodeCli.Domain.Repositories.Base.ChatSession;
using WebCodeCli.Domain.Repositories.Base.UserFeishuBotConfig;

namespace WebCodeCli.Domain.Tests;

public class FeishuCardActionServiceTests
{
    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_UsesCurrentFeishuSession()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-acp";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(activeSessionId);
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider());

        await service.HandleCardActionAsync(
            """{"action":"execute_command"}""",
            chatId: chatId,
            inputValues: "/init");

        var usedSessionId = await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(activeSessionId, usedSessionId);
    }

    [Fact]
    public async Task HandleCardActionAsync_ToggleReplyTts_DisablesReplyTtsAndRefreshesHelpCard()
    {
        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(null)
        {
            SessionUsername = "luhaiyan"
        };
        var feishuBotConfigService = new StubUserFeishuBotConfigService();
        feishuBotConfigService.Seed(new UserFeishuBotConfigEntity
        {
            Username = "luhaiyan",
            IsEnabled = true,
            ReplyTtsEnabled = true,
            ReplyTtsVoiceId = "voice-a"
        });

        var serviceProvider = new TestServiceProvider(feishuBotConfigService: feishuBotConfigService);
        var service = CreateService(cliExecutor, feishuChannel, serviceProvider);

        var response = await service.HandleCardActionAsync(
            """{"action":"toggle_reply_tts"}""",
            chatId: "oc_tts_toggle_chat");

        var savedConfig = await feishuBotConfigService.GetByUsernameAsync("luhaiyan");
        Assert.NotNull(savedConfig);
        Assert.False(savedConfig!.ReplyTtsEnabled);
        Assert.Equal("voice-a", savedConfig.ReplyTtsVoiceId);
        Assert.Equal("✅ 已关闭飞书语音回复", ExtractToastContent(response));
        Assert.Contains("语音回复：关", ExtractCardContentStrings(response));
    }

    [Fact]
    public async Task HandleCardActionAsync_ToggleReplyTts_ReturnsErrorToast_WhenUserConfigMissing()
    {
        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(null)
        {
            SessionUsername = "missing-user"
        };
        var feishuBotConfigService = new StubUserFeishuBotConfigService();
        var serviceProvider = new TestServiceProvider(feishuBotConfigService: feishuBotConfigService);
        var service = CreateService(cliExecutor, feishuChannel, serviceProvider);

        var response = await service.HandleCardActionAsync(
            """{"action":"toggle_reply_tts"}""",
            chatId: "oc_tts_toggle_chat");

        Assert.Equal("❌ 未找到当前飞书用户配置", ExtractToastContent(response));
        Assert.DoesNotContain("语音回复：", SerializeResponse(response));
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_ForwardsRawPromptWithoutReplyPrefixInstructions()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-acp";

        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-card-prefix-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(workspaceRoot, "superpowers");
        Directory.CreateDirectory(workspacePath);

        try
        {
            var cliExecutor = new RecordingCliExecutorService();
            cliExecutor.SetSessionWorkspacePath(activeSessionId, workspacePath);

            var feishuChannel = new StubFeishuChannelService(activeSessionId);
            var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider());

            await service.HandleCardActionAsync(
                """{"action":"execute_command"}""",
                chatId: chatId,
                operatorUserId: "ou_test_user",
                inputValues: @"D:\MMIS\Base\Docs\superpowers");

            await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));

            var prompt = Assert.Single(cliExecutor.ExecutedPrompts);
            Assert.Equal(@"D:\MMIS\Base\Docs\superpowers", prompt);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleCardActionAsync_SubmitAttachmentPrompt_ReturnsWarning_WhenSupplementTextIsEmpty()
    {
        const string chatId = "oc_attachment_chat";
        const string boundSessionId = "session-bound";

        var cliExecutor = new RecordingCliExecutorService();
        cliExecutor.SetSessionWorkspacePath(boundSessionId, @"D:\repo\superpowers");
        var feishuChannel = new StubFeishuChannelService("session-other");
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider());

        var response = await service.HandleCardActionAsync(
            $$"""
            {"action":"submit_attachment_prompt","chat_key":"{{chatId}}","session_id":"{{boundSessionId}}","tool_id":"codex","attachment_type":"image","attachment_name":"screen.png","attachment_path":"D:\\repo\\superpowers\\.webcode\\feishu-inputs\\20260515-screen.png","attachment_mime_type":"image/png"}
            """,
            formValue: new Dictionary<string, object>());

        Assert.Equal(FeishuAttachmentSubmissionDefaults.EmptyPromptWarning, ExtractToastContent(response));
        Assert.Empty(cliExecutor.ExecutedPrompts);
    }

    [Fact]
    public async Task HandleCardActionAsync_SubmitAttachmentPrompt_ReturnsWarning_WhenSupplementTextExceedsMaxLength()
    {
        const string chatId = "oc_attachment_chat";
        const string boundSessionId = "session-bound";

        var cliExecutor = new RecordingCliExecutorService();
        cliExecutor.SetSessionWorkspacePath(boundSessionId, @"D:\repo\superpowers");
        var feishuChannel = new StubFeishuChannelService("session-other");
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider());

        var response = await service.HandleCardActionAsync(
            $$"""
            {"action":"submit_attachment_prompt","chat_key":"{{chatId}}","session_id":"{{boundSessionId}}","tool_id":"codex","attachment_type":"image","attachment_name":"screen.png","attachment_path":"D:\\repo\\superpowers\\.webcode\\feishu-inputs\\20260515-screen.png","attachment_mime_type":"image/png"}
            """,
            formValue: new Dictionary<string, object>
            {
                [FeishuAttachmentSubmissionDefaults.PromptFieldName] = new string('a', FeishuAttachmentSubmissionDefaults.PromptMaxLength + 1)
            });

        Assert.Equal(FeishuAttachmentSubmissionDefaults.PromptTooLongWarning, ExtractToastContent(response));
        Assert.Empty(cliExecutor.ExecutedPrompts);
    }

    [Fact]
    public async Task HandleCardActionAsync_SubmitAttachmentPrompt_BuildsAttachmentPromptAndUsesBoundSession()
    {
        const string chatId = "oc_attachment_chat";
        const string boundSessionId = "session-bound";
        const string attachmentPath = @"D:\repo\superpowers\.webcode\feishu-inputs\20260515-screen.png";

        var cliExecutor = new RecordingCliExecutorService();
        cliExecutor.SetSessionWorkspacePath(boundSessionId, @"D:\repo\superpowers");
        var cardKit = new StubFeishuCardKitClient();
        var feishuChannel = new StubFeishuChannelService("session-other");
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider(), cardKit);

        await service.HandleCardActionAsync(
            $$"""
            {"action":"submit_attachment_prompt","chat_key":"{{chatId}}","session_id":"{{boundSessionId}}","tool_id":"codex","attachment_type":"image","attachment_name":"screen.png","attachment_path":"{{attachmentPath.Replace("\\", "\\\\")}}","attachment_mime_type":"image/png"}
            """,
            formValue: new Dictionary<string, object>
            {
                [FeishuAttachmentSubmissionDefaults.PromptFieldName] = "检查这个页面为什么点收起再展开才刷新"
            });

        var usedSessionId = await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(boundSessionId, usedSessionId);

        var prompt = Assert.Single(cliExecutor.ExecutedPrompts);
        Assert.Contains("[Feishu image attached]", prompt, StringComparison.Ordinal);
        Assert.Contains("screen.png", prompt, StringComparison.Ordinal);
        Assert.Contains(attachmentPath, prompt, StringComparison.Ordinal);
        Assert.Contains("image/png", prompt, StringComparison.Ordinal);
        Assert.Contains("检查这个页面为什么点收起再展开才刷新", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_CreatesStreamingCardWithRecentSessionMenu()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "11111111-current";

        var cliExecutor = new RecordingCliExecutorService();
        cliExecutor.SetSessionWorkspacePath(activeSessionId, @"D:\repo\superpowers");

        var cardKit = new StubFeishuCardKitClient();
        var feishuChannel = new StubFeishuChannelService(activeSessionId);
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = activeSessionId,
                Username = "luhaiyan",
                Title = "旧会话标题",
                ToolId = "codex",
                WorkspacePath = @"D:\repo\superpowers",
                FeishuChatKey = chatId,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                CreatedAt = DateTime.Now.AddMinutes(-30),
                UpdatedAt = DateTime.Now
            },
            new ChatSessionEntity
            {
                SessionId = "22222222-other",
                Username = "luhaiyan",
                Title = "Backend API",
                ToolId = "claude-code",
                WorkspacePath = @"D:\repo\backend",
                FeishuChatKey = chatId,
                IsWorkspaceValid = true,
                IsFeishuActive = false,
                CreatedAt = DateTime.Now.AddMinutes(-60),
                UpdatedAt = DateTime.Now.AddMinutes(-5)
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository),
            cardKit);

        await service.HandleCardActionAsync(
            """{"action":"execute_command"}""",
            chatId: chatId,
            inputValues: "缁х画");

        await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));
        var completionMessage = await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

        Assert.NotNull(cardKit.LastStreamingChrome);
        Assert.Contains("处理中", cardKit.InitialStreamingStatusMarkdown);
        Assert.Equal(chatId, completionMessage.ChatId);
        Assert.Equal("当前会话：superpowers  旧会话标题\n已完成", completionMessage.Content);
        var chrome = cardKit.LastStreamingChrome!;
        Assert.Contains("当前会话", chrome.StatusMarkdown);
        Assert.Contains("superpowers", chrome.StatusMarkdown);
        Assert.Contains("旧会话标题", chrome.StatusMarkdown);
        Assert.DoesNotContain("11111111", chrome.StatusMarkdown);
        Assert.Contains(chrome.OverflowOptions, option => option.Text.Contains("Backend API", StringComparison.Ordinal));
        Assert.Contains(chrome.OverflowOptions, option => option.Text == "模型/会话管理...");

        var switchOption = Assert.Single(chrome.OverflowOptions, option => option.Text.Contains("Backend API", StringComparison.Ordinal));
        Assert.DoesNotContain("22222222", switchOption.Text);
        var valueJson = JsonSerializer.Serialize(switchOption.Value);
        Assert.Contains("\"action\":\"switch_session\"", valueJson);
        Assert.Contains("\"session_id\":\"22222222-other\"", valueJson);
        Assert.Contains("\"chat_key\":\"oc_current_chat\"", valueJson);

        var moreOption = Assert.Single(chrome.OverflowOptions, option => option.Text == "模型/会话管理...");
        var moreValueJson = JsonSerializer.Serialize(moreOption.Value);
        Assert.Contains("\"action\":\"open_session_manager\"", moreValueJson);
        Assert.Contains("\"send_as_new_card\":true", moreValueJson);
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_WhenCodexStdoutOnlyReportsThreadStarted_UsesExternalHistoryAssistantMessage()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-history-fallback";

        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-card-history-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(workspaceRoot, "superpowers");
        Directory.CreateDirectory(workspacePath);

        try
        {
            var cliExecutor = new RecordingCliExecutorService
            {
                Adapter = new CodexAdapter(),
                SupportsStreamParsingEnabled = true,
                StandardExecutionContent = "{\"type\":\"thread.started\",\"thread_id\":\"thread-1\"}\n"
            };
            cliExecutor.SetSessionWorkspacePath(activeSessionId, workspacePath);

            var historyService = new StubExternalCliSessionHistoryService(
            [
                new ExternalCliHistoryMessage
                {
                    Role = "user",
                    Content = "帮我看下goal命令有执行吗？"
                },
                new ExternalCliHistoryMessage
                {
                    Role = "assistant",
                    Content = "执行了，而且还在执行中。"
                }
            ]);

            var cardKit = new StubFeishuCardKitClient();
            var feishuChannel = new StubFeishuChannelService(activeSessionId)
            {
                ResolvedToolId = "codex"
            };
            var serviceProvider = new TestServiceProvider(externalCliSessionHistoryService: historyService);
            var service = CreateService(cliExecutor, feishuChannel, serviceProvider, cardKit);

            await service.HandleCardActionAsync(
                """{"action":"execute_command"}""",
                chatId: chatId,
                operatorUserId: "ou_test_user",
                inputValues: "帮我看下goal命令有执行吗？");

            await cliExecutor.WaitForExecutionCompletionAsync(TimeSpan.FromSeconds(3));
            var completionMessage = await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

            Assert.Equal("执行了，而且还在执行中。", cardKit.FinalStreamingContent);
            Assert.Equal("codex", historyService.LastToolId);
            Assert.Equal("thread-1", historyService.LastCliThreadId);
            Assert.Contains("当前会话：superpowers", completionMessage.Content, StringComparison.Ordinal);
            Assert.EndsWith("\n已完成", completionMessage.Content, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_KeepsAssistantTextSeparateFromLatestToolCallSummary()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-tool-summary";

        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-card-tool-summary-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(workspaceRoot, "superpowers");
        Directory.CreateDirectory(workspacePath);

        try
        {
            var cliExecutor = new RecordingCliExecutorService
            {
                Adapter = new ClaudeCodeAdapter(),
                SupportsStreamParsingEnabled = true,
                StandardExecutionContent = """{"type":"tool_use","name":"Bash","input":{"command":"git status --short"}}""" + "\n",
                StandardExecutionCompletionContent = """{"type":"assistant","content":"已经处理完了。"}""" + "\n"
            };
            cliExecutor.SetSessionWorkspacePath(activeSessionId, workspacePath);

            var cardKit = new StubFeishuCardKitClient();
            var feishuChannel = new StubFeishuChannelService(activeSessionId)
            {
                ResolvedToolId = "claude-code"
            };
            var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider(), cardKit);

            await service.HandleCardActionAsync(
                """{"action":"execute_command"}""",
                chatId: chatId,
                operatorUserId: "ou_test_user",
                inputValues: "看下当前工作树");

            await cliExecutor.WaitForExecutionCompletionAsync(TimeSpan.FromSeconds(3));
            await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

            Assert.Equal("已经处理完了。", cardKit.FinalStreamingContent);
            Assert.DoesNotContain("git status --short", cardKit.FinalStreamingContent, StringComparison.Ordinal);
            Assert.NotNull(cardKit.LastStreamingChrome);
            Assert.Equal(
                "**调用工具：** `Bash · git status --short`",
                cardKit.LastStreamingChrome!.LatestToolCallMarkdown);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_ShowsTouchedFileNameInLatestToolCallSummary()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-tool-file-summary";

        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-card-tool-file-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(workspaceRoot, "superpowers");
        Directory.CreateDirectory(workspacePath);

        try
        {
            var cliExecutor = new RecordingCliExecutorService
            {
                Adapter = new ClaudeCodeAdapter(),
                SupportsStreamParsingEnabled = true,
                StandardExecutionContent = """{"type":"tool_use","name":"Read","input":{"file_path":"D:\\VSWorkshop\\WebCode\\WebCodeCli\\Pages\\CodeAssistantMobile.razor","offset":1,"limit":120}}""" + "\n",
                StandardExecutionCompletionContent = """{"type":"assistant","content":"已读取。"}""" + "\n"
            };
            cliExecutor.SetSessionWorkspacePath(activeSessionId, workspacePath);

            var cardKit = new StubFeishuCardKitClient();
            var feishuChannel = new StubFeishuChannelService(activeSessionId)
            {
                ResolvedToolId = "claude-code"
            };
            var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider(), cardKit);

            await service.HandleCardActionAsync(
                """{"action":"execute_command"}""",
                chatId: chatId,
                operatorUserId: "ou_test_user",
                inputValues: "读取一下移动端页面");

            await cliExecutor.WaitForExecutionCompletionAsync(TimeSpan.FromSeconds(3));
            await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

            Assert.Equal("已读取。", cardKit.FinalStreamingContent);
            Assert.NotNull(cardKit.LastStreamingChrome);
            Assert.Equal(
                "**调用工具：** `Read · 文件: CodeAssistantMobile.razor`",
                cardKit.LastStreamingChrome!.LatestToolCallMarkdown);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_StripsWindowsPowerShellPathInLatestToolCallSummary()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-tool-shell-path";

        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-card-tool-shell-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(workspaceRoot, "superpowers");
        Directory.CreateDirectory(workspacePath);

        try
        {
            var cliExecutor = new RecordingCliExecutorService
            {
                Adapter = new ClaudeCodeAdapter(),
                SupportsStreamParsingEnabled = true,
                StandardExecutionContent = """{"type":"tool_use","name":"Bash","input":{"command":"C:\\WINDOWS\\System32\\WindowsPowerShell\\v1.0\\powershell.exe -NoProfile -Command Get-Content D:\\VSWorkshop\\WebCode\\WebCodeCli\\Pages\\CodeAssistantMobile.razor -TotalCount 20"}}""" + "\n",
                StandardExecutionCompletionContent = """{"type":"assistant","content":"已经看过了。"}""" + "\n"
            };
            cliExecutor.SetSessionWorkspacePath(activeSessionId, workspacePath);

            var cardKit = new StubFeishuCardKitClient();
            var feishuChannel = new StubFeishuChannelService(activeSessionId)
            {
                ResolvedToolId = "claude-code"
            };
            var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider(), cardKit);

            await service.HandleCardActionAsync(
                """{"action":"execute_command"}""",
                chatId: chatId,
                operatorUserId: "ou_test_user",
                inputValues: "看一下移动端页面");

            await cliExecutor.WaitForExecutionCompletionAsync(TimeSpan.FromSeconds(3));
            await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

            Assert.Equal("已经看过了。", cardKit.FinalStreamingContent);
            Assert.NotNull(cardKit.LastStreamingChrome);
            Assert.NotNull(cardKit.LastStreamingChrome!.LatestToolCallMarkdown);
            Assert.Contains("powershell.exe", cardKit.LastStreamingChrome.LatestToolCallMarkdown, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"C:\WINDOWS\System32\WindowsPowerShell\v1.0", cardKit.LastStreamingChrome.LatestToolCallMarkdown, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("CodeAssistantMobile.razor", cardKit.LastStreamingChrome.LatestToolCallMarkdown, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_WhenCodexStreamingReportsRawErrorLine_ShowsFallbackOutputDuringStreaming()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-raw-error";
        const string rawError = "2026-05-07T05:34:44.541725Z ERROR codex_core::tools::router: error=apply_patch verification failed: ...\n";

        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-card-raw-error-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(workspaceRoot, "superpowers");
        Directory.CreateDirectory(workspacePath);

        try
        {
            var cliExecutor = new RecordingCliExecutorService
            {
                Adapter = new CodexAdapter(),
                SupportsStreamParsingEnabled = true,
                StandardExecutionContent = rawError
            };
            cliExecutor.SetSessionWorkspacePath(activeSessionId, workspacePath);

            var cardKit = new StubFeishuCardKitClient();
            var feishuChannel = new StubFeishuChannelService(activeSessionId)
            {
                ResolvedToolId = "codex"
            };
            var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider(), cardKit);

            await service.HandleCardActionAsync(
                """{"action":"execute_command"}""",
                chatId: chatId,
                operatorUserId: "ou_test_user",
                inputValues: "看下为什么卡住");

            await cliExecutor.WaitForExecutionCompletionAsync(TimeSpan.FromSeconds(3));
            await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

            Assert.Contains(cardKit.StreamingUpdates, update => update.Contains("apply_patch verification failed", StringComparison.Ordinal));
            Assert.Equal(rawError.Trim(), cardKit.FinalStreamingContent);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_WhenCardUpdateDisconnects_FreezesCardAndPersistsFinalAssistantOutput()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-card-disconnect";

        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-card-action-disconnect-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(workspaceRoot, "superpowers");
        Directory.CreateDirectory(workspacePath);

        try
        {
            var cliExecutor = new RecordingCliExecutorService
            {
                StandardExecutionContent = "第一段\n",
                StandardExecutionCompletionContent = "第二段\n"
            };
            cliExecutor.SetSessionWorkspacePath(activeSessionId, workspacePath);

            var cardKit = new StubFeishuCardKitClient
            {
                FailUpdateOnAttempt = 2
            };
            var chatSessionService = new StubChatSessionService();
            var feishuChannel = new StubFeishuChannelService(activeSessionId)
            {
                ResolvedToolId = "codex"
            };
            var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider(), cardKit, chatSessionService);

            await service.HandleCardActionAsync(
                """{"action":"execute_command"}""",
                chatId: chatId,
                operatorUserId: "ou_test_user",
                inputValues: "输出两段内容");

            await cliExecutor.WaitForExecutionCompletionAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(2, cardKit.UpdateAttemptCount);
            Assert.Single(cardKit.StreamingUpdates);
            Assert.Equal("第一段", cardKit.StreamingUpdates[0]);
            Assert.NotNull(cardKit.FinalStreamingContent);
            Assert.Contains("第一段", cardKit.FinalStreamingContent!, StringComparison.Ordinal);
            Assert.Contains("**错误：飞书流式更新断连，已停止继续推送卡片。**", cardKit.FinalStreamingContent!, StringComparison.Ordinal);
            Assert.Contains("执行出错", cardKit.FinalStreamingStatusMarkdown, StringComparison.Ordinal);
            Assert.Null(feishuChannel.LastSentMessage);
            Assert.Contains(
                chatSessionService.Messages[activeSessionId],
                message => message.Role == "assistant" && message.Content == "第一段\n第二段");
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_CreatesTopChipGroupsDisabledUntilCompletion()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-streaming-top-chips";

        var cliExecutor = new RecordingCliExecutorService();
        cliExecutor.SetSessionWorkspacePath(activeSessionId, @"D:\repo\superpowers");

        var cardKit = new StubFeishuCardKitClient();
        var feishuChannel = new StubFeishuChannelService(activeSessionId)
        {
            ResolvedToolId = "codex"
        };
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = activeSessionId,
                Username = "luhaiyan",
                Title = "Top Chips",
                ToolId = "codex",
                WorkspacePath = @"D:\repo\superpowers",
                ToolLaunchOverridesJson = SessionLaunchOverrideHelper.Serialize(
                    new Dictionary<string, SessionToolLaunchOverride>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["codex"] = new SessionToolLaunchOverride
                        {
                            Model = "gpt-5.4",
                            ReasoningEffort = "high"
                        }
                    }),
                FeishuChatKey = chatId,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                CreatedAt = DateTime.Now.AddMinutes(-30),
                UpdatedAt = DateTime.Now
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository),
            cardKit);

        await service.HandleCardActionAsync(
            """{"action":"execute_command"}""",
            chatId: chatId,
            inputValues: "缁х画");

        await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));
        await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

        var initialChrome = Assert.IsType<FeishuStreamingCardChrome>(cardKit.InitialStreamingChromeSnapshot);
        Assert.Contains(initialChrome.OverflowOptions, item => item.Text == "模型：gpt-5.4");
        Assert.Contains(initialChrome.OverflowOptions, item => item.Text == "模型：gpt-5.4-mini");
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
                Assert.Equal(["low", "medium", "high", "xhigh"], reasoningGroup.Items.Select(item => item.Text).ToArray());
            });

        var finalChrome = Assert.IsType<FeishuStreamingCardChrome>(cardKit.FinalStreamingChromeSnapshot);
        Assert.All(finalChrome.TopChipGroups, group => Assert.True(group.IsEnabled));
        Assert.All(finalChrome.TopChipGroups.SelectMany(group => group.Items), item => Assert.True(item.IsEnabled));
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_HighlightsCurrentLaunchStateFromCodexProjectConfig()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-streaming-config-top-chips";

        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-card-config-state-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(workspaceRoot, "superpowers");
        var codexDirectory = Path.Combine(workspacePath, ".codex");
        Directory.CreateDirectory(codexDirectory);
        await File.WriteAllTextAsync(
            Path.Combine(codexDirectory, "config.toml.base"),
            "model = \"gpt-5.4\"\nmodel_reasoning_effort = \"low\"\n");

        try
        {
            var cliExecutor = new RecordingCliExecutorService();
            cliExecutor.SetSessionWorkspacePath(activeSessionId, workspacePath);

            var cardKit = new StubFeishuCardKitClient();
            var feishuChannel = new StubFeishuChannelService(activeSessionId)
            {
                ResolvedToolId = "codex"
            };
            var sessionRepository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = activeSessionId,
                    Username = "luhaiyan",
                    Title = "Config Top Chips",
                    ToolId = "codex",
                    WorkspacePath = workspacePath,
                    FeishuChatKey = chatId,
                    IsWorkspaceValid = true,
                    IsFeishuActive = true,
                    CreatedAt = DateTime.Now.AddMinutes(-30),
                    UpdatedAt = DateTime.Now
                }
            ]);

            var service = CreateService(
                cliExecutor,
                feishuChannel,
                new TestServiceProvider(chatSessionRepository: sessionRepository),
                cardKit);

            await service.HandleCardActionAsync(
                """{"action":"execute_command"}""",
                chatId: chatId,
                inputValues: "缁х画");

            await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));
            await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

            var initialChrome = Assert.IsType<FeishuStreamingCardChrome>(cardKit.InitialStreamingChromeSnapshot);
            Assert.DoesNotContain(initialChrome.TopChipGroups, group => group.Kind == "model");
            Assert.Contains(initialChrome.TopChipGroups, group => group.Kind == "switch_hint" && !string.IsNullOrWhiteSpace(group.SummaryMarkdown));
            Assert.Contains(initialChrome.OverflowOptions, item => item.Text == "模型：gpt-5.4");
            Assert.Contains(initialChrome.TopChipGroups.SelectMany(group => group.Items), item => item.Text == "low" && item.IsActive);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_AttachesSuperpowersQuickActions_WhenPlanFilesExistAndSessionHistoryContainsSuperpowers()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-superpowers-quick-actions";

        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-superpowers-footer-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(workspaceRoot, "superpowers");
        Directory.CreateDirectory(Path.Combine(workspacePath, "docs", "superpowers", "plans"));
        await File.WriteAllTextAsync(
            Path.Combine(workspacePath, "docs", "superpowers", "plans", "approved-plan.md"),
            "# approved");

        try
        {
            var cliExecutor = new RecordingCliExecutorService
            {
                StandardExecutionContent = "plan completed"
            };
            cliExecutor.SetSessionWorkspacePath(activeSessionId, workspacePath);

            var chatSessionService = new StubChatSessionService();
            chatSessionService.Messages[activeSessionId] =
            [
                new ChatMessage
                {
                    Role = "user",
                    Content = "使用superpowers技能，先看一下计划",
                    IsCompleted = true,
                    CreatedAt = DateTime.UtcNow.AddMinutes(-2)
                }
            ];

            var cardKit = new StubFeishuCardKitClient();
            var feishuChannel = new StubFeishuChannelService(activeSessionId);
            var sessionRepository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = activeSessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    WorkspacePath = workspacePath,
                    FeishuChatKey = chatId,
                    IsFeishuActive = true,
                    ToolLaunchOverridesJson = "{\"codex\":{\"usePersistentProcess\":true}}",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                    UpdatedAt = DateTime.UtcNow
                }
            ]);
            var service = CreateService(
                cliExecutor,
                feishuChannel,
                new TestServiceProvider(chatSessionRepository: sessionRepository),
                cardKit,
                chatSessionService);

            await service.HandleCardActionAsync(
                """{"action":"execute_command"}""",
                chatId: chatId,
                inputValues: "继续");

            await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));
            await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

            Assert.NotNull(cardKit.LastStreamingChrome);
            var chrome = cardKit.LastStreamingChrome!;
            Assert.NotNull(chrome.BottomPrompt);
            Assert.Equal(SuperpowersQuickActionDefaults.QuickInputFieldName, chrome.BottomPrompt!.InputName);
            Assert.Equal(SuperpowersQuickActionDefaults.InstructionText, chrome.BottomPrompt.InputLabel);
            Assert.Equal(SuperpowersQuickActionDefaults.QuickInputPlaceholder, chrome.BottomPrompt.Placeholder);

            var quickInputValueJson = JsonSerializer.Serialize(chrome.BottomPrompt.Value);
            Assert.Contains($"\"action\":\"{FeishuHelpCardAction.SubmitSuperpowersQuickInputAction}\"", quickInputValueJson);
            Assert.Contains($"\"session_id\":\"{activeSessionId}\"", quickInputValueJson);
            Assert.Contains($"\"chat_key\":\"{chatId}\"", quickInputValueJson);

            var goalPrompt = Assert.Single(chrome.AdditionalBottomPrompts);
            Assert.Equal(GoalQuickActionDefaults.QuickInputFieldName, goalPrompt.InputName);
            Assert.Equal(GoalQuickActionDefaults.InstructionText, goalPrompt.InputLabel);
            Assert.Equal(GoalQuickActionDefaults.QuickInputPlaceholder, goalPrompt.Placeholder);

            var goalInputValueJson = JsonSerializer.Serialize(goalPrompt.Value);
            Assert.Contains($"\"action\":\"{FeishuHelpCardAction.SubmitGoalQuickInputAction}\"", goalInputValueJson);
            Assert.Contains($"\"session_id\":\"{activeSessionId}\"", goalInputValueJson);
            Assert.Contains($"\"chat_key\":\"{chatId}\"", goalInputValueJson);

            var initialChrome = Assert.IsType<FeishuStreamingCardChrome>(cardKit.InitialStreamingChromeSnapshot);
            Assert.Contains(initialChrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.StopButtonText);
            Assert.Equal(
            [
                "goal_row_1",
                "goal_row_1",
                "goal_row_2",
                "goal_row_2",
                "execution_control_row",
                "execution_control_row",
                "plan_action_row",
                "plan_action_row"
            ],
            initialChrome.BottomActions.Select(action => action.RowKey).ToArray());

            Assert.Equal(7, chrome.BottomActions.Count);
            Assert.Equal(
            [
                GoalQuickActionDefaults.StatusButtonText,
                GoalQuickActionDefaults.PauseButtonText,
                GoalQuickActionDefaults.ClearButtonText,
                GoalQuickActionDefaults.ResumeButtonText,
                SuperpowersQuickActionDefaults.ContinueButtonText,
                SuperpowersQuickActionDefaults.ExecutePlanButtonText,
                SuperpowersQuickActionDefaults.ExecuteSubagentPlanButtonText
            ],
            chrome.BottomActions.Select(action => action.Text).ToArray());
            Assert.Contains(chrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.ContinueButtonText);
            Assert.Contains(chrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.ExecutePlanButtonText);
            Assert.Contains(chrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.ExecuteSubagentPlanButtonText);
            Assert.Contains(chrome.BottomActions, action => action.Text == "/goal");
            Assert.Contains(chrome.BottomActions, action => action.Text == "/goal pause");
            Assert.Contains(chrome.BottomActions, action => action.Text == "/goal clear");
            Assert.Contains(chrome.BottomActions, action => action.Text == "/goal resume");
            Assert.DoesNotContain(chrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.StopButtonText);

            var continueValueJson = JsonSerializer.Serialize(
                Assert.Single(chrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.ContinueButtonText).Value);
            Assert.Contains($"\"action\":\"{FeishuHelpCardAction.ContinueSuperpowersAction}\"", continueValueJson);

            var executePlanValueJson = JsonSerializer.Serialize(
                Assert.Single(chrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.ExecutePlanButtonText).Value);
            Assert.Contains($"\"action\":\"{FeishuHelpCardAction.ExecuteSuperpowersPlanAction}\"", executePlanValueJson);

            var executeSubagentValueJson = JsonSerializer.Serialize(
                Assert.Single(chrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.ExecuteSubagentPlanButtonText).Value);
            Assert.Contains($"\"action\":\"{FeishuHelpCardAction.ExecuteSuperpowersSubagentPlanAction}\"", executeSubagentValueJson);

            var statusGoalValueJson = JsonSerializer.Serialize(
                Assert.Single(chrome.BottomActions, action => action.Text == "/goal").Value);
            Assert.Contains("\"action\":\"status_goal\"", statusGoalValueJson);

            var pauseGoalValueJson = JsonSerializer.Serialize(
                Assert.Single(chrome.BottomActions, action => action.Text == "/goal pause").Value);
            Assert.Contains("\"action\":\"pause_goal\"", pauseGoalValueJson);

            var clearGoalValueJson = JsonSerializer.Serialize(
                Assert.Single(chrome.BottomActions, action => action.Text == "/goal clear").Value);
            Assert.Contains("\"action\":\"clear_goal\"", clearGoalValueJson);

            var resumeGoalValueJson = JsonSerializer.Serialize(
                Assert.Single(chrome.BottomActions, action => action.Text == "/goal resume").Value);
            Assert.Contains("\"action\":\"resume_goal\"", resumeGoalValueJson);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_AttachesQuickInputAndKeepsContinueAction_WhenPlanFilesMissing()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-superpowers-no-plan";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionContent = "plan completed"
        };
        cliExecutor.SetSessionWorkspacePath(activeSessionId, @"D:\repo\superpowers");

        var chatSessionService = new StubChatSessionService();
        chatSessionService.Messages[activeSessionId] =
        [
            new ChatMessage
            {
                Role = "user",
                Content = "superpowers",
                IsCompleted = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(-2)
            }
        ];

        var cardKit = new StubFeishuCardKitClient();
        var feishuChannel = new StubFeishuChannelService(activeSessionId);
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = activeSessionId,
                Username = "luhaiyan",
                ToolId = "codex",
                WorkspacePath = @"D:\repo\superpowers",
                FeishuChatKey = chatId,
                IsFeishuActive = true,
                ToolLaunchOverridesJson = "{\"codex\":{\"usePersistentProcess\":true}}",
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAt = DateTime.UtcNow
            }
        ]);
        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository),
            cardKit,
            chatSessionService);

        await service.HandleCardActionAsync(
            """{"action":"execute_command"}""",
            chatId: chatId,
            inputValues: "继续");

        await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));
        await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

        Assert.NotNull(cardKit.LastStreamingChrome);
        var chrome = cardKit.LastStreamingChrome!;
        Assert.NotNull(chrome.BottomPrompt);
        Assert.Equal(SuperpowersQuickActionDefaults.QuickInputFieldName, chrome.BottomPrompt!.InputName);
        var initialChrome = Assert.IsType<FeishuStreamingCardChrome>(cardKit.InitialStreamingChromeSnapshot);
        Assert.Contains(initialChrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.StopButtonText);
        Assert.Equal(
        [
            "goal_row_1",
            "goal_row_1",
            "goal_row_2",
            "goal_row_2",
            "execution_control_row",
            "execution_control_row"
        ],
        initialChrome.BottomActions.Select(action => action.RowKey).ToArray());
        Assert.Equal(
        [
            GoalQuickActionDefaults.StatusButtonText,
            GoalQuickActionDefaults.PauseButtonText,
            GoalQuickActionDefaults.ClearButtonText,
            GoalQuickActionDefaults.ResumeButtonText,
            SuperpowersQuickActionDefaults.ContinueButtonText
        ],
        chrome.BottomActions.Select(action => action.Text).ToArray());
        Assert.Contains(chrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.ContinueButtonText);
        Assert.Contains(chrome.BottomActions, action => action.Text == GoalQuickActionDefaults.PauseButtonText);
        Assert.Contains(chrome.BottomActions, action => action.Text == GoalQuickActionDefaults.ClearButtonText);
        Assert.Contains(chrome.BottomActions, action => action.Text == GoalQuickActionDefaults.ResumeButtonText);
        Assert.DoesNotContain(chrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.StopButtonText);
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_HidesGoalButtons_WhenUsingOneShotProcess()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-superpowers-one-shot";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionContent = "plan completed"
        };
        cliExecutor.SetSessionWorkspacePath(activeSessionId, @"D:\repo\superpowers");
        cliExecutor.SetToolUsePersistentProcess("codex", false);

        var chatSessionService = new StubChatSessionService();
        chatSessionService.Messages[activeSessionId] =
        [
            new ChatMessage
            {
                Role = "user",
                Content = "superpowers",
                IsCompleted = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(-2)
            }
        ];

        var cardKit = new StubFeishuCardKitClient();
        var feishuChannel = new StubFeishuChannelService(activeSessionId);
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = activeSessionId,
                Username = "luhaiyan",
                ToolId = "codex",
                WorkspacePath = @"D:\repo\superpowers",
                FeishuChatKey = chatId,
                IsFeishuActive = true,
                ToolLaunchOverridesJson = "{\"codex\":{\"usePersistentProcess\":false}}",
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAt = DateTime.UtcNow
            }
        ]);
        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository),
            cardKit,
            chatSessionService);

        await service.HandleCardActionAsync(
            """{"action":"execute_command"}""",
            chatId: chatId,
            inputValues: "继续");

        await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));
        await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

        Assert.NotNull(cardKit.LastStreamingChrome);
        var chrome = cardKit.LastStreamingChrome!;
        Assert.DoesNotContain(chrome.BottomActions, action => action.Text == GoalQuickActionDefaults.StatusButtonText);
        Assert.DoesNotContain(chrome.BottomActions, action => action.Text == GoalQuickActionDefaults.PauseButtonText);
        Assert.DoesNotContain(chrome.BottomActions, action => action.Text == GoalQuickActionDefaults.ClearButtonText);
        Assert.DoesNotContain(chrome.BottomActions, action => action.Text == GoalQuickActionDefaults.ResumeButtonText);
        Assert.Contains(chrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.ContinueButtonText);
    }

    [Fact]
    public async Task HandleCardActionAsync_SuperpowersQuickAction_WhenActiveSessionChanged_ReturnsConfirmCardWithoutExecuting()
    {
        const string chatId = "oc_current_chat";
        const string boundSessionId = "session-bound";
        const string currentSessionId = "session-current";
        const string existingReply = "这是之前已经输出完成的回复内容";

        var cliExecutor = new RecordingCliExecutorService();
        cliExecutor.SetSessionWorkspacePath(boundSessionId, @"D:\repo\bound");
        cliExecutor.SetSessionWorkspacePath(currentSessionId, @"D:\repo\current");

        var chatSessionService = new StubChatSessionService();
        chatSessionService.Messages[boundSessionId] =
        [
            new ChatMessage
            {
                Role = "assistant",
                Content = existingReply,
                IsCompleted = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(-1)
            }
        ];

        var feishuChannel = new StubFeishuChannelService(currentSessionId);
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = boundSessionId,
                Username = "luhaiyan",
                ToolId = "codex",
                WorkspacePath = @"D:\repo\bound",
                FeishuChatKey = chatId,
                IsFeishuActive = false,
                ToolLaunchOverridesJson = "{\"codex\":{\"usePersistentProcess\":true}}",
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
            },
            new ChatSessionEntity
            {
                SessionId = currentSessionId,
                Username = "luhaiyan",
                ToolId = "codex",
                WorkspacePath = @"D:\repo\current",
                FeishuChatKey = chatId,
                IsFeishuActive = true,
                ToolLaunchOverridesJson = "{\"codex\":{\"usePersistentProcess\":true}}",
                CreatedAt = DateTime.UtcNow.AddMinutes(-4),
                UpdatedAt = DateTime.UtcNow
            }
        ]);
        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository),
            chatSessionService: chatSessionService);

        var response = await service.HandleCardActionAsync(
            $$"""{"action":"{{FeishuHelpCardAction.ContinueSuperpowersAction}}","session_id":"{{boundSessionId}}","chat_key":"{{chatId}}","tool_id":"codex"}""",
            chatId: chatId);

        Assert.Equal("⚠️ 当前激活会话已变化，请先确认要执行的会话", ExtractToastContent(response));
        var cardContents = ExtractCardContentStrings(response);
        Assert.Contains(cardContents, content => content.Contains("回复内容", StringComparison.Ordinal));
        Assert.Contains(cardContents, content => content.Contains(existingReply, StringComparison.Ordinal));
        Assert.Contains(cardContents, content => content.Contains("Superpowers 工作流", StringComparison.Ordinal));
        Assert.Contains(cardContents, content => content.Contains(boundSessionId, StringComparison.Ordinal));
        Assert.Contains(cardContents, content => content.Contains(currentSessionId, StringComparison.Ordinal));
        Assert.False(cliExecutor.WasExecuted);
    }

    [Fact]
    public async Task HandleCardActionAsync_SuperpowersQuickAction_ConfirmBoundSession_ExecutesOnBoundSession()
    {
        const string chatId = "oc_current_chat";
        const string boundSessionId = "session-bound";
        const string currentSessionId = "session-current";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionContent = "confirmed"
        };
        cliExecutor.SetSessionWorkspacePath(boundSessionId, @"D:\repo\bound");
        cliExecutor.SetSessionWorkspacePath(currentSessionId, @"D:\repo\current");

        var chatSessionService = new StubChatSessionService();
        chatSessionService.Messages[boundSessionId] =
        [
            new ChatMessage
            {
                Role = "user",
                Content = "superpowers",
                IsCompleted = true,
                CreatedAt = DateTime.UtcNow.AddMinutes(-2)
            }
        ];

        var cardKit = new StubFeishuCardKitClient();
        var feishuChannel = new StubFeishuChannelService(currentSessionId);
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = boundSessionId,
                Username = "luhaiyan",
                ToolId = "codex",
                WorkspacePath = @"D:\repo\bound",
                FeishuChatKey = chatId,
                IsFeishuActive = false,
                ToolLaunchOverridesJson = "{\"codex\":{\"usePersistentProcess\":true}}",
                CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                UpdatedAt = DateTime.UtcNow.AddMinutes(-5)
            },
            new ChatSessionEntity
            {
                SessionId = currentSessionId,
                Username = "luhaiyan",
                ToolId = "codex",
                WorkspacePath = @"D:\repo\current",
                FeishuChatKey = chatId,
                IsFeishuActive = true,
                ToolLaunchOverridesJson = "{\"codex\":{\"usePersistentProcess\":true}}",
                CreatedAt = DateTime.UtcNow.AddMinutes(-4),
                UpdatedAt = DateTime.UtcNow
            }
        ]);
        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository),
            cardKit,
            chatSessionService);

        await service.HandleCardActionAsync(
            $$"""{"action":"{{FeishuHelpCardAction.ConfirmBoundSuperpowersAction}}","session_id":"{{boundSessionId}}","chat_key":"{{chatId}}","tool_id":"codex","command":"{{FeishuHelpCardAction.ContinueSuperpowersAction}}"}""",
            chatId: chatId);

        var usedSessionId = await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(boundSessionId, usedSessionId);
        Assert.True(cliExecutor.WasExecuted);
        Assert.NotEmpty(cliExecutor.ExecutedPrompts);
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_AttachesQuickInputAndKeepsContinueAction_WhenSessionHistoryLacksSuperpowers()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-superpowers-no-history";

        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-superpowers-no-history-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(workspaceRoot, "superpowers");
        Directory.CreateDirectory(Path.Combine(workspacePath, "docs", "superpowers", "plans"));
        await File.WriteAllTextAsync(
            Path.Combine(workspacePath, "docs", "superpowers", "plans", "approved-plan.md"),
            "# approved");

        try
        {
            var cliExecutor = new RecordingCliExecutorService
            {
                StandardExecutionContent = "plan completed"
            };
            cliExecutor.SetSessionWorkspacePath(activeSessionId, workspacePath);

            var cardKit = new StubFeishuCardKitClient();
            var feishuChannel = new StubFeishuChannelService(activeSessionId);
            var sessionRepository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = activeSessionId,
                    Username = "luhaiyan",
                    ToolId = "codex",
                    WorkspacePath = workspacePath,
                    FeishuChatKey = chatId,
                    IsFeishuActive = true,
                    ToolLaunchOverridesJson = "{\"codex\":{\"usePersistentProcess\":true}}",
                    CreatedAt = DateTime.UtcNow.AddMinutes(-10),
                    UpdatedAt = DateTime.UtcNow
                }
            ]);
            var service = CreateService(
                cliExecutor,
                feishuChannel,
                new TestServiceProvider(chatSessionRepository: sessionRepository),
                cardKit);

            await service.HandleCardActionAsync(
                """{"action":"execute_command"}""",
                chatId: chatId,
                inputValues: "继续");

            await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));
            await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

            Assert.NotNull(cardKit.LastStreamingChrome);
            var chrome = cardKit.LastStreamingChrome!;
            Assert.NotNull(chrome.BottomPrompt);
            Assert.Equal(SuperpowersQuickActionDefaults.QuickInputFieldName, chrome.BottomPrompt!.InputName);
            Assert.Contains(chrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.ContinueButtonText);
            Assert.Contains(chrome.BottomActions, action => action.Text == GoalQuickActionDefaults.PauseButtonText);
            Assert.Contains(chrome.BottomActions, action => action.Text == GoalQuickActionDefaults.ClearButtonText);
            Assert.Contains(chrome.BottomActions, action => action.Text == GoalQuickActionDefaults.ResumeButtonText);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HandleCardActionAsync_SubmitSuperpowersQuickInput_AutoPrefixesPromptAndUsesStandardExecutionPath()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-superpowers-quick-input";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionContent = "plan completed"
        };
        cliExecutor.SetSessionWorkspacePath(activeSessionId, @"D:\repo\superpowers");

        var cardKit = new StubFeishuCardKitClient();
        var feishuChannel = new StubFeishuChannelService(activeSessionId);
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider(), cardKit);

        var response = await service.HandleCardActionAsync(
            $$"""{"action":"{{FeishuHelpCardAction.SubmitSuperpowersQuickInputAction}}","chat_key":"{{chatId}}"}""",
            chatId: chatId,
            formValue: new Dictionary<string, object>
            {
                [SuperpowersQuickActionDefaults.QuickInputFieldName] = "写一个执行步骤"
            });

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Info, response.Toast?.Type);
        await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));
        await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(
            SuperpowersPromptBuilder.BuildQuickSkillPrompt("写一个执行步骤"),
            Assert.Single(cliExecutor.ExecutedPrompts));
        Assert.Empty(cliExecutor.LowInterruptionSessionIds);
    }

    [Fact]
    public async Task HandleCardActionAsync_SubmitSuperpowersQuickInput_UsesInputValuesWhenFormValueIsMissing()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-superpowers-quick-input-from-input";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionContent = "plan completed"
        };
        cliExecutor.SetSessionWorkspacePath(activeSessionId, @"D:\repo\superpowers");

        var cardKit = new StubFeishuCardKitClient();
        var feishuChannel = new StubFeishuChannelService(activeSessionId);
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider(), cardKit);

        var response = await service.HandleCardActionAsync(
            $$"""{"action":"{{FeishuHelpCardAction.SubmitSuperpowersQuickInputAction}}","chat_key":"{{chatId}}"}""",
            chatId: chatId,
            inputValues: "写一个执行步骤");

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Info, response.Toast?.Type);
        await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));
        await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(
            SuperpowersPromptBuilder.BuildQuickSkillPrompt("写一个执行步骤"),
            Assert.Single(cliExecutor.ExecutedPrompts));
    }

    [Fact]
    public async Task HandleCardActionAsync_SubmitSuperpowersQuickInput_DoesNotDoublePrefix()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-superpowers-quick-input-prefixed";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionContent = "plan completed"
        };
        cliExecutor.SetSessionWorkspacePath(activeSessionId, @"D:\repo\superpowers");

        var cardKit = new StubFeishuCardKitClient();
        var feishuChannel = new StubFeishuChannelService(activeSessionId);
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider(), cardKit);

        await service.HandleCardActionAsync(
            $$"""{"action":"{{FeishuHelpCardAction.SubmitSuperpowersQuickInputAction}}","chat_key":"{{chatId}}"}""",
            chatId: chatId,
            formValue: new Dictionary<string, object>
            {
                [SuperpowersQuickActionDefaults.QuickInputFieldName] = "$superpowers ，使用superpowers技能，写一个执行步骤"
            });

        await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));
        await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(
            SuperpowersPromptBuilder.BuildQuickSkillPrompt("$using-superpowers ，使用superpowers技能，写一个执行步骤"),
            Assert.Single(cliExecutor.ExecutedPrompts));
    }

    [Fact]
    public async Task HandleCardActionAsync_SubmitGoalQuickInput_AutoPrefixesPromptAndUsesStandardExecutionPath()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-goal-quick-input";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionContent = "goal completed"
        };
        cliExecutor.SetSessionWorkspacePath(activeSessionId, @"D:\repo\superpowers");

        var capabilityService = new StubGoalCapabilityService
        {
            ProbeState = GoalCapabilityState.Available,
            ProbeOutcome = GoalCapabilityProbeOutcome.Available
        };

        var feishuChannel = new StubFeishuChannelService(activeSessionId)
        {
            ResolvedToolId = "codex"
        };
        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(goalCapabilityService: capabilityService));

        var response = await service.HandleCardActionAsync(
            $$"""{"action":"{{FeishuHelpCardAction.SubmitGoalQuickInputAction}}","chat_key":"{{chatId}}"}""",
            chatId: chatId,
            formValue: new Dictionary<string, object>
            {
                [GoalQuickActionDefaults.QuickInputFieldName] = "整理这个目标"
            });

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Info, response.Toast?.Type);
        await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(
            GoalPromptBuilder.BuildGoalPrompt("整理这个目标"),
            Assert.Single(cliExecutor.ExecutedPrompts));

        var probeContext = Assert.Single(capabilityService.ProbeContexts);
        Assert.Equal("codex", probeContext.ToolId);
    }

    [Fact]
    public async Task HandleCardActionAsync_SubmitGoalQuickInput_UsesInputValuesWhenFormValueIsMissing()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-goal-quick-input-from-input";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionContent = "goal completed"
        };
        cliExecutor.SetSessionWorkspacePath(activeSessionId, @"D:\repo\superpowers");

        var capabilityService = new StubGoalCapabilityService
        {
            ProbeState = GoalCapabilityState.Available,
            ProbeOutcome = GoalCapabilityProbeOutcome.Available
        };

        var feishuChannel = new StubFeishuChannelService(activeSessionId)
        {
            ResolvedToolId = "codex"
        };
        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(goalCapabilityService: capabilityService));

        var response = await service.HandleCardActionAsync(
            $$"""{"action":"{{FeishuHelpCardAction.SubmitGoalQuickInputAction}}","chat_key":"{{chatId}}"}""",
            chatId: chatId,
            inputValues: "整理这个目标");

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Info, response.Toast?.Type);
        await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(
            GoalPromptBuilder.BuildGoalPrompt("整理这个目标"),
            Assert.Single(cliExecutor.ExecutedPrompts));
    }

    [Fact]
    public async Task HandleCardActionAsync_SubmitGoalQuickInput_DoesNotDoublePrefix()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-goal-quick-input-prefixed";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionContent = "goal completed"
        };
        cliExecutor.SetSessionWorkspacePath(activeSessionId, @"D:\repo\superpowers");

        var capabilityService = new StubGoalCapabilityService
        {
            ProbeState = GoalCapabilityState.Available,
            ProbeOutcome = GoalCapabilityProbeOutcome.Available
        };

        var feishuChannel = new StubFeishuChannelService(activeSessionId)
        {
            ResolvedToolId = "codex"
        };
        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(goalCapabilityService: capabilityService));

        await service.HandleCardActionAsync(
            $$"""{"action":"{{FeishuHelpCardAction.SubmitGoalQuickInputAction}}","chat_key":"{{chatId}}"}""",
            chatId: chatId,
            formValue: new Dictionary<string, object>
            {
                [GoalQuickActionDefaults.QuickInputFieldName] = "/goal 整理这个目标"
            });

        await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(
            GoalPromptBuilder.BuildGoalPrompt("/goal 整理这个目标"),
            Assert.Single(cliExecutor.ExecutedPrompts));
    }

    [Theory]
    [InlineData("status_goal", "/goal")]
    [InlineData("clear_goal", "/goal clear")]
    [InlineData("resume_goal", "/goal resume")]
    public async Task HandleCardActionAsync_GoalActionButtons_ExecuteExpectedPrompt(string actionName, string expectedPrompt)
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-goal-actions";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionContent = "goal completed"
        };
        cliExecutor.SetSessionWorkspacePath(activeSessionId, @"D:\repo\superpowers");

        var capabilityService = new StubGoalCapabilityService
        {
            ProbeState = GoalCapabilityState.Available,
            ProbeOutcome = GoalCapabilityProbeOutcome.Available
        };

        var feishuChannel = new StubFeishuChannelService(activeSessionId)
        {
            ResolvedToolId = "codex"
        };
        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(goalCapabilityService: capabilityService));

        var response = await service.HandleCardActionAsync(
            $$"""{"action":"{{actionName}}","chat_key":"{{chatId}}"}""",
            chatId: chatId);

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Info, response.Toast?.Type);
        await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(expectedPrompt, Assert.Single(cliExecutor.ExecutedPrompts));

        var probeContext = Assert.Single(capabilityService.ProbeContexts);
        Assert.Equal("codex", probeContext.ToolId);
    }

    [Fact]
    public async Task HandleCardActionAsync_PauseGoal_StopsSessionAndSkipsGoalPromptExecution()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-goal-pause";

        var cliExecutor = new RecordingCliExecutorService();
        var capabilityService = new StubGoalCapabilityService
        {
            ProbeState = GoalCapabilityState.Available,
            ProbeOutcome = GoalCapabilityProbeOutcome.Available
        };

        var feishuChannel = new StubFeishuChannelService(activeSessionId)
        {
            ResolvedToolId = "codex"
        };
        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(goalCapabilityService: capabilityService));

        var response = await service.HandleCardActionAsync(
            $$"""{"action":"{{FeishuHelpCardAction.PauseGoalAction}}","chat_key":"{{chatId}}"}""",
            chatId: chatId);

        Assert.Single(cliExecutor.StopRequests);
        Assert.Equal((activeSessionId, "codex"), cliExecutor.StopRequests[0]);
        Assert.Empty(cliExecutor.ExecutedPrompts);
        Assert.Empty(capabilityService.ProbeContexts);
        Assert.Equal("✅ 已请求停止当前执行", response.Toast?.Content);
        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Success, response.Toast?.Type);
    }

    [Fact]
    public async Task HandleCardActionAsync_ContinueSuperpowers_UsesFixedPromptAndSkipsCapabilityProbe()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-superpowers-continue";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionContent = "continued"
        };
        cliExecutor.SetSessionWorkspacePath(activeSessionId, @"D:\repo\superpowers");

        var capabilityService = new StubSuperpowersCapabilityService
        {
            ProbeState = SuperpowersCapabilityState.Unavailable,
            ProbeOutcome = SuperpowersCapabilityProbeOutcome.MissingCapability,
            ProbeMessage = "missing"
        };

        var feishuChannel = new StubFeishuChannelService(activeSessionId);
        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(superpowersCapabilityService: capabilityService));

        await service.HandleCardActionAsync(
            $$"""{"action":"{{FeishuHelpCardAction.ContinueSuperpowersAction}}","chat_key":"{{chatId}}"}""",
            chatId: chatId);

        await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(
            SuperpowersPromptBuilder.BuildContinuePrompt(),
            Assert.Single(cliExecutor.ExecutedPrompts));
        Assert.Empty(capabilityService.ProbeContexts);
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteSuperpowersPlan_UsesFixedPromptAndStandardExecutionPath()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-superpowers-execute-plan";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionContent = "plan completed"
        };
        cliExecutor.SetSessionWorkspacePath(activeSessionId, @"D:\repo\superpowers");

        var feishuChannel = new StubFeishuChannelService(activeSessionId);
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider());

        await service.HandleCardActionAsync(
            $$"""{"action":"{{FeishuHelpCardAction.ExecuteSuperpowersPlanAction}}","chat_key":"{{chatId}}"}""",
            chatId: chatId);

        await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(
            SuperpowersPromptBuilder.BuildExecutePlanPrompt(),
        Assert.Single(cliExecutor.ExecutedPrompts));
        Assert.Empty(cliExecutor.LowInterruptionSessionIds);
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteSuperpowersPlan_UsesSnapshotToolForProbeAndExecution()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-superpowers-snapshot-tool";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionContent = "plan completed"
        };
        cliExecutor.SetSessionWorkspacePath(activeSessionId, @"D:\repo\superpowers");

        var capabilityService = new StubSuperpowersCapabilityService
        {
            ProbeState = SuperpowersCapabilityState.Available,
            ProbeOutcome = SuperpowersCapabilityProbeOutcome.Available
        };

        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = activeSessionId,
                ToolId = "claude-code",
                CcSwitchSnapshotToolId = "codex",
                FeishuChatKey = chatId,
                IsFeishuActive = true
            }
        ]);

        var service = CreateService(
            cliExecutor,
            new StubFeishuChannelService(activeSessionId),
            new TestServiceProvider(
                chatSessionRepository: sessionRepository,
                superpowersCapabilityService: capabilityService));

        await service.HandleCardActionAsync(
            $$"""{"action":"{{FeishuHelpCardAction.ExecuteSuperpowersPlanAction}}","chat_key":"{{chatId}}","session_id":"{{activeSessionId}}"}""",
            chatId: chatId);

        await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));

        var probeContext = Assert.Single(capabilityService.ProbeContexts);
        Assert.Equal("codex", probeContext.ToolId);

        var execution = Assert.Single(cliExecutor.StandardExecutionRequests);
        Assert.Equal(activeSessionId, execution.SessionId);
        Assert.Equal("codex", execution.ToolId);
        Assert.Equal(SuperpowersPromptBuilder.BuildExecutePlanPrompt(), execution.Prompt);
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteSuperpowersSubagentPlan_UsesFixedPromptAndStandardExecutionPath()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-superpowers-execute-subagent-plan";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionContent = "plan completed"
        };
        cliExecutor.SetSessionWorkspacePath(activeSessionId, @"D:\repo\superpowers");

        var feishuChannel = new StubFeishuChannelService(activeSessionId);
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider());

        await service.HandleCardActionAsync(
            $$"""{"action":"{{FeishuHelpCardAction.ExecuteSuperpowersSubagentPlanAction}}","chat_key":"{{chatId}}"}""",
            chatId: chatId);

        await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(
            SuperpowersPromptBuilder.BuildSubagentExecutePlanPrompt(),
            Assert.Single(cliExecutor.ExecutedPrompts));
        Assert.Empty(cliExecutor.LowInterruptionSessionIds);
    }

    [Fact]
    public async Task HandleCardActionAsync_SuperpowersQuickAction_WhenSessionAlreadyRunning_ReturnsWarning()
    {
        const string chatId = "oc_current_chat";
        const string sessionId = "session-superpowers-busy";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionContent = "plan completed"
        };
        cliExecutor.SetSessionWorkspacePath(sessionId, @"D:\repo\superpowers");

        var feishuChannel = new StubFeishuChannelService(sessionId)
        {
            SessionExecutionActive = true
        };
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider());

        var response = await service.HandleCardActionAsync(
            $$"""{"action":"{{FeishuHelpCardAction.ExecuteSuperpowersPlanAction}}","chat_key":"{{chatId}}","session_id":"{{sessionId}}"}""",
            chatId: chatId);

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Warning, response.Toast?.Type);
        Assert.Contains("已有任务在执行", response.Toast?.Content, StringComparison.Ordinal);
        Assert.Empty(cliExecutor.ExecutedPrompts);
    }

    [Fact]
    public async Task HandleCardActionAsync_SuperpowersQuickAction_WhenCapabilityMissing_ReturnsWarningWithoutExecuting()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-superpowers-missing-capability";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionContent = "plan completed"
        };
        cliExecutor.SetSessionWorkspacePath(activeSessionId, @"D:\repo\superpowers");

        var capabilityService = new StubSuperpowersCapabilityService
        {
            ProbeState = SuperpowersCapabilityState.Unavailable,
            ProbeOutcome = SuperpowersCapabilityProbeOutcome.MissingCapability,
            ProbeMessage = SuperpowersQuickActionDefaults.CapabilityUnavailableText
        };

        var feishuChannel = new StubFeishuChannelService(activeSessionId);
        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(superpowersCapabilityService: capabilityService));

        var response = await service.HandleCardActionAsync(
            $$"""{"action":"{{FeishuHelpCardAction.ExecuteSuperpowersPlanAction}}","chat_key":"{{chatId}}","session_id":"{{activeSessionId}}"}""",
            chatId: chatId);

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Warning, response.Toast?.Type);
        Assert.Contains(SuperpowersQuickActionDefaults.CapabilityUnavailableText, response.Toast?.Content, StringComparison.Ordinal);
        Assert.Empty(cliExecutor.ExecutedPrompts);
    }

    [Fact]
    public async Task HandleCardActionAsync_GoalQuickAction_WhenCapabilityMissing_ReturnsWarningWithoutExecuting()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-goal-missing-capability";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionContent = "goal completed"
        };
        cliExecutor.SetSessionWorkspacePath(activeSessionId, @"D:\repo\superpowers");

        var capabilityService = new StubGoalCapabilityService
        {
            ProbeState = GoalCapabilityState.Unavailable,
            ProbeOutcome = GoalCapabilityProbeOutcome.MissingFeature,
            ProbeMessage = GoalQuickActionDefaults.CapabilityUnavailableText
        };

        var feishuChannel = new StubFeishuChannelService(activeSessionId)
        {
            ResolvedToolId = "codex"
        };
        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(goalCapabilityService: capabilityService));

        var response = await service.HandleCardActionAsync(
            $$"""{"action":"{{FeishuHelpCardAction.SubmitGoalQuickInputAction}}","chat_key":"{{chatId}}"}""",
            chatId: chatId,
            formValue: new Dictionary<string, object>
            {
                [GoalQuickActionDefaults.QuickInputFieldName] = "整理这个目标"
            });

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Warning, response.Toast?.Type);
        Assert.Contains(GoalQuickActionDefaults.CapabilityUnavailableText, response.Toast?.Content, StringComparison.Ordinal);
        Assert.Empty(cliExecutor.ExecutedPrompts);
    }

    [Fact]
    public async Task HandleCardActionAsync_RetrySuperpowersCapabilityDetection_WhenCapabilityAvailable_ReturnsSuccessToast()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-superpowers-retry";

        var capabilityService = new StubSuperpowersCapabilityService
        {
            ProbeState = SuperpowersCapabilityState.Available,
            ProbeOutcome = SuperpowersCapabilityProbeOutcome.Available
        };

        var response = await CreateService(
                new RecordingCliExecutorService(),
                new StubFeishuChannelService(activeSessionId),
                new TestServiceProvider(superpowersCapabilityService: capabilityService))
            .HandleCardActionAsync(
                $$"""{"action":"{{FeishuHelpCardAction.RetrySuperpowersCapabilityDetectionAction}}","chat_key":"{{chatId}}","session_id":"{{activeSessionId}}","tool_id":"codex"}""",
                chatId: chatId);

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Success, response.Toast?.Type);
        Assert.Contains("已重新检测", response.Toast?.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleCardActionAsync_StopStreamingExecution_StopsSessionAndReturnsSuccessToast()
    {
        const string chatId = "oc_current_chat";
        const string sessionId = "session-stop";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(sessionId);
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider());

        var response = await service.HandleCardActionAsync(
            $$"""{"action":"{{FeishuHelpCardAction.StopStreamingExecutionAction}}","chat_key":"{{chatId}}","session_id":"{{sessionId}}","tool_id":"codex"}""",
            chatId: chatId);

        Assert.Single(cliExecutor.StopRequests);
        Assert.Equal((sessionId, "codex"), cliExecutor.StopRequests[0]);
        Assert.Equal(sessionId, feishuChannel.LastStoppedSessionId);
        Assert.Equal("✅ 已请求停止当前执行", response.Toast?.Content);
        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Success, response.Toast?.Type);
    }

    [Fact]
    public async Task HandleCardActionAsync_LowInterruptionContinue_StartsNewStreamingCardWithDefaultPromptForm()
    {
        const string chatId = "oc_current_chat";
        const string sessionId = "session-low-interruption-run";

        var cliExecutor = new RecordingCliExecutorService
        {
            SupportsLowInterruption = true,
            LowInterruptionExecutionContent = "backlog remains"
        };
        cliExecutor.SetCliThreadId(sessionId, "thread-low-interruption");
        cliExecutor.SetSessionWorkspacePath(sessionId, @"D:\repo\superpowers");

        var cardKit = new StubFeishuCardKitClient();
        var feishuChannel = new StubFeishuChannelService(sessionId);
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider(), cardKit);

        var response = await service.HandleCardActionAsync(
            """{"action":"low_interruption_continue","session_id":"session-low-interruption-run","chat_key":"oc_current_chat","tool_id":"codex"}""",
            chatId: chatId);

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Info, response.Toast?.Type);

        var usedSessionId = await cliExecutor.WaitForLowInterruptionExecutionAsync(TimeSpan.FromSeconds(3));
        await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(sessionId, usedSessionId);
        Assert.Empty(cliExecutor.ExecutedPrompts);
        Assert.Equal([sessionId], cliExecutor.LowInterruptionSessionIds);
        var initialChrome = Assert.IsType<FeishuStreamingCardChrome>(cardKit.InitialStreamingChromeSnapshot);
        Assert.Contains(initialChrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.StopButtonText);
        Assert.NotNull(cardKit.LastStreamingChrome);
        Assert.NotNull(cardKit.LastStreamingChrome!.BottomPrompt);
        Assert.Contains(cardKit.LastStreamingChrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.ContinueButtonText);
        Assert.Contains(cardKit.LastStreamingChrome.BottomActions, action => action.Text == GoalQuickActionDefaults.PauseButtonText);
        Assert.Contains(cardKit.LastStreamingChrome.BottomActions, action => action.Text == GoalQuickActionDefaults.ClearButtonText);
        Assert.Contains(cardKit.LastStreamingChrome.BottomActions, action => action.Text == GoalQuickActionDefaults.ResumeButtonText);
        Assert.DoesNotContain(cardKit.LastStreamingChrome.BottomActions, action => action.Text == SuperpowersQuickActionDefaults.StopButtonText);
    }

    [Fact]
    public async Task HandleCardActionAsync_LowInterruptionContinue_PassesPromptFromFormValue()
    {
        const string chatId = "oc_current_chat";
        const string sessionId = "session-low-interruption-run";

        var cliExecutor = new RecordingCliExecutorService
        {
            SupportsLowInterruption = true,
            LowInterruptionExecutionContent = "backlog remains"
        };
        cliExecutor.SetCliThreadId(sessionId, "thread-low-interruption");
        cliExecutor.SetSessionWorkspacePath(sessionId, @"D:\repo\superpowers");

        var cardKit = new StubFeishuCardKitClient();
        var feishuChannel = new StubFeishuChannelService(sessionId);
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider(), cardKit);

        var response = await service.HandleCardActionAsync(
            """{"action":"low_interruption_continue","session_id":"session-low-interruption-run","chat_key":"oc_current_chat","tool_id":"codex"}""",
            chatId: chatId,
            formValue: new Dictionary<string, object>
            {
                [LowInterruptionContinueDefaults.PromptFieldName] = "finish the remaining plan items"
            });

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Info, response.Toast?.Type);

        var usedSessionId = await cliExecutor.WaitForLowInterruptionExecutionAsync(TimeSpan.FromSeconds(3));
        await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(sessionId, usedSessionId);
        Assert.Equal(["finish the remaining plan items"], cliExecutor.LowInterruptionPrompts);
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_QueuesReplyTtsAfterSuccessfulCompletion()
    {
        const string chatId = "oc_reply_tts_card_chat";
        const string sessionId = "session-reply-tts-card";
        const string appId = "cli_reply_tts_bot";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionContent = "card action completed"
        };
        cliExecutor.SetSessionWorkspacePath(sessionId, @"D:\repo\superpowers");

        var chatSessionService = new StubChatSessionService();
        var replyTtsOrchestrator = new RecordingReplyTtsOrchestrator();
        replyTtsOrchestrator.OnQueued = request =>
        {
            Assert.Contains(
                chatSessionService.Messages[sessionId],
                message => message.Role == "assistant" && message.Content == "card action completed" && message.IsCompleted);
            Assert.Equal("card action completed", request.Output);
            return Task.CompletedTask;
        };

        var service = CreateService(
            cliExecutor,
            new StubFeishuChannelService(sessionId),
            new TestServiceProvider(replyTtsOrchestrator: replyTtsOrchestrator),
            chatSessionService: chatSessionService);

        await service.HandleCardActionAsync(
            """{"action":"execute_command"}""",
            chatId: chatId,
            inputValues: "continue",
            appId: appId);

        var queued = await replyTtsOrchestrator.WhenQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await replyTtsOrchestrator.WhenCallbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(chatId, queued.ChatId);
        Assert.Equal("luhaiyan", queued.Username);
        Assert.Equal(appId, queued.AppId);
        Assert.Equal(sessionId, queued.SessionId);
        Assert.Equal("card action completed", queued.Output);
    }

    [Fact]
    public async Task HandleCardActionAsync_LowInterruptionContinue_QueuesReplyTtsAfterSuccessfulCompletion()
    {
        const string chatId = "oc_reply_tts_low_interruption_chat";
        const string sessionId = "session-reply-tts-low-interruption";
        const string appId = "cli_reply_tts_bot";

        var cliExecutor = new RecordingCliExecutorService
        {
            SupportsLowInterruption = true,
            LowInterruptionExecutionContent = "low interruption completed"
        };
        cliExecutor.SetCliThreadId(sessionId, "thread-reply-tts-low-interruption");
        cliExecutor.SetSessionWorkspacePath(sessionId, @"D:\repo\superpowers");

        var chatSessionService = new StubChatSessionService();
        var replyTtsOrchestrator = new RecordingReplyTtsOrchestrator();
        replyTtsOrchestrator.OnQueued = request =>
        {
            Assert.Contains(
                chatSessionService.Messages[sessionId],
                message => message.Role == "assistant" && message.Content == "low interruption completed" && message.IsCompleted);
            Assert.Equal("low interruption completed", request.Output);
            return Task.CompletedTask;
        };

        var service = CreateService(
            cliExecutor,
            new StubFeishuChannelService(sessionId),
            new TestServiceProvider(replyTtsOrchestrator: replyTtsOrchestrator),
            chatSessionService: chatSessionService);

        await service.HandleCardActionAsync(
            """{"action":"low_interruption_continue","session_id":"session-reply-tts-low-interruption","chat_key":"oc_reply_tts_low_interruption_chat","tool_id":"codex"}""",
            chatId: chatId,
            appId: appId);

        var queued = await replyTtsOrchestrator.WhenQueued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await replyTtsOrchestrator.WhenCallbackCompleted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(chatId, queued.ChatId);
        Assert.Equal("luhaiyan", queued.Username);
        Assert.Equal(appId, queued.AppId);
        Assert.Equal(sessionId, queued.SessionId);
        Assert.Equal("low interruption completed", queued.Output);
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_DoesNotQueueReplyTtsWhenExecutionErrors()
    {
        const string sessionId = "session-reply-tts-error";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionIsError = true,
            StandardExecutionErrorMessage = "execution failed"
        };
        cliExecutor.SetSessionWorkspacePath(sessionId, @"D:\repo\superpowers");

        var replyTtsOrchestrator = new RecordingReplyTtsOrchestrator();
        var service = CreateService(
            cliExecutor,
            new StubFeishuChannelService(sessionId),
            new TestServiceProvider(replyTtsOrchestrator: replyTtsOrchestrator));

        await service.HandleCardActionAsync(
            """{"action":"execute_command"}""",
            chatId: "oc_reply_tts_error_chat",
            inputValues: "continue");

        await cliExecutor.WaitForExecutionCompletionAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(replyTtsOrchestrator.Requests);
    }

    [Fact]
    public async Task HandleCardActionAsync_LowInterruptionContinue_DoesNotQueueReplyTtsWhenExecutionErrors()
    {
        const string sessionId = "session-reply-tts-low-interruption-error";

        var cliExecutor = new RecordingCliExecutorService
        {
            SupportsLowInterruption = true,
            LowInterruptionExecutionIsError = true,
            LowInterruptionExecutionErrorMessage = "low interruption failed"
        };
        cliExecutor.SetCliThreadId(sessionId, "thread-reply-tts-low-interruption-error");
        cliExecutor.SetSessionWorkspacePath(sessionId, @"D:\repo\superpowers");

        var replyTtsOrchestrator = new RecordingReplyTtsOrchestrator();
        var service = CreateService(
            cliExecutor,
            new StubFeishuChannelService(sessionId),
            new TestServiceProvider(replyTtsOrchestrator: replyTtsOrchestrator));

        await service.HandleCardActionAsync(
            """{"action":"low_interruption_continue","session_id":"session-reply-tts-low-interruption-error","chat_key":"oc_reply_tts_low_interruption_error_chat","tool_id":"codex"}""",
            chatId: "oc_reply_tts_low_interruption_error_chat");

        await cliExecutor.WaitForLowInterruptionExecutionCompletionAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(replyTtsOrchestrator.Requests);
    }

    [Fact]
    public async Task HandleCardActionAsync_LowInterruptionContinue_WhenThreadMissing_ReturnsWarning()
    {
        var cliExecutor = new RecordingCliExecutorService
        {
            SupportsLowInterruption = true
        };
        var feishuChannel = new StubFeishuChannelService("session-no-thread");
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider());

        var response = await service.HandleCardActionAsync(
            """{"action":"low_interruption_continue","session_id":"session-no-thread","chat_key":"oc_current_chat","tool_id":"codex"}""",
            chatId: "oc_current_chat");

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Warning, response.Toast?.Type);
        Assert.Contains("CLI 线程", response.Toast?.Content);
        Assert.Empty(cliExecutor.LowInterruptionSessionIds);
    }

    [Fact]
    public async Task HandleCardActionAsync_LowInterruptionContinue_WhenSessionAlreadyRunning_ReturnsWarning()
    {
        var cliExecutor = new RecordingCliExecutorService
        {
            SupportsLowInterruption = true
        };
        cliExecutor.SetCliThreadId("session-running", "thread-running");

        var feishuChannel = new StubFeishuChannelService("session-running")
        {
            SessionExecutionActive = true
        };
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider());

        var response = await service.HandleCardActionAsync(
            """{"action":"low_interruption_continue","session_id":"session-running","chat_key":"oc_current_chat","tool_id":"codex"}""",
            chatId: "oc_current_chat");

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Warning, response.Toast?.Type);
        Assert.Contains("已有任务在执行", response.Toast?.Content);
        Assert.Empty(cliExecutor.LowInterruptionSessionIds);
    }

    [Fact]
    public async Task HandleCardActionAsync_OpenSessionManager_SendAsNewCard_SendsSessionManagerCardToChat()
    {
        const string chatId = "oc_workspace_chat";

        var cliExecutor = new RecordingCliExecutorService();
        var cardKit = new StubFeishuCardKitClient();
        var feishuChannel = new StubFeishuChannelService(null);
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = "session-new-card",
                Username = "luhaiyan",
                WorkspacePath = @"D:\repo\superpowers",
                ToolId = "codex",
                FeishuChatKey = chatId,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                IsCustomWorkspace = true
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository),
            cardKit);

        var response = await service.HandleCardActionAsync(
            """{"action":"open_session_manager","chat_key":"oc_workspace_chat","send_as_new_card":true}""",
            chatId: chatId);

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Success, response.Toast?.Type);
        Assert.Contains("已发送会话管理卡片", response.Toast?.Content);

        var (sentChatId, cardJson) = await cardKit.WaitForRawCardSentAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(chatId, sentChatId);
        Assert.Contains("\"action\":\"show_create_session_form\"", cardJson);
        Assert.Contains("\"action\":\"switch_session\"", cardJson);
        Assert.Contains("\"action\":\"show_rename_session_form\"", cardJson);
        Assert.Contains("\"action\":\"show_session_launch_settings_form\"", cardJson);
        Assert.Contains("\"action\":\"sync_session_provider\"", cardJson);
        Assert.Contains("session-new", cardJson);
    }

    [Fact]
    public async Task HandleCardActionAsync_OpenSessionManager_DefaultsToRecentThreeSessionsUntilExpanded()
    {
        const string chatId = "oc_workspace_chat";
        const string currentSessionId = "session-visible-01";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(currentSessionId);
        var now = DateTime.Now;
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = currentSessionId,
                Username = "luhaiyan",
                Title = "Visible Session 01",
                WorkspacePath = @"D:\repo\visible-01",
                ToolId = "codex",
                FeishuChatKey = chatId,
                CreatedAt = now.AddMinutes(-40),
                UpdatedAt = now.AddMinutes(-1),
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                IsCustomWorkspace = true
            },
            new ChatSessionEntity
            {
                SessionId = "session-visible-02",
                Username = "luhaiyan",
                Title = "Visible Session 02",
                WorkspacePath = @"D:\repo\visible-02",
                ToolId = "claude-code",
                ToolLaunchOverridesJson = SessionLaunchOverrideHelper.Serialize(
                    new Dictionary<string, SessionToolLaunchOverride>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["claude-code"] = new SessionToolLaunchOverride
                        {
                            UseGoalRuntime = true
                        }
                    }),
                FeishuChatKey = chatId,
                CreatedAt = now.AddMinutes(-50),
                UpdatedAt = now.AddMinutes(-2),
                IsWorkspaceValid = true,
                IsFeishuActive = false,
                IsCustomWorkspace = true
            },
            new ChatSessionEntity
            {
                SessionId = "session-visible-03",
                Username = "luhaiyan",
                Title = "Visible Session 03",
                WorkspacePath = @"D:\repo\visible-03",
                ToolId = "codex",
                FeishuChatKey = chatId,
                CreatedAt = now.AddMinutes(-60),
                UpdatedAt = now.AddMinutes(-3),
                IsWorkspaceValid = true,
                IsFeishuActive = false,
                IsCustomWorkspace = true
            },
            new ChatSessionEntity
            {
                SessionId = "session-hidden-04",
                Username = "luhaiyan",
                Title = "Hidden Session 04",
                WorkspacePath = @"D:\repo\hidden-04",
                ToolId = "claude-code",
                FeishuChatKey = chatId,
                CreatedAt = now.AddMinutes(-70),
                UpdatedAt = now.AddMinutes(-4),
                IsWorkspaceValid = true,
                IsFeishuActive = false,
                IsCustomWorkspace = true
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository));

        var collapsedResponse = await service.HandleCardActionAsync(
            """{"action":"open_session_manager","chat_key":"oc_workspace_chat"}""",
            chatId: chatId);

        var collapsedPayload = SerializeResponse(collapsedResponse);
        var collapsedContents = ExtractCardContentStrings(collapsedResponse);
        Assert.Contains("Visible Session 01", collapsedPayload);
        Assert.Contains("Visible Session 02", collapsedPayload);
        Assert.Contains("Visible Session 03", collapsedPayload);
        Assert.DoesNotContain("Hidden Session 04", collapsedPayload);
        Assert.Contains(collapsedContents, content => content.Contains("🎯 Goal持续会话：**1** 个", StringComparison.Ordinal));
        Assert.Contains(collapsedContents, content => content.Contains("🎯 **Goal持续会话**", StringComparison.Ordinal));
        Assert.Contains(collapsedContents, content => content.Contains("当前默认展示最近 **3** 个会话", StringComparison.Ordinal));
        Assert.Contains(collapsedContents, content => content.Contains("更多会话", StringComparison.Ordinal));
        Assert.Contains("\"show_all_sessions\":true", collapsedPayload);

        var expandedResponse = await service.HandleCardActionAsync(
            """{"action":"open_session_manager","chat_key":"oc_workspace_chat","show_all_sessions":true}""",
            chatId: chatId);

        var expandedPayload = SerializeResponse(expandedResponse);
        var expandedContents = ExtractCardContentStrings(expandedResponse);
        Assert.Contains("Hidden Session 04", expandedPayload);
        Assert.Contains(expandedContents, content => content.Contains("当前展示全部 **4** 个会话", StringComparison.Ordinal));
        Assert.Contains(expandedContents, content => content.Contains("收起", StringComparison.Ordinal));
        Assert.Contains("\"show_all_sessions\":false", expandedPayload);
    }

    [Fact]
    public async Task HandleCardActionAsync_ShowSessionLaunchSettingsForm_ForCodex_IncludesModelAndReasoningDropdowns()
    {
        const string chatId = "oc_workspace_chat";
        const string sessionId = "session-launch-settings-codex";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(sessionId);
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = sessionId,
                Username = "luhaiyan",
                Title = "Codex Session",
                WorkspacePath = @"D:\repo\codex-session",
                ToolId = "codex",
                FeishuChatKey = chatId,
                CreatedAt = DateTime.Now.AddMinutes(-30),
                UpdatedAt = DateTime.Now,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                IsCustomWorkspace = true
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository));

        var response = await service.HandleCardActionAsync(
            """{"action":"show_session_launch_settings_form","session_id":"session-launch-settings-codex","chat_key":"oc_workspace_chat","show_all_sessions":true}""",
            chatId: chatId);

        var payload = SerializeResponse(response);
        Assert.Contains("save_session_launch_settings", payload);
        Assert.Contains("clear_session_launch_settings", payload);
        Assert.Contains("launch_model", payload);
        Assert.Contains("launch_reasoning_effort", payload);
        Assert.Contains("select_static", payload);
        Assert.Contains("gpt-5.4", payload);
        Assert.Contains("__follow_default__", payload);
        Assert.Contains("\"form_action_type\":\"submit\"", payload);
        Assert.DoesNotContain("\"action_type\":\"form_submit\"", payload);
        Assert.DoesNotContain("\"label\":{\"tag\":\"plain_text\"", payload);
        Assert.DoesNotContain("\"initial_option\":\"\"", payload);
        Assert.Contains("\"show_all_sessions\":true", payload);
    }

    [Fact]
    public async Task HandleCardActionAsync_ShowSessionLaunchSettingsForm_ForClaudeCode_OnlyIncludesModelDropdown()
    {
        const string chatId = "oc_workspace_chat";
        const string sessionId = "session-launch-settings-claude";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(sessionId);
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = sessionId,
                Username = "luhaiyan",
                Title = "Claude Session",
                WorkspacePath = @"D:\repo\claude-session",
                ToolId = "claude-code",
                FeishuChatKey = chatId,
                CreatedAt = DateTime.Now.AddMinutes(-30),
                UpdatedAt = DateTime.Now,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                IsCustomWorkspace = true
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository));

        var response = await service.HandleCardActionAsync(
            """{"action":"show_session_launch_settings_form","session_id":"session-launch-settings-claude","chat_key":"oc_workspace_chat"}""",
            chatId: chatId);

        var payload = SerializeResponse(response);
        Assert.Contains("launch_model", payload);
        Assert.Contains("select_static", payload);
        Assert.Contains("claude-sonnet-4-6", payload);
        Assert.Contains("__follow_default__", payload);
        Assert.DoesNotContain("\"label\":{\"tag\":\"plain_text\"", payload);
        Assert.DoesNotContain("\"initial_option\":\"\"", payload);
        Assert.DoesNotContain("launch_reasoning_effort", payload);
    }

    [Fact]
    public async Task HandleCardActionAsync_SaveSessionLaunchSettings_PersistsOverrideResetsRuntimeAndRefreshesCard()
    {
        const string chatId = "oc_workspace_chat";
        const string sessionId = "session-save-launch-settings";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(sessionId);
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = sessionId,
                Username = "luhaiyan",
                Title = "Launch Override Session",
                WorkspacePath = @"D:\repo\launch-override",
                ToolId = "codex",
                FeishuChatKey = chatId,
                CreatedAt = DateTime.Now.AddMinutes(-30),
                UpdatedAt = DateTime.Now,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                IsCustomWorkspace = true
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository));

        var response = await service.HandleCardActionAsync(
            """{"action":"save_session_launch_settings","session_id":"session-save-launch-settings","chat_key":"oc_workspace_chat","show_all_sessions":true}""",
            formValue: new Dictionary<string, object>
            {
                ["launch_model"] = "gpt-5.4",
                ["launch_reasoning_effort"] = "high"
            },
            chatId: chatId);

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Success, response.Toast?.Type);
        Assert.Contains("已保存该会话的启动设置", response.Toast?.Content);
        Assert.Single(cliExecutor.ResetRequests);
        Assert.Equal((sessionId, false), cliExecutor.ResetRequests[0]);

        var updatedSession = await sessionRepository.GetByIdAndUsernameAsync(sessionId, "luhaiyan");
        Assert.NotNull(updatedSession);
        var launchOverride = SessionLaunchOverrideHelper.GetEffectiveOverride(
            SessionLaunchOverrideHelper.Deserialize(updatedSession!.ToolLaunchOverridesJson),
            "codex",
            updatedSession.ToolId,
            updatedSession.CcSwitchSnapshotToolId);
        Assert.NotNull(launchOverride);
        Assert.Equal("gpt-5.4", launchOverride!.Model);
        Assert.Equal("high", launchOverride.ReasoningEffort);

        var payload = SerializeResponse(response);
        var cardContents = ExtractCardContentStrings(response);
        Assert.Contains(cardContents, content => content.Contains("🤖 模型: `gpt-5.4`", StringComparison.Ordinal));
        Assert.Contains(cardContents, content => content.Contains("🧠 思考: `high`", StringComparison.Ordinal));
        Assert.Contains("\"show_all_sessions\":true", payload);
    }

    [Fact]
    public async Task HandleCardActionAsync_SaveSessionLaunchSettings_FallsBackToChatIdWhenChatKeyMissing()
    {
        const string chatId = "oc_workspace_chat";
        const string sessionId = "session-save-launch-settings-missing-chatkey";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(sessionId);
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = sessionId,
                Username = "luhaiyan",
                Title = "Launch Override Session",
                WorkspacePath = @"D:\repo\launch-override-missing-chatkey",
                ToolId = "codex",
                FeishuChatKey = chatId,
                CreatedAt = DateTime.Now.AddMinutes(-30),
                UpdatedAt = DateTime.Now,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                IsCustomWorkspace = true
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository));

        var response = await service.HandleCardActionAsync(
            """{"action":"save_session_launch_settings","session_id":"session-save-launch-settings-missing-chatkey"}""",
            formValue: new Dictionary<string, object>
            {
                ["launch_model"] = "gpt-5.4",
                ["launch_reasoning_effort"] = "high"
            },
            chatId: chatId);

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Success, response.Toast?.Type);
        Assert.Single(cliExecutor.ResetRequests);
        Assert.Equal((sessionId, false), cliExecutor.ResetRequests[0]);

        var updatedSession = await sessionRepository.GetByIdAndUsernameAsync(sessionId, "luhaiyan");
        Assert.NotNull(updatedSession);
        var launchOverride = SessionLaunchOverrideHelper.GetEffectiveOverride(
            SessionLaunchOverrideHelper.Deserialize(updatedSession!.ToolLaunchOverridesJson),
            "codex",
            updatedSession.ToolId,
            updatedSession.CcSwitchSnapshotToolId);
        Assert.NotNull(launchOverride);
        Assert.Equal("gpt-5.4", launchOverride!.Model);
        Assert.Equal("high", launchOverride.ReasoningEffort);
    }

    [Fact]
    public async Task HandleCardActionAsync_SaveSessionLaunchSettings_PreservesMissingFieldsAndSupportsFollowDefaultSentinel()
    {
        const string chatId = "oc_workspace_chat";
        const string sessionId = "session-save-launch-settings-partial";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(sessionId);
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = sessionId,
                Username = "luhaiyan",
                Title = "Launch Override Partial Session",
                WorkspacePath = @"D:\repo\launch-override-partial",
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
                FeishuChatKey = chatId,
                CreatedAt = DateTime.Now.AddMinutes(-30),
                UpdatedAt = DateTime.Now,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                IsCustomWorkspace = true
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository));

        var response = await service.HandleCardActionAsync(
            """{"action":"save_session_launch_settings","session_id":"session-save-launch-settings-partial","chat_key":"oc_workspace_chat"}""",
            formValue: new Dictionary<string, object>
            {
                ["launch_reasoning_effort"] = "__follow_default__"
            },
            chatId: chatId);

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Success, response.Toast?.Type);
        Assert.Single(cliExecutor.ResetRequests);
        Assert.Equal((sessionId, false), cliExecutor.ResetRequests[0]);

        var updatedSession = await sessionRepository.GetByIdAndUsernameAsync(sessionId, "luhaiyan");
        Assert.NotNull(updatedSession);
        var launchOverride = SessionLaunchOverrideHelper.GetEffectiveOverride(
            SessionLaunchOverrideHelper.Deserialize(updatedSession!.ToolLaunchOverridesJson),
            "codex",
            updatedSession.ToolId,
            updatedSession.CcSwitchSnapshotToolId);
        Assert.NotNull(launchOverride);
        Assert.Equal("gpt-5.4", launchOverride!.Model);
        Assert.Null(launchOverride.ReasoningEffort);
    }

    [Fact]
    public async Task HandleCardActionAsync_SwitchStreamingCardModel_PersistsOverrideResetsRuntimeAndRefreshesCard()
    {
        const string chatId = "oc_workspace_chat";
        const string sessionId = "session-switch-streaming-model";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(sessionId);
        var chatSessionService = new StubChatSessionService();
        chatSessionService.AddMessage(sessionId, new ChatMessage
        {
            Role = "assistant",
            Content = "latest assistant content"
        });

        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = sessionId,
                Username = "luhaiyan",
                Title = "Switch Model",
                WorkspacePath = @"D:\repo\switch-model",
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
                FeishuChatKey = chatId,
                CreatedAt = DateTime.Now.AddMinutes(-30),
                UpdatedAt = DateTime.Now,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                IsCustomWorkspace = true
            }
        ]);

        var response = await CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository),
            chatSessionService: chatSessionService)
            .HandleCardActionAsync(
                """{"action":"switch_streaming_card_model","session_id":"session-switch-streaming-model","chat_key":"oc_workspace_chat","model":"gpt-5.4-mini"}""",
                chatId: chatId);

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Success, response.Toast?.Type);
        Assert.Contains("gpt-5.4-mini", response.Toast?.Content);
        Assert.Single(cliExecutor.ResetRequests);
        Assert.Equal((sessionId, false), cliExecutor.ResetRequests[0]);

        var updatedSession = await sessionRepository.GetByIdAndUsernameAsync(sessionId, "luhaiyan");
        var launchOverride = SessionLaunchOverrideHelper.GetEffectiveOverride(
            SessionLaunchOverrideHelper.Deserialize(updatedSession!.ToolLaunchOverridesJson),
            "codex",
            updatedSession.ToolId,
            updatedSession.CcSwitchSnapshotToolId);
        Assert.NotNull(launchOverride);
        Assert.Equal("gpt-5.4-mini", launchOverride!.Model);
        Assert.Equal("high", launchOverride.ReasoningEffort);

        var payload = SerializeResponse(response);
        var cardContents = ExtractCardContentStrings(response);
        Assert.Contains("latest assistant content", cardContents);
        Assert.Contains("gpt-5.4-mini", payload);
        Assert.Contains("reasoning_effort", payload);
    }

    [Fact]
    public async Task HandleCardActionAsync_SwitchStreamingCardReasoningEffort_RejectsWhileExecutionActive()
    {
        const string chatId = "oc_workspace_chat";
        const string sessionId = "session-switch-streaming-reasoning";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(sessionId)
        {
            SessionExecutionActive = true
        };
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = sessionId,
                Username = "luhaiyan",
                Title = "Switch Reasoning",
                WorkspacePath = @"D:\repo\switch-reasoning",
                ToolId = "codex",
                FeishuChatKey = chatId,
                CreatedAt = DateTime.Now.AddMinutes(-30),
                UpdatedAt = DateTime.Now,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                IsCustomWorkspace = true
            }
        ]);

        var response = await CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository))
            .HandleCardActionAsync(
                """{"action":"switch_streaming_card_reasoning_effort","session_id":"session-switch-streaming-reasoning","chat_key":"oc_workspace_chat","reasoning_effort":"xhigh"}""",
                chatId: chatId);

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Warning, response.Toast?.Type);
        Assert.Contains("当前回复尚未完成", response.Toast?.Content);
        Assert.Empty(cliExecutor.ResetRequests);

        var updatedSession = await sessionRepository.GetByIdAndUsernameAsync(sessionId, "luhaiyan");
        Assert.Null(updatedSession!.ToolLaunchOverridesJson);
    }

    [Fact]
    public async Task HandleCardActionAsync_SwitchStreamingCardModel_RejectsStaleChatMismatch()
    {
        const string chatId = "oc_workspace_chat";
        const string sessionId = "session-switch-streaming-stale";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(sessionId);
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = sessionId,
                Username = "luhaiyan",
                Title = "Stale Session",
                WorkspacePath = @"D:\repo\stale-session",
                ToolId = "codex",
                FeishuChatKey = "oc_another_chat",
                CreatedAt = DateTime.Now.AddMinutes(-30),
                UpdatedAt = DateTime.Now,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                IsCustomWorkspace = true
            }
        ]);

        var response = await CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository))
            .HandleCardActionAsync(
                """{"action":"switch_streaming_card_model","session_id":"session-switch-streaming-stale","chat_key":"oc_workspace_chat","model":"gpt-5.4"}""",
                chatId: chatId);

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Error, response.Toast?.Type);
        Assert.Contains("会话不存在或已失效", response.Toast?.Content);
        Assert.Empty(cliExecutor.ResetRequests);
    }

    [Fact]
    public async Task HandleCardActionAsync_ClearSessionLaunchSettings_RemovesOverrideResetsRuntimeAndRefreshesCard()
    {
        const string chatId = "oc_workspace_chat";
        const string sessionId = "session-clear-launch-settings";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(sessionId);
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = sessionId,
                Username = "luhaiyan",
                Title = "Launch Override Session",
                WorkspacePath = @"D:\repo\launch-override",
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
                FeishuChatKey = chatId,
                CreatedAt = DateTime.Now.AddMinutes(-30),
                UpdatedAt = DateTime.Now,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                IsCustomWorkspace = true
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository));

        var response = await service.HandleCardActionAsync(
            """{"action":"clear_session_launch_settings","session_id":"session-clear-launch-settings","chat_key":"oc_workspace_chat"}""",
            chatId: chatId);

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Success, response.Toast?.Type);
        Assert.Contains("已清除该会话的启动覆盖", response.Toast?.Content);
        Assert.Single(cliExecutor.ResetRequests);
        Assert.Equal((sessionId, false), cliExecutor.ResetRequests[0]);

        var updatedSession = await sessionRepository.GetByIdAndUsernameAsync(sessionId, "luhaiyan");
        Assert.NotNull(updatedSession);
        Assert.Null(updatedSession!.ToolLaunchOverridesJson);

        var cardContents = ExtractCardContentStrings(response);
        Assert.DoesNotContain(cardContents, content => content.Contains("馃 妯″瀷:", StringComparison.Ordinal));
        Assert.DoesNotContain(cardContents, content => content.Contains("馃 鎬濊€?", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleCardActionAsync_RenameSession_UpdatesTitleAndRefreshesSessionManagerCard()
    {
        const string chatId = "oc_workspace_chat";
        const string sessionId = "session-rename-target";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(sessionId);
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = sessionId,
                Username = "luhaiyan",
                Title = "旧会话标题",
                WorkspacePath = @"D:\repo\superpowers",
                ToolId = "codex",
                FeishuChatKey = chatId,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                IsCustomWorkspace = true
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository));

        var response = await service.HandleCardActionAsync(
            """{"action":"rename_session","session_id":"session-rename-target","chat_key":"oc_workspace_chat"}""",
            formValue: new Dictionary<string, object>
            {
                ["session_title"] = "鏂扮殑浼氳瘽鏍囬"
            },
            chatId: chatId);

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Success, response.Toast?.Type);
        Assert.Contains("鏂扮殑浼氳瘽鏍囬", response.Toast?.Content);

        var updatedSession = await sessionRepository.GetByIdAndUsernameAsync(sessionId, "luhaiyan");
        Assert.NotNull(updatedSession);
        Assert.Equal("鏂扮殑浼氳瘽鏍囬", updatedSession!.Title);

        var payload = SerializeResponse(response);
        Assert.Contains("\"action\":\"show_rename_session_form\"", payload);
        var cardContents = ExtractCardContentStrings(response);
        Assert.Contains(cardContents, content => content.Contains("鏂扮殑浼氳瘽鏍囬", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HandleCardActionAsync_SyncSessionProvider_ReturnsImmediatelyAndRefreshesSessionManagerCardInBackground()
    {
        const string chatId = "oc_workspace_chat";
        const string sessionId = "session-sync-provider";

        var cliExecutor = new RecordingCliExecutorService
        {
            ThreadSyncBlocker = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var feishuChannel = new StubFeishuChannelService(sessionId);
        var cardKit = new StubFeishuCardKitClient();
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = sessionId,
                Username = "luhaiyan",
                WorkspacePath = @"D:\repo\superpowers",
                ToolId = "codex",
                FeishuChatKey = chatId,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                IsCustomWorkspace = true
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository),
            cardKit);

        var responseTask = service.HandleCardActionAsync(
            """{"action":"sync_session_provider","session_id":"session-sync-provider","chat_key":"oc_workspace_chat"}""",
            chatId: chatId);
        var completedTask = await Task.WhenAny(responseTask, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.Same(responseTask, completedTask);

        var response = await responseTask;
        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Info, response.Toast?.Type);
        Assert.Contains("已开始后台同步", response.Toast?.Content, StringComparison.Ordinal);

        var syncRequest = await cliExecutor.WaitForThreadSyncRequestAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(sessionId, syncRequest.SessionId);
        Assert.Equal("codex", syncRequest.ToolId);

        cliExecutor.ThreadSyncBlocker!.TrySetResult(null);

        var sentCard = await cardKit.WaitForRawCardSentAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(chatId, sentCard.ChatId);
        Assert.Contains("sync_session_provider", sentCard.CardJson, StringComparison.Ordinal);
        Assert.Contains("session-sync-provider", sentCard.CardJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleCardActionAsync_SyncSessionProvider_PartialSuccess_SendsWarningTextAndRefreshesSessionManagerCard()
    {
        const string chatId = "oc_workspace_chat";
        const string sessionId = "session-sync-provider-warning";

        var cliExecutor = new RecordingCliExecutorService
        {
            ThreadSyncResult = new CodexThreadProviderSyncResult
            {
                Message = "thread synced with warnings",
                HasWarnings = true
            }
        };
        var feishuChannel = new StubFeishuChannelService(sessionId);
        var cardKit = new StubFeishuCardKitClient();
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = sessionId,
                Username = "luhaiyan",
                WorkspacePath = @"D:\repo\superpowers",
                ToolId = "codex",
                FeishuChatKey = chatId,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                IsCustomWorkspace = true
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository),
            cardKit);

        var response = await service.HandleCardActionAsync(
            """{"action":"sync_session_provider","session_id":"session-sync-provider-warning","chat_key":"oc_workspace_chat"}""",
            chatId: chatId);

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Info, response.Toast?.Type);
        Assert.Contains("已开始后台同步", response.Toast?.Content, StringComparison.Ordinal);

        var syncRequest = await cliExecutor.WaitForThreadSyncRequestAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(sessionId, syncRequest.SessionId);

        var sentCard = await cardKit.WaitForRawCardSentAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(chatId, sentCard.ChatId);
        Assert.Contains("session-sync-provider-warning", sentCard.CardJson, StringComparison.Ordinal);

        var warningMessage = await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(chatId, warningMessage.ChatId);
        Assert.Contains("thread synced with warnings", warningMessage.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleCardActionAsync_SyncSessionProvider_BackgroundFailure_SendsFailureTextWithoutBlockingCallback()
    {
        const string chatId = "oc_workspace_chat";
        const string sessionId = "session-sync-provider-failure";

        var cliExecutor = new RecordingCliExecutorService
        {
            ThreadSyncException = new InvalidOperationException("同步线程失败")
        };
        var feishuChannel = new StubFeishuChannelService(sessionId);
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = sessionId,
                Username = "luhaiyan",
                WorkspacePath = @"D:\repo\superpowers",
                ToolId = "codex",
                FeishuChatKey = chatId,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                IsCustomWorkspace = true
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository));

        var responseTask = service.HandleCardActionAsync(
            """{"action":"sync_session_provider","session_id":"session-sync-provider-failure","chat_key":"oc_workspace_chat"}""",
            chatId: chatId);
        var completedTask = await Task.WhenAny(responseTask, Task.Delay(TimeSpan.FromMilliseconds(300)));
        Assert.Same(responseTask, completedTask);

        var response = await responseTask;
        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Info, response.Toast?.Type);
        Assert.Contains("已开始后台同步", response.Toast?.Content, StringComparison.Ordinal);

        var failureMessage = await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(chatId, failureMessage.ChatId);
        Assert.Contains("同步当前 cc-switch Provider 失败", failureMessage.Content, StringComparison.Ordinal);
        Assert.Contains("同步线程失败", failureMessage.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_WithoutActiveSession_ReturnsCreateSessionForm()
    {
        const string chatId = "oc_current_chat";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(null);
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider());

        var response = await service.HandleCardActionAsync(
            """{"action":"execute_command"}""",
            chatId: chatId,
            inputValues: "/init");

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Warning, response.Toast?.Type);
        Assert.Contains("当前没有活跃会话", response.Toast?.Content);
        Assert.False(cliExecutor.WasExecuted);
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_HistoryCommand_SendsExternalCliHistoryWithoutExecutingCli()
    {
        const string chatId = "oc_current_chat";
        const string sessionId = "session-history";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(sessionId);
        var historyService = new StubExternalCliSessionHistoryService(
        [
            new ExternalCliHistoryMessage { Role = "user", Content = "浣犲ソ" },
            new ExternalCliHistoryMessage { Role = "assistant", Content = "涓栫晫" }
        ]);

        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = sessionId,
                Username = "luhaiyan",
                ToolId = "codex",
                CliThreadId = "codex-thread-1",
                WorkspacePath = @"D:\repo",
                FeishuChatKey = chatId,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(
                chatSessionRepository: sessionRepository,
                externalCliSessionHistoryService: historyService));

        var response = await service.HandleCardActionAsync(
            """{"action":"execute_command"}""",
            chatId: chatId,
            inputValues: "/history");

        var sent = await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Info, response.Toast?.Type);
        Assert.Contains("CLI 会话历史", response.Toast?.Content);
        Assert.False(cliExecutor.WasExecuted);
        Assert.Equal("codex", historyService.LastToolId);
        Assert.Equal("codex-thread-1", historyService.LastCliThreadId);
        Assert.Equal(50, historyService.LastMaxCount);
        Assert.Contains("原生 Thread ID: codex-thread-1", sent.Content);
        Assert.Contains(@"历史来源: D:\repo\.codex\sessions\2026\05\04\rollout-history.jsonl", sent.Content);
        Assert.Contains("浣犲ソ", sent.Content);
        Assert.Contains("涓栫晫", sent.Content);
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_HistoryCommand_RespectsRequestedMessageLimit()
    {
        const string chatId = "oc_current_chat";
        const string sessionId = "session-history-limit";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(sessionId);
        var historyService = new StubExternalCliSessionHistoryService(
        [
            new ExternalCliHistoryMessage { Role = "user", Content = "第一条" },
            new ExternalCliHistoryMessage { Role = "assistant", Content = "第二条" },
            new ExternalCliHistoryMessage { Role = "user", Content = "第三条" }
        ]);

        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = sessionId,
                Username = "luhaiyan",
                ToolId = "codex",
                CliThreadId = "codex-thread-limit",
                WorkspacePath = @"D:\repo",
                FeishuChatKey = chatId,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(
                chatSessionRepository: sessionRepository,
                externalCliSessionHistoryService: historyService));

        await service.HandleCardActionAsync(
            """{"action":"execute_command"}""",
            chatId: chatId,
            inputValues: "/history 2");

        var sent = await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

        Assert.False(cliExecutor.WasExecuted);
        Assert.Equal(2, historyService.LastMaxCount);
        Assert.Contains("第二条", sent.Content);
        Assert.Contains("第三条", sent.Content);
        Assert.DoesNotContain("第一条", sent.Content);
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_HistoryCommand_PreservesAssistantTailWhenMessageIsLong()
    {
        const string chatId = "oc_current_chat";
        const string sessionId = "session-history-long";
        const string tailContent = "如果你要，我下一步可以直接帮你继续联调。";
        var longAssistantContent = new string('甲', 1500) + "\n" + new string('乙', 900) + "\n" + tailContent;

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(sessionId);
        var historyService = new StubExternalCliSessionHistoryService(
        [
            new ExternalCliHistoryMessage { Role = "assistant", Content = longAssistantContent }
        ]);

        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = sessionId,
                Username = "luhaiyan",
                ToolId = "codex",
                CliThreadId = "codex-thread-long",
                WorkspacePath = @"D:\repo",
                FeishuChatKey = chatId,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(
                chatSessionRepository: sessionRepository,
                externalCliSessionHistoryService: historyService));

        await service.HandleCardActionAsync(
            """{"action":"execute_command"}""",
            chatId: chatId,
            inputValues: "/history");

        var sent = await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

        Assert.False(cliExecutor.WasExecuted);
        Assert.Contains(longAssistantContent, sent.Content);
        Assert.Contains(tailContent, sent.Content);
    }

    [Fact]
    public async Task HandleCardActionAsync_BrowseCurrentSessionDirectory_ReturnsDirectoryCard()
    {
        const string chatId = "oc_workspace_chat";
        const string activeSessionId = "session-files";

        var workspacePath = Path.Combine(Path.GetTempPath(), $"feishu-browse-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        Directory.CreateDirectory(Path.Combine(workspacePath, "src"));
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "README.md"), "hello from feishu");

        try
        {
            var cliExecutor = new RecordingCliExecutorService();
            cliExecutor.SetSessionWorkspacePath(activeSessionId, workspacePath);

            var feishuChannel = new StubFeishuChannelService(activeSessionId);
            var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider());

            var response = await service.HandleCardActionAsync(
                """{"action":"browse_current_session_directory","chat_key":"oc_workspace_chat"}""",
                chatId: chatId);

            var payload = SerializeResponse(response);
            Assert.Contains("README.md", payload);
            Assert.Contains("src", payload);
            Assert.Contains("\"action\":\"preview_session_file\"", payload);
            Assert.Contains("\"action\":\"browse_session_directory\"", payload);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task HandleCardActionAsync_BrowseCurrentSessionDirectory_SkipsReservedWindowsDeviceEntries()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string chatId = "oc_workspace_chat";
        const string activeSessionId = "session-files";
        var workspacePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

        var cliExecutor = new RecordingCliExecutorService();
        cliExecutor.SetSessionWorkspacePath(activeSessionId, workspacePath);

        var feishuChannel = new StubFeishuChannelService(activeSessionId);
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider());

        var response = await service.HandleCardActionAsync(
            """{"action":"browse_current_session_directory","chat_key":"oc_workspace_chat"}""",
            chatId: chatId);

        var payload = SerializeResponse(response);
        Assert.Contains("\"action\":\"browse_session_directory\"", payload);
        Assert.DoesNotContain("\"nul\"", payload, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandleCardActionAsync_PreviewSessionFile_ReturnsTextPreview()
    {
        const string chatId = "oc_workspace_chat";
        const string activeSessionId = "session-files";

        var workspacePath = Path.Combine(Path.GetTempPath(), $"feishu-preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);
        await File.WriteAllTextAsync(Path.Combine(workspacePath, "README.md"), "hello from feishu\nsecond line");

        try
        {
            var cliExecutor = new RecordingCliExecutorService();
            cliExecutor.SetSessionWorkspacePath(activeSessionId, workspacePath);

            var feishuChannel = new StubFeishuChannelService(activeSessionId);
            var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider());

            var response = await service.HandleCardActionAsync(
                """{"action":"preview_session_file","chat_key":"oc_workspace_chat","session_id":"session-files","file_path":"README.md","directory_path":"","page":0}""",
                chatId: chatId);

            var payload = SerializeResponse(response);
            Assert.Contains("hello from feishu", payload);
            Assert.Contains("second line", payload);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task HandleCardActionAsync_BrowseAllowedDirectory_ReturnsWhitelistBrowserCard()
    {
        const string chatId = "oc_allowed_chat";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(null);
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider());

        var response = await service.HandleCardActionAsync(
            """{"action":"browse_allowed_directory","chat_key":"oc_allowed_chat","tool_id":"claude-code"}""",
            chatId: chatId);

        var payload = SerializeResponse(response);
        Assert.Contains(@"D:\\VSWorkshop\\allowed", payload);
        Assert.Contains("\"action\":\"copy_path_to_chat\"", payload);
        Assert.Contains("\"action\":\"browse_allowed_directory\"", payload);
    }

    [Fact]
    public async Task HandleCardActionAsync_CreateSession_WithExistingWorkspace_DoesNotRequireCliWorkspaceCache()
    {
        const string chatId = "oc_workspace_chat";
        const string workspacePath = @"D:\VSWorkshop\TestWebCode\projects\luhaiyan\43cc07a314174cae9deb8cc2e69becaa";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(null);
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider());

        var response = await service.HandleCardActionAsync(
            """{"action":"create_session","chat_key":"oc_workspace_chat","create_mode":"existing","tool_id":"codex","workspace_path":"D:\\VSWorkshop\\TestWebCode\\projects\\luhaiyan\\43cc07a314174cae9deb8cc2e69becaa"}""",
            chatId: chatId,
            operatorUserId: "ou_test_user");

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Success, response.Toast?.Type);
        Assert.Contains(workspacePath, response.Toast?.Content);
        Assert.Equal("session-new", feishuChannel.LastSwitchedSessionId);
        Assert.Equal(workspacePath, feishuChannel.CreatedWorkspacePath);
        Assert.Equal("codex", feishuChannel.CreatedToolId);
    }

    [Fact]
    public async Task HandleCardActionAsync_OpenSessionManager_FallsBackToSessionRepositoryWorkspace()
    {
        const string chatId = "oc_workspace_chat";
        const string activeSessionId = "session-db-backed";

        var workspacePath = Path.Combine(Path.GetTempPath(), $"feishu-session-manager-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workspacePath);

        try
        {
            var cliExecutor = new RecordingCliExecutorService();
            var feishuChannel = new StubFeishuChannelService(activeSessionId);
            var sessionRepository = new StubChatSessionRepository(
            [
                new ChatSessionEntity
                {
                    SessionId = activeSessionId,
                    Username = "luhaiyan",
                    WorkspacePath = workspacePath,
                    ToolId = "codex",
                    FeishuChatKey = chatId,
                    CreatedAt = DateTime.Now,
                    UpdatedAt = DateTime.Now,
                    IsWorkspaceValid = true,
                    IsFeishuActive = true,
                    IsCustomWorkspace = true
                }
            ]);

            var serviceProvider = new TestServiceProvider(chatSessionRepository: sessionRepository);
            var service = CreateService(cliExecutor, feishuChannel, serviceProvider);

            var response = await service.HandleCardActionAsync(
                """{"action":"open_session_manager","chat_key":"oc_workspace_chat"}""",
                chatId: chatId);

            var payload = SerializeResponse(response);
            Assert.Contains(workspacePath.Replace(@"\", @"\\"), payload);
            Assert.DoesNotContain("(工作区未初始化或已失效)", payload);
        }
        finally
        {
            Directory.Delete(workspacePath, recursive: true);
        }
    }

    [Fact]
    public async Task HandleCardActionAsync_SwitchSession_SendsExternalCliHistory()
    {
        const string chatId = "oc_workspace_chat";
        const string sessionId = "session-switch-history";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(null);
        var historyService = new StubExternalCliSessionHistoryService(
        [
            new ExternalCliHistoryMessage
            {
                Role = "assistant",
                Content = "杩欐槸 CLI 鍘熺敓鍘嗗彶",
                CreatedAt = new DateTime(2026, 3, 23, 9, 0, 0, DateTimeKind.Local)
            }
        ]);

        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = sessionId,
                Username = "luhaiyan",
                ToolId = "claude-code",
                CliThreadId = "claude-session-1",
                WorkspacePath = @"D:\repo",
                FeishuChatKey = chatId,
                IsWorkspaceValid = true,
                IsFeishuActive = false,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(
                chatSessionRepository: sessionRepository,
                externalCliSessionHistoryService: historyService));

        var response = await service.HandleCardActionAsync(
            """{"action":"switch_session","chat_key":"oc_workspace_chat","session_id":"session-switch-history"}""",
            chatId: chatId);

        var sent = await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Success, response.Toast?.Type);
        Assert.Equal("claude-code", historyService.LastToolId);
        Assert.Equal("claude-session-1", historyService.LastCliThreadId);
        Assert.Contains("杩欐槸 CLI 鍘熺敓鍘嗗彶", sent.Content);
        Assert.DoesNotContain("鏆傛棤鍘嗗彶娑堟伅", sent.Content);
    }

    [Fact]
    public async Task HandleCardActionAsync_SwitchSession_PausesCurrentSessionPulseBeforeSwitch()
    {
        const string chatId = "oc_workspace_chat";
        const string currentSessionId = "session-current";
        const string targetSessionId = "session-switch-target";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(currentSessionId);
        var historyService = new StubExternalCliSessionHistoryService(
        [
            new ExternalCliHistoryMessage
            {
                Role = "assistant",
                Content = "杩欐槸 CLI 鍘熺敓鍘嗗彶",
                CreatedAt = new DateTime(2026, 3, 23, 9, 0, 0, DateTimeKind.Local)
            }
        ]);

        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = targetSessionId,
                Username = "luhaiyan",
                ToolId = "claude-code",
                CliThreadId = "claude-session-2",
                WorkspacePath = @"D:\repo",
                FeishuChatKey = chatId,
                IsWorkspaceValid = true,
                IsFeishuActive = false,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(
                chatSessionRepository: sessionRepository,
                externalCliSessionHistoryService: historyService));

        var response = await service.HandleCardActionAsync(
            """{"action":"switch_session","chat_key":"oc_workspace_chat","session_id":"session-switch-target"}""",
            chatId: chatId);

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Success, response.Toast?.Type);
        Assert.Equal(currentSessionId, feishuChannel.LastPausedSessionId);
        Assert.Equal(TimeSpan.FromSeconds(3), feishuChannel.LastPauseDuration);
        Assert.Equal(targetSessionId, feishuChannel.LastSwitchedSessionId);
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_WithSessionOverflowMenu_ResumesPulseAfterQuietWindow()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "11111111-current";

        var cliExecutor = new RecordingCliExecutorService
        {
            StandardExecutionContent = "still working",
            StandardExecutionCompletionDelay = TimeSpan.FromSeconds(6)
        };
        cliExecutor.SetSessionWorkspacePath(activeSessionId, @"D:\repo\superpowers");

        var cardKit = new StubFeishuCardKitClient();
        var feishuChannel = new StubFeishuChannelService(activeSessionId);
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = activeSessionId,
                Username = "luhaiyan",
                Title = "旧会话标题",
                ToolId = "codex",
                WorkspacePath = @"D:\repo\superpowers",
                FeishuChatKey = chatId,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                CreatedAt = DateTime.Now.AddMinutes(-30),
                UpdatedAt = DateTime.Now
            },
            new ChatSessionEntity
            {
                SessionId = "22222222-other",
                Username = "luhaiyan",
                Title = "Backend API",
                ToolId = "claude-code",
                WorkspacePath = @"D:\repo\backend",
                FeishuChatKey = chatId,
                IsWorkspaceValid = true,
                IsFeishuActive = false,
                CreatedAt = DateTime.Now.AddMinutes(-60),
                UpdatedAt = DateTime.Now.AddMinutes(-5)
            }
        ]);

        var service = CreateService(
            cliExecutor,
            feishuChannel,
            new TestServiceProvider(chatSessionRepository: sessionRepository),
            cardKit);

        var response = await service.HandleCardActionAsync(
            """{"action":"execute_command"}""",
            chatId: chatId,
            inputValues: "缁х画");

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Info, response.Toast?.Type);

        await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));
        await Task.Delay(1300, TestContext.Current.CancellationToken);

        Assert.Single(cardKit.StreamingUpdates);
        Assert.Single(cardKit.StreamingStatusMarkdownSnapshots.Distinct(StringComparer.Ordinal));

        await Task.Delay(3100, TestContext.Current.CancellationToken);

        Assert.True(cardKit.StreamingUpdates.Count > 1);
        Assert.True(cardKit.StreamingStatusMarkdownSnapshots.Distinct(StringComparer.Ordinal).Count() > 1);

        await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task HandleCardActionAsync_DiscoverExternalCliSessions_ShowsFullCountAndPagination()
    {
        const string chatId = "oc_external_cli_chat";

        var discovered = Enumerable.Range(1, 285)
            .Select(index => new ExternalCliSessionSummary
            {
                ToolId = "claude-code",
                ToolName = "Claude Code",
                CliThreadId = $"claude-session-{index:D3}",
                Title = $"Claude 浼氳瘽 {index:D3}",
                WorkspacePath = $@"D:\VSWorkshop\allowed\workspace-{index:D3}",
                UpdatedAt = new DateTime(2026, 3, 23, 10, 0, 0).AddMinutes(-index)
            })
            .ToList();

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(null);
        var serviceProvider = new TestServiceProvider(
            externalCliSessionService: new StubExternalCliSessionService(discovered));
        var service = CreateService(cliExecutor, feishuChannel, serviceProvider);

        var response = await service.HandleCardActionAsync(
            """{"action":"discover_external_cli_sessions","chat_key":"oc_external_cli_chat","tool_id":"claude-code"}""",
            chatId: chatId,
            operatorUserId: "ou_test_user");

        var payload = SerializeResponse(response);
        using var document = JsonDocument.Parse(payload);
        var summaryContent = document.RootElement
            .GetProperty("card")
            .GetProperty("data")
            .GetProperty("body")
            .GetProperty("elements")[0]
            .GetProperty("text")
            .GetProperty("content")
            .GetString();

        Assert.NotNull(summaryContent);
        Assert.Contains("当前找到 **285** 个可导入会话", summaryContent);
        Assert.Contains("1/29", summaryContent);
        var elementContents = document.RootElement
            .GetProperty("card")
            .GetProperty("data")
            .GetProperty("body")
            .GetProperty("elements")
            .EnumerateArray()
            .Where(element => element.TryGetProperty("text", out _))
            .Select(element => element.GetProperty("text"))
            .Where(text => text.TryGetProperty("content", out _))
            .Select(text => text.GetProperty("content").GetString())
            .Where(content => !string.IsNullOrWhiteSpace(content))
            .ToList();

        Assert.Contains(elementContents, content => content!.Contains("Claude 浼氳瘽 001", StringComparison.Ordinal));
        Assert.Contains("\"page\":1", payload);
    }

    [Fact]
    public async Task HandleCardActionAsync_CloseSession_WithMissingWorkspace_ClosesImmediately()
    {
        const string chatId = "oc_workspace_chat";
        const string sessionId = "session-missing-workspace";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(sessionId);
        var sessionRepository = new StubChatSessionRepository(
        [
            new ChatSessionEntity
            {
                SessionId = sessionId,
                Username = "luhaiyan",
                WorkspacePath = null,
                ToolId = "codex",
                FeishuChatKey = chatId,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                IsWorkspaceValid = true,
                IsFeishuActive = true,
                IsCustomWorkspace = false
            }
        ]);

        var serviceProvider = new TestServiceProvider(chatSessionRepository: sessionRepository);
        var service = CreateService(cliExecutor, feishuChannel, serviceProvider);

        var response = await service.HandleCardActionAsync(
            """{"action":"close_session","chat_key":"oc_workspace_chat","session_id":"session-missing-workspace"}""",
            chatId: chatId,
            operatorUserId: "ou_test_user");

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Info, response.Toast?.Type);
        Assert.Equal(sessionId, feishuChannel.LastClosedSessionId);
        Assert.Contains("已关闭会话", response.Toast?.Content);
    }

    [Fact]
    public async Task HandleCardActionAsync_CopyPathToChat_SendsCopyableMessage()
    {
        const string chatId = "oc_allowed_chat";
        const string path = @"D:\VSWorkshop\allowed\README.md";

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(null);
        var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider());
        var actionJson = JsonSerializer.Serialize(new
        {
            action = "copy_path_to_chat",
            chat_key = chatId,
            copy_path = path
        });

        var response = await service.HandleCardActionAsync(
            actionJson,
            chatId: chatId);

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Success, response.Toast?.Type);
        Assert.Equal("oc_allowed_chat", feishuChannel.LastSentChatId);
        Assert.Contains(path, feishuChannel.LastSentMessage);
    }

    [Fact]
    public async Task HandleCardActionAsync_OpenProjectManager_ReturnsProjectListForBoundUser()
    {
        const string chatId = "oc_project_chat";
        var userContext = new TestUserContextService();
        var projectService = new TestProjectService(userContext, [
            new ProjectInfo
            {
                ProjectId = "project-1",
                Name = "WmsServerV4",
                GitUrl = "http://sql-for-tfs2017:8080/tfs/DefaultCollection/WmsV4/_git/WmsServerV4",
                AuthType = "https",
                Branch = string.Empty,
                Status = "ready",
                LocalPath = @"D:\repos\WmsServerV4",
                UpdatedAt = DateTime.Now
            }
        ]);

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(null);
        var serviceProvider = new TestServiceProvider(userContext, projectService);
        var service = CreateService(cliExecutor, feishuChannel, serviceProvider);

        var response = await service.HandleCardActionAsync(
            """{"action":"open_project_manager","chat_key":"oc_project_chat"}""",
            chatId: chatId);

        var payload = SerializeResponse(response);
        Assert.Contains("WmsServerV4", payload);
        Assert.Contains("create_session_from_project", payload);
        Assert.Contains("show_project_branch_switcher", payload);
        Assert.Equal("luhaiyan", projectService.LastUsernameSeen);
    }

    [Fact]
    public async Task HandleCardActionAsync_ShowProjectBranchSwitcher_ReturnsImmediateToastAndSendsBranchCardAsync()
    {
        const string chatId = "oc_project_chat";
        const string appId = "cli_user_branch";
        var userContext = new TestUserContextService();
        var projectService = new TestProjectService(userContext, [
            new ProjectInfo
            {
                ProjectId = "project-1",
                Name = "WmsServerV4",
                GitUrl = "http://sql-for-tfs2017:8080/tfs/DefaultCollection/WmsV4/_git/WmsServerV4",
                AuthType = "https",
                Branch = "main",
                Status = "ready",
                LocalPath = @"D:\repos\WmsServerV4",
                UpdatedAt = DateTime.Now
            }
        ]);

        var cliExecutor = new RecordingCliExecutorService();
        var cardKit = new StubFeishuCardKitClient();
        var feishuChannel = new StubFeishuChannelService(null);
        var serviceProvider = new TestServiceProvider(userContext, projectService);
        projectService.GetProjectBranchesDelay = TimeSpan.FromSeconds(2);
        var service = CreateService(cliExecutor, feishuChannel, serviceProvider, cardKit);

        var startedAt = DateTime.UtcNow;
        var response = await service.HandleCardActionAsync(
            """{"action":"show_project_branch_switcher","chat_key":"oc_project_chat","project_id":"project-1"}""",
            chatId: chatId,
            appId: appId);
        var elapsed = DateTime.UtcNow - startedAt;

        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"Expected immediate callback response, actual: {elapsed}");
        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Info, response.Toast?.Type);

        var (_, cardJson) = await cardKit.WaitForRawCardSentAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("switch_project_branch", cardJson);
        Assert.Contains("release", cardJson);
        Assert.Contains("main", cardJson);
        Assert.Equal(appId, cardKit.LastRawCardOptionsOverride?.AppId);
    }

    [Fact]
    public async Task HandleCardActionAsync_CloneProject_UsesBoundBotContextForCompletionNotification()
    {
        const string chatId = "oc_project_chat";
        const string appId = "cli_user_clone";
        var userContext = new TestUserContextService();
        var projectService = new TestProjectService(userContext, [
            new ProjectInfo
            {
                ProjectId = "project-1",
                Name = "WmsServerV4",
                GitUrl = "http://sql-for-tfs2017:8080/tfs/DefaultCollection/WmsV4/_git/WmsServerV4",
                AuthType = "https",
                Branch = string.Empty,
                Status = "pending",
                LocalPath = @"D:\repos\WmsServerV4",
                UpdatedAt = DateTime.Now
            }
        ])
        {
            CloneProjectDelay = TimeSpan.FromMilliseconds(50)
        };

        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(null);
        var serviceProvider = new TestServiceProvider(userContext, projectService);
        var service = CreateService(cliExecutor, feishuChannel, serviceProvider);

        var response = await service.HandleCardActionAsync(
            """{"action":"clone_project","chat_key":"oc_project_chat","project_id":"project-1"}""",
            chatId: chatId,
            operatorUserId: "ou_test_user",
            appId: appId);

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Info, response.Toast?.Type);

        var message = await feishuChannel.WaitForMessageAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(chatId, message.ChatId);
        Assert.Contains("项目 WmsServerV4 克隆完成", message.Content);
        Assert.Equal("luhaiyan", message.Username);
        Assert.Equal(appId, message.AppId);
    }

    [Fact]
    public async Task HandleCardActionAsync_ShowProjectBranchSwitcher_PaginatesLargeBranchListAsync()
    {
        const string chatId = "oc_project_chat";
        var userContext = new TestUserContextService();
        var projectService = new TestProjectService(userContext, [
            new ProjectInfo
            {
                ProjectId = "project-1",
                Name = "WmsServerV4",
                GitUrl = "http://sql-for-tfs2017:8080/tfs/DefaultCollection/WmsV4/_git/WmsServerV4",
                AuthType = "https",
                Branch = "branch-01",
                Status = "ready",
                LocalPath = @"D:\repos\WmsServerV4",
                UpdatedAt = DateTime.Now
            }
        ])
        {
            ProjectBranches = Enumerable.Range(1, 25)
                .Select(index => $"branch-{index:00}")
                .ToList()
        };

        var cliExecutor = new RecordingCliExecutorService();
        var cardKit = new StubFeishuCardKitClient();
        var feishuChannel = new StubFeishuChannelService(null);
        var serviceProvider = new TestServiceProvider(userContext, projectService);
        var service = CreateService(cliExecutor, feishuChannel, serviceProvider, cardKit);

        var response = await service.HandleCardActionAsync(
            """{"action":"show_project_branch_switcher","chat_key":"oc_project_chat","project_id":"project-1"}""",
            chatId: chatId);

        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Info, response.Toast?.Type);

        var (_, cardJson) = await cardKit.WaitForRawCardSentAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("branch-01", cardJson);
        Assert.Contains("branch-12", cardJson);
        Assert.DoesNotContain("branch-13", cardJson);
        Assert.Contains("1/3", cardJson);
        Assert.Contains("\"action\":\"show_project_branch_switcher\"", cardJson);
        Assert.Contains("\"page\":1", cardJson);
    }

    [Fact]
    public async Task HandleCardActionAsync_SwitchProjectBranch_ReturnsImmediateToastAndSendsUpdatedManagerCardAsync()
    {
        const string chatId = "oc_project_chat";
        var userContext = new TestUserContextService();
        var projectService = new TestProjectService(userContext, [
            new ProjectInfo
            {
                ProjectId = "project-1",
                Name = "WmsServerV4",
                GitUrl = "http://sql-for-tfs2017:8080/tfs/DefaultCollection/WmsV4/_git/WmsServerV4",
                AuthType = "https",
                Branch = "main",
                Status = "ready",
                LocalPath = @"D:\repos\WmsServerV4",
                UpdatedAt = DateTime.Now
            }
        ]);

        var cliExecutor = new RecordingCliExecutorService();
        var cardKit = new StubFeishuCardKitClient();
        var feishuChannel = new StubFeishuChannelService(null);
        var serviceProvider = new TestServiceProvider(userContext, projectService);
        projectService.SwitchProjectBranchDelay = TimeSpan.FromSeconds(2);
        var service = CreateService(cliExecutor, feishuChannel, serviceProvider, cardKit);

        var startedAt = DateTime.UtcNow;
        var response = await service.HandleCardActionAsync(
            """{"action":"switch_project_branch","chat_key":"oc_project_chat","project_id":"project-1","branch":"release"}""",
            chatId: chatId);
        var elapsed = DateTime.UtcNow - startedAt;

        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"Expected immediate callback response, actual: {elapsed}");
        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Info, response.Toast?.Type);

        var (_, cardJson) = await cardKit.WaitForRawCardSentAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("project-1", projectService.LastSwitchedProjectId);
        Assert.Equal("release", projectService.LastSwitchedBranch);
        Assert.Contains("show_project_branch_switcher", cardJson);
        Assert.Contains("release", cardJson);
    }

    [Fact]
    public async Task HandleCardActionAsync_DeleteProject_ReturnsImmediateToastAndSendsUpdatedManagerCardAsync()
    {
        const string chatId = "oc_project_chat";
        var userContext = new TestUserContextService();
        var projectService = new TestProjectService(userContext, [
            new ProjectInfo
            {
                ProjectId = "project-1",
                Name = "WmsServerV4",
                GitUrl = "http://sql-for-tfs2017:8080/tfs/DefaultCollection/WmsV4/_git/WmsServerV4",
                AuthType = "https",
                Branch = "main",
                Status = "ready",
                LocalPath = @"D:\repos\WmsServerV4",
                UpdatedAt = DateTime.Now
            }
        ]);

        var cliExecutor = new RecordingCliExecutorService();
        var cardKit = new StubFeishuCardKitClient();
        var feishuChannel = new StubFeishuChannelService(null);
        var serviceProvider = new TestServiceProvider(userContext, projectService);
        projectService.DeleteProjectDelay = TimeSpan.FromSeconds(2);
        var service = CreateService(cliExecutor, feishuChannel, serviceProvider, cardKit);

        var startedAt = DateTime.UtcNow;
        var response = await service.HandleCardActionAsync(
            """{"action":"delete_project","chat_key":"oc_project_chat","project_id":"project-1"}""",
            chatId: chatId);
        var elapsed = DateTime.UtcNow - startedAt;

        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"Expected immediate callback response, actual: {elapsed}");
        Assert.Equal(CardActionTriggerResponseDto.ToastSuffix.ToastType.Info, response.Toast?.Type);

        var (_, cardJson) = await cardKit.WaitForRawCardSentAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("project-1", projectService.LastDeletedProjectId);
        Assert.DoesNotContain("WmsServerV4", cardJson);
        Assert.Contains("show_create_project_form", cardJson);
    }

    [Fact]
    public async Task HandleCardActionAsync_ShowCreateProjectForm_IncludesNamedSubmitButtons()
    {
        const string chatId = "oc_project_chat";
        var userContext = new TestUserContextService();
        var projectService = new TestProjectService(userContext);
        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(null);
        var serviceProvider = new TestServiceProvider(userContext, projectService);
        var service = CreateService(cliExecutor, feishuChannel, serviceProvider);

        var response = await service.HandleCardActionAsync(
            """{"action":"show_create_project_form","chat_key":"oc_project_chat"}""",
            chatId: chatId);

        var payload = SerializeResponse(response);
        Assert.Contains("\"name\":\"create_project_submit\"", payload);
        Assert.Contains("\"name\":\"fetch_project_branches_submit\"", payload);
    }

    [Fact]
    public async Task HandleCardActionAsync_ShowEditProjectForm_IncludesProjectScopedSubmitButtons()
    {
        const string chatId = "oc_project_chat";
        var userContext = new TestUserContextService();
        var projectService = new TestProjectService(userContext, [
            new ProjectInfo
            {
                ProjectId = "project-1",
                Name = "WmsServerV4",
                GitUrl = "http://sql-for-tfs2017:8080/tfs/DefaultCollection/WmsV4/_git/WmsServerV4",
                AuthType = "https",
                Branch = string.Empty,
                Status = "ready",
                LocalPath = @"D:\repos\WmsServerV4",
                UpdatedAt = DateTime.Now
            }
        ]);
        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(null);
        var serviceProvider = new TestServiceProvider(userContext, projectService);
        var service = CreateService(cliExecutor, feishuChannel, serviceProvider);

        var response = await service.HandleCardActionAsync(
            """{"action":"show_edit_project_form","chat_key":"oc_project_chat","project_id":"project-1"}""",
            chatId: chatId);

        var payload = SerializeResponse(response);
        Assert.Contains("\"name\":\"update_project_submit__project-1\"", payload);
        Assert.Contains("\"name\":\"fetch_project_branches_submit__project-1\"", payload);
    }

    [Fact]
    public async Task HandleCardActionAsync_CreateProject_UsesBoundWebUserContext()
    {
        const string chatId = "oc_project_chat";
        var userContext = new TestUserContextService();
        var projectService = new TestProjectService(userContext);
        var cliExecutor = new RecordingCliExecutorService();
        var feishuChannel = new StubFeishuChannelService(null);
        var serviceProvider = new TestServiceProvider(userContext, projectService);
        var service = CreateService(cliExecutor, feishuChannel, serviceProvider);

        var response = await service.HandleCardActionAsync(
            """{"action":"create_project","chat_key":"oc_project_chat"}""",
            formValue: new Dictionary<string, object>
            {
                ["project_name"] = "TfsProject",
                ["project_git_url"] = "http://sql-for-tfs2017:8080/tfs/DefaultCollection/WmsV4/_git/WmsServerV4",
                ["project_auth_type"] = "https",
                ["project_https_username"] = "alice",
                ["project_https_token"] = "secret",
                ["project_branch"] = string.Empty
            },
            chatId: chatId);

        Assert.Equal("luhaiyan", projectService.LastUsernameSeen);
        Assert.NotNull(projectService.LastCreatedRequest);
        Assert.Equal(string.Empty, projectService.LastCreatedRequest!.Branch);
        Assert.Equal("alice", projectService.LastCreatedRequest.HttpsUsername);
        Assert.Equal("https", projectService.LastCreatedRequest.AuthType);

        var payload = SerializeResponse(response);
        Assert.Contains("TfsProject", payload);
    }

    [Fact]
    public async Task HandleCardActionAsync_CreateSessionFromProject_UsesProjectWorkspace()
    {
        const string chatId = "oc_project_chat";
        var projectPath = Path.Combine(Path.GetTempPath(), $"feishu-project-{Guid.NewGuid():N}");
        Directory.CreateDirectory(projectPath);

        try
        {
            var userContext = new TestUserContextService();
            var projectService = new TestProjectService(userContext, [
                new ProjectInfo
                {
                    ProjectId = "project-ready",
                    Name = "ReadyProject",
                    GitUrl = "http://sql-for-tfs2017:8080/tfs/DefaultCollection/WmsV4/_git/WmsServerV4",
                    AuthType = "https",
                    Branch = string.Empty,
                    Status = "ready",
                    LocalPath = projectPath,
                    UpdatedAt = DateTime.Now
                }
            ]);

            var cliExecutor = new RecordingCliExecutorService();
            var feishuChannel = new StubFeishuChannelService(null);
            var serviceProvider = new TestServiceProvider(userContext, projectService);
            var service = CreateService(cliExecutor, feishuChannel, serviceProvider);

            var response = await service.HandleCardActionAsync(
                """{"action":"create_session_from_project","chat_key":"oc_project_chat","project_id":"project-ready"}""",
                chatId: chatId);

            Assert.Equal(projectPath, feishuChannel.CreatedWorkspacePath);
            Assert.Equal("session-new", feishuChannel.LastSwitchedSessionId);
            Assert.Equal("claude-code", feishuChannel.CreatedToolId);

            var payload = SerializeResponse(response);
            Assert.Contains("ReadyProject", payload);
        }
        finally
        {
            Directory.Delete(projectPath, recursive: true);
        }
    }

    private static string SerializeResponse(CardActionTriggerResponseDto response)
    {
        return JsonSerializer.Serialize(response);
    }

    private static List<string> ExtractCardContentStrings(CardActionTriggerResponseDto response)
    {
        using var document = JsonDocument.Parse(SerializeResponse(response));
        var contents = new List<string>();
        if (!document.RootElement.TryGetProperty("card", out var cardElement))
        {
            return contents;
        }

        CollectContentStrings(cardElement, contents);
        return contents;
    }

    private static string? ExtractToastContent(CardActionTriggerResponseDto response)
    {
        using var document = JsonDocument.Parse(SerializeResponse(response));
        return document.RootElement.TryGetProperty("toast", out var toastElement)
            && toastElement.TryGetProperty("content", out var contentElement)
            && contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString()
            : null;
    }

    private static void CollectContentStrings(JsonElement element, List<string> contents)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, "content", StringComparison.Ordinal)
                        && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value))
                        {
                            contents.Add(value);
                        }
                    }

                    CollectContentStrings(property.Value, contents);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    CollectContentStrings(item, contents);
                }
                break;
        }
    }

    private static FeishuCardActionService CreateService(
        RecordingCliExecutorService cliExecutor,
        StubFeishuChannelService feishuChannel,
        IServiceProvider serviceProvider,
        StubFeishuCardKitClient? cardKit = null,
        StubChatSessionService? chatSessionService = null)
    {
        var commandService = new FeishuCommandService(
            NullLogger<FeishuCommandService>.Instance,
            new CommandScannerService());

        return new FeishuCardActionService(
            commandService,
            new FeishuHelpCardBuilder(),
            cardKit ?? new StubFeishuCardKitClient(),
            cliExecutor,
            chatSessionService ?? new StubChatSessionService(),
            feishuChannel,
            NullLogger<FeishuCardActionService>.Instance,
            serviceProvider);
    }

    private sealed class RecordingCliExecutorService : ICliExecutorService
    {
        private readonly TaskCompletionSource<string> _executionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _executionCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _lowInterruptionExecutionStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<string> _lowInterruptionExecutionCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Dictionary<string, CliToolConfig> _tools = new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-code"] = new CliToolConfig { Id = "claude-code", Name = "Claude Code" },
            ["codex"] = new CliToolConfig { Id = "codex", Name = "Codex", UsePersistentProcess = true }
        };
        private readonly Dictionary<string, string> _cliThreadIds = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _workspacePaths = new(StringComparer.OrdinalIgnoreCase);

        public bool WasExecuted { get; private set; }

        public bool SupportsLowInterruption { get; set; }

        public string StandardExecutionContent { get; set; } = "initialized";

        public TimeSpan StandardExecutionCompletionDelay { get; set; } = TimeSpan.Zero;

        public string StandardExecutionCompletionContent { get; set; } = string.Empty;

        public bool StandardExecutionIsError { get; set; }

        public string StandardExecutionErrorMessage { get; set; } = "执行失败";

        public string LowInterruptionExecutionContent { get; set; } = "continued";

        public bool LowInterruptionExecutionIsError { get; set; }

        public string LowInterruptionExecutionErrorMessage { get; set; } = "执行失败";

        public List<string> ExecutedPrompts { get; } = new();

        public List<(string SessionId, string ToolId, string Prompt)> StandardExecutionRequests { get; } = new();

        public List<string> LowInterruptionSessionIds { get; } = new();

        public List<string?> LowInterruptionPrompts { get; } = new();

        public List<(string SessionId, string? ToolId)> SyncRequests { get; } = new();

        public List<(string SessionId, string? ToolId)> ThreadSyncRequests { get; } = new();

        public CodexThreadProviderSyncResult ThreadSyncResult { get; set; } = new()
        {
            Message = "thread sync complete"
        };

        public TaskCompletionSource<object?>? ThreadSyncBlocker { get; set; }

        public Exception? ThreadSyncException { get; set; }

        public List<(string SessionId, bool ClearCliThreadId)> ResetRequests { get; } = new();

        public List<(string SessionId, string? ToolId)> StopRequests { get; } = new();

        private readonly TaskCompletionSource<(string SessionId, string? ToolId)> _threadSyncStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void SetSessionWorkspacePath(string sessionId, string workspacePath)
        {
            _workspacePaths[sessionId] = workspacePath;
        }

        public void SetToolUsePersistentProcess(string toolId, bool usePersistentProcess)
        {
            if (_tools.TryGetValue(toolId, out var tool))
            {
                tool.UsePersistentProcess = usePersistentProcess;
            }
        }

        public async Task<string> WaitForExecutionAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            using var _ = cts.Token.Register(() => _executionStarted.TrySetCanceled(cts.Token));
            return await _executionStarted.Task;
        }

        public async Task<string> WaitForExecutionCompletionAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            using var _ = cts.Token.Register(() => _executionCompleted.TrySetCanceled(cts.Token));
            return await _executionCompleted.Task;
        }

        public async Task<string> WaitForLowInterruptionExecutionAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            using var _ = cts.Token.Register(() => _lowInterruptionExecutionStarted.TrySetCanceled(cts.Token));
            return await _lowInterruptionExecutionStarted.Task;
        }

        public async Task<string> WaitForLowInterruptionExecutionCompletionAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            using var _ = cts.Token.Register(() => _lowInterruptionExecutionCompleted.TrySetCanceled(cts.Token));
            return await _lowInterruptionExecutionCompleted.Task;
        }

        public async Task<(string SessionId, string? ToolId)> WaitForThreadSyncRequestAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            using var _ = cts.Token.Register(() => _threadSyncStarted.TrySetCanceled(cts.Token));
            return await _threadSyncStarted.Task;
        }

        public ICliToolAdapter? Adapter { get; set; }

        public bool SupportsStreamParsingEnabled { get; set; }

        public ICliToolAdapter? GetAdapter(CliToolConfig tool) => Adapter;

        public ICliToolAdapter? GetAdapterById(string toolId) => Adapter;

        public bool SupportsStreamParsing(CliToolConfig tool) => SupportsStreamParsingEnabled;

        public string? GetCliThreadId(string sessionId)
            => _cliThreadIds.TryGetValue(sessionId, out var cliThreadId) ? cliThreadId : null;

        public void SetCliThreadId(string sessionId, string threadId)
        {
            _cliThreadIds[sessionId] = threadId;
        }

        public Task ResetSessionRuntimeAsync(
            string sessionId,
            bool clearCliThreadId = true,
            CancellationToken cancellationToken = default)
        {
            ResetRequests.Add((sessionId, clearCliThreadId));
            if (clearCliThreadId)
            {
                _cliThreadIds.Remove(sessionId);
            }
            return Task.CompletedTask;
        }

        public Task StopSessionExecutionAsync(string sessionId, string? toolId = null, CancellationToken cancellationToken = default)
        {
            StopRequests.Add((sessionId, toolId));
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<StreamOutputChunk> ExecuteStreamAsync(
            string sessionId,
            string toolId,
            string userPrompt,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            WasExecuted = true;
            ExecutedPrompts.Add(userPrompt);
            StandardExecutionRequests.Add((sessionId, toolId, userPrompt));
            _executionStarted.TrySetResult(sessionId);
            try
            {
                if (StandardExecutionIsError)
                {
                    yield return new StreamOutputChunk
                    {
                        IsError = true,
                        IsCompleted = true,
                        ErrorMessage = StandardExecutionErrorMessage
                    };
                    yield break;
                }

                var hasTrailingCompletionChunk = StandardExecutionCompletionDelay > TimeSpan.Zero
                    || !string.IsNullOrEmpty(StandardExecutionCompletionContent);
                yield return new StreamOutputChunk
                {
                    Content = StandardExecutionContent,
                    IsCompleted = !hasTrailingCompletionChunk
                };

                if (hasTrailingCompletionChunk)
                {
                    if (StandardExecutionCompletionDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(StandardExecutionCompletionDelay, cancellationToken);
                    }

                    yield return new StreamOutputChunk
                    {
                        Content = StandardExecutionCompletionContent,
                        IsCompleted = true
                    };
                }
            }
            finally
            {
                _executionCompleted.TrySetResult(sessionId);
            }

            await Task.CompletedTask;
        }

        public bool SupportsLowInterruptionContinue(string toolId)
            => SupportsLowInterruption && _tools.ContainsKey(toolId);

        public bool CanStartLowInterruptionContinue(string sessionId, string toolId)
            => SupportsLowInterruptionContinue(toolId) && !string.IsNullOrWhiteSpace(GetCliThreadId(sessionId));

        public async IAsyncEnumerable<StreamOutputChunk> ExecuteLowInterruptionContinueStreamAsync(
            string sessionId,
            string toolId,
            string? prompt = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            LowInterruptionSessionIds.Add(sessionId);
            LowInterruptionPrompts.Add(prompt);
            _lowInterruptionExecutionStarted.TrySetResult(sessionId);
            try
            {
                if (LowInterruptionExecutionIsError)
                {
                    yield return new StreamOutputChunk
                    {
                        IsError = true,
                        IsCompleted = true,
                        ErrorMessage = LowInterruptionExecutionErrorMessage
                    };
                    yield break;
                }

                yield return new StreamOutputChunk
                {
                    Content = LowInterruptionExecutionContent,
                    IsCompleted = true
                };
            }
            finally
            {
                _lowInterruptionExecutionCompleted.TrySetResult(sessionId);
            }

            await Task.CompletedTask;
        }

        public List<CliToolConfig> GetAvailableTools(string? username = null) => _tools.Values.ToList();

        public CliToolConfig? GetTool(string toolId, string? username = null)
            => _tools.TryGetValue(toolId, out var tool) ? tool : null;

        public bool ValidateTool(string toolId, string? username = null) => _tools.ContainsKey(toolId);

        public void CleanupSessionWorkspace(string sessionId) { }

        public void CleanupExpiredWorkspaces() { }

        public string GetSessionWorkspacePath(string sessionId)
        {
            return _workspacePaths.TryGetValue(sessionId, out var workspacePath)
                ? workspacePath
                : throw new InvalidOperationException($"浼氳瘽 {sessionId} 宸ヤ綔鐩綍涓嶅瓨鍦ㄦ垨宸茶娓呯悊锛岃閲嶆柊鍒涘缓浼氳瘽");
        }

        public Task<Dictionary<string, string>> GetToolEnvironmentVariablesAsync(string toolId, string? username = null)
            => Task.FromResult(new Dictionary<string, string>());

        public Task<CcSwitchSessionSnapshot?> SyncSessionCcSwitchSnapshotAsync(string sessionId, string? toolId = null, CancellationToken cancellationToken = default)
        {
            SyncRequests.Add((sessionId, toolId));
            return Task.FromResult<CcSwitchSessionSnapshot?>(new CcSwitchSessionSnapshot
            {
                ToolId = toolId ?? string.Empty,
                UsesSnapshot = true,
                ProviderId = "provider-from-test",
                ProviderName = "Provider From Test",
                ProviderCategory = "custom",
                SourceLiveConfigPath = @"C:\Users\tester\.codex\config.toml",
                SnapshotRelativePath = Path.Combine(".codex", "config.toml"),
                SyncedAt = DateTime.Now
            });
        }

        public async Task<CodexThreadProviderSyncResult> SyncCodexThreadProviderAsync(string sessionId, string? toolId = null, CancellationToken cancellationToken = default)
        {
            ThreadSyncRequests.Add((sessionId, toolId));
            _threadSyncStarted.TrySetResult((sessionId, toolId));

            if (ThreadSyncBlocker != null)
            {
                await ThreadSyncBlocker.Task.WaitAsync(cancellationToken);
            }

            if (ThreadSyncException != null)
            {
                throw ThreadSyncException;
            }

            return ThreadSyncResult;
        }

        public Task<bool> SaveToolEnvironmentVariablesAsync(string toolId, Dictionary<string, string> envVars, string? username = null)
            => Task.FromResult(true);

        public byte[]? GetWorkspaceFile(string sessionId, string relativePath)
        {
            var workspacePath = GetSessionWorkspacePath(sessionId);
            var fullPath = Path.Combine(workspacePath, relativePath);
            return File.Exists(fullPath) ? File.ReadAllBytes(fullPath) : null;
        }

        public byte[]? GetWorkspaceZip(string sessionId) => null;

        public Task<bool> UploadFileToWorkspaceAsync(string sessionId, string fileName, byte[] fileContent, string? relativePath = null) => Task.FromResult(true);

        public Task<bool> CreateFolderInWorkspaceAsync(string sessionId, string folderPath) => Task.FromResult(true);

        public Task<bool> DeleteWorkspaceItemAsync(string sessionId, string relativePath, bool isDirectory) => Task.FromResult(true);

        public Task<bool> MoveFileInWorkspaceAsync(string sessionId, string sourcePath, string targetPath) => Task.FromResult(true);

        public Task<bool> CopyFileInWorkspaceAsync(string sessionId, string sourcePath, string targetPath) => Task.FromResult(true);

        public Task<bool> RenameFileInWorkspaceAsync(string sessionId, string oldPath, string newName) => Task.FromResult(true);

        public Task<int> BatchDeleteFilesAsync(string sessionId, List<string> relativePaths) => Task.FromResult(0);

        public Task<string> InitializeSessionWorkspaceAsync(string sessionId, string? projectId = null, bool includeGit = false) => Task.FromResult(sessionId);

        public void RefreshWorkspaceRootCache() { }
    }

    private sealed class StubChatSessionService : IChatSessionService
    {
        public Dictionary<string, List<ChatMessage>> Messages { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void AddMessage(string sessionId, ChatMessage message)
        {
            if (!Messages.TryGetValue(sessionId, out var sessionMessages))
            {
                sessionMessages = [];
                Messages[sessionId] = sessionMessages;
            }

            sessionMessages.Add(message);
        }

        public void ClearSession(string sessionId) => Messages.Remove(sessionId);

        public ChatMessage? GetMessage(string sessionId, string messageId) => null;

        public List<ChatMessage> GetMessages(string sessionId)
            => Messages.TryGetValue(sessionId, out var sessionMessages)
                ? [.. sessionMessages]
                : [];

        public void UpdateMessage(string sessionId, string messageId, Action<ChatMessage> updateAction) { }
    }

    private sealed class RecordingReplyTtsOrchestrator : IReplyTtsOrchestrator
    {
        public List<FeishuCompletedReplyTtsRequest> Requests { get; } = new();

        public TaskCompletionSource<FeishuCompletedReplyTtsRequest> WhenQueued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<FeishuCompletedReplyTtsRequest> WhenCallbackCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<FeishuCompletedReplyTtsRequest, Task>? OnQueued { get; set; }

        public async Task QueueCompletedReplyAsync(FeishuCompletedReplyTtsRequest request)
        {
            Requests.Add(request);
            WhenQueued.TrySetResult(request);
            if (OnQueued == null)
            {
                WhenCallbackCompleted.TrySetResult(request);
                return;
            }

            try
            {
                await OnQueued(request);
                WhenCallbackCompleted.TrySetResult(request);
            }
            catch (Exception ex)
            {
                WhenCallbackCompleted.TrySetException(ex);
                throw;
            }
        }
    }

    private sealed class StubFeishuCardKitClient : IFeishuCardKitClient
    {
        private readonly TaskCompletionSource<(string ChatId, string CardJson)> _rawCardSent = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FeishuOptions? LastRawCardOptionsOverride { get; private set; }

        public FeishuStreamingCardChrome? LastStreamingChrome { get; private set; }

        public FeishuStreamingCardChrome? InitialStreamingChromeSnapshot { get; private set; }

        public FeishuStreamingCardChrome? FinalStreamingChromeSnapshot { get; private set; }

        public string? InitialStreamingStatusMarkdown { get; private set; }

        public List<string> StreamingUpdates { get; } = new();

        public List<string> StreamingStatusMarkdownSnapshots { get; } = new();

        public string? FinalStreamingContent { get; private set; }

        public string? FinalStreamingStatusMarkdown { get; private set; }

        public int? FailUpdateOnAttempt { get; set; }

        public int UpdateAttemptCount { get; private set; }

        public Task<string> CreateCardAsync(string initialContent, string? title = null, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        public Task<bool> UpdateCardAsync(string cardId, string content, int sequence, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => Task.FromResult(true);

        public Task<string> SendCardMessageAsync(string chatId, string cardId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        public Task<string> SendTextMessageAsync(string chatId, string content, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        public Task<string> ReplyCardMessageAsync(string replyMessageId, string cardId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        public Task<string> ReplyTextMessageAsync(string replyMessageId, string content, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        public Task<FeishuDownloadedAttachment> DownloadIncomingAttachmentAsync(
            FeishuIncomingAttachment attachment,
            CancellationToken cancellationToken = default,
            FeishuOptions? optionsOverride = null)
            => throw new NotSupportedException();

        public Task<FeishuStreamingHandle> CreateStreamingHandleAsync(string chatId, string? replyMessageId, string initialContent, string? title = null, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null, FeishuStreamingCardChrome? chrome = null)
        {
            LastStreamingChrome = chrome;
            InitialStreamingChromeSnapshot = CloneChrome(chrome);
            InitialStreamingStatusMarkdown = chrome?.StatusMarkdown;
            if (!string.IsNullOrWhiteSpace(InitialStreamingStatusMarkdown))
            {
                StreamingStatusMarkdownSnapshots.Add(InitialStreamingStatusMarkdown);
            }

            return Task.FromResult(new FeishuStreamingHandle(
                "card-1",
                "message-1",
                (content, _) =>
                {
                    UpdateAttemptCount++;
                    if (FailUpdateOnAttempt.HasValue && UpdateAttemptCount >= FailUpdateOnAttempt.Value)
                    {
                        return Task.FromResult(false);
                    }

                    StreamingUpdates.Add(content);
                    if (!string.IsNullOrWhiteSpace(chrome?.StatusMarkdown))
                    {
                        StreamingStatusMarkdownSnapshots.Add(chrome.StatusMarkdown);
                    }

                    return Task.FromResult(true);
                },
                (content, _) =>
                {
                    FinalStreamingContent = content;
                    FinalStreamingChromeSnapshot = CloneChrome(chrome);
                    FinalStreamingStatusMarkdown = chrome?.StatusMarkdown;
                    if (!string.IsNullOrWhiteSpace(FinalStreamingStatusMarkdown))
                    {
                        StreamingStatusMarkdownSnapshots.Add(FinalStreamingStatusMarkdown);
                    }

                    return Task.FromResult(true);
                },
                throttleMs: 0));
        }

        public Task<string> SendRawCardAsync(string chatId, string cardJson, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            LastRawCardOptionsOverride = optionsOverride;
            _rawCardSent.TrySetResult((chatId, cardJson));
            return Task.FromResult("raw-card-message");
        }

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

        public async Task<(string ChatId, string CardJson)> WaitForRawCardSentAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            using var _ = cts.Token.Register(() => _rawCardSent.TrySetCanceled(cts.Token));
            return await _rawCardSent.Task;
        }

        private static FeishuStreamingCardChrome? CloneChrome(FeishuStreamingCardChrome? chrome)
        {
            if (chrome == null)
            {
                return null;
            }

            return new FeishuStreamingCardChrome
            {
                StatusMarkdown = chrome.StatusMarkdown,
                LatestToolCallMarkdown = chrome.LatestToolCallMarkdown,
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
                        Value = action.Value,
                        RowKey = action.RowKey
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
                    .ToList(),
                BottomNoticeMarkdowns = chrome.BottomNoticeMarkdowns.ToList()
            };
        }
    }

    private sealed class StubFeishuChannelService(string? currentSessionId) : IFeishuChannelService
    {
        private readonly TaskCompletionSource<(string ChatId, string Content, string? Username, string? AppId)> _messageSent = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<(PreparedMessageSubmission Submission, string ChatId, string? ReplyToMessageId, string? Username, string? AppId)> _preparedSubmissionExecuted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _preparedSubmissionCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _preparedSubmissionRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsRunning => true;

        public bool SessionExecutionActive { get; set; }

        public bool BlockPreparedSubmissionExecution { get; set; }

        public string? CreatedWorkspacePath { get; private set; }

        public string? CreatedToolId { get; private set; }

        public string? LastSwitchedSessionId { get; private set; }

        public string? LastSentChatId { get; private set; }

        public string? LastSentMessage { get; private set; }

        public string? LastSentUsername { get; private set; }

        public string? LastSentAppId { get; private set; }

        public string? LastClosedSessionId { get; private set; }

        public string? LastStoppedSessionId { get; private set; }

        public string? LastPausedSessionId { get; private set; }

        public TimeSpan? LastPauseDuration { get; private set; }

        public string ResolvedToolId { get; set; } = "claude-code";

        public string? SessionUsername { get; set; } = "luhaiyan";

        public Task<string> SendMessageAsync(string chatId, string content, string? username = null, string? appId = null)
        {
            LastSentChatId = chatId;
            LastSentMessage = content;
            LastSentUsername = username;
            LastSentAppId = appId;
            _messageSent.TrySetResult((chatId, content, username, appId));
            return Task.FromResult("notify-message");
        }

        public Task<string> ReplyMessageAsync(string messageId, string content, string? username = null, string? appId = null)
            => Task.FromResult("reply-message");

        public Task<FeishuStreamingHandle> SendStreamingMessageAsync(string chatId, string initialContent, string? replyToMessageId = null, string? username = null, string? appId = null)
            => throw new NotSupportedException();

        public Task HandleIncomingMessageAsync(FeishuIncomingMessage message) => throw new NotSupportedException();

        public Task ExecutePreparedSubmissionAsync(
            PreparedMessageSubmission submission,
            string chatId,
            string? replyToMessageId = null,
            string? username = null,
            string? appId = null,
            CancellationToken cancellationToken = default)
        {
            return ExecutePreparedSubmissionCoreAsync(submission, chatId, replyToMessageId, username, appId);
        }

        private async Task ExecutePreparedSubmissionCoreAsync(
            PreparedMessageSubmission submission,
            string chatId,
            string? replyToMessageId,
            string? username,
            string? appId)
        {
            _preparedSubmissionExecuted.TrySetResult((submission, chatId, replyToMessageId, username, appId));
            if (BlockPreparedSubmissionExecution)
            {
                await _preparedSubmissionRelease.Task;
            }

            _preparedSubmissionCompleted.TrySetResult();
        }

        public string? GetCurrentSession(string chatKey, string? username = null) => currentSessionId;

        public DateTime? GetSessionLastActiveTime(string sessionId) => DateTime.UtcNow;

        public List<string> GetChatSessions(string chatKey, string? username = null) => currentSessionId == null ? [] : [currentSessionId];

        public bool SwitchCurrentSession(string chatKey, string sessionId, string? username = null)
        {
            LastSwitchedSessionId = sessionId;
            return true;
        }

        public bool CloseSession(string chatKey, string sessionId, string? username = null)
        {
            LastClosedSessionId = sessionId;
            return true;
        }

        public bool IsSessionExecutionActive(string sessionId) => SessionExecutionActive;

        public bool StopSessionExecution(string sessionId)
        {
            SessionExecutionActive = false;
            LastStoppedSessionId = sessionId;
            return true;
        }

        public void PauseSessionStatusPulse(string sessionId, TimeSpan duration)
        {
            LastPausedSessionId = sessionId;
            LastPauseDuration = duration;
        }

        public string CreateNewSession(FeishuIncomingMessage message, string? customWorkspacePath = null, string? toolId = null)
        {
            CreatedWorkspacePath = customWorkspacePath;
            CreatedToolId = toolId;
            return "session-new";
        }

        public string? GetSessionUsername(string chatKey) => SessionUsername;

        public string ResolveToolId(string chatKey, string? username = null) => ResolvedToolId;

        public async Task<(string ChatId, string Content, string? Username, string? AppId)> WaitForMessageAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            using var _ = cts.Token.Register(() => _messageSent.TrySetCanceled(cts.Token));
            return await _messageSent.Task;
        }

        public async Task<(PreparedMessageSubmission Submission, string ChatId, string? ReplyToMessageId, string? Username, string? AppId)> WaitForPreparedSubmissionAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            using var _ = cts.Token.Register(() => _preparedSubmissionExecuted.TrySetCanceled(cts.Token));
            return await _preparedSubmissionExecuted.Task;
        }

        public void ReleasePreparedSubmissionExecution()
        {
            _preparedSubmissionRelease.TrySetResult();
        }

        public async Task WaitForPreparedSubmissionCompletionAsync(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            using var _ = cts.Token.Register(() => _preparedSubmissionCompleted.TrySetCanceled(cts.Token));
            await _preparedSubmissionCompleted.Task;
        }
    }

    private sealed class StubExternalCliSessionHistoryService(IEnumerable<ExternalCliHistoryMessage> messages)
        : IExternalCliSessionHistoryService
    {
        private readonly List<ExternalCliHistoryMessage> _messages = messages.ToList();

        public string? LastToolId { get; private set; }

        public string? LastCliThreadId { get; private set; }

        public int LastMaxCount { get; private set; }

        public string SourcePath { get; set; } = @"D:\repo\.codex\sessions\2026\05\04\rollout-history.jsonl";

        public Task<ExternalCliHistoryResult> GetRecentHistoryAsync(
            string toolId,
            string cliThreadId,
            int maxCount = 20,
            string? workspacePath = null,
            CancellationToken cancellationToken = default)
        {
            LastToolId = toolId;
            LastCliThreadId = cliThreadId;
            LastMaxCount = maxCount;
            return Task.FromResult(new ExternalCliHistoryResult
            {
                Messages = _messages.TakeLast(maxCount).ToList(),
                SourcePath = SourcePath
            });
        }

        public Task<List<ExternalCliHistoryMessage>> GetRecentMessagesAsync(
            string toolId,
            string cliThreadId,
            int maxCount = 20,
            string? workspacePath = null,
            CancellationToken cancellationToken = default)
        {
            LastToolId = toolId;
            LastCliThreadId = cliThreadId;
            LastMaxCount = maxCount;
            return Task.FromResult(_messages.TakeLast(maxCount).ToList());
        }
    }

    [Fact]
    public async Task HandleCardActionAsync_ExecuteCommand_NormalizesFeishuPostJsonPromptBeforeExecution()
    {
        const string chatId = "oc_current_chat";
        const string activeSessionId = "session-acp";

        var workspaceRoot = Path.Combine(Path.GetTempPath(), $"feishu-card-post-json-{Guid.NewGuid():N}");
        var workspacePath = Path.Combine(workspaceRoot, "superpowers");
        Directory.CreateDirectory(workspacePath);

        const string rawPostJson = """
{"zh_cn":{"title":"WhereAuto","content":[[{"tag":"text","text":"鏈€缁堟鏋跺舰鎬併€?}],[{"tag":"text","text":"鏇村悎鐞嗙殑鏄妸浜嬪姟杈圭晫鎻愬崌鍒?Application 鍛戒护灞傘€?}],[{"tag":"text","text":"鍐嶇敤superpowers鎶€鑳借璁轰笅鎬庝箞瀹炵幇杩欎簺鍐呭銆?}]]}}
""";
        const string expectedPrompt = """
# WhereAuto

最终框架形态。
更合理的是把事务边界提升到 Application 命令层。
再用superpowers技能讨论下怎么实现这些内容。
""";

        try
        {
            var cliExecutor = new RecordingCliExecutorService();
            cliExecutor.SetSessionWorkspacePath(activeSessionId, workspacePath);

            var feishuChannel = new StubFeishuChannelService(activeSessionId);
            var service = CreateService(cliExecutor, feishuChannel, new TestServiceProvider());

            await service.HandleCardActionAsync(
                """{"action":"execute_command"}""",
                chatId: chatId,
                operatorUserId: "ou_test_user",
                inputValues: rawPostJson);

            await cliExecutor.WaitForExecutionAsync(TimeSpan.FromSeconds(3));

            var prompt = Assert.Single(cliExecutor.ExecutedPrompts);
            Assert.Contains("WhereAuto", prompt, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    private sealed class StubExternalCliSessionService(IEnumerable<ExternalCliSessionSummary> sessions)
        : IExternalCliSessionService
    {
        private readonly List<ExternalCliSessionSummary> _sessions = sessions.ToList();

        public Task<List<ExternalCliSessionSummary>> DiscoverAsync(
            string username,
            string? toolId = null,
            int maxCount = 20,
            CancellationToken cancellationToken = default)
        {
            var query = _sessions.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(toolId))
            {
                query = query.Where(x => string.Equals(x.ToolId, toolId, StringComparison.OrdinalIgnoreCase));
            }

            return Task.FromResult(query.Take(maxCount).ToList());
        }

        public Task<ImportExternalCliSessionResult> ImportAsync(
            string username,
            ImportExternalCliSessionRequest request,
            string? feishuChatKey = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubMessageSubmissionService : IMessageSubmissionService
    {
        public MessageDraft? LastDraft { get; private set; }

        public Task<PreparedMessageSubmission> PrepareAsync(MessageDraft draft, CancellationToken cancellationToken = default)
        {
            LastDraft = draft;

            return Task.FromResult(new PreparedMessageSubmission
            {
                SessionId = draft.SessionId,
                ToolId = draft.ToolId,
                Text = draft.Text,
                Attachments =
                [
                    .. draft.Attachments.Select(attachment => new MessageAttachment
                    {
                        Id = attachment.Id,
                        DisplayName = attachment.FileName,
                        MimeType = attachment.ContentType,
                        Extension = Path.GetExtension(attachment.FileName),
                        SizeBytes = attachment.Content?.LongLength ?? 0,
                        Kind = MessageAttachmentKind.Text,
                        WorkspaceRelativePath = attachment.FileName
                    })
                ]
            });
        }
    }

    private sealed class TestServiceProvider : IServiceProvider, IServiceScopeFactory, IServiceScope
    {
        private readonly StubFeishuUserBindingService _bindingService = new();
        private readonly StubUserFeishuBotConfigService _feishuBotConfigService;
        private readonly StubSessionDirectoryService _sessionDirectoryService = new();
        private readonly ICcSwitchService _ccSwitchService;
        private readonly TestUserContextService _userContextService;
        private readonly TestProjectService _projectService;
        private readonly IChatSessionRepository _chatSessionRepository;
        private readonly IExternalCliSessionHistoryService _externalCliSessionHistoryService;
        private readonly IExternalCliSessionService _externalCliSessionService;
        private readonly ISuperpowersCapabilityService _superpowersCapabilityService;
        private readonly IGoalCapabilityService _goalCapabilityService;
        private readonly IReplyTtsOrchestrator? _replyTtsOrchestrator;
        private readonly IMessageSubmissionService _messageSubmissionService;
        private readonly IFeishuAttachmentDraftService _attachmentDraftService;

        public TestServiceProvider(
            TestUserContextService? userContextService = null,
            TestProjectService? projectService = null,
            IChatSessionRepository? chatSessionRepository = null,
            ICcSwitchService? ccSwitchService = null,
            IExternalCliSessionHistoryService? externalCliSessionHistoryService = null,
            IExternalCliSessionService? externalCliSessionService = null,
            ISuperpowersCapabilityService? superpowersCapabilityService = null,
            IGoalCapabilityService? goalCapabilityService = null,
            IReplyTtsOrchestrator? replyTtsOrchestrator = null,
            StubUserFeishuBotConfigService? feishuBotConfigService = null,
            IMessageSubmissionService? messageSubmissionService = null,
            IFeishuAttachmentDraftService? attachmentDraftService = null)
        {
            _feishuBotConfigService = feishuBotConfigService ?? new StubUserFeishuBotConfigService();
            _userContextService = userContextService ?? new TestUserContextService();
            _projectService = projectService ?? new TestProjectService(_userContextService);
            _chatSessionRepository = chatSessionRepository ?? new StubChatSessionRepository([]);
            _ccSwitchService = ccSwitchService ?? new StubCcSwitchService();
            _externalCliSessionHistoryService = externalCliSessionHistoryService ?? new StubExternalCliSessionHistoryService([]);
            _externalCliSessionService = externalCliSessionService ?? new StubExternalCliSessionService([]);
            _superpowersCapabilityService = superpowersCapabilityService ?? new StubSuperpowersCapabilityService();
            _goalCapabilityService = goalCapabilityService ?? new StubGoalCapabilityService();
            _replyTtsOrchestrator = replyTtsOrchestrator;
            _messageSubmissionService = messageSubmissionService ?? new StubMessageSubmissionService();
            _attachmentDraftService = attachmentDraftService ?? new FeishuAttachmentDraftService();
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IServiceScopeFactory))
            {
                return this;
            }

            if (serviceType == typeof(IFeishuUserBindingService))
            {
                return _bindingService;
            }

            if (serviceType == typeof(ISessionDirectoryService))
            {
                return _sessionDirectoryService;
            }

            if (serviceType == typeof(IUserFeishuBotConfigService))
            {
                return _feishuBotConfigService;
            }

            if (serviceType == typeof(IUserContextService))
            {
                return _userContextService;
            }

            if (serviceType == typeof(IProjectService))
            {
                return _projectService;
            }

            if (serviceType == typeof(IChatSessionRepository))
            {
                return _chatSessionRepository;
            }

            if (serviceType == typeof(ICcSwitchService))
            {
                return _ccSwitchService;
            }

            if (serviceType == typeof(IExternalCliSessionHistoryService))
            {
                return _externalCliSessionHistoryService;
            }

            if (serviceType == typeof(IExternalCliSessionService))
            {
                return _externalCliSessionService;
            }

            if (serviceType == typeof(ISuperpowersCapabilityService))
            {
                return _superpowersCapabilityService;
            }

            if (serviceType == typeof(IGoalCapabilityService))
            {
                return _goalCapabilityService;
            }

            if (serviceType == typeof(IReplyTtsOrchestrator))
            {
                return _replyTtsOrchestrator;
            }

            if (serviceType == typeof(IMessageSubmissionService))
            {
                return _messageSubmissionService;
            }

            if (serviceType == typeof(IFeishuAttachmentDraftService))
            {
                return _attachmentDraftService;
            }

            if (serviceType == typeof(FeishuAttachmentDraftCardBuilder))
            {
                return new FeishuAttachmentDraftCardBuilder();
            }

            return null;
        }

        public IServiceScope CreateScope() => this;

        public IServiceProvider ServiceProvider => this;

        public void Dispose()
        {
        }
    }

    private sealed class StubCcSwitchService : ICcSwitchService
    {
        public bool IsManagedTool(string toolId)
        {
            return string.Equals(toolId, "claude-code", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolId, "codex", StringComparison.OrdinalIgnoreCase)
                || string.Equals(toolId, "opencode", StringComparison.OrdinalIgnoreCase);
        }

        public Task<CcSwitchStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CcSwitchStatus { IsDetected = true });
        }

        public Task<CcSwitchToolStatus> GetToolStatusAsync(string toolId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CcSwitchToolStatus
            {
                ToolId = toolId,
                ToolName = toolId,
                IsManaged = IsManagedTool(toolId),
                IsDetected = true
            });
        }

        public Task<IReadOnlyDictionary<string, CcSwitchToolStatus>> GetToolStatusesAsync(IEnumerable<string> toolIds, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = toolIds.ToDictionary(
                toolId => toolId,
                toolId => new CcSwitchToolStatus
                {
                    ToolId = toolId,
                    ToolName = toolId,
                    IsManaged = IsManagedTool(toolId),
                    IsDetected = true
                },
                StringComparer.OrdinalIgnoreCase);
            return Task.FromResult<IReadOnlyDictionary<string, CcSwitchToolStatus>>(result);
        }

        public Task<CcSwitchModelCatalog> GetModelCatalogAsync(string toolId, string? providerId = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var models = toolId switch
            {
                "codex" => new List<CcSwitchModelOption>
                {
                    new() { Id = "gpt-5.4", DisplayName = "gpt-5.4" },
                    new() { Id = "gpt-5.4-mini", DisplayName = "gpt-5.4-mini" }
                },
                "claude-code" => new List<CcSwitchModelOption>
                {
                    new() { Id = "claude-sonnet-4-6", DisplayName = "claude-sonnet-4-6" }
                },
                _ => new List<CcSwitchModelOption>()
            };

            return Task.FromResult(new CcSwitchModelCatalog
            {
                ToolId = toolId,
                ToolName = toolId,
                IsManaged = IsManagedTool(toolId),
                IsDetected = true,
                ProviderId = providerId,
                Models = models,
                IsRemoteFetched = true
            });
        }
    }

    private sealed class StubSuperpowersCapabilityService : ISuperpowersCapabilityService
    {
        public SuperpowersCapabilityState CachedState { get; set; } = SuperpowersCapabilityState.Unknown;

        public string? CachedMessage { get; set; }

        public SuperpowersCapabilityState ProbeState { get; set; } = SuperpowersCapabilityState.Available;

        public SuperpowersCapabilityProbeOutcome ProbeOutcome { get; set; } = SuperpowersCapabilityProbeOutcome.Available;

        public string? ProbeMessage { get; set; }

        public List<SuperpowersCapabilityContext> ProbeContexts { get; } = new();

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
            ProbeContexts.Add(new SuperpowersCapabilityContext
            {
                ToolId = context.ToolId,
                ProviderId = context.ProviderId,
                WorkspacePath = context.WorkspacePath
            });

            return Task.FromResult(new SuperpowersCapabilityProbeResult
            {
                ToolId = context.ToolId,
                ProviderId = context.ProviderId ?? SuperpowersCapabilityService.UnscopedProviderId,
                CacheKey = $"{context.ToolId}::{context.ProviderId ?? SuperpowersCapabilityService.UnscopedProviderId}",
                State = ProbeState,
                Outcome = ProbeOutcome,
                Message = ProbeMessage
            });
        }
    }

    private sealed class StubGoalCapabilityService : IGoalCapabilityService
    {
        public GoalCapabilityState CachedState { get; set; } = GoalCapabilityState.Unknown;

        public string? CachedMessage { get; set; }

        public GoalCapabilityState ProbeState { get; set; } = GoalCapabilityState.Available;

        public GoalCapabilityProbeOutcome ProbeOutcome { get; set; } = GoalCapabilityProbeOutcome.Available;

        public string? ProbeMessage { get; set; }

        public List<GoalCapabilityContext> ProbeContexts { get; } = new();

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
            ProbeContexts.Add(new GoalCapabilityContext
            {
                ToolId = context.ToolId,
                ProviderId = context.ProviderId,
                WorkspacePath = context.WorkspacePath
            });

            return Task.FromResult(new GoalCapabilityProbeResult
            {
                ToolId = context.ToolId,
                ProviderId = context.ProviderId ?? GoalCapabilityService.UnscopedProviderId,
                CacheKey = $"{context.ToolId}::{context.ProviderId ?? GoalCapabilityService.UnscopedProviderId}",
                State = ProbeState,
                Outcome = ProbeOutcome,
                Message = ProbeMessage
            });
        }
    }

    private sealed class StubChatSessionRepository(IEnumerable<ChatSessionEntity> sessions) : IChatSessionRepository
    {
        private readonly List<ChatSessionEntity> _sessions = sessions.ToList();

        public SqlSugarScope GetDB() => throw new NotSupportedException();

        public List<ChatSessionEntity> GetList() => _sessions.ToList();

        public Task<List<ChatSessionEntity>> GetListAsync() => Task.FromResult(GetList());

        public List<ChatSessionEntity> GetList(Expression<Func<ChatSessionEntity, bool>> whereExpression)
            => _sessions.AsQueryable().Where(whereExpression).ToList();

        public Task<List<ChatSessionEntity>> GetListAsync(Expression<Func<ChatSessionEntity, bool>> whereExpression)
            => Task.FromResult(GetList(whereExpression));

        public int Count(Expression<Func<ChatSessionEntity, bool>> whereExpression)
            => _sessions.AsQueryable().Count(whereExpression);

        public Task<int> CountAsync(Expression<Func<ChatSessionEntity, bool>> whereExpression)
            => Task.FromResult(Count(whereExpression));

        public PageList<ChatSessionEntity> GetPageList(Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page)
            => throw new NotSupportedException();

        public PageList<P> GetPageList<P>(Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page)
            => throw new NotSupportedException();

        public Task<PageList<ChatSessionEntity>> GetPageListAsync(Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page)
            => throw new NotSupportedException();

        public Task<PageList<P>> GetPageListAsync<P>(Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page)
            => throw new NotSupportedException();

        public PageList<ChatSessionEntity> GetPageList(Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, Expression<Func<ChatSessionEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc)
            => throw new NotSupportedException();

        public Task<PageList<ChatSessionEntity>> GetPageListAsync(Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, Expression<Func<ChatSessionEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc)
            => throw new NotSupportedException();

        public PageList<P> GetPageList<P>(Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, Expression<Func<ChatSessionEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc)
            => throw new NotSupportedException();

        public Task<PageList<P>> GetPageListAsync<P>(Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, Expression<Func<ChatSessionEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc)
            => throw new NotSupportedException();

        public PageList<ChatSessionEntity> GetPageList(List<IConditionalModel> conditionalList, PageModel page)
            => throw new NotSupportedException();

        public Task<PageList<ChatSessionEntity>> GetPageListAsync(List<IConditionalModel> conditionalList, PageModel page)
            => throw new NotSupportedException();

        public PageList<ChatSessionEntity> GetPageList(List<IConditionalModel> conditionalList, PageModel page, Expression<Func<ChatSessionEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc)
            => throw new NotSupportedException();

        public Task<PageList<ChatSessionEntity>> GetPageListAsync(List<IConditionalModel> conditionalList, PageModel page, Expression<Func<ChatSessionEntity, object>> orderByExpression = null, OrderByType orderByType = OrderByType.Asc)
            => throw new NotSupportedException();

        public ChatSessionEntity GetById(dynamic id)
            => _sessions.First(x => string.Equals(x.SessionId, id?.ToString(), StringComparison.OrdinalIgnoreCase));

        public Task<ChatSessionEntity> GetByIdAsync(dynamic id)
            => Task.FromResult(_sessions.FirstOrDefault(x => string.Equals(x.SessionId, id?.ToString(), StringComparison.OrdinalIgnoreCase)))!;

        public ChatSessionEntity GetSingle(Expression<Func<ChatSessionEntity, bool>> whereExpression)
            => _sessions.AsQueryable().Single(whereExpression);

        public Task<ChatSessionEntity> GetSingleAsync(Expression<Func<ChatSessionEntity, bool>> whereExpression)
            => Task.FromResult(GetSingle(whereExpression));

        public ChatSessionEntity GetFirst(Expression<Func<ChatSessionEntity, bool>> whereExpression)
            => _sessions.AsQueryable().First(whereExpression);

        public Task<ChatSessionEntity> GetFirstAsync(Expression<Func<ChatSessionEntity, bool>> whereExpression)
            => Task.FromResult(GetFirst(whereExpression));

        public bool Insert(ChatSessionEntity obj)
        {
            _sessions.Add(obj);
            return true;
        }

        public Task<bool> InsertAsync(ChatSessionEntity obj) => Task.FromResult(Insert(obj));

        public bool InsertRange(List<ChatSessionEntity> objs)
        {
            _sessions.AddRange(objs);
            return true;
        }

        public Task<bool> InsertRangeAsync(List<ChatSessionEntity> objs) => Task.FromResult(InsertRange(objs));

        public int InsertReturnIdentity(ChatSessionEntity obj) => throw new NotSupportedException();

        public Task<int> InsertReturnIdentityAsync(ChatSessionEntity obj) => throw new NotSupportedException();

        public long InsertReturnBigIdentity(ChatSessionEntity obj) => throw new NotSupportedException();

        public Task<long> InsertReturnBigIdentityAsync(ChatSessionEntity obj) => throw new NotSupportedException();

        public bool DeleteByIds(dynamic[] ids) => throw new NotSupportedException();

        public Task<bool> DeleteByIdsAsync(dynamic[] ids) => throw new NotSupportedException();

        public bool Delete(dynamic id) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(dynamic id) => throw new NotSupportedException();

        public bool Delete(ChatSessionEntity obj) => _sessions.Remove(obj);

        public Task<bool> DeleteAsync(ChatSessionEntity obj) => Task.FromResult(Delete(obj));

        public bool Delete(Expression<Func<ChatSessionEntity, bool>> whereExpression) => throw new NotSupportedException();

        public Task<bool> DeleteAsync(Expression<Func<ChatSessionEntity, bool>> whereExpression) => throw new NotSupportedException();

        public bool Update(ChatSessionEntity obj)
        {
            var index = _sessions.FindIndex(x => string.Equals(x.SessionId, obj.SessionId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return false;
            }

            _sessions[index] = obj;
            return true;
        }

        public Task<bool> UpdateAsync(ChatSessionEntity obj) => Task.FromResult(Update(obj));

        public bool UpdateRange(List<ChatSessionEntity> objs) => throw new NotSupportedException();

        public bool InsertOrUpdate(ChatSessionEntity obj) => throw new NotSupportedException();

        public Task<bool> InsertOrUpdateAsync(ChatSessionEntity obj) => throw new NotSupportedException();

        public Task<bool> UpdateRangeAsync(List<ChatSessionEntity> objs) => throw new NotSupportedException();

        public bool IsAny(Expression<Func<ChatSessionEntity, bool>> whereExpression)
            => _sessions.AsQueryable().Any(whereExpression);

        public Task<bool> IsAnyAsync(Expression<Func<ChatSessionEntity, bool>> whereExpression)
            => Task.FromResult(IsAny(whereExpression));

        public Task<List<ChatSessionEntity>> GetByUsernameAsync(string username)
        {
            return Task.FromResult(_sessions
                .Where(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase))
                .ToList());
        }

        public Task<ChatSessionEntity?> GetByIdAndUsernameAsync(string sessionId, string username)
        {
            return Task.FromResult(_sessions.FirstOrDefault(x =>
                string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)));
        }

        public Task<bool> DeleteByIdAndUsernameAsync(string sessionId, string username) => throw new NotSupportedException();

        public Task<List<ChatSessionEntity>> GetByUsernameOrderByUpdatedAtAsync(string username)
        {
            return Task.FromResult(_sessions
                .Where(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => x.UpdatedAt)
                .ToList());
        }

        public Task<ChatSessionEntity?> GetByUsernameToolAndCliThreadIdAsync(string username, string toolId, string cliThreadId)
        {
            return Task.FromResult(_sessions.FirstOrDefault(x =>
                string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.ToolId, toolId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.CliThreadId, cliThreadId, StringComparison.OrdinalIgnoreCase)));
        }

        public Task<ChatSessionEntity?> GetByToolAndCliThreadIdAsync(string toolId, string cliThreadId)
        {
            return Task.FromResult(_sessions.FirstOrDefault(x =>
                string.Equals(x.ToolId, toolId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.CliThreadId, cliThreadId, StringComparison.OrdinalIgnoreCase)));
        }

        public Task<bool> UpdateCliThreadIdAsync(string sessionId, string? cliThreadId)
        {
            var session = _sessions.FirstOrDefault(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            if (session == null)
            {
                return Task.FromResult(false);
            }

            session.CliThreadId = cliThreadId;
            session.UpdatedAt = DateTime.Now;
            return Task.FromResult(true);
        }

        public Task<bool> UpdateWorkspaceBindingAsync(string sessionId, string? workspacePath, bool isCustomWorkspace)
        {
            var session = _sessions.FirstOrDefault(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            if (session == null)
            {
                return Task.FromResult(false);
            }

            session.WorkspacePath = workspacePath;
            session.IsCustomWorkspace = isCustomWorkspace;
            session.UpdatedAt = DateTime.Now;
            return Task.FromResult(true);
        }

        public Task<bool> UpdateSessionTitleAsync(string sessionId, string title)
        {
            var session = _sessions.FirstOrDefault(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            if (session == null)
            {
                return Task.FromResult(false);
            }

            session.Title = title;
            session.UpdatedAt = DateTime.Now;
            return Task.FromResult(true);
        }

        public Task<bool> UpdateCcSwitchSnapshotAsync(string sessionId, CcSwitchSessionSnapshot snapshot)
        {
            var session = _sessions.FirstOrDefault(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            if (session == null)
            {
                return Task.FromResult(false);
            }

            session.UsesCcSwitchSnapshot = snapshot.UsesSnapshot;
            session.CcSwitchSnapshotToolId = snapshot.ToolId;
            session.CcSwitchProviderId = snapshot.ProviderId;
            session.CcSwitchProviderName = snapshot.ProviderName;
            session.CcSwitchProviderCategory = snapshot.ProviderCategory;
            session.CcSwitchLiveConfigPath = snapshot.SourceLiveConfigPath;
            session.CcSwitchSnapshotRelativePath = snapshot.SnapshotRelativePath;
            session.CcSwitchSnapshotSyncedAt = snapshot.SyncedAt;
            session.UpdatedAt = DateTime.Now;
            return Task.FromResult(true);
        }

        public Task<List<ChatSessionEntity>> GetByFeishuChatKeyAsync(string feishuChatKey)
        {
            return Task.FromResult(_sessions
                .Where(x => string.Equals(x.FeishuChatKey, feishuChatKey, StringComparison.OrdinalIgnoreCase))
                .ToList());
        }

        public Task<ChatSessionEntity?> GetActiveByFeishuChatKeyAsync(string feishuChatKey)
        {
            return Task.FromResult(_sessions.FirstOrDefault(x =>
                string.Equals(x.FeishuChatKey, feishuChatKey, StringComparison.OrdinalIgnoreCase) &&
                x.IsFeishuActive));
        }

        public Task<bool> SetActiveSessionAsync(string feishuChatKey, string sessionId) => throw new NotSupportedException();

        public Task<bool> CloseFeishuSessionAsync(string feishuChatKey, string sessionId) => throw new NotSupportedException();

        public Task<string> CreateFeishuSessionAsync(string feishuChatKey, string username, string? workspacePath = null, string? toolId = null)
            => throw new NotSupportedException();
    }

    private sealed class StubFeishuUserBindingService : IFeishuUserBindingService
    {
        public Task<string?> GetBoundWebUsernameAsync(string feishuUserId) => Task.FromResult<string?>(null);

        public Task<bool> IsBoundAsync(string feishuUserId) => Task.FromResult(false);

        public Task<(bool Success, string? ErrorMessage, string? WebUsername)> BindAsync(string feishuUserId, string webUsername, string? appId = null)
            => Task.FromResult((true, (string?)null, (string?)webUsername));

        public Task<bool> UnbindAsync(string feishuUserId) => Task.FromResult(true);

        public Task<List<string>> GetBindableWebUsernamesAsync(string? appId = null) => Task.FromResult(new List<string>());

        public Task<HashSet<string>> GetAllBoundWebUsernamesAsync() => Task.FromResult(new HashSet<string>());
    }

    private sealed class StubUserFeishuBotConfigService : IUserFeishuBotConfigService
    {
        private static readonly FeishuOptions DefaultOptions = new()
        {
            Enabled = true,
            AppId = "shared-app-id",
            AppSecret = "test-app-secret",
            DefaultCardTitle = "AI鍔╂墜",
            ThinkingMessage = "鎬濊€冧腑..."
        };

        private readonly Dictionary<string, UserFeishuBotConfigEntity> _configs = new(StringComparer.OrdinalIgnoreCase);

        public void Seed(UserFeishuBotConfigEntity config)
        {
            _configs[config.Username] = Clone(config);
        }

        public Task<UserFeishuBotConfigEntity?> GetByUsernameAsync(string username)
        {
            return Task.FromResult<UserFeishuBotConfigEntity?>(
                _configs.TryGetValue(username, out var config)
                    ? Clone(config)
                    : null);
        }

        public Task<UserFeishuBotConfigEntity?> GetByAppIdAsync(string appId)
        {
            var config = _configs.Values.FirstOrDefault(item =>
                string.Equals(item.AppId, appId, StringComparison.OrdinalIgnoreCase));
            return Task.FromResult<UserFeishuBotConfigEntity?>(config == null ? null : Clone(config));
        }

        public Task<UserFeishuBotConfigSaveResult> SaveAsync(UserFeishuBotConfigEntity config)
        {
            _configs[config.Username] = Clone(config);
            return Task.FromResult(UserFeishuBotConfigSaveResult.Saved());
        }

        public Task<bool> DeleteAsync(string username) => Task.FromResult(true);

        public Task<string?> FindConflictingUsernameByAppIdAsync(string username, string? appId)
            => Task.FromResult<string?>(null);

        public Task<List<UserFeishuBotConfigEntity>> GetAutoStartCandidatesAsync()
            => Task.FromResult(_configs.Values.Select(Clone).ToList());

        public Task<bool> UpdateRuntimePreferenceAsync(string username, bool autoStartEnabled, DateTime? lastStartedAt = null)
            => Task.FromResult(true);

        public FeishuOptions GetSharedDefaults() => DefaultOptions;

        public Task<FeishuOptions> GetEffectiveOptionsAsync(string? username)
            => Task.FromResult(DefaultOptions);

        public Task<FeishuOptions?> GetEffectiveOptionsByAppIdAsync(string? appId)
            => Task.FromResult<FeishuOptions?>(string.IsNullOrWhiteSpace(appId)
                ? null
                : new FeishuOptions
                {
                    Enabled = true,
                    AppId = appId,
                    AppSecret = "test-app-secret",
                    DefaultCardTitle = "AI鍔╂墜",
                    ThinkingMessage = "鎬濊€冧腑..."
                });

        private static UserFeishuBotConfigEntity Clone(UserFeishuBotConfigEntity config)
        {
            return new UserFeishuBotConfigEntity
            {
                Id = config.Id,
                Username = config.Username,
                IsEnabled = config.IsEnabled,
                AutoStartEnabled = config.AutoStartEnabled,
                AppId = config.AppId,
                AppSecret = config.AppSecret,
                EncryptKey = config.EncryptKey,
                VerificationToken = config.VerificationToken,
                DefaultCardTitle = config.DefaultCardTitle,
                ThinkingMessage = config.ThinkingMessage,
                HttpTimeoutSeconds = config.HttpTimeoutSeconds,
                StreamingThrottleMs = config.StreamingThrottleMs,
                ReplyTtsEnabled = config.ReplyTtsEnabled,
                ReplyTtsVoiceId = config.ReplyTtsVoiceId,
                LastStartedAt = config.LastStartedAt,
                CreatedAt = config.CreatedAt,
                UpdatedAt = config.UpdatedAt
            };
        }
    }

    private sealed class StubSessionDirectoryService : ISessionDirectoryService
    {
        private readonly string _allowedRoot = @"D:\VSWorkshop\allowed";

        public Task SetSessionWorkspaceAsync(string sessionId, string username, string directoryPath, bool isCustom = true) => Task.CompletedTask;

        public Task<string?> GetSessionWorkspaceAsync(string sessionId, string username) => Task.FromResult<string?>(null);

        public Task SwitchSessionWorkspaceAsync(string sessionId, string username, string newDirectoryPath) => Task.CompletedTask;

        public Task<bool> VerifySessionWorkspacePermissionAsync(string sessionId, string username, string requiredPermission = "write") => Task.FromResult(true);

        public Task<List<object>> GetUserAccessibleDirectoriesAsync(string username)
        {
            var directories = new List<object>
            {
                new
                {
                    Type = "owned",
                    DirectoryType = "workspace",
                    Alias = "ACP",
                    Permission = "owner",
                    DirectoryPath = @"D:\VSWorkshop\acp"
                }
            };

            return Task.FromResult(directories);
        }

        public Task<AllowedDirectoryBrowseResult> BrowseAllowedDirectoriesAsync(string? path, string? username = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Task.FromResult(new AllowedDirectoryBrowseResult
                {
                    HasConfiguredRoots = true,
                    Roots =
                    [
                        new AllowedDirectoryRootItem
                        {
                            Name = "allowed",
                            Path = _allowedRoot
                        }
                    ]
                });
            }

            if (string.Equals(path, _allowedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new AllowedDirectoryBrowseResult
                {
                    HasConfiguredRoots = true,
                    CurrentPath = _allowedRoot,
                    RootPath = _allowedRoot,
                    Entries =
                    [
                        new AllowedDirectoryBrowseEntry
                        {
                            Name = "src",
                            Path = Path.Combine(_allowedRoot, "src"),
                            IsDirectory = true
                        },
                        new AllowedDirectoryBrowseEntry
                        {
                            Name = "README.md",
                            Path = Path.Combine(_allowedRoot, "README.md"),
                            IsDirectory = false,
                            Size = 42,
                            Extension = ".md"
                        }
                    ]
                });
            }

            if (string.Equals(path, Path.Combine(_allowedRoot, "src"), StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new AllowedDirectoryBrowseResult
                {
                    HasConfiguredRoots = true,
                    CurrentPath = path,
                    ParentPath = _allowedRoot,
                    RootPath = _allowedRoot,
                    Entries = []
                });
            }

            throw new DirectoryNotFoundException(path);
        }
    }

    private sealed class TestUserContextService : IUserContextService
    {
        private string _currentUsername = "default";

        public string GetCurrentUsername() => _currentUsername;

        public string GetCurrentRole() => "admin";

        public bool IsAuthenticated() => true;

        public void SetCurrentUsername(string username)
        {
            _currentUsername = username;
        }
    }

    private sealed class TestProjectService : IProjectService
    {
        private readonly IUserContextService _userContextService;
        private readonly Dictionary<string, ProjectInfo> _projects;

        public TestProjectService(IUserContextService userContextService, IEnumerable<ProjectInfo>? seedProjects = null)
        {
            _userContextService = userContextService;
            _projects = (seedProjects ?? [])
                .ToDictionary(project => project.ProjectId, project => project, StringComparer.OrdinalIgnoreCase);
        }

        public List<string> ProjectBranches { get; set; } = ["main", "release"];

        public string? LastUsernameSeen { get; private set; }

        public CreateProjectRequest? LastCreatedRequest { get; private set; }

        public string? LastSwitchedProjectId { get; private set; }

        public string? LastSwitchedBranch { get; private set; }

        public string? LastDeletedProjectId { get; private set; }

        public TimeSpan GetProjectBranchesDelay { get; set; }

        public TimeSpan SwitchProjectBranchDelay { get; set; }

        public TimeSpan DeleteProjectDelay { get; set; }

        public TimeSpan CloneProjectDelay { get; set; }

        public Task<List<ProjectInfo>> GetProjectsAsync()
        {
            LastUsernameSeen = _userContextService.GetCurrentUsername();
            return Task.FromResult(_projects.Values.OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase).ToList());
        }

        public Task<ProjectInfo?> GetProjectAsync(string projectId)
        {
            LastUsernameSeen = _userContextService.GetCurrentUsername();
            _projects.TryGetValue(projectId, out var project);
            return Task.FromResult(project);
        }

        public Task<(ProjectInfo? Project, string? ErrorMessage)> CreateProjectAsync(CreateProjectRequest request)
        {
            LastUsernameSeen = _userContextService.GetCurrentUsername();
            LastCreatedRequest = request;

            var project = new ProjectInfo
            {
                ProjectId = $"project-{_projects.Count + 1}",
                Name = request.Name,
                GitUrl = request.GitUrl,
                AuthType = request.AuthType,
                Branch = request.Branch,
                Status = "pending",
                UpdatedAt = DateTime.Now
            };

            _projects[project.ProjectId] = project;
            return Task.FromResult<(ProjectInfo?, string?)>((project, null));
        }

        public Task<(ProjectInfo? Project, string? ErrorMessage)> CreateProjectFromZipAsync(string projectName, byte[] zipFileContent)
            => throw new NotSupportedException();

        public Task<(bool Success, string? ErrorMessage)> UpdateProjectAsync(string projectId, UpdateProjectRequest request)
        {
            LastUsernameSeen = _userContextService.GetCurrentUsername();
            if (!_projects.TryGetValue(projectId, out var project))
            {
                return Task.FromResult((false, (string?)"项目不存在"));
            }

            project.Name = request.Name ?? project.Name;
            project.GitUrl = request.GitUrl ?? project.GitUrl;
            project.AuthType = request.AuthType ?? project.AuthType;
            project.Branch = request.Branch ?? project.Branch;
            project.UpdatedAt = DateTime.Now;
            return Task.FromResult((true, (string?)null));
        }

        public Task<(bool Success, string? ErrorMessage)> DeleteProjectAsync(string projectId)
        {
            LastUsernameSeen = _userContextService.GetCurrentUsername();
            LastDeletedProjectId = projectId;
            if (DeleteProjectDelay > TimeSpan.Zero)
            {
                return WaitAndDeleteAsync();
            }

            return Task.FromResult((_projects.Remove(projectId), (string?)null));

            async Task<(bool Success, string? ErrorMessage)> WaitAndDeleteAsync()
            {
                await Task.Delay(DeleteProjectDelay);
                return (_projects.Remove(projectId), (string?)null);
            }
        }

        public Task<(bool Success, string? ErrorMessage)> CloneProjectAsync(string projectId, Action<CloneProgress>? progress = null)
        {
            LastUsernameSeen = _userContextService.GetCurrentUsername();
            if (CloneProjectDelay > TimeSpan.Zero)
            {
                return WaitAndCloneAsync();
            }

            return Task.FromResult((true, (string?)null));

            async Task<(bool Success, string? ErrorMessage)> WaitAndCloneAsync()
            {
                await Task.Delay(CloneProjectDelay);
                return (true, (string?)null);
            }
        }

        public Task<(bool Success, string? ErrorMessage)> PullProjectAsync(string projectId)
            => Task.FromResult((true, (string?)null));

        public Task<(List<string> Branches, string? ErrorMessage)> GetBranchesAsync(GetBranchesRequest request)
            => Task.FromResult<(List<string>, string?)>((ProjectBranches.ToList(), null));

        public Task<(List<string> Branches, string? ErrorMessage)> GetProjectBranchesAsync(string projectId)
        {
            LastUsernameSeen = _userContextService.GetCurrentUsername();
            if (GetProjectBranchesDelay > TimeSpan.Zero)
            {
                return WaitAndReturnBranchesAsync();
            }

            return Task.FromResult<(List<string>, string?)>((ProjectBranches.ToList(), null));

            async Task<(List<string> Branches, string? ErrorMessage)> WaitAndReturnBranchesAsync()
            {
                await Task.Delay(GetProjectBranchesDelay);
                return (ProjectBranches.ToList(), null);
            }
        }

        public async Task<(bool Success, string? ErrorMessage)> SwitchProjectBranchAsync(string projectId, string branch)
        {
            LastUsernameSeen = _userContextService.GetCurrentUsername();
            LastSwitchedProjectId = projectId;
            LastSwitchedBranch = branch;

            if (SwitchProjectBranchDelay > TimeSpan.Zero)
            {
                await Task.Delay(SwitchProjectBranchDelay);
            }

            if (!_projects.TryGetValue(projectId, out var project))
            {
                return (false, (string?)"项目不存在");
            }

            project.Branch = branch;
            project.UpdatedAt = DateTime.Now;
            return (true, (string?)null);
        }

        public string? GetProjectLocalPath(string projectId)
        {
            return _projects.TryGetValue(projectId, out var project) ? project.LocalPath : null;
        }

        public Task<(bool Success, string? ErrorMessage)> CopyProjectToWorkspaceAsync(string projectId, string targetPath, bool includeGit)
            => Task.FromResult((true, (string?)null));
    }
}
