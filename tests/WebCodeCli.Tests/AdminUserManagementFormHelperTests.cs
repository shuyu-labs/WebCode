using WebCodeCli.Helpers;
using Xunit;

namespace WebCodeCli.Tests;

public sealed class AdminUserManagementFormHelperTests
{
    [Fact]
    public void ParseAllowedDirectories_TrimsDeduplicatesAndKeepsOrder()
    {
        var input = "  D:\\Work  \r\n\r\nD:\\Data\r\n d:\\work \nC:\\Temp ";

        var result = AdminUserManagementFormHelper.ParseAllowedDirectories(input);

        Assert.Equal(
            new[]
            {
                "D:\\Work",
                "D:\\Data",
                "C:\\Temp"
            },
            result);
    }

    [Fact]
    public void FormatAllowedDirectories_JoinsNonEmptyValues()
    {
        var result = AdminUserManagementFormHelper.FormatAllowedDirectories(new[]
        {
            "D:\\Work",
            "",
            " C:\\Temp "
        });

        Assert.Equal($"D:\\Work{Environment.NewLine}C:\\Temp", result);
    }

    [Fact]
    public void GetAllowedToolIds_ReturnsOnlyEnabledToolIds()
    {
        var toolMap = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["codex"] = true,
            ["claude"] = false,
            ["gemini"] = true
        };

        var result = AdminUserManagementFormHelper.GetAllowedToolIds(toolMap);

        Assert.True(result.SetEquals(new[] { "codex", "gemini" }));
    }

    [Theory]
    [InlineData(true, null, null, null, null, null, null, null, null, null, true)]
    [InlineData(false, "app-id", null, null, null, null, null, null, null, null, true)]
    [InlineData(false, null, null, null, null, null, null, "ou_admin", null, null, true)]
    [InlineData(false, null, null, null, null, null, null, null, 30, null, true)]
    [InlineData(false, null, null, null, null, null, null, null, null, null, false)]
    public void HasCustomFeishuConfig_DetectsWhetherOverrideExists(
        bool isEnabled,
        string? appId,
        string? appSecret,
        string? encryptKey,
        string? verificationToken,
        string? defaultCardTitle,
        string? thinkingMessage,
        string? documentAdminOpenId,
        int? httpTimeoutSeconds,
        int? streamingThrottleMs,
        bool expected)
    {
        var result = AdminUserManagementFormHelper.HasCustomFeishuConfig(
            isEnabled,
            appId,
            appSecret,
            encryptKey,
            verificationToken,
            defaultCardTitle,
            thinkingMessage,
            documentAdminOpenId,
            httpTimeoutSeconds,
            streamingThrottleMs);

        Assert.Equal(expected, result);
    }
}
