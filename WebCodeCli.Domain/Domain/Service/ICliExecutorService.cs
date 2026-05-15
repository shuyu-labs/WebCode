using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service.Adapters;

namespace WebCodeCli.Domain.Domain.Service;

/// <summary>
/// CLI 执行服务接口
/// </summary>
public interface ICliExecutorService
{
    /// <summary>
    /// 获取指定工具的适配器
    /// </summary>
    /// <param name="tool">CLI工具配置</param>
    /// <returns>适配器实例，如果没有匹配的适配器则返回null</returns>
    ICliToolAdapter? GetAdapter(CliToolConfig tool);

    /// <summary>
    /// 获取指定工具的适配器
    /// </summary>
    /// <param name="toolId">工具ID</param>
    /// <returns>适配器实例，如果没有匹配的适配器则返回null</returns>
    ICliToolAdapter? GetAdapterById(string toolId);

    /// <summary>
    /// 检查指定工具是否支持流式解析
    /// </summary>
    /// <param name="tool">CLI工具配置</param>
    /// <returns>是否支持流式解析</returns>
    bool SupportsStreamParsing(CliToolConfig tool);

    /// <summary>
    /// 获取指定会话的CLI线程ID
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <returns>CLI线程ID</returns>
    string? GetCliThreadId(string sessionId);

    /// <summary>
    /// 设置指定会话的CLI线程ID
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <param name="threadId">CLI线程ID</param>
    void SetCliThreadId(string sessionId, string threadId);

    /// <summary>
    /// 仅重置当前会话运行态，不删除工作区和历史消息
    /// </summary>
    Task ResetSessionRuntimeAsync(
        string sessionId,
        bool clearCliThreadId = true,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 停止指定会话当前正在运行的 CLI 执行
    /// </summary>
    Task StopSessionExecutionAsync(
        string sessionId,
        string? toolId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 执行 CLI 命令并返回流式输出
    /// </summary>
    /// <param name="sessionId">会话ID,用于创建独立工作区</param>
    /// <param name="toolId">CLI 工具ID</param>
    /// <param name="userPrompt">用户输入</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>流式输出块</returns>
    IAsyncEnumerable<StreamOutputChunk> ExecuteStreamAsync(
        string sessionId,
        string toolId,
        string userPrompt,
        CancellationToken cancellationToken = default)
        => ExecuteStreamAsync(
            new CliExecutionRequest
            {
                SessionId = sessionId,
                ToolId = toolId,
                PromptText = userPrompt
            },
            cancellationToken);

    IAsyncEnumerable<StreamOutputChunk> ExecuteStreamAsync(
        CliExecutionRequest request,
        CancellationToken cancellationToken = default)
        => throw new NotSupportedException("Implement the request-based ExecuteStreamAsync overload.");

    /// <summary>
    /// 判断工具是否支持少打断执行
    /// </summary>
    bool SupportsLowInterruptionContinue(string toolId);

    /// <summary>
    /// 判断当前会话是否可以启动少打断执行
    /// </summary>
    bool CanStartLowInterruptionContinue(string sessionId, string toolId);

    /// <summary>
    /// 以原生 resume/continue 方式启动一次少打断执行
    /// </summary>
    IAsyncEnumerable<StreamOutputChunk> ExecuteLowInterruptionContinueStreamAsync(
        string sessionId,
        string toolId,
        string? prompt = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 获取所有可用的 CLI 工具
    /// </summary>
    /// <returns>CLI 工具列表</returns>
    List<CliToolConfig> GetAvailableTools(string? username = null);

    /// <summary>
    /// 获取指定的 CLI 工具配置
    /// </summary>
    /// <param name="toolId">工具ID</param>
    /// <returns>CLI 工具配置</returns>
    CliToolConfig? GetTool(string toolId, string? username = null);

    /// <summary>
    /// 验证 CLI 工具是否可用
    /// </summary>
    /// <param name="toolId">工具ID</param>
    /// <returns>是否可用</returns>
    bool ValidateTool(string toolId, string? username = null);

    /// <summary>
    /// 清理指定会话的工作区
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    void CleanupSessionWorkspace(string sessionId);

    /// <summary>
    /// 清理所有过期的会话工作区
    /// </summary>
    void CleanupExpiredWorkspaces();

    /// <summary>
    /// 获取会话工作区路径
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <returns>工作区路径</returns>
    string GetSessionWorkspacePath(string sessionId);

    /// <summary>
    /// 获取指定工具的环境变量配置（优先从数据库读取）
    /// </summary>
    /// <param name="toolId">工具ID</param>
    /// <returns>环境变量字典</returns>
    Task<Dictionary<string, string>> GetToolEnvironmentVariablesAsync(string toolId, string? username = null);

    /// <summary>
    /// 将会话固定的 Provider 快照同步到当前 cc-switch 激活 Provider
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <param name="toolId">工具ID，留空时使用会话当前工具</param>
    /// <returns>最新的会话快照元数据</returns>
    Task<CcSwitchSessionSnapshot?> SyncSessionCcSwitchSnapshotAsync(string sessionId, string? toolId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 将当前 Codex thread 同步到当前 cc-switch 激活 Provider
    /// </summary>
    Task<CodexThreadProviderSyncResult> SyncCodexThreadProviderAsync(
        string sessionId,
        string? toolId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存指定工具的环境变量配置到数据库
    /// </summary>
    /// <param name="toolId">工具ID</param>
    /// <param name="envVars">环境变量字典</param>
    /// <returns>是否保存成功</returns>
    Task<bool> SaveToolEnvironmentVariablesAsync(string toolId, Dictionary<string, string> envVars, string? username = null);

    /// <summary>
    /// 获取会话工作区的文件内容
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <param name="relativePath">相对路径</param>
    /// <returns>文件内容的字节数组</returns>
    byte[]? GetWorkspaceFile(string sessionId, string relativePath);

    /// <summary>
    /// 获取会话工作区的所有文件（打包为ZIP）
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <returns>ZIP文件的字节数组</returns>
    byte[]? GetWorkspaceZip(string sessionId);

    /// <summary>
    /// 上传文件到会话工作区
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <param name="fileName">文件名</param>
    /// <param name="fileContent">文件内容</param>
    /// <param name="relativePath">相对路径（可选，如果为空则放在根目录）</param>
    /// <returns>是否上传成功</returns>
    Task<bool> UploadFileToWorkspaceAsync(string sessionId, string fileName, byte[] fileContent, string? relativePath = null);

    /// <summary>
    /// 在会话工作区创建文件夹
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <param name="folderPath">文件夹路径（相对于工作区根目录）</param>
    /// <returns>是否创建成功</returns>
    Task<bool> CreateFolderInWorkspaceAsync(string sessionId, string folderPath);

    /// <summary>
    /// 删除会话工作区中的文件或文件夹
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <param name="relativePath">相对路径</param>
    /// <param name="isDirectory">是否是文件夹</param>
    /// <returns>是否删除成功</returns>
    Task<bool> DeleteWorkspaceItemAsync(string sessionId, string relativePath, bool isDirectory);

    /// <summary>
    /// 移动会话工作区中的文件或文件夹
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <param name="sourcePath">源路径（相对于工作区根目录）</param>
    /// <param name="targetPath">目标路径（相对于工作区根目录）</param>
    /// <returns>是否移动成功</returns>
    Task<bool> MoveFileInWorkspaceAsync(string sessionId, string sourcePath, string targetPath);

    /// <summary>
    /// 复制会话工作区中的文件或文件夹
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <param name="sourcePath">源路径（相对于工作区根目录）</param>
    /// <param name="targetPath">目标路径（相对于工作区根目录）</param>
    /// <returns>是否复制成功</returns>
    Task<bool> CopyFileInWorkspaceAsync(string sessionId, string sourcePath, string targetPath);

    /// <summary>
    /// 重命名会话工作区中的文件或文件夹
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <param name="oldPath">旧路径（相对于工作区根目录）</param>
    /// <param name="newName">新名称（仅文件/文件夹名，不包含路径）</param>
    /// <returns>是否重命名成功</returns>
    Task<bool> RenameFileInWorkspaceAsync(string sessionId, string oldPath, string newName);

    /// <summary>
    /// 批量删除会话工作区中的文件
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <param name="relativePaths">相对路径列表</param>
    /// <returns>成功删除的文件数量</returns>
    Task<int> BatchDeleteFilesAsync(string sessionId, List<string> relativePaths);
    
    /// <summary>
    /// 初始化会话工作区（可选择关联项目）
    /// </summary>
    /// <param name="sessionId">会话ID</param>
    /// <param name="projectId">项目ID（可选，如果提供则从项目复制代码）</param>
    /// <param name="includeGit">是否包含 .git 目录</param>
    /// <returns>工作区路径</returns>
    Task<string> InitializeSessionWorkspaceAsync(string sessionId, string? projectId = null, bool includeGit = false);
    
    /// <summary>
    /// 刷新工作区根目录缓存（当数据库配置更新时调用）
    /// </summary>
    void RefreshWorkspaceRootCache();
}

