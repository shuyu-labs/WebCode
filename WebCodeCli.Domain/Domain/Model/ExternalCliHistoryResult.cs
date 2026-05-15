namespace WebCodeCli.Domain.Domain.Model;

/// <summary>
/// 外部 CLI 会话历史读取结果，包含消息和来源信息。
/// </summary>
public class ExternalCliHistoryResult
{
    public List<ExternalCliHistoryMessage> Messages { get; set; } = [];

    public string? SourcePath { get; set; }
}
