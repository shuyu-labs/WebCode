using SqlSugar;

namespace WebCodeCli.Domain.Repositories.Base.ChatSession;

[SugarTable("ChatMessageAttachment")]
public class ChatMessageAttachmentEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [SugarColumn(Length = 64, IsNullable = false)]
    public string MessageId { get; set; } = string.Empty;

    [SugarColumn(Length = 64, IsNullable = false)]
    public string SessionId { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string Username { get; set; } = string.Empty;

    [SugarColumn(Length = 256, IsNullable = false)]
    public string AttachmentId { get; set; } = string.Empty;

    [SugarColumn(Length = 256, IsNullable = false)]
    public string DisplayName { get; set; } = string.Empty;

    [SugarColumn(Length = 128, IsNullable = false)]
    public string MimeType { get; set; } = "application/octet-stream";

    [SugarColumn(Length = 32, IsNullable = false)]
    public string Extension { get; set; } = string.Empty;

    public long SizeBytes { get; set; }

    [SugarColumn(Length = 32, IsNullable = false)]
    public string Kind { get; set; } = string.Empty;

    [SugarColumn(Length = 512, IsNullable = false)]
    public string WorkspaceRelativePath { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
