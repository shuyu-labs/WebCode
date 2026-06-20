using WebCodeCli.Domain.Domain.Service.Adapters;

namespace WebCodeCli.Domain.Tests;

public sealed class CodexAdapterTests
{
    [Fact]
    public void ParseOutputLine_WhenAgentMessageIncludesPhase_PreservesAssistantPhase()
    {
        var adapter = new CodexAdapter();

        var outputEvent = adapter.ParseOutputLine(
            """{"type":"item.updated","item":{"type":"agent_message","text":"hello","phase":"final_answer"}}""");

        Assert.NotNull(outputEvent);
        Assert.Equal("agent_message", outputEvent!.ItemType);
        Assert.Equal("hello", adapter.ExtractAssistantMessage(outputEvent));
        Assert.Equal("final_answer", outputEvent.AssistantPhase);
    }

    [Fact]
    public void ParseOutputLine_WhenCompletedAgentMessageIncludesPhase_PreservesAssistantPhase()
    {
        var adapter = new CodexAdapter();

        var outputEvent = adapter.ParseOutputLine(
            """{"type":"item.completed","item":{"type":"agent_message","text":"done","phase":"final_answer"}}""");

        Assert.NotNull(outputEvent);
        Assert.Equal("agent_message", outputEvent!.ItemType);
        Assert.Equal("done", adapter.ExtractAssistantMessage(outputEvent));
        Assert.Equal("final_answer", outputEvent.AssistantPhase);
    }
}
