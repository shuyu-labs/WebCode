using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Common.Options;

namespace WebCodeCli.Domain.Domain.Service.Channels;

[ServiceDescription(typeof(ReplyTtsChunker), ServiceLifetime.Scoped)]
public sealed class ReplyTtsChunker
{
    private static readonly char[] SentenceDelimiters = ['。', '！', '？', '!', '?', ';', '；'];
    private static readonly char[] ClauseDelimiters = ['，', ',', '、', ':', '：'];

    private readonly int _maxChars;
    private readonly int _retryMaxChars;

    [ActivatorUtilitiesConstructor]
    public ReplyTtsChunker(IOptions<FeishuReplyTtsOptions> options)
        : this(options?.Value?.TtsChunkMaxChars ?? 1200)
    {
    }

    public ReplyTtsChunker(int maxChars)
    {
        if (maxChars <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxChars), "Max chars must be greater than zero.");
        }

        _maxChars = maxChars;
        _retryMaxChars = ResolveRetryMaxChars(maxChars);
    }

    public IReadOnlyList<string> Split(string? text)
    {
        var normalized = NormalizeInput(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        var paragraphs = normalized.Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var paragraph in paragraphs)
        {
            if (paragraph.Length > _maxChars)
            {
                FlushCurrentChunk(chunks, current);
                foreach (var paragraphChunk in SplitLongSegment(paragraph, _maxChars))
                {
                    chunks.Add(paragraphChunk);
                }

                continue;
            }

            if (current.Length == 0)
            {
                current.Append(paragraph);
                continue;
            }

            if (current.Length + 2 + paragraph.Length <= _maxChars)
            {
                current.Append("\n\n");
                current.Append(paragraph);
                continue;
            }

            FlushCurrentChunk(chunks, current);
            current.Append(paragraph);
        }

        FlushCurrentChunk(chunks, current);
        return chunks;
    }

    public IReadOnlyList<string> SplitForRetry(string? text)
    {
        var normalized = NormalizeInput(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return [];
        }

        var structuredLines = normalized
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (structuredLines.Length > 1)
        {
            return SplitStructuredLines(structuredLines, _retryMaxChars);
        }

        return SplitRetryLongSegment(normalized, _retryMaxChars);
    }

    private IReadOnlyList<string> SplitStructuredLines(IReadOnlyList<string> structuredLines, int maxChars)
    {
        var lineSegments = new List<string>(structuredLines.Count);
        foreach (var line in structuredLines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (trimmed.Length <= maxChars)
            {
                lineSegments.Add(trimmed);
                continue;
            }

            lineSegments.AddRange(SplitRetryLongSegment(trimmed, maxChars));
        }

        if (lineSegments.Count <= 2)
        {
            return lineSegments;
        }

        var chunks = new List<string>((lineSegments.Count + 1) / 2);
        var current = new StringBuilder();
        var lineCount = 0;

        foreach (var segment in lineSegments)
        {
            if (current.Length == 0)
            {
                current.Append(segment);
                lineCount = 1;
                continue;
            }

            if (lineCount < 2 && current.Length + 1 + segment.Length <= maxChars)
            {
                current.Append('\n');
                current.Append(segment);
                lineCount++;
                continue;
            }

            FlushCurrentChunk(chunks, current);
            current.Append(segment);
            lineCount = 1;
        }

        FlushCurrentChunk(chunks, current);
        return chunks;
    }

    private IReadOnlyList<string> SplitRetryLongSegment(string segment, int maxChars)
    {
        var sentencePieces = SplitByDelimiters(segment, SentenceDelimiters);
        if (sentencePieces.Count > 1)
        {
            return sentencePieces
                .SelectMany(piece => SplitRetryPiece(piece, maxChars))
                .ToList();
        }

        return SplitRetryPiece(segment, maxChars);
    }

    private IEnumerable<string> SplitLongSegment(string segment, int maxChars)
    {
        var sentenceChunks = CombineWithinLimit(SplitByDelimiters(segment, SentenceDelimiters), maxChars);
        if (sentenceChunks.Count > 1 || sentenceChunks[0].Length <= maxChars)
        {
            return sentenceChunks;
        }

        var clauseChunks = CombineWithinLimit(SplitByDelimiters(segment, ClauseDelimiters), maxChars);
        if (clauseChunks.Count > 1 || clauseChunks[0].Length <= maxChars)
        {
            return clauseChunks;
        }

        return HardBreak(segment, maxChars);
    }

    private static IReadOnlyList<string> SplitRetryPiece(string piece, int maxChars)
    {
        var trimmed = piece.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return [];
        }

        if (trimmed.Length <= maxChars)
        {
            return [trimmed];
        }

        var clausePieces = SplitByDelimiters(trimmed, ClauseDelimiters);
        if (clausePieces.Count > 1)
        {
            return clausePieces
                .SelectMany(clause => clause.Length <= maxChars ? [clause.Trim()] : HardBreak(clause, maxChars))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .ToList();
        }

        return HardBreak(trimmed, maxChars);
    }

    private static List<string> CombineWithinLimit(IReadOnlyList<string> pieces, int maxChars)
    {
        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (var piece in pieces.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            var trimmed = piece.Trim();
            if (trimmed.Length > maxChars)
            {
                FlushCurrentChunk(chunks, current);
                chunks.AddRange(HardBreak(trimmed, maxChars));
                continue;
            }

            if (current.Length == 0)
            {
                current.Append(trimmed);
                continue;
            }

            if (current.Length + 1 + trimmed.Length <= maxChars)
            {
                current.Append(' ');
                current.Append(trimmed);
                continue;
            }

            FlushCurrentChunk(chunks, current);
            current.Append(trimmed);
        }

        FlushCurrentChunk(chunks, current);
        return chunks;
    }

    private static List<string> HardBreak(string segment, int maxChars)
    {
        var chunks = new List<string>();
        var remaining = segment.Trim();

        while (remaining.Length > maxChars)
        {
            var breakIndex = remaining.LastIndexOf(' ', maxChars);
            if (breakIndex <= 0)
            {
                breakIndex = maxChars;
            }

            chunks.Add(remaining[..breakIndex].Trim());
            remaining = remaining[breakIndex..].Trim();
        }

        if (!string.IsNullOrWhiteSpace(remaining))
        {
            chunks.Add(remaining);
        }

        return chunks;
    }

    private static List<string> SplitByDelimiters(string segment, IReadOnlyCollection<char> delimiters)
    {
        var pieces = new List<string>();
        var current = new StringBuilder();

        foreach (var character in segment)
        {
            current.Append(character);
            if (delimiters.Contains(character))
            {
                pieces.Add(current.ToString().Trim());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            pieces.Add(current.ToString().Trim());
        }

        return pieces;
    }

    private static void FlushCurrentChunk(ICollection<string> chunks, StringBuilder current)
    {
        if (current.Length == 0)
        {
            return;
        }

        chunks.Add(current.ToString().Trim());
        current.Clear();
    }

    private static string NormalizeInput(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        var lines = normalized
            .Split('\n')
            .Select(static line => line.TrimEnd())
            .ToArray();

        return string.Join("\n", lines).Trim();
    }

    private static int ResolveRetryMaxChars(int maxChars)
    {
        return Math.Min(maxChars, Math.Max(40, Math.Min(maxChars / 2, 160)));
    }
}
