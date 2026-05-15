using WebCodeCli.Pages;
using Xunit;

namespace WebCodeCli.Tests;

public sealed class GoalQuickActionSubmissionHelperTests
{
    [Theory]
    [InlineData("整理这个目标", "/goal 整理这个目标")]
    [InlineData("/goal 整理这个目标", "/goal 整理这个目标")]
    [InlineData("  整理这个目标  ", "/goal 整理这个目标")]
    public void BuildMessage_AppliesGoalPrefixRules(string input, string expected)
    {
        var result = GoalQuickActionSubmissionHelper.BuildMessage(input);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildMessage_ReturnsNull_ForBlankQuickInput(string? input)
    {
        var result = GoalQuickActionSubmissionHelper.BuildMessage(input);

        Assert.Null(result);
    }
}
