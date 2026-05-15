using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service.Adapters;

/// <summary>
/// CLI工具适配器接口
/// 定义了不同CLI工具（如Codex、Claude Code等）需要实现的统一接口
/// </summary>
public interface ICliToolAdapter
{
    /// <summary>
    /// 适配器支持的工具ID列表
    /// </summary>
    string[] SupportedToolIds { get; }

    /// <summary>
    /// 检查此适配器是否能处理指定的CLI工具配置
    /// </summary>
    /// <param name="tool">CLI工具配置</param>
    /// <returns>是否能处理</returns>
    bool CanHandle(CliToolConfig tool);

    /// <summary>
    /// 是否支持流式JSON输出解析
    /// </summary>
    bool SupportsStreamParsing { get; }

    CliAttachmentCapabilities GetAttachmentCapabilities(CliToolConfig tool)
        => CliAttachmentCapabilities.ReferenceOnly();

    string BuildArguments(CliToolConfig tool, CliExecutionRequest request)
    {
        if (request.NativeAttachments.Count > 0)
        {
            throw new InvalidOperationException(
                $"Adapter for tool '{tool.Id}' must override request-based argument translation when native attachments are present.");
        }

        return BuildArguments(tool, request.BuildPromptText(), request.SessionContext);
    }

    /// <summary>
    /// 构建命令行参数
    /// </summary>
    /// <param name="tool">CLI工具配置</param>
    /// <param name="prompt">用户输入</param>
    /// <param name="context">会话上下文</param>
    /// <returns>完整的命令行参数字符串</returns>
    string BuildArguments(CliToolConfig tool, string prompt, CliSessionContext context);

    /// <summary>
    /// 构建少打断执行参数，不追加新的用户提示词
    /// </summary>
    string BuildLowInterruptionArguments(CliToolConfig tool, CliSessionContext context);

    /// <summary>
    /// 解析输出流中的事件
    /// </summary>
    /// <param name="line">输出行</param>
    /// <returns>解析后的事件，如果无法解析则返回null</returns>
    CliOutputEvent? ParseOutputLine(string line);

    /// <summary>
    /// 从输出中解析会话/线程ID
    /// </summary>
    /// <param name="outputEvent">输出事件</param>
    /// <returns>会话ID，如果没有则返回null</returns>
    string? ExtractSessionId(CliOutputEvent outputEvent);

    /// <summary>
    /// 从输出事件中提取助手消息内容
    /// </summary>
    /// <param name="outputEvent">输出事件</param>
    /// <returns>消息内容，如果不是消息事件则返回null</returns>
    string? ExtractAssistantMessage(CliOutputEvent outputEvent);

    /// <summary>
    /// 获取事件的显示标题
    /// </summary>
    /// <param name="outputEvent">输出事件</param>
    /// <returns>显示标题</returns>
    string GetEventTitle(CliOutputEvent outputEvent);

    /// <summary>
    /// 获取事件的显示样式类名
    /// </summary>
    /// <param name="outputEvent">输出事件</param>
    /// <returns>CSS类名</returns>
    string GetEventBadgeClass(CliOutputEvent outputEvent);

    /// <summary>
    /// 获取事件的显示标签
    /// </summary>
    /// <param name="outputEvent">输出事件</param>
    /// <returns>标签文本</returns>
    string GetEventBadgeLabel(CliOutputEvent outputEvent);
}

