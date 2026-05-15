using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service;

namespace WebCodeCli.Domain.Tests;

public class ExternalCliHistoryTextBuilderTests
{
    [Fact]
    public void Build_IncludesCliThreadIdSourcePathAndMessages()
    {
        var output = ExternalCliHistoryTextBuilder.Build(
            "当前 CLI 会话历史 abc12345",
            [
                new ExternalCliHistoryMessage
                {
                    Role = "user",
                    Content = "  第一条\r\n第二行 ",
                    CreatedAt = new DateTime(2026, 5, 4, 9, 30, 0)
                },
                new ExternalCliHistoryMessage
                {
                    Role = "assistant",
                    Content = "回复内容",
                    CreatedAt = new DateTime(2026, 5, 4, 9, 31, 0)
                }
            ],
            "Codex",
            @"D:\repo",
            "codex-thread-1",
            @"D:\repo\.codex\sessions\2026\05\04\rollout-1.jsonl");

        Assert.Contains("当前 CLI 会话历史 abc12345", output);
        Assert.True(
            output.IndexOf("当前 CLI 会话历史 abc12345", StringComparison.Ordinal) <
            output.IndexOf(@"历史来源: D:\repo\.codex\sessions\2026\05\04\rollout-1.jsonl", StringComparison.Ordinal));
        Assert.Contains("CLI 工具: Codex", output);
        Assert.Contains(@"工作目录: D:\repo", output);
        Assert.Contains("原生 Thread ID: codex-thread-1", output);
        Assert.Contains(@"历史来源: D:\repo\.codex\sessions\2026\05\04\rollout-1.jsonl", output);
        Assert.Contains("显示条数: 最近 2 条", output);
        Assert.Contains("[用户] 09:30", output);
        Assert.Contains("第一条\n第二行", output);
        Assert.Contains("[助手] 09:31", output);
        Assert.Contains("回复内容", output);
    }

    [Fact]
    public void Build_ShowsFallbacks_WhenThreadIdAndSourcePathAreMissing()
    {
        var output = ExternalCliHistoryTextBuilder.Build(
            "当前 CLI 会话历史",
            [],
            "Codex",
            null,
            null,
            null);

        Assert.Contains("原生 Thread ID: 未绑定", output);
        Assert.Contains("历史来源: 未定位", output);
        Assert.Contains("该 CLI 会话暂无可解析的历史消息。", output);
    }
}
