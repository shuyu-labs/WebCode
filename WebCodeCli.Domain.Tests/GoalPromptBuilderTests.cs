using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service;
using System.Text;

namespace WebCodeCli.Domain.Tests;

public sealed class GoalPromptBuilderTests
{
    [Theory]
    [InlineData("整理这个目标", "/goal 整理这个目标")]
    [InlineData("/goal 整理这个目标", "/goal 整理这个目标")]
    [InlineData("  整理这个目标  ", "/goal 整理这个目标")]
    public void BuildGoalPrompt_AppliesPrefixOnlyWhenMissing(string input, string expected)
    {
        Assert.Equal(expected, GoalPromptBuilder.BuildGoalPrompt(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildGoalPrompt_ReturnsNullForBlankInput(string? input)
    {
        Assert.Null(GoalPromptBuilder.BuildGoalPrompt(input));
    }

    [Fact]
    public void BuildSubagentPlanGoalPrompt_RequiresClosingAllPlanChecklistItemsBeforeGoalCompletion()
    {
        var prompt = GoalPromptBuilder.BuildSubagentPlanGoalPrompt();

        Assert.StartsWith("/goal ", prompt, StringComparison.Ordinal);
        Assert.Contains("plan文档内的[ ]check list都检查收口后，变成[x]后才算goal完成", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildSubagentPlanGoalPrompt_WhenReplyReferencesPlanMarkdown_UsesReferencedPlanDocument()
    {
        var workspaceRoot = CreateWorkspaceWithFiles(
            ("docs/superpowers/plans/approved-plan.md", "# approved"));

        try
        {
            var reply = """
                先按这个计划收口：
                [approved plan](docs/superpowers/plans/approved-plan.md)
                """;

            var prompt = GoalPromptBuilder.BuildSubagentPlanGoalPrompt(reply, workspaceRoot);

            Assert.StartsWith("/goal ", prompt, StringComparison.Ordinal);
            Assert.Contains("docs/superpowers/plans/approved-plan.md", prompt, StringComparison.Ordinal);
            Assert.Contains("该plan文档内的[ ]check list", prompt, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public void BuildSubagentPlanGoalPrompt_WhenReplyMentionsBarePlanMarkdownFileName_UsesResolvedPlanDocument()
    {
        var workspaceRoot = CreateWorkspaceWithFiles(
            ("docs/superpowers/plans/2026-06-11-mmis-ai-first-operation-wave-2-implementation-plan.md", "# approved"));

        try
        {
            var reply = """
                是，这份 2026-06-11-mmis-ai-first-operation-wave-2-implementation-plan.md 就是接下来要执行的 plan。
                """;

            var prompt = GoalPromptBuilder.BuildSubagentPlanGoalPrompt(reply, workspaceRoot);

            Assert.StartsWith("/goal ", prompt, StringComparison.Ordinal);
            Assert.Contains(
                "docs/superpowers/plans/2026-06-11-mmis-ai-first-operation-wave-2-implementation-plan.md",
                prompt,
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    private static string CreateWorkspaceWithFiles(params (string RelativePath, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "webcode-goal-prompt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        foreach (var file in files)
        {
            var absolutePath = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
            File.WriteAllText(absolutePath, file.Content, Encoding.UTF8);
        }

        return root;
    }
}
