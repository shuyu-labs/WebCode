using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model.Channels;
using FeishuNetSdk.Im.Dtos;

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

    /// <summary>
    /// 创建流式回复句柄
    /// </summary>
    /// <param name="chatId">会话 ID</param>
    /// <param name="replyMessageId">被回复的消息 ID（可选，为空时直接发送）</param>
    /// <param name="initialContent">初始内容</param>
    /// <param name="title">卡片标题</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>流式回复句柄</returns>
    Task<string> UploadAudioFileAsync(
        string filePath,
        int durationMs,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        throw new NotSupportedException();
    }

    Task<string> SendAudioMessageAsync(
        string chatId,
        string fileKey,
        int durationMs,
        CancellationToken cancellationToken = default,
        FeishuOptions? optionsOverride = null)
    {
        throw new NotSupportedException();
    }

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
}
