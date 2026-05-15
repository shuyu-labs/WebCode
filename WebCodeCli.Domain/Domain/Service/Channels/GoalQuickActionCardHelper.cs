using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service;

namespace WebCodeCli.Domain.Domain.Service.Channels;

internal static class GoalQuickActionCardHelper
{
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
            Value = new
            {
                action = FeishuHelpCardAction.SubmitGoalQuickInputAction,
                session_id = sessionId,
                chat_key = chatKey,
                tool_id = toolId
            }
        };
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
}
