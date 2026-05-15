using WebCodeCli.Domain.Domain.Service.Channels;

namespace WebCodeCli.Domain.Tests;

public sealed class ReplyTtsChunkerTests
{
    [Fact]
    public void Split_WhenParagraphsFitLimit_KeepsSingleChunk()
    {
        var chunker = new ReplyTtsChunker(maxChars: 80);

        var chunks = chunker.Split("第一段很短。\n\n第二段也很短。");

        Assert.Collection(
            chunks,
            chunk => Assert.Equal("第一段很短。\n\n第二段也很短。", chunk));
    }

    [Fact]
    public void Split_WhenParagraphBoundaryFitsBetter_PrefersParagraphChunksBeforeSentenceFallback()
    {
        var chunker = new ReplyTtsChunker(maxChars: 10);

        var chunks = chunker.Split("第一段很短。\n\n第二段也短。");

        Assert.Collection(
            chunks,
            chunk => Assert.Equal("第一段很短。", chunk),
            chunk => Assert.Equal("第二段也短。", chunk));
    }

    [Fact]
    public void Split_WhenParagraphExceedsLimit_SplitsOnSentenceBoundariesFirst()
    {
        var chunker = new ReplyTtsChunker(maxChars: 10);

        var chunks = chunker.Split("第一句很短。第二句也短。第三句也短。");

        Assert.Collection(
            chunks,
            chunk => Assert.Equal("第一句很短。", chunk),
            chunk => Assert.Equal("第二句也短。", chunk),
            chunk => Assert.Equal("第三句也短。", chunk));
    }

    [Fact]
    public void Split_WhenStructuredShortLinesFitLimit_KeepsLargerChunkForPrimaryPass()
    {
        var chunker = new ReplyTtsChunker(maxChars: 120);

        var chunks = chunker.Split(
            """
            顶部区域只放公共字段：
            条码
            托盘号
            当前库位
            分区（可改，下拉）
            """);

        Assert.Collection(
            chunks,
            chunk => Assert.Equal("顶部区域只放公共字段：\n条码\n托盘号\n当前库位\n分区（可改，下拉）", chunk));
    }

    [Fact]
    public void SplitForRetry_WhenStructuredShortLines_GroupsAdjacentLinesIntoPairs()
    {
        var chunker = new ReplyTtsChunker(maxChars: 120);

        var chunks = chunker.SplitForRetry(
            """
            顶部区域只放公共字段：
            条码
            托盘号
            当前库位
            分区（可改，下拉）
            """);

        Assert.Collection(
            chunks,
            chunk => Assert.Equal("顶部区域只放公共字段：\n条码", chunk),
            chunk => Assert.Equal("托盘号\n当前库位", chunk),
            chunk => Assert.Equal("分区（可改，下拉）", chunk));
    }

    [Fact]
    public void SplitForRetry_WhenOnlyTwoStructuredLines_RemainsAbleToSplitToSingles()
    {
        var chunker = new ReplyTtsChunker(maxChars: 120);

        var chunks = chunker.SplitForRetry("第一句。\n第二句。");

        Assert.Collection(
            chunks,
            chunk => Assert.Equal("第一句。", chunk),
            chunk => Assert.Equal("第二句。", chunk));
    }

    [Fact]
    public void SplitForRetry_WhenSingleParagraphStillNeedsSmallerChunks_SplitsBySentence()
    {
        var chunker = new ReplyTtsChunker(maxChars: 120);

        var chunks = chunker.SplitForRetry("第一句很短。第二句也很短。第三句也很短。");

        Assert.Collection(
            chunks,
            chunk => Assert.Equal("第一句很短。", chunk),
            chunk => Assert.Equal("第二句也很短。", chunk),
            chunk => Assert.Equal("第三句也很短。", chunk));
    }
}
