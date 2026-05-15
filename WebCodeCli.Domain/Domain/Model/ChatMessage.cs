namespace WebCodeCli.Domain.Domain.Model;

/// <summary>
/// 聊天消息
/// </summary>
public class ChatMessage
{
    /// <summary>
    /// 消息ID
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// <summary>
    /// 消息角色：user 或 assistant
    /// </summary>
    public string Role { get; set; } = string.Empty;

    /// <summary>
    /// 消息内容
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 娑堟伅闄勪欢
    /// </summary>
    public List<MessageAttachment> Attachments { get; set; } = new();

    /// <summary>
    /// 使用的 CLI 工具ID
    /// </summary>
    public string? CliToolId { get; set; }

    /// <summary>
    /// 创建时间
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 是否完成
    /// </summary>
    public bool IsCompleted { get; set; } = false;

    /// <summary>
    /// 是否出错
    /// </summary>
    public bool HasError { get; set; } = false;

    /// <summary>
    /// 错误信息
    /// </summary>
    public string? ErrorMessage { get; set; }

    /// <summary>
    /// Codex Session ID（用于resume）
    /// </summary>
    public string? CodexSessionId { get; set; }
}

