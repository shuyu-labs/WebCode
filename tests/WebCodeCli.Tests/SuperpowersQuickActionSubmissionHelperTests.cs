using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Pages;
using Xunit;

namespace WebCodeCli.Tests;

public sealed class SuperpowersQuickActionSubmissionHelperTests
{
    [Fact]
    public void BuildMessage_ReturnsContinuePrompt_ForContinueAction()
    {
        var result = SuperpowersQuickActionSubmissionHelper.BuildMessage(
            SuperpowersQuickActionRequestType.Continue,
            quickInput: null);

        Assert.Equal(SuperpowersQuickActionDefaults.ContinuePrompt, result);
    }

    [Fact]
    public void BuildMessage_ReturnsExecutePlanPrompt_ForExecutePlanAction()
    {
        var result = SuperpowersQuickActionSubmissionHelper.BuildMessage(
            SuperpowersQuickActionRequestType.ExecutePlan,
            quickInput: null);

        Assert.Equal(SuperpowersQuickActionDefaults.ExecutePlanPrompt, result);
    }

    [Fact]
    public void BuildMessage_ReturnsExecuteSubagentPlanPrompt_ForExecuteSubagentPlanAction()
    {
        var result = SuperpowersQuickActionSubmissionHelper.BuildMessage(
            SuperpowersQuickActionRequestType.ExecuteSubagentPlan,
            quickInput: null);

        Assert.Equal(SuperpowersQuickActionDefaults.ExecuteSubagentPlanPrompt, result);
    }

    [Fact]
    public void BuildMessage_ReturnsCompleteWorktreePrompt_ForCompleteWorktreeAction()
    {
        var result = SuperpowersQuickActionSubmissionHelper.BuildMessage(
            SuperpowersQuickActionRequestType.ExecuteCompleteWorktree,
            quickInput: null);

        Assert.Equal(SuperpowersQuickActionDefaults.CompleteWorktreePrompt, result);
    }

    [Theory]
    [InlineData("整理这个 plan", "$using-superpowers ，使用superpowers技能，整理这个 plan\n\nReply to the user in Chinese. Write documentation in English only. 代码注释需要使用中英文双语。 Keep exception and error messages in Chinese.")]
    [InlineData("$superpowers ，使用superpowers技能，整理这个 plan", "$using-superpowers ，使用superpowers技能，整理这个 plan\n\nReply to the user in Chinese. Write documentation in English only. 代码注释需要使用中英文双语。 Keep exception and error messages in Chinese.")]
    [InlineData("  整理这个 plan  ", "$using-superpowers ，使用superpowers技能，整理这个 plan\n\nReply to the user in Chinese. Write documentation in English only. 代码注释需要使用中英文双语。 Keep exception and error messages in Chinese.")]
    public void BuildMessage_AppliesQuickInputPrefixRules(string input, string expected)
    {
        var result = SuperpowersQuickActionSubmissionHelper.BuildMessage(
            SuperpowersQuickActionRequestType.QuickInput,
            input);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildMessage_ReturnsNull_ForBlankQuickInput(string? input)
    {
        var result = SuperpowersQuickActionSubmissionHelper.BuildMessage(
            SuperpowersQuickActionRequestType.QuickInput,
            input);

        Assert.Null(result);
    }
}
