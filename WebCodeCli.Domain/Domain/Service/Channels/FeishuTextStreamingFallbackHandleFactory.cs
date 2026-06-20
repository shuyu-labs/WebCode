using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Domain.Domain.Service.Channels;

internal static class FeishuTextStreamingFallbackHandleFactory
{
    private const int MaxChunkChars = 3000;
    private const int InitialTailChars = 6000;
    private const string OverflowNotice = "飞书卡片已超限，后续改为普通文本继续输出。";
    private const string TruncationNotice = "前文已截断，仅显示最新内容。";

    public static async Task<FeishuStreamingHandle> CreateAsync(
        IFeishuCardKitClient cardKit,
        string chatId,
        string? replyMessageId,
        string initialContent,
        FeishuOptions effectiveOptions,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(cardKit);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        ArgumentNullException.ThrowIfNull(effectiveOptions);

        var state = new TextStreamingFallbackState(cardKit, chatId, effectiveOptions);
        var messageId = await state.SendInitialAsync(replyMessageId, initialContent, cancellationToken);

        return new FeishuStreamingHandle(
            "text-fallback",
            messageId,
            (content, _) => state.UpdateAsync(content, CancellationToken.None),
            (content, _) => state.FinishAsync(content, CancellationToken.None),
            effectiveOptions.StreamingThrottleMs);
    }

    private sealed class TextStreamingFallbackState(
        IFeishuCardKitClient cardKit,
        string chatId,
        FeishuOptions effectiveOptions)
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private string _lastSourceContent = string.Empty;
        private string? _lastReplyTargetMessageId;

        public async Task<string> SendInitialAsync(
            string? replyMessageId,
            string initialContent,
            CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                var truncated = initialContent.Length > InitialTailChars;
                var displayContent = truncated
                    ? TakeTail(initialContent, InitialTailChars)
                    : initialContent;
                var segments = BuildInitialSegments(displayContent, truncated);
                var firstTarget = replyMessageId;
                string? firstMessageId = null;
                string? lastMessageId = null;

                foreach (var segment in segments)
                {
                    string currentMessageId;
                    if (string.IsNullOrWhiteSpace(lastMessageId) && !string.IsNullOrWhiteSpace(firstTarget))
                    {
                        currentMessageId = await cardKit.ReplyTextMessageAsync(firstTarget, segment, cancellationToken, effectiveOptions);
                    }
                    else if (string.IsNullOrWhiteSpace(lastMessageId))
                    {
                        currentMessageId = await cardKit.SendTextMessageAsync(chatId, segment, cancellationToken, effectiveOptions);
                    }
                    else
                    {
                        currentMessageId = await cardKit.ReplyTextMessageAsync(lastMessageId, segment, cancellationToken, effectiveOptions);
                    }

                    firstMessageId ??= currentMessageId;
                    lastMessageId = currentMessageId;
                }

                _lastReplyTargetMessageId = lastMessageId ?? firstMessageId;
                _lastSourceContent = initialContent;
                return firstMessageId ?? lastMessageId ?? string.Empty;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public async Task<bool> UpdateAsync(string content, CancellationToken cancellationToken)
        {
            await _sendLock.WaitAsync(cancellationToken);
            try
            {
                var delta = ComputeDelta(_lastSourceContent, content);
                if (string.IsNullOrEmpty(delta))
                {
                    _lastSourceContent = content;
                    return true;
                }

                foreach (var segment in SplitIntoChunks(delta))
                {
                    if (string.IsNullOrWhiteSpace(_lastReplyTargetMessageId))
                    {
                        _lastReplyTargetMessageId = await cardKit.SendTextMessageAsync(chatId, segment, cancellationToken, effectiveOptions);
                    }
                    else
                    {
                        _lastReplyTargetMessageId = await cardKit.ReplyTextMessageAsync(_lastReplyTargetMessageId, segment, cancellationToken, effectiveOptions);
                    }
                }

                _lastSourceContent = content;
                return true;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public Task<bool> FinishAsync(string content, CancellationToken cancellationToken)
            => UpdateAsync(content, cancellationToken);
    }

    private static IReadOnlyList<string> BuildInitialSegments(string content, bool truncated)
    {
        var prefix = truncated
            ? $"{OverflowNotice}\n\n{TruncationNotice}"
            : OverflowNotice;

        if (string.IsNullOrWhiteSpace(content))
        {
            return [prefix];
        }

        var contentSegments = SplitIntoChunks(content);
        var segments = new List<string>(contentSegments.Count + 1)
        {
            $"{prefix}\n\n{contentSegments[0]}"
        };

        for (var index = 1; index < contentSegments.Count; index++)
        {
            segments.Add(contentSegments[index]);
        }

        return segments;
    }

    private static List<string> SplitIntoChunks(string content)
    {
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var segments = new List<string>();
        var start = 0;
        while (start < normalized.Length)
        {
            var length = Math.Min(MaxChunkChars, normalized.Length - start);
            var segment = normalized.Substring(start, length);
            segments.Add(segment);
            start += length;
        }

        if (segments.Count == 0)
        {
            segments.Add(string.Empty);
        }

        return segments;
    }

    private static string ComputeDelta(string previousContent, string currentContent)
    {
        if (string.IsNullOrEmpty(currentContent))
        {
            return string.Empty;
        }

        if (string.IsNullOrEmpty(previousContent))
        {
            return currentContent;
        }

        if (currentContent.StartsWith(previousContent, StringComparison.Ordinal))
        {
            return currentContent[previousContent.Length..];
        }

        return $"{TruncationNotice}\n\n{TakeTail(currentContent, InitialTailChars)}";
    }

    private static string TakeTail(string content, int maxChars)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxChars)
        {
            return content;
        }

        var tail = content[^maxChars..].TrimStart();
        var newlineIndex = tail.IndexOf('\n');
        if (newlineIndex >= 0 && newlineIndex < tail.Length - 1)
        {
            return tail[(newlineIndex + 1)..].TrimStart();
        }

        return tail;
    }
}
