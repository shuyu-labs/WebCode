namespace WebCodeCli.Domain.Domain.Service.Channels;

public interface IReplyTtsEnablementService
{
    Task<bool> HasEnabledReplyTtsAsync(CancellationToken cancellationToken = default);
}
