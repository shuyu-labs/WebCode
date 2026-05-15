using System.Text.RegularExpressions;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Domain.Model;
using Microsoft.Extensions.DependencyInjection;

namespace WebCodeCli.Domain.Domain.Service;

public interface ISkillService
{
    Task<List<SkillItem>> GetSkillsAsync();
}

[ServiceDescription(typeof(ISkillService), ServiceLifetime.Scoped)]
public class SkillService : ISkillService
{
    public async Task<List<SkillItem>> GetSkillsAsync()
    {
        var skills = new List<SkillItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, source) in GetSkillDirectories())
        {
            if (!Directory.Exists(path))
            {
                continue;
            }

            var loaded = await LoadSkillsFromDirectory(path, source);
            foreach (var skill in loaded)
            {
                if (seen.Add($"{skill.Source}:{skill.Name}"))
                {
                    skills.Add(skill);
                }
            }
        }

        return skills
            .OrderBy(s => s.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private IEnumerable<(string Path, string Source)> GetSkillDirectories()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return (Path.Combine(userProfile, ".claude", "skills"), "claude");
        yield return (Path.Combine(userProfile, ".codex", "skills"), "codex");
        yield return (Path.Combine(userProfile, ".opencode", "skills"), "opencode");
        yield return (Path.Combine(userProfile, ".config", "opencode", "skills"), "opencode");

        var projectClaudeSkills = FindProjectDirectory("skills", "claude");
        if (!string.IsNullOrWhiteSpace(projectClaudeSkills))
        {
            yield return (projectClaudeSkills, "claude");
        }

        var projectCodexSkills = FindProjectDirectory(".codex", "skills");
        if (!string.IsNullOrWhiteSpace(projectCodexSkills))
        {
            yield return (projectCodexSkills, "codex");
        }

        var legacyProjectCodexSkills = FindProjectDirectory("skills", "codex");
        if (!string.IsNullOrWhiteSpace(legacyProjectCodexSkills)
            && !string.Equals(legacyProjectCodexSkills, projectCodexSkills, StringComparison.OrdinalIgnoreCase))
        {
            yield return (legacyProjectCodexSkills, "codex");
        }

        var projectOpenCodeSkills = FindProjectDirectory(".opencode", "skills");
        if (!string.IsNullOrWhiteSpace(projectOpenCodeSkills))
        {
            yield return (projectOpenCodeSkills, "opencode");
        }

        // OpenCode 可兼容 Claude Code skills，除非显式关闭。
        if (ShouldIncludeClaudeSkillsForOpenCode())
        {
            yield return (Path.Combine(userProfile, ".claude", "skills"), "opencode");

            if (!string.IsNullOrWhiteSpace(projectClaudeSkills))
            {
                yield return (projectClaudeSkills, "opencode");
            }
        }
    }

    private static bool ShouldIncludeClaudeSkillsForOpenCode()
    {
        var rawValue = Environment.GetEnvironmentVariable("OPENCODE_DISABLE_CLAUDE_CODE_SKILLS");
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return true;
        }

        return !rawValue.Equals("1", StringComparison.OrdinalIgnoreCase)
            && !rawValue.Equals("true", StringComparison.OrdinalIgnoreCase)
            && !rawValue.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string? FindProjectDirectory(params string[] segments)
    {
        var currentDir = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(currentDir))
        {
            var candidate = Path.Combine(new[] { currentDir }.Concat(segments).ToArray());
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            var parent = Directory.GetParent(currentDir);
            currentDir = parent?.FullName ?? string.Empty;
        }

        return null;
    }

    private async Task<List<SkillItem>> LoadSkillsFromDirectory(string skillsPath, string source)
    {
        var skills = new List<SkillItem>();

        try
        {
            var skillDirectories = Directory.GetDirectories(skillsPath);

            foreach (var skillDir in skillDirectories)
            {
                var skillMdPath = Path.Combine(skillDir, "SKILL.md");
                if (!File.Exists(skillMdPath))
                {
                    continue;
                }

                var skill = await ParseSkillFile(skillMdPath, source);
                if (skill != null)
                {
                    skills.Add(skill);
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error loading skills from {skillsPath}: {ex.Message}");
        }

        return skills;
    }

    private async Task<SkillItem?> ParseSkillFile(string filePath, string source)
    {
        try
        {
            var content = await File.ReadAllTextAsync(filePath);

            var frontMatterMatch = Regex.Match(content, @"^---\s*\n(.*?)\n---", RegexOptions.Singleline);
            if (!frontMatterMatch.Success)
            {
                return null;
            }

            var frontMatter = frontMatterMatch.Groups[1].Value;
            var nameMatch = Regex.Match(frontMatter, @"name:\s*(.+)");
            var descriptionMatch = Regex.Match(frontMatter, @"description:\s*(.+)");

            if (!nameMatch.Success)
            {
                return null;
            }

            return new SkillItem
            {
                Name = nameMatch.Groups[1].Value.Trim(),
                Description = descriptionMatch.Success ? descriptionMatch.Groups[1].Value.Trim() : string.Empty,
                Source = source
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing skill file {filePath}: {ex.Message}");
            return null;
        }
    }
}
