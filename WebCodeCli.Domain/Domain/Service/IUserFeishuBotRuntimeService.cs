namespace WebCodeCli.Domain.Domain.Service;

public interface IUserFeishuBotRuntimeService
{
    Task<UserFeishuBotRuntimeStatus> GetStatusAsync(string username, CancellationToken cancellationToken = default);
    Task<UserFeishuBotRuntimeStatus> StartAsync(string username, CancellationToken cancellationToken = default);
    Task<UserFeishuBotRuntimeStatus> StopAsync(string username, CancellationToken cancellationToken = default);
    Task RecoverAsync(CancellationToken cancellationToken = default);
}
