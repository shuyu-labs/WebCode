using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service;

namespace WebCodeCli.Domain.Domain.Service.Channels;

internal static class SuperpowersQuickActionCardHelper
{
    private const string ExecutionControlRow = "execution_control_row";
    private const string PlanActionRow = "plan_action_row";
    private const string GoalPlanActionRow = "goal_plan_action_row";
    private const string CapabilityActionRow = "capability_action_row";
    private const string SessionConfirmActionRow = "session_confirm_action_row";

    public static bool ShouldShowPlanActions(
        IEnumerable<string?>? sessionContents,
        bool hasPlanFiles)
    {
        return hasPlanFiles
               && SessionContainsSuperpowers(sessionContents);
    }

    public static FeishuStreamingCardBottomPrompt? CreateBottomPrompt(
        string sessionId,
        string chatKey,
        string? toolId,
        SuperpowersCapabilitySnapshot? capabilityState = null)
    {
        if (capabilityState?.State == SuperpowersCapabilityState.Unavailable)
        {
            return null;
        }

        return new FeishuStreamingCardBottomPrompt
        {
            InputName = SuperpowersQuickActionDefaults.QuickInputFieldName,
            InputLabel = SuperpowersQuickActionDefaults.InstructionText,
            Placeholder = SuperpowersQuickActionDefaults.QuickInputPlaceholder,
            DefaultValue = string.Empty,
            ButtonText = SuperpowersQuickActionDefaults.QuickSubmitButtonText,
            ButtonType = "primary",
            Value = BuildActionValue(
                FeishuHelpCardAction.SubmitSuperpowersQuickInputAction,
                sessionId,
                chatKey,
                toolId)
        };
    }

    public static IReadOnlyList<FeishuStreamingCardBottomAction> CreateBottomActions(
        string sessionId,
        string chatKey,
        string? toolId,
        bool showPlanActions,
        SuperpowersCapabilitySnapshot? capabilityState = null,
        bool showStopAction = false)
    {
        var actions = new List<FeishuStreamingCardBottomAction>();

        if (capabilityState?.State == SuperpowersCapabilityState.Unavailable)
        {
            actions.Add(new FeishuStreamingCardBottomAction
            {
                Text = SuperpowersQuickActionDefaults.CapabilityRetryButtonText,
                Type = "default",
                RowKey = CapabilityActionRow,
                Value = BuildActionValue(
                    FeishuHelpCardAction.RetrySuperpowersCapabilityDetectionAction,
                    sessionId,
                    chatKey,
                    toolId)
            });

            if (showStopAction)
            {
                actions.Add(CreateStopAction(sessionId, chatKey, toolId));
            }

            return actions;
        }

        actions.Add(new FeishuStreamingCardBottomAction
        {
            Text = SuperpowersQuickActionDefaults.ContinueButtonText,
            Type = "default",
            RowKey = ExecutionControlRow,
            Value = BuildActionValue(
                FeishuHelpCardAction.ContinueSuperpowersAction,
                sessionId,
                chatKey,
                toolId)
        });

        if (showStopAction)
        {
            actions.Add(CreateStopAction(sessionId, chatKey, toolId));
        }

        if (showPlanActions)
        {
            actions.AddRange(
            [
                new FeishuStreamingCardBottomAction
                {
                    Text = SuperpowersQuickActionDefaults.ExecutePlanButtonText,
                    Type = "default",
                    RowKey = PlanActionRow,
                    Value = BuildActionValue(
                        FeishuHelpCardAction.ExecuteSuperpowersPlanAction,
                        sessionId,
                        chatKey,
                        toolId)
                },
                new FeishuStreamingCardBottomAction
                {
                    Text = SuperpowersQuickActionDefaults.ExecuteSubagentPlanButtonText,
                    Type = "primary",
                    RowKey = PlanActionRow,
                    Value = BuildActionValue(
                        FeishuHelpCardAction.ExecuteSuperpowersSubagentPlanAction,
                        sessionId,
                        chatKey,
                        toolId)
                },
                new FeishuStreamingCardBottomAction
                {
                    Text = SuperpowersQuickActionDefaults.ExecuteGoalPlanButtonText,
                    Type = "primary",
                    RowKey = GoalPlanActionRow,
                    Value = BuildActionValue(
                        FeishuHelpCardAction.ExecuteSuperpowersGoalPlanAction,
                        sessionId,
                        chatKey,
                        toolId)
                },
                new FeishuStreamingCardBottomAction
                {
                    Text = SuperpowersQuickActionDefaults.CompleteWorktreeButtonText,
                    Type = "default",
                    RowKey = GoalPlanActionRow,
                    Value = BuildActionValue(
                        FeishuHelpCardAction.ExecuteSuperpowersCompleteWorktreeAction,
                        sessionId,
                        chatKey,
                        toolId)
                }
            ]);
        }

        return actions;
    }

    public static string? MergeCapabilityStatusMarkdown(
        string? statusMarkdown,
        SuperpowersCapabilitySnapshot? capabilityState)
    {
        if (capabilityState?.State != SuperpowersCapabilityState.Unavailable
            || string.IsNullOrWhiteSpace(capabilityState.Message))
        {
            return statusMarkdown;
        }

        var capabilityMessage = $"⚠️ {capabilityState.Message}，请点击“{SuperpowersQuickActionDefaults.CapabilityRetryButtonText}”";
        return string.IsNullOrWhiteSpace(statusMarkdown)
            ? capabilityMessage
            : $"{statusMarkdown}\n{capabilityMessage}";
    }

    public static string BuildSessionMismatchConfirmationMarkdown(
        string boundSessionId,
        string currentSessionId)
    {
        return
            "⚠️ 当前激活会话已经变化，已暂停直接执行。\n\n" +
            $"卡片绑定会话：`{boundSessionId}`\n" +
            $"当前激活会话：`{currentSessionId}`\n\n" +
            "请选择接下来要对哪个会话执行这次 Superpowers 操作。";
    }

    public static IReadOnlyList<FeishuStreamingCardBottomAction> CreateSessionMismatchConfirmActions(
        string boundSessionId,
        string currentSessionId,
        string chatKey,
        string? toolId,
        string command)
    {
        return
        [
            new FeishuStreamingCardBottomAction
            {
                Text = "继续原会话",
                Type = "default",
                RowKey = SessionConfirmActionRow,
                Value = new
                {
                    action = FeishuHelpCardAction.ConfirmBoundSuperpowersAction,
                    session_id = boundSessionId,
                    chat_key = chatKey,
                    tool_id = toolId,
                    command
                }
            },
            new FeishuStreamingCardBottomAction
            {
                Text = "改为当前会话",
                Type = "primary",
                RowKey = SessionConfirmActionRow,
                Value = new
                {
                    action = FeishuHelpCardAction.ConfirmCurrentSuperpowersAction,
                    session_id = currentSessionId,
                    chat_key = chatKey,
                    tool_id = toolId,
                    command
                }
            }
        ];
    }

    private static object BuildActionValue(
        string action,
        string sessionId,
        string chatKey,
        string? toolId)
    {
        return new
        {
            action,
            session_id = sessionId,
            chat_key = chatKey,
            tool_id = toolId
        };
    }

    private static FeishuStreamingCardBottomAction CreateStopAction(
        string sessionId,
        string chatKey,
        string? toolId)
    {
        return new FeishuStreamingCardBottomAction
        {
            Text = SuperpowersQuickActionDefaults.StopButtonText,
            Type = "default",
            RowKey = ExecutionControlRow,
            Value = BuildActionValue(
                FeishuHelpCardAction.StopStreamingExecutionAction,
                sessionId,
                chatKey,
                toolId)
        };
    }

    private static bool SessionContainsSuperpowers(IEnumerable<string?>? sessionContents)
    {
        return sessionContents != null
               && sessionContents.Any(content =>
                   !string.IsNullOrWhiteSpace(content)
                   && content.Contains("superpowers", StringComparison.OrdinalIgnoreCase));
    }
}
