using System.Text;
using System.Text.Json;
using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service.Adapters;

/// <summary>
/// Claude Code CLI工具适配器
/// 处理Anthropic Claude Code CLI的命令构建和stream-json输出解析
/// 
/// Claude Code CLI支持以下模式:
/// - 交互模式: claude (启动REPL)
/// - Print模式: claude -p "prompt" (单次执行)
/// - 流式输出: --output-format=stream-json
/// - 会话恢复: --continue / --resume
/// </summary>
public class ClaudeCodeAdapter : ICliToolAdapter
{
    /// <summary>
    /// 默认参数模板
    /// 支持的占位符:
    /// - {prompt}: 用户提示词
    /// - {session}: 会话恢复参数（如果有）
    /// </summary>
    public const string DefaultArgumentTemplate = "-p --verbose --output-format=stream-json --dangerously-skip-permissions {session} \"{prompt}\"";

    public string[] SupportedToolIds => new[] { "claude-code", "claude" };

    public bool SupportsStreamParsing => true;

    public bool CanHandle(CliToolConfig tool)
    {
        if (tool == null) return false;

        return string.Equals(tool.Id, "claude-code", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(tool.Id, "claude", StringComparison.OrdinalIgnoreCase) ||
               (tool.Command?.Contains("claude", StringComparison.OrdinalIgnoreCase) ?? false) &&
               !(tool.Command?.Contains("codex", StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public string BuildArguments(CliToolConfig tool, string prompt, CliSessionContext context)
    {
        var escapedPrompt = EscapeShellArgument(prompt);
        
        // 获取参数模板：优先使用配置的 ArgumentTemplate，为空则使用默认值
        var template = !string.IsNullOrWhiteSpace(tool.ArgumentTemplate) 
            ? tool.ArgumentTemplate 
            : DefaultArgumentTemplate;

        // 构建会话恢复参数
        var sessionArg = string.Empty;
        if (context.IsResume && !string.IsNullOrEmpty(context.CliThreadId))
        {
            sessionArg = $"--resume {context.CliThreadId}";
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

        // Claude Code 在 stdout/stderr 可能输出非 JSON 行（例如 Error: ...）。
        // 这些行不应进入 JSON 解析，否则会导致 UI 出现 parse_error。
        if (!trimmed.StartsWith("{", StringComparison.Ordinal) && !trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            var isError = trimmed.StartsWith("Error:", StringComparison.OrdinalIgnoreCase) ||
                          trimmed.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase);

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

            // Claude Code stream-json 格式解析
            // 你贴出来的实际格式是：
            // - type=system (subtype=init)  -> 初始化事件
            // - type=assistant/user         -> message 对象里包含 content[] (text/tool_use/tool_result)
            // 同时也兼容旧格式：type=init/message/result/error/tool_use/tool_result
            var eventType = GetStringProperty(root, "type") ?? "unknown";

            var outputEvent = new CliOutputEvent
            {
                EventType = eventType,
                RawJson = line
            };

            switch (eventType)
            {
                case "init":
                    ParseInitEvent(root, outputEvent);
                    break;
                case "message":
                    ParseMessageEvent(root, outputEvent);
                    break;
                case "tool_use":
                    ParseToolUseEvent(root, outputEvent);
                    break;
                case "tool_result":
                    ParseToolResultEvent(root, outputEvent);
                    break;
                case "result":
                    ParseResultEvent(root, outputEvent);
                    break;
                case "error":
                    ParseErrorEvent(root, outputEvent);
                    break;
                case "system":
                    // system/init 是 Claude Code 新版常见的初始化事件
                    if (root.TryGetProperty("subtype", out var subtypeEl) &&
                        subtypeEl.ValueKind == JsonValueKind.String &&
                        string.Equals(subtypeEl.GetString(), "init", StringComparison.OrdinalIgnoreCase))
                    {
                        outputEvent.EventType = "init";
                        ParseInitEvent(root, outputEvent);
                    }
                    else
                    {
                        ParseSystemEvent(root, outputEvent);
                    }
                    break;
                case "assistant":
                    ParseAssistantOrUserEvent(root, outputEvent, isAssistant: true);
                    break;
                case "user":
                    ParseAssistantOrUserEvent(root, outputEvent, isAssistant: false);
                    break;
                default:
                    // 尝试检测是否为消息内容
                    if (root.TryGetProperty("content", out _) || root.TryGetProperty("text", out _))
                    {
                        ParseGenericContentEvent(root, outputEvent);
                    }
                    else
                    {
                        outputEvent.IsUnknown = true;
                        outputEvent.Content = $"未识别的事件类型: {eventType}";
                    }
                    break;
            }

            return outputEvent;
        }
        catch (JsonException ex)
        {
            // 兜底：即使出现异常，也不要把它当作 parse_error 事件污染 UI。
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
        // Claude Code的助手消息通常在 message 或 assistant 类型事件中
        if ((outputEvent.EventType == "message" || 
             outputEvent.EventType == "assistant" || 
             outputEvent.EventType == "assistant:message") &&
            !string.IsNullOrEmpty(outputEvent.Content))
        {
            return outputEvent.Content;
        }
        return null;
    }

    public string GetEventTitle(CliOutputEvent outputEvent)
    {
        return outputEvent.Title ?? outputEvent.EventType switch
        {
            "init" => "会话初始化",
            "message" => "消息",
            "assistant" or "assistant:message" => "助手消息",
            "tool_use" => "工具调用",
            "tool_result" => "工具结果",
            "result" => "执行完成",
            "error" => "错误",
            "system" => "系统消息",
            _ => $"事件 ({outputEvent.EventType})"
        };
    }

    public string GetEventBadgeClass(CliOutputEvent outputEvent)
    {
        if (outputEvent.IsUnknown)
            return "bg-amber-100 text-amber-700";

        if (outputEvent.IsError)
            return "bg-red-100 text-red-700";

        return outputEvent.EventType switch
        {
            "init" => "bg-primary-100 text-primary-700",
            "message" or "assistant" or "assistant:message" => "bg-emerald-100 text-emerald-700",
            "tool_use" => "bg-sky-100 text-sky-700",
            "tool_result" => "bg-blue-100 text-blue-700",
            "result" => "bg-emerald-100 text-emerald-700",
            "error" => "bg-red-100 text-red-700",
            "system" => "bg-gray-100 text-gray-700",
            _ => "bg-gray-200 text-gray-700"
        };
    }

    public string GetEventBadgeLabel(CliOutputEvent outputEvent)
    {
        if (outputEvent.IsUnknown)
            return "未识别";

        if (outputEvent.IsError)
            return "错误";

        return outputEvent.EventType switch
        {
            "init" => "初始化",
            "message" or "assistant" or "assistant:message" => "回复",
            "tool_use" => "工具调用",
            "tool_result" => "工具结果",
            "result" => "完成",
            "error" => "错误",
            "system" => "系统",
            _ => "事件"
        };
    }

    #region Private Parsing Methods

    private void ParseInitEvent(JsonElement root, CliOutputEvent outputEvent)
    {
        outputEvent.Title = "会话初始化";

        // 提取会话ID
        if (root.TryGetProperty("session_id", out var sessionIdElement))
        {
            outputEvent.SessionId = sessionIdElement.GetString();
        }
        else if (root.TryGetProperty("conversation_id", out var convIdElement))
        {
            outputEvent.SessionId = convIdElement.GetString();
        }

        var contentBuilder = new StringBuilder();
        if (!string.IsNullOrEmpty(outputEvent.SessionId))
        {
            contentBuilder.AppendLine($"会话标识: {outputEvent.SessionId}");
        }
        else
        {
            contentBuilder.AppendLine("Claude Code 会话已启动");
        }

        if (root.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String)
        {
            var cwd = cwdEl.GetString();
            if (!string.IsNullOrWhiteSpace(cwd))
            {
                contentBuilder.AppendLine($"工作目录: {cwd}");
            }
        }

        // 提取模型信息
        if (root.TryGetProperty("model", out var modelElement))
        {
            var model = modelElement.GetString();
            if (!string.IsNullOrEmpty(model))
            {
                contentBuilder.AppendLine($"模型: {model}");
            }
        }

        outputEvent.Content = contentBuilder.ToString().TrimEnd();
    }

    private void ParseMessageEvent(JsonElement root, CliOutputEvent outputEvent)
    {
        outputEvent.Title = "消息";
        outputEvent.ItemType = "agent_message";

        // Extract session ID
        if (root.TryGetProperty("session_id", out var sessionIdElement))
        {
            outputEvent.SessionId = sessionIdElement.GetString();
        }

        // Check if there is a role field at the top level (indicating this is an assistant/user message structure)
        var role = GetStringProperty(root, "role");
        var isAssistant = !string.IsNullOrEmpty(role) && 
                          role.Equals("assistant", StringComparison.OrdinalIgnoreCase);

        // If content is an array, handle it like assistant/user events
        if (root.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.Array)
        {
            // 1) Prioritize identifying tool_use/tool_result
            foreach (var item in contentProp.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var itemType = GetStringProperty(item, "type") ?? string.Empty;

                if (string.Equals(itemType, "tool_use", StringComparison.OrdinalIgnoreCase))
                {
                    outputEvent.EventType = "tool_use";
                    ParseToolUseFromContentItem(item, outputEvent);
                    return;
                }

                if (string.Equals(itemType, "tool_result", StringComparison.OrdinalIgnoreCase))
                {
                    outputEvent.EventType = "tool_result";
                    ParseToolResultFromContentItem(item, outputEvent);
                    return;
                }
            }

            // 2) Extract text content (including text and thinking types)
            var text = ExtractTextFromContentArray(contentProp);
            if (!string.IsNullOrWhiteSpace(text))
            {
                outputEvent.EventType = isAssistant ? "assistant" : "message";
                outputEvent.Title = isAssistant ? "助手消息" : "消息";
                outputEvent.Content = text.TrimEnd();
                outputEvent.IsUnknown = false;
                return;
            }

            // 3) Fallback: no recognizable content in array
            outputEvent.Content = "消息（无可显示内容）";
            outputEvent.IsUnknown = false;
            return;
        }

        // Original logic: extract string type message content
        var content = GetStringProperty(root, "content") ??
                      GetStringProperty(root, "text") ??
                      GetStringProperty(root, "message");

        if (!string.IsNullOrEmpty(content))
        {
            outputEvent.Content = content;
        }
    }

    private void ParseAssistantMessageEvent(JsonElement root, CliOutputEvent outputEvent)
    {
        outputEvent.Title = "助手消息";
        outputEvent.ItemType = "agent_message";

        // 尝试多种内容字段
        var content = GetStringProperty(root, "content") ??
                      GetStringProperty(root, "text") ??
                      GetStringProperty(root, "message");

        // 如果内容在嵌套对象中
        if (string.IsNullOrEmpty(content) && root.TryGetProperty("message", out var messageElement) &&
            messageElement.ValueKind == JsonValueKind.Object)
        {
            content = GetStringProperty(messageElement, "content") ??
                      GetStringProperty(messageElement, "text");
        }

        outputEvent.Content = content ?? string.Empty;
    }

    private void ParseAssistantOrUserEvent(JsonElement root, CliOutputEvent outputEvent, bool isAssistant)
    {
        // 顶层就带 session_id
        if (root.TryGetProperty("session_id", out var sessionIdEl) && sessionIdEl.ValueKind == JsonValueKind.String)
        {
            outputEvent.SessionId = sessionIdEl.GetString();
        }

        if (!root.TryGetProperty("message", out var messageEl) || messageEl.ValueKind != JsonValueKind.Object)
        {
            // 退化处理：没有 message 就按原来的 assistant/system 解析
            if (isAssistant)
            {
                ParseAssistantMessageEvent(root, outputEvent);
            }
            else
            {
                outputEvent.EventType = "user";
                outputEvent.Title = "用户输入";
                outputEvent.Content = root.GetRawText();
                outputEvent.IsUnknown = false;
            }
            return;
        }

        // message.role
        var role = GetStringProperty(messageEl, "role");
        if (!string.IsNullOrEmpty(role) && role.Equals("assistant", StringComparison.OrdinalIgnoreCase))
        {
            isAssistant = true;
        }

        // message.content: [ {type:text|tool_use|tool_result, ...}, ... ]
        if (messageEl.TryGetProperty("content", out var contentArr) && contentArr.ValueKind == JsonValueKind.Array)
        {
            // 1) 优先识别 tool_use/tool_result（它们通常单独成一行）
            foreach (var item in contentArr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var itemType = GetStringProperty(item, "type") ?? string.Empty;

                if (string.Equals(itemType, "tool_use", StringComparison.OrdinalIgnoreCase))
                {
                    outputEvent.EventType = "tool_use";
                    ParseToolUseFromContentItem(item, outputEvent);
                    return;
                }

                if (string.Equals(itemType, "tool_result", StringComparison.OrdinalIgnoreCase))
                {
                    outputEvent.EventType = "tool_result";
                    ParseToolResultFromContentItem(item, outputEvent);
                    return;
                }
            }

            // 2) 聚合 text
            var text = ExtractTextFromContentArray(contentArr);
            if (!string.IsNullOrWhiteSpace(text))
            {
                outputEvent.EventType = isAssistant ? "assistant" : "user";
                outputEvent.Title = isAssistant ? "助手消息" : "用户输入";
                outputEvent.ItemType = isAssistant ? "agent_message" : "user_message";
                outputEvent.Content = text.TrimEnd();
                outputEvent.IsUnknown = false;
                return;
            }
        }

        // 兜底：没有 content 数组或数组里没有可识别内容
        outputEvent.EventType = isAssistant ? "assistant" : "user";
        outputEvent.Title = isAssistant ? "助手消息" : "用户输入";
        outputEvent.ItemType = isAssistant ? "agent_message" : "user_message";
        outputEvent.Content = messageEl.GetRawText();
        outputEvent.IsUnknown = false;
    }

    private void ParseToolUseFromContentItem(JsonElement item, CliOutputEvent outputEvent)
    {
        outputEvent.Title = "工具调用";
        outputEvent.ItemType = "command_execution";

        var toolName = GetStringProperty(item, "name") ?? GetStringProperty(item, "tool");
        var toolUseId = GetStringProperty(item, "id");
        var contentBuilder = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(toolName))
        {
            contentBuilder.AppendLine($"工具: {toolName}");
        }

        if (item.TryGetProperty("input", out var inputEl) && inputEl.ValueKind == JsonValueKind.Object)
        {
            // Claude Code 的 TodoWrite / TodoRead / TodoUpdate 等待办工具：将其渲染为 todo_list，避免 UI 只能看到原始 JSON。
            if (!string.IsNullOrWhiteSpace(toolName) &&
                (string.Equals(toolName, "TodoWrite", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(toolName, "TodoRead", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(toolName, "TodoUpdate", StringComparison.OrdinalIgnoreCase)))
            {
                outputEvent.ItemType = "todo_list";
                outputEvent.Title = "待办列表更新";
                outputEvent.Content = FormatTodoToolInput(inputEl, outputEvent);
                outputEvent.CommandExecution = null;
                outputEvent.IsUnknown = false;
                return;
            }

            // Claude Code 的 AskUserQuestion 工具：展示问题让用户选择
            if (!string.IsNullOrWhiteSpace(toolName) &&
                string.Equals(toolName, "AskUserQuestion", StringComparison.OrdinalIgnoreCase))
            {
                outputEvent.ItemType = "user_question";
                outputEvent.Title = "请回答问题";
                outputEvent.Content = FormatAskUserQuestionInput(inputEl, outputEvent, toolUseId);
                outputEvent.CommandExecution = null;
                outputEvent.IsUnknown = false;
                return;
            }

            var command = GetStringProperty(inputEl, "command");
            if (!string.IsNullOrWhiteSpace(command))
            {
                contentBuilder.AppendLine($"命令: {command}");
            }

            outputEvent.CommandExecution = new CliCommandExecution
            {
                Command = command ?? toolName,
                Status = "executing"
            };
        }

        outputEvent.Content = contentBuilder.Length > 0 ? contentBuilder.ToString().TrimEnd() : "工具调用中...";
        outputEvent.IsUnknown = false;
    }

    private static string FormatAskUserQuestionInput(JsonElement inputEl, CliOutputEvent outputEvent, string? toolUseId)
    {
        outputEvent.UserQuestion = new CliUserQuestion
        {
            ToolUseId = toolUseId,
            Questions = new List<CliQuestionItem>()
        };
        var sb = new StringBuilder();

        // AskUserQuestion 结构：{ questions: [ { header, question, multiSelect, options: [ { label, description } ] } ] }
        if (inputEl.TryGetProperty("questions", out var questionsEl) && questionsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var question in questionsEl.EnumerateArray())
            {
                if (question.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var questionItem = new CliQuestionItem
                {
                    Header = GetStringProperty(question, "header"),
                    Question = GetStringProperty(question, "question"),
                    MultiSelect = false,
                    Options = new List<CliQuestionOption>()
                };

                // 解析 multiSelect
                if (question.TryGetProperty("multiSelect", out var multiSelectEl) && 
                    multiSelectEl.ValueKind == JsonValueKind.True)
                {
                    questionItem.MultiSelect = true;
                }

                // 解析选项
                if (question.TryGetProperty("options", out var optionsEl) && optionsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var option in optionsEl.EnumerateArray())
                    {
                        if (option.ValueKind != JsonValueKind.Object)
                        {
                            continue;
                        }

                        questionItem.Options.Add(new CliQuestionOption
                        {
                            Label = GetStringProperty(option, "label"),
                            Description = GetStringProperty(option, "description")
                        });
                    }
                }

                outputEvent.UserQuestion.Questions.Add(questionItem);

                // 构建显示文本
                if (!string.IsNullOrWhiteSpace(questionItem.Header))
                {
                    sb.AppendLine($"**{questionItem.Header}**");
                }
                if (!string.IsNullOrWhiteSpace(questionItem.Question))
                {
                    sb.AppendLine(questionItem.Question);
                }
                if (questionItem.Options.Count > 0)
                {
                    sb.AppendLine();
                    foreach (var opt in questionItem.Options)
                    {
                        var optText = $"- {opt.Label}";
                        if (!string.IsNullOrWhiteSpace(opt.Description))
                        {
                            optText += $": {opt.Description}";
                        }
                        sb.AppendLine(optText);
                    }
                }
                sb.AppendLine();
            }

            return sb.Length > 0 ? sb.ToString().TrimEnd() : "请选择一个选项";
        }

        return inputEl.GetRawText();
    }

    private static string FormatTodoToolInput(JsonElement inputEl, CliOutputEvent outputEvent)
    {
        outputEvent.TodoItems = new List<CliTodoItem>();
        var sb = new StringBuilder();

        // Claude Code TodoWrite 常见结构：{ todos: [ { content, activeForm, status }, ... ] }
        if (inputEl.TryGetProperty("todos", out var todosEl) && todosEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var todo in todosEl.EnumerateArray())
            {
                if (todo.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var title = GetStringProperty(todo, "activeForm");
                if (string.IsNullOrWhiteSpace(title))
                {
                    title = GetStringProperty(todo, "content") ?? GetStringProperty(todo, "text") ?? string.Empty;
                }

                var status = GetStringProperty(todo, "status") ?? string.Empty;
                outputEvent.TodoItems.Add(new CliTodoItem
                {
                    Title = title,
                    Status = status
                });

                var statusIcon = status switch
                {
                    var s when string.Equals(s, "completed", StringComparison.OrdinalIgnoreCase) => "✓",
                    var s when string.Equals(s, "in_progress", StringComparison.OrdinalIgnoreCase) => "◐",
                    var s when string.Equals(s, "pending", StringComparison.OrdinalIgnoreCase) => "○",
                    _ => "○"
                };

                sb.AppendLine($"{statusIcon} {title}".TrimEnd());
            }

            return sb.Length > 0 ? sb.ToString().TrimEnd() : "待办列表已更新";
        }

        // 兜底：兼容 items 结构（与 Codex todo_list 类似）
        if (inputEl.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var todo in itemsEl.EnumerateArray())
            {
                if (todo.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var title = GetStringProperty(todo, "title") ?? GetStringProperty(todo, "text") ?? string.Empty;
                var status = GetStringProperty(todo, "status") ?? string.Empty;

                outputEvent.TodoItems.Add(new CliTodoItem
                {
                    Title = title,
                    Status = status
                });

                var statusIcon = status switch
                {
                    var s when string.Equals(s, "completed", StringComparison.OrdinalIgnoreCase) => "✓",
                    var s when string.Equals(s, "in_progress", StringComparison.OrdinalIgnoreCase) => "◐",
                    var s when string.Equals(s, "pending", StringComparison.OrdinalIgnoreCase) => "○",
                    _ => "○"
                };

                sb.AppendLine($"{statusIcon} {title}".TrimEnd());
            }

            return sb.Length > 0 ? sb.ToString().TrimEnd() : "待办列表已更新";
        }

        return inputEl.GetRawText();
    }

    private void ParseToolResultFromContentItem(JsonElement item, CliOutputEvent outputEvent)
    {
        outputEvent.Title = "工具结果";
        outputEvent.ItemType = "command_execution";

        var isError = false;
        if (item.TryGetProperty("is_error", out var isErrEl) && isErrEl.ValueKind == JsonValueKind.True)
        {
            isError = true;
        }

        var content = GetStringProperty(item, "content") ?? GetStringProperty(item, "output") ?? string.Empty;
        outputEvent.Content = content;
        outputEvent.IsError = isError;
        outputEvent.IsUnknown = false;

        outputEvent.CommandExecution = new CliCommandExecution
        {
            Output = content,
            Status = isError ? "failed" : "completed"
        };
    }

    private static string ExtractTextFromContentArray(JsonElement contentArr)
    {
        var sb = new StringBuilder();
        foreach (var item in contentArr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var itemType = GetStringProperty(item, "type") ?? string.Empty;
            
            // Handle type="text" content
            if (string.Equals(itemType, "text", StringComparison.OrdinalIgnoreCase))
            {
                var text = GetStringProperty(item, "text");
                if (!string.IsNullOrEmpty(text))
                {
                    sb.Append(text);
                }
                continue;
            }
            
            // Handle type="thinking" content
            if (string.Equals(itemType, "thinking", StringComparison.OrdinalIgnoreCase))
            {
                var thinking = GetStringProperty(item, "thinking");
                if (!string.IsNullOrEmpty(thinking))
                {
                    // Add thinking content, optionally with identifier
                    if (sb.Length > 0)
                    {
                        sb.AppendLine();
                    }
                    sb.Append(thinking);
                }
                continue;
            }
        }
        return sb.ToString();
    }

    private void ParseToolUseEvent(JsonElement root, CliOutputEvent outputEvent)
    {
        outputEvent.Title = "工具调用";
        outputEvent.ItemType = "tool_call";

        var contentBuilder = new StringBuilder();

        // 提取工具名称
        var toolName = GetStringProperty(root, "name") ?? GetStringProperty(root, "tool");
        if (!string.IsNullOrEmpty(toolName))
        {
            contentBuilder.AppendLine($"工具: {toolName}");
        }

        // 提取工具输入
        if (root.TryGetProperty("input", out var inputElement))
        {
            if (inputElement.ValueKind == JsonValueKind.Object)
            {
                contentBuilder.AppendLine("输入:");
                contentBuilder.AppendLine(inputElement.GetRawText());
            }
            else if (inputElement.ValueKind == JsonValueKind.String)
            {
                contentBuilder.AppendLine($"输入: {inputElement.GetString()}");
            }
        }

        outputEvent.Content = contentBuilder.ToString().TrimEnd();

        // 如果是命令执行工具
        if (toolName?.Contains("bash", StringComparison.OrdinalIgnoreCase) == true ||
            toolName?.Contains("command", StringComparison.OrdinalIgnoreCase) == true)
        {
            outputEvent.ItemType = "command_execution";
            outputEvent.CommandExecution = new CliCommandExecution
            {
                Command = GetStringProperty(root, "input") ?? GetInputCommand(root),
                Status = "executing"
            };
        }
    }

    private void ParseToolResultEvent(JsonElement root, CliOutputEvent outputEvent)
    {
        outputEvent.Title = "工具结果";
        outputEvent.ItemType = "tool_result";

        var content = GetStringProperty(root, "output") ??
                      GetStringProperty(root, "result") ??
                      GetStringProperty(root, "content");

        outputEvent.Content = content ?? "工具执行完成";

        // 提取退出代码
        if (root.TryGetProperty("exit_code", out var exitCodeElement) && 
            exitCodeElement.ValueKind == JsonValueKind.Number)
        {
            outputEvent.CommandExecution = new CliCommandExecution
            {
                Output = content,
                ExitCode = exitCodeElement.GetInt32(),
                Status = "completed"
            };
        }
    }

    private void ParseResultEvent(JsonElement root, CliOutputEvent outputEvent)
    {
        outputEvent.Title = "执行完成";

        var contentBuilder = new StringBuilder();
        contentBuilder.AppendLine("Claude Code 执行已完成。");

        // 提取最终结果
        var result = GetStringProperty(root, "result") ?? GetStringProperty(root, "content");
        if (!string.IsNullOrEmpty(result))
        {
            contentBuilder.AppendLine();
            contentBuilder.AppendLine(result);
        }

        // 提取使用统计
        if (root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object)
        {
            outputEvent.Usage = new CliOutputEventUsage
            {
                InputTokens = GetLongProperty(usageElement, "input_tokens"),
                OutputTokens = GetLongProperty(usageElement, "output_tokens")
            };
        }

        outputEvent.Content = contentBuilder.ToString().TrimEnd();
    }

    private void ParseErrorEvent(JsonElement root, CliOutputEvent outputEvent)
    {
        outputEvent.Title = "错误";
        outputEvent.IsError = true;

        var errorMessage = GetStringProperty(root, "message") ??
                           GetStringProperty(root, "error") ??
                           GetStringProperty(root, "content");

        if (!string.IsNullOrEmpty(errorMessage))
        {
            outputEvent.ErrorMessage = errorMessage;
            outputEvent.Content = errorMessage;
        }
        else
        {
            outputEvent.Content = "发生未知错误";
        }

        // 提取错误代码
        if (root.TryGetProperty("code", out var codeElement))
        {
            var code = codeElement.ValueKind == JsonValueKind.String
                ? codeElement.GetString()
                : codeElement.GetRawText();
            if (!string.IsNullOrEmpty(code))
            {
                outputEvent.Content += $"\n错误代码: {code}";
            }
        }
    }

    private void ParseSystemEvent(JsonElement root, CliOutputEvent outputEvent)
    {
        outputEvent.Title = "系统消息";
        outputEvent.ItemType = "system";

        var content = GetStringProperty(root, "message") ??
                      GetStringProperty(root, "content") ??
                      GetStringProperty(root, "text");

        outputEvent.Content = content ?? "系统消息";
    }

    private void ParseGenericContentEvent(JsonElement root, CliOutputEvent outputEvent)
    {
        // 通用内容解析
        var content = GetStringProperty(root, "content") ??
                      GetStringProperty(root, "text") ??
                      GetStringProperty(root, "message");

        if (!string.IsNullOrEmpty(content))
        {
            outputEvent.Content = content;
            outputEvent.ItemType = "agent_message";
        }
        else
        {
            outputEvent.IsUnknown = true;
            outputEvent.Content = "无法解析的内容";
        }
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
        {
            return prop.GetString();
        }
        return null;
    }

    private static long GetLongProperty(JsonElement element, string propertyName)
    {
        if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.Number)
        {
            return prop.GetInt64();
        }
        return 0;
    }

    private static string? GetInputCommand(JsonElement root)
    {
        if (root.TryGetProperty("input", out var inputElement) && inputElement.ValueKind == JsonValueKind.Object)
        {
            if (inputElement.TryGetProperty("command", out var cmdElement) && cmdElement.ValueKind == JsonValueKind.String)
            {
                return cmdElement.GetString();
            }
        }
        return null;
    }

    private static string EscapeShellArgument(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        // 转义双引号和反斜杠
        return input
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }

    #endregion
}

