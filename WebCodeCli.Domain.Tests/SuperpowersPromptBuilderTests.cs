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

    [Theory]
    [InlineData("写一个执行步骤", "$using-superpowers ，使用superpowers技能，写一个执行步骤\n\nReply to the user in Chinese. Write documentation and code comments in English only. Keep exception and error messages in Chinese.")]
    [InlineData("$superpowers ，使用superpowers技能，写一个执行步骤", "$using-superpowers ，使用superpowers技能，写一个执行步骤\n\nReply to the user in Chinese. Write documentation and code comments in English only. Keep exception and error messages in Chinese.")]
    [InlineData("  写一个执行步骤  ", "$using-superpowers ，使用superpowers技能，写一个执行步骤\n\nReply to the user in Chinese. Write documentation and code comments in English only. Keep exception and error messages in Chinese.")]
    public void BuildQuickSkillPrompt_AppliesPrefixOnlyWhenMissing(string input, string expected)
    {
        Assert.Equal(expected, SuperpowersPromptBuilder.BuildQuickSkillPrompt(input));
    }

    [Fact]
    public void BuildQuickSkillPrompt_DoesNotDuplicateLanguagePolicy()
    {
        var input = "$using-superpowers ，使用superpowers技能，写一个执行步骤\n\nReply to the user in Chinese. Write documentation and code comments in English only. Keep exception and error messages in Chinese.";

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
