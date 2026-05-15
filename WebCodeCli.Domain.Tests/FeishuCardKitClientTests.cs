using System.Net;
using System.Text.Json;
using FeishuNetSdk.Im.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service.Channels;

namespace WebCodeCli.Domain.Tests;

public class FeishuCardKitClientTests
{
    [Fact]
    public async Task UploadAudioFileAsync_PostsMultipartFormDataWithDuration()
    {
        var tempFile = Path.GetTempFileName();
        await File.WriteAllTextAsync(tempFile, "opus", TestContext.Current.CancellationToken);

        try
        {
            var handler = new StubHttpMessageHandler(
            [
                CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
                CreateJsonResponse("""{"code":0,"data":{"file_key":"file_v2_123"}}""")
            ]);

            var client = CreateClient(handler);

            var fileKey = await client.UploadAudioFileAsync(tempFile, 3200, TestContext.Current.CancellationToken);

            Assert.Equal("file_v2_123", fileKey);
            Assert.Equal(
            [
                "/open-apis/auth/v3/tenant_access_token/internal",
                "/open-apis/im/v1/files"
            ], handler.RequestPaths);
            Assert.Contains("multipart/form-data", handler.RequestContentTypes[1], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("name=file_type", handler.RequestBodies[1], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("opus", handler.RequestBodies[1], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("name=file_name", handler.RequestBodies[1], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("name=duration", handler.RequestBodies[1], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("3200", handler.RequestBodies[1], StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public async Task SendAudioMessageAsync_SendsAudioPayload()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_audio_success"}}""")
        ]);

        var client = CreateClient(handler);

        var messageId = await client.SendAudioMessageAsync("oc_audio_chat", "file_v2_123", 3200, TestContext.Current.CancellationToken);

        Assert.Equal("om_audio_success", messageId);
        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/im/v1/messages"
        ], handler.RequestPaths);

        using var requestDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("audio", requestDoc.RootElement.GetProperty("msg_type").GetString());
        Assert.Equal("oc_audio_chat", requestDoc.RootElement.GetProperty("receive_id").GetString());

        using var contentDoc = JsonDocument.Parse(requestDoc.RootElement.GetProperty("content").GetString()!);
        Assert.Equal("file_v2_123", contentDoc.RootElement.GetProperty("file_key").GetString());
    }

    [Fact]
    public async Task SendTextMessageAsync_SendsTextPayload()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_text_success"}}""")
        ]);

        var client = CreateClient(handler);

        var messageId = await client.SendTextMessageAsync("oc_text_chat", "已完成", TestContext.Current.CancellationToken);

        Assert.Equal("om_text_success", messageId);
        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/im/v1/messages"
        ], handler.RequestPaths);

        using var requestDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("text", requestDoc.RootElement.GetProperty("msg_type").GetString());
        Assert.Equal("oc_text_chat", requestDoc.RootElement.GetProperty("receive_id").GetString());

        using var contentDoc = JsonDocument.Parse(requestDoc.RootElement.GetProperty("content").GetString()!);
        Assert.Equal("已完成", contentDoc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ReplyTextMessageAsync_SendsTextPayload()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_text_reply_success"}}""")
        ]);

        var client = CreateClient(handler);

        var messageId = await client.ReplyTextMessageAsync("om_reply", "已完成", TestContext.Current.CancellationToken);

        Assert.Equal("om_text_reply_success", messageId);
        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/im/v1/messages/om_reply/reply"
        ], handler.RequestPaths);

        using var requestDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("text", requestDoc.RootElement.GetProperty("msg_type").GetString());

        using var contentDoc = JsonDocument.Parse(requestDoc.RootElement.GetProperty("content").GetString()!);
        Assert.Equal("已完成", contentDoc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ReplyRawCardAsync_Throws_WhenReplyReturnsBusinessError()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":10002,"msg":"invalid card payload"}""")
        ]);

        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ReplyRawCardAsync(
                "om_reply",
                """{"schema":"2.0","body":{"elements":[]}}""",
                TestContext.Current.CancellationToken));

        Assert.Contains("Reply raw Feishu card message failed", exception.Message);
        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/cardkit/v1/cards",
            "/open-apis/im/v1/messages/om_reply/reply"
        ], handler.RequestPaths);
    }

    [Fact]
    public async Task ReplyElementsCardAsync_CreatesCardThenRepliesWithCardId()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_reply_success"}}""")
        ]);

        var client = CreateClient(handler);
        var card = new ElementsCardV2Dto
        {
            Header = new ElementsCardV2Dto.HeaderSuffix
            {
                Template = "blue",
                Title = new HeaderTitleElement { Content = "Help card" }
            },
            Body = new ElementsCardV2Dto.BodySuffix
            {
                Elements =
                [
                    new
                    {
                        tag = "div",
                        text = new { tag = "plain_text", content = "hello" }
                    }
                ]
            }
        };

        var messageId = await client.ReplyElementsCardAsync("om_reply", card, TestContext.Current.CancellationToken);

        Assert.Equal("om_reply_success", messageId);
        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/cardkit/v1/cards",
            "/open-apis/im/v1/messages/om_reply/reply"
        ], handler.RequestPaths);

        using var createDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("card_json", createDoc.RootElement.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.String, createDoc.RootElement.GetProperty("data").ValueKind);

        using var cardDoc = JsonDocument.Parse(createDoc.RootElement.GetProperty("data").GetString()!);
        Assert.Equal("2.0", cardDoc.RootElement.GetProperty("schema").GetString());
        Assert.Equal("blue", cardDoc.RootElement.GetProperty("header").GetProperty("template").GetString());
        Assert.Equal("Help card", cardDoc.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString());
        Assert.Equal("div", cardDoc.RootElement.GetProperty("body").GetProperty("elements")[0].GetProperty("tag").GetString());

        using var requestDoc = JsonDocument.Parse(handler.RequestBodies[2]);
        Assert.Equal("interactive", requestDoc.RootElement.GetProperty("msg_type").GetString());
        Assert.Equal(JsonValueKind.String, requestDoc.RootElement.GetProperty("content").ValueKind);

        using var replyDoc = JsonDocument.Parse(requestDoc.RootElement.GetProperty("content").GetString()!);
        Assert.Equal("card", replyDoc.RootElement.GetProperty("type").GetString());
        Assert.Equal("card_123", replyDoc.RootElement.GetProperty("data").GetProperty("card_id").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_FallsBackToReadableChineseStatusHeader()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""")
        ]);

        var client = CreateClient(handler);
        var chrome = new FeishuStreamingCardChrome();
        chrome.OverflowOptions.Add(new FeishuStreamingCardOverflowOption
        {
            Text = "Backend API",
            Value = new { action = "switch_session", session_id = "session-2", chat_key = "oc_stream_chat" }
        });
        chrome.OverflowOptions.Add(new FeishuStreamingCardOverflowOption
        {
            Text = "模型/会话管理...",
            Value = new { action = "open_session_manager" }
        });

        await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "still have backlog",
            "AI 助手",
            TestContext.Current.CancellationToken,
            chrome: chrome);

        using var createDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        using var cardDoc = JsonDocument.Parse(createDoc.RootElement.GetProperty("data").GetString()!);
        Assert.False(cardDoc.RootElement.GetProperty("config").TryGetProperty("streaming_mode", out _));
        var elements = cardDoc.RootElement.GetProperty("body").GetProperty("elements");
        var statusModule = elements[0];
        var overflow = statusModule.GetProperty("extra");

        Assert.Equal("当前会话", statusModule.GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("overflow", overflow.GetProperty("tag").GetString());
        Assert.Equal("Backend API", overflow.GetProperty("options")[0].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("{\"action\":\"switch_session\",\"session_id\":\"session-2\",\"chat_key\":\"oc_stream_chat\"}", overflow.GetProperty("options")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_RendersBottomPromptForm()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""")
        ]);

        var client = CreateClient(handler);
        var chrome = new FeishuStreamingCardChrome
        {
            StatusMarkdown = "当前会话"
        };
        chrome.BottomPrompt = new FeishuStreamingCardBottomPrompt
        {
            InputName = LowInterruptionContinueDefaults.PromptFieldName,
            InputLabel = "少打断提示词",
            Placeholder = LowInterruptionContinueDefaults.PromptPlaceholder,
            DefaultValue = LowInterruptionContinueDefaults.DefaultPrompt,
            ButtonText = "少打断执行",
            ButtonType = "primary",
            Value = new
            {
                action = "low_interruption_continue",
                session_id = "session-1",
                chat_key = "oc_stream_chat",
                tool_id = "codex"
            }
        };

        await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "still have backlog",
            "AI 助手",
            TestContext.Current.CancellationToken,
            chrome: chrome);

        using var createDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        using var cardDoc = JsonDocument.Parse(createDoc.RootElement.GetProperty("data").GetString()!);
        var elements = cardDoc.RootElement.GetProperty("body").GetProperty("elements");
        Assert.Equal("🟥🟥🟥 **回复内容**", elements[1].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("🟥🟥🟥 **Superpowers 工作流**", elements[3].GetProperty("text").GetProperty("content").GetString());
        var bottomActionModule = elements.EnumerateArray().Last();

        Assert.Equal("form", bottomActionModule.GetProperty("tag").GetString());

        var buttonRow = bottomActionModule.GetProperty("elements")[0];
        Assert.Equal("column_set", buttonRow.GetProperty("tag").GetString());

        var input = buttonRow.GetProperty("columns")[0].GetProperty("elements")[0];
        Assert.Equal("input", input.GetProperty("tag").GetString());
        Assert.Equal(LowInterruptionContinueDefaults.PromptFieldName, input.GetProperty("name").GetString());
        Assert.Equal(LowInterruptionContinueDefaults.DefaultPrompt, input.GetProperty("default_value").GetString());

        var button = buttonRow.GetProperty("columns")[1].GetProperty("elements")[0];
        Assert.Equal("button", button.GetProperty("tag").GetString());
        Assert.Equal("primary", button.GetProperty("type").GetString());
        Assert.Equal("少打断执行", button.GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("form_submit", button.GetProperty("action_type").GetString());
        Assert.Equal("low_interruption_continue", button.GetProperty("value").GetProperty("action").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_UsesUniqueSubmitButtonNames_ForMultipleBottomPrompts()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""")
        ]);

        var client = CreateClient(handler);
        var chrome = new FeishuStreamingCardChrome
        {
            StatusMarkdown = "当前会话",
            BottomPrompt = new FeishuStreamingCardBottomPrompt
            {
                FormName = "superpowers_quick_action_form",
                InputName = "superpowers_quick_input",
                InputLabel = "使用 superpowers 工作流",
                Placeholder = "输入后提交",
                DefaultValue = string.Empty,
                ButtonText = "提交",
                ButtonType = "primary",
                Value = new { action = "submit_superpowers_quick_input" }
            },
            AdditionalBottomPrompts =
            [
                new FeishuStreamingCardBottomPrompt
                {
                    FormName = "goal_quick_action_form",
                    InputName = "goal_quick_input",
                    InputLabel = "使用 /goal 工作流",
                    Placeholder = "输入后提交",
                    DefaultValue = string.Empty,
                    ButtonText = "提交",
                    ButtonType = "primary",
                    Value = new { action = "submit_goal_quick_input" }
                }
            ]
        };

        await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "still have backlog",
            "AI 助手",
            TestContext.Current.CancellationToken,
            chrome: chrome);

        using var createDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        using var cardDoc = JsonDocument.Parse(createDoc.RootElement.GetProperty("data").GetString()!);
        var elements = cardDoc.RootElement.GetProperty("body").GetProperty("elements");

        var firstFormButton = elements[4].GetProperty("elements")[0].GetProperty("columns")[1].GetProperty("elements")[0];
        var secondFormButton = elements[5].GetProperty("elements")[0].GetProperty("columns")[1].GetProperty("elements")[0];

        Assert.Equal("superpowers_quick_input_submit", firstFormButton.GetProperty("name").GetString());
        Assert.Equal("goal_quick_input_submit", secondFormButton.GetProperty("name").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_RendersTopChipGroupsBetweenStatusAndBody()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""")
        ]);

        var client = CreateClient(handler);
        var chrome = new FeishuStreamingCardChrome
        {
            StatusMarkdown = "褰撳墠浼氳瘽"
        };
        chrome.TopChipGroups.Add(new FeishuStreamingCardTopChipGroup
        {
            Kind = "model",
            IsEnabled = true,
            SummaryMarkdown = "🤖 模型：`gpt-5.3-codex-spark`",
            OverflowOptions =
            [
                new FeishuStreamingCardOverflowOption
                {
                    Text = "gpt-5.3-codex-spark",
                    Value = new { action = "switch_streaming_card_model", session_id = "session-1", chat_key = "oc_stream_chat", model = "gpt-5.3-codex-spark" }
                },
                new FeishuStreamingCardOverflowOption
                {
                    Text = "gpt-5.2",
                    Value = new { action = "switch_streaming_card_model", session_id = "session-1", chat_key = "oc_stream_chat", model = "gpt-5.2" }
                }
            ]
        });

        await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "still have backlog",
            "AI 鍔╂墜",
            TestContext.Current.CancellationToken,
            chrome: chrome);

        using var createDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        using var cardDoc = JsonDocument.Parse(createDoc.RootElement.GetProperty("data").GetString()!);
        var elements = cardDoc.RootElement.GetProperty("body").GetProperty("elements");

        Assert.Equal("div", elements[0].GetProperty("tag").GetString());
        Assert.Equal("🟥🟥🟥 **思考等级**", elements[1].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("div", elements[2].GetProperty("tag").GetString());
        Assert.Equal("🟥🟥🟥 **回复内容**", elements[3].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("markdown", elements[4].GetProperty("tag").GetString());
        Assert.Equal("🤖 模型：`gpt-5.3-codex-spark`", elements[2].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("overflow", elements[2].GetProperty("extra").GetProperty("tag").GetString());
        var options = elements[2].GetProperty("extra").GetProperty("options");
        Assert.Equal(2, options.GetArrayLength());
        Assert.Equal("gpt-5.3-codex-spark", options[0].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("gpt-5.2", options[1].GetProperty("text").GetProperty("content").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_SplitsTopChipGroupIntoMultipleRowsWhenMoreThanSixItems()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""")
        ]);

        var client = CreateClient(handler);
        var chrome = new FeishuStreamingCardChrome
        {
            StatusMarkdown = "当前会话"
        };

        var items = Enumerable.Range(1, 7)
            .Select(index => new FeishuStreamingCardTopChipItem
            {
                Text = $"gpt-5.{index}",
                IsActive = index == 1,
                IsEnabled = true,
                Value = new
                {
                    action = "switch_streaming_card_model",
                    session_id = "session-1",
                    chat_key = "oc_stream_chat",
                    model = $"gpt-5.{index}"
                }
            })
            .ToList();

        chrome.TopChipGroups.Add(new FeishuStreamingCardTopChipGroup
        {
            Kind = "model",
            Items = items
        });

        await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "still have backlog",
            "AI 助手",
            TestContext.Current.CancellationToken,
            chrome: chrome);

        using var createDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        using var cardDoc = JsonDocument.Parse(createDoc.RootElement.GetProperty("data").GetString()!);
        var elements = cardDoc.RootElement.GetProperty("body").GetProperty("elements");

        Assert.Equal("🟥🟥🟥 **思考等级**", elements[1].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("column_set", elements[2].GetProperty("tag").GetString());
        Assert.Equal("column_set", elements[3].GetProperty("tag").GetString());
        Assert.Equal(6, elements[2].GetProperty("columns").GetArrayLength());
        Assert.Equal(1, elements[3].GetProperty("columns").GetArrayLength());
        Assert.Equal("gpt-5.1", elements[2].GetProperty("columns")[0].GetProperty("elements")[0].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("gpt-5.7", elements[3].GetProperty("columns")[0].GetProperty("elements")[0].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("🟥🟥🟥 **回复内容**", elements[4].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("markdown", elements[5].GetProperty("tag").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_RendersWorkflowSectionMarkerBeforeBottomActions()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""")
        ]);

        var client = CreateClient(handler);
        var chrome = new FeishuStreamingCardChrome
        {
            StatusMarkdown = "当前会话"
        };
        chrome.BottomActions.Add(new FeishuStreamingCardBottomAction
        {
            Text = "执行 plan",
            Type = "primary",
            Value = new { action = "execute_superpowers_plan", session_id = "session-1" }
        });

        await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "still have backlog",
            "AI 助手",
            TestContext.Current.CancellationToken,
            chrome: chrome);

        using var createDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        using var cardDoc = JsonDocument.Parse(createDoc.RootElement.GetProperty("data").GetString()!);
        var elements = cardDoc.RootElement.GetProperty("body").GetProperty("elements");

        Assert.Equal("🟥🟥🟥 **回复内容**", elements[1].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("markdown", elements[2].GetProperty("tag").GetString());
        Assert.Equal("🟥🟥🟥 **Superpowers 工作流**", elements[3].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("column_set", elements[4].GetProperty("tag").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_KeepsClientStreamingMode_WhenNoOverflowActionsExist()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""")
        ]);

        var client = CreateClient(handler);
        var chrome = new FeishuStreamingCardChrome
        {
            StatusMarkdown = "当前会话"
        };

        await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "still have backlog",
            "AI 助手",
            TestContext.Current.CancellationToken,
            chrome: chrome);

        using var createDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        using var cardDoc = JsonDocument.Parse(createDoc.RootElement.GetProperty("data").GetString()!);

        Assert.True(cardDoc.RootElement.GetProperty("config").GetProperty("streaming_mode").GetBoolean());
    }

    [Fact]
    public async Task FeishuStreamingHandle_FinishAsync_WaitsForInflightUpdate_AndBlocksLaterUpdates()
    {
        var updateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new List<string>();

        var handle = new FeishuStreamingHandle(
            "card-1",
            "message-1",
            async (content, sequence) =>
            {
                operations.Add($"update:{sequence}:{content}");
                updateEntered.TrySetResult();
                await releaseUpdate.Task;
            },
            (content, sequence) =>
            {
                operations.Add($"finish:{sequence}:{content}");
                return Task.CompletedTask;
            },
            throttleMs: 0);

        var inflightUpdate = handle.UpdateAsync("streaming");
        await updateEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var finishTask = handle.FinishAsync("final");
        Assert.False(finishTask.IsCompleted);

        releaseUpdate.TrySetResult();

        await inflightUpdate;
        await finishTask;
        await handle.UpdateAsync("late");

        Assert.Equal(
            [
                "update:1:streaming",
                "finish:2:final"
            ],
            operations);
    }

    [Fact]
    public async Task FeishuStreamingHandle_UpdateAsync_HonorsQuietWindowAfterUpdate()
    {
        var operations = new List<string>();
        var handle = new FeishuStreamingHandle(
            "card-1",
            "message-1",
            (content, sequence) =>
            {
                operations.Add($"update:{sequence}:{content}");
                return Task.CompletedTask;
            },
            (content, sequence) => Task.CompletedTask,
            throttleMs: 0,
            quietWindowAfterUpdateMs: 120);

        await handle.UpdateAsync("first");
        await handle.UpdateAsync("second");
        await Task.Delay(160, TestContext.Current.CancellationToken);
        await handle.UpdateAsync("third");

        Assert.Equal(
            [
                "update:1:first",
                "update:2:third"
            ],
            operations);
    }

    private static FeishuCardKitClient CreateClient(StubHttpMessageHandler handler)
    {
        var options = Options.Create(new FeishuOptions
        {
            AppId = "app-id",
            AppSecret = "app-secret",
            HttpTimeoutSeconds = 30
        });

        return new FeishuCardKitClient(
            options,
            NullLogger<FeishuCardKitClient>.Instance,
            new StubHttpClientFactory(new HttpClient(handler)));
    }

    private static HttpResponseMessage CreateJsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json)
        };
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHttpMessageHandler(IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<string> RequestPaths { get; } = [];
        public List<string> RequestBodies { get; } = [];
        public List<string?> RequestContentTypes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            RequestContentTypes.Add(request.Content?.Headers.ContentType?.MediaType);
            RequestBodies.Add(request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            if (_responses.Count == 0)
            {
                throw new Xunit.Sdk.XunitException($"Unexpected request sent to {request.RequestUri}.");
            }

            return _responses.Dequeue();
        }
    }
}
