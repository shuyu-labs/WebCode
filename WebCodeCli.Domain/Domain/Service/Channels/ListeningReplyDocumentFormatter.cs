using System.Text;
using System.Text.RegularExpressions;

namespace WebCodeCli.Domain.Domain.Service.Channels;

public static partial class ListeningReplyDocumentFormatter
{
    private const string PlaceholderPrefix = "\u6587\u4ef6\u5185\u5bb9";
    private const string CommandPlaceholderPrefix = "\u547D\u4EE4\u5185\u5BB9";
    private const char ChineseColon = '\uFF1A';

    public static string Format(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        var commandExtraction = ExtractCommandBlocks(input);
        var rewrittenInput = commandExtraction.RewrittenText;
        var matches = FileReferenceRegex().Matches(rewrittenInput);
        if (matches.Count == 0 && commandExtraction.OrderedCommands.Count == 0)
        {
            return input;
        }

        var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
        var orderedReferences = new List<string>();
        var rewritten = FileReferenceRegex().Replace(rewrittenInput, match =>
        {
            var value = match.Value;
            if (IsLikelyVersionToken(value))
            {
                return value;
            }

            if (!mapping.TryGetValue(value, out var placeholder))
            {
                placeholder = $"{PlaceholderPrefix}{mapping.Count + 1}";
                mapping[value] = placeholder;
                orderedReferences.Add(value);
            }

            return placeholder;
        });
        rewritten = RestoreMaskedCodeBlocks(rewritten, commandExtraction.MaskedCodeBlocks);

        var builder = new StringBuilder(rewritten.Trim());
        AppendFileAppendix(builder, orderedReferences);
        AppendCommandAppendix(builder, commandExtraction.OrderedCommands);
        return builder.ToString();
    }

    private static CommandExtractionResult ExtractCommandBlocks(string input)
    {
        var commandMapping = new Dictionary<string, string>(StringComparer.Ordinal);
        var orderedCommands = new List<string>();
        var maskedCodeBlocks = new Dictionary<string, string>(StringComparer.Ordinal);
        var maskedCodeBlockCount = 0;
        var rewritten = FencedCodeBlockRegex().Replace(input, match =>
        {
            var language = match.Groups["language"].Value;
            var content = NormalizeCommandContent(match.Groups["content"].Value);
            if (!IsCommandBlock(language, content))
            {
                var mask = $"__LISTENING_CODE_BLOCK_{++maskedCodeBlockCount}__";
                maskedCodeBlocks[mask] = match.Value;
                return mask;
            }

            var key = $"{language}\n{content}";
            if (!commandMapping.TryGetValue(key, out var placeholder))
            {
                placeholder = $"[{CommandPlaceholderPrefix}{commandMapping.Count + 1}]";
                commandMapping[key] = placeholder;
                orderedCommands.Add(content);
            }

            return placeholder;
        });

        rewritten = ExtractStandaloneCommandLines(rewritten, commandMapping, orderedCommands);

        return new CommandExtractionResult(rewritten, orderedCommands, maskedCodeBlocks);
    }

    private static string ExtractStandaloneCommandLines(
        string input,
        IDictionary<string, string> commandMapping,
        ICollection<string> orderedCommands)
    {
        var builder = new StringBuilder(input.Length);
        var index = 0;
        while (index < input.Length)
        {
            var lineStart = index;
            while (index < input.Length && input[index] is not '\r' and not '\n')
            {
                index++;
            }

            var line = input[lineStart..index];
            if (TryExtractStandaloneCommandLine(line, out var commandText, out var replacement))
            {
                if (!commandMapping.TryGetValue(commandText, out var placeholder))
                {
                    placeholder = $"[{CommandPlaceholderPrefix}{commandMapping.Count + 1}]";
                    commandMapping[commandText] = placeholder;
                    orderedCommands.Add(commandText);
                }

                builder.Append(replacement(placeholder));
            }
            else
            {
                builder.Append(line);
            }

            if (index >= input.Length)
            {
                continue;
            }

            if (input[index] == '\r' && index + 1 < input.Length && input[index + 1] == '\n')
            {
                builder.Append("\r\n");
                index += 2;
                continue;
            }

            builder.Append(input[index]);
            index++;
        }

        return builder.ToString();
    }

    private static bool TryExtractStandaloneCommandLine(
        string line,
        out string commandText,
        out Func<string, string> replacement)
    {
        commandText = string.Empty;
        replacement = static _ => string.Empty;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var listMatch = MarkdownListCommandRegex().Match(line);
        if (listMatch.Success)
        {
            commandText = NormalizeCommandContent(listMatch.Groups["command"].Value);
            if (!IsLikelyCommandLine(commandText))
            {
                commandText = string.Empty;
                return false;
            }

            var prefix = listMatch.Groups["prefix"].Value;
            var suffix = listMatch.Groups["suffix"].Value;
            replacement = placeholder => $"{prefix}{placeholder}{suffix}";
            return true;
        }

        var standaloneMatch = StandaloneCommandLineRegex().Match(line);
        if (!standaloneMatch.Success)
        {
            return false;
        }

        commandText = NormalizeCommandContent(standaloneMatch.Groups["command"].Value);
        if (!IsLikelyCommandLine(commandText))
        {
            commandText = string.Empty;
            return false;
        }

        var standalonePrefix = standaloneMatch.Groups["prefix"].Value;
        var standaloneSuffix = standaloneMatch.Groups["suffix"].Value;
        replacement = placeholder => $"{standalonePrefix}{placeholder}{standaloneSuffix}";
        return true;
    }

    private static void AppendFileAppendix(StringBuilder builder, IReadOnlyList<string> orderedReferences)
    {
        if (orderedReferences.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine();
        for (var i = 0; i < orderedReferences.Count; i++)
        {
            var reference = orderedReferences[i];
            builder.Append(PlaceholderPrefix)
                .Append(i + 1)
                .Append(ChineseColon)
                .Append(reference);

            if (i < orderedReferences.Count - 1)
            {
                builder.AppendLine();
            }
        }
    }

    private static void AppendCommandAppendix(StringBuilder builder, IReadOnlyList<string> orderedCommands)
    {
        if (orderedCommands.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine();
        for (var i = 0; i < orderedCommands.Count; i++)
        {
            if (i > 0)
            {
                builder.AppendLine();
                builder.AppendLine();
            }

            builder.Append(CommandPlaceholderPrefix)
                .Append(i + 1)
                .Append(ChineseColon)
                .Append(orderedCommands[i]);
        }
    }

    private static bool IsCommandBlock(string? language, string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(language))
        {
            var normalizedLanguage = language.Trim().ToLowerInvariant();
            if (normalizedLanguage is "powershell" or "pwsh" or "bash" or "sh" or "shell" or "zsh" or "cmd" or "bat" or "batch" or "console")
            {
                return true;
            }
        }

        var nonEmptyLines = content
            .Split(["\r\n", "\n"], StringSplitOptions.None)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        return nonEmptyLines.Length > 0 && nonEmptyLines.All(IsLikelyCommandLine);
    }

    private static bool IsLikelyCommandLine(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.StartsWith("powershell ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("pwsh ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("dotnet ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("git ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("npm ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("pnpm ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("yarn ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("npx ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("node ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("python ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("bash ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("sh ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("cmd ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("curl ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("wget ", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeCommandContent(string content)
    {
        return content.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
    }

    private static string RestoreMaskedCodeBlocks(string input, IReadOnlyDictionary<string, string> maskedCodeBlocks)
    {
        var restored = input;
        foreach (var maskedCodeBlock in maskedCodeBlocks)
        {
            restored = restored.Replace(maskedCodeBlock.Key, maskedCodeBlock.Value, StringComparison.Ordinal);
        }

        return restored;
    }

    private static bool IsLikelyVersionToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return Regex.IsMatch(value, @"^[vV]?\d+(?:\.\d+){1,}$", RegexOptions.CultureInvariant);
    }

    [GeneratedRegex(@"(?<![A-Za-z0-9_@./\\-])(?:(?:/[A-Za-z]:[/\\]|(?:[A-Za-z]:)?[/\\]|[A-Za-z0-9_.-]+[/\\]))?(?:[A-Za-z0-9_.:-]+[/\\])*[A-Za-z0-9_-]+(?:\.[A-Za-z0-9_+-]+)+(?::\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex FileReferenceRegex();

    [GeneratedRegex("```(?<language>[A-Za-z0-9_-]+)?[ \\t]*\\r?\\n(?<content>.*?)\\r?\\n```", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex FencedCodeBlockRegex();

    [GeneratedRegex(@"^(?<prefix>\s*[-*]\s*)(?:`(?<command>[^`]+)`|(?<command>.+?))(?<suffix>\s*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownListCommandRegex();

    [GeneratedRegex(@"^(?<prefix>\s*)(?:`(?<command>[^`]+)`|(?<command>(?:powershell|pwsh|dotnet|git|npm|pnpm|yarn|npx|node|python|bash|sh|cmd|curl|wget)\b.+?))(?<suffix>\s*)$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StandaloneCommandLineRegex();

    private sealed record CommandExtractionResult(
        string RewrittenText,
        IReadOnlyList<string> OrderedCommands,
        IReadOnlyDictionary<string, string> MaskedCodeBlocks);
}
