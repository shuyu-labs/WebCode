using Microsoft.AspNetCore.Mvc;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Service;

namespace WebCodeCli.Controllers;

/// <summary>
/// 项目管理 API 控制器
/// </summary>
[ApiController]
[Route("api/project")]
public class ProjectController : ControllerBase
{
    private readonly IProjectService _projectService;
    private readonly ILogger<ProjectController> _logger;

    public ProjectController(
        IProjectService projectService,
        ILogger<ProjectController> logger)
    {
        _projectService = projectService;
        _logger = logger;
    }

    /// <summary>
    /// 获取所有项目
    /// </summary>
    [HttpGet]
    public async Task<ActionResult<List<ProjectInfo>>> GetProjects()
    {
        try
        {
            var projects = await _projectService.GetProjectsAsync();
            return Ok(projects);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取项目列表失败");
            return StatusCode(500, new { Error = "获取项目列表失败" });
        }
    }

    /// <summary>
    /// 获取单个项目
    /// </summary>
    [HttpGet("{projectId}")]
    public async Task<ActionResult<ProjectInfo>> GetProject(string projectId)
    {
        try
        {
            var project = await _projectService.GetProjectAsync(projectId);
            
            if (project == null)
            {
                return NotFound(new { Error = "项目不存在" });
            }
            
            return Ok(project);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取项目失败: {ProjectId}", projectId);
            return StatusCode(500, new { Error = "获取项目失败" });
        }
    }

    /// <summary>
    /// 创建项目
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ProjectInfo>> CreateProject([FromBody] CreateProjectRequest request)
    {
        try
        {
            if (request == null)
            {
                return BadRequest(new { Error = "无效的请求数据" });
            }
            
            var (project, errorMessage) = await _projectService.CreateProjectAsync(request);
            
            if (project == null)
            {
                return BadRequest(new { Error = errorMessage ?? "创建项目失败" });
            }
            
            return Ok(project);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "创建项目失败");
            return StatusCode(500, new { Error = "创建项目失败" });
        }
    }

    /// <summary>
    /// 从 ZIP 压缩包创建项目
    /// </summary>
    [HttpPost("upload-zip")]
    [RequestSizeLimit(100_000_000)] // 100MB 限制
    public async Task<ActionResult<ProjectInfo>> CreateProjectFromZip([FromForm] string name, IFormFile zipFile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return BadRequest(new { Error = "项目名称不能为空" });
            }
            
            if (zipFile == null || zipFile.Length == 0)
            {
                return BadRequest(new { Error = "请选择 ZIP 文件" });
            }
            
            // 验证文件扩展名
            var extension = Path.GetExtension(zipFile.FileName)?.ToLowerInvariant();
            if (extension != ".zip")
            {
                return BadRequest(new { Error = "请选择有效的 ZIP 文件" });
            }
            
            // 验证文件大小（100MB）
            if (zipFile.Length > 100_000_000)
            {
                return BadRequest(new { Error = "文件过大，最大支持 100MB" });
            }
            
            // 读取文件内容
            using var memoryStream = new MemoryStream();
            await zipFile.CopyToAsync(memoryStream);
            var zipContent = memoryStream.ToArray();
            
            var (project, errorMessage) = await _projectService.CreateProjectFromZipAsync(name, zipContent);
            
            if (project == null)
            {
                return BadRequest(new { Error = errorMessage ?? "创建项目失败" });
            }
            
            return Ok(project);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "从 ZIP 创建项目失败");
            return StatusCode(500, new { Error = "创建项目失败" });
        }
    }

    /// <summary>
    /// 更新项目
    /// </summary>
    [HttpPut("{projectId}")]
    public async Task<ActionResult> UpdateProject(string projectId, [FromBody] UpdateProjectRequest request)
    {
        try
        {
            if (request == null)
            {
                return BadRequest(new { Error = "无效的请求数据" });
            }
            
            var (success, errorMessage) = await _projectService.UpdateProjectAsync(projectId, request);
            
            if (!success)
            {
                return BadRequest(new { Error = errorMessage ?? "更新项目失败" });
            }
            
            return Ok(new { Success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新项目失败: {ProjectId}", projectId);
            return StatusCode(500, new { Error = "更新项目失败" });
        }
    }

    /// <summary>
    /// 删除项目
    /// </summary>
    [HttpDelete("{projectId}")]
    public async Task<ActionResult> DeleteProject(string projectId)
    {
        try
        {
            var (success, errorMessage) = await _projectService.DeleteProjectAsync(projectId);
            
            if (!success)
            {
                return BadRequest(new { Error = errorMessage ?? "删除项目失败" });
            }
            
            return Ok(new { Success = true });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除项目失败: {ProjectId}", projectId);
            return StatusCode(500, new { Error = "删除项目失败" });
        }
    }

    /// <summary>
    /// 克隆项目代码
    /// </summary>
    [HttpPost("{projectId}/clone")]
    public async Task<ActionResult> CloneProject(string projectId)
    {
        try
        {
            var (success, errorMessage) = await _projectService.CloneProjectAsync(projectId);
            
            if (!success)
            {
                return BadRequest(new { Error = errorMessage ?? "克隆项目失败" });
            }
            
            return Ok(new { Success = true, Message = "项目克隆成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "克隆项目失败: {ProjectId}", projectId);
            return StatusCode(500, new { Error = "克隆项目失败" });
        }
    }

    /// <summary>
    /// 更新项目代码（拉取最新）
    /// </summary>
    [HttpPost("{projectId}/pull")]
    public async Task<ActionResult> PullProject(string projectId)
    {
        try
        {
            var (success, errorMessage) = await _projectService.PullProjectAsync(projectId);
            
            if (!success)
            {
                return BadRequest(new { Error = errorMessage ?? "更新项目失败" });
            }
            
            return Ok(new { Success = true, Message = "项目更新成功" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "更新项目失败: {ProjectId}", projectId);
            return StatusCode(500, new { Error = "更新项目失败" });
        }
    }

    /// <summary>
    /// 获取远程仓库的分支列表
    /// </summary>
    [HttpPost("branches")]
    public async Task<ActionResult<List<string>>> GetBranches([FromBody] GetBranchesRequest request)
    {
        try
        {
            if (request == null || string.IsNullOrWhiteSpace(request.GitUrl))
            {
                return BadRequest(new { Error = "Git 仓库地址不能为空" });
            }
            
            var (branches, errorMessage) = await _projectService.GetBranchesAsync(request);
            
            if (errorMessage != null)
            {
                return BadRequest(new { Error = errorMessage, Branches = branches });
            }
            
            return Ok(branches);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "获取分支列表失败");
            return StatusCode(500, new { Error = "获取分支列表失败" });
        }
    }
}
