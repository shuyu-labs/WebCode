using WebCodeCli.Domain.Domain.Service.Channels;

namespace WebCodeCli.Domain.Tests;

public sealed class ListeningReplyDocumentFormatterTests
{
    private const string FilePlaceholder1 = "\u6587\u4ef6\u5185\u5bb91";
    private const string FilePlaceholder2 = "\u6587\u4ef6\u5185\u5bb92";
    private const string FilePlaceholder3 = "\u6587\u4ef6\u5185\u5bb93";
    private const string FilePlaceholder4 = "\u6587\u4ef6\u5185\u5bb94";
    private const string CommandPlaceholder1 = "\u547d\u4ee4\u5185\u5bb91";
    private const string CommandPlaceholder2 = "\u547d\u4ee4\u5185\u5bb92";
    private const char FullwidthColon = '\uFF1A';

    [Fact]
    public void Format_ReplacesDistinctFileReferencesWithSequentialPlaceholders()
    {
        const string input = "Build succeeded. Current warnings mention /D:/VSWorkshop/WebCode/WebCodeCli/Pages/SharedWorkspace.razor:812 and /D:/VSWorkshop/WebCode/WebCodeCli/Pages/SharedSession.razor:241.";

        var output = ListeningReplyDocumentFormatter.Format(input);

        Assert.Contains(FilePlaceholder1, output, StringComparison.Ordinal);
        Assert.Contains(FilePlaceholder2, output, StringComparison.Ordinal);
        Assert.Contains($"{FilePlaceholder1}{FullwidthColon}/D:/VSWorkshop/WebCode/WebCodeCli/Pages/SharedWorkspace.razor:812", output, StringComparison.Ordinal);
        Assert.Contains($"{FilePlaceholder2}{FullwidthColon}/D:/VSWorkshop/WebCode/WebCodeCli/Pages/SharedSession.razor:241", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ReusesPlaceholderForRepeatedFileReference()
    {
        const string input = "Check /D:/repo/a.cs:1 first, then review /D:/repo/a.cs:1 again.";

        var output = ListeningReplyDocumentFormatter.Format(input);
        var bodyOnly = GetBody(output);

        Assert.Contains(FilePlaceholder1, output, StringComparison.Ordinal);
        Assert.DoesNotContain(FilePlaceholder2, output, StringComparison.Ordinal);
        Assert.Equal(2, bodyOnly.Split(FilePlaceholder1, StringSplitOptions.None).Length - 1);
        Assert.Contains($"{FilePlaceholder1}{FullwidthColon}/D:/repo/a.cs:1", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_LeavesPlainTextUntouched_WhenNoFileReferenceExists()
    {
        const string input = "Build succeeded. Current warnings are from existing packages only.";

        var output = ListeningReplyDocumentFormatter.Format(input);

        Assert.Equal(input, output);
    }

    [Fact]
    public void Format_ReplacesRelativePathAndBareFileNames_InMarkdownLinkText()
    {
        const string input = """
Package C is now ready for direct AI usage.
Added dedicated skill and local script:
[mmis-page-metadata-operations/skill.md](<link-1>)
[page-metadata-ops.ps1](<link-2>)

Routed the main workflow entry as well:
[authoring-first-automation.md](<link-3>)
[2026-05-28-mmis-page-metadata-full-stack-rearchitecture-implementation-plan.md](<link-4>)
""";

        var output = ListeningReplyDocumentFormatter.Format(input);
        var bodyOnly = GetBody(output);

        Assert.DoesNotContain("mmis-page-metadata-operations/skill.md", bodyOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("page-metadata-ops.ps1", bodyOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("authoring-first-automation.md", bodyOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("2026-05-28-mmis-page-metadata-full-stack-rearchitecture-implementation-plan.md", bodyOnly, StringComparison.Ordinal);
        Assert.Contains($"[{FilePlaceholder1}](<link-1>)", bodyOnly, StringComparison.Ordinal);
        Assert.Contains($"[{FilePlaceholder2}](<link-2>)", bodyOnly, StringComparison.Ordinal);
        Assert.Contains($"[{FilePlaceholder3}](<link-3>)", bodyOnly, StringComparison.Ordinal);
        Assert.Contains($"[{FilePlaceholder4}](<link-4>)", bodyOnly, StringComparison.Ordinal);
        Assert.Contains($"{FilePlaceholder1}{FullwidthColon}mmis-page-metadata-operations/skill.md", output, StringComparison.Ordinal);
        Assert.Contains($"{FilePlaceholder2}{FullwidthColon}page-metadata-ops.ps1", output, StringComparison.Ordinal);
        Assert.Contains($"{FilePlaceholder3}{FullwidthColon}authoring-first-automation.md", output, StringComparison.Ordinal);
        Assert.Contains($"{FilePlaceholder4}{FullwidthColon}2026-05-28-mmis-page-metadata-full-stack-rearchitecture-implementation-plan.md", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ReplacesWindowsAndUnixStyleRelativePaths_AsSingleFileReference()
    {
        const string input = @"Please inspect WmsServerV4\src\Cimc.Tianda.Wms.Application\1397\Custom\MoveOut\DeliveringBillServiceCus1397.cs and docs/agent-notes/2026-05-29.md.";

        var output = ListeningReplyDocumentFormatter.Format(input);
        var bodyOnly = GetBody(output);

        Assert.DoesNotContain(@"WmsServerV4\src\Cimc.Tianda.Wms.Application\1397\Custom\MoveOut\DeliveringBillServiceCus1397.cs", bodyOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("docs/agent-notes/2026-05-29.md", bodyOnly, StringComparison.Ordinal);
        Assert.Contains(FilePlaceholder1, bodyOnly, StringComparison.Ordinal);
        Assert.Contains(FilePlaceholder2, bodyOnly, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_DoesNotRewrite_UrlsEmailsOrVersions()
    {
        const string input = "See https://open.feishu.cn/document/server-path, contact alice@example.com, and verify version v1.2.3.";

        var output = ListeningReplyDocumentFormatter.Format(input);
        var bodyOnly = GetBody(output);

        Assert.Contains("https://open.feishu.cn/document/server-path", bodyOnly, StringComparison.Ordinal);
        Assert.Contains("alice@example.com", bodyOnly, StringComparison.Ordinal);
        Assert.Contains("v1.2.3", bodyOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("\u6587\u4ef6\u5185\u5bb9", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ReplacesFencedPowerShellBlock_WithCommandPlaceholderAndAppendsCommandsAtEnd()
    {
        const string input = """
Validated commands:
```powershell
powershell -NoProfile -File MMIS-Server/scripts/page-metadata-ops.ps1 -Operation publish -PageCode system/user
powershell -NoProfile -File MMIS-Server/scripts/page-metadata-ops.ps1 -Operation verify -PageCode system/user
```
""";

        var output = ListeningReplyDocumentFormatter.Format(input);
        var bodyOnly = GetBody(output);

        Assert.Contains($"[{CommandPlaceholder1}]", bodyOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("```powershell", bodyOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("-Operation publish", bodyOnly, StringComparison.Ordinal);
        Assert.Contains($"{CommandPlaceholder1}{FullwidthColon}powershell -NoProfile -File MMIS-Server/scripts/page-metadata-ops.ps1 -Operation publish -PageCode system/user", output, StringComparison.Ordinal);
        Assert.Contains("powershell -NoProfile -File MMIS-Server/scripts/page-metadata-ops.ps1 -Operation verify -PageCode system/user", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_LeavesInlineBackticksUntouched_WhenTheyAreNotFencedCommands()
    {
        const string input = "Run `dotnet build` to inspect the output and then review docs/agent-notes/2026-06-09.md.";

        var output = ListeningReplyDocumentFormatter.Format(input);
        var bodyOnly = GetBody(output);

        Assert.DoesNotContain($"[{CommandPlaceholder1}]", bodyOnly, StringComparison.Ordinal);
        Assert.Contains("`dotnet build`", bodyOnly, StringComparison.Ordinal);
        Assert.Contains(FilePlaceholder1, bodyOnly, StringComparison.Ordinal);
        Assert.Contains($"{FilePlaceholder1}{FullwidthColon}docs/agent-notes/2026-06-09.md", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ReplacesMarkdownListCommandItems_WithCommandPlaceholdersAndAppendsCommandsAtEnd()
    {
        const string input = """
- `dotnet build WebCodeCli.sln --no-restore -v minimal`
- `dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter "FullyQualifiedName~ListeningReplyDocumentFormatterTests"`
""";

        var output = ListeningReplyDocumentFormatter.Format(input);
        var bodyOnly = GetBody(output);

        Assert.Contains($"- [{CommandPlaceholder1}]", bodyOnly, StringComparison.Ordinal);
        Assert.Contains($"- [{CommandPlaceholder2}]", bodyOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet build WebCodeCli.sln --no-restore -v minimal", bodyOnly, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter \"FullyQualifiedName~ListeningReplyDocumentFormatterTests\"", bodyOnly, StringComparison.Ordinal);
        Assert.Contains($"{CommandPlaceholder1}{FullwidthColon}dotnet build WebCodeCli.sln --no-restore -v minimal", output, StringComparison.Ordinal);
        Assert.Contains($"{CommandPlaceholder2}{FullwidthColon}dotnet test WebCodeCli.Domain.Tests/WebCodeCli.Domain.Tests.csproj --filter \"FullyQualifiedName~ListeningReplyDocumentFormatterTests\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_ReplacesStandaloneCommandLines_WithoutTouchingInlineCommandProse()
    {
        const string input = """
dotnet build WebCodeCli.sln --no-restore -v minimal
powershell -NoProfile -File scripts/check.ps1

Run `dotnet build` to inspect the output details.
""";

        var output = ListeningReplyDocumentFormatter.Format(input);
        var bodyOnly = GetBody(output);

        Assert.Contains($"[{CommandPlaceholder1}]", bodyOnly, StringComparison.Ordinal);
        Assert.Contains($"[{CommandPlaceholder2}]", bodyOnly, StringComparison.Ordinal);
        Assert.Contains("Run `dotnet build` to inspect the output details.", bodyOnly, StringComparison.Ordinal);
        Assert.Contains($"{CommandPlaceholder1}{FullwidthColon}dotnet build WebCodeCli.sln --no-restore -v minimal", output, StringComparison.Ordinal);
        Assert.Contains($"{CommandPlaceholder2}{FullwidthColon}powershell -NoProfile -File scripts/check.ps1", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Format_LeavesNonCommandFencedCodeBlockUntouched()
    {
        const string input = """
```json
{"message":"keep me"}
```
""";

        var output = ListeningReplyDocumentFormatter.Format(input);

        Assert.Equal(input, output);
    }

    private static string GetBody(string output)
    {
        var appendixMarkers = new[]
        {
            $"{Environment.NewLine}{Environment.NewLine}文件内容1：",
            $"{Environment.NewLine}{Environment.NewLine}命令内容1："
        };

        var bodyEnd = appendixMarkers
            .Select(marker => output.IndexOf(marker, StringComparison.Ordinal))
            .Where(index => index >= 0)
            .DefaultIfEmpty(output.Length)
            .Min();

        return output[..bodyEnd];
    }
}
