namespace WebCodeCli.Domain.Domain.Model;

/// <summary>
/// CLI 执行结果
/// </summary>
public class CliExecutionResult
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// 输出内容
    /// </summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>
    /// 错误信息
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// 退出代码
    /// </summary>
    public int? ExitCode { get; set; }

    /// <summary>
    /// 执行时长（毫秒）
    /// </summary>
    public long ExecutionTimeMs { get; set; }

    /// <summary>
    /// 是否超时
    /// </summary>
    public bool IsTimeout { get; set; }
}

/// <summary>
/// 流式输出块
/// </summary>
public class StreamOutputChunk
{
    /// <summary>
    /// 内容
    /// </summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// 是否是错误流
    /// </summary>
    public bool IsError { get; set; }

    /// <summary>
    /// 是否完成
    /// </summary>
    public bool IsCompleted { get; set; }

    public bool IsTurnBoundary { get; set; }

    /// <summary>
    /// 错误信息
    /// </summary>
    public string? ErrorMessage { get; set; }
}

