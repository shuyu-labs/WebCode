using SqlSugar;

namespace WebCodeCli.Domain.Repositories.Base.ChatSession;

/// <summary>
/// 聊天消息实体
/// </summary>
[SugarTable("ChatMessage")]
public class ChatMessageEntity
{
    /// <summary>
    /// 主键ID（自增）
    /// </summary>
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }
    
    /// <summary>
    /// 关联的会话ID
    /// </summary>
    [SugarColumn(Length = 64, IsNullable = false)]
    public string MessageId { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// 閫昏緫娑堟伅ID
    /// </summary>
    [SugarColumn(Length = 64, IsNullable = false)]
    public string SessionId { get; set; } = string.Empty;
    
    /// <summary>
    /// 用户名（多用户支持，冗余存储便于查询）
    /// </summary>
    [SugarColumn(Length = 128, IsNullable = false)]
    public string Username { get; set; } = "default";
    
    /// <summary>
    /// 消息角色（user/assistant/system）
    /// </summary>
    [SugarColumn(Length = 32, IsNullable = false)]
    public string Role { get; set; } = string.Empty;
    
    /// <summary>
    /// 消息内容
    /// </summary>
    [SugarColumn(ColumnDataType = "TEXT", IsNullable = true)]
    public string? Content { get; set; }
    
    /// <summary>
    /// 创建时间
    /// </summary>
    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAt { get; set; } = DateTime.Now;
}
