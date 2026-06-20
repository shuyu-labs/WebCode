using System.Text;
using WebCodeCli.Domain.Domain.Service.Channels;

namespace WebCodeCli.Domain.Tests;

public sealed class MarkdownReferenceExtractorTests
{
    [Fact]
    public void Extract_FindsBareMarkdownPathsAndMarkdownLinkTargets()
    {
        var workspaceRoot = CreateWorkspaceWithFiles(
            ("docs/agent-notes/2026-06-09.md", "# note"),
            ("docs/superpowers/specs/2026-06-09-feishu-markdown-doc-import-and-rendering-design.md", "# spec"));

        try
        {
            var text = """
                先看 docs/agent-notes/2026-06-09.md
                再看 [设计文档](docs/superpowers/specs/2026-06-09-feishu-markdown-doc-import-and-rendering-design.md)
                """;

            var results = MarkdownReferenceExtractor.Extract(text, workspaceRoot);

            Assert.Equal(
                [
                    "docs/agent-notes/2026-06-09.md",
                    "docs/superpowers/specs/2026-06-09-feishu-markdown-doc-import-and-rendering-design.md"
                ],
                results.Select(static item => item.RelativePath).ToArray());
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public void Extract_DeduplicatesRepeatedReferences()
    {
        var workspaceRoot = CreateWorkspaceWithFiles(
            ("docs/agent-notes/2026-06-09.md", "# note"));

        try
        {
            var text = """
                docs/agent-notes/2026-06-09.md
                [再次查看](./docs/agent-notes/2026-06-09.md)
                """;

            var results = MarkdownReferenceExtractor.Extract(text, workspaceRoot);

            var candidate = Assert.Single(results);
            Assert.Equal("docs/agent-notes/2026-06-09.md", candidate.Title);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public void Extract_FindsBareMarkdownFileNamesAndResolvesKnownPlanLikeDocumentPaths()
    {
        var workspaceRoot = CreateWorkspaceWithFiles(
            ("docs/superpowers/plans/2026-06-11-mmis-ai-first-operation-wave-2-implementation-plan.md", "# plan"));

        try
        {
            var text = """
                是，这份 2026-06-11-mmis-ai-first-operation-wave-2-implementation-plan.md 就是接下来要执行的 plan。
                """;

            var results = MarkdownReferenceExtractor.Extract(text, workspaceRoot);

            var candidate = Assert.Single(results);
            Assert.Equal(
                "docs/superpowers/plans/2026-06-11-mmis-ai-first-operation-wave-2-implementation-plan.md",
                candidate.RelativePath);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public void Extract_RejectsPathsOutsideWorkspaceRoot()
    {
        var workspaceRoot = CreateWorkspaceWithFiles(
            ("docs/inside.md", "# inside"));

        try
        {
            var results = MarkdownReferenceExtractor.Extract(@"..\outside\secret.md", workspaceRoot);
            Assert.Empty(results);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public void Extract_RejectsRemoteUrlsAndRemoteMarkdownLinks()
    {
        var workspaceRoot = CreateWorkspaceWithFiles(
            ("docs/inside.md", "# inside"));

        try
        {
            var text = """
                http://localhost:3000/a/b/c.md
                https://example.com/docs/readme.md?query=1
                [remote](https://example.com/docs/guide.md)
                [remote-with-anchor](https://example.com/docs/guide.md#part)
                """;

            var results = MarkdownReferenceExtractor.Extract(text, workspaceRoot);

            Assert.Empty(results);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    private static string CreateWorkspaceWithFiles(params (string RelativePath, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "webcode-md-extractor-" + Guid.NewGuid().ToString("N"));
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
