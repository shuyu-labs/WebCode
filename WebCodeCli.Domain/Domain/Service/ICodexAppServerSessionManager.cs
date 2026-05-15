using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service.Adapters;

namespace WebCodeCli.Domain.Domain.Service;

public interface ICodexAppServerSessionManager : IDisposable
{
    Task<string> EnsureThreadAsync(
        string sessionId,
        string commandPath,
        CliToolConfig tool,
        string workingDirectory,
        Dictionary<string, string>? environmentVariables,
        CliSessionContext sessionContext,
        string? existingThreadId,
        CancellationToken cancellationToken = default);

    Task<AppServerTurnRun> StartTurnAsync(
        string sessionId,
        string commandPath,
        CliToolConfig tool,
        string workingDirectory,
        Dictionary<string, string>? environmentVariables,
        CliSessionContext sessionContext,
        string userPrompt,
        string? existingThreadId,
        CancellationToken cancellationToken = default);

    Task<AppServerGoalSnapshot?> GetGoalAsync(
        string sessionId,
        string commandPath,
        CliToolConfig tool,
        string workingDirectory,
        Dictionary<string, string>? environmentVariables,
        CliSessionContext sessionContext,
        string? existingThreadId,
        CancellationToken cancellationToken = default);

    Task<AppServerGoalSnapshot?> SetGoalAsync(
        string sessionId,
        string commandPath,
        CliToolConfig tool,
        string workingDirectory,
        Dictionary<string, string>? environmentVariables,
        CliSessionContext sessionContext,
        string objective,
        string status,
        long? tokenBudget,
        string? existingThreadId,
        CancellationToken cancellationToken = default);

    Task<bool> ClearGoalAsync(
        string sessionId,
        string commandPath,
        CliToolConfig tool,
        string workingDirectory,
        Dictionary<string, string>? environmentVariables,
        CliSessionContext sessionContext,
        string? existingThreadId,
        CancellationToken cancellationToken = default);

    Task<bool> InterruptActiveTurnAsync(
        string sessionId,
        string commandPath,
        CliToolConfig tool,
        string workingDirectory,
        Dictionary<string, string>? environmentVariables,
        CliSessionContext sessionContext,
        string? existingThreadId,
        CancellationToken cancellationToken = default);

    Task<bool> InterruptActiveTurnAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    bool CleanupSession(string sessionId);
}
