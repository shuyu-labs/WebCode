using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service;

namespace WebCodeCli.Domain.Tests;

public class SuperpowersPromptBuilderTests
{
    [Fact]
    public void BuildContinuePrompt_ReturnsApprovedPrompt()
    {
        Assert.Equal(
            SuperpowersQuickActionDefaults.ContinuePrompt,
            SuperpowersPromptBuilder.BuildContinuePrompt());
        Assert.StartsWith("可以，认可", SuperpowersPromptBuilder.BuildContinuePrompt(), StringComparison.Ordinal);
    }

    [Fact]
    public void BuildExecutePlanPrompt_ReturnsApprovedPrompt()
    {
        Assert.Equal(
            SuperpowersQuickActionDefaults.ExecutePlanPrompt,
            SuperpowersPromptBuilder.BuildExecutePlanPrompt());
    }

    [Fact]
    public void BuildSubagentExecutePlanPrompt_ReturnsApprovedCombinedPrompt()
    {
        Assert.Equal(
            SuperpowersQuickActionDefaults.ExecuteSubagentPlanPrompt,
            SuperpowersPromptBuilder.BuildSubagentExecutePlanPrompt());
    }

    [Fact]
    public void BuildCompleteWorktreePrompt_ReturnsApprovedPrompt()
    {
        Assert.Equal(
            SuperpowersQuickActionDefaults.CompleteWorktreePrompt,
            SuperpowersPromptBuilder.BuildCompleteWorktreePrompt());
    }

    [Theory]
    [InlineData("写一个执行步骤", "$using-superpowers ，使用superpowers技能，写一个执行步骤\n\nReply to the user in Chinese. Write documentation in English only. 代码注释需要使用中英文双语。 Keep exception and error messages in Chinese.")]
    [InlineData("$superpowers ，使用superpowers技能，写一个执行步骤", "$using-superpowers ，使用superpowers技能，写一个执行步骤\n\nReply to the user in Chinese. Write documentation in English only. 代码注释需要使用中英文双语。 Keep exception and error messages in Chinese.")]
    [InlineData("  写一个执行步骤  ", "$using-superpowers ，使用superpowers技能，写一个执行步骤\n\nReply to the user in Chinese. Write documentation in English only. 代码注释需要使用中英文双语。 Keep exception and error messages in Chinese.")]
    public void BuildQuickSkillPrompt_AppliesPrefixOnlyWhenMissing(string input, string expected)
    {
        Assert.Equal(expected, SuperpowersPromptBuilder.BuildQuickSkillPrompt(input));
    }

    [Fact]
    public void BuildQuickSkillPrompt_DoesNotDuplicateLanguagePolicy()
    {
        var input = "$using-superpowers ，使用superpowers技能，写一个执行步骤\n\nReply to the user in Chinese. Write documentation in English only. 代码注释需要使用中英文双语。 Keep exception and error messages in Chinese.";

        Assert.Equal(input, SuperpowersPromptBuilder.BuildQuickSkillPrompt(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildQuickSkillPrompt_ReturnsNullForBlankInput(string? input)
    {
        Assert.Null(SuperpowersPromptBuilder.BuildQuickSkillPrompt(input));
    }
}
