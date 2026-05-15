using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service;

namespace WebCodeCli.Domain.Domain.Service.Channels;

internal static class SuperpowersQuickActionCardHelper
{
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
        SuperpowersCapabilitySnapshot? capabilityState = null)
    {
        if (capabilityState?.State == SuperpowersCapabilityState.Unavailable)
        {
            return
            [
                new FeishuStreamingCardBottomAction
                {
                    Text = SuperpowersQuickActionDefaults.CapabilityRetryButtonText,
                    Type = "default",
                    Value = BuildActionValue(
                        FeishuHelpCardAction.RetrySuperpowersCapabilityDetectionAction,
                        sessionId,
                        chatKey,
                        toolId)
                }
            ];
        }

        var actions = new List<FeishuStreamingCardBottomAction>
        {
            new()
            {
                Text = SuperpowersQuickActionDefaults.ContinueButtonText,
                Type = "default",
                Value = BuildActionValue(
                    FeishuHelpCardAction.ContinueSuperpowersAction,
                    sessionId,
                    chatKey,
                    toolId)
            }
        };

        if (!showPlanActions)
        {
            return actions;
        }

        actions.AddRange(
        [
            new FeishuStreamingCardBottomAction
            {
                Text = SuperpowersQuickActionDefaults.ExecutePlanButtonText,
                Type = "default",
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
                Value = BuildActionValue(
                    FeishuHelpCardAction.ExecuteSuperpowersSubagentPlanAction,
                    sessionId,
                    chatKey,
                    toolId)
            }
        ]);

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

    private static bool SessionContainsSuperpowers(IEnumerable<string?>? sessionContents)
    {
        return sessionContents != null
               && sessionContents.Any(content =>
                   !string.IsNullOrWhiteSpace(content)
                   && content.Contains("superpowers", StringComparison.OrdinalIgnoreCase));
    }
}
