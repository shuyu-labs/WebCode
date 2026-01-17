using System.Text;
using System.Text.Json;
using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service.Adapters;

/// <summary>
/// OpenCode CLI工具适配器
/// 处理 OpenCode CLI 的命令构建和 JSON 事件流输出解析
/// 
/// OpenCode CLI 特性:
/// - 支持多种 AI 模型 (通过 models.dev 集成)
/// - MCP 服务器支持
/// - Agent 系统
/// - 会话管理 (--continue, --session)
/// - JSON 格式输出 (--format json)
/// 
/// 命令格式:
/// - 启动 TUI: opencode
/// - 非交互模式: opencode run [message..] --format json
/// - 继续会话: opencode run --session <id> [message..]
/// - 上次会话: opencode run --continue [message..]
/// - 附加文件: opencode run [message..] --file <path>
/// - 指定模型: opencode run [message..] --model <provider/model>
/// 
/// 配置说明:
/// - Command: "opencode"
/// - ArgumentTemplate: 可选，为空时使用默认模板
/// - 支持通过环境变量覆盖 ArgumentTemplate
/// </summary>
public class OpenCodeAdapter : ICliToolAdapter
{
    /// <summary>
    /// 默认参数模板
    /// 支持的占位符:
    /// - {prompt}: 用户提示词
    /// - {session}: 会话恢复参数（如果有，格式为 "--session id" 或 "--continue"）
    /// </summary>
    public const string DefaultArgumentTemplate = "run {session} \"{prompt}\" --format json";

    public string[] SupportedToolIds => new[] { "opencode", "opencode-cli" };

    public bool SupportsStreamParsing => true;

    public bool CanHandle(CliToolConfig tool)
    {
        if (tool == null) return false;

        return SupportedToolIds.Any(id => 
            string.Equals(tool.Id, id, StringComparison.OrdinalIgnoreCase)) ||
               (tool.Command?.Contains("opencode", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public string BuildArguments(CliToolConfig tool, string prompt, CliSessionContext context)
    {
        var escapedPrompt = EscapeShellArgument(prompt);

        // 获取参数模板：优先使用配置的 ArgumentTemplate，为空则使用默认值
        // 模型配置说明:
        // OpenCode 通过以下方式配置模型 (优先级从高到低):
        // 1. OPENCODE_CONFIG_CONTENT 环境变量 (JSON 配置)
        // 2. 命令行参数 --model <provider/model>
        // 3. 项目配置文件 opencode.json
        // 4. 全局配置文件 ~/.config/opencode/opencode.json
        var template = !string.IsNullOrWhiteSpace(tool.ArgumentTemplate) 
            ? tool.ArgumentTemplate 
            : DefaultArgumentTemplate;

        // 构建会话恢复参数
        var sessionArg = string.Empty;
        if (context.IsResume && !string.IsNullOrEmpty(context.CliThreadId))
        {
            sessionArg = $"--session {context.CliThreadId}";
        }
        else if (context.IsResume)
        {
            sessionArg = "--continue";
        }

        // 替换模板占位符
        var result = template
            .Replace("{prompt}", escapedPrompt)
            .Replace("{session}", sessionArg)
            .Trim();

        // 清理多余空格
        while (result.Contains("  "))
        {
            result = result.Replace("  ", " ");
        }

        return result;
    }

    public CliOutputEvent? ParseOutputLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var trimmed = line.Trim();

        // OpenCode 的 --format json 输出 JSON 事件流
        if (!trimmed.StartsWith("{", StringComparison.Ordinal))
        {
            // 非 JSON 行，检查是否是错误信息
            var isError =
                trimmed.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("error", StringComparison.OrdinalIgnoreCase);

            if (isError)
            {
                return new CliOutputEvent
                {
                    EventType = "error",
                    IsError = true,
                    Content = trimmed,
                    Title = "错误"
                };
            }

            // 其他非 JSON 行作为普通输出
            return new CliOutputEvent
            {
                EventType = "raw",
                IsError = false,
                IsUnknown = false,
                Title = "输出",
                Content = trimmed
            };
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            var outputEvent = new CliOutputEvent
            {
                RawJson = trimmed
            };

            // 解析事件类型
            if (root.TryGetProperty("type", out var typeElement))
            {
                outputEvent.EventType = typeElement.GetString() ?? "unknown";
            }

            // 解析会话ID (sessionID 字段)
            if (root.TryGetProperty("sessionID", out var sessionIdElement))
            {
                outputEvent.SessionId = sessionIdElement.GetString();
            }

            // 根据不同的事件类型解析内容
            // OpenCode 的 JSON 事件格式:
            // {"type":"step_start","timestamp":...,"sessionID":"...","part":{...}}
            // {"type":"text","timestamp":...,"sessionID":"...","part":{"type":"text","text":"..."}}
            // {"type":"step_finish","timestamp":...,"sessionID":"...","part":{...}}
            // {"type":"tool_start","timestamp":...,"sessionID":"...","part":{"type":"tool-start","tool":"..."}}
            // {"type":"tool_finish","timestamp":...,"sessionID":"...","part":{"type":"tool-finish",...}}
            switch (outputEvent.EventType)
            {
                case "step_start":
                    ParseStepStartEvent(root, outputEvent);
                    break;
                    
                case "step_finish":
                    ParseStepFinishEvent(root, outputEvent);
                    break;

                case "tool_start":
                    ParseToolStartEvent(root, outputEvent);
                    break;
                    
                case "tool_finish":
                    ParseToolFinishEvent(root, outputEvent);
                    break;

                case "tool_use":
                    ParseToolUseEvent(root, outputEvent);
                    break;

                case "tool_result":
                    ParseToolResultEvent(root, outputEvent);
                    break;

                case "message":
                case "text":
                    ParseMessageEvent(root, outputEvent);
                    break;

                case "error":
                    ParseErrorEvent(root, outputEvent);
                    break;

                case "session_start":
                    ParseSessionStartEvent(root, outputEvent);
                    break;

                case "session_end":
                case "complete":
                    ParseCompleteEvent(root, outputEvent);
                    break;

                default:
                    outputEvent.IsUnknown = true;
                    outputEvent.Title = "未知事件";
                    // 尝试从 part 字段提取信息
                    if (root.TryGetProperty("part", out var partElement))
                    {
                        ParsePartElement(partElement, outputEvent);
                    }
                    break;
            }

            return outputEvent;
        }
        catch (JsonException)
        {
            // JSON 解析失败，返回原始内容
            return new CliOutputEvent
            {
                EventType = "raw",
                IsError = false,
                IsUnknown = false,
                Title = "输出",
                Content = trimmed
            };
        }
    }

    public string? ExtractSessionId(CliOutputEvent outputEvent)
    {
        return outputEvent.SessionId;
    }

    public string? ExtractAssistantMessage(CliOutputEvent outputEvent)
    {
        if (outputEvent.EventType == "message" || 
            outputEvent.EventType == "text" ||
            outputEvent.ItemType == "message")
        {
            return outputEvent.Content;
        }
        return null;
    }

    public string GetEventTitle(CliOutputEvent outputEvent)
    {
        return outputEvent.EventType switch
        {
            "session_start" => "会话开始",
            "step_start" => "步骤开始",
            "step_finish" => "步骤完成",
            "message" or "text" => "AI 回复",
            "tool_start" => "工具调用",
            "tool_finish" => "工具完成",
            "tool_use" => "工具调用",
            "tool_result" => "工具结果",
            "error" => "错误",
            "session_end" or "complete" => "完成",
            _ => outputEvent.Title ?? "输出"
        };
    }

    public string GetEventBadgeClass(CliOutputEvent outputEvent)
    {
        if (outputEvent.IsError)
            return "bg-red-100 text-red-700";

        return outputEvent.EventType switch
        {
            "session_start" => "bg-primary-100 text-primary-700",
            "step_start" => "bg-sky-100 text-sky-700",
            "step_finish" => "bg-emerald-100 text-emerald-700",
            "message" or "text" => "bg-emerald-100 text-emerald-700",
            "tool_start" => "bg-sky-100 text-sky-700",
            "tool_finish" => "bg-blue-100 text-blue-700",
            "tool_use" => "bg-sky-100 text-sky-700",
            "tool_result" => "bg-blue-100 text-blue-700",
            "session_end" or "complete" => "bg-emerald-100 text-emerald-700",
            _ => "bg-gray-200 text-gray-700"
        };
    }

    public string GetEventBadgeLabel(CliOutputEvent outputEvent)
    {
        if (outputEvent.IsError)
            return "ERROR";

        return outputEvent.EventType switch
        {
            "session_start" => "START",
            "step_start" => "STEP",
            "step_finish" => "DONE",
            "message" or "text" => "MESSAGE",
            "tool_start" => "TOOL",
            "tool_finish" => "RESULT",
            "tool_use" => "TOOL",
            "tool_result" => "RESULT",
            "session_end" or "complete" => "DONE",
            _ => outputEvent.EventType?.ToUpperInvariant() ?? "INFO"
        };
    }

    #region Private Parsing Methods

    private void ParseSessionStartEvent(JsonElement root, CliOutputEvent outputEvent)
    {
        outputEvent.Title = "会话开始";
        outputEvent.Content = "OpenCode 会话已启动";
    }

    private void ParseStepStartEvent(JsonElement root, CliOutputEvent outputEvent)
    {
        outputEvent.Title = "步骤开始";
        outputEvent.ItemType = "step_start";
        
        // 从 part 字段提取信息
        if (root.TryGetProperty("part", out var partElement))
        {
            if (partElement.TryGetProperty("snapshot", out var snapshotElement))
            {
                outputEvent.Content = $"快照: {snapshotElement.GetString()}";
            }
        }
        
        if (string.IsNullOrEmpty(outputEvent.Content))
        {
            outputEvent.Content = "开始处理请求...";
        }
    }

    private void ParseStepFinishEvent(JsonElement root, CliOutputEvent outputEvent)
    {
        outputEvent.Title = "步骤完成";
        outputEvent.ItemType = "step_finish";
        
        var sb = new StringBuilder();
        
        // 从 part 字段提取信息
        if (root.TryGetProperty("part", out var partElement))
        {
            if (partElement.TryGetProperty("reason", out var reasonElement))
            {
                var reason = reasonElement.GetString();
                sb.AppendLine($"结束原因: {reason}");
            }
            
            if (partElement.TryGetProperty("cost", out var costElement))
            {
                var cost = costElement.GetDouble();
                sb.AppendLine($"费用: ${cost:F6}");
            }
            
            // 使用 Usage 属性存储 token 信息，以卡片样式展示
            if (partElement.TryGetProperty("tokens", out var tokensElement))
            {
                outputEvent.Usage = new CliOutputEventUsage
                {
                    InputTokens = GetLongProperty(tokensElement, "input"),
                    OutputTokens = GetLongProperty(tokensElement, "output")
                };
                
                // 尝试获取缓存 token（如果存在）
                if (tokensElement.TryGetProperty("cache", out var cacheElement))
                {
                    outputEvent.Usage.CachedInputTokens = GetLongProperty(cacheElement, "read");
                }
            }
        }
        
        outputEvent.Content = sb.Length > 0 ? sb.ToString().TrimEnd() : "处理完成";
    }

    private void ParseToolStartEvent(JsonElement root, CliOutputEvent outputEvent)
    {
        outputEvent.Title = "工具调用";
        outputEvent.ItemType = "tool_start";
        
        var sb = new StringBuilder();
        
        // 从 part 字段提取工具信息
        if (root.TryGetProperty("part", out var partElement))
        {
            if (partElement.TryGetProperty("tool", out var toolElement))
            {
                sb.AppendLine($"工具: {toolElement.GetString()}");
            }
            
            if (partElement.TryGetProperty("state", out var stateElement))
            {
                if (stateElement.TryGetProperty("title", out var titleElement))
                {
                    sb.AppendLine($"操作: {titleElement.GetString()}");
                }
                
                if (stateElement.TryGetProperty("input", out var inputElement))
                {
                    var inputStr = inputElement.ToString();
                    if (inputStr.Length > 500)
                    {
                        inputStr = inputStr.Substring(0, 500) + "...";
                    }
                    sb.AppendLine($"参数: {inputStr}");
                }
            }
        }
        
        outputEvent.Content = sb.Length > 0 ? sb.ToString().TrimEnd() : "调用工具中...";
    }

    private void ParseToolFinishEvent(JsonElement root, CliOutputEvent outputEvent)
    {
        outputEvent.Title = "工具完成";
        outputEvent.ItemType = "tool_finish";
        
        var sb = new StringBuilder();
        
        // 从 part 字段提取结果
        if (root.TryGetProperty("part", out var partElement))
        {
            if (partElement.TryGetProperty("tool", out var toolElement))
            {
                sb.AppendLine($"工具: {toolElement.GetString()}");
            }
            
            if (partElement.TryGetProperty("state", out var stateElement))
            {
                if (stateElement.TryGetProperty("status", out var statusElement))
                {
                    var status = statusElement.GetString();
                    sb.AppendLine($"状态: {status}");
                    
                    if (status == "error" || status == "failed")
                    {
                        outputEvent.IsError = true;
                    }
                }
                
                if (stateElement.TryGetProperty("output", out var outputElement))
                {
                    var output = outputElement.GetString();
                    if (!string.IsNullOrEmpty(output))
                    {
                        if (output.Length > 1000)
                        {
                            output = output.Substring(0, 1000) + "...";
                        }
                        sb.AppendLine($"输出:\n{output}");
                    }
                }
            }
        }
        
        outputEvent.Content = sb.Length > 0 ? sb.ToString().TrimEnd() : "工具执行完成";
    }

    private void ParseMessageEvent(JsonElement root, CliOutputEvent outputEvent)
    {
        outputEvent.ItemType = "message";
        outputEvent.Title = "AI 回复";
        
        // OpenCode 的 text 事件格式:
        // {"type":"text","part":{"type":"text","text":"你好！有什么我可以帮助你的吗？",...}}
        
        // 优先从 part.text 提取消息内容 (OpenCode 格式)
        if (root.TryGetProperty("part", out var partElement))
        {
            if (partElement.TryGetProperty("text", out var partTextElement))
            {
                outputEvent.Content = partTextElement.GetString();
                return;
            }
            if (partElement.TryGetProperty("content", out var partContentElement))
            {
                outputEvent.Content = partContentElement.GetString();
                return;
            }
        }
        
        // 兼容其他格式
        if (root.TryGetProperty("content", out var contentElement))
        {
            outputEvent.Content = contentElement.GetString();
        }
        else if (root.TryGetProperty("text", out var textElement))
        {
            outputEvent.Content = textElement.GetString();
        }
    }

    private void ParseToolUseEvent(JsonElement root, CliOutputEvent outputEvent)
    {
        outputEvent.ItemType = "tool_use";
        outputEvent.Title = "工具调用";
        
        var sb = new StringBuilder();
        
        // 从 part 字段提取工具信息
        if (root.TryGetProperty("part", out var partElement))
        {
            if (partElement.TryGetProperty("tool", out var toolElement))
            {
                sb.AppendLine($"工具: {toolElement.GetString()}");
            }
            
            if (partElement.TryGetProperty("state", out var stateElement))
            {
                if (stateElement.TryGetProperty("title", out var titleElement))
                {
                    sb.AppendLine($"操作: {titleElement.GetString()}");
                }
                
                if (stateElement.TryGetProperty("input", out var inputElement))
                {
                    sb.AppendLine($"参数: {inputElement.ToString()}");
                }
            }
        }
        
        outputEvent.Content = sb.ToString().TrimEnd();
    }

    private void ParseToolResultEvent(JsonElement root, CliOutputEvent outputEvent)
    {
        outputEvent.ItemType = "tool_result";
        outputEvent.Title = "工具结果";
        
        if (root.TryGetProperty("result", out var resultElement))
        {
            outputEvent.Content = resultElement.ToString();
        }
        else if (root.TryGetProperty("part", out var partElement))
        {
            if (partElement.TryGetProperty("state", out var stateElement))
            {
                if (stateElement.TryGetProperty("output", out var outputElement))
                {
                    outputEvent.Content = outputElement.GetString();
                }
                
                if (stateElement.TryGetProperty("status", out var statusElement))
                {
                    var status = statusElement.GetString();
                    if (status == "error" || status == "failed")
                    {
                        outputEvent.IsError = true;
                    }
                }
            }
        }
    }

    private void ParseErrorEvent(JsonElement root, CliOutputEvent outputEvent)
    {
        outputEvent.IsError = true;
        outputEvent.Title = "错误";
        
        if (root.TryGetProperty("error", out var errorElement))
        {
            outputEvent.Content = errorElement.GetString();
        }
        else if (root.TryGetProperty("message", out var messageElement))
        {
            outputEvent.Content = messageElement.GetString();
        }
    }

    private void ParseCompleteEvent(JsonElement root, CliOutputEvent outputEvent)
    {
        outputEvent.Title = "完成";
        outputEvent.Content = "任务执行完成";
    }

    private void ParsePartElement(JsonElement partElement, CliOutputEvent outputEvent)
    {
        // part 元素通常包含工具调用的详细信息
        if (partElement.TryGetProperty("type", out var typeElement))
        {
            outputEvent.ItemType = typeElement.GetString();
        }
        
        if (partElement.TryGetProperty("content", out var contentElement))
        {
            outputEvent.Content = contentElement.GetString();
        }
        
        outputEvent.Title = outputEvent.ItemType ?? "输出";
    }

    private static string EscapeShellArgument(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "\"\"";

        // 转义双引号和反斜杠
        var escaped = input
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");

        return escaped;
    }

    private static long GetLongProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            return prop.GetInt64();
        }
        return 0;
    }

    #endregion
}
