using WebCodeCli.Domain.Domain.Model.Channels;
using Xunit;

namespace WebCodeCli.Tests;

public sealed class FeishuStreamingErrorFormatterTests
{
    [Fact]
    public void AppendError_WhenContentExists_AppendsSeparatorAndBoldError()
    {
        var result = FeishuStreamingErrorFormatter.AppendError(
            "第一段输出\n第二段输出",
            "exceeded retry limit, last status: 429 Too Many Requests");

        Assert.Equal(
            "第一段输出\n第二段输出\n\n---\n\n**错误：exceeded retry limit, last status: 429 Too Many Requests**",
            result);
    }

    [Fact]
    public void AppendError_WhenContentMissing_ReturnsOnlyBoldError()
    {
        var result = FeishuStreamingErrorFormatter.AppendError(
            null,
            "执行失败");

        Assert.Equal("**错误：执行失败**", result);
    }
}
