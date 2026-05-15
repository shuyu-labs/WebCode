using WebCodeCli.Domain.Domain.Service.Channels;

namespace WebCodeCli.Domain.Tests;

public sealed class ReplyTtsSpeechTextNormalizerTests
{
    [Fact]
    public void Normalize_RemovesMarkdownLinksAndCodeBlocks()
    {
        var normalizer = new ReplyTtsSpeechTextNormalizer();

        var result = normalizer.Normalize(
            """
            # Heading

            - **Bold** item with [docs](https://example.com/docs)
            Visit https://example.com/raw for more.

            ```csharp
            Console.WriteLine("hi");
            ```
            """);

        var expected = """
            Heading
            Bold item with docs
            Visit this link for more.

            Code snippet omitted.
            """
            .ReplaceLineEndings("\n")
            .Trim();

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Normalize_ReplacesInlineTechnicalReferencesWithFileClassAndMethodNames()
    {
        var normalizer = new ReplyTtsSpeechTextNormalizer();

        var result = normalizer.Normalize(
            """
            Check `Cimc.Tianda.Wms.Web/src/views/mobile/receiving/index.vue:125`.
            Fields come from `lotAttr1 / lotAttr8 / attr1 / attr2 / containerAttr2`.
            Run `dotnet test WebCodeCli.Domain.Tests`.
            Call `WebCodeCli.Domain.Domain.Service.Channels.SherpaKokoroTtsClient.SynthesizeAsync`.
            Call `GetStockByContainerCodeOrBarcode(containerCodeOrBarcode)`.
            """);

        Assert.Contains("index.vue 文件", result, StringComparison.Ordinal);
        Assert.Contains("若干属性字段", result, StringComparison.Ordinal);
        Assert.Contains("相关命令", result, StringComparison.Ordinal);
        Assert.Contains("SherpaKokoroTtsClient 类 SynthesizeAsync 方法", result, StringComparison.Ordinal);
        Assert.Contains("GetStockByContainerCodeOrBarcode 方法", result, StringComparison.Ordinal);
        Assert.DoesNotContain("Cimc.Tianda.Wms.Web/src/views/mobile/receiving/index.vue", result, StringComparison.Ordinal);
        Assert.DoesNotContain("WebCodeCli.Domain.Domain.Service.Channels.SherpaKokoroTtsClient.SynthesizeAsync", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_TreatsBareCodeFileNamesAsFiles()
    {
        var normalizer = new ReplyTtsSpeechTextNormalizer();

        var result = normalizer.Normalize(
            """
            ConfigurationPublicationControllerTests.cs、ConfigurationPublicationDeliveryInboxControllerTests.cs、
            ConfigurationPublicationDispatchAuditControllerTests.cs、ConfigurationPublicationApiIntegrationTests.cs、
            VersionGovernanceApiIntegrationTests.cs 这几组测试继续收口。
            """);

        Assert.Contains("ConfigurationPublicationControllerTests.cs 文件", result, StringComparison.Ordinal);
        Assert.Contains("ConfigurationPublicationDeliveryInboxControllerTests.cs 文件", result, StringComparison.Ordinal);
        Assert.Contains("ConfigurationPublicationDispatchAuditControllerTests.cs 文件", result, StringComparison.Ordinal);
        Assert.Contains("ConfigurationPublicationApiIntegrationTests.cs 文件", result, StringComparison.Ordinal);
        Assert.Contains("VersionGovernanceApiIntegrationTests.cs 文件", result, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs 方法", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_GenericallyHandlesCodeLikeIdentifiersWithoutDomainSentenceTemplates()
    {
        var normalizer = new ReplyTtsSpeechTextNormalizer();

        var result = normalizer.Normalize(
            """
            第二步，后端收货入库事务改造，在 1397 重写 MoveInStereo，调用 GetStockByContainerCodeOrBarcode，并检查 container.Type。
            继续按 superpowers:brainstorming 收尾设计，不进代码。
            """);

        Assert.Contains("MoveInStereo 方法", result, StringComparison.Ordinal);
        Assert.Contains("GetStockByContainerCodeOrBarcode 方法", result, StringComparison.Ordinal);
        Assert.Contains("container 类 Type 方法", result, StringComparison.Ordinal);
        Assert.Contains("superpowers 类 brainstorming 方法", result, StringComparison.Ordinal);
        Assert.DoesNotContain("第二步会改造后端收货入库事务", result, StringComparison.Ordinal);
        Assert.DoesNotContain("入库流程", result, StringComparison.Ordinal);
        Assert.DoesNotContain("扫码查询接口", result, StringComparison.Ordinal);
    }

    [Fact]
    public void Normalize_DoesNotApplyReceivingPlanSpecificSentenceRewrites()
    {
        var normalizer = new ReplyTtsSpeechTextNormalizer();

        var result = normalizer.Normalize(
            """
            前端收口设计页面 `Cimc.Tianda.Wms.Web/src/views/mobile/receiving/index.vue:1` 保留扫码后多记录卡片展示这个方向。
            客户简称 整列删掉，不再出现在顶部或列表里。
            """);

        Assert.DoesNotContain("这个页面会保留扫码后的多记录卡片展示", result, StringComparison.Ordinal);
        Assert.DoesNotContain("客户简称这一列会删除", result, StringComparison.Ordinal);
        Assert.Contains("前端收口设计页面", result, StringComparison.Ordinal);
        Assert.Contains("index.vue 文件", result, StringComparison.Ordinal);
    }
}
