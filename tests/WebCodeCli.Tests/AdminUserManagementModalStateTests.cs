using System.Reflection;
using WebCodeCli.Components;
using WebCodeCli.Domain.Domain.Model;
using Xunit;

namespace WebCodeCli.Tests;

public sealed class AdminUserManagementModalStateTests
{
    private static readonly Type ModalType = typeof(AdminUserManagementModal);

    [Fact]
    public void CreateDetailEditorSeed_PreservesCurrentDetailSections_WhenReloadingSameUser()
    {
        var editorType = GetNestedType("EditableUserModel");
        var feishuType = GetNestedType("EditableFeishuBotConfigModel");
        var summaryType = GetNestedType("UserSummaryDto");
        var method = GetStaticMethod("CreateDetailEditorSeed");

        var currentEditor = Activator.CreateInstance(editorType, nonPublic: true)!;
        SetProperty(currentEditor, "Username", "alice");
        SetProperty(currentEditor, "DisplayName", "Old Display");
        SetProperty(currentEditor, "Role", UserAccessConstants.UserRole);
        SetProperty(currentEditor, "Enabled", true);
        SetProperty(currentEditor, "HasStoredFeishuConfig", true);
        SetProperty(currentEditor, "AllowedToolIds", new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "git", "shell" });
        SetProperty(currentEditor, "AllowedDirectoriesText", @"D:\workspace");

        var currentFeishu = Activator.CreateInstance(feishuType, nonPublic: true)!;
        SetProperty(currentFeishu, "AppId", "app-123");
        SetProperty(currentFeishu, "FullReplyDocEnabled", true);
        SetProperty(currentFeishu, "FinalReplyDocEnabled", false);
        SetProperty(currentFeishu, "AudioFullReplyDocEnabled", true);
        SetProperty(currentFeishu, "AudioFinalReplyDocEnabled", true);
        SetProperty(currentEditor, "FeishuBot", currentFeishu);

        var selectedUser = Activator.CreateInstance(summaryType, nonPublic: true)!;
        SetProperty(selectedUser, "Username", "alice");
        SetProperty(selectedUser, "DisplayName", "New Display");
        SetProperty(selectedUser, "Role", UserAccessConstants.AdminRole);
        SetProperty(selectedUser, "Status", UserAccessConstants.DisabledStatus);
        SetProperty(selectedUser, "CreatedAt", new DateTime(2026, 5, 2, 10, 0, 0, DateTimeKind.Utc));

        var seededEditor = method.Invoke(null, [selectedUser, currentEditor])!;
        var seededFeishu = GetProperty<object>(seededEditor, "FeishuBot");
        var seededTools = GetProperty<HashSet<string>>(seededEditor, "AllowedToolIds");

        Assert.Equal("alice", GetProperty<string>(seededEditor, "Username"));
        Assert.Equal("New Display", GetProperty<string>(seededEditor, "DisplayName"));
        Assert.Equal(UserAccessConstants.AdminRole, GetProperty<string>(seededEditor, "Role"));
        Assert.False(GetProperty<bool>(seededEditor, "Enabled"));
        Assert.True(GetProperty<bool>(seededEditor, "HasStoredFeishuConfig"));
        Assert.Equal(@"D:\workspace", GetProperty<string>(seededEditor, "AllowedDirectoriesText"));
        Assert.NotSame(currentEditor, seededEditor);
        Assert.NotSame(currentFeishu, seededFeishu);
        Assert.NotSame(GetProperty<HashSet<string>>(currentEditor, "AllowedToolIds"), seededTools);
        Assert.Equal(["git", "shell"], seededTools.OrderBy(static value => value, StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.Equal("app-123", GetProperty<string>(seededFeishu, "AppId"));
        Assert.True(GetProperty<bool>(seededFeishu, "FullReplyDocEnabled"));
        Assert.False(GetProperty<bool>(seededFeishu, "FinalReplyDocEnabled"));
        Assert.True(GetProperty<bool>(seededFeishu, "AudioFullReplyDocEnabled"));
        Assert.True(GetProperty<bool>(seededFeishu, "AudioFinalReplyDocEnabled"));
    }

    [Fact]
    public void CreateDetailEditorSeed_PreservesReferencedMarkdownDocImportEnabled_WhenReloadingSameUser()
    {
        var editorType = GetNestedType("EditableUserModel");
        var feishuType = GetNestedType("EditableFeishuBotConfigModel");
        var summaryType = GetNestedType("UserSummaryDto");
        var method = GetStaticMethod("CreateDetailEditorSeed");

        var currentEditor = Activator.CreateInstance(editorType, nonPublic: true)!;
        SetProperty(currentEditor, "Username", "alice");

        var currentFeishu = Activator.CreateInstance(feishuType, nonPublic: true)!;
        SetOptionalProperty(currentFeishu, "ReferencedMarkdownDocImportEnabled", true);
        SetProperty(currentEditor, "FeishuBot", currentFeishu);

        var selectedUser = Activator.CreateInstance(summaryType, nonPublic: true)!;
        SetProperty(selectedUser, "Username", "alice");
        SetProperty(selectedUser, "DisplayName", "Alice");
        SetProperty(selectedUser, "Role", UserAccessConstants.UserRole);
        SetProperty(selectedUser, "Status", UserAccessConstants.EnabledStatus);
        SetProperty(selectedUser, "CreatedAt", new DateTime(2026, 6, 9, 9, 0, 0, DateTimeKind.Utc));

        var seededEditor = method.Invoke(null, [selectedUser, currentEditor])!;
        var seededFeishu = GetProperty<object>(seededEditor, "FeishuBot");

        Assert.True(GetOptionalBooleanProperty(seededFeishu, "ReferencedMarkdownDocImportEnabled"));
    }

    [Fact]
    public void HasCustomFeishuConfig_ReturnsTrue_WhenOnlyReferencedMarkdownDocImportEnabled()
    {
        var feishuType = GetNestedType("EditableFeishuBotConfigModel");
        var method = GetStaticMethod("HasCustomFeishuConfig");
        var config = Activator.CreateInstance(feishuType, nonPublic: true)!;

        SetOptionalProperty(config, "ReferencedMarkdownDocImportEnabled", true);

        var result = method.Invoke(null, [config]);

        Assert.Equal(true, result);
    }

    private static Type GetNestedType(string name)
    {
        return ModalType.GetNestedType(name, BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Nested type '{name}' was not found.");
    }

    private static MethodInfo GetStaticMethod(string name)
    {
        return ModalType.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Static method '{name}' was not found.");
    }

    private static T GetProperty<T>(object instance, string propertyName)
    {
        return (T)(GetPropertyInfo(instance.GetType(), propertyName).GetValue(instance)
            ?? throw new InvalidOperationException($"Property '{propertyName}' on '{instance.GetType().Name}' was null."));
    }

    private static void SetProperty(object instance, string propertyName, object? value)
    {
        GetPropertyInfo(instance.GetType(), propertyName).SetValue(instance, value);
    }

    private static void SetOptionalProperty(object instance, string propertyName, object? value)
    {
        instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(instance, value);
    }

    private static bool GetOptionalBooleanProperty(object instance, string propertyName)
    {
        return instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                ?.GetValue(instance) as bool?
            ?? false;
    }

    private static PropertyInfo GetPropertyInfo(Type type, string propertyName)
    {
        return type.GetProperty(propertyName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Property '{propertyName}' was not found on '{type.Name}'.");
    }
}
