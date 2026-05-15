using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using WebCodeCli.Domain.Common.Extensions;

namespace WebCodeCli.Domain.Domain.Service.Channels;

[ServiceDescription(typeof(ReplyTtsSpeechTextNormalizer), ServiceLifetime.Singleton)]
public sealed class ReplyTtsSpeechTextNormalizer
{
    private static readonly Regex CodeBlockRegex = new("```.*?```", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex InlineCodeRegex = new("`(?<code>[^`]+)`", RegexOptions.Compiled);
    private static readonly Regex MarkdownLinkRegex = new(@"\[(?<text>[^\]]+)\]\((?<url>https?://[^)]+)\)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex RawUrlRegex = new(@"https?://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex HeadingRegex = new(@"^\s{0,3}#{1,6}\s*", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex QuotePrefixRegex = new(@"^\s{0,3}>\s?", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex BulletPrefixRegex = new(@"^\s*(?:[-+*]|\d+\.)\s+", RegexOptions.Multiline | RegexOptions.Compiled);
    private static readonly Regex FileReferenceRegex = new(@"\b(?:[A-Za-z0-9_.-]+[/\\])+[A-Za-z0-9_.-]+(?::\d+)?\b", RegexOptions.Compiled);
    private static readonly Regex SlashSeparatedIdentifierListRegex = new(@"\b[A-Za-z][A-Za-z0-9_]*(?:\s*/\s*[A-Za-z][A-Za-z0-9_]*){2,}\b", RegexOptions.Compiled);
    private static readonly Regex CodeLikeIdentifierRegex = new(
        @"(?<![\p{L}\p{N}_])(?:[A-Za-z][A-Za-z0-9_]*(?:[.:/\\-][A-Za-z0-9_]+)+|[A-Za-z0-9_]*[a-z][A-Za-z0-9_]*[A-Z][A-Za-z0-9_]*|[A-Za-z]+[0-9][A-Za-z0-9_]*)(?![\p{L}\p{N}_]|\s+(?:文件|类|方法|成员))",
        RegexOptions.Compiled);
    private static readonly Regex SingleAsteriskRegex = new(@"(?<!\*)\*(?!\*)", RegexOptions.Compiled);
    private static readonly Regex SingleUnderscoreRegex = new(@"(?<!_)_(?!_)", RegexOptions.Compiled);
    private static readonly Regex CjkSlashRegex = new(@"(?<left>[\p{IsCJKUnifiedIdeographs}A-Za-z0-9]+)\s*/\s*(?<right>[\p{IsCJKUnifiedIdeographs}A-Za-z0-9]+)", RegexOptions.Compiled);
    private static readonly Regex CjkInnerSpacesRegex = new(@"(?<=[\p{IsCJKUnifiedIdeographs}])\s+(?=[\p{IsCJKUnifiedIdeographs}])", RegexOptions.Compiled);
    private static readonly Regex PunctuationLeadingSpacesRegex = new(@"[ \t]+(?=[，。；：、“”（）])", RegexOptions.Compiled);
    private static readonly Regex PunctuationTrailingSpacesRegex = new(@"(?<=[，。；：、“”（）])[ \t]+", RegexOptions.Compiled);
    private static readonly Regex TrailingWhitespaceRegex = new(@"[ \t]+\n", RegexOptions.Compiled);
    private static readonly Regex RepeatedBlankLinesRegex = new(@"\n{3,}", RegexOptions.Compiled);
    private static readonly Regex RepeatedSpacesRegex = new(@"[ \t]{2,}", RegexOptions.Compiled);

    public string Normalize(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        var normalized = markdown
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

        normalized = CodeBlockRegex.Replace(normalized, "\nCode snippet omitted.\n");
        normalized = MarkdownLinkRegex.Replace(normalized, static match => match.Groups["text"].Value.Trim());
        normalized = RawUrlRegex.Replace(normalized, "this link");
        normalized = HeadingRegex.Replace(normalized, string.Empty);
        normalized = QuotePrefixRegex.Replace(normalized, string.Empty);
        normalized = BulletPrefixRegex.Replace(normalized, string.Empty);
        normalized = InlineCodeRegex.Replace(normalized, static match => NormalizeInlineCode(match.Groups["code"].Value));
        normalized = normalized
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace('`', ' ');
        normalized = FileReferenceRegex.Replace(normalized, static match => FormatFileReference(match.Value));
        normalized = SlashSeparatedIdentifierListRegex.Replace(normalized, "若干属性字段");
        normalized = CodeLikeIdentifierRegex.Replace(normalized, FormatCodeLikeIdentifierMatch);
        normalized = SingleAsteriskRegex.Replace(normalized, string.Empty);
        normalized = SingleUnderscoreRegex.Replace(normalized, string.Empty);
        normalized = CjkSlashRegex.Replace(normalized, "${left}和${right}");
        normalized = CjkInnerSpacesRegex.Replace(normalized, string.Empty);
        normalized = PunctuationLeadingSpacesRegex.Replace(normalized, string.Empty);
        normalized = PunctuationTrailingSpacesRegex.Replace(normalized, string.Empty);
        normalized = TrailingWhitespaceRegex.Replace(normalized, "\n");
        normalized = RepeatedBlankLinesRegex.Replace(normalized, "\n\n");
        normalized = RepeatedSpacesRegex.Replace(normalized, " ");

        return normalized.Trim();
    }

    private static string NormalizeInlineCode(string code)
    {
        var trimmed = code.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return string.Empty;
        }

        if (FileReferenceRegex.IsMatch(trimmed))
        {
            return FileReferenceRegex.Replace(trimmed, static match => FormatFileReference(match.Value));
        }

        if (SlashSeparatedIdentifierListRegex.IsMatch(trimmed))
        {
            return "若干属性字段";
        }

        if (trimmed.StartsWith("npx ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("npm ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("dotnet ", StringComparison.OrdinalIgnoreCase))
        {
            return "相关命令";
        }

        if (trimmed.Contains('(', StringComparison.Ordinal) || trimmed.Contains(')', StringComparison.Ordinal))
        {
            return FormatCallableReference(trimmed);
        }

        if (trimmed.Contains('{', StringComparison.Ordinal) || trimmed.Contains('}', StringComparison.Ordinal))
        {
            return "相关调用";
        }

        if (CodeLikeIdentifierRegex.IsMatch(trimmed))
        {
            return FormatCodeIdentifier(trimmed);
        }

        return trimmed;
    }

    private static string FormatCodeLikeIdentifierMatch(Match match)
    {
        if (LooksLikeFileName(match.Value))
        {
            return FormatFileReference(match.Value);
        }

        return FormatCodeIdentifier(match.Value);
    }

    private static string FormatFileReference(string reference)
    {
        var withoutLine = RemoveTrailingLineNumber(reference.Trim());
        var normalized = withoutLine.Replace('\\', '/');
        var fileName = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();

        return string.IsNullOrWhiteSpace(fileName)
            ? "相关文件"
            : $"{fileName} 文件";
    }

    private static string FormatCallableReference(string reference)
    {
        var methodCandidate = reference.Split('(', 2, StringSplitOptions.TrimEntries)[0];
        return string.IsNullOrWhiteSpace(methodCandidate)
            ? "相关调用"
            : FormatCodeIdentifier(methodCandidate, preferMethod: true);
    }

    private static string FormatCodeIdentifier(string identifier, bool preferMethod = false)
    {
        var normalized = identifier.Trim().Trim('`', '.', ':', '/', '\\', '-');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "相关技术标识";
        }

        if (LooksLikeFileName(normalized))
        {
            return FormatFileReference(normalized);
        }

        var segments = normalized
            .Split(['.', ':', '/', '\\', '-'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length >= 2)
        {
            var typeName = segments[^2];
            var memberName = segments[^1];
            var memberKind = preferMethod || normalized.Contains(':', StringComparison.Ordinal) || LooksLikeMethodName(memberName)
                ? "方法"
                : "成员";
            return $"{typeName} 类 {memberName} {memberKind}";
        }

        return $"{normalized} 方法";
    }

    private static string RemoveTrailingLineNumber(string reference)
    {
        var colonIndex = reference.LastIndexOf(':');
        if (colonIndex <= 1 || colonIndex == reference.Length - 1)
        {
            return reference;
        }

        return reference[(colonIndex + 1)..].All(char.IsDigit)
            ? reference[..colonIndex]
            : reference;
    }

    private static bool LooksLikeMethodName(string value)
    {
        return value.EndsWith("Async", StringComparison.Ordinal) ||
               value.Contains("(", StringComparison.Ordinal) ||
               value.Length > 0 && char.IsUpper(value[0]);
    }

    private static bool LooksLikeFileName(string value)
    {
        var extension = Path.GetExtension(RemoveTrailingLineNumber(value));
        return extension is ".cs" or ".vue" or ".ts" or ".tsx" or ".js" or ".jsx" or ".json" or ".md" or ".py" or ".html" or ".css" or ".scss";
    }
}
