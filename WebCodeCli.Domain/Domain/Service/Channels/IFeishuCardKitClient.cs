using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model.Channels;
using FeishuNetSdk.Im.Dtos;
using System.Text.Json;

namespace WebCodeCli.Domain.Domain.Service.Channels;

/// <summary>
/// 飞书 CardKit 客户端接口
/// </summary>
public interface IFeishuCardKitClient
{
    /// <summary>
    /// 创建流式卡片
    /// </summary>
    /// <param name="initialContent">初始内容</param>
    /// <param name="title">卡片标题</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>卡片 ID</returns>
    Task<string> CreateCardAsync(
        string initialContent,
        string? title = null,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null);

    /// <summary>
    /// 更新流式卡片
    /// </summary>
    /// <param name="cardId">卡片 ID</param>
    /// <param name="content">新内容</param>
    /// <param name="sequence">序列号（必须递增）</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>更新是否成功</returns>
    Task<bool> UpdateCardAsync(
        string cardId,
        string content,
        int sequence,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null);

    /// <summary>
    /// 发送卡片消息
    /// </summary>
    /// <param name="chatId">会话 ID</param>
    /// <param name="cardId">卡片 ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>消息 ID</returns>
    Task<string> SendCardMessageAsync(
        string chatId,
        string cardId,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null);

    /// <summary>
    /// 发送文本消息
    /// </summary>
    /// <param name="chatId">会话 ID</param>
    /// <param name="content">文本内容</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>消息 ID</returns>
    Task<string> SendTextMessageAsync(
        string chatId,
        string content,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null);

    /// <summary>
    /// 回复卡片消息
    /// </summary>
    /// <param name="replyMessageId">被回复的消息 ID</param>
    /// <param name="cardId">卡片 ID</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>回复消息 ID</returns>
    Task<string> ReplyCardMessageAsync(
        string replyMessageId,
        string cardId,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null);

    /// <summary>
    /// 回复文本消息
    /// </summary>
    /// <param name="replyMessageId">被回复的消息 ID</param>
    /// <param name="content">文本内容</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>回复消息 ID</returns>
    Task<string> ReplyTextMessageAsync(
        string replyMessageId,
        string content,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null);

    Task<FeishuCloudDocumentInfo> CreateCloudDocumentAsync(
        string title,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null,
        string? folderToken = null)
    {
        throw new NotSupportedException();
    }

    Task AppendCloudDocumentTextAsync(
        string documentId,
        string blockId,
        string text,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        throw new NotSupportedException();
    }

    Task SetCloudDocumentTenantReadableAsync(
        string documentId,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        throw new NotSupportedException();
    }

    Task GrantCloudDocumentMemberFullAccessAsync(
        string documentId,
        string openId,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        throw new NotSupportedException();
    }

    Task GrantCloudFolderMemberFullAccessAsync(
        string folderToken,
        string openId,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        throw new NotSupportedException();
    }

    Task<string> EnsureCloudFolderAsync(
        string folderName,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        throw new NotSupportedException();
    }

    Task MoveCloudDocumentToFolderAsync(
        string documentId,
        string folderToken,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        throw new NotSupportedException();
    }

    Task<JsonElement> ConvertMarkdownToCloudDocumentBlocksAsync(
        string markdown,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        throw new NotSupportedException();
    }

    Task AppendCloudDocumentBlocksAsync(
        string documentId,
        string blockId,
        IReadOnlyCollection<JsonElement> blocks,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        throw new NotSupportedException();
    }

    Task<IReadOnlyList<string>> ListCloudDocumentChildBlockIdsAsync(
        string documentId,
        string blockId,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        throw new NotSupportedException();
    }

    Task DeleteCloudDocumentChildBlocksAsync(
        string documentId,
        string blockId,
        int startIndex,
        int endIndex,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        throw new NotSupportedException();
    }

    Task<FeishuCloudDocumentInfo?> FindCloudDocumentInFolderByTitleAsync(
        string folderToken,
        string title,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        throw new NotSupportedException();
    }

    Task<string> UploadCloudFileAsync(
        string fileName,
        byte[] content,
        string? folderToken,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        throw new NotSupportedException();
    }

    Task<FeishuCloudDocumentInfo> ImportMarkdownFileAsCloudDocumentAsync(
        string fileName,
        byte[] content,
        string title,
        string? folderToken,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        throw new NotSupportedException();
    }

    Task<FeishuDownloadedAttachment> DownloadIncomingAttachmentAsync(
        FeishuIncomingAttachment attachment,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        throw new NotSupportedException();
    }

    /// <summary>
    /// 创建流式回复句柄
    /// </summary>
    /// <param name="chatId">会话 ID</param>
    /// <param name="replyMessageId">被回复的消息 ID（可选，为空时直接发送）</param>
    /// <param name="initialContent">初始内容</param>
    /// <param name="title">卡片标题</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>流式回复句柄</returns>
    Task<FeishuStreamingHandle> CreateStreamingHandleAsync(
        string chatId,
        string? replyMessageId,
        string initialContent,
        string? title = null,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null,
        FeishuStreamingCardChrome? chrome = null);

    /// <summary>
    /// 发送原始JSON卡片消息（帮助功能专用）
    /// </summary>
    /// <param name="chatId">会话 ID</param>
    /// <param name="cardJson">卡片JSON字符串</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>消息 ID</returns>
    Task<string> SendRawCardAsync(
        string chatId,
        string cardJson,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null);

    /// <summary>
    /// 回复 V2 DTO 卡片消息（帮助功能专用）
    /// </summary>
    /// <param name="replyMessageId">被回复的消息 ID</param>
    /// <param name="card">V2 卡片 DTO</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>回复消息 ID</returns>
    Task<string> ReplyElementsCardAsync(
        string replyMessageId,
        ElementsCardV2Dto card,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null);

    /// <summary>
    /// 回复原始JSON卡片消息（帮助功能专用）
    /// </summary>
    /// <param name="replyMessageId">被回复的消息 ID</param>
    /// <param name="cardJson">卡片JSON字符串</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>回复消息 ID</returns>
    Task<string> ReplyRawCardAsync(
        string replyMessageId,
        string cardJson,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null);

    /// <summary>
    /// 下载入站消息中携带的图片或文件资源。
    /// </summary>
    /// <param name="messageId">飞书消息 ID</param>
    /// <param name="fileKey">资源 file_key / image_key</param>
    /// <param name="resourceType">资源类型，通常为 image 或 file</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <param name="optionsOverride">可选的机器人配置覆盖</param>
    /// <returns>资源内容、文件名和 MIME 类型</returns>
    Task<(byte[] Content, string FileName, string MimeType)> DownloadMessageResourceAsync(
        string messageId,
        string fileKey,
        string resourceType,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null);
}

public class FeishuDownloadedAttachment
{
    public string DisplayName { get; set; } = string.Empty;

    public string MimeType { get; set; } = "application/octet-stream";

    public byte[] Content { get; set; } = [];

    public long SizeBytes { get; set; }
}
