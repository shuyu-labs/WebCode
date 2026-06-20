using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service.Adapters;

namespace WebCodeCli.Domain.Tests;

public class CliExecutionRequestAdapterTests
{
    [Fact]
    public void CodexAdapter_GetAttachmentCapabilities_DeclaresNativeImageSupport()
    {
        var adapter = new CodexAdapter();
        var tool = new CliToolConfig
        {
            Id = "codex",
            Name = "Codex",
            Command = "codex",
            Enabled = true
        };

        var capabilities = adapter.GetAttachmentCapabilities(tool);

        Assert.True(capabilities.SupportsNativeAttachments);
        Assert.True(capabilities.SupportsMultipleNativeAttachments);
        Assert.Contains(MessageAttachmentKind.Image, capabilities.NativeKinds);
    }

    [Fact]
    public void CodexAdapter_BuildArguments_RequestOverload_EncodesNativeImagesAndReferencePreamble()
    {
        var adapter = new CodexAdapter();
        var tool = new CliToolConfig
        {
            Id = "codex",
            Name = "Codex",
            Command = "codex",
            Enabled = true
        };
        var request = new CliExecutionRequest
        {
            SessionId = "session-123",
            ToolId = "codex",
            PromptText = "Review the staged files.",
            SessionContext = new CliSessionContext
            {
                SessionId = "session-123",
                WorkingDirectory = Path.GetTempPath()
            },
            NativeAttachments =
            [
                new CliExecutionAttachment
                {
                    DisplayName = "diagram.png",
                    Kind = MessageAttachmentKind.Image,
                    AbsolutePath = @"D:\attachments\diagram.png",
                    WorkspaceRelativePath = ".webcode/message-inputs/submission-1/diagram.png"
                }
            ],
            ReferenceAttachments =
            [
                new CliExecutionAttachment
                {
                    DisplayName = "notes.pdf",
                    Kind = MessageAttachmentKind.Pdf,
                    AbsolutePath = @"D:\attachments\notes.pdf",
                    WorkspaceRelativePath = ".webcode/message-inputs/submission-1/notes.pdf"
                }
            ]
        };

        var arguments = adapter.BuildArguments(tool, request);

        Assert.Contains("-i \"D:\\attachments\\diagram.png\"", arguments, StringComparison.Ordinal);
        Assert.Contains("[WEBCODE_REFERENCE_ATTACHMENTS]", arguments, StringComparison.Ordinal);
        Assert.Contains(".webcode/message-inputs/submission-1/notes.pdf", arguments, StringComparison.Ordinal);
        Assert.Contains("Review the staged files.", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void CodexAdapter_BuildArguments_WithNativeAttachmentAndDashPrefixedPrompt_PlacesAttachmentsBeforeArgumentTerminator()
    {
        var adapter = new CodexAdapter();
        var tool = new CliToolConfig
        {
            Id = "codex",
            Name = "Codex",
            Command = "codex",
            Enabled = true
        };
        var request = new CliExecutionRequest
        {
            SessionId = "session-123",
            ToolId = "codex",
            PromptText = "- Docs/superpowers/plans/test.md",
            SessionContext = new CliSessionContext
            {
                SessionId = "session-123",
                WorkingDirectory = Path.GetTempPath()
            },
            NativeAttachments =
            [
                new CliExecutionAttachment
                {
                    DisplayName = "diagram.png",
                    Kind = MessageAttachmentKind.Image,
                    AbsolutePath = @"D:\attachments\diagram.png",
                    WorkspaceRelativePath = ".webcode/message-inputs/submission-1/diagram.png"
                }
            ]
        };

        var arguments = adapter.BuildArguments(tool, request);

        Assert.Equal(
            "exec --skip-git-repo-check --dangerously-bypass-approvals-and-sandbox --json -i \"D:\\attachments\\diagram.png\" -- \"- Docs/superpowers/plans/test.md\"",
            arguments);
    }

    [Fact]
    public void ClaudeCodeAdapter_BuildArguments_RequestOverload_IncludesReferenceAttachmentPreamble()
    {
        var adapter = new ClaudeCodeAdapter();
        var tool = new CliToolConfig
        {
            Id = "claude-code",
            Name = "Claude Code",
            Command = "claude",
            Enabled = true
        };
        var request = CreateReferenceOnlyRequest("claude-code");

        var arguments = adapter.BuildArguments(tool, request);

        Assert.Contains("[WEBCODE_REFERENCE_ATTACHMENTS]", arguments, StringComparison.Ordinal);
        Assert.Contains(".webcode/message-inputs/submission-1/notes.txt", arguments, StringComparison.Ordinal);
        Assert.Contains("Review the staged files.", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenCodeAdapter_BuildArguments_RequestOverload_IncludesReferenceAttachmentPreamble()
    {
        var adapter = new OpenCodeAdapter();
        var tool = new CliToolConfig
        {
            Id = "opencode",
            Name = "OpenCode",
            Command = "opencode",
            Enabled = true
        };
        var request = CreateReferenceOnlyRequest("opencode");

        var arguments = adapter.BuildArguments(tool, request);

        Assert.Contains("[WEBCODE_REFERENCE_ATTACHMENTS]", arguments, StringComparison.Ordinal);
        Assert.Contains(".webcode/message-inputs/submission-1/notes.txt", arguments, StringComparison.Ordinal);
        Assert.Contains("Review the staged files.", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyAdapter_DefaultRequestBuildArguments_PreservesReferenceAttachmentsViaPromptPreamble()
    {
        ICliToolAdapter adapter = new LegacyPromptOnlyAdapter();
        var tool = new CliToolConfig
        {
            Id = "legacy-tool",
            Name = "Legacy Tool",
            Command = "legacy",
            Enabled = true
        };
        var request = CreateReferenceOnlyRequest("legacy-tool");

        var arguments = adapter.BuildArguments(tool, request);

        Assert.Contains("[WEBCODE_REFERENCE_ATTACHMENTS]", arguments, StringComparison.Ordinal);
        Assert.Contains(".webcode/message-inputs/submission-1/notes.txt", arguments, StringComparison.Ordinal);
        Assert.Contains("Review the staged files.", arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void LegacyAdapter_DefaultRequestBuildArguments_WhenNativeAttachmentsExist_FailsFast()
    {
        ICliToolAdapter adapter = new LegacyPromptOnlyAdapter();
        var tool = new CliToolConfig
        {
            Id = "legacy-tool",
            Name = "Legacy Tool",
            Command = "legacy",
            Enabled = true
        };
        var request = new CliExecutionRequest
        {
            SessionId = "session-123",
            ToolId = "legacy-tool",
            PromptText = "Review the staged files.",
            SessionContext = new CliSessionContext
            {
                SessionId = "session-123",
                WorkingDirectory = Path.GetTempPath()
            },
            NativeAttachments =
            [
                new CliExecutionAttachment
                {
                    DisplayName = "diagram.png",
                    Kind = MessageAttachmentKind.Image,
                    AbsolutePath = @"D:\attachments\diagram.png",
                    WorkspaceRelativePath = ".webcode/message-inputs/submission-1/diagram.png"
                }
            ]
        };

        var exception = Assert.Throws<InvalidOperationException>(() => adapter.BuildArguments(tool, request));

        Assert.Contains("native attachment", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("legacy-tool", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static CliExecutionRequest CreateReferenceOnlyRequest(string toolId)
    {
        return new CliExecutionRequest
        {
            SessionId = "session-123",
            ToolId = toolId,
            PromptText = "Review the staged files.",
            SessionContext = new CliSessionContext
            {
                SessionId = "session-123",
                WorkingDirectory = Path.GetTempPath()
            },
            ReferenceAttachments =
            [
                new CliExecutionAttachment
                {
                    DisplayName = "notes.txt",
                    Kind = MessageAttachmentKind.Text,
                    AbsolutePath = @"D:\attachments\notes.txt",
                    WorkspaceRelativePath = ".webcode/message-inputs/submission-1/notes.txt"
                }
            ]
        };
    }

    private sealed class LegacyPromptOnlyAdapter : ICliToolAdapter
    {
        public string[] SupportedToolIds => ["legacy-tool"];

        public bool SupportsStreamParsing => false;

        public bool CanHandle(CliToolConfig tool)
            => string.Equals(tool.Id, "legacy-tool", StringComparison.OrdinalIgnoreCase);

        public string BuildArguments(CliToolConfig tool, string prompt, CliSessionContext context)
            => prompt;

        public string BuildLowInterruptionArguments(CliToolConfig tool, CliSessionContext context)
            => throw new NotSupportedException();

        public CliOutputEvent? ParseOutputLine(string line) => null;

        public string? ExtractSessionId(CliOutputEvent outputEvent) => null;

        public string? ExtractAssistantMessage(CliOutputEvent outputEvent) => null;

        public string GetEventTitle(CliOutputEvent outputEvent) => string.Empty;

        public string GetEventBadgeClass(CliOutputEvent outputEvent) => string.Empty;

        public string GetEventBadgeLabel(CliOutputEvent outputEvent) => string.Empty;
    }
}
