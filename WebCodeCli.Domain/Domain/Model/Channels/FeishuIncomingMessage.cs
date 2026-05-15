namespace WebCodeCli.Domain.Domain.Model.Channels;

/// <summary>
/// 飞书收到的消息
/// </summary>
public class FeishuIncomingMessage
{
    /// <summary>
    /// 消息 ID
    /// </summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>
    /// 会话 ID
    /// </summary>
    public string ChatId { get; set; } = string.Empty;

    /// <summary>
    /// 会话类型 (p2p/group)
    /// </summary>
    public string ChatType { get; set; } = "p2p";

    /// <summary>
    /// 当前接收事件的飞书应用 ID
    /// </summary>
    public string? AppId { get; set; }

    /// <summary>
    /// 消息内容
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 飞书原始消息内容 JSON
    /// </summary>
    public string RawContent { get; set; } = string.Empty;

    /// <summary>
    /// 飞书消息类型，例如 text/image/file/post
    /// </summary>
    public string MessageType { get; set; } = "text";

    /// <summary>
    /// 解析后的附件列表
    /// </summary>
    public List<FeishuIncomingAttachment> Attachments { get; set; } = new();

    /// <summary>
    /// 发送者 ID
    /// </summary>
    public string SenderId { get; set; } = string.Empty;

    /// <summary>
    /// 发送者名称
    /// </summary>
    public string SenderName { get; set; } = string.Empty;

    /// <summary>
    /// 会话名称
    /// </summary>
    public string? ChatName { get; set; }

    /// <summary>
    /// 是否 @ 了机器人
    /// </summary>
    public bool IsBotMentioned { get; set; }

    /// <summary>
    /// 事件 ID（用于去重）
    /// </summary>
    public string EventId { get; set; } = string.Empty;
}
