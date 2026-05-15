using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FeishuNetSdk.Im.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Domain.Domain.Service.Channels;

/// <summary>
/// 椋炰功 CardKit 瀹㈡埛绔疄鐜?
/// </summary>
[ServiceDescription(typeof(IFeishuCardKitClient), ServiceLifetime.Scoped)]
public class FeishuCardKitClient : IFeishuCardKitClient
{
    private const int CardUpdateMaxAttempts = 2;
    private readonly FeishuOptions _defaultOptions;
    private readonly ILogger<FeishuCardKitClient> _logger;
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl = "https://open.feishu.cn";
    private readonly ConcurrentDictionary<string, TokenCacheEntry> _tokenCache = new(StringComparer.Ordinal);

    public FeishuCardKitClient(
        IOptions<FeishuOptions> options,
        ILogger<FeishuCardKitClient> logger,
        IHttpClientFactory httpClientFactory)
    {
        _defaultOptions = options.Value;
        _logger = logger;
        _httpClient = httpClientFactory.CreateClient("FeishuClient");
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
    }

    public async Task<string> CreateCardAsync(
        string initialContent,
        string? title = null,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        return await CreateCardCoreAsync(
            initialContent,
            title ?? effectiveOptions.DefaultCardTitle,
            cancellationToken,
            effectiveOptions,
            chrome: null);
    }

    public async Task<bool> UpdateCardAsync(
        string cardId,
        string content,
        int sequence,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        return await UpdateCardCoreAsync(
            cardId,
            content,
            sequence,
            title: null,
            cancellationToken,
            effectiveOptions,
            chrome: null);
    }

    public async Task<string> SendCardMessageAsync(
        string chatId,
        string cardId,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);

        var payload = new
        {
            receive_id = chatId,
            msg_type = "interactive",
            content = JsonSerializer.Serialize(new
            {
                type = "card",
                data = new
                {
                    card_id = cardId
                }
            })
        };

        var response = await PostAsync(
            "/open-apis/im/v1/messages?receive_id_type=chat_id",
            token,
            payload,
            effectiveOptions,
            cancellationToken);

        var result = await ParseResponseAsync(response, cancellationToken);
        EnsureBusinessSuccess(result, "Send Feishu card message");
        return ExtractMessageId(result, "send card message");
    }

    public async Task<string> SendTextMessageAsync(
        string chatId,
        string content,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);

        var payload = new
        {
            receive_id = chatId,
            msg_type = "text",
            content = JsonSerializer.Serialize(new
            {
                text = content
            })
        };

        var response = await PostAsync(
            "/open-apis/im/v1/messages?receive_id_type=chat_id",
            token,
            payload,
            effectiveOptions,
            cancellationToken);

        var result = await ParseResponseAsync(response, cancellationToken);
        EnsureBusinessSuccess(result, "Send Feishu text message");
        return ExtractMessageId(result, "send text message");
    }

    public async Task<string> UploadAudioFileAsync(
        string filePath,
        int durationMs,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Audio file path is required.", nameof(filePath));
        }

        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);

        using var fileStream = File.OpenRead(filePath);
        using var payload = new MultipartFormDataContent
        {
            { new StringContent("opus"), "file_type" },
            { new StringContent(Path.GetFileName(filePath)), "file_name" },
            { new StringContent(durationMs.ToString()), "duration" }
        };
        payload.Add(new StreamContent(fileStream), "file", Path.GetFileName(filePath));

        var response = await PostMultipartAsync(
            "/open-apis/im/v1/files",
            token,
            payload,
            effectiveOptions,
            cancellationToken);

        var result = await ParseResponseAsync(response, cancellationToken);
        EnsureBusinessSuccess(result, "Upload Feishu audio file");

        if (result.TryGetProperty("data", out var data) &&
            data.TryGetProperty("file_key", out var fileKeyProp))
        {
            return fileKeyProp.GetString() ?? string.Empty;
        }

        throw new InvalidOperationException("Failed to upload audio file: invalid response");
    }

    public async Task<string> SendAudioMessageAsync(
        string chatId,
        string fileKey,
        int durationMs,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);

        var payload = new
        {
            receive_id = chatId,
            msg_type = "audio",
            content = JsonSerializer.Serialize(new
            {
                file_key = fileKey
            })
        };

        var response = await PostAsync(
            "/open-apis/im/v1/messages?receive_id_type=chat_id",
            token,
            payload,
            effectiveOptions,
            cancellationToken);

        var result = await ParseResponseAsync(response, cancellationToken);
        EnsureBusinessSuccess(result, "Send Feishu audio message");
        return ExtractMessageId(result, "send audio message");
    }

    public async Task<string> ReplyCardMessageAsync(
        string replyMessageId,
        string cardId,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        _logger.LogInformation("馃摛 [FeishuCardKit] ReplyCardMessageAsync: ReplyMessageId={ReplyMessageId}, CardId={CardId}",
            replyMessageId, cardId);

        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);

        var payload = new
        {
            msg_type = "interactive",
            content = JsonSerializer.Serialize(new
            {
                type = "card",
                data = new
                {
                    card_id = cardId
                }
            })
        };

        _logger.LogInformation("馃摛 [FeishuCardKit] 鍙戦€?POST 璇锋眰鍒?/open-apis/im/v1/messages/{ReplyMessageId}/reply", replyMessageId);
        var response = await PostAsync(
            $"/open-apis/im/v1/messages/{replyMessageId}/reply",
            token,
            payload,
            effectiveOptions,
            cancellationToken);

        _logger.LogInformation("馃摛 [FeishuCardKit] 鍝嶅簲鐘舵€佺爜: {StatusCode}", response.StatusCode);
        var result = await ParseResponseAsync(response, cancellationToken);
        EnsureBusinessSuccess(result, "Reply Feishu card message");
        _logger.LogDebug("馃摛 [FeishuCardKit] 鍝嶅簲鍐呭: {Response}", result);
        var messageId = ExtractMessageId(result, "reply card message");
        _logger.LogInformation("鉁?[FeishuCardKit] 鍥炲鎴愬姛, MessageId={MessageId}", messageId);
        return messageId;
    }

    public async Task<string> ReplyTextMessageAsync(
        string replyMessageId,
        string content,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);

        var payload = new
        {
            msg_type = "text",
            content = JsonSerializer.Serialize(new
            {
                text = content
            })
        };

        var response = await PostAsync(
            $"/open-apis/im/v1/messages/{replyMessageId}/reply",
            token,
            payload,
            effectiveOptions,
            cancellationToken);

        var result = await ParseResponseAsync(response, cancellationToken);
        EnsureBusinessSuccess(result, "Reply Feishu text message");
        return ExtractMessageId(result, "reply text message");
    }

    public async Task<(byte[] Content, string FileName, string MimeType)> DownloadMessageResourceAsync(
        string messageId,
        string fileKey,
        string resourceType,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(messageId))
        {
            throw new ArgumentException("Message id is required.", nameof(messageId));
        }

        if (string.IsNullOrWhiteSpace(fileKey))
        {
            throw new ArgumentException("File key is required.", nameof(fileKey));
        }

        if (string.IsNullOrWhiteSpace(resourceType))
        {
            throw new ArgumentException("Resource type is required.", nameof(resourceType));
        }

        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);
        var encodedMessageId = Uri.EscapeDataString(messageId);
        var encodedFileKey = Uri.EscapeDataString(fileKey);
        var encodedType = Uri.EscapeDataString(resourceType);

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_baseUrl}/open-apis/im/v1/messages/{encodedMessageId}/resources/{encodedFileKey}?type={encodedType}");
        request.Headers.Add("Authorization", $"Bearer {token}");

        var response = await SendAsync(request, effectiveOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "Download Feishu message resource failed: Status={Status}, MessageId={MessageId}, FileKey={FileKey}, Type={Type}, Body={Body}",
                response.StatusCode,
                messageId,
                fileKey,
                resourceType,
                body);
            throw new HttpRequestException($"Download Feishu message resource failed: {response.StatusCode}");
        }

        var content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var mimeType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var fileName = TryResolveDownloadFileName(response, fileKey, resourceType, mimeType);
        return (content, fileName, mimeType);
    }

    public async Task<FeishuDownloadedAttachment> DownloadIncomingAttachmentAsync(
        FeishuIncomingAttachment attachment,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        ArgumentException.ThrowIfNullOrWhiteSpace(attachment.AttachmentKey);

        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);
        var path = string.Equals(attachment.MessageType, "image", StringComparison.OrdinalIgnoreCase)
            ? $"/open-apis/im/v1/images/{attachment.AttachmentKey}"
            : $"/open-apis/im/v1/files/{attachment.AttachmentKey}/download";

        var response = await GetAsync(path, token, effectiveOptions, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError(
                "Download attachment failed: Status={Status}, Key={AttachmentKey}, Content={Content}",
                response.StatusCode,
                attachment.AttachmentKey,
                content);
            throw new HttpRequestException($"Download attachment failed: {response.StatusCode}");
        }

        var contentBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        var contentType = response.Content.Headers.ContentType?.MediaType;

        return new FeishuDownloadedAttachment
        {
            DisplayName = string.IsNullOrWhiteSpace(attachment.DisplayName)
                ? attachment.AttachmentKey
                : attachment.DisplayName,
            MimeType = string.IsNullOrWhiteSpace(contentType) ? attachment.MimeType : contentType,
            Content = contentBytes,
            SizeBytes = contentBytes.LongLength
        };
    }

    public async Task<FeishuStreamingHandle> CreateStreamingHandleAsync(
        string chatId,
        string? replyMessageId,
        string initialContent,
        string? title = null,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null,
        FeishuStreamingCardChrome? chrome = null)
    {
        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var cardTitle = title ?? effectiveOptions.DefaultCardTitle;

        // 1. 鍒涘缓鍗＄墖
        var cardId = await CreateCardCoreAsync(
            initialContent,
            cardTitle,
            cancellationToken,
            effectiveOptions,
            chrome);

        // 2. 鍙戦€佹垨鍥炲鍗＄墖娑堟伅
        string messageId;
        if (!string.IsNullOrEmpty(replyMessageId))
        {
            messageId = await ReplyCardMessageAsync(replyMessageId, cardId, cancellationToken, effectiveOptions);
        }
        else
        {
            messageId = await SendCardMessageAsync(chatId, cardId, cancellationToken, effectiveOptions);
        }

        // 3. 鍒涘缓娴佸紡鍙ユ焺
        var quietWindowAfterUpdateMs = ResolveQuietWindowAfterUpdateMs(chrome);
        return new FeishuStreamingHandle(
            cardId,
            messageId,
            (content, sequence) => UpdateCardCoreAsync(cardId, content, sequence, cardTitle, cancellationToken, effectiveOptions, chrome),
            (content, sequence) => UpdateCardCoreAsync(cardId, content, sequence, cardTitle, cancellationToken, effectiveOptions, chrome),
            effectiveOptions.StreamingThrottleMs,
            quietWindowAfterUpdateMs
        );
    }

    private async Task<string> CreateCardCoreAsync(
        string initialContent,
        string title,
        CancellationToken cancellationToken,
        FeishuOptions effectiveOptions,
        FeishuStreamingCardChrome? chrome)
    {
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);
        var cardData = BuildStreamingCardData(initialContent, title, chrome, includeHeader: true);

        var payload = new
        {
            type = "card_json",
            data = JsonSerializer.Serialize(cardData)
        };

        var response = await PostAsync("/open-apis/cardkit/v1/cards", token, payload, effectiveOptions, cancellationToken);
        var result = await ParseResponseAsync(response, cancellationToken);
        EnsureBusinessSuccess(result, "Create CardKit card");

        if (result.TryGetProperty("data", out var data) &&
            data.TryGetProperty("card_id", out var cardIdProp))
        {
            return cardIdProp.GetString() ?? string.Empty;
        }

        throw new InvalidOperationException("Failed to create card: invalid response");
    }

    private async Task<bool> UpdateCardCoreAsync(
        string cardId,
        string content,
        int sequence,
        string? title,
        CancellationToken cancellationToken,
        FeishuOptions effectiveOptions,
        FeishuStreamingCardChrome? chrome)
    {
        try
        {
            var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);
            var cardData = BuildStreamingCardData(content, title, chrome, includeHeader: !string.IsNullOrWhiteSpace(title));
            var updateUuid = CreateCardUpdateUuid(cardId, sequence);

            var payload = new
            {
                card = new
                {
                    type = "card_json",
                    data = JsonSerializer.Serialize(cardData)
                },
                sequence,
                uuid = updateUuid
            };

            for (var attempt = 1; attempt <= CardUpdateMaxAttempts; attempt++)
            {
                try
                {
                    var response = await PutAsync($"/open-apis/cardkit/v1/cards/{cardId}", token, payload, effectiveOptions, cancellationToken);
                    var result = await ParseResponseAsync(response, cancellationToken);
                    EnsureBusinessSuccess(result, "Update CardKit card");

                    if (result.TryGetProperty("code", out var codeProp))
                    {
                        var code = codeProp.GetInt32();
                        if (code == 0) return true;

                        _logger.LogWarning(
                            "Update card failed (cardId={CardId}, seq={Sequence}): Code={Code}, Msg={Msg}",
                            cardId, sequence, code,
                            result.TryGetProperty("msg", out var msgProp) ? msgProp.GetString() : "Unknown");
                        return false;
                    }

                    return false;
                }
                catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    if (attempt < CardUpdateMaxAttempts)
                    {
                        _logger.LogWarning(
                            ex,
                            "更新卡片超时，准备重试 (cardId={CardId}, seq={Sequence}, attempt={Attempt}/{MaxAttempts}, uuid={Uuid})",
                            cardId,
                            sequence,
                            attempt,
                            CardUpdateMaxAttempts,
                            updateUuid);
                        continue;
                    }

                    _logger.LogWarning(
                        ex,
                        "更新卡片超时，已跳过本次更新但保持流式卡片继续推送 (cardId={CardId}, seq={Sequence}, attempt={Attempt}/{MaxAttempts}, uuid={Uuid})",
                        cardId,
                        sequence,
                        attempt,
                        CardUpdateMaxAttempts,
                        updateUuid);
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update card failed (cardId={CardId}, seq={Sequence})", cardId, sequence);
            return false;
        }
    }

    private static string CreateCardUpdateUuid(string cardId, int sequence)
    {
        var input = Encoding.UTF8.GetBytes($"{cardId}:{sequence}");
        return Convert.ToHexString(SHA256.HashData(input));
    }

    private object BuildStreamingCardData(
        string content,
        string? title,
        FeishuStreamingCardChrome? chrome,
        bool includeHeader)
    {
        var config = BuildStreamingCardConfig(chrome);
        var body = new
        {
            elements = BuildStreamingCardElements(content, chrome)
        };

        if (includeHeader)
        {
            return new
            {
                schema = "2.0",
                config,
                header = new
                {
                    title = new
                    {
                        tag = "plain_text",
                        content = title ?? _defaultOptions.DefaultCardTitle
                    }
                },
                body
            };
        }

        return new
        {
            schema = "2.0",
            config,
            body
        };
    }

    private static object BuildStreamingCardConfig(FeishuStreamingCardChrome? chrome)
    {
        return ShouldEnableClientStreamingMode(chrome)
            ? new
            {
                update_multi = true,
                streaming_mode = true
            }
            : new
            {
                update_multi = true
            };
    }

    private static bool ShouldEnableClientStreamingMode(FeishuStreamingCardChrome? chrome)
    {
        // Mobile Feishu becomes unreliable when overflow actions live on cards marked
        // as client-streaming. Keep server-side updates, but downgrade the card config
        // so overflow callbacks are handled as normal interactive updates.
        return chrome?.OverflowOptions.Count is not > 0;
    }

    private static int ResolveQuietWindowAfterUpdateMs(FeishuStreamingCardChrome? chrome)
    {
        // When overflow actions are present on a still-updating card, mobile Feishu often
        // drops the click before card.action.trigger reaches the server. Leave a larger
        // post-update quiet window so users can complete the overflow tap without the
        // card re-rendering underneath them.
        return chrome?.OverflowOptions.Count is > 0 ? 4000 : 0;
    }

    internal static object[] BuildStreamingCardElements(string content, FeishuStreamingCardChrome? chrome)
    {
        if (chrome == null)
        {
            return
            [
                new
                {
                    tag = "markdown",
                    content
                }
            ];
        }

        var hasStatusSection = !string.IsNullOrWhiteSpace(chrome.StatusMarkdown) || chrome.OverflowOptions.Count > 0;
        var hasTopChipGroups = chrome.TopChipGroups.Any(group =>
            !string.IsNullOrWhiteSpace(group.SummaryMarkdown)
            || group.OverflowOptions.Count > 0
            || group.Items.Any(item => !string.IsNullOrWhiteSpace(item.Text)));
        var hasToolSummary = !string.IsNullOrWhiteSpace(chrome.LatestToolCallMarkdown);
        var hasBottomNotice = chrome.BottomNoticeMarkdowns.Any(markdown => !string.IsNullOrWhiteSpace(markdown));
        var allBottomPrompts = EnumerateBottomPrompts(chrome).ToArray();
        var hasBottomActions = chrome.BottomActions.Count > 0;
        var hasBottomPrompt = allBottomPrompts.Length > 0;
        if (!hasStatusSection && !hasTopChipGroups && !hasToolSummary && !hasBottomNotice && !hasBottomActions && !hasBottomPrompt)
        {
            return
            [
                new
                {
                    tag = "markdown",
                    content
                }
            ];
        }

        var elements = new List<object>();
        if (hasStatusSection)
        {
            elements.Add(BuildStatusModule(chrome));
        }

        if (hasTopChipGroups)
        {
            elements.Add(BuildSectionMarker("思考等级"));

            foreach (var module in FeishuStreamingTopChipLayout.BuildModules(chrome.TopChipGroups, BuildTopChipAction))
            {
                elements.Add(module);
            }
        }

        elements.Add(BuildSectionMarker("回复内容"));
        elements.Add(new
        {
            tag = "markdown",
            content
        });

        if (hasToolSummary)
        {
            elements.Add(BuildToolSummaryLine(chrome.LatestToolCallMarkdown!));
        }

        if (hasBottomNotice || hasBottomPrompt || hasBottomActions)
        {
            elements.Add(BuildSectionMarker("Superpowers 工作流"));

            foreach (var markdown in chrome.BottomNoticeMarkdowns.Where(markdown => !string.IsNullOrWhiteSpace(markdown)))
            {
                elements.Add(BuildToolSummaryLine(markdown));
            }

            foreach (var prompt in allBottomPrompts)
            {
                elements.Add(BuildBottomPromptForm(prompt));
            }

            if (hasBottomActions)
            {
                foreach (var row in BuildBottomActionRows(chrome.BottomActions))
                {
                    elements.Add(new
                    {
                        tag = "column_set",
                        flex_mode = "none",
                        horizontal_spacing = "8px",
                        columns = BuildBottomActionColumns(row)
                    });
                }
            }
        }

        return elements.ToArray();
    }

    private static object BuildToolSummaryLine(string markdown)
    {
        return new
        {
            tag = "div",
            text = new
            {
                tag = "lark_md",
                content = markdown
            }
        };
    }

    private static object BuildStatusModule(FeishuStreamingCardChrome chrome)
    {
        var statusMarkdown = string.IsNullOrWhiteSpace(chrome.StatusMarkdown)
            ? "当前会话"
            : chrome.StatusMarkdown;

        if (chrome.OverflowOptions.Count > 0)
        {
            return new
            {
                tag = "div",
                text = new
                {
                    tag = "lark_md",
                    content = statusMarkdown
                },
                extra = new
                {
                    tag = "overflow",
                    options = BuildOverflowOptions(chrome.OverflowOptions)
                }
            };
        }

        return new
        {
            tag = "div",
            text = new
            {
                tag = "lark_md",
                content = statusMarkdown
            }
        };
    }

    private static object BuildSectionMarker(string title)
    {
        return new
        {
            tag = "div",
            text = new
            {
                tag = "lark_md",
                content = $"🟥🟥🟥 **{title}**"
            }
        };
    }

    private static object BuildBottomPromptForm(FeishuStreamingCardBottomPrompt prompt)
    {
        return new
        {
            tag = "form",
            name = string.IsNullOrWhiteSpace(prompt.FormName) ? "low_interruption_continue_form" : prompt.FormName,
            elements = new object[]
            {
                new
                {
                    tag = "column_set",
                    flex_mode = "none",
                    horizontal_spacing = "8px",
                    columns = new object[]
                    {
                        new
                        {
                            tag = "column",
                            width = "weighted",
                            weight = 5,
                            vertical_align = "top",
                            elements = new object[]
                            {
                                new
                                {
                                    tag = "input",
                                    input_type = "text",
                                    name = prompt.InputName,
                                    label = new { tag = "plain_text", content = prompt.InputLabel },
                                    placeholder = new { tag = "plain_text", content = prompt.Placeholder },
                                    default_value = prompt.DefaultValue
                                }
                            }
                        },
                        new
                        {
                            tag = "column",
                            width = "auto",
                            vertical_align = "bottom",
                            elements = new object[]
                            {
                                new
                                {
                                    tag = "button",
                                    text = new { tag = "plain_text", content = prompt.ButtonText },
                                    type = string.IsNullOrWhiteSpace(prompt.ButtonType) ? "primary" : prompt.ButtonType,
                                    action_type = "form_submit",
                                    name = BuildBottomPromptSubmitButtonName(prompt),
                                    value = prompt.Value
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    private static IEnumerable<FeishuStreamingCardBottomPrompt> EnumerateBottomPrompts(FeishuStreamingCardChrome chrome)
    {
        if (chrome.BottomPrompt != null)
        {
            yield return chrome.BottomPrompt;
        }

        foreach (var prompt in chrome.AdditionalBottomPrompts)
        {
            if (prompt != null)
            {
                yield return prompt;
            }
        }
    }

    private static string BuildBottomPromptSubmitButtonName(FeishuStreamingCardBottomPrompt prompt)
    {
        var source = !string.IsNullOrWhiteSpace(prompt.InputName)
            ? prompt.InputName
            : !string.IsNullOrWhiteSpace(prompt.FormName)
                ? prompt.FormName
                : "bottom_prompt";

        Span<char> buffer = stackalloc char[source.Length];
        var index = 0;
        foreach (var ch in source)
        {
            buffer[index++] = char.IsLetterOrDigit(ch) ? ch : '_';
        }

        var normalized = new string(buffer[..index]).Trim('_');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = "bottom_prompt";
        }

        return $"{normalized}_submit";
    }

    private static object[] BuildOverflowOptions(IEnumerable<FeishuStreamingCardOverflowOption> options)
    {
        return options
            .Where(option => !string.IsNullOrWhiteSpace(option.Text))
            .Select(option => (object)new
            {
                text = new
                {
                    tag = "plain_text",
                    content = option.Text
                },
                value = JsonSerializer.Serialize(option.Value)
            })
            .ToArray();
    }

    private static object BuildTopChipAction(FeishuStreamingCardTopChipItem item)
    {
        return FeishuStreamingTopChipLayout.BuildButton(item);
    }

    private static IReadOnlyList<List<FeishuStreamingCardBottomAction>> BuildBottomActionRows(
        IEnumerable<FeishuStreamingCardBottomAction> actions)
    {
        var rows = new List<List<FeishuStreamingCardBottomAction>>();
        var rowIndexes = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var action in actions.Where(action => !string.IsNullOrWhiteSpace(action.Text)))
        {
            var rowKey = string.IsNullOrWhiteSpace(action.RowKey)
                ? "__default__"
                : action.RowKey.Trim();

            if (!rowIndexes.TryGetValue(rowKey, out var rowIndex))
            {
                rowIndex = rows.Count;
                rowIndexes[rowKey] = rowIndex;
                rows.Add([]);
            }

            rows[rowIndex].Add(action);
        }

        return rows;
    }

    private static object[] BuildBottomActionColumns(IEnumerable<FeishuStreamingCardBottomAction> actions)
    {
        return actions
            .Where(action => !string.IsNullOrWhiteSpace(action.Text))
            .Select(action => (object)new
            {
                tag = "column",
                width = "weighted",
                weight = 1,
                elements = new object[]
                {
                    new
                    {
                        tag = "button",
                        text = new
                        {
                            tag = "plain_text",
                            content = action.Text
                        },
                        type = string.IsNullOrWhiteSpace(action.Type) ? "default" : action.Type,
                        behaviors = new[]
                        {
                            new
                            {
                                type = "callback",
                                value = action.Value
                            }
                        }
                    }
                }
            })
            .ToArray();
    }

    private string ExtractMessageId(JsonElement result, string operationName)
    {
        if (result.TryGetProperty("data", out var data) &&
            data.TryGetProperty("message_id", out var messageIdProp))
        {
            return messageIdProp.GetString() ?? string.Empty;
        }

        _logger.LogError("鉂?[FeishuCardKit] 鍝嶅簲涓病鏈?message_id, Operation={Operation}", operationName);
        throw new InvalidOperationException($"Failed to {operationName}: invalid response");
    }

    /// <summary>
    /// 鑾峰彇鎴栧埛鏂拌闂护鐗?
    /// 浣跨敤 SemaphoreSlim 瀹炵幇寮傛瀹夊叏鐨勫弻閲嶆鏌ラ攣瀹?
    /// </summary>
    private async Task<string> EnsureTokenAsync(FeishuOptions options, CancellationToken cancellationToken)
    {
        var cacheEntry = GetTokenCacheEntry(options);

        // 蹇€熻矾寰勶細token 鏈夋晥鐩存帴杩斿洖
        if (!string.IsNullOrEmpty(cacheEntry.AccessToken) && DateTime.UtcNow < cacheEntry.TokenExpiresAt)
        {
            return cacheEntry.AccessToken;
        }

        await cacheEntry.TokenLock.WaitAsync(cancellationToken);
        try
        {
            // 鍙岄噸妫€鏌?
            if (!string.IsNullOrEmpty(cacheEntry.AccessToken) && DateTime.UtcNow < cacheEntry.TokenExpiresAt)
            {
                return cacheEntry.AccessToken;
            }

            var payload = new
            {
                app_id = options.AppId,
                app_secret = options.AppSecret
            };

            HttpResponseMessage response;
            try
            {
                response = await PostAsync(
                    "/open-apis/auth/v3/tenant_access_token/internal",
                    string.Empty,
                    payload,
                    options,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to refresh access token for AppId={AppId}", options.AppId);
                // 鍥為€€锛氫娇鐢ㄤ笂娆℃湁鏁堢殑 token锛堝鏋滆繕鏈夋晥锛?
                if (!string.IsNullOrEmpty(cacheEntry.LastValidToken))
                {
                    _logger.LogWarning("Using fallback token due to refresh failure for AppId={AppId}", options.AppId);
                    return cacheEntry.LastValidToken;
                }
                throw;
            }

            var result = await ParseResponseAsync(response, cancellationToken);
            EnsureBusinessSuccess(result, "Refresh Feishu tenant token");

            if (result.TryGetProperty("tenant_access_token", out var tokenProp) &&
                result.TryGetProperty("expire", out var expireProp))
            {
                var newToken = tokenProp.GetString() ?? string.Empty;
                var expireSeconds = expireProp.GetInt32();

                cacheEntry.AccessToken = newToken;
                cacheEntry.TokenExpiresAt = DateTime.UtcNow.AddSeconds(expireSeconds - 60);
                cacheEntry.LastValidToken = newToken;

                _logger.LogDebug("Access token refreshed for AppId={AppId}, expires at {ExpiresAt}", options.AppId, cacheEntry.TokenExpiresAt);
                return cacheEntry.AccessToken;
            }

            // 瑙ｆ瀽澶辫触浣嗗彲鑳借繕鏈夋棫 token 鍙敤
            if (!string.IsNullOrEmpty(cacheEntry.LastValidToken))
            {
                _logger.LogWarning("Token parse failed, using fallback token for AppId={AppId}", options.AppId);
                return cacheEntry.LastValidToken;
            }

            throw new InvalidOperationException("Failed to get access token: invalid response");
        }
        finally
        {
            cacheEntry.TokenLock.Release();
        }
    }

    private async Task<HttpResponseMessage> PostAsync(
        string path,
        string token,
        object payload,
        FeishuOptions options,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}{path}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Add("Authorization", $"Bearer {token}");
        }

        return await SendAsync(request, options, cancellationToken);
    }

    private async Task<HttpResponseMessage> GetAsync(
        string path,
        string token,
        FeishuOptions options,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}{path}");
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Add("Authorization", $"Bearer {token}");
        }

        return await SendAsync(request, options, cancellationToken);
    }

    private async Task<HttpResponseMessage> PutAsync(
        string path,
        string token,
        object payload,
        FeishuOptions options,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"{_baseUrl}{path}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Add("Authorization", $"Bearer {token}");
        }

        return await SendAsync(request, options, cancellationToken);
    }

    private async Task<HttpResponseMessage> PostMultipartAsync(
        string path,
        string token,
        HttpContent payload,
        FeishuOptions options,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}{path}")
        {
            Content = payload
        };

        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Add("Authorization", $"Bearer {token}");
        }

        return await SendAsync(request, options, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        FeishuOptions options,
        CancellationToken cancellationToken)
    {
        if (options.HttpTimeoutSeconds <= 0)
        {
            return await _httpClient.SendAsync(request, cancellationToken);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(options.HttpTimeoutSeconds));
        return await _httpClient.SendAsync(request, timeoutCts.Token);
    }

    private static string TryResolveDownloadFileName(
        HttpResponseMessage response,
        string fileKey,
        string resourceType,
        string mimeType)
    {
        var contentDisposition = response.Content.Headers.ContentDisposition;
        var fileName = contentDisposition?.FileNameStar ?? contentDisposition?.FileName;
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            return fileName.Trim('"');
        }

        var extension = GuessFileExtension(resourceType, mimeType);
        return $"{resourceType}-{fileKey}{extension}";
    }

    private static string GuessFileExtension(string resourceType, string mimeType)
    {
        if (mimeType.Contains("png", StringComparison.OrdinalIgnoreCase))
        {
            return ".png";
        }

        if (mimeType.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ||
            mimeType.Contains("jpg", StringComparison.OrdinalIgnoreCase))
        {
            return ".jpg";
        }

        if (mimeType.Contains("gif", StringComparison.OrdinalIgnoreCase))
        {
            return ".gif";
        }

        if (mimeType.Contains("webp", StringComparison.OrdinalIgnoreCase))
        {
            return ".webp";
        }

        if (mimeType.Contains("pdf", StringComparison.OrdinalIgnoreCase))
        {
            return ".pdf";
        }

        if (mimeType.Contains("plain", StringComparison.OrdinalIgnoreCase))
        {
            return ".txt";
        }

        return string.Equals(resourceType, "image", StringComparison.OrdinalIgnoreCase)
            ? ".png"
            : string.Empty;
    }

    private async Task<JsonElement> ParseResponseAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var content = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "API request failed: Status={Status}, Content={Content}",
                response.StatusCode,
                content);
            throw new HttpRequestException($"API request failed: {response.StatusCode}");
        }

        return JsonDocument.Parse(content).RootElement;
    }

    private void EnsureBusinessSuccess(JsonElement result, string operationName)
    {
        if (!result.TryGetProperty("code", out var codeProp))
        {
            return;
        }

        var code = codeProp.GetInt32();
        if (code == 0)
        {
            return;
        }

        var message = result.TryGetProperty("msg", out var msgProp)
            ? msgProp.GetString()
            : "Unknown error";

        throw new InvalidOperationException($"{operationName} failed: {message} (code: {code})");
    }

    private FeishuOptions GetEffectiveOptions(FeishuOptions? optionsOverride)
    {
        return optionsOverride ?? _defaultOptions;
    }

    private TokenCacheEntry GetTokenCacheEntry(FeishuOptions options)
    {
        var cacheKey = $"{options.AppId}\n{options.AppSecret}";
        return _tokenCache.GetOrAdd(cacheKey, _ => new TokenCacheEntry());
    }

    /// <summary>
    /// 鍙戦€佸師濮婮SON鍗＄墖娑堟伅锛堝府鍔╁姛鑳戒笓鐢級
    /// 閫氳繃 CardKit 鍒涘缓鍗＄墖锛岄伩鍏岼SON鏍煎紡闂
    /// </summary>
    public async Task<string> SendRawCardAsync(
        string chatId,
        string cardJson,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        _logger.LogInformation("馃摛 [FeishuCardKit] 閫氳繃 CardKit 鍙戦€佸崱鐗?");

        // 1. 鍏堢敤 CardKit API 鍒涘缓鍗＄墖
        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);

        var createCardPayload = new
        {
            type = "card_json",
            data = cardJson
        };

        var createResponse = await PostAsync(
            "/open-apis/cardkit/v1/cards",
            token,
            createCardPayload,
            effectiveOptions,
            cancellationToken);

        var createResult = await ParseResponseAsync(createResponse, cancellationToken);
        EnsureBusinessSuccess(createResult, "Create raw CardKit card");

        if (!createResult.TryGetProperty("data", out var createData) ||
            !createData.TryGetProperty("card_id", out var cardIdProp))
        {
            throw new InvalidOperationException("Failed to create card via CardKit");
        }

        var cardId = cardIdProp.GetString() ?? string.Empty;
        _logger.LogInformation("馃摛 [FeishuCardKit] CardKit鍒涘缓鎴愬姛: CardId={CardId}", cardId);

        // 2. 鍐嶅彂閫佸崱鐗囨秷鎭?
        return await SendCardMessageAsync(chatId, cardId, cancellationToken, effectiveOptions);
    }

    /// <summary>
    /// 鍥炲 V2 DTO 鍗＄墖娑堟伅锛堝府鍔╁姛鑳戒笓鐢級
    /// </summary>
    public Task<string> ReplyElementsCardAsync(
        string replyMessageId,
        ElementsCardV2Dto card,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        var cardJson = SerializeElementsCard(card);
        return ReplyRawCardAsync(replyMessageId, cardJson, cancellationToken, optionsOverride);
    }

    /// <summary>
    /// 鍥炲鍘熷JSON鍗＄墖娑堟伅(甯姪鍔熻兘涓撶敤)
    /// 鍙傝€?OpenCowork 瀹炵幇:鍏堝垱寤哄崱鐗囪幏鍙?card_id,鍐嶅彂閫?
    /// </summary>
    public async Task<string> ReplyRawCardAsync(
        string replyMessageId,
        string cardJson,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        _logger.LogInformation("馃摛 [FeishuCardKit] 鍥炲浜や簰寮忓崱鐗? ReplyMessageId={ReplyMessageId}", replyMessageId);
        _logger.LogDebug("馃摛 [FeishuCardKit] 鍗＄墖JSON: {CardJson}", cardJson);

        try
        {
            var effectiveOptions = GetEffectiveOptions(optionsOverride);
            var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);

            // 姝ラ1: 浣跨敤 CardKit API 鍒涘缓鍗＄墖,鑾峰彇 card_id
            _logger.LogInformation("馃摛 [FeishuCardKit] 姝ラ1: 鍒涘缓鍗＄墖...");

            var createCardPayload = new
            {
                type = "card_json",
                data = cardJson  // cardJson 鏄瓧绗︿覆
            };

            var createResponse = await PostAsync(
                "/open-apis/cardkit/v1/cards",
                token,
                createCardPayload,
                effectiveOptions,
                cancellationToken);

            var createContent = await createResponse.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogDebug("馃摛 [FeishuCardKit] 鍒涘缓鍗＄墖鍝嶅簲: {Response}", createContent);

            if (!createResponse.IsSuccessStatusCode)
            {
                _logger.LogError("鉂?[FeishuCardKit] 鍒涘缓鍗＄墖澶辫触: {Content}", createContent);
                throw new HttpRequestException($"Create card failed: {createResponse.StatusCode}");
            }

            var createResult = JsonDocument.Parse(createContent).RootElement;
            EnsureBusinessSuccess(createResult, "Create reply CardKit card");

            if (!createResult.TryGetProperty("data", out var createData) ||
                !createData.TryGetProperty("card_id", out var cardIdProp))
            {
                _logger.LogError("鉂?[FeishuCardKit] 鍝嶅簲涓病鏈?card_id");
                throw new InvalidOperationException("Failed to get card_id from response");
            }

            var cardId = cardIdProp.GetString() ?? string.Empty;
            _logger.LogInformation("馃摛 [FeishuCardKit] 姝ラ1: 鍗＄墖鍒涘缓鎴愬姛, CardId={CardId}", cardId);

            // 姝ラ2: 浣跨敤娑堟伅 API 鍥炲鍗＄墖(鍙戦€?card_id)
            _logger.LogInformation("馃摛 [FeishuCardKit] 姝ラ2: 鍥炲鍗＄墖娑堟伅...");

            var replyPayload = new
            {
                msg_type = "interactive",
                content = JsonSerializer.Serialize(new
                {
                    type = "card",
                    data = new { card_id = cardId }
                })
            };

            var replyResponse = await PostAsync(
                $"/open-apis/im/v1/messages/{replyMessageId}/reply",
                token,
                replyPayload,
                effectiveOptions,
                cancellationToken);

            var replyContent = await replyResponse.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogDebug("馃摛 [FeishuCardKit] 鍥炲娑堟伅鍝嶅簲: {Response}", replyContent);

            if (!replyResponse.IsSuccessStatusCode)
            {
                _logger.LogError("鉂?[FeishuCardKit] 鍥炲娑堟伅澶辫触: {Content}", replyContent);
                throw new HttpRequestException($"Reply message failed: {replyResponse.StatusCode}");
            }

            var replyResult = JsonDocument.Parse(replyContent).RootElement;
            EnsureBusinessSuccess(replyResult, "Reply raw Feishu card message");

            if (replyResult.TryGetProperty("data", out var data) &&
                data.TryGetProperty("message_id", out var messageIdProp))
            {
                var messageId = messageIdProp.GetString() ?? string.Empty;
                _logger.LogInformation("鉁?[FeishuCardKit] 鍗＄墖鍥炲鎴愬姛, MessageId={MessageId}", messageId);
                return messageId;
            }

            _logger.LogError("鉂?[FeishuCardKit] 鍝嶅簲涓病鏈?message_id");
            throw new InvalidOperationException("Failed to get message_id from response");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "鉂?[FeishuCardKit] ReplyRawCardAsync 澶辫触");
            throw;
        }
    }

    private sealed class TokenCacheEntry
    {
        public string AccessToken { get; set; } = string.Empty;
        public DateTime TokenExpiresAt { get; set; } = DateTime.MinValue;
        public string? LastValidToken { get; set; }
        public SemaphoreSlim TokenLock { get; } = new(1, 1);
    }

    private static string SerializeElementsCard(ElementsCardV2Dto card)
    {
        var payload = new
        {
            schema = string.IsNullOrWhiteSpace(card.Schema) ? "2.0" : card.Schema,
            config = card.Config,
            header = card.Header,
            card_link = card.CardLink,
            body = card.Body
        };

        return JsonSerializer.Serialize(payload);
    }
}

