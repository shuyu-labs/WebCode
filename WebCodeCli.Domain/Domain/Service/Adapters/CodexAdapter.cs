using System.Text;
using System.Text.Json;
using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service.Adapters;

/// <summary>
/// Codex CLI工具适配器
/// 处理OpenAI Codex CLI的命令构建和JSONL输出解析
/// </summary>
public class CodexAdapter : ICliToolAdapter
{
    public string[] SupportedToolIds => new[] { "codex" };

    public bool SupportsStreamParsing => true;

    public bool CanHandle(CliToolConfig tool)
    {
        if (tool == null) return false;

        return string.Equals(tool.Id, "codex", StringComparison.OrdinalIgnoreCase) ||
               (tool.Command?.Contains("codex", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public string BuildArguments(CliToolConfig tool, string prompt, CliSessionContext context)
    {
        var escapedPrompt = EscapeJsonString(prompt);

        // Codex 的 resume 需要将 `resume <session_id> <prompt>` 作为独立参数传递，
        // 因此配置中不应把 {prompt} 包在一整个引号里；这里做兼容处理。
        var template = tool.ArgumentTemplate ?? string.Empty;
        var resumeCompatibleTemplate = template.Replace("\"{prompt}\"", "{prompt}");

        if (context.IsResume && !string.IsNullOrEmpty(context.CliThreadId))
        {
            // 恢复会话模式（正确语法：codex exec ... resume <session_id> "<prompt>"）
            // 复用配置模板，以确保包含 --skip-git-repo-check 等必需标志。
            if (string.IsNullOrWhiteSpace(resumeCompatibleTemplate) || !resumeCompatibleTemplate.Contains("{prompt}", StringComparison.Ordinal))
            {
                // 兜底：如果模板异常，至少保证包含 skip-git-repo-check
                return $"exec --skip-git-repo-check --dangerously-bypass-approvals-and-sandbox --json resume {context.CliThreadId} \"{escapedPrompt}\"";
            }

            return resumeCompatibleTemplate.Replace("{prompt}", $"resume {context.CliThreadId} \"{escapedPrompt}\"");
        }
        else
        {
            // 首次请求，使用配置的参数模板
            if (string.IsNullOrWhiteSpace(template) || !template.Contains("{prompt}", StringComparison.Ordinal))
            {
                return $"exec --skip-git-repo-check --dangerously-bypass-approvals-and-sandbox --json \"{escapedPrompt}\"";
            }

            // 若模板已包含 "{prompt}"，则不再额外加引号。
            if (template.Contains("\"{prompt}\"", StringComparison.Ordinal))
            {
                return template.Replace("{prompt}", escapedPrompt);
            }

            return template.Replace("{prompt}", $"\"{escapedPrompt}\"");
        }
    }

    public CliOutputEvent? ParseOutputLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var trimmed = line.Trim();

        // Codex 的 --json 输出通常为 JSONL，但仍可能混入非 JSON 行（例如分隔符/提示/错误文本）。
        // 非 JSON 行不应进入 JSON 解析，否则会导致 parse_error。
        if (!trimmed.StartsWith("{", StringComparison.Ordinal) && !trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            // 兼容旧版本的 session id 文本格式
            if (trimmed.StartsWith("session id:", StringComparison.OrdinalIgnoreCase))
            {
                var parts = trimmed.Split(':', 2);
                var legacyId = parts.Length == 2 ? parts[1].Trim() : null;
                if (!string.IsNullOrWhiteSpace(legacyId))
                {
                    return new CliOutputEvent
                    {
                        EventType = "thread.started",
                        SessionId = legacyId,
                        Title = null,
                        Content = null
                    };
                }
            }

            // Codex/Rust 侧日志常见格式：
            // 2026-...Z ERROR rmcp::...: ...
            // 或者直接 Error:/ERROR:
            var isError =
                trimmed.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains(" ERROR ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains(" FATAL ", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("fatal:", StringComparison.OrdinalIgnoreCase) ||
                trimmed.Contains("rmcp::", StringComparison.OrdinalIgnoreCase);

            return new CliOutputEvent
            {
                EventType = isError ? "error" : "raw",
                IsError = isError,
                IsUnknown = false,
                Title = isError ? "错误" : "输出",
                Content = trimmed,
                RawJson = null
            };
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;

            if (!root.TryGetProperty("type", out var typeElement))
            {
                return new CliOutputEvent
                {
                    EventType = "unknown",
                    IsUnknown = true,
                    Content = "缺少 type 字段",
                    RawJson = line
                };
            }

            var eventType = typeElement.GetString() ?? string.Empty;
            var outputEvent = new CliOutputEvent
            {
                EventType = eventType,
                RawJson = line
            };

            switch (eventType)
            {
                case "thread.started":
                    ParseThreadStarted(root, outputEvent);
                    break;
                case "turn.started":
                    ParseTurnStarted(outputEvent);
                    break;
                case "turn.completed":
                    ParseTurnCompleted(root, outputEvent);
                    break;
                case "turn.failed":
                    ParseTurnFailed(root, outputEvent);
                    break;
                case "item.started":
                    ParseItemStarted(root, outputEvent);
                    break;
                case "item.updated":
                    ParseItemUpdated(root, outputEvent);
                    break;
                case "item.completed":
                    ParseItemCompleted(root, outputEvent);
                    break;
                case "error":
                    ParseError(root, outputEvent);
                    break;
                default:
                    outputEvent.IsUnknown = true;
                    outputEvent.Content = $"未识别的事件类型: {eventType}";
                    break;
            }

            return outputEvent;
        }
        catch (JsonException)
        {
            // 兜底：避免 parse_error 污染 UI
            return new CliOutputEvent
            {
                EventType = "raw",
                IsError = false,
                IsUnknown = false,
                Title = "输出",
                Content = trimmed,
                RawJson = null
            };
        }
    }

    public string? ExtractSessionId(CliOutputEvent outputEvent)
    {
        return outputEvent.SessionId;
    }

    public string? ExtractAssistantMessage(CliOutputEvent outputEvent)
    {
        if (outputEvent.ItemType == "agent_message" && !string.IsNullOrEmpty(outputEvent.Content))
        {
            return outputEvent.Content;
        }
        return null;
    }

    public string GetEventTitle(CliOutputEvent outputEvent)
    {
        return outputEvent.Title ?? outputEvent.EventType switch
        {
            "thread.started" => "会话已启动",
            "turn.started" => "开始新一轮交互",
            "turn.completed" => "交互已完成",
            "turn.failed" => "交互失败",
            "item.started" => GetItemTitle(outputEvent.ItemType, "开始"),
            "item.updated" => GetItemTitle(outputEvent.ItemType, "更新"),
            "item.completed" => GetItemTitle(outputEvent.ItemType, "完成"),
            "error" => "错误",
            _ => $"事件 ({outputEvent.EventType})"
        };
    }

    public string GetEventBadgeClass(CliOutputEvent outputEvent)
    {
        if (outputEvent.IsUnknown)
            return "bg-amber-100 text-amber-700";

        if (outputEvent.ItemType == "reasoning")
            return "bg-purple-100 text-purple-700";

        if (outputEvent.ItemType == "agent_message")
            return "bg-amber-100 text-amber-700";

        return outputEvent.EventType switch
        {
            "thread.started" => "bg-primary-100 text-primary-700",
            "turn.started" => "bg-sky-100 text-sky-700",
            "turn.completed" => "bg-emerald-100 text-emerald-700",
            "turn.failed" => "bg-red-100 text-red-700",
            "item.started" => "bg-amber-100 text-amber-700",
            "item.updated" => "bg-blue-100 text-blue-700",
            "error" => "bg-red-100 text-red-700",
            _ => "bg-gray-200 text-gray-700"
        };
    }

    public string GetEventBadgeLabel(CliOutputEvent outputEvent)
    {
        if (outputEvent.IsUnknown)
            return "未识别";

        if (outputEvent.ItemType == "reasoning")
            return "推理";

        if (outputEvent.ItemType == "agent_message")
            return "回复";

        return outputEvent.EventType switch
        {
            "thread.started" => "线程",
            "turn.started" => "交互开始",
            "turn.completed" => "交互结束",
            "turn.failed" => "交互失败",
            "item.started" => "节点开始",
            "item.updated" => "节点更新",
            "error" => "错误",
            _ => "事件"
        };
    }

    #region Private Parsing Methods

    private void ParseThreadStarted(JsonElement root, CliOutputEvent outputEvent)
    {
        if (root.TryGetProperty("thread_id", out var threadIdElement))
        {
            var threadId = threadIdElement.GetString();
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                outputEvent.SessionId = threadId;
            }
        }

        outputEvent.Title = null;
        outputEvent.Content = null;
    }

    private void ParseTurnStarted(CliOutputEvent outputEvent)
    {
        outputEvent.Title = null;
        outputEvent.Content = null;
    }

    private void ParseTurnCompleted(JsonElement root, CliOutputEvent outputEvent)
    {
        if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
        {
            outputEvent.Usage = new CliOutputEventUsage
            {
                InputTokens = GetLongProperty(usageElement, "input_tokens"),
                CachedInputTokens = GetLongProperty(usageElement, "cached_input_tokens"),
                OutputTokens = GetLongProperty(usageElement, "output_tokens")
            };
        }

        outputEvent.Title = null;
        outputEvent.Content = null;
    }

    private void ParseTurnFailed(JsonElement root, CliOutputEvent outputEvent)
    {
        var contentBuilder = new StringBuilder();
        contentBuilder.AppendLine("本轮交互失败。");

        if (root.TryGetProperty("error", out var errorElement))
        {
            if (errorElement.ValueKind == JsonValueKind.String)
            {
                var errorMsg = errorElement.GetString();
                if (!string.IsNullOrWhiteSpace(errorMsg))
                {
                    contentBuilder.AppendLine();
                    contentBuilder.AppendLine("错误信息:");
                    contentBuilder.AppendLine(errorMsg);
                }
            }
            else if (errorElement.ValueKind == JsonValueKind.Object)
            {
                if (errorElement.TryGetProperty("message", out var msgElement) && msgElement.ValueKind == JsonValueKind.String)
                {
                    var errorMsg = msgElement.GetString();
                    if (!string.IsNullOrWhiteSpace(errorMsg))
                    {
                        contentBuilder.AppendLine();
                        contentBuilder.AppendLine("错误信息:");
                        contentBuilder.AppendLine(errorMsg);
                    }
                }

                if (errorElement.TryGetProperty("code", out var codeElement))
                {
                    var code = codeElement.ValueKind == JsonValueKind.String
                        ? codeElement.GetString()
                        : codeElement.GetRawText();
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        contentBuilder.AppendLine($"错误代码: {code}");
                    }
                }
            }
        }

        outputEvent.Title = "交互失败";
        outputEvent.Content = contentBuilder.ToString().TrimEnd();
        outputEvent.IsError = true;
        outputEvent.IsUnknown = true;
    }

    private void ParseError(JsonElement root, CliOutputEvent outputEvent)
    {
        var contentBuilder = new StringBuilder();

        if (root.TryGetProperty("message", out var messageElement) && messageElement.ValueKind == JsonValueKind.String)
        {
            var errorMsg = messageElement.GetString();
            if (!string.IsNullOrWhiteSpace(errorMsg))
            {
                contentBuilder.AppendLine(errorMsg);
                outputEvent.ErrorMessage = errorMsg;
            }
        }

        if (root.TryGetProperty("code", out var codeElement))
        {
            var code = codeElement.ValueKind == JsonValueKind.String
                ? codeElement.GetString()
                : codeElement.GetRawText();
            if (!string.IsNullOrWhiteSpace(code))
            {
                contentBuilder.AppendLine($"错误代码: {code}");
            }
        }

        if (root.TryGetProperty("details", out var detailsElement) && detailsElement.ValueKind == JsonValueKind.String)
        {
            var details = detailsElement.GetString();
            if (!string.IsNullOrWhiteSpace(details))
            {
                contentBuilder.AppendLine();
                contentBuilder.AppendLine("详细信息:");
                contentBuilder.AppendLine(details);
            }
        }

        if (contentBuilder.Length == 0)
        {
            contentBuilder.Append("发生未知错误。");
        }

        outputEvent.Title = "错误";
        outputEvent.Content = contentBuilder.ToString().TrimEnd();
        outputEvent.IsError = true;
        outputEvent.IsUnknown = true;
    }

    private void ParseItemStarted(JsonElement root, CliOutputEvent outputEvent)
    {
        if (!root.TryGetProperty("item", out var itemElement) || itemElement.ValueKind != JsonValueKind.Object)
        {
            outputEvent.IsUnknown = true;
            outputEvent.Content = "item.started 缺少 item 字段";
            return;
        }

        if (itemElement.TryGetProperty("type", out var itemTypeElement) && itemTypeElement.ValueKind == JsonValueKind.String)
        {
            outputEvent.ItemType = itemTypeElement.GetString();
        }

        if (itemElement.TryGetProperty("thread_id", out var itemThread) && itemThread.ValueKind == JsonValueKind.String)
        {
            var threadId = itemThread.GetString();
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                outputEvent.SessionId = threadId;
            }
        }

        var contentBuilder = new StringBuilder();

        if (itemElement.TryGetProperty("command", out var commandElement) && commandElement.ValueKind == JsonValueKind.String)
        {
            var commandText = commandElement.GetString();
            if (!string.IsNullOrWhiteSpace(commandText))
            {
                contentBuilder.AppendLine($"执行命令: {commandText}");
                outputEvent.CommandExecution = new CliCommandExecution { Command = commandText };
            }
        }

        if (itemElement.TryGetProperty("status", out var statusElement))
        {
            var statusText = statusElement.ValueKind == JsonValueKind.String
                ? statusElement.GetString()
                : statusElement.GetRawText();

            if (!string.IsNullOrWhiteSpace(statusText))
            {
                contentBuilder.AppendLine($"状态: {statusText}");
                if (outputEvent.CommandExecution != null)
                {
                    outputEvent.CommandExecution.Status = statusText;
                }
            }
        }

        if (contentBuilder.Length == 0)
        {
            contentBuilder.Append("已开始处理该节点。");
        }

        outputEvent.Title = GetItemTitle(outputEvent.ItemType, "开始");
        outputEvent.Content = contentBuilder.ToString().TrimEnd();
    }

    private void ParseItemUpdated(JsonElement root, CliOutputEvent outputEvent)
    {
        if (!root.TryGetProperty("item", out var itemElement) || itemElement.ValueKind != JsonValueKind.Object)
        {
            outputEvent.IsUnknown = true;
            outputEvent.Content = "item.updated 缺少 item 字段";
            return;
        }

        if (itemElement.TryGetProperty("type", out var itemTypeElement) && itemTypeElement.ValueKind == JsonValueKind.String)
        {
            outputEvent.ItemType = itemTypeElement.GetString();
        }

        if (itemElement.TryGetProperty("thread_id", out var itemThread) && itemThread.ValueKind == JsonValueKind.String)
        {
            var threadId = itemThread.GetString();
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                outputEvent.SessionId = threadId;
            }
        }

        outputEvent.Content = outputEvent.ItemType switch
        {
            "todo_list" => FormatTodoListContent(itemElement, outputEvent),
            "command_execution" => FormatCommandExecutionContent(itemElement, outputEvent),
            "file_change" => FormatFileChangeContent(itemElement, outputEvent),
            "agent_message" => ExtractItemText(itemElement),
            "reasoning" => ExtractItemText(itemElement),
            _ => ExtractItemText(itemElement)
        };

        outputEvent.Title = GetItemTitle(outputEvent.ItemType, "更新");
    }

    private void ParseItemCompleted(JsonElement root, CliOutputEvent outputEvent)
    {
        if (!root.TryGetProperty("item", out var itemElement) || itemElement.ValueKind != JsonValueKind.Object)
        {
            outputEvent.IsUnknown = true;
            outputEvent.Content = "item.completed 缺少 item 字段";
            return;
        }

        if (itemElement.TryGetProperty("type", out var itemTypeElement) && itemTypeElement.ValueKind == JsonValueKind.String)
        {
            outputEvent.ItemType = itemTypeElement.GetString();
        }

        if (itemElement.TryGetProperty("thread_id", out var itemThread) && itemThread.ValueKind == JsonValueKind.String)
        {
            var threadId = itemThread.GetString();
            if (!string.IsNullOrWhiteSpace(threadId))
            {
                outputEvent.SessionId = threadId;
            }
        }

        outputEvent.Content = outputEvent.ItemType switch
        {
            "command_execution" => FormatCommandExecutionContent(itemElement, outputEvent),
            "agent_message" => ExtractItemText(itemElement),
            "file_change" => FormatFileChangeContent(itemElement, outputEvent),
            _ => ExtractItemText(itemElement)
        };

        outputEvent.Title = GetItemTitle(outputEvent.ItemType, "完成");
    }

    private string GetItemTitle(string? itemType, string action)
    {
        return itemType switch
        {
            "command_execution" => $"命令执行{action}",
            "agent_message" => $"助手消息{action}",
            "reasoning" => $"推理过程{action}",
            "mcp_tool_call" => $"MCP 工具调用{action}",
            "web_search" => $"网络搜索{action}",
            "todo_list" => $"待办列表{action}",
            _ => $"节点{action}（{itemType ?? "未知类型"}）"
        };
    }

    private string FormatTodoListContent(JsonElement itemElement, CliOutputEvent outputEvent)
    {
        var contentBuilder = new StringBuilder();
        outputEvent.TodoItems = new List<CliTodoItem>();

        if (itemElement.TryGetProperty("items", out var itemsElement) && itemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsElement.EnumerateArray())
            {
                var todoItem = new CliTodoItem();

                // Codex todo_list 常见字段：text + completed(bool)
                // 兼容字段：title/status
                if (item.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
                {
                    todoItem.Id = idElement.GetString();
                }
                if (item.TryGetProperty("title", out var titleElement) && titleElement.ValueKind == JsonValueKind.String)
                {
                    todoItem.Title = titleElement.GetString();
                }
                if (string.IsNullOrWhiteSpace(todoItem.Title) &&
                    item.TryGetProperty("text", out var textElement) && textElement.ValueKind == JsonValueKind.String)
                {
                    todoItem.Title = textElement.GetString();
                }
                if (item.TryGetProperty("status", out var statusElement) && statusElement.ValueKind == JsonValueKind.String)
                {
                    todoItem.Status = statusElement.GetString();
                }
                else if (item.TryGetProperty("completed", out var completedElement) &&
                         (completedElement.ValueKind == JsonValueKind.True || completedElement.ValueKind == JsonValueKind.False))
                {
                    todoItem.Status = completedElement.GetBoolean() ? "completed" : "pending";
                }

                outputEvent.TodoItems.Add(todoItem);

                var statusIcon = todoItem.Status switch
                {
                    "completed" => "✓",
                    "in_progress" => "◐",
                    "pending" => "○",
                    _ => "○"
                };

                var title = string.IsNullOrWhiteSpace(todoItem.Title) ? "(无标题)" : todoItem.Title;
                // 每项独占一行，使用双换行确保Markdown渲染为段落
                contentBuilder.AppendLine($"{statusIcon} {title}");
                contentBuilder.AppendLine(); // 空行分隔
            }
        }

        return contentBuilder.Length > 0 ? contentBuilder.ToString().TrimEnd() : "待办列表已更新";
    }

    private string FormatCommandExecutionContent(JsonElement itemElement, CliOutputEvent outputEvent)
    {
        var contentBuilder = new StringBuilder();
        outputEvent.CommandExecution ??= new CliCommandExecution();

        if (itemElement.TryGetProperty("command", out var commandElement))
        {
            outputEvent.CommandExecution.Command = commandElement.GetString();
            contentBuilder.AppendLine($"命令: {outputEvent.CommandExecution.Command}");
        }

        // Codex 命令输出常见字段：aggregated_output（字符串）
        string? outputText = null;
        if (itemElement.TryGetProperty("aggregated_output", out var aggOutputElement) && aggOutputElement.ValueKind == JsonValueKind.String)
        {
            outputText = aggOutputElement.GetString();
        }
        else if (itemElement.TryGetProperty("output", out var outputElement) && outputElement.ValueKind == JsonValueKind.String)
        {
            outputText = outputElement.GetString();
        }

        outputEvent.CommandExecution.Output = outputText;
        if (!string.IsNullOrWhiteSpace(outputText))
        {
            contentBuilder.AppendLine("输出:");
            contentBuilder.AppendLine(outputText);
        }

        if (itemElement.TryGetProperty("exit_code", out var exitCodeElement) && exitCodeElement.ValueKind == JsonValueKind.Number)
        {
            outputEvent.CommandExecution.ExitCode = exitCodeElement.GetInt32();
            contentBuilder.AppendLine($"退出代码: {outputEvent.CommandExecution.ExitCode}");
        }

        if (itemElement.TryGetProperty("status", out var statusElement))
        {
            outputEvent.CommandExecution.Status = statusElement.GetString();
        }

        return contentBuilder.Length > 0 ? contentBuilder.ToString().TrimEnd() : "命令执行中...";
    }

    private string FormatFileChangeContent(JsonElement itemElement, CliOutputEvent outputEvent)
    {
        var contentBuilder = new StringBuilder();

        if (itemElement.TryGetProperty("changes", out var changesElement) && changesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var change in changesElement.EnumerateArray())
            {
                if (change.ValueKind != JsonValueKind.Object) continue;

                var path = change.TryGetProperty("path", out var pathEl) && pathEl.ValueKind == JsonValueKind.String
                    ? pathEl.GetString()
                    : null;
                var kind = change.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String
                    ? kindEl.GetString()
                    : null;

                var prefix = kind switch
                {
                    "add" => "+",
                    "modify" => "M",
                    "delete" => "-",
                    _ => "*"
                };

                if (!string.IsNullOrWhiteSpace(path))
                {
                    contentBuilder.AppendLine($"{prefix} {path}");
                }
            }
        }

        if (itemElement.TryGetProperty("status", out var statusEl))
        {
            var statusText = statusEl.ValueKind == JsonValueKind.String ? statusEl.GetString() : statusEl.GetRawText();
            if (!string.IsNullOrWhiteSpace(statusText))
            {
                contentBuilder.AppendLine($"状态: {statusText}");
            }
        }

        return contentBuilder.Length > 0 ? contentBuilder.ToString().TrimEnd() : "文件变更已记录";
    }

    private string ExtractItemText(JsonElement itemElement)
    {
        // 尝试多个可能的文本字段
        var textFields = new[] { "text", "content", "message", "output" };

        foreach (var field in textFields)
        {
            if (itemElement.TryGetProperty(field, out var textElement) && textElement.ValueKind == JsonValueKind.String)
            {
                var text = textElement.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return string.Empty;
    }

    private static long GetLongProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop))
        {
            if (prop.ValueKind == JsonValueKind.Number)
            {
                return prop.GetInt64();
            }
        }
        return 0;
    }

    private static string EscapeJsonString(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        return input
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }

    #endregion
}

