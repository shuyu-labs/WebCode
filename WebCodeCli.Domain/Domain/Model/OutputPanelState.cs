namespace WebCodeCli.Domain.Domain.Model;

/// <summary>
/// 输出结果区域（Tab=输出结果）的持久化状态。
/// </summary>
public class OutputPanelState
{
    public string SessionId { get; set; } = string.Empty;

    /// <summary>
    /// 非 JSONL 模式下的原始输出（会作为 Markdown 渲染）。
    /// JSONL 模式下也可能用于展示最终 assistant 文本。
    /// </summary>
    public string? RawOutput { get; set; } = string.Empty;

    /// <summary>
    /// 是否启用 JSONL 流式输出模式。
    /// </summary>
    public bool IsJsonlOutputActive { get; set; }

    /// <summary>
    /// JSONL 模式下的当前 thread id。
    /// </summary>
    public string ActiveThreadId { get; set; } = string.Empty;

    /// <summary>
    /// JSONL 事件列表（用于“命令执行/工具调用”等卡片展示）。
    /// </summary>
    public List<OutputJsonlEvent> JsonlEvents { get; set; } = new();

    /// <summary>
    /// 事件 JSON 字符串（用于数据库存储）
    /// </summary>
    public string? EventsJson { get; set; }

    /// <summary>
    /// 显示的事件数量
    /// </summary>
    public int DisplayedEventCount { get; set; } = 20;

    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public class OutputJsonlEvent
{
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? ItemType { get; set; }
    public OutputJsonlUsageDetail? Usage { get; set; }
    public bool IsUnknown { get; set; }
}

public class OutputJsonlUsageDetail
{
    public long? InputTokens { get; set; }
    public long? CachedInputTokens { get; set; }
    public long? OutputTokens { get; set; }
}
/// <summary>
/// 输出事件组（用于 OutputResultPanel 组件）
/// </summary>
public class OutputEventGroup
{
    public string Id { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty; // "command_execution" | "tool_call" | "single"
    public string Title { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public bool IsCollapsible { get; set; }
    public List<OutputEvent> Items { get; set; } = new();
}

/// <summary>
/// 输出事件（用于 OutputResultPanel 组件）
/// </summary>
public class OutputEvent
{
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? ItemType { get; set; }
    public TokenUsage? Usage { get; set; }
    
    /// <summary>
    /// 用户问题（用于 AskUserQuestion 工具）
    /// </summary>
    public UserQuestion? UserQuestion { get; set; }
}

/// <summary>
/// 用户问题（用于 AskUserQuestion 工具）
/// </summary>
public class UserQuestion
{
    /// <summary>
    /// 工具调用 ID（用于发送响应）
    /// </summary>
    public string? ToolUseId { get; set; }
    
    /// <summary>
    /// 问题列表
    /// </summary>
    public List<QuestionItem> Questions { get; set; } = new();
    
    /// <summary>
    /// 是否已回答
    /// </summary>
    public bool IsAnswered { get; set; }
}

/// <summary>
/// 单个问题项
/// </summary>
public class QuestionItem
{
    /// <summary>
    /// 问题标题/头部
    /// </summary>
    public string? Header { get; set; }
    
    /// <summary>
    /// 问题内容
    /// </summary>
    public string? Question { get; set; }
    
    /// <summary>
    /// 是否允许多选
    /// </summary>
    public bool MultiSelect { get; set; }
    
    /// <summary>
    /// 选项列表
    /// </summary>
    public List<QuestionOption> Options { get; set; } = new();
    
    /// <summary>
    /// 已选择的选项索引
    /// </summary>
    public List<int> SelectedIndexes { get; set; } = new();
}

/// <summary>
/// 问题选项
/// </summary>
public class QuestionOption
{
    /// <summary>
    /// 选项标签
    /// </summary>
    public string? Label { get; set; }
    
    /// <summary>
    /// 选项描述
    /// </summary>
    public string? Description { get; set; }
}

/// <summary>
/// Token 使用情况
/// </summary>
public class TokenUsage
{
    public int? InputTokens { get; set; }
    public int? CachedInputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? TotalTokens { get; set; }
}