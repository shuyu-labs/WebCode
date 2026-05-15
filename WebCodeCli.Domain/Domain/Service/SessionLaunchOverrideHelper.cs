using System.Text.Json;
using System.Text.Json.Serialization;
using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service;

/// <summary>
/// 会话级启动覆盖项共享辅助类
/// </summary>
public static class SessionLaunchOverrideHelper
{
    private static readonly HashSet<string> SupportedTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "codex",
        "claude-code",
        "opencode"
    };

    private static readonly HashSet<string> CodexReasoningEfforts = new(StringComparer.OrdinalIgnoreCase)
    {
        "low",
        "medium",
        "high",
        "xhigh"
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 规范化工具 ID
    /// </summary>
    public static string NormalizeToolId(string? toolId)
    {
        return toolId?.Trim().ToLowerInvariant() switch
        {
            "claude" => "claude-code",
            "opencode-cli" => "opencode",
            var value => value ?? string.Empty
        };
    }

    /// <summary>
    /// 判断工具是否支持会话级启动覆盖项
    /// </summary>
    public static bool SupportsLaunchOverrides(string? toolId)
    {
        return SupportedTools.Contains(NormalizeToolId(toolId));
    }

    /// <summary>
    /// 解析会话的有效工具 ID
    /// </summary>
    public static string ResolveEffectiveToolId(string? sessionToolId, string? snapshotToolId = null)
    {
        var effectiveToolId = string.IsNullOrWhiteSpace(snapshotToolId)
            ? sessionToolId
            : snapshotToolId;
        return NormalizeToolId(effectiveToolId);
    }

    /// <summary>
    /// 从 JSON 反序列化启动覆盖项
    /// </summary>
    public static Dictionary<string, SessionToolLaunchOverride> Deserialize(string? json)
    {
        var result = new Dictionary<string, SessionToolLaunchOverride>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, SessionToolLaunchOverride>>(json, SerializerOptions);
            if (parsed == null)
            {
                return result;
            }

            foreach (var kvp in parsed)
            {
                if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null)
                {
                    continue;
                }

                var normalizedToolId = NormalizeToolId(kvp.Key);
                if (string.IsNullOrWhiteSpace(normalizedToolId))
                {
                    continue;
                }

                var normalizedOverride = NormalizeStoredOverride(kvp.Value);
                if (normalizedOverride == null)
                {
                    continue;
                }

                result[normalizedToolId] = normalizedOverride;
            }
        }
        catch
        {
            return new Dictionary<string, SessionToolLaunchOverride>(StringComparer.OrdinalIgnoreCase);
        }

        return result;
    }

    /// <summary>
    /// 将启动覆盖项序列化为 JSON
    /// </summary>
    public static string? Serialize(IReadOnlyDictionary<string, SessionToolLaunchOverride>? overrides)
    {
        var normalizedOverrides = NormalizeOverrideDictionary(overrides);
        if (normalizedOverrides.Count == 0)
        {
            return null;
        }

        return JsonSerializer.Serialize(normalizedOverrides, SerializerOptions);
    }

    /// <summary>
    /// 获取会话当前有效工具的覆盖项
    /// </summary>
    public static SessionToolLaunchOverride? GetEffectiveOverride(SessionHistory? session, string? toolId = null)
    {
        if (session == null)
        {
            return null;
        }

        return GetEffectiveOverride(
            session.ToolLaunchOverrides,
            toolId,
            session.ToolId,
            session.CcSwitchSnapshotToolId);
    }

    /// <summary>
    /// 获取指定工具的覆盖项
    /// </summary>
    public static SessionToolLaunchOverride? GetEffectiveOverride(
        IReadOnlyDictionary<string, SessionToolLaunchOverride>? overrides,
        string? toolId,
        string? sessionToolId = null,
        string? snapshotToolId = null)
    {
        if (overrides == null || overrides.Count == 0)
        {
            return null;
        }

        var effectiveToolId = !string.IsNullOrWhiteSpace(toolId)
            ? NormalizeToolId(toolId)
            : ResolveEffectiveToolId(sessionToolId, snapshotToolId);

        if (string.IsNullOrWhiteSpace(effectiveToolId))
        {
            return null;
        }

        return overrides.TryGetValue(effectiveToolId, out var existingOverride)
            ? NormalizeStoredOverride(existingOverride)
            : null;
    }

    /// <summary>
    /// 应用单个工具的覆盖项变更
    /// </summary>
    public static Dictionary<string, SessionToolLaunchOverride> ApplyOverride(
        IReadOnlyDictionary<string, SessionToolLaunchOverride>? currentOverrides,
        string toolId,
        string? model,
        string? reasoningEffort,
        bool? usePersistentProcess = null)
    {
        var normalizedToolId = NormalizeToolId(toolId);
        if (!SupportsLaunchOverrides(normalizedToolId))
        {
            throw new ArgumentException($"Tool '{toolId}' does not support launch overrides.", nameof(toolId));
        }

        ValidateOrThrow(normalizedToolId, model, reasoningEffort);

        var updatedOverrides = NormalizeOverrideDictionary(currentOverrides);
        updatedOverrides.TryGetValue(normalizedToolId, out var existingOverride);
        var normalizedModel = NormalizeValue(model);
        var normalizedReasoningEffort = NormalizeValue(reasoningEffort);
        var normalizedUsePersistentProcess = usePersistentProcess ?? existingOverride?.UsePersistentProcess;
        var normalizedOverride = CreateNormalizedOverride(
            normalizedModel,
            normalizedReasoningEffort,
            normalizedUsePersistentProcess,
            existingOverride?.UseGoalRuntime);

        if (normalizedOverride == null)
        {
            updatedOverrides.Remove(normalizedToolId);
            return updatedOverrides;
        }

        updatedOverrides[normalizedToolId] = normalizedOverride;

        return updatedOverrides;
    }

    /// <summary>
    /// 仅更新工具的持久化进程覆盖项
    /// </summary>
    public static Dictionary<string, SessionToolLaunchOverride> ApplyPersistentProcessOverride(
        IReadOnlyDictionary<string, SessionToolLaunchOverride>? currentOverrides,
        string toolId,
        bool? usePersistentProcess)
    {
        var normalizedToolId = NormalizeToolId(toolId);
        if (!SupportsLaunchOverrides(normalizedToolId))
        {
            throw new ArgumentException($"Tool '{toolId}' does not support launch overrides.", nameof(toolId));
        }

        var updatedOverrides = NormalizeOverrideDictionary(currentOverrides);
        updatedOverrides.TryGetValue(normalizedToolId, out var existingOverride);
        var normalizedOverride = CreateNormalizedOverride(
            existingOverride?.Model,
            existingOverride?.ReasoningEffort,
            usePersistentProcess,
            existingOverride?.UseGoalRuntime);

        if (normalizedOverride == null)
        {
            updatedOverrides.Remove(normalizedToolId);
            return updatedOverrides;
        }

        updatedOverrides[normalizedToolId] = normalizedOverride;

        return updatedOverrides;
    }

    /// <summary>
    /// 仅更新工具的 goal runtime 覆盖项
    /// </summary>
    public static Dictionary<string, SessionToolLaunchOverride> ApplyGoalRuntimeOverride(
        IReadOnlyDictionary<string, SessionToolLaunchOverride>? currentOverrides,
        string toolId,
        bool? useGoalRuntime)
    {
        var normalizedToolId = NormalizeToolId(toolId);
        if (!SupportsLaunchOverrides(normalizedToolId))
        {
            throw new ArgumentException($"Tool '{toolId}' does not support launch overrides.", nameof(toolId));
        }

        var updatedOverrides = NormalizeOverrideDictionary(currentOverrides);
        updatedOverrides.TryGetValue(normalizedToolId, out var existingOverride);
        var normalizedOverride = CreateNormalizedOverride(
            existingOverride?.Model,
            existingOverride?.ReasoningEffort,
            existingOverride?.UsePersistentProcess,
            useGoalRuntime);

        if (normalizedOverride == null)
        {
            updatedOverrides.Remove(normalizedToolId);
            return updatedOverrides;
        }

        updatedOverrides[normalizedToolId] = normalizedOverride;

        return updatedOverrides;
    }

    /// <summary>
    /// 从指定工具中移除覆盖项
    /// </summary>
    public static Dictionary<string, SessionToolLaunchOverride> RemoveOverride(
        IReadOnlyDictionary<string, SessionToolLaunchOverride>? currentOverrides,
        string toolId)
    {
        var normalizedToolId = NormalizeToolId(toolId);
        if (!SupportsLaunchOverrides(normalizedToolId))
        {
            throw new ArgumentException($"Tool '{toolId}' does not support launch overrides.", nameof(toolId));
        }

        var updatedOverrides = NormalizeOverrideDictionary(currentOverrides);
        updatedOverrides.Remove(normalizedToolId);

        return updatedOverrides;
    }

    /// <summary>
    /// 校验覆盖项输入
    /// </summary>
    public static void ValidateOrThrow(string toolId, string? model, string? reasoningEffort)
    {
        var normalizedToolId = NormalizeToolId(toolId);
        if (!SupportsLaunchOverrides(normalizedToolId))
        {
            throw new ArgumentException($"Tool '{toolId}' does not support launch overrides.", nameof(toolId));
        }

        var normalizedModel = NormalizeValue(model);
        var normalizedReasoningEffort = NormalizeValue(reasoningEffort);

        if (!string.IsNullOrWhiteSpace(normalizedReasoningEffort))
        {
            if (!string.Equals(normalizedToolId, "codex", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"Tool '{normalizedToolId}' does not support reasoning effort overrides.", nameof(reasoningEffort));
            }

            if (!CodexReasoningEfforts.Contains(normalizedReasoningEffort))
            {
                throw new ArgumentException($"Unsupported Codex reasoning effort '{normalizedReasoningEffort}'.", nameof(reasoningEffort));
            }
        }

        if (string.IsNullOrWhiteSpace(normalizedModel) && string.IsNullOrWhiteSpace(normalizedReasoningEffort))
        {
            return;
        }
    }

    private static Dictionary<string, SessionToolLaunchOverride> NormalizeOverrideDictionary(
        IReadOnlyDictionary<string, SessionToolLaunchOverride>? overrides)
    {
        var result = new Dictionary<string, SessionToolLaunchOverride>(StringComparer.OrdinalIgnoreCase);
        if (overrides == null)
        {
            return result;
        }

        foreach (var kvp in overrides)
        {
            if (string.IsNullOrWhiteSpace(kvp.Key) || kvp.Value == null)
            {
                continue;
            }

            var normalizedToolId = NormalizeToolId(kvp.Key);
            if (string.IsNullOrWhiteSpace(normalizedToolId))
            {
                continue;
            }

            var normalizedOverride = NormalizeStoredOverride(kvp.Value);
            if (normalizedOverride == null)
            {
                continue;
            }

            result[normalizedToolId] = normalizedOverride;
        }

        return result;
    }

    private static SessionToolLaunchOverride? NormalizeStoredOverride(SessionToolLaunchOverride? value)
    {
        if (value == null)
        {
            return null;
        }

        return CreateNormalizedOverride(
            value.Model,
            value.ReasoningEffort,
            value.UsePersistentProcess,
            value.UseGoalRuntime);
    }

    private static SessionToolLaunchOverride? CreateNormalizedOverride(
        string? model,
        string? reasoningEffort,
        bool? usePersistentProcess,
        bool? useGoalRuntime)
    {
        var normalizedModel = NormalizeValue(model);
        var normalizedReasoningEffort = NormalizeValue(reasoningEffort);
        if (string.IsNullOrWhiteSpace(normalizedModel)
            && string.IsNullOrWhiteSpace(normalizedReasoningEffort)
            && !usePersistentProcess.HasValue
            && !useGoalRuntime.HasValue)
        {
            return null;
        }

        return new SessionToolLaunchOverride
        {
            Model = normalizedModel,
            ReasoningEffort = normalizedReasoningEffort,
            UsePersistentProcess = usePersistentProcess,
            UseGoalRuntime = useGoalRuntime
        };
    }

    private static string? NormalizeValue(string? value)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
