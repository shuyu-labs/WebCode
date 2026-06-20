using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Domain.Domain.Service.Channels;

internal sealed class FeishuStreamingCardSession
{
    internal const int MaxReplacementCardsPerLogicalStream = 10;

    private readonly Func<FeishuStreamingHandle, string, CancellationToken, Task<FeishuStreamingHandle?>> _replacementFactory;
    private readonly Action<FeishuStreamingHandle> _handleChanged;
    private readonly Func<FeishuStreamingHandle, string, CancellationToken, Task>? _stoppedHandleFinalizer;
    private readonly bool _deferReplacementUntilNextForegroundUpdate;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private int _replacementCount;
    private bool _replacementPending;

    public FeishuStreamingCardSession(
        FeishuStreamingHandle initialHandle,
        Func<FeishuStreamingHandle, string, CancellationToken, Task<FeishuStreamingHandle?>> replacementFactory,
        Action<FeishuStreamingHandle>? handleChanged = null,
        Func<FeishuStreamingHandle, string, CancellationToken, Task>? stoppedHandleFinalizer = null,
        bool deferReplacementUntilNextForegroundUpdate = false)
    {
        CurrentHandle = initialHandle;
        _replacementFactory = replacementFactory;
        _handleChanged = handleChanged ?? (_ => { });
        _stoppedHandleFinalizer = stoppedHandleFinalizer;
        _deferReplacementUntilNextForegroundUpdate = deferReplacementUntilNextForegroundUpdate;
    }

    public FeishuStreamingHandle CurrentHandle { get; private set; }

    public bool HasPendingReplacement => _replacementPending;

    public async Task<bool> UpdateAsync(
        string content,
        CancellationToken cancellationToken,
        bool allowPendingReplacementActivation = true)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_replacementPending && CurrentHandle.AreCardUpdatesStopped)
            {
                if (!allowPendingReplacementActivation)
                {
                    return true;
                }

                return await TryCreateReplacementHandleAsync(content, cancellationToken);
            }

            await CurrentHandle.UpdateAsync(content);
            if (!CurrentHandle.AreCardUpdatesStopped)
            {
                return true;
            }

            if (_deferReplacementUntilNextForegroundUpdate)
            {
                _replacementPending = true;
                return true;
            }

            return await TryCreateReplacementHandleAsync(content, cancellationToken);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<bool> FinishAsync(string finalContent)
    {
        await _lock.WaitAsync();
        try
        {
            if (_replacementPending || CurrentHandle.AreCardUpdatesStopped)
            {
                return await TryCreateReplacementAndFinishAsync(finalContent);
            }

            var finished = await CurrentHandle.FinishAsync(finalContent);
            if (finished)
            {
                return true;
            }

            return await TryCreateReplacementAndFinishAsync(finalContent);
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<bool> TryCreateReplacementAndFinishAsync(string finalContent)
    {
        if (!await TryCreateReplacementHandleAsync(finalContent, CancellationToken.None, finalizeStoppedHandle: false))
        {
            return false;
        }

        return await CurrentHandle.FinishAsync(finalContent);
    }

    private async Task<bool> TryCreateReplacementHandleAsync(
        string latestContent,
        CancellationToken cancellationToken,
        bool finalizeStoppedHandle = true)
    {
        if (_replacementCount >= MaxReplacementCardsPerLogicalStream)
        {
            return false;
        }

        var stoppedHandle = CurrentHandle;
        var replacement = await _replacementFactory(stoppedHandle, latestContent, cancellationToken);
        if (replacement == null)
        {
            return false;
        }

        if (finalizeStoppedHandle && _stoppedHandleFinalizer != null)
        {
            try
            {
                await _stoppedHandleFinalizer(stoppedHandle, latestContent, cancellationToken);
            }
            catch
            {
            }
        }

        _replacementCount++;
        _replacementPending = false;
        CurrentHandle = replacement;
        _handleChanged(replacement);
        return true;
    }

    public async Task SwitchHandleAsync(
        FeishuStreamingHandle handle,
        bool resetReplacementCount = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);

        await _lock.WaitAsync(cancellationToken);
        try
        {
            CurrentHandle = handle;
            _replacementPending = false;
            if (resetReplacementCount)
            {
                _replacementCount = 0;
            }

            _handleChanged(handle);
        }
        finally
        {
            _lock.Release();
        }
    }
}
