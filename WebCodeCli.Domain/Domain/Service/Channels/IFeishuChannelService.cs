using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;

namespace WebCodeCli.Domain.Domain.Service.Channels;

public interface IFeishuChannelService
{
    bool IsRunning { get; }

    Task<string> SendMessageAsync(string chatId, string content, string? username = null, string? appId = null);

    Task<string> ReplyMessageAsync(string messageId, string content, string? username = null, string? appId = null);

    Task<FeishuStreamingHandle> SendStreamingMessageAsync(
        string chatId,
        string initialContent,
        string? replyToMessageId = null,
        string? username = null,
        string? appId = null);

    Task HandleIncomingMessageAsync(FeishuIncomingMessage message);

    Task ExecutePreparedSubmissionAsync(
        PreparedMessageSubmission submission,
        string chatId,
        string? replyToMessageId = null,
        string? username = null,
        string? appId = null,
        CancellationToken cancellationToken = default);

    string? GetCurrentSession(string chatKey, string? username = null);

    DateTime? GetSessionLastActiveTime(string sessionId);

    List<string> GetChatSessions(string chatKey, string? username = null);

    bool SwitchCurrentSession(string chatKey, string sessionId, string? username = null);

    bool CloseSession(string chatKey, string sessionId, string? username = null);

    bool IsSessionExecutionActive(string sessionId);

    bool StopSessionExecution(string sessionId);

    void PauseSessionStatusPulse(string sessionId, TimeSpan duration);

    string CreateNewSession(FeishuIncomingMessage message, string? customWorkspacePath = null, string? toolId = null);

    string? GetSessionUsername(string chatKey);

    string ResolveToolId(string chatKey, string? username = null);
}
