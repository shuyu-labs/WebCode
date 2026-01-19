using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SqlSugar;
using System;
using System.Threading.Tasks;
using WebCodeCli.Domain.Repositories.Base.SessionShare;

namespace WebCodeCli.Controllers;

/// <summary>
/// 数据库迁移控制器
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class MigrationController : ControllerBase
{
    private readonly ILogger<MigrationController> _logger;
    private readonly ISessionShareRepository _sessionShareRepository;

    public MigrationController(
        ILogger<MigrationController> logger,
        ISessionShareRepository sessionShareRepository)
    {
        _logger = logger;
        _sessionShareRepository = sessionShareRepository;
    }

    /// <summary>
    /// 手动执行SessionShare表迁移
    /// </summary>
    [HttpPost("session-share")]
    public async Task<IActionResult> MigrateSessionShare()
    {
        try
        {
            _logger.LogInformation("开始手动迁移SessionShare表...");
            
            var db = _sessionShareRepository.GetDB();
            
            // 检查表是否存在
            bool tableExists = db.DbMaintenance.IsAnyTable("SessionShare", false);
            _logger.LogInformation($"SessionShare表存在状态: {tableExists}");
            
            if (!tableExists)
            {
                _logger.LogInformation("表不存在，开始创建...");
                db.CodeFirst.InitTables<SessionShare>();
                _logger.LogInformation("SessionShare表创建成功");
                
                return Ok(new 
                { 
                    success = true, 
                    message = "SessionShare表创建成功",
                    existed = false
                });
            }
            else
            {
                _logger.LogInformation("表已存在");
                
                // 获取表结构信息
                var columns = db.DbMaintenance.GetColumnInfosByTableName("SessionShare", false);
                
                return Ok(new 
                { 
                    success = true, 
                    message = "SessionShare表已存在",
                    existed = true,
                    columnCount = columns.Count
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "迁移SessionShare表失败");
            return StatusCode(500, new 
            { 
                success = false, 
                message = $"迁移失败: {ex.Message}",
                stackTrace = ex.StackTrace
            });
        }
    }

    /// <summary>
    /// 检查所有表状态
    /// </summary>
    [HttpGet("check-tables")]
    public IActionResult CheckTables()
    {
        try
        {
            var db = _sessionShareRepository.GetDB();
            var tables = db.DbMaintenance.GetTableInfoList(false);
            
            return Ok(new 
            { 
                success = true, 
                tableCount = tables.Count,
                tables = tables.Select(t => t.Name).ToList()
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "检查表状态失败");
            return StatusCode(500, new 
            { 
                success = false, 
                message = $"检查失败: {ex.Message}"
            });
        }
    }

    /// <summary>
    /// 重建SessionShare表（危险操作，会删除现有数据）
    /// </summary>
    [HttpPost("rebuild-session-share")]
    public IActionResult RebuildSessionShare()
    {
        try
        {
            _logger.LogWarning("开始重建SessionShare表（将删除现有数据）...");
            
            var db = _sessionShareRepository.GetDB();
            
            // 删除表
            if (db.DbMaintenance.IsAnyTable("SessionShare", false))
            {
                db.DbMaintenance.DropTable("SessionShare");
                _logger.LogInformation("SessionShare表已删除");
            }
            
            // 重新创建
            db.CodeFirst.InitTables<SessionShare>();
            _logger.LogInformation("SessionShare表已重建");
            
            return Ok(new 
            { 
                success = true, 
                message = "SessionShare表重建成功"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重建SessionShare表失败");
            return StatusCode(500, new 
            { 
                success = false, 
                message = $"重建失败: {ex.Message}",
                stackTrace = ex.StackTrace
            });
        }
    }
}
