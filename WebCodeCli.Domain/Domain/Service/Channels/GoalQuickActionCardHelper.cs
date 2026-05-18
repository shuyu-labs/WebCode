using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service;

namespace WebCodeCli.Domain.Domain.Service.Channels;

internal static class GoalQuickActionCardHelper
{
    private const string GoalRow1 = "goal_row_1";
    private const string GoalRow2 = "goal_row_2";
    private const string GoalRow3 = "goal_row_3";

    public static FeishuStreamingCardBottomPrompt? CreateBottomPrompt(
        string sessionId,
        string chatKey,
        string? toolId,
        GoalCapabilitySnapshot? capabilityState = null)
    {
        if (capabilityState?.State == GoalCapabilityState.Unavailable)
        {
            return null;
        }

        return new FeishuStreamingCardBottomPrompt
        {
            FormName = "goal_quick_action_form",
            InputName = GoalQuickActionDefaults.QuickInputFieldName,
            InputLabel = GoalQuickActionDefaults.InstructionText,
            Placeholder = GoalQuickActionDefaults.QuickInputPlaceholder,
            DefaultValue = string.Empty,
            ButtonText = GoalQuickActionDefaults.QuickSubmitButtonText,
            ButtonType = "primary",
            Value = BuildActionValue(
                FeishuHelpCardAction.SubmitGoalQuickInputAction,
                sessionId,
                chatKey,
                toolId)
        };
    }

    public static IReadOnlyList<FeishuStreamingCardBottomAction> CreateBottomActions(
        string sessionId,
        string chatKey,
        string? toolId,
        GoalCapabilitySnapshot? capabilityState = null,
        bool showButtons = true,
        bool showTemporaryExitAction = false)
    {
        if (!showButtons || capabilityState?.State == GoalCapabilityState.Unavailable)
        {
            return [];
        }

        var actions = new List<FeishuStreamingCardBottomAction>
        {
            new FeishuStreamingCardBottomAction
            {
                Text = GoalQuickActionDefaults.StatusButtonText,
                Type = "default",
                RowKey = GoalRow1,
                Value = BuildActionValue(
                    FeishuHelpCardAction.StatusGoalAction,
                    sessionId,
                    chatKey,
                    toolId)
            },
            new FeishuStreamingCardBottomAction
            {
                Text = GoalQuickActionDefaults.PauseButtonText,
                Type = "default",
                RowKey = GoalRow1,
                Value = BuildActionValue(
                    FeishuHelpCardAction.PauseGoalAction,
                    sessionId,
                    chatKey,
                    toolId)
            },
            new FeishuStreamingCardBottomAction
            {
                Text = GoalQuickActionDefaults.ClearButtonText,
                Type = "default",
                RowKey = GoalRow2,
                Value = BuildActionValue(
                    FeishuHelpCardAction.ClearGoalAction,
                    sessionId,
                    chatKey,
                    toolId)
            },
            new FeishuStreamingCardBottomAction
            {
                Text = GoalQuickActionDefaults.ResumeButtonText,
                Type = "primary",
                RowKey = GoalRow2,
                Value = BuildActionValue(
                    FeishuHelpCardAction.ResumeGoalAction,
                    sessionId,
                    chatKey,
                    toolId)
            }
        };

        if (showTemporaryExitAction)
        {
            actions.Add(new FeishuStreamingCardBottomAction
            {
                Text = GoalQuickActionDefaults.TemporaryExitButtonText,
                Type = "default",
                RowKey = GoalRow3,
                Value = BuildActionValue(
                    FeishuHelpCardAction.TemporarilyExitGoalRuntimeAction,
                    sessionId,
                    chatKey,
                    toolId)
            });
        }

        return actions;
    }

    public static string? MergeCapabilityStatusMarkdown(
        string? statusMarkdown,
        GoalCapabilitySnapshot? capabilityState)
    {
        if (capabilityState?.State != GoalCapabilityState.Unavailable
            || string.IsNullOrWhiteSpace(capabilityState.Message))
        {
            return statusMarkdown;
        }

        var capabilityMessage = $"⚠️ {capabilityState.Message}";
        return string.IsNullOrWhiteSpace(statusMarkdown)
            ? capabilityMessage
            : $"{statusMarkdown}\n{capabilityMessage}";
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
}
