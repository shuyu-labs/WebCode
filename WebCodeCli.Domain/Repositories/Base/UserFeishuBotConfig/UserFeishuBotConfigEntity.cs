using SqlSugar;

namespace WebCodeCli.Domain.Repositories.Base.UserFeishuBotConfig;

[SugarTable("UserFeishuBotConfig")]
[SugarIndex("IX_UserFeishuBotConfig_Username", nameof(Username), OrderByType.Asc, IsUnique = true)]
public class UserFeishuBotConfigEntity
{
    [SugarColumn(IsPrimaryKey = true, IsIdentity = true)]
    public int Id { get; set; }

    [SugarColumn(Length = 128, IsNullable = false)]
    public string Username { get; set; } = string.Empty;

    [SugarColumn(IsNullable = false)]
    public bool IsEnabled { get; set; } = true;

    [SugarColumn(IsNullable = false)]
    public bool AutoStartEnabled { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? AppId { get; set; }

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? AppSecret { get; set; }

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? EncryptKey { get; set; }

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? VerificationToken { get; set; }

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? DefaultCardTitle { get; set; }

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? ThinkingMessage { get; set; }

    [SugarColumn(IsNullable = true)]
    public int? HttpTimeoutSeconds { get; set; }

    [SugarColumn(IsNullable = true)]
    public int? StreamingThrottleMs { get; set; }

    [SugarColumn(IsNullable = false)]
    public bool ReplyTtsEnabled { get; set; }

    [SugarColumn(Length = 128, IsNullable = true)]
    public string? ReplyTtsVoiceId { get; set; }

    [SugarColumn(IsNullable = true)]
    public DateTime? LastStartedAt { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [SugarColumn(IsNullable = false)]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
