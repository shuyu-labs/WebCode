using System.IO.Compression;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WebCodeCli.Domain.Common.Extensions;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Repositories.Base.Project;

namespace WebCodeCli.Domain.Domain.Service;

/// <summary>
/// 项目管理服务实现
/// </summary>
[ServiceDescription(typeof(IProjectService), ServiceLifetime.Scoped)]
public class ProjectService : IProjectService
{
    private readonly IProjectRepository _projectRepository;
    private readonly IUserContextService _userContextService;
    private readonly ISystemSettingsService _systemSettingsService;
    private readonly IGitService _gitService;
    private readonly ILogger<ProjectService> _logger;
    
    // 项目目录在工作区根目录下的子文件夹名
    private const string ProjectsFolder = "projects";
    
    // 简单的 AES 加密密钥（实际生产环境应该使用更安全的密钥管理）
    private const string EncryptionKey = "WebCode2024!Proj";

    public ProjectService(
        IProjectRepository projectRepository,
        IUserContextService userContextService,
        ISystemSettingsService systemSettingsService,
        IGitService gitService,
        ILogger<ProjectService> logger)
    {
        _projectRepository = projectRepository;
        _userContextService = userContextService;
        _systemSettingsService = systemSettingsService;
        _gitService = gitService;
        _logger = logger;
    }

    /// <summary>
    /// 获取当前用户的所有项目
    /// </summary>
    public async Task<List<ProjectInfo>> GetProjectsAsync()
    {
        try
        {
            var username = _userContextService.GetCurrentUsername();
            var entities = await _projectRepository.GetByUsernameOrderByUpdatedAtAsync(username);
            
            return entities.Select(MapToProjectInfo).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取项目列表失败");
            return new List<ProjectInfo>();
        }
    }

    /// <summary>
    /// 获取单个项目详情
    /// </summary>
    public async Task<ProjectInfo?> GetProjectAsync(string projectId)
    {
        try
        {
            var username = _userContextService.GetCurrentUsername();
            var entity = await _projectRepository.GetByIdAndUsernameAsync(projectId, username);
            
            return entity == null ? null : MapToProjectInfo(entity);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取项目详情失败: {ProjectId}", projectId);
            return null;
        }
    }

    /// <summary>
    /// 创建项目（仅保存配置，不克隆代码）
    /// </summary>
    public async Task<(ProjectInfo? Project, string? ErrorMessage)> CreateProjectAsync(CreateProjectRequest request)
    {
        try
        {
            var username = _userContextService.GetCurrentUsername();
            
            // 验证项目名称
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return (null, "项目名称不能为空");
            }
            
            // 验证仓库地址
            if (string.IsNullOrWhiteSpace(request.GitUrl))
            {
                return (null, "Git 仓库地址不能为空");
            }
            
            // 检查名称是否已存在
            if (await _projectRepository.ExistsByNameAndUsernameAsync(request.Name, username))
            {
                return (null, "项目名称已存在");
            }
            
            // 生成项目ID和本地路径
            var projectId = Guid.NewGuid().ToString("N");
            var localPath = await GetProjectLocalPathAsync(username, projectId);
            
            // 创建实体
            var entity = new ProjectEntity
            {
                ProjectId = projectId,
                Username = username,
                Name = request.Name.Trim(),
                GitUrl = request.GitUrl.Trim(),
                AuthType = request.AuthType ?? "none",
                HttpsUsername = request.HttpsUsername,
                HttpsToken = EncryptIfNotEmpty(request.HttpsToken),
                SshPrivateKey = EncryptIfNotEmpty(request.SshPrivateKey),
                SshPassphrase = EncryptIfNotEmpty(request.SshPassphrase),
                Branch = string.IsNullOrWhiteSpace(request.Branch) ? "main" : request.Branch.Trim(),
                LocalPath = localPath,
                Status = "pending",
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            
            var success = await _projectRepository.InsertAsync(entity);
            if (!success)
            {
                return (null, "保存项目失败");
            }
            
            _logger.LogInformation("项目创建成功: {ProjectId}, {Name}", projectId, request.Name);
            return (MapToProjectInfo(entity), null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建项目失败");
            return (null, $"创建项目失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从 ZIP 压缩包创建项目
    /// </summary>
    public async Task<(ProjectInfo? Project, string? ErrorMessage)> CreateProjectFromZipAsync(string projectName, byte[] zipFileContent)
    {
        var tempPath = string.Empty;
        
        try
        {
            var username = _userContextService.GetCurrentUsername();
            
            // 验证项目名称
            if (string.IsNullOrWhiteSpace(projectName))
            {
                return (null, "项目名称不能为空");
            }
            
            // 验证 ZIP 文件内容
            if (zipFileContent == null || zipFileContent.Length == 0)
            {
                return (null, "ZIP 文件内容为空");
            }
            
            // 检查名称是否已存在
            if (await _projectRepository.ExistsByNameAndUsernameAsync(projectName, username))
            {
                return (null, "项目名称已存在");
            }
            
            // 生成项目ID和本地路径
            var projectId = Guid.NewGuid().ToString("N");
            var localPath = await GetProjectLocalPathAsync(username, projectId);
            
            // 创建临时解压目录
            tempPath = Path.Combine(Path.GetTempPath(), $"webcode_zip_{projectId}");
            
            // 确保临时目录存在
            if (Directory.Exists(tempPath))
            {
                Directory.Delete(tempPath, true);
            }
            Directory.CreateDirectory(tempPath);
            
            // 解压到临时目录
            using (var zipStream = new MemoryStream(zipFileContent))
            {
                using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
                archive.ExtractToDirectory(tempPath);
            }
            
            // 确保目标父目录存在
            var parentDir = Path.GetDirectoryName(localPath);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
            {
                Directory.CreateDirectory(parentDir);
            }
            
            // 检查是否只有一个根目录，如果是则提升内容
            var rootItems = Directory.GetFileSystemEntries(tempPath);
            string sourcePath;
            if (rootItems.Length == 1 && Directory.Exists(rootItems[0]))
            {
                // 只有一个根目录，使用它作为源目录
                sourcePath = rootItems[0];
            }
            else
            {
                // 多个文件/目录，使用整个临时目录作为源
                sourcePath = tempPath;
            }
            
            // 使用复制代替移动（跨卷兼容）
            await CopyDirectoryAsync(sourcePath, localPath, excludeGit: false);
            
            // 创建实体（ZIP 上传的项目 GitUrl 设为特殊标记）
            var entity = new ProjectEntity
            {
                ProjectId = projectId,
                Username = username,
                Name = projectName.Trim(),
                GitUrl = string.Empty, // ZIP 上传项目无 Git URL
                AuthType = "none",
                Branch = string.Empty,
                LocalPath = localPath,
                Status = "ready", // ZIP 上传后直接就绪
                LastSyncAt = DateTime.Now,
                CreatedAt = DateTime.Now,
                UpdatedAt = DateTime.Now
            };
            
            var success = await _projectRepository.InsertAsync(entity);
            if (!success)
            {
                // 回滚：删除已解压的目录
                if (Directory.Exists(localPath))
                {
                    Directory.Delete(localPath, true);
                }
                return (null, "保存项目失败");
            }
            
            _logger.LogInformation("ZIP 项目创建成功: {ProjectId}, {Name}", projectId, projectName);
            return (MapToProjectInfo(entity), null);
        }
        catch (InvalidDataException)
        {
            return (null, "无效的 ZIP 文件格式");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "从 ZIP 创建项目失败");
            return (null, $"创建项目失败: {ex.Message}");
        }
        finally
        {
            // 清理临时目录
            if (!string.IsNullOrEmpty(tempPath) && Directory.Exists(tempPath))
            {
                try
                {
                    Directory.Delete(tempPath, true);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "清理临时目录失败: {TempPath}", tempPath);
                }
            }
        }
    }

    /// <summary>
    /// 更新项目配置
    /// </summary>
    public async Task<(bool Success, string? ErrorMessage)> UpdateProjectAsync(string projectId, UpdateProjectRequest request)
    {
        try
        {
            var username = _userContextService.GetCurrentUsername();
            var entity = await _projectRepository.GetByIdAndUsernameAsync(projectId, username);
            
            if (entity == null)
            {
                return (false, "项目不存在");
            }
            
            // 检查名称是否已被其他项目使用
            if (!string.IsNullOrWhiteSpace(request.Name) && 
                await _projectRepository.ExistsByNameAndUsernameAsync(request.Name, username, projectId))
            {
                return (false, "项目名称已存在");
            }
            
            // 更新字段
            if (!string.IsNullOrWhiteSpace(request.Name))
            {
                entity.Name = request.Name.Trim();
            }
            
            if (!string.IsNullOrWhiteSpace(request.GitUrl))
            {
                entity.GitUrl = request.GitUrl.Trim();
                // 更新仓库地址后需要重新克隆
                entity.Status = "pending";
            }
            
            if (!string.IsNullOrWhiteSpace(request.AuthType))
            {
                entity.AuthType = request.AuthType;
            }
            
            if (request.HttpsUsername != null)
            {
                entity.HttpsUsername = request.HttpsUsername;
            }
            
            if (request.HttpsToken != null)
            {
                entity.HttpsToken = EncryptIfNotEmpty(request.HttpsToken);
            }
            
            if (request.SshPrivateKey != null)
            {
                entity.SshPrivateKey = EncryptIfNotEmpty(request.SshPrivateKey);
            }
            
            if (request.SshPassphrase != null)
            {
                entity.SshPassphrase = EncryptIfNotEmpty(request.SshPassphrase);
            }
            
            if (!string.IsNullOrWhiteSpace(request.Branch))
            {
                entity.Branch = request.Branch.Trim();
            }
            
            entity.UpdatedAt = DateTime.Now;
            
            var success = await _projectRepository.UpdateAsync(entity);
            if (!success)
            {
                return (false, "更新项目失败");
            }
            
            _logger.LogInformation("项目更新成功: {ProjectId}", projectId);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新项目失败: {ProjectId}", projectId);
            return (false, $"更新项目失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 删除项目（包括本地代码）
    /// </summary>
    public async Task<(bool Success, string? ErrorMessage)> DeleteProjectAsync(string projectId)
    {
        try
        {
            var username = _userContextService.GetCurrentUsername();
            var entity = await _projectRepository.GetByIdAndUsernameAsync(projectId, username);
            
            if (entity == null)
            {
                return (false, "项目不存在");
            }
            
            // 删除本地代码目录
            if (!string.IsNullOrEmpty(entity.LocalPath) && Directory.Exists(entity.LocalPath))
            {
                try
                {
                    // 某些 Git 文件可能是只读的，需要先取消只读属性
                    SetAttributesNormal(new DirectoryInfo(entity.LocalPath));
                    Directory.Delete(entity.LocalPath, true);
                    _logger.LogInformation("已删除项目本地目录: {LocalPath}", entity.LocalPath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "删除项目本地目录失败: {LocalPath}", entity.LocalPath);
                    // 继续删除数据库记录
                }
            }
            
            // 删除数据库记录
            var success = await _projectRepository.DeleteByIdAndUsernameAsync(projectId, username);
            if (!success)
            {
                return (false, "删除项目失败");
            }
            
            _logger.LogInformation("项目删除成功: {ProjectId}", projectId);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除项目失败: {ProjectId}", projectId);
            return (false, $"删除项目失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 克隆项目代码
    /// </summary>
    public async Task<(bool Success, string? ErrorMessage)> CloneProjectAsync(string projectId, Action<CloneProgress>? progress = null)
    {
        try
        {
            var username = _userContextService.GetCurrentUsername();
            var entity = await _projectRepository.GetByIdAndUsernameAsync(projectId, username);
            
            if (entity == null)
            {
                return (false, "项目不存在");
            }
            
            // 更新状态为克隆中
            entity.Status = "cloning";
            entity.ErrorMessage = null;
            entity.UpdatedAt = DateTime.Now;
            await _projectRepository.UpdateAsync(entity);
            
            try
            {
                // 确保本地路径存在
                if (string.IsNullOrEmpty(entity.LocalPath))
                {
                    entity.LocalPath = await GetProjectLocalPathAsync(username, projectId);
                }
                
                // 构建凭据
                var credentials = BuildCredentials(entity);
                
                // 执行克隆
                var (success, errorMessage) = await _gitService.CloneAsync(
                    entity.GitUrl,
                    entity.LocalPath,
                    entity.Branch,
                    credentials,
                    progress);
                
                if (success)
                {
                    entity.Status = "ready";
                    entity.LastSyncAt = DateTime.Now;
                    entity.ErrorMessage = null;
                    _logger.LogInformation("项目克隆成功: {ProjectId}, 路径: {LocalPath}", projectId, entity.LocalPath);
                }
                else
                {
                    entity.Status = "error";
                    entity.ErrorMessage = errorMessage;
                    _logger.LogWarning("项目克隆失败: {ProjectId}, 错误: {Error}", projectId, errorMessage);
                }
                
                entity.UpdatedAt = DateTime.Now;
                await _projectRepository.UpdateAsync(entity);
                
                return (success, errorMessage);
            }
            catch (Exception ex)
            {
                entity.Status = "error";
                entity.ErrorMessage = ex.Message;
                entity.UpdatedAt = DateTime.Now;
                await _projectRepository.UpdateAsync(entity);
                
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "克隆项目失败: {ProjectId}", projectId);
            return (false, $"克隆项目失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 更新项目代码（拉取最新）
    /// </summary>
    public async Task<(bool Success, string? ErrorMessage)> PullProjectAsync(string projectId)
    {
        try
        {
            var username = _userContextService.GetCurrentUsername();
            var entity = await _projectRepository.GetByIdAndUsernameAsync(projectId, username);
            
            if (entity == null)
            {
                return (false, "项目不存在");
            }
            
            if (entity.Status != "ready")
            {
                return (false, "项目尚未克隆完成，请先克隆项目");
            }
            
            if (string.IsNullOrEmpty(entity.LocalPath) || !Directory.Exists(entity.LocalPath))
            {
                return (false, "项目本地目录不存在，请重新克隆");
            }
            
            // 构建凭据
            var credentials = BuildCredentials(entity);
            
            // 执行拉取
            var (success, errorMessage) = await _gitService.PullAsync(entity.LocalPath, credentials);
            
            if (success)
            {
                entity.LastSyncAt = DateTime.Now;
                entity.UpdatedAt = DateTime.Now;
                await _projectRepository.UpdateAsync(entity);
                
                _logger.LogInformation("项目更新成功: {ProjectId}", projectId);
            }
            else
            {
                _logger.LogWarning("项目更新失败: {ProjectId}, 错误: {Error}", projectId, errorMessage);
            }
            
            return (success, errorMessage);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新项目失败: {ProjectId}", projectId);
            return (false, $"更新项目失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取远程仓库的分支列表
    /// </summary>
    public async Task<(List<string> Branches, string? ErrorMessage)> GetBranchesAsync(GetBranchesRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.GitUrl))
            {
                return (new List<string>(), "Git 仓库地址不能为空");
            }
            
            var credentials = new GitCredentials
            {
                AuthType = request.AuthType ?? "none",
                HttpsUsername = request.HttpsUsername,
                HttpsToken = request.HttpsToken,
                SshPrivateKey = request.SshPrivateKey,
                SshPassphrase = request.SshPassphrase
            };
            
            return await _gitService.ListRemoteBranchesAsync(request.GitUrl, credentials);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取分支列表失败");
            return (new List<string>(), $"获取分支列表失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 获取项目的本地路径
    /// </summary>
    public string? GetProjectLocalPath(string projectId)
    {
        try
        {
            var username = _userContextService.GetCurrentUsername();
            var entity = _projectRepository.GetByIdAndUsernameAsync(projectId, username).GetAwaiter().GetResult();
            return entity?.LocalPath;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取项目本地路径失败: {ProjectId}", projectId);
            return null;
        }
    }

    /// <summary>
    /// 复制项目代码到会话工作区
    /// </summary>
    public async Task<(bool Success, string? ErrorMessage)> CopyProjectToWorkspaceAsync(string projectId, string targetPath, bool includeGit)
    {
        try
        {
            var username = _userContextService.GetCurrentUsername();
            var entity = await _projectRepository.GetByIdAndUsernameAsync(projectId, username);
            
            if (entity == null)
            {
                return (false, "项目不存在");
            }
            
            if (entity.Status != "ready")
            {
                return (false, "项目尚未克隆完成");
            }
            
            if (string.IsNullOrEmpty(entity.LocalPath) || !Directory.Exists(entity.LocalPath))
            {
                return (false, "项目本地目录不存在");
            }
            
            // 确保目标目录存在
            if (!Directory.Exists(targetPath))
            {
                Directory.CreateDirectory(targetPath);
            }
            
            // 复制文件（可选包含 .git 目录）
            await CopyDirectoryAsync(entity.LocalPath, targetPath, excludeGit: !includeGit);
            
            _logger.LogInformation("项目代码已复制到工作区: {ProjectId} -> {TargetPath}", projectId, targetPath);
            return (true, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "复制项目代码失败: {ProjectId} -> {TargetPath}", projectId, targetPath);
            return (false, $"复制项目代码失败: {ex.Message}");
        }
    }
    
    #region Private Methods
    
    private async Task<string> GetProjectLocalPathAsync(string username, string projectId)
    {
        var workspaceRoot = await _systemSettingsService.GetWorkspaceRootAsync();
        return Path.Combine(workspaceRoot, ProjectsFolder, username, projectId);
    }
    
    private static ProjectInfo MapToProjectInfo(ProjectEntity entity)
    {
        return new ProjectInfo
        {
            ProjectId = entity.ProjectId,
            Name = entity.Name,
            GitUrl = entity.GitUrl,
            AuthType = entity.AuthType,
            Branch = entity.Branch,
            LocalPath = entity.LocalPath,
            LastSyncAt = entity.LastSyncAt,
            Status = entity.Status,
            ErrorMessage = entity.ErrorMessage,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
    }
    
    private GitCredentials BuildCredentials(ProjectEntity entity)
    {
        return new GitCredentials
        {
            AuthType = entity.AuthType,
            HttpsUsername = entity.HttpsUsername,
            HttpsToken = DecryptIfNotEmpty(entity.HttpsToken),
            SshPrivateKey = DecryptIfNotEmpty(entity.SshPrivateKey),
            SshPassphrase = DecryptIfNotEmpty(entity.SshPassphrase)
        };
    }
    
    private static string? EncryptIfNotEmpty(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }
        
        // 简单的 Base64 编码（实际生产环境应使用 AES 加密）
        // TODO: 实现真正的 AES 加密
        return Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));
    }
    
    private static string? DecryptIfNotEmpty(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }
        
        try
        {
            // 简单的 Base64 解码
            // TODO: 实现真正的 AES 解密
            return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch
        {
            // 如果解密失败，返回原值
            return value;
        }
    }
    
    private static void SetAttributesNormal(DirectoryInfo dir)
    {
        foreach (var subDir in dir.GetDirectories())
        {
            SetAttributesNormal(subDir);
            subDir.Attributes = FileAttributes.Normal;
        }
        
        foreach (var file in dir.GetFiles())
        {
            file.Attributes = FileAttributes.Normal;
        }
    }
    
    private static async Task CopyDirectoryAsync(string sourceDir, string targetDir, bool excludeGit = false)
    {
        await Task.Run(() =>
        {
            var source = new DirectoryInfo(sourceDir);
            var target = new DirectoryInfo(targetDir);
            
            if (!target.Exists)
            {
                target.Create();
            }
            
            // 复制文件
            foreach (var file in source.GetFiles())
            {
                var targetFile = Path.Combine(target.FullName, file.Name);
                file.CopyTo(targetFile, true);
            }
            
            // 递归复制子目录
            foreach (var subDir in source.GetDirectories())
            {
                // 排除 .git 目录
                if (excludeGit && subDir.Name.Equals(".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                
                var targetSubDir = Path.Combine(target.FullName, subDir.Name);
                CopyDirectoryAsync(subDir.FullName, targetSubDir, excludeGit).GetAwaiter().GetResult();
            }
        });
    }
    
    #endregion
}
