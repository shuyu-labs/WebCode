using System.Text.Json;
using FeishuNetSdk.CallbackEvents;
using FeishuNetSdk.Extensions;
using FeishuNetSdk.Im.Dtos;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Domain.Domain.Service.Channels;

/// <summary>
/// 飞书帮助卡片构建器
/// 构建各类帮助卡片的JSON
/// </summary>
public class FeishuHelpCardBuilder
{
    // ========== 新的实现：使用 ElementsCardV2Dto 和 SetCard 扩展方法 ==========

    /// <summary>
    /// 构建命令选择卡片（卡片1）- 使用 ElementsCardV2Dto
    /// </summary>
    public ElementsCardV2Dto BuildCommandListCardV2(
        List<FeishuCommandCategory> categories,
        bool showRefreshButton = true,
        bool replyTtsEnabled = false,
        bool showGoalQuickActionButtons = true,
        bool showSuperpowersQuickActions = true)
    {
        var elements = new List<object>();

        // 顶部操作按钮组
        if (showRefreshButton)
        {
            elements.Add(new
            {
                tag = "column_set",
                flex_mode = "none",
                background_style = "default",
                columns = new[]
                {
                    new
                    {
                        tag = "column",
                        width = "weighted",
                        weight = 1,
                        vertical_align = "top",
                        elements = new[]
                        {
                            new
                            {
                                tag = "button",
                                text = new { tag = "plain_text", content = "🔄 更新命令列表" },
                                type = "default",
                                behaviors = new[]
                                {
                                    new
                                    {
                                        type = "callback",
                                        value = new { action = "refresh_commands" }
                                    }
                                }
                            }
                        }
                    },
                    new
                    {
                        tag = "column",
                        width = "weighted",
                        weight = 1,
                        vertical_align = "top",
                        elements = new[]
                        {
                            new
                            {
                                tag = "button",
                                text = new { tag = "plain_text", content = "📋 会话管理" },
                                type = "primary",
                                behaviors = new[]
                                {
                                    new
                                    {
                                        type = "callback",
                                        value = new { action = "open_session_manager" }
                                    }
                                }
                            }
                        }
                    }
                }
            });

            elements.Add(new
            {
                tag = "column_set",
                flex_mode = "none",
                background_style = "default",
                columns = new[]
                {
                    BuildTopActionColumn(
                        $"语音回复：{(replyTtsEnabled ? "开" : "关")}",
                        replyTtsEnabled ? "primary" : "default",
                        new { action = FeishuHelpCardAction.ToggleReplyTtsAction })
                }
            });
        }

        // 每个分组显示为分类按钮，避免首页元素超限
        foreach (var category in categories)
        {
            if (category.Commands.Count == 0)
                continue;

            elements.Add(BuildCategoryActionRow(category));
        }

        AppendSuperpowersQuickActionElements(elements, showGoalQuickActionButtons, showSuperpowersQuickActions);

        return new ElementsCardV2Dto
        {
            Header = new ElementsCardV2Dto.HeaderSuffix
            {
                Template = "blue",
                Title = new HeaderTitleElement { Content = "🤖 当前 CLI 命令帮助" }
            },
            Config = new ElementsCardV2Dto.ConfigSuffix
            {
                EnableForward = true,
                UpdateMulti = true
            },
            Body = new ElementsCardV2Dto.BodySuffix
            {
                Elements = elements.ToArray()
            }
        };
    }

    /// <summary>
    /// 构建过滤后的搜索卡片 - 使用 ElementsCardV2Dto
    /// </summary>
    public ElementsCardV2Dto BuildFilteredCardV2(
        List<FeishuCommandCategory> categories,
        string keyword,
        bool showGoalQuickActionButtons = true,
        bool showSuperpowersQuickActions = true)
    {
        var elements = new List<object>
        {
            new
            {
                tag = "div",
                text = new
                {
                    tag = "lark_md",
                    content = $"🔍 搜索结果：**{keyword}**\n\n点击下方按钮返回完整列表"
                }
            },
            new
            {
                tag = "column_set",
                flex_mode = "none",
                background_style = "default",
                columns = new[]
                {
                    new
                    {
                        tag = "column",
                        width = "weighted",
                        weight = 1,
                        vertical_align = "top",
                        elements = new[]
                        {
                            new
                            {
                                tag = "button",
                                text = new { tag = "plain_text", content = "📋 显示全部命令" },
                                type = "default",
                                behaviors = new[]
                                {
                                    new
                                    {
                                        type = "callback",
                                        value = new { action = "back_to_list" }
                                    }
                                }
                            }
                        }
                    }
                }
            },
            new { tag = "hr" }
        };

        var allCommands = categories.SelectMany(c => c.Commands).ToList();
        if (allCommands.Count > 0)
        {
            elements.Add(new
            {
                tag = "div",
                text = new { tag = "lark_md", content = $"**找到 {allCommands.Count} 个匹配命令：**" }
            });

            foreach (var command in allCommands)
            {
                elements.Add(BuildCommandActionRow(command));
            }
        }
        else
        {
            elements.Add(new
            {
                tag = "div",
                text = new { tag = "lark_md", content = "❌ 未找到匹配的命令，请尝试其他关键词" }
            });
        }

        AppendSuperpowersQuickActionElements(elements, showGoalQuickActionButtons, showSuperpowersQuickActions);

        return new ElementsCardV2Dto
        {
            Header = new ElementsCardV2Dto.HeaderSuffix
            {
                Template = "turquoise",
                Title = new HeaderTitleElement { Content = "🔍 命令搜索" }
            },
            Config = new ElementsCardV2Dto.ConfigSuffix
            {
                EnableForward = true,
                UpdateMulti = true
            },
            Body = new ElementsCardV2Dto.BodySuffix
            {
                Elements = elements.ToArray()
            }
        };
    }

    /// <summary>
    /// 构建分类命令卡片（卡片1-2）- 展示某个分类下的命令按钮
    /// </summary>
    public ElementsCardV2Dto BuildCategoryCommandsCardV2(
        FeishuCommandCategory category,
        bool showGoalQuickActionButtons = true,
        bool showSuperpowersQuickActions = true)
    {
        var elements = new List<object>
        {
            new
            {
                tag = "div",
                text = new
                {
                    tag = "lark_md",
                    content = $"**{SanitizeMarkdown(category.Name)}**\n共 {category.Commands.Count} 条命令，点击右侧按钮进入执行卡片"
                }
            },
            new
            {
                tag = "column_set",
                flex_mode = "none",
                background_style = "default",
                columns = new[]
                {
                    new
                    {
                        tag = "column",
                        width = "weighted",
                        weight = 1,
                        vertical_align = "top",
                        elements = new[]
                        {
                            new
                            {
                                tag = "button",
                                text = new { tag = "plain_text", content = "📋 返回分类列表" },
                                type = "default",
                                behaviors = new[]
                                {
                                    new
                                    {
                                        type = "callback",
                                        value = new { action = "back_to_list" }
                                    }
                                }
                            }
                        }
                    }
                }
            },
            new { tag = "hr" }
        };

        foreach (var command in category.Commands)
        {
            elements.Add(BuildCommandActionRow(command));
        }

        AppendSuperpowersQuickActionElements(elements, showGoalQuickActionButtons, showSuperpowersQuickActions);

        return new ElementsCardV2Dto
        {
            Header = new ElementsCardV2Dto.HeaderSuffix
            {
                Template = "blue",
                Title = new HeaderTitleElement { Content = $"📁 {category.Name}" }
            },
            Config = new ElementsCardV2Dto.ConfigSuffix
            {
                EnableForward = true,
                UpdateMulti = true
            },
            Body = new ElementsCardV2Dto.BodySuffix
            {
                Elements = elements.ToArray()
            }
        };
    }

    /// <summary>
    /// 构建执行卡片（卡片2）- 使用 ElementsCardV2Dto
    /// </summary>
    public ElementsCardV2Dto BuildExecuteCardV2(FeishuCommand command)
    {
        var elements = new List<object>
        {
            new DivElement().SetText(new LarkMdElement($"**📝 命令说明：**\n{command.Description}")),
            new DivElement().SetText(new LarkMdElement($"**💡 用法示例：**\n`{command.Usage}`")),
            new HrElement(),
            // 命令输入框
            new
            {
                tag = "input",
                input_type = "text",
                name = "command_input",
                label = new { tag = "plain_text", content = "点编辑后enter发送" },
                placeholder = new { tag = "plain_text", content = "编辑命令..." },
                default_value = string.IsNullOrWhiteSpace(command.ExecuteText) ? command.Name : command.ExecuteText,
                behaviors = new[]
                {
                    new{
                        type = "callback",
                        value = new {
                            // 回传交互数据。支持 object 数据类型。开放平台 SDK 仅支持 object 类型的回传交互数据。
                            action = "execute_command" // 自定义的请求回调交互参数。
                        }
                    }
                }
            },
            new ColumnSetElement
            {
                FlexMode = "none",
                BackgroundStyle = "default",
                Columns = new[]
                {
                    new ColumnElement
                    {
                        Width = "weighted",
                        Weight = 1,
                        VerticalAlign = "top",
                        Elements = new Element[]
                        {
                            new FormButtonElement(
                                Name: "cancel_btn",
                                Text: new PlainTextElement("❌ 返回"),
                                Type: "default",
                                ActionType: "request")
                            {
                                Behaviors = new Behaviors[]
                                {
                                    new CallbackBehaviors(new { action = "back_to_list" })
                                }
                            }
                        }
                    }
                }
            }
        };

        return new ElementsCardV2Dto
        {
            Header = new ElementsCardV2Dto.HeaderSuffix
            {
                Template = "purple",
                Title = new HeaderTitleElement { Content = $"⚙️ 执行命令：{command.Name}" }
            },
            Config = new ElementsCardV2Dto.ConfigSuffix
            {
                EnableForward = true,
                UpdateMulti = true
            },
            Body = new ElementsCardV2Dto.BodySuffix
            {
                Elements = elements.ToArray()
            }
        };
    }

    public ElementsCardV2Dto BuildBindWebUserCardV2(string[] bindableUsernames)
    {
        var hint = bindableUsernames.Length > 0
            ? $"可绑定用户：{string.Join("、", bindableUsernames)}"
            : "请联系管理员确认可用的 Web 用户名";

        var elements = new List<object>
        {
            new
            {
                tag = "div",
                text = new
                {
                    tag = "lark_md",
                    content = $"## 🔐 绑定 Web 用户\n绑定后，飞书将共享 Web 端的会话、项目和工作区。\n\n{hint}"
                }
            },
            new { tag = "hr" },
            new
            {
                tag = "form",
                name = "bind_web_user_form",
                elements = new object[]
                {
                    new
                    {
                        tag = "input",
                        input_type = "text",
                        name = "web_username",
                        label = new { tag = "plain_text", content = "Web 用户名" },
                        placeholder = new { tag = "plain_text", content = "请输入 Web 用户名" }
                    },
                    new
                    {
                        tag = "input",
                        input_type = "password",
                        name = "web_password",
                        label = new { tag = "plain_text", content = "Web 密码" },
                        placeholder = new { tag = "plain_text", content = "请输入 Web 密码" }
                    },
                    new
                    {
                        tag = "column_set",
                        flex_mode = "none",
                        background_style = "default",
                        horizontal_spacing = "default",
                        columns = new object[]
                        {
                            new
                            {
                                tag = "column",
                                width = "auto",
                                vertical_align = "top",
                                elements = new object[]
                                {
                                    new
                                    {
                                        tag = "button",
                                        text = new { tag = "plain_text", content = "绑定" },
                                        type = "primary",
                                        action_type = "form_submit",
                                        name = "bind_web_user_submit",
                                        value = new { action = "bind_web_user" }
                                    }
                                }
                            },
                            new
                            {
                                tag = "column",
                                width = "auto",
                                vertical_align = "top",
                                elements = new object[]
                                {
                                    new
                                    {
                                        tag = "button",
                                        text = new { tag = "plain_text", content = "取消" },
                                        type = "default",
                                        action_type = "form_reset",
                                        name = "bind_web_user_reset"
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        return new ElementsCardV2Dto
        {
            Header = new ElementsCardV2Dto.HeaderSuffix
            {
                Template = "blue",
                Title = new HeaderTitleElement { Content = "🔐 绑定 Web 用户" }
            },
            Config = new ElementsCardV2Dto.ConfigSuffix
            {
                EnableForward = true,
                UpdateMulti = true
            },
            Body = new ElementsCardV2Dto.BodySuffix
            {
                Elements = elements.ToArray()
            }
        };
    }

    public string BuildBindWebUserCard(string[] bindableUsernames)
    {
        var hint = bindableUsernames.Length > 0
            ? $"可绑定用户：{string.Join("、", bindableUsernames)}"
            : "请联系管理员确认可用的 Web 用户名";

        var card = new
        {
            schema = "2.0",
            config = new { enable_forward = true, update_multi = true },
            header = new
            {
                template = "blue",
                title = new { tag = "plain_text", content = "🔐 绑定 Web 用户" }
            },
            body = new
            {
                elements = new object[]
                {
                    new
                    {
                        tag = "div",
                        text = new
                        {
                            tag = "lark_md",
                            content = $"## 🔐 绑定 Web 用户\n绑定后，飞书将共享 Web 端的会话、项目和工作区。\n\n{hint}"
                        }
                    },
                    new { tag = "hr" },
                    new
                    {
                        tag = "form",
                        name = "bind_web_user_form",
                        elements = new object[]
                        {
                            new
                            {
                                tag = "input",
                                input_type = "text",
                                name = "web_username",
                                label = new { tag = "plain_text", content = "Web 用户名" },
                                placeholder = new { tag = "plain_text", content = "请输入 Web 用户名" }
                            },
                            new
                            {
                                tag = "input",
                                input_type = "password",
                                name = "web_password",
                                label = new { tag = "plain_text", content = "Web 密码" },
                                placeholder = new { tag = "plain_text", content = "请输入 Web 密码" }
                            },
                            new
                            {
                                tag = "column_set",
                                flex_mode = "none",
                                background_style = "default",
                                horizontal_spacing = "default",
                                columns = new object[]
                                {
                                    new
                                    {
                                        tag = "column",
                                        width = "auto",
                                        vertical_align = "top",
                                        elements = new object[]
                                        {
                                            new
                                            {
                                                tag = "button",
                                                text = new { tag = "plain_text", content = "绑定" },
                                                type = "primary",
                                                action_type = "form_submit",
                                                name = "bind_web_user_submit",
                                                value = new { action = "bind_web_user" }
                                            }
                                        }
                                    },
                                    new
                                    {
                                        tag = "column",
                                        width = "auto",
                                        vertical_align = "top",
                                        elements = new object[]
                                        {
                                            new
                                            {
                                                tag = "button",
                                                text = new { tag = "plain_text", content = "取消" },
                                                type = "default",
                                                action_type = "form_reset",
                                                name = "bind_web_user_reset"
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        };

        return JsonSerializer.Serialize(card);
    }

    /// <summary>
    /// 构建SDK类型的卡片响应（用于回调）- 使用 SetCard 扩展方法
    /// </summary>
    public CardActionTriggerResponseDto BuildCardActionResponseV2(ElementsCardV2Dto card, string toastMessage, string toastType = "info")
    {
        var response = new CardActionTriggerResponseDto();

        if (!string.IsNullOrEmpty(toastMessage))
        {
            response.Toast = new CardActionTriggerResponseDto.ToastSuffix
            {
                Content = toastMessage,
                Type = toastType switch
                {
                    "success" => CardActionTriggerResponseDto.ToastSuffix.ToastType.Success,
                    "error" => CardActionTriggerResponseDto.ToastSuffix.ToastType.Error,
                    "warning" => CardActionTriggerResponseDto.ToastSuffix.ToastType.Warning,
                    _ => CardActionTriggerResponseDto.ToastSuffix.ToastType.Info
                }
            };
        }

        if (card != null)
        {
            response.SetCard(card);
        }

        return response;
    }

    /// <summary>
    /// 构建SDK类型的仅Toast响应（用于回调）
    /// </summary>
    public CardActionTriggerResponseDto BuildCardActionToastOnlyResponse(string toastMessage, string toastType = "info")
    {
        return BuildCardActionResponseV2(null!, toastMessage, toastType);
    }

    public ElementsCardV2Dto BuildSuperpowersSessionMismatchConfirmCardV2(
        string boundSessionId,
        string currentSessionId,
        string chatKey,
        string? toolId,
        string action)
    {
        var elements = new List<object>
        {
            new
            {
                tag = "div",
                text = new
                {
                    tag = "lark_md",
                    content =
                        "⚠️ 当前激活会话已经变化，已暂停直接执行。\n\n" +
                        $"卡片绑定会话：`{boundSessionId}`\n" +
                        $"当前激活会话：`{currentSessionId}`\n\n" +
                        "请选择接下来要对哪个会话执行这次 Superpowers 操作。"
                }
            },
            new { tag = "hr" },
            new
            {
                tag = "column_set",
                flex_mode = "none",
                columns = new object[]
                {
                    BuildTopActionColumn(
                        "继续原会话",
                        "default",
                        new
                        {
                            action = FeishuHelpCardAction.ConfirmBoundSuperpowersAction,
                            session_id = boundSessionId,
                            chat_key = chatKey,
                            tool_id = toolId,
                            command = action
                        }),
                    BuildTopActionColumn(
                        "改为当前会话",
                        "primary",
                        new
                        {
                            action = FeishuHelpCardAction.ConfirmCurrentSuperpowersAction,
                            session_id = currentSessionId,
                            chat_key = chatKey,
                            tool_id = toolId,
                            command = action
                        })
                }
            }
        };

        return new ElementsCardV2Dto
        {
            Header = new ElementsCardV2Dto.HeaderSuffix
            {
                Template = "orange",
                Title = new HeaderTitleElement { Content = "确认 Superpowers 会话" }
            },
            Config = new ElementsCardV2Dto.ConfigSuffix
            {
                EnableForward = true,
                UpdateMulti = true
            },
            Body = new ElementsCardV2Dto.BodySuffix
            {
                Elements = elements.ToArray()
            }
        };
    }

    public ElementsCardV2Dto BuildGoalOverwriteConfirmCardV2(
        string sessionId,
        string chatKey,
        string? toolId,
        string newGoalPrompt)
    {
        var promptPreview = SanitizeMarkdown(newGoalPrompt);
        promptPreview = promptPreview.Replace("`", "'");
        if (promptPreview.Length > 180)
        {
            promptPreview = $"{promptPreview[..180]}...";
        }

        var elements = new List<object>
        {
            new
            {
                tag = "div",
                text = new
                {
                    tag = "lark_md",
                    content =
                        "⚠️ 当前 goal 仍在执行，新的提交不会直接覆盖。\n\n" +
                        $"当前会话：`{sessionId}`\n" +
                        $"待提交目标：`{promptPreview}`\n\n" +
                        "请选择接下来的操作。"
                }
            },
            new { tag = "hr" },
            new
            {
                tag = "column_set",
                flex_mode = "none",
                columns = new object[]
                {
                    BuildTopActionColumn(
                        "继续当前 goal",
                        "default",
                        new
                        {
                            action = FeishuHelpCardAction.ContinueCurrentGoalAction,
                            session_id = sessionId,
                            chat_key = chatKey,
                            tool_id = toolId
                        }),
                    BuildTopActionColumn(
                        "中断并覆盖",
                        "primary",
                        new
                        {
                            action = FeishuHelpCardAction.ConfirmOverwriteGoalAction,
                            session_id = sessionId,
                            chat_key = chatKey,
                            tool_id = toolId,
                            command = newGoalPrompt
                        }),
                    BuildTopActionColumn(
                        "查看当前状态",
                        "default",
                        new
                        {
                            action = FeishuHelpCardAction.StatusGoalAction,
                            session_id = sessionId,
                            chat_key = chatKey,
                            tool_id = toolId
                        })
                }
            }
        };

        return new ElementsCardV2Dto
        {
            Header = new ElementsCardV2Dto.HeaderSuffix
            {
                Template = "orange",
                Title = new HeaderTitleElement { Content = "确认覆盖 Goal" }
            },
            Config = new ElementsCardV2Dto.ConfigSuffix
            {
                EnableForward = true,
                UpdateMulti = true
            },
            Body = new ElementsCardV2Dto.BodySuffix
            {
                Elements = elements.ToArray()
            }
        };
    }

    // ========== 原来的实现（用于初始发送卡片） ==========

    /// <summary>
    /// 构建命令选择卡片（卡片1）- 用于初始发送
    /// </summary>
    /// <param name="categories">命令分组列表</param>
    /// <param name="showRefreshButton">是否显示刷新按钮</param>
    /// <returns>飞书卡片JSON</returns>
    public ElementsCardV2Dto BuildSyncSessionProviderConfirmCardV2(
        string sessionId,
        string chatKey,
        string? toolId,
        bool showAllSessions = false)
    {
        var elements = new List<object>
        {
            new
            {
                tag = "div",
                text = new
                {
                    tag = "lark_md",
                    content =
                        "⚠️ 当前 goal runtime 仍在运行，同步 Provider 不会热切当前进程。\n\n" +
                        $"当前会话：`{sessionId}`\n\n" +
                        "如果继续同步，WebCode 会先同步线程 Provider，再重启当前 goal runtime，后续新的 turn 才会使用新的 Provider。"
                }
            },
            new { tag = "hr" },
            new
            {
                tag = "column_set",
                flex_mode = "none",
                columns = new object[]
                {
                    BuildTopActionColumn(
                        "继续当前 goal",
                        "default",
                        new
                        {
                            action = "open_session_manager",
                            chat_key = chatKey,
                            show_all_sessions = showAllSessions
                        }),
                    BuildTopActionColumn(
                        "中断并同步 Provider",
                        "primary",
                        new
                        {
                            action = FeishuHelpCardAction.ConfirmSyncSessionProviderAction,
                            session_id = sessionId,
                            chat_key = chatKey,
                            tool_id = toolId,
                            show_all_sessions = showAllSessions
                        }),
                    BuildTopActionColumn(
                        "查看当前状态",
                        "default",
                        new
                        {
                            action = FeishuHelpCardAction.StatusGoalAction,
                            session_id = sessionId,
                            chat_key = chatKey,
                            tool_id = toolId
                        })
                }
            }
        };

        return new ElementsCardV2Dto
        {
            Header = new ElementsCardV2Dto.HeaderSuffix
            {
                Template = "orange",
                Title = new HeaderTitleElement { Content = "确认同步 Provider" }
            },
            Config = new ElementsCardV2Dto.ConfigSuffix
            {
                EnableForward = true,
                UpdateMulti = true
            },
            Body = new ElementsCardV2Dto.BodySuffix
            {
                Elements = elements.ToArray()
            }
        };
    }

    public string BuildCommandListCard(
        List<FeishuCommandCategory> categories,
        bool showRefreshButton = true,
        bool replyTtsEnabled = false,
        bool showGoalQuickActionButtons = true,
        bool showSuperpowersQuickActions = true)
    {
        var elements = new List<object>();

        // 顶部操作按钮组
        if (showRefreshButton)
        {
            elements.Add(new
            {
                tag = "column_set",
                flex_mode = "none",
                background_style = "default",
                columns = new[]
                {
                    new
                    {
                        tag = "column",
                        width = "weighted",
                        weight = 1,
                        vertical_align = "top",
                        elements = new[]
                        {
                            new
                            {
                                tag = "button",
                                text = new { tag = "plain_text", content = "🔄 更新命令列表" },
                                type = "default",
                                behaviors = new[]
                                {
                                    new
                                    {
                                        type = "callback",
                                        value = new { action = "refresh_commands" }
                                    }
                                }
                            }
                        }
                    },
                    new
                    {
                        tag = "column",
                        width = "weighted",
                        weight = 1,
                        vertical_align = "top",
                        elements = new[]
                        {
                            new
                            {
                                tag = "button",
                                text = new { tag = "plain_text", content = "📋 会话管理" },
                                type = "primary",
                                behaviors = new[]
                                {
                                    new
                                    {
                                        type = "callback",
                                        value = new { action = "open_session_manager" }
                                    }
                                }
                            }
                        }
                    }
                }
            });

            elements.Add(new
            {
                tag = "column_set",
                flex_mode = "none",
                background_style = "default",
                columns = new[]
                {
                    BuildTopActionColumn(
                        $"语音回复：{(replyTtsEnabled ? "开" : "关")}",
                        replyTtsEnabled ? "primary" : "default",
                        new { action = FeishuHelpCardAction.ToggleReplyTtsAction })
                }
            });
        }

        // 每个分组显示为分类按钮，避免首页元素超限
        foreach (var category in categories)
        {
            if (category.Commands.Count == 0)
                continue;

            elements.Add(BuildCategoryActionRow(category));
        }

        AppendSuperpowersQuickActionElements(elements, showGoalQuickActionButtons, showSuperpowersQuickActions);

        var card = new
        {
            schema = "2.0",
            config = new { enable_forward = true, update_multi = true },
            header = new
            {
                template = "blue",
                title = new { tag = "plain_text", content = "🤖 当前 CLI 命令帮助" }
            },
            body = new { elements = elements }
        };

        return JsonSerializer.Serialize(card);
    }

    /// <summary>
    /// 构建执行卡片（卡片2）
    /// </summary>
    /// <param name="command">选中的命令</param>
    /// <returns>飞书卡片JSON</returns>
    // public string BuildExecuteCard(FeishuCommand command)
    // {
    //     var formElements = new List<object>
    //     {
    //         new
    //         {
    //             tag = "div",
    //             text = new { tag = "lark_md", content = $"**📝 命令说明：**\n{command.Description}" }
    //         },
    //         new
    //         {
    //             tag = "div",
    //             text = new { tag = "lark_md", content = $"**💡 用法示例：**\n`{command.Usage}`" }
    //         },
    //         new { tag = "hr" },
    //         // 使用匿名对象创建输入框（因为需要 placeholder 和 default_value 属性）
    //         new
    //         {
    //             tag = "input",
    //             name = "command_input",
    //             placeholder = "编辑命令...",
    //             default_value = command.Name.StartsWith("/") ? command.Name : $"claude {command.Name}"
    //         },
    //         new
    //         {
    //             tag = "column_set",
    //             flex_mode = "none",
    //             background_style = "default",
    //             columns = new[]
    //             {
    //                 new
    //                 {
    //                     tag = "column",
    //                     width = "weighted",
    //                     weight = 1,
    //                     vertical_align = "top",
    //                     elements = new[]
    //                     {
    //                         // 使用 SDK 的表单按钮
    //                         new FormButtonElement(
    //                             Name: "cancel_button",
    //                             Text: new PlainTextElement("❌ 取消"),
    //                             Type: "default",
    //                             ActionType: "request",
    //                             Behaviors: new Behaviors[]
    //                             {
    //                                 new CallbackBehaviors(
    //                                     Value: JsonSerializer.Serialize(new { action = "back_to_list" })
    //                                 )
    //                             }
    //                         )
    //                     }
    //                 },
    //                 new
    //                 {
    //                     tag = "column",
    //                     width = "weighted",
    //                     weight = 1,
    //                     vertical_align = "top",
    //                     elements = new[]
    //                     {
    //                         // 使用 SDK 的表单按钮
    //                         new FormButtonElement(
    //                             Name: "execute_button",
    //                             Text: new PlainTextElement("✅ 执行命令"),
    //                             Type: "primary",
    //                             ActionType: "form_submit"
    //                         )
    //                     }
    //                 }
    //             }
    //         }
    //     };

    //     // 使用 SDK 的 FormContainerElement
    //     var formContainer = new FormContainerElement(
    //         Name: "execute_command_form",
    //         Elements: formElements.ToArray()
    //     );

    //     var card = new
    //     {
    //         schema = "2.0",
    //         config = new { enable_forward = true, update_multi = true },
    //         header = new
    //         {
    //             template = "purple",
    //             title = new { tag = "plain_text", content = $"⚙️ 执行命令：{command.Name}" }
    //         },
    //         body = new { elements = new object[] { formContainer } }
    //     };

    //     return JsonSerializer.Serialize(card);
    // }

    /// <summary>
    /// 构建过滤后的搜索卡片
    /// </summary>
    /// <param name="categories">过滤后的命令分组</param>
    /// <param name="keyword">搜索关键词</param>
    /// <returns>飞书卡片JSON</returns>
    public string BuildFilteredCard(
        List<FeishuCommandCategory> categories,
        string keyword,
        bool showGoalQuickActionButtons = true,
        bool showSuperpowersQuickActions = true)
    {
        var elements = new List<object>();

        // 过滤说明
        elements.Add(new
        {
            tag = "div",
            text = new { tag = "lark_md", content = $"🔍 搜索结果：**{keyword}**\n\n点击下方按钮返回完整列表" }
        });

        // 返回按钮 - 使用 column_set + button 而非 action 标签
        elements.Add(new
        {
            tag = "column_set",
            flex_mode = "none",
            background_style = "default",
            columns = new[]
            {
                new
                {
                    tag = "column",
                    width = "weighted",
                    weight = 1,
                    vertical_align = "top",
                    elements = new[]
                    {
                        new
                        {
                            tag = "button",
                            text = new { tag = "plain_text", content = "📋 显示全部命令" },
                            type = "default",
                            behaviors = new[]
                            {
                                new
                                {
                                    type = "callback",
                                    value = new { action = "back_to_list" }
                                }
                            }
                        }
                    }
                }
            }
        });

        elements.Add(new { tag = "hr" });

        // 匹配的命令（不分组，直接显示）
        var allCommands = categories.SelectMany(c => c.Commands).ToList();
        if (allCommands.Count > 0)
        {
            elements.Add(new
            {
                tag = "div",
                text = new { tag = "lark_md", content = $"**找到 {allCommands.Count} 个匹配命令：**" }
            });

            foreach (var command in allCommands)
            {
                elements.Add(BuildCommandActionRow(command));
            }
        }
        else
        {
            elements.Add(new
            {
                tag = "div",
                text = new { tag = "lark_md", content = "❌ 未找到匹配的命令，请尝试其他关键词" }
            });
        }

        AppendSuperpowersQuickActionElements(elements, showGoalQuickActionButtons, showSuperpowersQuickActions);

        var card = new
        {
            schema = "2.0",
            config = new { enable_forward = true },
            header = new
            {
                template = "turquoise",
                title = new { tag = "plain_text", content = "🔍 命令搜索" }
            },
            body = new { elements = elements }
        };

        return JsonSerializer.Serialize(card);
    }

    /// <summary>
    /// 构建带toast的卡片响应
    /// </summary>
    /// <param name="cardJson">卡片JSON</param>
    /// <param name="toastMessage">toast消息</param>
    /// <param name="toastType">toast类型</param>
    /// <returns>飞书响应对象</returns>
    private static void AppendSuperpowersQuickActionElements(
        List<object> elements,
        bool showGoalQuickActionButtons,
        bool showSuperpowersQuickActions)
    {
        elements.Add(new { tag = "hr" });
        if (showSuperpowersQuickActions)
        {
            elements.Add(BuildSuperpowersQuickInput());
        }
        elements.Add(BuildGoalQuickInput());
        if (showGoalQuickActionButtons)
        {
            elements.AddRange(BuildGoalQuickActions());
        }
    }

    private static object BuildSuperpowersQuickInput()
    {
        return new
        {
            tag = "input",
            input_type = "text",
            name = SuperpowersQuickActionDefaults.QuickInputFieldName,
            label = new { tag = "plain_text", content = SuperpowersQuickActionDefaults.InstructionText },
            placeholder = new { tag = "plain_text", content = SuperpowersQuickActionDefaults.QuickInputPlaceholder },
            behaviors = new[]
            {
                new
                {
                    type = "callback",
                    value = new
                    {
                        action = FeishuHelpCardAction.SubmitSuperpowersQuickInputAction
                    }
                }
            }
        };
    }

    private static object BuildGoalQuickInput()
    {
        return new
        {
            tag = "input",
            input_type = "text",
            name = GoalQuickActionDefaults.QuickInputFieldName,
            label = new { tag = "plain_text", content = GoalQuickActionDefaults.InstructionText },
            placeholder = new { tag = "plain_text", content = GoalQuickActionDefaults.QuickInputPlaceholder },
            behaviors = new[]
            {
                new
                {
                    type = "callback",
                    value = new
                    {
                        action = FeishuHelpCardAction.SubmitGoalQuickInputAction
                    }
                }
            }
        };
    }

    private static IEnumerable<object> BuildGoalQuickActions()
    {
        return
        [
            BuildGoalQuickActionRow(
                BuildGoalQuickActionColumn(GoalQuickActionDefaults.StatusButtonText, FeishuHelpCardAction.StatusGoalAction),
                BuildGoalQuickActionColumn(GoalQuickActionDefaults.PauseButtonText, FeishuHelpCardAction.PauseGoalAction)),
            BuildGoalQuickActionRow(
                BuildGoalQuickActionColumn(GoalQuickActionDefaults.ClearButtonText, FeishuHelpCardAction.ClearGoalAction),
                BuildGoalQuickActionColumn(GoalQuickActionDefaults.ResumeButtonText, FeishuHelpCardAction.ResumeGoalAction, "primary"))
        ];
    }

    private static object BuildGoalQuickActionRow(params object[] columns)
    {
        return new
        {
            tag = "column_set",
            flex_mode = "none",
            columns
        };
    }

    private static object BuildGoalQuickActionColumn(string text, string action, string type = "default")
    {
        return new
        {
            tag = "column",
            width = "weighted",
            weight = 1,
            elements = new object[]
            {
                new
                {
                    tag = "button",
                    text = new
                    {
                        tag = "plain_text",
                        content = text
                    },
                    type,
                    behaviors = new[]
                    {
                        new
                        {
                            type = "callback",
                            value = new
                            {
                                action
                            }
                        }
                    }
                }
            }
        };
    }

    public object BuildToastResponse(string cardJson, string toastMessage, string toastType = "info")
    {
        return new
        {
            toast = new
            {
                type = toastType,
                content = toastMessage
            },
            card = new
            {
                type = "raw",
                data = JsonSerializer.Deserialize<Dictionary<string, object>>(cardJson)
            }
        };
    }

    /// <summary>
    /// 构建仅toast的响应
    /// </summary>
    /// <param name="toastMessage">toast消息</param>
    /// <param name="toastType">toast类型</param>
    /// <returns>飞书响应对象</returns>
    public object BuildToastOnlyResponse(string toastMessage, string toastType = "info")
    {
        return new
        {
            toast = new
            {
                type = toastType,
                content = toastMessage
            }
        };
    }

    private static string BuildCategoryPreviewMarkdown(FeishuCommandCategory category)
    {
        var lines = category.Commands
            .Take(5)
            .Select(cmd => $"- `{SanitizeMarkdown(cmd.Name)}`：{SanitizeMarkdown(cmd.Description)}")
            .ToList();

        if (category.Commands.Count > 5)
        {
            lines.Add($"- ... 共 {category.Commands.Count} 条，可点右侧选择");
        }

        var detail = lines.Count > 0 ? "\n" + string.Join("\n", lines) : string.Empty;
        return $"**{SanitizeMarkdown(category.Name)}**{detail}";
    }

    private static object BuildCategoryActionRow(FeishuCommandCategory category)
    {
        return new
        {
            tag = "column_set",
            flex_mode = "none",
            background_style = "default",
            columns = new object[]
            {
                new
                {
                    tag = "column",
                    width = "weighted",
                    weight = 5,
                    vertical_align = "top",
                    elements = new object[]
                    {
                        new
                        {
                            tag = "div",
                            text = new
                            {
                                tag = "lark_md",
                                content = $"**{SanitizeMarkdown(category.Name)}**\n共 {category.Commands.Count} 条命令"
                            }
                        }
                    }
                },
                new
                {
                    tag = "column",
                    width = "auto",
                    vertical_align = "top",
                    elements = new object[]
                    {
                        new
                        {
                            tag = "button",
                            text = new { tag = "plain_text", content = "查看" },
                            type = "default",
                            behaviors = new[]
                            {
                                new
                                {
                                    type = "callback",
                                    value = new { action = "show_category", category_id = category.Id }
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    private static object BuildTopActionColumn(string text, string type, object value)
    {
        return new
        {
            tag = "column",
            width = "weighted",
            weight = 1,
            vertical_align = "top",
            elements = new object[]
            {
                new
                {
                    tag = "button",
                    text = new { tag = "plain_text", content = text },
                    type,
                    behaviors = new[]
                    {
                        new
                        {
                            type = "callback",
                            value
                        }
                    }
                }
            }
        };
    }

    private static object BuildCommandActionRow(FeishuCommand command)
    {
        var description = SanitizeMarkdown(command.Description);
        if (description.Length > 80)
        {
            description = description[..80] + "...";
        }

        var markdown = string.IsNullOrWhiteSpace(description)
            ? $"`{command.Name}`"
            : $"`{command.Name}`\n{description}";

        return new
        {
            tag = "column_set",
            flex_mode = "none",
            background_style = "default",
            columns = new object[]
            {
                new
                {
                    tag = "column",
                    width = "weighted",
                    weight = 5,
                    vertical_align = "top",
                    elements = new object[]
                    {
                        new
                        {
                            tag = "div",
                            text = new { tag = "lark_md", content = markdown }
                        }
                    }
                },
                new
                {
                    tag = "column",
                    width = "auto",
                    vertical_align = "top",
                    elements = new object[]
                    {
                        new
                        {
                            tag = "button",
                            text = new { tag = "plain_text", content = "使用" },
                            type = "default",
                            behaviors = new[]
                            {
                                new
                                {
                                    type = "callback",
                                    value = new { action = "select_command", command_id = command.Id }
                                }
                            }
                        }
                    }
                }
            }
        };
    }

    private static string BuildCommandOptionText(FeishuCommand command)
    {
        var description = SanitizeMarkdown(command.Description);
        if (description.Length > 50)
        {
            description = description[..50] + "...";
        }

        return string.IsNullOrWhiteSpace(description)
            ? command.Name
            : $"{command.Name} - {description}";
    }

    private static string SerializeActionValue(object value)
    {
        return JsonSerializer.Serialize(value);
    }

    private static object BuildOverflowRow(string markdown, object[] options)
    {
        return new
        {
            tag = "div",
            text = new
            {
                tag = "lark_md",
                content = markdown
            },
            extra = new
            {
                tag = "overflow",
                options
            }
        };
    }

    private static string SanitizeMarkdown(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Replace("\r", " ").Replace("\n", " ").Trim();
    }

}
