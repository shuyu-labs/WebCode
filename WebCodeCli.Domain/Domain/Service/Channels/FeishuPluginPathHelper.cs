namespace WebCodeCli.Domain.Domain.Service.Channels;

/// <summary>
/// 飞书插件路径助手
/// 提供跨平台的插件、技能目录路径获取方法
/// </summary>
public static class FeishuPluginPathHelper
{
    /// <summary>
    /// 获取全局插件目录
    /// </summary>
    /// <returns>插件目录路径</returns>
    public static string GetPluginsDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".claude", "plugins");
    }

    /// <summary>
    /// 获取全局技能目录
    /// </summary>
    /// <returns>技能目录路径</returns>
    public static string GetSkillsDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".claude", "skills");
    }

    /// <summary>
    /// 获取项目技能目录
    /// 从应用基目录向上查找，优先返回 .codex/skills，否则回退到旧的 skills/codex 结构
    /// </summary>
    /// <returns>项目技能目录路径</returns>
    public static string GetProjectSkillsDirectory()
    {
        // 优先查找项目根目录下的 .codex/skills 文件夹
        var baseDir = AppContext.BaseDirectory;

        // 向上查找直到找到技能目录或到达根目录
        var currentDir = baseDir;
        while (!string.IsNullOrEmpty(currentDir))
        {
            var codexSkillsDir = Path.Combine(currentDir, ".codex", "skills");
            if (Directory.Exists(codexSkillsDir))
            {
                return codexSkillsDir;
            }

            var legacySkillsDir = Path.Combine(currentDir, "skills");
            if (Directory.Exists(legacySkillsDir))
            {
                return legacySkillsDir;
            }

            var parent = Directory.GetParent(currentDir);
            currentDir = parent?.FullName ?? string.Empty;
        }

        // 如果没找到，返回基目录下的 .codex/skills（可能不存在）
        return Path.Combine(baseDir, ".codex", "skills");
    }
}
