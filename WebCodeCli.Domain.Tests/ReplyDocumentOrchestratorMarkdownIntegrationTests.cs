using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SqlSugar;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service;
using WebCodeCli.Domain.Domain.Service.Channels;
using WebCodeCli.Domain.Model;
using WebCodeCli.Domain.Repositories.Base.ChatSession;
using WebCodeCli.Domain.Repositories.Base.UserFeishuBotConfig;

namespace WebCodeCli.Domain.Tests;

public sealed class ReplyDocumentOrchestratorMarkdownIntegrationTests
{
    [Fact]
    public async Task QueueCompletedReplyAsync_WhenReplyDocumentBodyContainsMarkdown_AppendsPlainTextInsteadOfConvertedBlocks()
    {
        using var harness = new ReplyDocumentOrchestratorHarness(
            new UserFeishuBotConfigEntity
            {
                Username = "luhaiyan",
                FullReplyDocEnabled = true
            });

        harness.CardKit.ConvertedBlocks.Add(ParseJsonElement("""{"block_type":2,"text":{"elements":[{"text_run":{"content":"转换后的正文","text_element_style":{}}}]}}"""));

        await harness.Orchestrator.QueueCompletedReplyAsync(new FeishuCompletedReplyDocumentRequest
        {
            ChatId = "oc-convert-chat",
            SessionId = "session-convert",
            CliThreadId = "thread-convert",
            OriginalUserQuestion = "question",
            Username = "luhaiyan",
            Output = "# 标题"
        });

        await WaitUntilAsync(() => harness.CardKit.TextMessages.Count == 1);

        Assert.Empty(harness.CardKit.AppendedBlockBatches);
        var appendedText = Assert.Single(harness.CardKit.AppendedTexts);
        Assert.Equal("doc-1", appendedText.DocumentId);
        Assert.Equal("## 用户内容\n\nquestion\n\n---\n\n# 标题", appendedText.Text);
    }

    [Fact]
    public async Task QueueCompletedReplyAsync_WhenReplyDocumentBodyContainsStructuredSections_PreservesOriginalBodyOrderInPlainTextAppend()
    {
        using var harness = new ReplyDocumentOrchestratorHarness(
            new UserFeishuBotConfigEntity
            {
                Username = "luhaiyan",
                FullReplyDocEnabled = true
            });

        harness.CardKit.ConvertedBlocks.Add(ParseJsonElement("""{"block_type":2,"text":{"elements":[{"text_run":{"content":"乱序块A","text_element_style":{}}}]}}"""));
        harness.CardKit.ConvertedBlocks.Add(ParseJsonElement("""{"block_type":2,"text":{"elements":[{"text_run":{"content":"乱序块B","text_element_style":{}}}]}}"""));

        var assistantBody = """
我会把刚才的 AI-first 操作面确认纳入 Route 07 设计记录，继续做最后几类设计收口：人工 UI 验证、公共/领域组件、后端契约和技能治理。先重新加载本轮必须使用的 superpowers 与 MMIS 约束，然后继续单问题确认。Route 07 第十七个需要确认的问题：人工 UI 验证应该验证哪些主流程，后续是否需要形成独立的人类操作测试技能？

我推荐 B：

B. Route 07 spec 先定义人类 UI 验证路径，实施后如果页面稳定再沉淀为技能。

Route 07 人工 UI 验证至少覆盖这些路径：
- 巡检路线：创建/编辑路线、配置点位顺序、必检/选检、跳过规则、证据要求，发布后确认运行时可用。
- 巡检计划：绑定路线、目标范围、频率/日历、默认责任人、生成窗口、工作流/通知策略，执行生成预览。
- 点检计划：选择设备/部件/维护对象、配置点检项、阈值/判定规则、证据要求，执行生成预览。
- 计划生成：从已发布计划生成 WorkOrders，检查生成批次、幂等键、失败重试、来源版本。
- 一次性发起：从计划、异常或人工入口临时发起一次巡检/点检，确认不修改已发布计划。
- 执行快照：进入 WorkOrders 执行页，确认路线点位、检查项、标准版本、证据要求、目标快照是生成时冻结的。
- 异常发现：从执行证据创建/合并异常，确认 Product Abnormal 拥有异常生命周期。
- 调整提案/直接修订：触发低风险直接修订或高风险提案/审批，确认版本化、审计和运行时效果。

不建议现在就强制创建 mmis-inspection-ui-verification skill，因为页面和 authoring surface 还没实现，容易写成空泛规则。更稳的是：Route 07 spec 先列预期人类 UI 验证路径，Route 07 实施完成且页面稳定后，如果这些流程可复用，再创建或更新对应的 UI 验证 skill。

不推荐 A：只写后端验证，不写人类 UI 路径。巡检/点检大量是现场操作，人工验证路径必须提前定边界。

不推荐 C：现在立即创建完整 UI 技能。当前还处于设计期，没有稳定页面和真实路径，技能会过早固化。

你是否认可按 B 写入 Route 07 设计？
""";

        await harness.Orchestrator.QueueCompletedReplyAsync(new FeishuCompletedReplyDocumentRequest
        {
            ChatId = "oc-structured-order-chat",
            SessionId = "session-structured-order",
            CliThreadId = "thread-structured-order",
            OriginalUserQuestion = "人工 UI 验证应该验证哪些主流程，后续是否需要形成独立的人类操作测试技能？",
            Username = "luhaiyan",
            Output = assistantBody
        });

        await WaitUntilAsync(() => harness.CardKit.TextMessages.Count == 1);

        Assert.Empty(harness.CardKit.AppendedBlockBatches);
        var appendedText = Assert.Single(harness.CardKit.AppendedTexts).Text;
        Assert.StartsWith("## 用户内容\n\n人工 UI 验证应该验证哪些主流程，后续是否需要形成独立的人类操作测试技能？\n\n---\n\n我会把刚才的 AI-first 操作面确认纳入 Route 07 设计记录", appendedText, StringComparison.Ordinal);
        Assert.Contains("- 巡检计划：绑定路线、目标范围、频率/日历、默认责任人、生成窗口、工作流/通知策略，执行生成预览。", appendedText, StringComparison.Ordinal);
        Assert.Contains("- 计划生成：从已发布计划生成 WorkOrders，检查生成批次、幂等键、失败重试、来源版本。", appendedText, StringComparison.Ordinal);
        Assert.Contains("你是否认可按 B 写入 Route 07 设计？", appendedText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueueCompletedReplyAsync_WhenReferencedMarkdownImportEnabled_ImportsMarkdownFileAndSendsLink()
    {
        var workspaceRoot = CreateWorkspaceWithFile("docs/agent-notes/2026-06-09.md", "# note");

        try
        {
            using var harness = new ReplyDocumentOrchestratorHarness(
                new UserFeishuBotConfigEntity
                {
                    Username = "luhaiyan",
                    ReferencedMarkdownDocImportEnabled = true
                },
                session: new ReplyDocumentSessionContext
                {
                    SessionId = "session-md-import",
                    Username = "luhaiyan",
                    WorkspacePath = workspaceRoot,
                    Title = "markdown import"
                });

            await harness.Orchestrator.QueueCompletedReplyAsync(new FeishuCompletedReplyDocumentRequest
            {
                ChatId = "oc-md-import-chat",
                SessionId = "session-md-import",
                Username = "luhaiyan",
                Output = "请看 docs/agent-notes/2026-06-09.md"
            });

            await WaitUntilAsync(() => harness.CardKit.TextMessages.Count == 1);

            Assert.Equal(["markdown import"], harness.CardKit.EnsuredFolderNames);
            var imported = Assert.Single(harness.CardKit.ImportedMarkdownDocuments);
            Assert.Equal("2026-06-09.md", imported.FileName);
            Assert.Equal("docs/agent-notes/2026-06-09.md", imported.Title);
            Assert.Equal("folder-1", imported.FolderToken);
            Assert.Contains("已生成Markdown在线文档：[docs/agent-notes/2026-06-09.md](", harness.CardKit.TextMessages[0], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task QueueCompletedReplyAsync_WhenReplyMentionsBarePlanMarkdownFileName_ImportsMarkdownFileAndSendsLink()
    {
        var workspaceRoot = CreateWorkspaceWithFile(
            "docs/superpowers/plans/2026-06-11-mmis-ai-first-operation-wave-2-implementation-plan.md",
            "# plan");

        try
        {
            using var harness = new ReplyDocumentOrchestratorHarness(
                new UserFeishuBotConfigEntity
                {
                    Username = "luhaiyan",
                    ReferencedMarkdownDocImportEnabled = true
                },
                session: new ReplyDocumentSessionContext
                {
                    SessionId = "session-md-bare-plan-import",
                    Username = "luhaiyan",
                    WorkspacePath = workspaceRoot,
                    Title = "markdown import"
                });

            await harness.Orchestrator.QueueCompletedReplyAsync(new FeishuCompletedReplyDocumentRequest
            {
                ChatId = "oc-md-bare-plan-import-chat",
                SessionId = "session-md-bare-plan-import",
                Username = "luhaiyan",
                Output = "这份 2026-06-11-mmis-ai-first-operation-wave-2-implementation-plan.md 就是接下来要执行的 plan。"
            });

            await WaitUntilAsync(() => harness.CardKit.TextMessages.Count == 1);

            var imported = Assert.Single(harness.CardKit.ImportedMarkdownDocuments);
            Assert.Equal(
                "2026-06-11-mmis-ai-first-operation-wave-2-implementation-plan.md",
                imported.FileName);
            Assert.Equal(
                "docs/superpowers/plans/2026-06-11-mmis-ai-first-operation-wave-2-implementation-plan.md",
                imported.Title);
            Assert.Contains(
                "docs/superpowers/plans/2026-06-11-mmis-ai-first-operation-wave-2-implementation-plan.md",
                harness.CardKit.TextMessages[0],
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task QueueCompletedReplyAsync_WhenFinalOnlyReplyMentionsBareSpecMarkdownFileName_ImportsMarkdownFileAndSendsLink()
    {
        var workspaceRoot = CreateWorkspaceWithFile(
            "docs/superpowers/specs/2026-06-11-mmis-product-v1-total-delivery-roadmap-design.md",
            "# spec");

        try
        {
            using var harness = new ReplyDocumentOrchestratorHarness(
                new UserFeishuBotConfigEntity
                {
                    Username = "luhaiyan",
                    ReferencedMarkdownDocImportEnabled = true
                },
                session: new ReplyDocumentSessionContext
                {
                    SessionId = "session-md-final-spec-import",
                    Username = "luhaiyan",
                    WorkspacePath = workspaceRoot,
                    Title = "markdown import"
                });

            await harness.Orchestrator.QueueCompletedReplyAsync(new FeishuCompletedReplyDocumentRequest
            {
                ChatId = "oc-md-final-spec-import-chat",
                SessionId = "session-md-final-spec-import",
                Username = "luhaiyan",
                Output = "提交已完成，开始做收尾验证。",
                FinalAnswerOutput = "已创建并提交文档：2026-06-11-mmis-product-v1-total-delivery-roadmap-design.md"
            });

            await WaitUntilAsync(() => harness.CardKit.TextMessages.Count == 1);

            var imported = Assert.Single(harness.CardKit.ImportedMarkdownDocuments);
            Assert.Equal(
                "2026-06-11-mmis-product-v1-total-delivery-roadmap-design.md",
                imported.FileName);
            Assert.Equal(
                "docs/superpowers/specs/2026-06-11-mmis-product-v1-total-delivery-roadmap-design.md",
                imported.Title);
            Assert.Contains(
                "docs/superpowers/specs/2026-06-11-mmis-product-v1-total-delivery-roadmap-design.md",
                harness.CardKit.TextMessages[0],
                StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task QueueCompletedReplyAsync_WhenMarkdownImportFails_StillCreatesNormalReplyDocument()
    {
        var workspaceRoot = CreateWorkspaceWithFile("docs/agent-notes/2026-06-09.md", "# note");

        try
        {
            using var harness = new ReplyDocumentOrchestratorHarness(
                new UserFeishuBotConfigEntity
                {
                    Username = "luhaiyan",
                    FullReplyDocEnabled = true,
                    ReferencedMarkdownDocImportEnabled = true
                },
                session: new ReplyDocumentSessionContext
                {
                    SessionId = "session-md-warning",
                    Username = "luhaiyan",
                    WorkspacePath = workspaceRoot,
                    Title = "markdown warning"
                });

            harness.CardKit.ImportMarkdownException = new HttpRequestException("import failed");

            await harness.Orchestrator.QueueCompletedReplyAsync(new FeishuCompletedReplyDocumentRequest
            {
                ChatId = "oc-md-warning-chat",
                SessionId = "session-md-warning",
                CliThreadId = "thread-md-warning",
                Username = "luhaiyan",
                OriginalUserQuestion = "continue",
                Output = "full reply body\n请看 docs/agent-notes/2026-06-09.md"
            });

            await WaitUntilAsync(() => harness.CardKit.TextMessages.Count == 2);

            Assert.Single(harness.CardKit.CreatedDocuments);
            Assert.Contains("已生成完整回复文档：", harness.CardKit.TextMessages[0], StringComparison.Ordinal);
            Assert.Contains("Markdown在线文档处理失败", harness.CardKit.TextMessages[1], StringComparison.Ordinal);
            Assert.Contains("docs/agent-notes/2026-06-09.md", harness.CardKit.TextMessages[1], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task QueueCompletedReplyAsync_WhenMarkdownImportSucceeds_AppliesImportedDocumentPermissionsAndAdminGrant()
    {
        var workspaceRoot = CreateWorkspaceWithFile("docs/agent-notes/2026-06-09.md", "# note");

        try
        {
            using var harness = new ReplyDocumentOrchestratorHarness(
                new UserFeishuBotConfigEntity
                {
                    Username = "luhaiyan",
                    ReferencedMarkdownDocImportEnabled = true,
                    DocumentAdminOpenId = "ou_doc_admin"
                },
                session: new ReplyDocumentSessionContext
                {
                    SessionId = "session-md-admin",
                    Username = "luhaiyan",
                    WorkspacePath = workspaceRoot,
                    Title = "markdown admin"
                });

            await harness.Orchestrator.QueueCompletedReplyAsync(new FeishuCompletedReplyDocumentRequest
            {
                ChatId = "oc-md-admin-chat",
                SessionId = "session-md-admin",
                Username = "luhaiyan",
                Output = "请看 docs/agent-notes/2026-06-09.md"
            });

            await WaitUntilAsync(() => harness.CardKit.TextMessages.Count == 1);

            Assert.Equal(["imported-doc-1"], harness.CardKit.PermissionUpdates);
            Assert.Equal([("imported-doc-1", "ou_doc_admin")], harness.CardKit.DocumentAdminGrants);
            Assert.Equal([("folder-1", "ou_doc_admin")], harness.CardKit.FolderAdminGrants);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task QueueCompletedReplyAsync_WhenMarkdownImportAdminGrantFails_SendsWarningWithoutBlockingLink()
    {
        var workspaceRoot = CreateWorkspaceWithFile("docs/agent-notes/2026-06-09.md", "# note");

        try
        {
            using var harness = new ReplyDocumentOrchestratorHarness(
                new UserFeishuBotConfigEntity
                {
                    Username = "luhaiyan",
                    ReferencedMarkdownDocImportEnabled = true,
                    DocumentAdminOpenId = "ou_doc_admin"
                },
                session: new ReplyDocumentSessionContext
                {
                    SessionId = "session-md-admin-warning",
                    Username = "luhaiyan",
                    WorkspacePath = workspaceRoot,
                    Title = "markdown admin warning"
                });

            harness.CardKit.GrantDocumentAdminException = new InvalidOperationException("grant failed");

            await harness.Orchestrator.QueueCompletedReplyAsync(new FeishuCompletedReplyDocumentRequest
            {
                ChatId = "oc-md-admin-warning-chat",
                SessionId = "session-md-admin-warning",
                Username = "luhaiyan",
                Output = "请看 docs/agent-notes/2026-06-09.md"
            });

            await WaitUntilAsync(() => harness.CardKit.TextMessages.Count == 2);

            Assert.Contains("已生成Markdown在线文档", harness.CardKit.TextMessages[0], StringComparison.Ordinal);
            Assert.Contains("文档管理员权限授予失败", harness.CardKit.TextMessages[1], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    [Fact]
    public async Task QueueCompletedReplyAsync_WhenMarkdownImportFolderAdminGrantFails_SendsLinkThenFolderWarning()
    {
        var workspaceRoot = CreateWorkspaceWithFile("docs/agent-notes/2026-06-09.md", "# note");

        try
        {
            using var harness = new ReplyDocumentOrchestratorHarness(
                new UserFeishuBotConfigEntity
                {
                    Username = "luhaiyan",
                    ReferencedMarkdownDocImportEnabled = true,
                    DocumentAdminOpenId = "ou_doc_admin"
                },
                session: new ReplyDocumentSessionContext
                {
                    SessionId = "session-md-folder-admin-warning",
                    Username = "luhaiyan",
                    WorkspacePath = workspaceRoot,
                    Title = "markdown folder admin warning"
                });

            harness.CardKit.GrantFolderAdminException = new HttpRequestException(
                "API request failed: Status=BadRequest, Content={\"code\":99991672,\"msg\":\"Access denied. One of the following scopes is required: [drive:drive].应用尚未开通所需的应用身份权限：[drive:drive]，点击链接申请并开通任一权限即可：https://open.feishu.cn/app/test/auth?q=drive:drive\"}");

            await harness.Orchestrator.QueueCompletedReplyAsync(new FeishuCompletedReplyDocumentRequest
            {
                ChatId = "oc-md-folder-admin-warning-chat",
                SessionId = "session-md-folder-admin-warning",
                Username = "luhaiyan",
                Output = "请看 docs/agent-notes/2026-06-09.md"
            });

            await WaitUntilAsync(() => harness.CardKit.TextMessages.Count == 2);

            Assert.Contains("已生成Markdown在线文档", harness.CardKit.TextMessages[0], StringComparison.Ordinal);
            Assert.Contains("会话文档文件夹管理员权限授予失败", harness.CardKit.TextMessages[1], StringComparison.Ordinal);
            Assert.Contains("drive:drive", harness.CardKit.TextMessages[1], StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspaceRoot, recursive: true);
        }
    }

    private static JsonElement ParseJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string CreateWorkspaceWithFile(string relativePath, string content)
    {
        var root = Path.Combine(Path.GetTempPath(), "webcode-orchestrator-md-" + Guid.NewGuid().ToString("N"));
        var absolutePath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        File.WriteAllText(absolutePath, content, Encoding.UTF8);
        return root;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        Assert.True(condition(), "Timed out waiting for the expected condition.");
    }

    private sealed class ReplyDocumentOrchestratorHarness : IDisposable
    {
        private readonly ServiceProvider _serviceProvider;

        public ReplyDocumentOrchestratorHarness(
            UserFeishuBotConfigEntity config,
            ReplyDocumentSessionContext? session = null)
        {
            ConfigService = new TrackingUserFeishuBotConfigService(config);
            CardKit = new TrackingFeishuCardKitClient();
            ChatSessionRepository = new TrackingChatSessionRepository(session);
            HistoryService = new TrackingExternalCliSessionHistoryService();

            var services = new ServiceCollection();
            services.AddScoped<IUserFeishuBotConfigService>(_ => ConfigService);
            services.AddScoped<IFeishuCardKitClient>(_ => CardKit);
            services.AddScoped<IChatSessionRepository>(_ => ChatSessionRepository);
            services.AddScoped<IExternalCliSessionHistoryService>(_ => HistoryService);
            services.AddSingleton<IReferencedMarkdownImportStateStore, InMemoryReferencedMarkdownImportStateStore>();
            services.AddLogging();

            _serviceProvider = services.BuildServiceProvider();
            Orchestrator = new ReplyDocumentOrchestrator(
                _serviceProvider,
                NullLogger<ReplyDocumentOrchestrator>.Instance);
        }

        public TrackingUserFeishuBotConfigService ConfigService { get; }
        public TrackingFeishuCardKitClient CardKit { get; }
        public TrackingChatSessionRepository ChatSessionRepository { get; }
        public TrackingExternalCliSessionHistoryService HistoryService { get; }
        public ReplyDocumentOrchestrator Orchestrator { get; }

        public void Dispose()
        {
            _serviceProvider.Dispose();
        }
    }

    private sealed class TrackingUserFeishuBotConfigService(UserFeishuBotConfigEntity config) : IUserFeishuBotConfigService
    {
        public Task<UserFeishuBotConfigEntity?> GetByUsernameAsync(string username)
            => Task.FromResult<UserFeishuBotConfigEntity?>(string.Equals(username, config.Username, StringComparison.OrdinalIgnoreCase) ? config : null);

        public Task<UserFeishuBotConfigEntity?> GetByAppIdAsync(string appId) => Task.FromResult<UserFeishuBotConfigEntity?>(null);
        public Task<UserFeishuBotConfigSaveResult> SaveAsync(UserFeishuBotConfigEntity configEntity) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string username) => Task.FromResult(true);
        public Task<string?> FindConflictingUsernameByAppIdAsync(string username, string? appId) => Task.FromResult<string?>(null);
        public Task<List<UserFeishuBotConfigEntity>> GetAutoStartCandidatesAsync() => Task.FromResult(new List<UserFeishuBotConfigEntity>());
        public Task<bool> UpdateRuntimePreferenceAsync(string username, bool autoStartEnabled, DateTime? lastStartedAt = null) => Task.FromResult(true);
        public FeishuOptions GetSharedDefaults() => new() { Enabled = true, AppId = "shared-app-id", AppSecret = "shared-secret" };
        public Task<FeishuOptions> GetEffectiveOptionsAsync(string? username) => Task.FromResult(GetSharedDefaults());
        public Task<FeishuOptions?> GetEffectiveOptionsByAppIdAsync(string? appId) => Task.FromResult<FeishuOptions?>(null);
    }

    private sealed class TrackingChatSessionRepository(ReplyDocumentSessionContext? session) : IChatSessionRepository
    {
        private readonly Dictionary<string, ChatSessionEntity> _sessions = session == null
            ? []
            : new Dictionary<string, ChatSessionEntity>(StringComparer.OrdinalIgnoreCase)
            {
                [session.SessionId] = new ChatSessionEntity
                {
                    SessionId = session.SessionId,
                    Username = session.Username,
                    Title = session.Title,
                    ToolId = session.ToolId,
                    CliThreadId = session.CliThreadId,
                    WorkspacePath = session.WorkspacePath,
                    CcSwitchSnapshotToolId = session.SnapshotToolId
                }
            };

        public SqlSugarScope GetDB() => throw new NotSupportedException();
        public List<ChatSessionEntity> GetList() => _sessions.Values.ToList();
        public Task<List<ChatSessionEntity>> GetListAsync() => Task.FromResult(GetList());
        public List<ChatSessionEntity> GetList(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => _sessions.Values.AsQueryable().Where(whereExpression).ToList();
        public Task<List<ChatSessionEntity>> GetListAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => Task.FromResult(GetList(whereExpression));
        public int Count(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => _sessions.Values.AsQueryable().Count(whereExpression);
        public Task<int> CountAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => Task.FromResult(Count(whereExpression));
        public PageList<ChatSessionEntity> GetPageList(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public PageList<P> GetPageList<P>(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public Task<PageList<ChatSessionEntity>> GetPageListAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public Task<PageList<P>> GetPageListAsync<P>(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public PageList<ChatSessionEntity> GetPageList(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, System.Linq.Expressions.Expression<Func<ChatSessionEntity, object>> orderByExpression = null, SqlSugar.OrderByType orderByType = SqlSugar.OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<ChatSessionEntity>> GetPageListAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, System.Linq.Expressions.Expression<Func<ChatSessionEntity, object>> orderByExpression = null, SqlSugar.OrderByType orderByType = SqlSugar.OrderByType.Asc) => throw new NotSupportedException();
        public PageList<P> GetPageList<P>(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, System.Linq.Expressions.Expression<Func<ChatSessionEntity, object>> orderByExpression = null, SqlSugar.OrderByType orderByType = SqlSugar.OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<P>> GetPageListAsync<P>(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression, PageModel page, System.Linq.Expressions.Expression<Func<ChatSessionEntity, object>> orderByExpression = null, SqlSugar.OrderByType orderByType = SqlSugar.OrderByType.Asc) => throw new NotSupportedException();
        public PageList<ChatSessionEntity> GetPageList(List<SqlSugar.IConditionalModel> conditionalList, PageModel page) => throw new NotSupportedException();
        public Task<PageList<ChatSessionEntity>> GetPageListAsync(List<SqlSugar.IConditionalModel> conditionalList, PageModel page) => throw new NotSupportedException();
        public PageList<ChatSessionEntity> GetPageList(List<SqlSugar.IConditionalModel> conditionalList, PageModel page, System.Linq.Expressions.Expression<Func<ChatSessionEntity, object>> orderByExpression = null, SqlSugar.OrderByType orderByType = SqlSugar.OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<ChatSessionEntity>> GetPageListAsync(List<SqlSugar.IConditionalModel> conditionalList, PageModel page, System.Linq.Expressions.Expression<Func<ChatSessionEntity, object>> orderByExpression = null, SqlSugar.OrderByType orderByType = SqlSugar.OrderByType.Asc) => throw new NotSupportedException();
        public ChatSessionEntity GetById(dynamic id) => _sessions[(id?.ToString() ?? string.Empty)];
        public Task<ChatSessionEntity> GetByIdAsync(dynamic id)
        {
            ChatSessionEntity? found = _sessions.TryGetValue(id?.ToString() ?? string.Empty, out ChatSessionEntity? stored) ? stored : null;
            return Task.FromResult(found)!;
        }

        public ChatSessionEntity GetSingle(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => _sessions.Values.AsQueryable().Single(whereExpression);
        public Task<ChatSessionEntity> GetSingleAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => Task.FromResult(GetSingle(whereExpression));
        public ChatSessionEntity GetFirst(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => _sessions.Values.AsQueryable().First(whereExpression);
        public Task<ChatSessionEntity> GetFirstAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => Task.FromResult(GetFirst(whereExpression));
        public bool Insert(ChatSessionEntity obj) => throw new NotSupportedException();
        public Task<bool> InsertAsync(ChatSessionEntity obj) => throw new NotSupportedException();
        public bool InsertRange(List<ChatSessionEntity> objs) => throw new NotSupportedException();
        public Task<bool> InsertRangeAsync(List<ChatSessionEntity> objs) => throw new NotSupportedException();
        public int InsertReturnIdentity(ChatSessionEntity obj) => throw new NotSupportedException();
        public Task<int> InsertReturnIdentityAsync(ChatSessionEntity obj) => throw new NotSupportedException();
        public long InsertReturnBigIdentity(ChatSessionEntity obj) => throw new NotSupportedException();
        public Task<long> InsertReturnBigIdentityAsync(ChatSessionEntity obj) => throw new NotSupportedException();
        public bool DeleteByIds(dynamic[] ids) => throw new NotSupportedException();
        public Task<bool> DeleteByIdsAsync(dynamic[] ids) => throw new NotSupportedException();
        public bool Delete(dynamic id) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(dynamic id) => throw new NotSupportedException();
        public bool Delete(ChatSessionEntity obj) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(ChatSessionEntity obj) => throw new NotSupportedException();
        public bool Delete(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => throw new NotSupportedException();
        public bool Update(ChatSessionEntity obj) => throw new NotSupportedException();
        public Task<bool> UpdateAsync(ChatSessionEntity obj) => throw new NotSupportedException();
        public bool UpdateRange(List<ChatSessionEntity> objs) => throw new NotSupportedException();
        public bool InsertOrUpdate(ChatSessionEntity obj) => throw new NotSupportedException();
        public Task<bool> InsertOrUpdateAsync(ChatSessionEntity obj) => throw new NotSupportedException();
        public Task<bool> UpdateRangeAsync(List<ChatSessionEntity> objs) => throw new NotSupportedException();
        public bool IsAny(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => _sessions.Values.AsQueryable().Any(whereExpression);
        public Task<bool> IsAnyAsync(System.Linq.Expressions.Expression<Func<ChatSessionEntity, bool>> whereExpression) => Task.FromResult(IsAny(whereExpression));
        public Task<List<ChatSessionEntity>> GetByUsernameAsync(string username) => Task.FromResult(_sessions.Values.Where(x => string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)).ToList());
        public Task<ChatSessionEntity?> GetByIdAndUsernameAsync(string sessionId, string username) => Task.FromResult(_sessions.Values.FirstOrDefault(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase)));
        public Task<bool> DeleteByIdAndUsernameAsync(string sessionId, string username) => throw new NotSupportedException();
        public Task<List<ChatSessionEntity>> GetByUsernameOrderByUpdatedAtAsync(string username) => throw new NotSupportedException();
        public Task<ChatSessionEntity?> GetByUsernameToolAndCliThreadIdAsync(string username, string toolId, string cliThreadId) => throw new NotSupportedException();
        public Task<ChatSessionEntity?> GetByToolAndCliThreadIdAsync(string toolId, string cliThreadId) => throw new NotSupportedException();
        public Task<bool> UpdateCliThreadIdAsync(string sessionId, string? cliThreadId) => throw new NotSupportedException();
        public Task<bool> UpdateWorkspaceBindingAsync(string sessionId, string? workspacePath, bool isCustomWorkspace) => throw new NotSupportedException();
        public Task<bool> UpdateSessionTitleAsync(string sessionId, string title) => throw new NotSupportedException();
        public Task<bool> UpdateCcSwitchSnapshotAsync(string sessionId, CcSwitchSessionSnapshot snapshot) => throw new NotSupportedException();
        public Task<List<ChatSessionEntity>> GetByFeishuChatKeyAsync(string feishuChatKey) => throw new NotSupportedException();
        public Task<ChatSessionEntity?> GetActiveByFeishuChatKeyAsync(string feishuChatKey) => throw new NotSupportedException();
        public Task<bool> SetActiveSessionAsync(string feishuChatKey, string sessionId) => throw new NotSupportedException();
        public Task<bool> CloseFeishuSessionAsync(string feishuChatKey, string sessionId) => throw new NotSupportedException();
        public Task<string> CreateFeishuSessionAsync(string feishuChatKey, string username, string? workspacePath = null, string? toolId = null) => throw new NotSupportedException();
    }

    private sealed class TrackingExternalCliSessionHistoryService : IExternalCliSessionHistoryService
    {
        public Task<ExternalCliHistoryResult> GetRecentHistoryAsync(string toolId, string cliThreadId, int maxCount = 20, string? workspacePath = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<List<ExternalCliHistoryMessage>> GetRecentMessagesAsync(string toolId, string cliThreadId, int maxCount = 20, string? workspacePath = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string?> GetCodexFinalAnswerTextAsync(string cliThreadId, string? workspacePath = null, CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }

    private sealed class TrackingFeishuCardKitClient : IFeishuCardKitClient
    {
        public Exception? ImportMarkdownException { get; set; }
        public Exception? GrantDocumentAdminException { get; set; }
        public Exception? GrantFolderAdminException { get; set; }

        public List<JsonElement> ConvertedBlocks { get; } = [];
        public List<(string Title, string DocumentId, string RootBlockId, string Url, string? FolderToken)> CreatedDocuments { get; } = [];
        public List<(string DocumentId, string BlockId, string Text)> AppendedTexts { get; } = [];
        public List<(string DocumentId, string BlockId, IReadOnlyList<JsonElement> Blocks)> AppendedBlockBatches { get; } = [];
        public Dictionary<(string DocumentId, string BlockId), List<string>> ChildBlockIdsByDocumentAndBlock { get; } = [];
        public List<string> PermissionUpdates { get; } = [];
        public List<string> TextMessages { get; } = [];
        public List<string> EnsuredFolderNames { get; } = [];
        public Dictionary<string, FeishuCloudDocumentInfo> ExistingFolderDocumentsByTitle { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string FileName, string Title, string FolderToken, string Body)> ImportedMarkdownDocuments { get; } = [];
        public List<(string DocumentId, string BlockId, int StartIndex, int EndIndex)> DeletedChildRanges { get; } = [];
        public List<(string DocumentId, string OpenId)> DocumentAdminGrants { get; } = [];
        public List<(string FolderToken, string OpenId)> FolderAdminGrants { get; } = [];

        public Task<FeishuCloudDocumentInfo> CreateCloudDocumentAsync(string title, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null, string? folderToken = null)
        {
            CreatedDocuments.Add((title, $"doc-{CreatedDocuments.Count + 1}", $"root-{CreatedDocuments.Count + 1}", $"https://feishu.cn/docx/doc-{CreatedDocuments.Count + 1}", folderToken));
            var created = CreatedDocuments[^1];
            return Task.FromResult(new FeishuCloudDocumentInfo
            {
                DocumentId = created.DocumentId,
                RootBlockId = created.RootBlockId,
                Url = created.Url
            });
        }

        public Task<JsonElement> ConvertMarkdownToCloudDocumentBlocksAsync(string markdown, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            var blocksJson = string.Join(",", ConvertedBlocks.Select(static block => block.GetRawText()));
            using var document = JsonDocument.Parse($$"""{"blocks":[{{blocksJson}}]}""");
            return Task.FromResult(document.RootElement.Clone());
        }

        public Task AppendCloudDocumentBlocksAsync(string documentId, string blockId, IReadOnlyCollection<JsonElement> blocks, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            AppendedBlockBatches.Add((documentId, blockId, blocks.Select(static block => block.Clone()).ToArray()));
            return Task.CompletedTask;
        }

        public Task AppendCloudDocumentTextAsync(string documentId, string blockId, string text, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            AppendedTexts.Add((documentId, blockId, text));
            return Task.CompletedTask;
        }

        public Task SetCloudDocumentTenantReadableAsync(string documentId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            PermissionUpdates.Add(documentId);
            return Task.CompletedTask;
        }

        public Task<string> EnsureCloudFolderAsync(string folderName, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            EnsuredFolderNames.Add(folderName);
            return Task.FromResult($"folder-{EnsuredFolderNames.Count}");
        }

        public Task<FeishuCloudDocumentInfo?> FindCloudDocumentInFolderByTitleAsync(string folderToken, string title, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            return Task.FromResult(ExistingFolderDocumentsByTitle.TryGetValue(title, out var existing) ? existing : null);
        }

        public Task<IReadOnlyList<string>> ListCloudDocumentChildBlockIdsAsync(string documentId, string blockId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            return Task.FromResult<IReadOnlyList<string>>(
                ChildBlockIdsByDocumentAndBlock.TryGetValue((documentId, blockId), out var blockIds)
                    ? [.. blockIds]
                    : []);
        }

        public Task DeleteCloudDocumentChildBlocksAsync(string documentId, string blockId, int startIndex, int endIndex, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            DeletedChildRanges.Add((documentId, blockId, startIndex, endIndex));
            return Task.CompletedTask;
        }

        public Task<FeishuCloudDocumentInfo> ImportMarkdownFileAsCloudDocumentAsync(string fileName, byte[] content, string title, string? folderToken, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            if (ImportMarkdownException != null)
            {
                throw ImportMarkdownException;
            }

            ImportedMarkdownDocuments.Add((fileName, title, folderToken ?? string.Empty, Encoding.UTF8.GetString(content)));
            var number = ImportedMarkdownDocuments.Count;
            return Task.FromResult(new FeishuCloudDocumentInfo
            {
                DocumentId = $"imported-doc-{number}",
                RootBlockId = $"imported-doc-{number}",
                Url = $"https://feishu.cn/docx/imported-doc-{number}"
            });
        }

        public Task<string> SendTextMessageAsync(string chatId, string content, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            TextMessages.Add(content);
            return Task.FromResult($"om_{TextMessages.Count}");
        }

        public Task GrantCloudDocumentMemberFullAccessAsync(string documentId, string openId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            if (GrantDocumentAdminException != null)
            {
                throw GrantDocumentAdminException;
            }

            DocumentAdminGrants.Add((documentId, openId));
            return Task.CompletedTask;
        }

        public Task GrantCloudFolderMemberFullAccessAsync(string folderToken, string openId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null)
        {
            if (GrantFolderAdminException != null)
            {
                throw GrantFolderAdminException;
            }

            FolderAdminGrants.Add((folderToken, openId));
            return Task.CompletedTask;
        }

        public Task<string> CreateCardAsync(string initialContent, string? title = null, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<FeishuStreamingHandle> CreateStreamingHandleAsync(string chatId, string? replyMessageId, string initialContent, string? title = null, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null, FeishuStreamingCardChrome? chrome = null) => throw new NotSupportedException();
        public Task<FeishuDownloadedAttachment> DownloadIncomingAttachmentAsync(FeishuIncomingAttachment attachment, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<(byte[] Content, string FileName, string MimeType)> DownloadMessageResourceAsync(string messageId, string fileKey, string resourceType, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task MoveCloudDocumentToFolderAsync(string documentId, string folderToken, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> ReplyCardMessageAsync(string replyMessageId, string cardId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> ReplyElementsCardAsync(string replyMessageId, FeishuNetSdk.Im.Dtos.ElementsCardV2Dto card, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> ReplyRawCardAsync(string replyMessageId, string cardJson, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> ReplyTextMessageAsync(string replyMessageId, string content, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> SendCardMessageAsync(string chatId, string cardId, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> SendRawCardAsync(string chatId, string cardJson, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<string> UploadCloudFileAsync(string fileName, byte[] content, string? folderToken, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
        public Task<bool> UpdateCardAsync(string cardId, string content, int sequence, CancellationToken cancellationToken = default, FeishuOptions? optionsOverride = null) => throw new NotSupportedException();
    }

    private sealed class InMemoryReferencedMarkdownImportStateStore : IReferencedMarkdownImportStateStore
    {
        private readonly Dictionary<(string FolderToken, string AbsolutePath), ReferencedMarkdownImportStateEntry> _entries = new();

        public Task<ReferencedMarkdownImportStateEntry?> GetAsync(string folderToken, string absolutePath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(
                _entries.TryGetValue((folderToken, absolutePath), out var entry)
                    ? entry
                    : null);
        }

        public Task UpsertAsync(ReferencedMarkdownImportStateEntry entry, CancellationToken cancellationToken = default)
        {
            _entries[(entry.FolderToken, entry.AbsolutePath)] = entry;
            return Task.CompletedTask;
        }
    }

    private sealed class ReplyDocumentSessionContext
    {
        public string SessionId { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string ToolId { get; set; } = string.Empty;
        public string? CliThreadId { get; set; }
        public string? Title { get; set; }
        public string? WorkspacePath { get; set; }
        public string? SnapshotToolId { get; set; }
    }
}
