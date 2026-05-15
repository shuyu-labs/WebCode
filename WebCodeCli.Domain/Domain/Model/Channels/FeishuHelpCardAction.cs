using System.Text.Json.Serialization;

namespace WebCodeCli.Domain.Domain.Model.Channels;

/// <summary>
/// 飞书帮助卡片回调动作模型
/// 用于解析卡片按钮点击时的action.value
/// </summary>
public class FeishuHelpCardAction
{
    public const string SubmitAttachmentPromptAction = "submit_attachment_prompt";
    public const string SubmitSuperpowersQuickInputAction = "submit_superpowers_quick_input";
    public const string SubmitGoalQuickInputAction = "submit_goal_quick_input";
    public const string StatusGoalAction = "status_goal";
    public const string PauseGoalAction = "pause_goal";
    public const string ClearGoalAction = "clear_goal";
    public const string ResumeGoalAction = "resume_goal";
    public const string ContinueSuperpowersAction = "continue_superpowers";
    public const string StopStreamingExecutionAction = "stop_streaming_execution";
    public const string ExecuteSuperpowersPlanAction = "execute_superpowers_plan";
    public const string ExecuteSuperpowersSubagentPlanAction = "execute_superpowers_subagent_plan";
    public const string ConfirmBoundSuperpowersAction = "confirm_bound_superpowers_action";
    public const string ConfirmCurrentSuperpowersAction = "confirm_current_superpowers_action";
    public const string RetrySuperpowersCapabilityDetectionAction = "retry_superpowers_capability_detection";
    public const string ToggleReplyTtsAction = "toggle_reply_tts";

    /// <summary>
    /// 动作类型
    /// refresh_commands, select_command, back_to_list, execute_command
    /// </summary>
    [JsonPropertyName("action")]
    public string Action { get; set; } = string.Empty;

    /// <summary>
    /// 命令ID（select_command时使用）
    /// </summary>
    [JsonPropertyName("command_id")]
    public string? CommandId { get; set; }

    /// <summary>
    /// 分类ID（show_category时使用）
    /// </summary>
    [JsonPropertyName("category_id")]
    public string? CategoryId { get; set; }

    /// <summary>
    /// 命令内容（execute_command时使用）
    /// </summary>
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    /// <summary>
    /// 会话ID（会话管理时使用）
    /// </summary>
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }

    /// <summary>
    /// 项目ID（项目管理时使用）
    /// </summary>
    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    /// <summary>
    /// 分支名称（项目分支切换时使用）
    /// </summary>
    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    /// <summary>
    /// 聊天Key（会话管理时使用，格式：feishu:{AppId}:{ChatId}）
    /// </summary>
    [JsonPropertyName("chat_key")]
    public string? ChatKey { get; set; }

    /// <summary>
    /// 创建会话模式（default/custom/existing）
    /// </summary>
    [JsonPropertyName("create_mode")]
    public string? CreateMode { get; set; }

    /// <summary>
    /// 工作区路径（已有目录选择时使用）
    /// </summary>
    [JsonPropertyName("workspace_path")]
    public string? WorkspacePath { get; set; }

    /// <summary>
    /// 要复制/发送到聊天的路径
    /// </summary>
    [JsonPropertyName("copy_path")]
    public string? CopyPath { get; set; }

    /// <summary>
    /// CLI 工具 ID（新建会话选择工具时使用）
    /// </summary>
    [JsonPropertyName("tool_id")]
    public string? ToolId { get; set; }

    /// <summary>
    /// 外部 CLI 会话/线程 ID（导入外部会话时使用）
    /// </summary>
    [JsonPropertyName("cli_thread_id")]
    public string? CliThreadId { get; set; }

    /// <summary>
    /// 外部会话标题（导入外部会话时使用）
    /// </summary>
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    /// <summary>
    /// 当前浏览的目录路径（相对于会话工作区根目录）
    /// </summary>
    [JsonPropertyName("directory_path")]
    public string? DirectoryPath { get; set; }

    /// <summary>
    /// 当前浏览的文件路径（相对于会话工作区根目录）
    /// </summary>
    [JsonPropertyName("file_path")]
    public string? FilePath { get; set; }

    /// <summary>
    /// 分页页码（从 0 开始）
    /// </summary>
    [JsonPropertyName("page")]
    public int? Page { get; set; }

    /// <summary>
    /// 会话管理卡片是否展开显示全部会话
    /// </summary>
    [JsonPropertyName("show_all_sessions")]
    public bool? ShowAllSessions { get; set; }

    /// <summary>
    /// 表单中的模型值（会话启动设置时使用）
    /// </summary>
    [JsonPropertyName("model")]
    public string? Model { get; set; }

    /// <summary>
    /// 表单中的思考等级（会话启动设置时使用）
    /// </summary>
    [JsonPropertyName("reasoning_effort")]
    public string? ReasoningEffort { get; set; }

    /// <summary>
    /// 是否以新的聊天卡片发送，而不是替换当前卡片
    /// </summary>
    [JsonPropertyName("send_as_new_card")]
    public bool SendAsNewCard { get; set; }

    [JsonPropertyName("attachment_type")]
    public string? AttachmentType { get; set; }

    [JsonPropertyName("attachment_name")]
    public string? AttachmentName { get; set; }

    [JsonPropertyName("attachment_path")]
    public string? AttachmentPath { get; set; }

    [JsonPropertyName("attachment_mime_type")]
    public string? AttachmentMimeType { get; set; }
}
