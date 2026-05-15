using Microsoft.Extensions.DependencyInjection;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Repositories.Base.UserFeishuBotConfig;

namespace WebCodeCli.Domain.Domain.Service.Channels;

[ServiceDescription(typeof(IReplyTtsEnablementService), ServiceLifetime.Scoped)]
public sealed class ReplyTtsEnablementService : IReplyTtsEnablementService
{
    private readonly IUserFeishuBotConfigRepository _repository;

    public ReplyTtsEnablementService(IUserFeishuBotConfigRepository repository)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
    }

    public async Task<bool> HasEnabledReplyTtsAsync(CancellationToken cancellationToken = default)
    {
        var enabledConfigs = await _repository.GetListAsync(static config => config.ReplyTtsEnabled);
        return enabledConfigs.Any(static config => config.ReplyTtsEnabled);
    }
}
