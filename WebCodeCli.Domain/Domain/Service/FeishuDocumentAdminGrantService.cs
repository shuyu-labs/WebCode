using Microsoft.Extensions.DependencyInjection;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Service.Channels;
using WebCodeCli.Domain.Repositories.Base.UserFeishuBotConfig;

namespace WebCodeCli.Domain.Domain.Service;

public interface IFeishuDocumentAdminGrantService
{
    Task<FeishuDocumentAdminGrantResult> GrantConfiguredAdminAsync(string username, string documentId);
    Task<FeishuDocumentAdminGrantBatchResult> GrantConfiguredAdminBatchAsync(string username, IEnumerable<string> documentIds);
}

[ServiceDescription(typeof(IFeishuDocumentAdminGrantService), ServiceLifetime.Scoped)]
public sealed class FeishuDocumentAdminGrantService : IFeishuDocumentAdminGrantService
{
    private readonly IUserFeishuBotConfigService _userFeishuBotConfigService;
    private readonly IFeishuCardKitClient _feishuCardKitClient;

    public FeishuDocumentAdminGrantService(
        IUserFeishuBotConfigService userFeishuBotConfigService,
        IFeishuCardKitClient feishuCardKitClient)
    {
        _userFeishuBotConfigService = userFeishuBotConfigService;
        _feishuCardKitClient = feishuCardKitClient;
    }

    public async Task<FeishuDocumentAdminGrantResult> GrantConfiguredAdminAsync(string username, string documentId)
    {
        var normalizedUsername = NormalizeRequiredValue(username, nameof(username), "用户名不能为空。");
        var normalizedDocumentId = NormalizeRequiredValue(documentId, nameof(documentId), "文档 ID 不能为空。");

        var config = await _userFeishuBotConfigService.GetByUsernameAsync(normalizedUsername);
        if (config == null)
        {
            return FeishuDocumentAdminGrantResult.NotFound("未找到对应用户的飞书机器人配置。");
        }

        if (string.IsNullOrWhiteSpace(config.DocumentAdminOpenId))
        {
            return FeishuDocumentAdminGrantResult.Invalid("当前用户尚未保存文档管理员 OpenID。");
        }

        var effectiveOptions = await _userFeishuBotConfigService.GetEffectiveOptionsAsync(normalizedUsername)
            ?? new FeishuOptions();

        await _feishuCardKitClient.GrantCloudDocumentMemberFullAccessAsync(
            normalizedDocumentId,
            config.DocumentAdminOpenId.Trim(),
            optionsOverride: effectiveOptions);

        return FeishuDocumentAdminGrantResult.Granted(
            normalizedUsername,
            normalizedDocumentId,
            config.DocumentAdminOpenId.Trim());
    }

    public async Task<FeishuDocumentAdminGrantBatchResult> GrantConfiguredAdminBatchAsync(string username, IEnumerable<string> documentIds)
    {
        var normalizedUsername = NormalizeRequiredValue(username, nameof(username), "用户名不能为空。");
        ArgumentNullException.ThrowIfNull(documentIds);

        var results = new List<FeishuDocumentAdminGrantResult>();
        foreach (var documentId in documentIds)
        {
            if (string.IsNullOrWhiteSpace(documentId))
            {
                continue;
            }

            try
            {
                results.Add(await GrantConfiguredAdminAsync(normalizedUsername, documentId));
            }
            catch (Exception ex)
            {
                results.Add(FeishuDocumentAdminGrantResult.Failure(
                    normalizedUsername,
                    documentId.Trim(),
                    ex.Message));
            }
        }

        return new FeishuDocumentAdminGrantBatchResult(results);
    }

    private static string NormalizeRequiredValue(string? value, string paramName, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(message, paramName);
        }

        return value.Trim();
    }
}

public sealed record FeishuDocumentAdminGrantResult(
    bool Success,
    string Username,
    string DocumentId,
    string? OpenId,
    string? ErrorMessage,
    int? StatusCode = null)
{
    public static FeishuDocumentAdminGrantResult Granted(string username, string documentId, string openId)
        => new(true, username, documentId, openId, null);

    public static FeishuDocumentAdminGrantResult Invalid(string errorMessage)
        => new(false, string.Empty, string.Empty, null, errorMessage, 400);

    public static FeishuDocumentAdminGrantResult NotFound(string errorMessage)
        => new(false, string.Empty, string.Empty, null, errorMessage, 404);

    public static FeishuDocumentAdminGrantResult Failure(string username, string documentId, string errorMessage)
        => new(false, username, documentId, null, errorMessage, 500);
}

public sealed record FeishuDocumentAdminGrantBatchResult(IReadOnlyList<FeishuDocumentAdminGrantResult> Results)
{
    public int SuccessCount => Results.Count(static x => x.Success);
    public int FailureCount => Results.Count(static x => !x.Success);
}
