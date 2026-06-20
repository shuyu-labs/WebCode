using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeishuNetSdk.Im.Dtos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Domain.Domain.Service.Channels;

/// <summary>
/// 椋炰功 CardKit 瀹㈡埛绔疄鐜?
/// </summary>
[ServiceDescription(typeof(IFeishuCardKitClient), ServiceLifetime.Scoped)]
public class FeishuCardKitClient : IFeishuCardKitClient
{
    private const int CardUpdateMaxAttempts = 2;
    private const int CardUpdateSequenceConflictCode = 300317;
    private const int CardUpdateDuplicateUuidCode = 200770;
    private const int CardOverMaxSizeCode = 200860;
    private const int CloudDocumentChildrenMaxBatchSize = 50;
    private const string ReducedContentNotice = "> 卡片已精简，前文已截断，仅显示最新内容。";
    private const int ReducedReplyTailChars = 5000;
    private const int MinimalReplyTailChars = 2400;
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
            chrome: null,
            new StreamingCardPayloadState());
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
            chrome: null,
            new StreamingCardPayloadState());
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

    public async Task<FeishuCloudDocumentInfo> CreateCloudDocumentAsync(
        string title,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null,
        string? folderToken = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Document title is required.", nameof(title));
        }

        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);
        object payload = string.IsNullOrWhiteSpace(folderToken)
            ? new
            {
                title
            }
            : new
            {
                title,
                folder_token = folderToken.Trim()
            };

        var response = await PostAsync(
            "/open-apis/docx/v1/documents",
            token,
            payload,
            effectiveOptions,
            cancellationToken);

        var result = await ParseResponseAsync(response, cancellationToken);
        EnsureBusinessSuccess(result, "Create Feishu cloud document");

        if (result.TryGetProperty("data", out var data)
            && data.TryGetProperty("document", out var document)
            && document.TryGetProperty("document_id", out var documentIdProp))
        {
            var documentId = documentIdProp.GetString() ?? string.Empty;
            var rootBlockId = document.TryGetProperty("revision_id", out _)
                && document.TryGetProperty("block_id", out var blockIdProp)
                ? blockIdProp.GetString() ?? string.Empty
                : string.Empty;

            if (string.IsNullOrWhiteSpace(rootBlockId)
                && data.TryGetProperty("document_id", out var dataDocumentIdProp))
            {
                documentId = string.IsNullOrWhiteSpace(documentId)
                    ? dataDocumentIdProp.GetString() ?? string.Empty
                    : documentId;
            }

            if (string.IsNullOrWhiteSpace(rootBlockId)
                && data.TryGetProperty("document", out var documentObject)
                && documentObject.TryGetProperty("root_block_id", out var rootBlockIdProp))
            {
                rootBlockId = rootBlockIdProp.GetString() ?? string.Empty;
            }

            if (string.IsNullOrWhiteSpace(documentId))
            {
                throw new InvalidOperationException("Failed to create Feishu cloud document: missing document_id.");
            }

            if (string.IsNullOrWhiteSpace(rootBlockId))
            {
                rootBlockId = documentId;
            }

            return new FeishuCloudDocumentInfo
            {
                DocumentId = documentId,
                RootBlockId = rootBlockId,
                Url = BuildCloudDocumentUrl(documentId)
            };
        }

        throw new InvalidOperationException("Failed to create Feishu cloud document: invalid response.");
    }

    public async Task AppendCloudDocumentTextAsync(
        string documentId,
        string blockId,
        string text,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            throw new ArgumentException("Document id is required.", nameof(documentId));
        }

        if (string.IsNullOrWhiteSpace(blockId))
        {
            throw new ArgumentException("Block id is required.", nameof(blockId));
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);
        var payload = new
        {
            children = new object[]
            {
                new
                {
                    block_type = 2,
                    text = new
                    {
                        elements = new object[]
                        {
                            new
                            {
                                text_run = new
                                {
                                    content = text,
                                    text_element_style = new { }
                                }
                            }
                        }
                    }
                }
            },
            index = 0
        };

        var response = await PostAsync(
            $"/open-apis/docx/v1/documents/{Uri.EscapeDataString(documentId)}/blocks/{Uri.EscapeDataString(blockId)}/children",
            token,
            payload,
            effectiveOptions,
            cancellationToken);

        var result = await ParseResponseAsync(response, cancellationToken);
        EnsureBusinessSuccess(result, "Append Feishu cloud document text");
    }

    public async Task SetCloudDocumentTenantReadableAsync(
        string documentId,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            throw new ArgumentException("Document id is required.", nameof(documentId));
        }

        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);
        var payload = new
        {
            external_access_entity = "open",
            security_entity = "anyone_can_view",
            comment_entity = "anyone_can_view"
        };

        var response = await PatchAsync(
            $"/open-apis/drive/v2/permissions/{Uri.EscapeDataString(documentId)}/public?type=docx",
            token,
            payload,
            effectiveOptions,
            cancellationToken);

        var result = await ParseResponseAsync(response, cancellationToken);
        EnsureBusinessSuccess(result, "Set Feishu cloud document tenant-readable permission");
    }

    public async Task GrantCloudDocumentMemberFullAccessAsync(
        string documentId,
        string openId,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            throw new ArgumentException("Document id is required.", nameof(documentId));
        }

        if (string.IsNullOrWhiteSpace(openId))
        {
            throw new ArgumentException("OpenID is required.", nameof(openId));
        }

        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);
        var payload = new
        {
            member_type = "openid",
            member_id = openId.Trim(),
            perm = "full_access",
            perm_type = "container",
            type = "user"
        };

        var response = await PostAsync(
            $"/open-apis/drive/v1/permissions/{Uri.EscapeDataString(documentId)}/members?type=docx",
            token,
            payload,
            effectiveOptions,
            cancellationToken);

        var result = await ParseResponseAsync(response, cancellationToken);
        EnsureBusinessSuccess(result, "Grant Feishu cloud document member full-access permission");
    }

    public async Task GrantCloudFolderMemberFullAccessAsync(
        string folderToken,
        string openId,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(folderToken))
        {
            throw new ArgumentException("Folder token is required.", nameof(folderToken));
        }

        if (string.IsNullOrWhiteSpace(openId))
        {
            throw new ArgumentException("OpenID is required.", nameof(openId));
        }

        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);
        var payload = new
        {
            member_type = "openid",
            member_id = openId.Trim(),
            perm = "full_access",
            perm_type = "container",
            type = "user"
        };

        var response = await PostAsync(
            $"/open-apis/drive/v1/permissions/{Uri.EscapeDataString(folderToken)}/members?type=folder",
            token,
            payload,
            effectiveOptions,
            cancellationToken);

        var result = await ParseResponseAsync(response, cancellationToken);
        EnsureBusinessSuccess(result, "Grant Feishu cloud folder member full-access permission");
    }

    public async Task<string> EnsureCloudFolderAsync(
        string folderName,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            throw new ArgumentException("Folder name is required.", nameof(folderName));
        }

        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);
        var rootFolderToken = await GetRootFolderTokenAsync(token, effectiveOptions, cancellationToken);
        var existingFolderToken = await TryFindFolderTokenByNameAsync(
            rootFolderToken,
            folderName.Trim(),
            token,
            effectiveOptions,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(existingFolderToken))
        {
            return existingFolderToken;
        }

        var createPayload = new
        {
            folder_token = rootFolderToken,
            name = folderName.Trim()
        };

        var createResponse = await PostAsync(
            "/open-apis/drive/v1/files/create_folder",
            token,
            createPayload,
            effectiveOptions,
            cancellationToken);

        var createResult = await ParseResponseAsync(createResponse, cancellationToken);
        EnsureBusinessSuccess(createResult, "Create Feishu cloud folder");

        if (createResult.TryGetProperty("data", out var createData)
            && createData.TryGetProperty("token", out var folderTokenProp)
            && !string.IsNullOrWhiteSpace(folderTokenProp.GetString()))
        {
            return folderTokenProp.GetString()!;
        }

        throw new InvalidOperationException("Failed to create Feishu cloud folder: missing token.");
    }

    public async Task MoveCloudDocumentToFolderAsync(
        string documentId,
        string folderToken,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            throw new ArgumentException("Document id is required.", nameof(documentId));
        }

        if (string.IsNullOrWhiteSpace(folderToken))
        {
            throw new ArgumentException("Folder token is required.", nameof(folderToken));
        }

        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);
        var payload = new
        {
            folder_token = folderToken.Trim(),
            type = "docx"
        };

        var response = await PostAsync(
            $"/open-apis/drive/v1/files/{Uri.EscapeDataString(documentId)}/move",
            token,
            payload,
            effectiveOptions,
            cancellationToken);

        var result = await ParseResponseAsync(response, cancellationToken);
        EnsureBusinessSuccess(result, "Move Feishu cloud document to folder");
    }

    public async Task<JsonElement> ConvertMarkdownToCloudDocumentBlocksAsync(
        string markdown,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            throw new ArgumentException("Markdown 内容不能为空。", nameof(markdown));
        }

        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);
        var payload = new
        {
            content_type = "markdown",
            content = markdown
        };

        var response = await PostAsync(
            "/open-apis/docx/v1/documents/blocks/convert",
            token,
            payload,
            effectiveOptions,
            cancellationToken);

        var result = await ParseResponseAsync(response, cancellationToken);
        EnsureBusinessSuccess(result, "Convert Feishu markdown to cloud document blocks");

        if (result.TryGetProperty("data", out var data))
        {
            return data.Clone();
        }

        throw new InvalidOperationException("Markdown 转换响应缺少 data。");
    }

    public async Task AppendCloudDocumentBlocksAsync(
        string documentId,
        string blockId,
        IReadOnlyCollection<JsonElement> blocks,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            throw new ArgumentException("文档 ID 不能为空。", nameof(documentId));
        }

        if (string.IsNullOrWhiteSpace(blockId))
        {
            throw new ArgumentException("块 ID 不能为空。", nameof(blockId));
        }

        ArgumentNullException.ThrowIfNull(blocks);

        if (blocks.Count == 0)
        {
            return;
        }

        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);
        var blockArray = blocks.ToArray();

        for (var offset = 0; offset < blockArray.Length; offset += CloudDocumentChildrenMaxBatchSize)
        {
            var payload = new
            {
                children = blockArray
                    .Skip(offset)
                    .Take(CloudDocumentChildrenMaxBatchSize)
                    .Select(NormalizeCloudDocumentBlockForAppend)
                    .ToArray(),
                index = offset
            };

            var response = await PostAsync(
                $"/open-apis/docx/v1/documents/{Uri.EscapeDataString(documentId)}/blocks/{Uri.EscapeDataString(blockId)}/children",
                token,
                payload,
                effectiveOptions,
                cancellationToken);

            var result = await ParseResponseAsync(response, cancellationToken);
            EnsureBusinessSuccess(result, "Append Feishu cloud document blocks");
        }
    }

    public async Task<IReadOnlyList<string>> ListCloudDocumentChildBlockIdsAsync(
        string documentId,
        string blockId,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            throw new ArgumentException("文档 ID 不能为空。", nameof(documentId));
        }

        if (string.IsNullOrWhiteSpace(blockId))
        {
            throw new ArgumentException("块 ID 不能为空。", nameof(blockId));
        }

        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);
        string? pageToken = null;
        var blockIds = new List<string>();

        do
        {
            var queryBuilder = new StringBuilder(
                $"/open-apis/docx/v1/documents/{Uri.EscapeDataString(documentId)}/blocks/{Uri.EscapeDataString(blockId)}/children?page_size=500");
            if (!string.IsNullOrWhiteSpace(pageToken))
            {
                queryBuilder.Append("&page_token=").Append(Uri.EscapeDataString(pageToken));
            }

            var response = await GetAsync(
                queryBuilder.ToString(),
                token,
                effectiveOptions,
                cancellationToken);

            var result = await ParseResponseAsync(response, cancellationToken);
            EnsureBusinessSuccess(result, "List Feishu cloud document child blocks");

            if (!result.TryGetProperty("data", out var data))
            {
                return blockIds;
            }

            if (data.TryGetProperty("items", out var items)
                && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var childBlockId = item.TryGetProperty("block_id", out var blockIdProp)
                        ? blockIdProp.GetString()
                        : null;

                    if (!string.IsNullOrWhiteSpace(childBlockId))
                    {
                        blockIds.Add(childBlockId);
                    }
                }
            }

            var hasMore = data.TryGetProperty("has_more", out var hasMoreProp)
                && hasMoreProp.ValueKind == JsonValueKind.True;
            pageToken = hasMore
                && data.TryGetProperty("page_token", out var nextPageTokenProp)
                ? nextPageTokenProp.GetString()
                : null;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        return blockIds;
    }

    public async Task DeleteCloudDocumentChildBlocksAsync(
        string documentId,
        string blockId,
        int startIndex,
        int endIndex,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(documentId))
        {
            throw new ArgumentException("文档 ID 不能为空。", nameof(documentId));
        }

        if (string.IsNullOrWhiteSpace(blockId))
        {
            throw new ArgumentException("块 ID 不能为空。", nameof(blockId));
        }

        if (startIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(startIndex), "起始索引不能小于 0。");
        }

        if (endIndex < startIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(endIndex), "结束索引不能小于起始索引。");
        }

        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);
        var payload = new
        {
            start_index = startIndex,
            end_index = endIndex
        };

        var response = await DeleteAsync(
            $"/open-apis/docx/v1/documents/{Uri.EscapeDataString(documentId)}/blocks/{Uri.EscapeDataString(blockId)}/children/batch_delete",
            token,
            payload,
            effectiveOptions,
            cancellationToken);

        var result = await ParseResponseAsync(response, cancellationToken);
        EnsureBusinessSuccess(result, "Delete Feishu cloud document child blocks");
    }

    public async Task<FeishuCloudDocumentInfo?> FindCloudDocumentInFolderByTitleAsync(
        string folderToken,
        string title,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(folderToken))
        {
            throw new ArgumentException("文件夹 Token 不能为空。", nameof(folderToken));
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("文档标题不能为空。", nameof(title));
        }

        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);
        string? pageToken = null;

        do
        {
            var queryBuilder = new StringBuilder("/open-apis/drive/v1/files?page_size=200");
            queryBuilder.Append("&folder_token=").Append(Uri.EscapeDataString(folderToken.Trim()));
            queryBuilder.Append("&order_by=EditedTime");
            if (!string.IsNullOrWhiteSpace(pageToken))
            {
                queryBuilder.Append("&page_token=").Append(Uri.EscapeDataString(pageToken));
            }

            var response = await GetAsync(
                queryBuilder.ToString(),
                token,
                effectiveOptions,
                cancellationToken);

            var result = await ParseResponseAsync(response, cancellationToken);
            EnsureBusinessSuccess(result, "List Feishu cloud folder items");

            if (!result.TryGetProperty("data", out var data))
            {
                return null;
            }

            if (data.TryGetProperty("files", out var files)
                && files.ValueKind == JsonValueKind.Array)
            {
                foreach (var file in files.EnumerateArray())
                {
                    var currentTitle = file.TryGetProperty("name", out var nameProp)
                        ? nameProp.GetString()
                        : null;
                    var type = file.TryGetProperty("type", out var typeProp)
                        ? typeProp.GetString()
                        : null;
                    var documentId = file.TryGetProperty("token", out var tokenProp)
                        ? tokenProp.GetString()
                        : null;

                    if (!string.Equals(currentTitle, title, StringComparison.Ordinal)
                        || !string.Equals(type, "docx", StringComparison.OrdinalIgnoreCase)
                        || string.IsNullOrWhiteSpace(documentId))
                    {
                        continue;
                    }

                    var url = file.TryGetProperty("url", out var urlProp)
                        ? urlProp.GetString()
                        : null;

                    return new FeishuCloudDocumentInfo
                    {
                        DocumentId = documentId,
                        RootBlockId = documentId,
                        Url = string.IsNullOrWhiteSpace(url) ? BuildCloudDocumentUrl(documentId) : url
                    };
                }
            }

            var hasMore = data.TryGetProperty("has_more", out var hasMoreProp)
                && hasMoreProp.ValueKind == JsonValueKind.True;
            pageToken = hasMore
                && data.TryGetProperty("next_page_token", out var nextPageTokenProp)
                ? nextPageTokenProp.GetString()
                : null;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        return null;
    }

    public async Task<string> UploadCloudFileAsync(
        string fileName,
        byte[] content,
        string? folderToken,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("文件名不能为空。", nameof(fileName));
        }

        ArgumentNullException.ThrowIfNull(content);

        if (content.Length == 0)
        {
            throw new ArgumentException("文件内容不能为空。", nameof(content));
        }

        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);
        var targetFolderToken = string.IsNullOrWhiteSpace(folderToken)
            ? await GetRootFolderTokenAsync(token, effectiveOptions, cancellationToken)
            : folderToken.Trim();
        using var formData = new MultipartFormDataContent();
        formData.Add(new StringContent(fileName), "file_name");
        formData.Add(new StringContent("explorer"), "parent_type");
        formData.Add(new StringContent(targetFolderToken), "parent_node");
        formData.Add(new StringContent(content.Length.ToString()), "size");

        var fileContent = new ByteArrayContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/markdown");
        formData.Add(fileContent, "file", fileName);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/open-apis/drive/v1/files/upload_all");
        request.Headers.Add("Authorization", $"Bearer {token}");
        request.Content = formData;

        var response = await SendAsync(request, effectiveOptions, cancellationToken);
        var result = await ParseResponseAsync(response, cancellationToken);
        EnsureBusinessSuccess(result, "Upload Feishu cloud file");

        if (result.TryGetProperty("data", out var data)
            && data.TryGetProperty("file_token", out var fileTokenProp)
            && !string.IsNullOrWhiteSpace(fileTokenProp.GetString()))
        {
            return fileTokenProp.GetString()!;
        }

        throw new InvalidOperationException("上传云空间文件响应缺少 file_token。");
    }

    public async Task<FeishuCloudDocumentInfo> ImportMarkdownFileAsCloudDocumentAsync(
        string fileName,
        byte[] content,
        string title,
        string? folderToken,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("文档标题不能为空。", nameof(title));
        }

        var fileToken = await UploadCloudFileAsync(
            fileName,
            content,
            folderToken,
            cancellationToken,
            optionsOverride);

        var extension = Path.GetExtension(fileName);
        var normalizedExtension = string.IsNullOrWhiteSpace(extension)
            ? "md"
            : extension.TrimStart('.').ToLowerInvariant();

        var effectiveOptions = GetEffectiveOptions(optionsOverride);
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);
        object payload = string.IsNullOrWhiteSpace(folderToken)
            ? new
            {
                file_extension = normalizedExtension,
                file_token = fileToken,
                type = "docx",
                file_name = title
            }
            : new
            {
                file_extension = normalizedExtension,
                file_token = fileToken,
                type = "docx",
                file_name = title,
                point = new
                {
                    mount_type = 1,
                    mount_key = folderToken.Trim()
                }
            };

        var createResponse = await PostAsync(
            "/open-apis/drive/v1/import_tasks",
            token,
            payload,
            effectiveOptions,
            cancellationToken);

        var createResult = await ParseResponseAsync(createResponse, cancellationToken);
        EnsureBusinessSuccess(createResult, "Create Feishu markdown import task");

        if (!createResult.TryGetProperty("data", out var createData)
            || !createData.TryGetProperty("ticket", out var ticketProp)
            || string.IsNullOrWhiteSpace(ticketProp.GetString()))
        {
            throw new InvalidOperationException("Markdown 导入任务响应缺少 ticket。");
        }

        var ticket = ticketProp.GetString()!;
        return await PollImportMarkdownFileAsCloudDocumentAsync(
            ticket,
            token,
            effectiveOptions,
            cancellationToken);
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
        var payloadState = new StreamingCardPayloadState();

        // 1. 鍒涘缓鍗＄墖
        var cardId = await CreateCardCoreAsync(
            initialContent,
            cardTitle,
            cancellationToken,
            effectiveOptions,
            chrome,
            payloadState);

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
            (content, sequence) => UpdateCardCoreAsync(cardId, content, sequence, cardTitle, cancellationToken, effectiveOptions, chrome, payloadState),
            (content, sequence) => UpdateCardCoreAsync(cardId, content, sequence, cardTitle, cancellationToken, effectiveOptions, chrome, payloadState),
            effectiveOptions.StreamingThrottleMs,
            quietWindowAfterUpdateMs
        );
    }

    private async Task<string> CreateCardCoreAsync(
        string initialContent,
        string title,
        CancellationToken cancellationToken,
        FeishuOptions effectiveOptions,
        FeishuStreamingCardChrome? chrome,
        StreamingCardPayloadState state)
    {
        var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);

        while (true)
        {
            var cardData = BuildStreamingCardData(
                initialContent,
                title,
                chrome,
                includeHeader: true,
                mode: state.Mode,
                maxReplyChars: state.MaxReplyChars);

            var payload = new
            {
                type = "card_json",
                data = JsonSerializer.Serialize(cardData)
            };

            var response = await PostAsync("/open-apis/cardkit/v1/cards", token, payload, effectiveOptions, cancellationToken);
            var result = await ParseResponseAsync(response, cancellationToken);

            if (IsBusinessSuccess(result))
            {
                if (result.TryGetProperty("data", out var data) &&
                    data.TryGetProperty("card_id", out var cardIdProp))
                {
                    return cardIdProp.GetString() ?? string.Empty;
                }

                throw new InvalidOperationException("Failed to create card: invalid response");
            }

            if (TryAdvanceOverflowReduction(result, state, cardId: null, sequence: null))
            {
                continue;
            }

            EnsureBusinessSuccess(result, "Create CardKit card");
            throw new InvalidOperationException("Failed to create card: invalid response");
        }
    }

    private async Task<bool> UpdateCardCoreAsync(
        string cardId,
        string content,
        int sequence,
        string? title,
        CancellationToken cancellationToken,
        FeishuOptions effectiveOptions,
        FeishuStreamingCardChrome? chrome,
        StreamingCardPayloadState state)
    {
        try
        {
            var token = await EnsureTokenAsync(effectiveOptions, cancellationToken);
            var updateUuid = CreateCardUpdateUuid(cardId, sequence);
            var sawRecoverableTimeout = false;

            for (var attempt = 1; attempt <= CardUpdateMaxAttempts; attempt++)
            {
                try
                {
                    var payload = BuildUpdatePayload(
                        content,
                        title,
                        chrome,
                        state.Mode,
                        state.MaxReplyChars,
                        sequence,
                        updateUuid);

                    var response = await PutAsync($"/open-apis/cardkit/v1/cards/{cardId}", token, payload, effectiveOptions, cancellationToken);
                    var result = await ParseResponseAsync(response, cancellationToken);

                    if (result.TryGetProperty("code", out var codeProp))
                    {
                        var code = codeProp.GetInt32();
                        if (code == 0)
                        {
                            return true;
                        }

                        // A timeout can mean Feishu applied the write but the client never saw the response.
                        // If the immediate retry for the same sequence then reports a sequence conflict,
                        // treat that as evidence the prior write likely already succeeded.
                        if ((code == CardUpdateSequenceConflictCode || code == CardUpdateDuplicateUuidCode) && sawRecoverableTimeout)
                        {
                            _logger.LogWarning(
                                "Update card retry hit duplicate-after-timeout signal; assuming previous write succeeded (cardId={CardId}, seq={Sequence}, code={Code}, uuid={Uuid})",
                                cardId,
                                sequence,
                                code,
                                updateUuid);
                            return true;
                        }

                        if (TryAdvanceOverflowReduction(result, state, cardId, sequence))
                        {
                            attempt--;
                            continue;
                        }

                        if (code == CardOverMaxSizeCode)
                        {
                            _logger.LogWarning(
                                "Update card failed because minimal reduced payload still exceeds CardKit max size (cardId={CardId}, seq={Sequence}, uuid={Uuid})",
                                cardId,
                                sequence,
                                updateUuid);
                            return false;
                        }

                        EnsureBusinessSuccess(result, "Update CardKit card");

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
                    sawRecoverableTimeout = true;
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
        bool includeHeader,
        StreamingCardPayloadMode mode = StreamingCardPayloadMode.Full,
        int? maxReplyChars = null)
    {
        var effectiveChrome = mode == StreamingCardPayloadMode.Full ? chrome : null;
        var renderedContent = RenderStreamingReplyContent(content, mode, maxReplyChars);
        var config = BuildStreamingCardConfig(effectiveChrome);
        var body = new
        {
            elements = BuildStreamingCardElements(renderedContent, effectiveChrome)
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
            elements.Add(BuildSectionMarker(SuperpowersQuickActionDefaults.WorkflowSectionTitle));

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

    private async Task<HttpResponseMessage> PatchAsync(
        string path,
        string token,
        object payload,
        FeishuOptions options,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Patch, $"{_baseUrl}{path}");
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

    private async Task<HttpResponseMessage> DeleteAsync(
        string path,
        string token,
        object payload,
        FeishuOptions options,
        CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}{path}");
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

    internal static string BuildCloudDocumentUrl(string documentId)
    {
        return $"https://feishu.cn/docx/{documentId}";
    }

    private static JsonNode NormalizeCloudDocumentBlockForAppend(JsonElement block)
    {
        return NormalizeCloudDocumentNode(block) ?? new JsonObject();
    }

    private static JsonNode? NormalizeCloudDocumentNode(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object => NormalizeCloudDocumentObject(element),
            JsonValueKind.Array => NormalizeCloudDocumentArray(element),
            JsonValueKind.String => JsonValue.Create(element.GetString()),
            JsonValueKind.Number => NormalizeCloudDocumentNumber(element),
            JsonValueKind.True => JsonValue.Create(true),
            JsonValueKind.False => JsonValue.Create(false),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => null
        };
    }

    private static JsonObject NormalizeCloudDocumentObject(JsonElement element)
    {
        var node = new JsonObject();

        foreach (var property in element.EnumerateObject())
        {
            if (ShouldSkipCloudDocumentProperty(property.Name))
            {
                continue;
            }

            if (string.Equals(property.Name, "children", StringComparison.Ordinal)
                && property.Value.ValueKind == JsonValueKind.Array
                && property.Value.GetArrayLength() == 0)
            {
                continue;
            }

            var normalized = NormalizeCloudDocumentNode(property.Value);
            if (normalized != null)
            {
                node[property.Name] = normalized;
            }
        }

        return node;
    }

    private static JsonArray NormalizeCloudDocumentArray(JsonElement element)
    {
        var array = new JsonArray();

        foreach (var item in element.EnumerateArray())
        {
            var normalized = NormalizeCloudDocumentNode(item);
            if (normalized != null)
            {
                array.Add(normalized);
            }
        }

        return array;
    }

    private static JsonNode? NormalizeCloudDocumentNumber(JsonElement element)
    {
        if (element.TryGetInt64(out var longValue))
        {
            return JsonValue.Create(longValue);
        }

        if (element.TryGetDecimal(out var decimalValue))
        {
            return JsonValue.Create(decimalValue);
        }

        return JsonValue.Create(element.GetDouble());
    }

    private static bool ShouldSkipCloudDocumentProperty(string propertyName)
    {
        return string.Equals(propertyName, "block_id", StringComparison.Ordinal)
            || string.Equals(propertyName, "block_uuid", StringComparison.Ordinal)
            || string.Equals(propertyName, "parent_id", StringComparison.Ordinal)
            || string.Equals(propertyName, "revision_id", StringComparison.Ordinal);
    }

    private async Task<string> GetRootFolderTokenAsync(
        string token,
        FeishuOptions options,
        CancellationToken cancellationToken)
    {
        var response = await GetAsync(
            "/open-apis/drive/explorer/v2/root_folder/meta",
            token,
            options,
            cancellationToken);

        var result = await ParseResponseAsync(response, cancellationToken);
        EnsureBusinessSuccess(result, "Get Feishu root folder metadata");

        if (result.TryGetProperty("data", out var data)
            && data.TryGetProperty("token", out var tokenProp)
            && !string.IsNullOrWhiteSpace(tokenProp.GetString()))
        {
            return tokenProp.GetString()!;
        }

        throw new InvalidOperationException("Failed to get Feishu root folder metadata: missing token.");
    }

    private async Task<string?> TryFindFolderTokenByNameAsync(
        string parentFolderToken,
        string folderName,
        string token,
        FeishuOptions options,
        CancellationToken cancellationToken)
    {
        string? pageToken = null;
        do
        {
            var queryBuilder = new StringBuilder("/open-apis/drive/v1/files?page_size=200");
            queryBuilder.Append("&folder_token=").Append(Uri.EscapeDataString(parentFolderToken));
            queryBuilder.Append("&order_by=EditedTime");
            if (!string.IsNullOrWhiteSpace(pageToken))
            {
                queryBuilder.Append("&page_token=").Append(Uri.EscapeDataString(pageToken));
            }

            var response = await GetAsync(
                queryBuilder.ToString(),
                token,
                options,
                cancellationToken);

            var result = await ParseResponseAsync(response, cancellationToken);
            EnsureBusinessSuccess(result, "List Feishu cloud folder items");

            if (result.TryGetProperty("data", out var data))
            {
                if (data.TryGetProperty("files", out var files)
                    && files.ValueKind == JsonValueKind.Array)
                {
                    foreach (var file in files.EnumerateArray())
                    {
                        var type = file.TryGetProperty("type", out var typeProp)
                            ? typeProp.GetString()
                            : null;
                        var name = file.TryGetProperty("name", out var nameProp)
                            ? nameProp.GetString()
                            : null;
                        var currentToken = file.TryGetProperty("token", out var fileTokenProp)
                            ? fileTokenProp.GetString()
                            : null;

                        if (string.Equals(type, "folder", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(name, folderName, StringComparison.Ordinal)
                            && !string.IsNullOrWhiteSpace(currentToken))
                        {
                            return currentToken;
                        }
                    }
                }

                var hasMore = data.TryGetProperty("has_more", out var hasMoreProp)
                    && hasMoreProp.ValueKind == JsonValueKind.True;
                pageToken = hasMore
                    && data.TryGetProperty("next_page_token", out var nextPageTokenProp)
                    ? nextPageTokenProp.GetString()
                    : null;
            }
            else
            {
                pageToken = null;
            }
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        return null;
    }

    private async Task<FeishuCloudDocumentInfo> PollImportMarkdownFileAsCloudDocumentAsync(
        string ticket,
        string token,
        FeishuOptions options,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);

        while (true)
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Markdown 导入超时，请稍后重试。");
            }

            var response = await GetAsync(
                $"/open-apis/drive/v1/import_tasks/{Uri.EscapeDataString(ticket)}",
                token,
                options,
                cancellationToken);

            var result = await ParseResponseAsync(response, cancellationToken);
            EnsureBusinessSuccess(result, "Get Feishu markdown import task");

            if (!result.TryGetProperty("data", out var data)
                || !data.TryGetProperty("result", out var importResult))
            {
                throw new InvalidOperationException("Markdown 导入结果响应缺少 result。");
            }

            var jobStatus = importResult.TryGetProperty("job_status", out var jobStatusProp)
                ? jobStatusProp.GetInt32()
                : -1;

            if (jobStatus == 0)
            {
                var documentId = importResult.TryGetProperty("token", out var tokenProp)
                    ? tokenProp.GetString()
                    : null;
                var url = importResult.TryGetProperty("url", out var urlProp)
                    ? urlProp.GetString()
                    : null;

                if (string.IsNullOrWhiteSpace(documentId))
                {
                    throw new InvalidOperationException("Markdown 导入成功但缺少文档 token。");
                }

                return new FeishuCloudDocumentInfo
                {
                    DocumentId = documentId,
                    RootBlockId = documentId,
                    Url = string.IsNullOrWhiteSpace(url) ? BuildCloudDocumentUrl(documentId) : url
                };
            }

            if (jobStatus == 1 || jobStatus == 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
                continue;
            }

            var message = importResult.TryGetProperty("job_error_msg", out var errorProp)
                ? errorProp.GetString()
                : null;
            throw new InvalidOperationException($"Markdown 导入失败：{message ?? $"任务状态 {jobStatus}"}");
        }
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
            throw new HttpRequestException($"API request failed: {response.StatusCode}, Content={content}");
        }

        return JsonDocument.Parse(content).RootElement;
    }

    private object BuildUpdatePayload(
        string content,
        string? title,
        FeishuStreamingCardChrome? chrome,
        StreamingCardPayloadMode mode,
        int? maxReplyChars,
        int sequence,
        string updateUuid)
    {
        var cardData = BuildStreamingCardData(
            content,
            title,
            chrome,
            includeHeader: !string.IsNullOrWhiteSpace(title),
            mode,
            maxReplyChars);

        return new
        {
            card = new
            {
                type = "card_json",
                data = JsonSerializer.Serialize(cardData)
            },
            sequence,
            uuid = updateUuid
        };
    }

    private bool TryAdvanceOverflowReduction(
        JsonElement result,
        StreamingCardPayloadState state,
        string? cardId,
        int? sequence)
    {
        if (!TryGetBusinessCode(result, out var code) || code != CardOverMaxSizeCode)
        {
            return false;
        }

        if (!state.TryAdvance())
        {
            if (cardId != null || sequence != null)
            {
                _logger.LogWarning(
                    "Feishu CardKit payload still exceeds max size after minimal reduction; stopping card updates (cardId={CardId}, seq={Sequence})",
                    cardId ?? "<create>",
                    sequence?.ToString() ?? "<create>");
            }
            return false;
        }

        _logger.LogWarning(
            "Feishu CardKit payload exceeded max size; retrying with reduced payload (cardId={CardId}, seq={Sequence}, mode={Mode}, maxReplyChars={MaxReplyChars})",
            cardId ?? "<create>",
            sequence?.ToString() ?? "<create>",
            state.Mode,
            state.MaxReplyChars?.ToString() ?? "<none>");
        return true;
    }

    private static bool IsBusinessSuccess(JsonElement result)
    {
        return !TryGetBusinessCode(result, out var code) || code == 0;
    }

    private static bool TryGetBusinessCode(JsonElement result, out int code)
    {
        if (result.TryGetProperty("code", out var codeProp))
        {
            code = codeProp.GetInt32();
            return true;
        }

        code = 0;
        return false;
    }

    private static string RenderStreamingReplyContent(string content, StreamingCardPayloadMode mode, int? maxReplyChars)
    {
        if (mode == StreamingCardPayloadMode.Full)
        {
            return content;
        }

        var effectiveLimit = maxReplyChars.GetValueOrDefault(mode == StreamingCardPayloadMode.Reduced ? ReducedReplyTailChars : MinimalReplyTailChars);
        var trimmedContent = TakeContentTail(content, effectiveLimit);
        return $"{ReducedContentNotice}\n\n{trimmedContent}";
    }

    private static string TakeContentTail(string content, int maxChars)
    {
        if (string.IsNullOrEmpty(content))
        {
            return string.Empty;
        }

        if (content.Length <= maxChars)
        {
            return content;
        }

        var tail = content[^maxChars..].TrimStart();
        if (tail.StartsWith("```", StringComparison.Ordinal))
        {
            return tail;
        }

        var newlineIndex = tail.IndexOf('\n');
        if (newlineIndex >= 0 && newlineIndex < tail.Length - 1)
        {
            return tail[(newlineIndex + 1)..].TrimStart();
        }

        return tail;
    }

    private void EnsureBusinessSuccess(JsonElement result, string operationName)
    {
        if (!TryGetBusinessCode(result, out var code))
        {
            return;
        }

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

    private sealed class StreamingCardPayloadState
    {
        public StreamingCardPayloadMode Mode { get; private set; } = StreamingCardPayloadMode.Full;

        public int? MaxReplyChars { get; private set; }

        public bool TryAdvance()
        {
            if (Mode == StreamingCardPayloadMode.Full)
            {
                Mode = StreamingCardPayloadMode.Reduced;
                MaxReplyChars = ReducedReplyTailChars;
                return true;
            }

            if (Mode == StreamingCardPayloadMode.Reduced)
            {
                Mode = StreamingCardPayloadMode.Minimal;
                MaxReplyChars = MinimalReplyTailChars;
                return true;
            }

            return false;
        }
    }

    private enum StreamingCardPayloadMode
    {
        Full = 0,
        Reduced = 1,
        Minimal = 2
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

