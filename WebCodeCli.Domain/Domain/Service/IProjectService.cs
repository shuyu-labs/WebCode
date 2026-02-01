using WebCodeCli.Domain.Domain.Model;

namespace WebCodeCli.Domain.Domain.Service;

/// <summary>
/// 项目管理服务接口
/// </summary>
public interface IProjectService
{
    /// <summary>
    /// 获取当前用户的所有项目
    /// </summary>
    Task<List<ProjectInfo>> GetProjectsAsync();
    
    /// <summary>
    /// 获取单个项目详情
    /// </summary>
    Task<ProjectInfo?> GetProjectAsync(string projectId);
    
    /// <summary>
    /// 创建项目（仅保存配置，不克隆代码）
    /// </summary>
    Task<(ProjectInfo? Project, string? ErrorMessage)> CreateProjectAsync(CreateProjectRequest request);
    
    /// <summary>
    /// 从 ZIP 压缩包创建项目
    /// </summary>
    /// <param name="projectName">项目名称</param>
    /// <param name="zipFileContent">ZIP 文件内容</param>
    Task<(ProjectInfo? Project, string? ErrorMessage)> CreateProjectFromZipAsync(string projectName, byte[] zipFileContent);
    
    /// <summary>
    /// 更新项目配置
    /// </summary>
    Task<(bool Success, string? ErrorMessage)> UpdateProjectAsync(string projectId, UpdateProjectRequest request);
    
    /// <summary>
    /// 删除项目（包括本地代码）
    /// </summary>
    Task<(bool Success, string? ErrorMessage)> DeleteProjectAsync(string projectId);
    
    /// <summary>
    /// 克隆项目代码
    /// </summary>
    /// <param name="projectId">项目ID</param>
    /// <param name="progress">进度回调</param>
    Task<(bool Success, string? ErrorMessage)> CloneProjectAsync(string projectId, Action<CloneProgress>? progress = null);
    
    /// <summary>
    /// 更新项目代码（拉取最新）
    /// </summary>
    Task<(bool Success, string? ErrorMessage)> PullProjectAsync(string projectId);
    
    /// <summary>
    /// 获取远程仓库的分支列表
    /// </summary>
    Task<(List<string> Branches, string? ErrorMessage)> GetBranchesAsync(GetBranchesRequest request);
    
    /// <summary>
    /// 获取项目的本地路径
    /// </summary>
    string? GetProjectLocalPath(string projectId);
    
    /// <summary>
    /// 复制项目代码到会话工作区
    /// </summary>
    /// <param name="projectId">项目ID</param>
    /// <param name="targetPath">目标工作区路径</param>
    /// <param name="includeGit">是否包含 .git 目录</param>
    Task<(bool Success, string? ErrorMessage)> CopyProjectToWorkspaceAsync(string projectId, string targetPath, bool includeGit);
}
