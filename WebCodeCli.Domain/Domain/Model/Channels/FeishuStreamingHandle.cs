namespace WebCodeCli.Domain.Domain.Model.Channels;

/// <summary>
/// 飞书流式回复句柄
/// </summary>
public class FeishuStreamingHandle : IDisposable
{
    private readonly Func<string, int, Task<bool>> _updateAsync;
    private readonly Func<string, int, Task<bool>> _finishAsync;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly int _throttleMs;
    private readonly int _quietWindowAfterUpdateMs;
    private DateTime _lastUpdate = DateTime.MinValue;
    private DateTime _quietUntil = DateTime.MinValue;
    private int _sequence = 0;
    private int _cardUpdatesStopped = 0;
    private bool _disposed = false;
    private bool _isFinished = false;

    /// <summary>
    /// 卡片 ID
    /// </summary>
    public string CardId { get; }

    /// <summary>
    /// 消息 ID
    /// </summary>
    public string MessageId { get; }

    /// <summary>
    /// 当前序列号
    /// </summary>
    public int Sequence => _sequence;

    public bool AreCardUpdatesStopped => Volatile.Read(ref _cardUpdatesStopped) == 1;

    public FeishuStreamingHandle(
        string cardId,
        string messageId,
        Func<string, int, Task> updateAsync,
        Func<string, int, Task> finishAsync,
        int throttleMs = 500,
        int quietWindowAfterUpdateMs = 0)
        : this(
            cardId,
            messageId,
            async (content, sequence) =>
            {
                await updateAsync(content, sequence);
                return true;
            },
            async (content, sequence) =>
            {
                await finishAsync(content, sequence);
                return true;
            },
            throttleMs,
            quietWindowAfterUpdateMs)
    {
    }

    public FeishuStreamingHandle(
        string cardId,
        string messageId,
        Func<string, int, Task<bool>> updateAsync,
        Func<string, int, Task<bool>> finishAsync,
        int throttleMs = 500,
        int quietWindowAfterUpdateMs = 0)
    {
        CardId = cardId;
        MessageId = messageId;
        _updateAsync = updateAsync;
        _finishAsync = finishAsync;
        _throttleMs = throttleMs;
        _quietWindowAfterUpdateMs = Math.Max(0, quietWindowAfterUpdateMs);
    }

    /// <summary>
    /// 更新卡片内容（带节流）
    /// </summary>
    public async Task UpdateAsync(string content)
    {
        if (_disposed || _isFinished || AreCardUpdatesStopped)
        {
            return;
        }

        await _operationLock.WaitAsync();
        try
        {
            if (_disposed || _isFinished || AreCardUpdatesStopped)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (now < _quietUntil)
            {
                return;
            }

            if ((now - _lastUpdate).TotalMilliseconds < _throttleMs)
            {
                return; // 节流跳过
            }

            _lastUpdate = now;
            var sequence = Interlocked.Increment(ref _sequence);
            var updated = await _updateAsync(content, sequence);
            if (!updated)
            {
                StopCardUpdates();
                return;
            }

            if (_quietWindowAfterUpdateMs > 0)
            {
                _quietUntil = DateTime.UtcNow.AddMilliseconds(_quietWindowAfterUpdateMs);
            }
        }
        finally
        {
            _operationLock.Release();
        }
    }

    /// <summary>
    /// 完成更新
    /// </summary>
    public async Task<bool> FinishAsync(string finalContent)
    {
        if (_disposed)
        {
            return true;
        }

        await _operationLock.WaitAsync();
        try
        {
            if (_disposed)
            {
                return true;
            }

            if (_isFinished)
            {
                return true;
            }

            var sequence = Interlocked.Increment(ref _sequence);
            var finished = await _finishAsync(finalContent, sequence);
            if (!finished)
            {
                StopCardUpdates();
                return false;
            }

            _isFinished = true;
            _disposed = true;
            return true;
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _isFinished = true;
    }

    public void StopCardUpdates()
    {
        Interlocked.Exchange(ref _cardUpdatesStopped, 1);
    }
}
