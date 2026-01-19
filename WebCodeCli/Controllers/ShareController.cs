using Microsoft.AspNetCore.Mvc;
using WebCodeCli.Domain.Domain.Service;
using WebCodeCli.Domain.Repositories.Base.SessionShare;

namespace WebCodeCli.Controllers;

/// <summary>
/// 会话分享控制器
/// </summary>
[ApiController]
[Route("api/share")]
public class ShareController : ControllerBase
{
    private readonly ISessionShareService _shareService;
    private readonly ISessionHistoryManager _sessionHistoryManager;
    private readonly ICliExecutorService _cliExecutorService;
    private readonly ILogger<ShareController> _logger;

    public ShareController(
        ISessionShareService shareService,
        ISessionHistoryManager sessionHistoryManager,
        ICliExecutorService cliExecutorService,
        ILogger<ShareController> logger)
    {
        _shareService = shareService;
        _sessionHistoryManager = sessionHistoryManager;
        _cliExecutorService = cliExecutorService;
        _logger = logger;
    }

    /// <summary>
    /// 创建分享
    /// POST /api/share
    /// </summary>
    [HttpPost]
    public async Task<IActionResult> CreateShare([FromBody] CreateShareRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(request.SessionId))
            {
                return BadRequest(new { error = "会话ID不能为空" });
            }

            if (string.IsNullOrWhiteSpace(request.Password))
            {
                return BadRequest(new { error = "密码不能为空" });
            }

            if (request.Password.Length < 4)
            {
                return BadRequest(new { error = "密码长度不能少于4位" });
            }

            var result = await _shareService.CreateShareAsync(
                request.SessionId,
                request.Password,
                request.Title,
                request.ExpiresAt);

            // 生成完整的分享链接
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            result.ShareUrl = _shareService.GenerateShareUrl(result.ShareCode, baseUrl);

            _logger.LogInformation("分享创建成功: ShareCode={ShareCode}, SessionId={SessionId}", 
                result.ShareCode, request.SessionId);

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建分享失败");
            return StatusCode(500, new { error = "创建分享失败", message = ex.Message });
        }
    }

    /// <summary>
    /// 验证分享密码
    /// POST /api/share/{shareCode}/validate
    /// </summary>
    [HttpPost("{shareCode}/validate")]
    public async Task<IActionResult> ValidateShare(string shareCode, [FromBody] ValidateShareRequest request)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(shareCode))
            {
                return BadRequest(new { error = "分享码不能为空" });
            }

            if (string.IsNullOrWhiteSpace(request.Password))
            {
                return BadRequest(new { error = "密码不能为空" });
            }

            var result = await _shareService.ValidateShareAsync(shareCode, request.Password);

            if (!result.IsValid)
            {
                return Unauthorized(new { error = result.ErrorMessage });
            }

            return Ok(result);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "验证分享失败: ShareCode={ShareCode}", shareCode);
            return StatusCode(500, new { error = "验证失败", message = ex.Message });
        }
    }

    /// <summary>
    /// 获取分享信息（不需要密码，用于显示分享页面基本信息）
    /// GET /api/share/{shareCode}/info
    /// </summary>
    [HttpGet("{shareCode}/info")]
    public async Task<IActionResult> GetShareInfo(string shareCode)
    {
        try
        {
            var info = await _shareService.GetShareInfoAsync(shareCode);

            if (info == null)
            {
                return NotFound(new { error = "分享不存在" });
            }

            // 返回基本信息，不包含敏感数据
            return Ok(new
            {
                info.ShareCode,
                info.Title,
                info.CreatedBy,
                info.CreatedAt,
                info.ExpiresAt,
                info.IsActive,
                info.IsExpired
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取分享信息失败: ShareCode={ShareCode}", shareCode);
            return StatusCode(500, new { error = "获取分享信息失败", message = ex.Message });
        }
    }

    /// <summary>
    /// 获取共享的会话数据（需要访问令牌）
    /// GET /api/share/{shareCode}/session
    /// </summary>
    [HttpGet("{shareCode}/session")]
    public async Task<IActionResult> GetSharedSession(string shareCode, [FromHeader(Name = "X-Share-Token")] string accessToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return Unauthorized(new { error = "缺少访问令牌" });
            }

            var sessionId = await _shareService.GetSessionIdByTokenAsync(shareCode, accessToken);

            if (string.IsNullOrEmpty(sessionId))
            {
                return Unauthorized(new { error = "访问令牌无效或已过期" });
            }

            // 获取会话信息
            var session = await _sessionHistoryManager.GetSessionAsync(sessionId);

            if (session == null)
            {
                return NotFound(new { error = "会话不存在" });
            }

            // 返回会话数据（只读视图）
            return Ok(new
            {
                session.SessionId,
                session.Title,
                session.ToolId,
                session.CreatedAt,
                session.UpdatedAt,
                session.Messages,
                session.WorkspacePath,
                session.IsWorkspaceValid
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取共享会话失败: ShareCode={ShareCode}", shareCode);
            return StatusCode(500, new { error = "获取会话失败", message = ex.Message });
        }
    }

    /// <summary>
    /// 获取共享工作区的文件列表
    /// GET /api/share/{shareCode}/workspace/files
    /// </summary>
    [HttpGet("{shareCode}/workspace/files")]
    public async Task<IActionResult> GetSharedWorkspaceFiles(string shareCode, [FromHeader(Name = "X-Share-Token")] string accessToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return Unauthorized(new { error = "缺少访问令牌" });
            }

            var sessionId = await _shareService.GetSessionIdByTokenAsync(shareCode, accessToken);

            if (string.IsNullOrEmpty(sessionId))
            {
                return Unauthorized(new { error = "访问令牌无效或已过期" });
            }

            // 获取工作区路径
            var workspacePath = _cliExecutorService.GetSessionWorkspacePath(sessionId);

            if (!Directory.Exists(workspacePath))
            {
                return NotFound(new { error = "工作区不存在" });
            }

            // 构建文件树
            var files = BuildFileTree(workspacePath, workspacePath);

            return Ok(files);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取共享工作区文件失败: ShareCode={ShareCode}", shareCode);
            return StatusCode(500, new { error = "获取文件失败", message = ex.Message });
        }
    }

    /// <summary>
    /// 获取共享工作区的文件内容
    /// GET /api/share/{shareCode}/workspace/file/{**filePath}
    /// </summary>
    [HttpGet("{shareCode}/workspace/file/{**filePath}")]
    public async Task<IActionResult> GetSharedWorkspaceFile(
        string shareCode, 
        string filePath,
        [FromHeader(Name = "X-Share-Token")] string accessToken)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return Unauthorized(new { error = "缺少访问令牌" });
            }

            var sessionId = await _shareService.GetSessionIdByTokenAsync(shareCode, accessToken);

            if (string.IsNullOrEmpty(sessionId))
            {
                return Unauthorized(new { error = "访问令牌无效或已过期" });
            }

            // 获取文件内容
            var fileBytes = _cliExecutorService.GetWorkspaceFile(sessionId, filePath);

            if (fileBytes == null)
            {
                return NotFound(new { error = "文件不存在" });
            }

            // 确定 Content-Type
            var contentType = GetContentType(filePath);

            return File(fileBytes, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取共享工作区文件内容失败: ShareCode={ShareCode}, FilePath={FilePath}", 
                shareCode, filePath);
            return StatusCode(500, new { error = "获取文件失败", message = ex.Message });
        }
    }

    /// <summary>
    /// 获取会话的所有分享
    /// GET /api/share/session/{sessionId}
    /// </summary>
    [HttpGet("session/{sessionId}")]
    public async Task<IActionResult> GetSessionShares(string sessionId)
    {
        try
        {
            var shares = await _shareService.GetSessionSharesAsync(sessionId);

            // 生成完整的分享链接
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            foreach (var share in shares)
            {
                share.ShareUrl = _shareService.GenerateShareUrl(share.ShareCode, baseUrl);
            }

            return Ok(shares);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取会话分享列表失败: SessionId={SessionId}", sessionId);
            return StatusCode(500, new { error = "获取分享列表失败", message = ex.Message });
        }
    }

    /// <summary>
    /// 停用分享
    /// PUT /api/share/{shareCode}/deactivate
    /// </summary>
    [HttpPut("{shareCode}/deactivate")]
    public async Task<IActionResult> DeactivateShare(string shareCode)
    {
        try
        {
            var result = await _shareService.DeactivateShareAsync(shareCode);

            if (!result)
            {
                return NotFound(new { error = "分享不存在" });
            }

            return Ok(new { message = "分享已停用" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "停用分享失败: ShareCode={ShareCode}", shareCode);
            return StatusCode(500, new { error = "停用分享失败", message = ex.Message });
        }
    }

    /// <summary>
    /// 删除分享
    /// DELETE /api/share/{shareCode}
    /// </summary>
    [HttpDelete("{shareCode}")]
    public async Task<IActionResult> DeleteShare(string shareCode)
    {
        try
        {
            var result = await _shareService.DeleteShareAsync(shareCode);

            if (!result)
            {
                return NotFound(new { error = "分享不存在" });
            }

            return Ok(new { message = "分享已删除" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除分享失败: ShareCode={ShareCode}", shareCode);
            return StatusCode(500, new { error = "删除分享失败", message = ex.Message });
        }
    }

    #region 私有方法

    /// <summary>
    /// 构建文件树
    /// </summary>
    private List<WorkspaceFileNode> BuildFileTree(string rootPath, string currentPath)
    {
        var result = new List<WorkspaceFileNode>();

        try
        {
            // 获取目录
            var directories = Directory.GetDirectories(currentPath);
            foreach (var dir in directories)
            {
                var dirInfo = new DirectoryInfo(dir);
                
                // 跳过隐藏目录和特殊目录
                if (dirInfo.Name.StartsWith(".") || 
                    dirInfo.Name == "node_modules" ||
                    dirInfo.Name == "__pycache__")
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(rootPath, dir).Replace("\\", "/");
                
                result.Add(new WorkspaceFileNode
                {
                    Name = dirInfo.Name,
                    Path = relativePath,
                    Type = "folder",
                    Children = BuildFileTree(rootPath, dir)
                });
            }

            // 获取文件
            var files = Directory.GetFiles(currentPath);
            foreach (var file in files)
            {
                var fileInfo = new FileInfo(file);
                
                // 跳过隐藏文件
                if (fileInfo.Name.StartsWith("."))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(rootPath, file).Replace("\\", "/");
                var extension = fileInfo.Extension.ToLower();

                result.Add(new WorkspaceFileNode
                {
                    Name = fileInfo.Name,
                    Path = relativePath,
                    Type = "file",
                    Size = fileInfo.Length,
                    Extension = extension,
                    IsHtml = extension == ".html" || extension == ".htm"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "构建文件树时发生错误: Path={Path}", currentPath);
        }

        return result;
    }

    /// <summary>
    /// 获取文件的 Content-Type
    /// </summary>
    private static string GetContentType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLower();
        
        return extension switch
        {
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".txt" => "text/plain",
            ".md" => "text/markdown",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            _ => "application/octet-stream"
        };
    }

    #endregion
}

/// <summary>
/// 工作区文件节点（用于API返回）
/// </summary>
public class WorkspaceFileNode
{
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public long Size { get; set; }
    public string Extension { get; set; } = string.Empty;
    public bool IsHtml { get; set; }
    public List<WorkspaceFileNode>? Children { get; set; }
}
