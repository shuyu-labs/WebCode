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
    public bool FullReplyDocEnabled { get; set; }

    [SugarColumn(IsNullable = false)]
    public bool FinalReplyDocEnabled { get; set; }

    [SugarColumn(IsNullable = false)]
    public bool AudioFullReplyDocEnabled { get; set; }

    [SugarColumn(IsNullable = false)]
    public bool AudioFinalReplyDocEnabled { get; set; }

    [SugarColumn(IsNullable = false)]
    public bool ReferencedMarkdownDocImportEnabled { get; set; }

    [SugarColumn(Length = 256, IsNullable = true)]
    public string? DocumentAdminOpenId { get; set; }

    [SugarColumn(ColumnName = "ReplyTtsEnabled", IsNullable = true)]
    public bool? LegacyReplyTtsEnabled { get; set; }

    [SugarColumn(ColumnName = "ReplyTtsMode", Length = 64, IsNullable = true)]
    public string? LegacyReplyTtsMode { get; set; }

    [SugarColumn(ColumnName = "ReplyTtsVoiceId", Length = 128, IsNullable = true)]
    public string? LegacyReplyTtsVoiceId { get; set; }

    [SugarColumn(IsIgnore = true)]
    public bool ReplyTtsEnabled
    {
        get => LegacyReplyTtsEnabled ?? FullReplyDocEnabled || FinalReplyDocEnabled;
        set => LegacyReplyTtsEnabled = value;
    }

    [SugarColumn(IsIgnore = true)]
    public string? ReplyTtsMode
    {
        get => LegacyReplyTtsMode;
        set => LegacyReplyTtsMode = value;
    }

    [SugarColumn(IsIgnore = true)]
    public string? ReplyTtsVoiceId
    {
        get => LegacyReplyTtsVoiceId;
        set => LegacyReplyTtsVoiceId = value;
    }

    [SugarColumn(IsNullable = true)]
    public DateTime? LastStartedAt { get; set; }

    [SugarColumn(IsNullable = false)]
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    [SugarColumn(IsNullable = false)]
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
