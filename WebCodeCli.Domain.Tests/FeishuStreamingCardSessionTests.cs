using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service.Channels;

namespace WebCodeCli.Domain.Tests;

public class FeishuStreamingCardSessionTests
{
    [Fact]
    public async Task UpdateAsync_AllowsUpToTenReplacementCardsBeforeStopping()
    {
        var createdHandles = new List<FeishuStreamingHandle>();
        var replacementFactoryCalls = 0;
        var currentHandle = CreateAlwaysFailingHandle("card-0");
        createdHandles.Add(currentHandle);

        var session = new FeishuStreamingCardSession(
            currentHandle,
            (stoppedHandle, latestContent, cancellationToken) =>
            {
                replacementFactoryCalls++;
                var replacement = CreateAlwaysFailingHandle($"card-{replacementFactoryCalls}");
                createdHandles.Add(replacement);
                return Task.FromResult<FeishuStreamingHandle?>(replacement);
            });

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            var updated = await session.UpdateAsync($"content-{attempt}", CancellationToken.None);
            Assert.True(updated);
            Assert.Equal($"card-{attempt}", session.CurrentHandle.CardId);
        }

        var eleventhReplacement = await session.UpdateAsync("content-11", CancellationToken.None);

        Assert.False(eleventhReplacement);
        Assert.Equal("card-10", session.CurrentHandle.CardId);
        Assert.Equal(10, replacementFactoryCalls);
        Assert.Equal(11, createdHandles.Count);
    }

    [Fact]
    public async Task UpdateAsync_WhenDeferredReplacementEnabled_WaitsForForegroundUpdateBeforeCreatingReplacement()
    {
        var createdHandles = new List<FeishuStreamingHandle>();
        var currentHandle = CreateHandle("card-0", failUpdateOnAttempt: 1);
        createdHandles.Add(currentHandle);

        var session = new FeishuStreamingCardSession(
            currentHandle,
            (stoppedHandle, latestContent, cancellationToken) =>
            {
                var replacement = CreateHandle("card-1");
                createdHandles.Add(replacement);
                return Task.FromResult<FeishuStreamingHandle?>(replacement);
            },
            deferReplacementUntilNextForegroundUpdate: true);

        var firstUpdate = await session.UpdateAsync(
            "content-1",
            CancellationToken.None,
            allowPendingReplacementActivation: true);
        var quietBackgroundUpdate = await session.UpdateAsync(
            "content-2",
            CancellationToken.None,
            allowPendingReplacementActivation: false);

        Assert.True(firstUpdate);
        Assert.True(quietBackgroundUpdate);
        Assert.True(session.HasPendingReplacement);
        Assert.Equal("card-0", session.CurrentHandle.CardId);
        Assert.Single(createdHandles);

        var foregroundRecoveryUpdate = await session.UpdateAsync(
            "content-2",
            CancellationToken.None,
            allowPendingReplacementActivation: true);

        Assert.True(foregroundRecoveryUpdate);
        Assert.False(session.HasPendingReplacement);
        Assert.Equal("card-1", session.CurrentHandle.CardId);
        Assert.Equal(2, createdHandles.Count);
    }

    private static FeishuStreamingHandle CreateAlwaysFailingHandle(string cardId)
    {
        return new FeishuStreamingHandle(
            cardId,
            $"message-{cardId}",
            (content, sequence) => Task.FromResult(false),
            (content, sequence) => Task.FromResult(false),
            throttleMs: 0);
    }

    private static FeishuStreamingHandle CreateHandle(string cardId, int? failUpdateOnAttempt = null)
    {
        var updateAttemptCount = 0;
        return new FeishuStreamingHandle(
            cardId,
            $"message-{cardId}",
            (content, sequence) =>
            {
                updateAttemptCount++;
                return Task.FromResult(!failUpdateOnAttempt.HasValue || updateAttemptCount < failUpdateOnAttempt.Value);
            },
            (content, sequence) => Task.FromResult(true),
            throttleMs: 0);
    }
}
