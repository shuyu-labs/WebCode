using Microsoft.Extensions.Logging;
using SqlSugar;
using WebCodeCli.Domain.Common;
using WebCodeCli.Domain.Common.Map;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Repositories.Base.SessionShare;
using WebCodeCli.Domain.Repositories.Base.ChatSession;
using WebCodeCli.Domain.Repositories.Base.SessionOutput;
using WebCodeCli.Domain.Repositories.Base.Template;
using WebCodeCli.Domain.Repositories.Base.InputHistory;
using WebCodeCli.Domain.Repositories.Base.QuickAction;
using WebCodeCli.Domain.Repositories.Base.UserSetting;
using WebCodeCli.Domain.Repositories.Base.Project;
using WebCodeCli.Domain.Repositories.Base.ScheduledTask;

namespace WebCodeCli.Domain.Common.Extensions;

/// <summary>
/// 数据库表初始化扩展
/// </summary>
public static class DatabaseInitializer
{
    /// <summary>
    /// 初始化CLI工具相关的数据库表
    /// </summary>
    public static void InitializeCliToolTables(this SqlSugarScope db, ILogger? logger = null)
    {
        try
        {
            // 创建CLI工具环境变量表
            logger?.LogInformation("开始初始化 CLI 工具环境变量表...");
            
            db.CodeFirst.InitTables<CliToolEnvironmentVariable>();
            
            logger?.LogInformation("CLI 工具环境变量表初始化成功");
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "初始化 CLI 工具环境变量表失败");
            throw;
        }
    }
    
    /// <summary>
    /// 初始化会话分享相关的数据库表
    /// </summary>
    public static void InitializeSessionShareTables(this SqlSugarScope db, ILogger? logger = null)
    {
        try
        {
            // 创建会话分享表
            logger?.LogInformation("开始初始化会话分享表...");
            
            db.CodeFirst.InitTables<SessionShare>();
            
            logger?.LogInformation("会话分享表初始化成功");
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "初始化会话分享表失败");
            throw;
        }
    }
    
    /// <summary>
    /// 初始化项目管理相关的数据库表
    /// </summary>
    public static void InitializeProjectTables(this SqlSugarScope db, ILogger? logger = null)
    {
        try
        {
            logger?.LogInformation("开始初始化项目管理表...");
            
            // 创建项目表
            db.CodeFirst.InitTables<ProjectEntity>();
            
            logger?.LogInformation("项目管理表初始化成功");
            
            // 创建索引
            InitializeProjectIndexes(db, logger);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "初始化项目管理表失败");
            throw;
        }
    }
    
    /// <summary>
    /// 为项目表创建索引
    /// </summary>
    private static void InitializeProjectIndexes(SqlSugarScope db, ILogger? logger = null)
    {
        try
        {
            logger?.LogInformation("开始创建项目相关索引...");
            
            // Project: Username + UpdatedAt 索引（项目列表排序）
            CreateIndexIfNotExists(db, "Project", "IX_Project_Username_UpdatedAt", 
                new[] { "Username", "UpdatedAt" }, logger);
            
            // Project: Username + Name 唯一索引（确保同一用户的项目名称唯一）
            CreateIndexIfNotExists(db, "Project", "IX_Project_Username_Name", 
                new[] { "Username", "Name" }, logger, isUnique: true);
            
            logger?.LogInformation("项目相关索引创建成功");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "创建项目索引时发生警告（可能索引已存在）");
        }
    }
    
    /// <summary>
    /// 初始化定时任务相关的数据库表
    /// </summary>
    public static void InitializeScheduledTaskTables(this SqlSugarScope db, ILogger? logger = null)
    {
        try
        {
            logger?.LogInformation("开始初始化定时任务相关表...");
            
            // 创建定时任务表
            db.CodeFirst.InitTables<ScheduledTaskEntity>();
            
            // 创建任务执行记录表
            db.CodeFirst.InitTables<TaskExecutionEntity>();
            
            logger?.LogInformation("定时任务相关表初始化成功");
            
            // 创建索引
            InitializeScheduledTaskIndexes(db, logger);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "初始化定时任务相关表失败");
            throw;
        }
    }
    
    /// <summary>
    /// 为定时任务相关表创建索引
    /// </summary>
    private static void InitializeScheduledTaskIndexes(SqlSugarScope db, ILogger? logger = null)
    {
        try
        {
            logger?.LogInformation("开始创建定时任务相关索引...");
            
            // ScheduledTask: Username + UpdatedAt 索引（任务列表排序）
            CreateIndexIfNotExists(db, "ScheduledTask", "IX_ScheduledTask_Username_UpdatedAt", 
                new[] { "Username", "UpdatedAt" }, logger);
            
            // ScheduledTask: Username + IsEnabled 索引（筛选启用的任务）
            CreateIndexIfNotExists(db, "ScheduledTask", "IX_ScheduledTask_Username_IsEnabled", 
                new[] { "Username", "IsEnabled" }, logger);
            
            // ScheduledTask: Username + Name 唯一索引（确保同一用户的任务名称唯一）
            CreateIndexIfNotExists(db, "ScheduledTask", "IX_ScheduledTask_Username_Name", 
                new[] { "Username", "Name" }, logger, isUnique: true);
            
            // TaskExecution: TaskId + StartTime 索引（按任务查询执行历史）
            CreateIndexIfNotExists(db, "TaskExecution", "IX_TaskExecution_TaskId_StartTime", 
                new[] { "TaskId", "StartTime" }, logger);
            
            // TaskExecution: Status 索引（查询正在运行的任务）
            CreateIndexIfNotExists(db, "TaskExecution", "IX_TaskExecution_Status", 
                new[] { "Status" }, logger);
            
            logger?.LogInformation("定时任务相关索引创建成功");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "创建定时任务索引时发生警告（可能索引已存在）");
        }
    }
    
    /// <summary>
    /// 初始化聊天会话相关的数据库表（从 IndexedDB 迁移）
    /// </summary>
    public static void InitializeChatSessionTables(this SqlSugarScope db, ILogger? logger = null)
    {
        try
        {
            logger?.LogInformation("开始初始化聊天会话相关表...");
            
            // 创建聊天会话表
            db.CodeFirst.InitTables<ChatSessionEntity>();
            
            // 创建聊天消息表
            db.CodeFirst.InitTables<ChatMessageEntity>();
            db.CodeFirst.InitTables<ChatMessageAttachmentEntity>();
            
            // 创建会话输出状态表
            db.CodeFirst.InitTables<SessionOutputEntity>();
            
            // 创建提示模板表
            db.CodeFirst.InitTables<PromptTemplateEntity>();
            
            // 创建输入历史表
            db.CodeFirst.InitTables<InputHistoryEntity>();
            
            // 创建快捷操作表
            db.CodeFirst.InitTables<QuickActionEntity>();
            
            // 创建用户设置表
            db.CodeFirst.InitTables<UserSettingEntity>();
            
            logger?.LogInformation("聊天会话相关表初始化成功");

            EnsureChatSessionSchema(db, logger);
            
            // 创建索引
            InitializeChatSessionIndexes(db, logger);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "初始化聊天会话相关表失败");
            throw;
        }
    }
    
    /// <summary>
    /// 确保 ChatSession 表的增量字段和兼容数据已就位
    /// </summary>
    private static void EnsureChatSessionSchema(SqlSugarScope db, ILogger? logger = null)
    {
        try
        {
            EnsureColumnIfNotExists(db, "ChatSession", "CliThreadId", "varchar(256) NULL", logger);
            EnsureColumnIfNotExists(db, "ChatSession", "UsesCcSwitchSnapshot", "INTEGER NOT NULL DEFAULT 0", logger);
            EnsureColumnIfNotExists(db, "ChatSession", "CcSwitchSnapshotToolId", "varchar(64) NULL", logger);
            EnsureColumnIfNotExists(db, "ChatSession", "CcSwitchProviderId", "varchar(256) NULL", logger);
            EnsureColumnIfNotExists(db, "ChatSession", "CcSwitchProviderName", "varchar(256) NULL", logger);
            EnsureColumnIfNotExists(db, "ChatSession", "CcSwitchProviderCategory", "varchar(128) NULL", logger);
            EnsureColumnIfNotExists(db, "ChatSession", "CcSwitchLiveConfigPath", "varchar(1024) NULL", logger);
            EnsureColumnIfNotExists(db, "ChatSession", "CcSwitchSnapshotRelativePath", "varchar(512) NULL", logger);
            EnsureColumnIfNotExists(db, "ChatSession", "CcSwitchSnapshotSyncedAt", "datetime NULL", logger);
            EnsureColumnIfNotExists(db, "ChatSession", "ToolLaunchOverridesJson", "TEXT NULL", logger);
            EnsureColumnIfNotExists(db, "ChatMessage", "MessageId", "varchar(64) NOT NULL DEFAULT ''", logger);
            BackfillLegacyChatMessageIds(db, logger);
            BackfillCliThreadIdsFromImportedTitles(db, logger);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "校正 ChatSession 表结构时发生警告");
        }
    }

    private static void EnsureColumnIfNotExists(
        SqlSugarScope db,
        string tableName,
        string columnName,
        string columnDefinition,
        ILogger? logger = null)
    {
        var pragmaSql = $"PRAGMA table_info(\"{tableName}\")";
        var tableInfo = db.Ado.GetDataTable(pragmaSql);
        var columnExists = tableInfo.Rows.Cast<System.Data.DataRow>()
            .Any(row => string.Equals(row["name"]?.ToString(), columnName, StringComparison.OrdinalIgnoreCase));

        if (columnExists)
        {
            return;
        }

        db.Ado.ExecuteCommand($"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition}");
        logger?.LogInformation("已为表 {TableName} 补充列 {ColumnName}", tableName, columnName);
    }

    private static void BackfillCliThreadIdsFromImportedTitles(SqlSugarScope db, ILogger? logger = null)
    {
        var sessions = db.Queryable<ChatSessionEntity>()
            .Where(x => x.CliThreadId == null || x.CliThreadId == string.Empty)
            .Select(x => new ChatSessionEntity
            {
                SessionId = x.SessionId,
                ToolId = x.ToolId,
                Title = x.Title
            })
            .ToList();

        var recoveredCount = 0;
        foreach (var session in sessions)
        {
            var cliThreadId = CliThreadIdRecoveryHelper.TryRecoverFromImportedTitle(session.ToolId, session.Title);
            if (string.IsNullOrWhiteSpace(cliThreadId))
            {
                continue;
            }

            recoveredCount += db.Updateable<ChatSessionEntity>()
                .SetColumns(x => x.CliThreadId == cliThreadId)
                .Where(x => x.SessionId == session.SessionId)
                .ExecuteCommand();
        }

        if (recoveredCount > 0)
        {
            logger?.LogInformation("已从导入标题回填 {Count} 条 ChatSession.CliThreadId", recoveredCount);
        }
    }

    /// <summary>
    /// 为聊天会话相关表创建索引
    /// </summary>
    private static void BackfillLegacyChatMessageIds(SqlSugarScope db, ILogger? logger = null)
    {
        var rows = db.Ado.ExecuteCommand(
            "UPDATE ChatMessage SET MessageId = 'legacy-msg-' || Id WHERE IFNULL(MessageId, '') = ''");

        if (rows > 0)
        {
            logger?.LogInformation("Backfilled {Count} ChatMessage.MessageId values", rows);
        }
    }

    private static void InitializeChatSessionIndexes(SqlSugarScope db, ILogger? logger = null)
    {
        try
        {
            logger?.LogInformation("开始创建聊天会话相关索引...");
            
            // ChatSession: Username + UpdatedAt 索引（会话列表排序）
            CreateIndexIfNotExists(db, "ChatSession", "IX_ChatSession_Username_UpdatedAt", 
                new[] { "Username", "UpdatedAt" }, logger);

            // ChatSession: Username + ToolId + CliThreadId 索引（用于恢复外部/CLI会话时快速查找）
            // 说明：CliThreadId 允许为空；SQLite 对 NULL 的唯一性处理较友好，但这里先用普通索引即可。
            CreateIndexIfNotExists(db, "ChatSession", "IX_ChatSession_Username_ToolId_CliThreadId",
                new[] { "Username", "ToolId", "CliThreadId" }, logger);

            // ChatSession: ToolId + CliThreadId 唯一索引（跨用户，确保一个外部 CLI 会话只能被一个用户占用）
            // 说明：CliThreadId 允许为空；SQLite UNIQUE INDEX 允许多个 NULL，不影响旧数据。
            CreateIndexIfNotExists(db, "ChatSession", "IX_ChatSession_ToolId_CliThreadId",
                new[] { "ToolId", "CliThreadId" }, logger, isUnique: true);
            
            // ChatMessage: SessionId 索引（按会话查询消息）
            CreateIndexIfNotExists(db, "ChatMessage", "IX_ChatMessage_SessionId", 
                new[] { "SessionId" }, logger);

            CreateIndexIfNotExists(db, "ChatMessageAttachment", "IX_ChatMessageAttachment_MessageId",
                new[] { "MessageId" }, logger);

            CreateIndexIfNotExists(db, "ChatMessageAttachment", "IX_ChatMessageAttachment_SessionId",
                new[] { "SessionId" }, logger);
            
            // ChatMessage: Username + SessionId 索引
            CreateIndexIfNotExists(db, "ChatMessage", "IX_ChatMessage_Username_SessionId", 
                new[] { "Username", "SessionId" }, logger);
            
            // PromptTemplate: Username + Category 索引
            CreateIndexIfNotExists(db, "PromptTemplate", "IX_PromptTemplate_Username_Category", 
                new[] { "Username", "Category" }, logger);
            
            // InputHistory: Username + Timestamp 索引
            CreateIndexIfNotExists(db, "InputHistory", "IX_InputHistory_Username_Timestamp", 
                new[] { "Username", "Timestamp" }, logger);
            
            // QuickAction: Username + Order 索引
            CreateIndexIfNotExists(db, "QuickAction", "IX_QuickAction_Username_Order", 
                new[] { "Username", "\"Order\"" }, logger);
            
            // UserSetting: Username + Key 唯一索引
            CreateIndexIfNotExists(db, "UserSetting", "IX_UserSetting_Username_Key", 
                new[] { "Username", "Key" }, logger, isUnique: true);
            
            // SessionOutput: Username 索引
            CreateIndexIfNotExists(db, "SessionOutput", "IX_SessionOutput_Username", 
                new[] { "Username" }, logger);
            
            logger?.LogInformation("聊天会话相关索引创建成功");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "创建索引时发生警告（可能索引已存在）");
        }
    }
    
    /// <summary>
    /// 如果索引不存在则创建
    /// </summary>
    private static void CreateIndexIfNotExists(SqlSugarScope db, string tableName, string indexName, 
        string[] columns, ILogger? logger, bool isUnique = false)
    {
        try
        {
            // 检查索引是否存在（SQLite 语法）
            var checkSql = $"SELECT name FROM sqlite_master WHERE type='index' AND name='{indexName}'";
            var exists = db.Ado.GetDataTable(checkSql).Rows.Count > 0;
            
            if (!exists)
            {
                var columnsStr = string.Join(", ", columns);
                var uniqueStr = isUnique ? "UNIQUE " : "";
                var createSql = $"CREATE {uniqueStr}INDEX {indexName} ON {tableName} ({columnsStr})";
                
                db.Ado.ExecuteCommand(createSql);
                logger?.LogDebug("索引 {IndexName} 创建成功", indexName);
            }
            else
            {
                logger?.LogDebug("索引 {IndexName} 已存在，跳过创建", indexName);
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "创建索引 {IndexName} 失败", indexName);
        }
    }
}
