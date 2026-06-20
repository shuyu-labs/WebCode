using System.Net;
using System.Text.Json;
using FeishuNetSdk.Im.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service.Channels;

namespace WebCodeCli.Domain.Tests;

public class FeishuCardKitClientTests
{
    [Fact]
    public async Task CreateCloudDocumentAsync_PostsDocxCreateRequestAndReturnsDocumentInfo()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"document":{"document_id":"doccn123","root_block_id":"root123"}}}""")
        ]);

        var client = CreateClient(handler);

        var document = await client.CreateCloudDocumentAsync("thread-1 缁х画 - 瀹屾暣鍥炲", TestContext.Current.CancellationToken);

        Assert.Equal("doccn123", document.DocumentId);
        Assert.Equal("root123", document.RootBlockId);
        Assert.Equal("https://feishu.cn/docx/doccn123", document.Url);
        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/docx/v1/documents"
        ], handler.RequestPaths);

        using var requestDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("thread-1 缁х画 - 瀹屾暣鍥炲", requestDoc.RootElement.GetProperty("title").GetString());
        Assert.False(requestDoc.RootElement.TryGetProperty("folder_token", out _));
    }

    [Fact]
    public async Task CreateCloudDocumentAsync_WhenFolderTokenProvided_PostsFolderTokenInDocxCreateRequest()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"document":{"document_id":"doccn123","root_block_id":"root123"}}}""")
        ]);

        var client = CreateClient(handler);

        var document = await client.CreateCloudDocumentAsync(
            "thread-1 缁х画 - 瀹屾暣鍥炲",
            TestContext.Current.CancellationToken,
            folderToken: "fld-target");

        Assert.Equal("doccn123", document.DocumentId);
        Assert.Equal("root123", document.RootBlockId);

        using var requestDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("thread-1 缁х画 - 瀹屾暣鍥炲", requestDoc.RootElement.GetProperty("title").GetString());
        Assert.Equal("fld-target", requestDoc.RootElement.GetProperty("folder_token").GetString());
    }

    [Fact]
    public async Task AppendCloudDocumentTextAsync_PostsTextChildrenRequest()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"children":[{"block_id":"blk1"}]}}""")
        ]);

        var client = CreateClient(handler);

        await client.AppendCloudDocumentTextAsync(
            "doccn123",
            "root123",
            "缁撹姝ｆ枃",
            TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/docx/v1/documents/doccn123/blocks/root123/children"
        ], handler.RequestPaths);

        using var requestDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        var children = requestDoc.RootElement.GetProperty("children");
        Assert.Equal(1, children.GetArrayLength());
        Assert.Equal(2, children[0].GetProperty("block_type").GetInt32());
        Assert.Equal("缁撹姝ｆ枃", children[0].GetProperty("text").GetProperty("elements")[0].GetProperty("text_run").GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Object, children[0].GetProperty("text").GetProperty("elements")[0].GetProperty("text_run").GetProperty("text_element_style").ValueKind);
    }

    [Fact]
    public async Task SetCloudDocumentTenantReadableAsync_PatchesDrivePermission()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{}}""")
        ]);

        var client = CreateClient(handler);

        await client.SetCloudDocumentTenantReadableAsync("doccn123", TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/drive/v2/permissions/doccn123/public"
        ], handler.RequestPaths);
        Assert.Equal("type=docx", handler.RequestQueries[1]);
        Assert.Equal("PATCH", handler.RequestMethods[1]);

        using var requestDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("open", requestDoc.RootElement.GetProperty("external_access_entity").GetString());
        Assert.Equal("anyone_can_view", requestDoc.RootElement.GetProperty("security_entity").GetString());
    }

    [Fact]
    public async Task GrantCloudDocumentMemberFullAccessAsync_PostsPermissionMemberRequest()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"member_id":"ou_doc_admin"}}""")
        ]);

        dynamic client = CreateClient(handler);

        await client.GrantCloudDocumentMemberFullAccessAsync(
            "doccn123",
            "ou_doc_admin",
            TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/drive/v1/permissions/doccn123/members"
        ], handler.RequestPaths);
        Assert.Equal("type=docx", handler.RequestQueries[1]);
        Assert.Equal("POST", handler.RequestMethods[1]);

        using var requestDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("ou_doc_admin", requestDoc.RootElement.GetProperty("member_id").GetString());
        Assert.Equal("openid", requestDoc.RootElement.GetProperty("member_type").GetString());
        Assert.Equal("full_access", requestDoc.RootElement.GetProperty("perm").GetString());
        Assert.Equal("container", requestDoc.RootElement.GetProperty("perm_type").GetString());
        Assert.Equal("user", requestDoc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task GrantCloudFolderMemberFullAccessAsync_PostsPermissionMemberRequest()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"member_id":"ou_doc_admin"}}""")
        ]);

        dynamic client = CreateClient(handler);

        await client.GrantCloudFolderMemberFullAccessAsync(
            "fld_123",
            "ou_doc_admin",
            TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/drive/v1/permissions/fld_123/members"
        ], handler.RequestPaths);
        Assert.Equal("type=folder", handler.RequestQueries[1]);
        Assert.Equal("POST", handler.RequestMethods[1]);

        using var requestDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("ou_doc_admin", requestDoc.RootElement.GetProperty("member_id").GetString());
        Assert.Equal("openid", requestDoc.RootElement.GetProperty("member_type").GetString());
        Assert.Equal("full_access", requestDoc.RootElement.GetProperty("perm").GetString());
        Assert.Equal("container", requestDoc.RootElement.GetProperty("perm_type").GetString());
        Assert.Equal("user", requestDoc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task EnsureCloudFolderAsync_WhenFolderExists_ReturnsExistingFolderToken()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"token":"fld-root"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"files":[{"name":"session-folder","token":"fld-existing","type":"folder"}],"has_more":false}}""")
        ]);

        var client = CreateClient(handler);

        var folderToken = await client.EnsureCloudFolderAsync("session-folder", TestContext.Current.CancellationToken);

        Assert.Equal("fld-existing", folderToken);
        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/drive/explorer/v2/root_folder/meta",
            "/open-apis/drive/v1/files"
        ], handler.RequestPaths);
        Assert.Equal("GET", handler.RequestMethods[1]);
        Assert.Equal("GET", handler.RequestMethods[2]);
        Assert.Contains("folder_token=fld-root", handler.RequestQueries[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnsureCloudFolderAsync_WhenFolderMissing_CreatesFolderUnderRoot()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"token":"fld-root"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"files":[],"has_more":false}}"""),
            CreateJsonResponse("""{"code":0,"data":{"token":"fld-created","url":"https://feishu.cn/drive/folder/fld-created"}}""")
        ]);

        var client = CreateClient(handler);

        var folderToken = await client.EnsureCloudFolderAsync("session-folder", TestContext.Current.CancellationToken);

        Assert.Equal("fld-created", folderToken);
        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/drive/explorer/v2/root_folder/meta",
            "/open-apis/drive/v1/files",
            "/open-apis/drive/v1/files/create_folder"
        ], handler.RequestPaths);

        using var requestDoc = JsonDocument.Parse(handler.RequestBodies[3]);
        Assert.Equal("fld-root", requestDoc.RootElement.GetProperty("folder_token").GetString());
        Assert.Equal("session-folder", requestDoc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task MoveCloudDocumentToFolderAsync_PostsDriveMoveRequest()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"task_id":"7360595374803812356"}}""")
        ]);

        var client = CreateClient(handler);

        await client.MoveCloudDocumentToFolderAsync("doccn123", "fld-target", TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/drive/v1/files/doccn123/move"
        ], handler.RequestPaths);
        Assert.Equal("POST", handler.RequestMethods[1]);

        using var requestDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("fld-target", requestDoc.RootElement.GetProperty("folder_token").GetString());
        Assert.Equal("docx", requestDoc.RootElement.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ConvertMarkdownToCloudDocumentBlocksAsync_PostsMarkdownConvertRequestAndReturnsBlocks()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"first_level_block_ids":["blk-heading"],"blocks":[{"block_id":"blk-heading","block_type":3,"children":[],"heading1":{"elements":[{"text_run":{"content":"标题","text_element_style":{}}}]}}]}}""")
        ]);

        var client = CreateClient(handler);

        var blocksData = await client.ConvertMarkdownToCloudDocumentBlocksAsync(
            "# 标题",
            TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/docx/v1/documents/blocks/convert"
        ], handler.RequestPaths);

        using var requestDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("markdown", requestDoc.RootElement.GetProperty("content_type").GetString());
        Assert.Equal("# 标题", requestDoc.RootElement.GetProperty("content").GetString());

        Assert.Equal("blk-heading", blocksData.GetProperty("first_level_block_ids")[0].GetString());
        Assert.Equal(3, blocksData.GetProperty("blocks")[0].GetProperty("block_type").GetInt32());
        Assert.Equal("标题", blocksData.GetProperty("blocks")[0].GetProperty("heading1").GetProperty("elements")[0].GetProperty("text_run").GetProperty("content").GetString());
    }

    [Fact]
    public async Task AppendCloudDocumentBlocksAsync_PostsProvidedBlocksToChildrenEndpoint()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"children":[{"block_id":"blk-created"}]}}""")
        ]);

        var client = CreateClient(handler);
        var blocks = ParseJsonElementArray("""[{"block_id":"blk-source","block_type":2,"children":[],"text":{"elements":[{"text_run":{"content":"转换后的段落","text_element_style":{}}}]}}]""");

        await client.AppendCloudDocumentBlocksAsync(
            "doccn123",
            "root123",
            blocks,
            TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/docx/v1/documents/doccn123/blocks/root123/children"
        ], handler.RequestPaths);

        using var requestDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        var children = requestDoc.RootElement.GetProperty("children");
        Assert.Equal(1, children.GetArrayLength());
        Assert.False(children[0].TryGetProperty("block_id", out _));
        Assert.Equal(2, children[0].GetProperty("block_type").GetInt32());
        Assert.Equal("转换后的段落", children[0].GetProperty("text").GetProperty("elements")[0].GetProperty("text_run").GetProperty("content").GetString());
        Assert.False(children[0].TryGetProperty("children", out _));
    }

    [Fact]
    public async Task AppendCloudDocumentBlocksAsync_WhenMoreThanFiftyBlocks_BatchesChildrenRequests()
    {
        var responses = new List<HttpResponseMessage>
        {
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}""")
        };
        responses.AddRange(
        [
            CreateJsonResponse("""{"code":0,"data":{"children":[{"block_id":"blk-created-1"}]}}"""),
            CreateJsonResponse("""{"code":0,"data":{"children":[{"block_id":"blk-created-2"}]}}""")
        ]);

        var handler = new StubHttpMessageHandler(responses);
        var client = CreateClient(handler);
        var blocks = Enumerable.Range(1, 51)
            .Select(index => ParseJsonElementArray(
                "[{\"block_id\":\"blk-" + index + "\",\"block_type\":2,\"children\":[],\"text\":{\"elements\":[{\"text_run\":{\"content\":\"段落" + index + "\",\"text_element_style\":{}}}]}}]")[0])
            .ToArray();

        await client.AppendCloudDocumentBlocksAsync(
            "doccn123",
            "root123",
            blocks,
            TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/docx/v1/documents/doccn123/blocks/root123/children",
            "/open-apis/docx/v1/documents/doccn123/blocks/root123/children"
        ], handler.RequestPaths);

        using var firstRequestDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        using var secondRequestDoc = JsonDocument.Parse(handler.RequestBodies[2]);
        var firstChildren = firstRequestDoc.RootElement.GetProperty("children");
        var secondChildren = secondRequestDoc.RootElement.GetProperty("children");
        Assert.Equal(50, firstChildren.GetArrayLength());
        Assert.Equal(1, secondChildren.GetArrayLength());
        Assert.Equal(0, firstRequestDoc.RootElement.GetProperty("index").GetInt32());
        Assert.Equal(50, secondRequestDoc.RootElement.GetProperty("index").GetInt32());
        Assert.Equal("段落1", firstChildren[0].GetProperty("text").GetProperty("elements")[0].GetProperty("text_run").GetProperty("content").GetString());
        Assert.Equal("段落50", firstChildren[49].GetProperty("text").GetProperty("elements")[0].GetProperty("text_run").GetProperty("content").GetString());
        Assert.Equal("段落51", secondChildren[0].GetProperty("text").GetProperty("elements")[0].GetProperty("text_run").GetProperty("content").GetString());
    }

    [Fact]
    public async Task ListCloudDocumentChildBlockIdsAsync_WhenChildrenSpanMultiplePages_ReturnsAllBlockIdsInOrder()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"items":[{"block_id":"blk-1"},{"block_id":"blk-2"}],"has_more":true,"page_token":"next-page"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"items":[{"block_id":"blk-3"}],"has_more":false}}""")
        ]);

        var client = CreateClient(handler);

        var blockIds = await client.ListCloudDocumentChildBlockIdsAsync(
            "doccn123",
            "root123",
            TestContext.Current.CancellationToken);

        Assert.Equal(["blk-1", "blk-2", "blk-3"], blockIds);
        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/docx/v1/documents/doccn123/blocks/root123/children",
            "/open-apis/docx/v1/documents/doccn123/blocks/root123/children"
        ], handler.RequestPaths);
        Assert.Contains("page_size=500", handler.RequestQueries[1], StringComparison.Ordinal);
        Assert.Contains("page_token=next-page", handler.RequestQueries[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteCloudDocumentChildBlocksAsync_DeletesRequestedRange()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{}}""")
        ]);

        var client = CreateClient(handler);

        await client.DeleteCloudDocumentChildBlocksAsync(
            "doccn123",
            "root123",
            0,
            2,
            TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/docx/v1/documents/doccn123/blocks/root123/children/batch_delete"
        ], handler.RequestPaths);
        Assert.Equal("DELETE", handler.RequestMethods[1]);

        using var requestDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal(0, requestDoc.RootElement.GetProperty("start_index").GetInt32());
        Assert.Equal(2, requestDoc.RootElement.GetProperty("end_index").GetInt32());
    }

    [Fact]
    public async Task FindCloudDocumentInFolderByTitleAsync_WhenExactDocxTitleExists_ReturnsDocumentInfo()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"files":[{"name":"docs/guide.md","token":"file_123","type":"file"},{"name":"docs/guide.md","token":"doccn123","type":"docx","url":"https://feishu.cn/docx/doccn123"}],"has_more":false}}""")
        ]);

        var client = CreateClient(handler);

        var document = await client.FindCloudDocumentInFolderByTitleAsync(
            "fld-target",
            "docs/guide.md",
            TestContext.Current.CancellationToken);

        Assert.NotNull(document);
        Assert.Equal("doccn123", document!.DocumentId);
        Assert.Equal("doccn123", document.RootBlockId);
        Assert.Equal("https://feishu.cn/docx/doccn123", document.Url);
        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/drive/v1/files"
        ], handler.RequestPaths);
        Assert.Contains("folder_token=fld-target", handler.RequestQueries[1], StringComparison.Ordinal);
        Assert.Contains("page_size=200", handler.RequestQueries[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task UploadCloudFileAsync_PostsMultipartFormDataAndReturnsFileToken()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"file_token":"file_token_123"}}""")
        ]);

        var client = CreateClient(handler);
        var fileContent = global::System.Text.Encoding.UTF8.GetBytes("# 标题\r\n\r\n正文");

        var fileToken = await client.UploadCloudFileAsync(
            "notes.md",
            fileContent,
            "fld-target",
            TestContext.Current.CancellationToken);

        Assert.Equal("file_token_123", fileToken);
        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/drive/v1/files/upload_all"
        ], handler.RequestPaths);
        Assert.Equal("POST", handler.RequestMethods[1]);
        Assert.StartsWith("multipart/form-data", handler.RequestContentTypes[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("name=file_name", handler.RequestBodies[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("notes.md", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Contains("name=parent_type", handler.RequestBodies[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("explorer", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Contains("name=parent_node", handler.RequestBodies[1], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fld-target", handler.RequestBodies[1], StringComparison.Ordinal);
        Assert.Contains("name=size", handler.RequestBodies[1], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportMarkdownFileAsCloudDocumentAsync_UploadsCreatesImportTaskPollsAndReturnsDocumentInfo()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"file_token":"file_token_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"ticket":"ticket_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"result":{"ticket":"ticket_123","job_status":1,"job_error_msg":"pending"}}}"""),
            CreateJsonResponse("""{"code":0,"data":{"result":{"ticket":"ticket_123","job_status":0,"job_error_msg":"success","token":"doccn123","url":"https://feishu.cn/docx/doccn123"}}}""")
        ]);

        var client = CreateClient(handler);
        var fileContent = global::System.Text.Encoding.UTF8.GetBytes("# 标题\r\n\r\n正文");

        var document = await client.ImportMarkdownFileAsCloudDocumentAsync(
            "notes.md",
            fileContent,
            "docs/guide.md",
            "fld-target",
            TestContext.Current.CancellationToken);

        Assert.Equal("doccn123", document.DocumentId);
        Assert.Equal("doccn123", document.RootBlockId);
        Assert.Equal("https://feishu.cn/docx/doccn123", document.Url);
        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/drive/v1/files/upload_all",
            "/open-apis/drive/v1/import_tasks",
            "/open-apis/drive/v1/import_tasks/ticket_123",
            "/open-apis/drive/v1/import_tasks/ticket_123"
        ], handler.RequestPaths);

        using var createImportTaskRequest = JsonDocument.Parse(handler.RequestBodies[2]);
        Assert.Equal("md", createImportTaskRequest.RootElement.GetProperty("file_extension").GetString());
        Assert.Equal("file_token_123", createImportTaskRequest.RootElement.GetProperty("file_token").GetString());
        Assert.Equal("docx", createImportTaskRequest.RootElement.GetProperty("type").GetString());
        Assert.Equal("docs/guide.md", createImportTaskRequest.RootElement.GetProperty("file_name").GetString());
        Assert.Equal(1, createImportTaskRequest.RootElement.GetProperty("point").GetProperty("mount_type").GetInt32());
        Assert.Equal("fld-target", createImportTaskRequest.RootElement.GetProperty("point").GetProperty("mount_key").GetString());

        var pollInterval = handler.RequestTimestamps[4] - handler.RequestTimestamps[3];
        Assert.True(
            pollInterval >= TimeSpan.FromMilliseconds(150),
            $"Expected markdown import polling to back off before retrying, but observed only {pollInterval.TotalMilliseconds:F0} ms between polls.");
    }

    [Fact]
    public async Task ImportMarkdownFileAsCloudDocumentAsync_WhenFolderTokenMissing_UsesRootFolderAndOmitsPoint()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"token":"fld-root","url":"https://feishu.cn/drive/folder/fld-root"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"file_token":"file_token_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"ticket":"ticket_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"result":{"ticket":"ticket_123","job_status":0,"job_error_msg":"success","token":"doccn123","url":"https://feishu.cn/docx/doccn123"}}}""")
        ]);

        var client = CreateClient(handler);
        var fileContent = global::System.Text.Encoding.UTF8.GetBytes("# 标题\r\n\r\n正文");

        var document = await client.ImportMarkdownFileAsCloudDocumentAsync(
            "notes.md",
            fileContent,
            "docs/guide.md",
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal("doccn123", document.DocumentId);
        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/drive/explorer/v2/root_folder/meta",
            "/open-apis/drive/v1/files/upload_all",
            "/open-apis/drive/v1/import_tasks",
            "/open-apis/drive/v1/import_tasks/ticket_123"
        ], handler.RequestPaths);

        Assert.Contains("fld-root", handler.RequestBodies[2], StringComparison.Ordinal);

        using var createImportTaskRequest = JsonDocument.Parse(handler.RequestBodies[3]);
        Assert.Equal("md", createImportTaskRequest.RootElement.GetProperty("file_extension").GetString());
        Assert.Equal("file_token_123", createImportTaskRequest.RootElement.GetProperty("file_token").GetString());
        Assert.Equal("docx", createImportTaskRequest.RootElement.GetProperty("type").GetString());
        Assert.Equal("docs/guide.md", createImportTaskRequest.RootElement.GetProperty("file_name").GetString());
        Assert.False(createImportTaskRequest.RootElement.TryGetProperty("point", out _));
    }

    [Fact]
    public async Task ImportMarkdownFileAsCloudDocumentAsync_WhenImportTaskFails_ThrowsChineseError()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"file_token":"file_token_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"ticket":"ticket_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"result":{"ticket":"ticket_123","job_status":118,"job_error_msg":"上传文件和导入任务文件后缀不一致"}}}""")
        ]);

        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ImportMarkdownFileAsCloudDocumentAsync(
                "notes.md",
                global::System.Text.Encoding.UTF8.GetBytes("# 标题"),
                "docs/guide.md",
                "fld-target",
                TestContext.Current.CancellationToken));

        Assert.Contains("Markdown 导入失败", exception.Message, StringComparison.Ordinal);
        Assert.Contains("上传文件和导入任务文件后缀不一致", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateCloudDocumentAsync_WhenApiReturnsBadRequest_ExceptionIncludesResponseBody()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent("""{"code":99991672,"msg":"Access denied","error":{"permission_violations":[{"subject":"docx:document"},{"subject":"docx:document:create"}]}}""")
            }
        ]);

        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.CreateCloudDocumentAsync("thread-1 continue - 瀹屾暣鍥炲", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("BadRequest", exception.Message, StringComparison.Ordinal);
        Assert.Contains("99991672", exception.Message, StringComparison.Ordinal);
        Assert.Contains("docx:document", exception.Message, StringComparison.Ordinal);
        Assert.Contains("docx:document:create", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadMessageResourceAsync_GetsBinaryBodyAndInfersFileName()
    {
        var imageBytes = new byte[] { 1, 2, 3, 4 };
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(imageBytes)
                {
                    Headers =
                    {
                        ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png"),
                        ContentDisposition = new System.Net.Http.Headers.ContentDispositionHeaderValue("attachment")
                        {
                            FileName = "\"screen.png\""
                        }
                    }
                }
            }
        ]);

        var client = CreateClient(handler);

        var result = await client.DownloadMessageResourceAsync(
            "om_message_123",
            "img_v2_123",
            "image",
            TestContext.Current.CancellationToken);

        Assert.Equal(imageBytes, result.Content);
        Assert.Equal("screen.png", result.FileName);
        Assert.Equal("image/png", result.MimeType);
        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/im/v1/messages/om_message_123/resources/img_v2_123"
        ], handler.RequestPaths);
        Assert.Equal("type=image", handler.RequestQueries[1]);
    }

    [Fact]
    public async Task SendTextMessageAsync_SendsTextPayload()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_text_success"}}""")
        ]);

        var client = CreateClient(handler);

        var messageId = await client.SendTextMessageAsync("oc_text_chat", "done", TestContext.Current.CancellationToken);

        Assert.Equal("om_text_success", messageId);
        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/im/v1/messages"
        ], handler.RequestPaths);

        using var requestDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("text", requestDoc.RootElement.GetProperty("msg_type").GetString());
        Assert.Equal("oc_text_chat", requestDoc.RootElement.GetProperty("receive_id").GetString());

        using var contentDoc = JsonDocument.Parse(requestDoc.RootElement.GetProperty("content").GetString()!);
        Assert.Equal("done", contentDoc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ReplyTextMessageAsync_SendsTextPayload()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_text_reply_success"}}""")
        ]);

        var client = CreateClient(handler);

        var messageId = await client.ReplyTextMessageAsync("om_reply", "done", TestContext.Current.CancellationToken);

        Assert.Equal("om_text_reply_success", messageId);
        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/im/v1/messages/om_reply/reply"
        ], handler.RequestPaths);

        using var requestDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("text", requestDoc.RootElement.GetProperty("msg_type").GetString());

        using var contentDoc = JsonDocument.Parse(requestDoc.RootElement.GetProperty("content").GetString()!);
        Assert.Equal("done", contentDoc.RootElement.GetProperty("text").GetString());
    }

    [Fact]
    public async Task ReplyRawCardAsync_Throws_WhenReplyReturnsBusinessError()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":10002,"msg":"invalid card payload"}""")
        ]);

        var client = CreateClient(handler);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ReplyRawCardAsync(
                "om_reply",
                """{"schema":"2.0","body":{"elements":[]}}""",
                TestContext.Current.CancellationToken));

        Assert.Contains("Reply raw Feishu card message failed", exception.Message);
        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/cardkit/v1/cards",
            "/open-apis/im/v1/messages/om_reply/reply"
        ], handler.RequestPaths);
    }

    [Fact]
    public async Task ReplyElementsCardAsync_CreatesCardThenRepliesWithCardId()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_reply_success"}}""")
        ]);

        var client = CreateClient(handler);
        var card = new ElementsCardV2Dto
        {
            Header = new ElementsCardV2Dto.HeaderSuffix
            {
                Template = "blue",
                Title = new HeaderTitleElement { Content = "Help card" }
            },
            Body = new ElementsCardV2Dto.BodySuffix
            {
                Elements =
                [
                    new
                    {
                        tag = "div",
                        text = new { tag = "plain_text", content = "hello" }
                    }
                ]
            }
        };

        var messageId = await client.ReplyElementsCardAsync("om_reply", card, TestContext.Current.CancellationToken);

        Assert.Equal("om_reply_success", messageId);
        Assert.Equal(
        [
            "/open-apis/auth/v3/tenant_access_token/internal",
            "/open-apis/cardkit/v1/cards",
            "/open-apis/im/v1/messages/om_reply/reply"
        ], handler.RequestPaths);

        using var createDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        Assert.Equal("card_json", createDoc.RootElement.GetProperty("type").GetString());
        Assert.Equal(JsonValueKind.String, createDoc.RootElement.GetProperty("data").ValueKind);

        using var cardDoc = JsonDocument.Parse(createDoc.RootElement.GetProperty("data").GetString()!);
        Assert.Equal("2.0", cardDoc.RootElement.GetProperty("schema").GetString());
        Assert.Equal("blue", cardDoc.RootElement.GetProperty("header").GetProperty("template").GetString());
        Assert.Equal("Help card", cardDoc.RootElement.GetProperty("header").GetProperty("title").GetProperty("content").GetString());
        Assert.Equal("div", cardDoc.RootElement.GetProperty("body").GetProperty("elements")[0].GetProperty("tag").GetString());

        using var requestDoc = JsonDocument.Parse(handler.RequestBodies[2]);
        Assert.Equal("interactive", requestDoc.RootElement.GetProperty("msg_type").GetString());
        Assert.Equal(JsonValueKind.String, requestDoc.RootElement.GetProperty("content").ValueKind);

        using var replyDoc = JsonDocument.Parse(requestDoc.RootElement.GetProperty("content").GetString()!);
        Assert.Equal("card", replyDoc.RootElement.GetProperty("type").GetString());
        Assert.Equal("card_123", replyDoc.RootElement.GetProperty("data").GetProperty("card_id").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_FallsBackToReadableChineseStatusHeader()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""")
        ]);

        var client = CreateClient(handler);
        var chrome = new FeishuStreamingCardChrome();
        chrome.OverflowOptions.Add(new FeishuStreamingCardOverflowOption
        {
            Text = "Backend API",
            Value = new { action = "switch_session", session_id = "session-2", chat_key = "oc_stream_chat" }
        });
        chrome.OverflowOptions.Add(new FeishuStreamingCardOverflowOption
        {
            Text = "妯″瀷/浼氳瘽绠＄悊...",
            Value = new { action = "open_session_manager" }
        });

        await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "still have backlog",
            "AI 鍔╂墜",
            TestContext.Current.CancellationToken,
            chrome: chrome);

        using var createDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        using var cardDoc = JsonDocument.Parse(createDoc.RootElement.GetProperty("data").GetString()!);
        Assert.False(cardDoc.RootElement.GetProperty("config").TryGetProperty("streaming_mode", out _));
        var elements = cardDoc.RootElement.GetProperty("body").GetProperty("elements");
        var statusModule = elements[0];
        var overflow = statusModule.GetProperty("extra");

        Assert.Equal("当前会话", statusModule.GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("overflow", overflow.GetProperty("tag").GetString());
        Assert.Equal("Backend API", overflow.GetProperty("options")[0].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("{\"action\":\"switch_session\",\"session_id\":\"session-2\",\"chat_key\":\"oc_stream_chat\"}", overflow.GetProperty("options")[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_RendersBottomPromptForm()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""")
        ]);

        var client = CreateClient(handler);
        var chrome = new FeishuStreamingCardChrome
        {
            StatusMarkdown = "褰撳墠浼氳瘽"
        };
        chrome.BottomPrompt = new FeishuStreamingCardBottomPrompt
        {
            InputName = LowInterruptionContinueDefaults.PromptFieldName,
            InputLabel = "灏戞墦鏂彁绀鸿瘝",
            Placeholder = LowInterruptionContinueDefaults.PromptPlaceholder,
            DefaultValue = LowInterruptionContinueDefaults.DefaultPrompt,
            ButtonText = "Continue",
            ButtonType = "primary",
            Value = new
            {
                action = "low_interruption_continue",
                session_id = "session-1",
                chat_key = "oc_stream_chat",
                tool_id = "codex"
            }
        };

        await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "still have backlog",
            "AI 鍔╂墜",
            TestContext.Current.CancellationToken,
            chrome: chrome);

        using var createDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        using var cardDoc = JsonDocument.Parse(createDoc.RootElement.GetProperty("data").GetString()!);
        var elements = cardDoc.RootElement.GetProperty("body").GetProperty("elements");
        Assert.Equal("🟥🟥🟥 **回复内容**", elements[1].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("🟥🟥🟥 **Superpowers 工作流/Goal不间断执行**", elements[3].GetProperty("text").GetProperty("content").GetString());
        var bottomActionModule = elements.EnumerateArray().Last();

        Assert.Equal("form", bottomActionModule.GetProperty("tag").GetString());

        var buttonRow = bottomActionModule.GetProperty("elements")[0];
        Assert.Equal("column_set", buttonRow.GetProperty("tag").GetString());

        var input = buttonRow.GetProperty("columns")[0].GetProperty("elements")[0];
        Assert.Equal("input", input.GetProperty("tag").GetString());
        Assert.Equal(LowInterruptionContinueDefaults.PromptFieldName, input.GetProperty("name").GetString());
        Assert.Equal(LowInterruptionContinueDefaults.DefaultPrompt, input.GetProperty("default_value").GetString());

        var button = buttonRow.GetProperty("columns")[1].GetProperty("elements")[0];
        Assert.Equal("button", button.GetProperty("tag").GetString());
        Assert.Equal("primary", button.GetProperty("type").GetString());
        Assert.Equal("Continue", button.GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("form_submit", button.GetProperty("action_type").GetString());
        Assert.Equal("low_interruption_continue", button.GetProperty("value").GetProperty("action").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_UsesUniqueSubmitButtonNames_ForMultipleBottomPrompts()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""")
        ]);

        var client = CreateClient(handler);
        var chrome = new FeishuStreamingCardChrome
        {
            StatusMarkdown = "褰撳墠浼氳瘽",
            BottomPrompt = new FeishuStreamingCardBottomPrompt
            {
                FormName = "superpowers_quick_action_form",
                InputName = "superpowers_quick_input",
                InputLabel = "Use superpowers workflow",
                Placeholder = "Enter text and submit",
                DefaultValue = string.Empty,
                ButtonText = "鎻愪氦",
                ButtonType = "primary",
                Value = new { action = "submit_superpowers_quick_input" }
            },
            AdditionalBottomPrompts =
            [
                new FeishuStreamingCardBottomPrompt
                {
                    FormName = "goal_quick_action_form",
                    InputName = "goal_quick_input",
                    InputLabel = "Use /goal workflow",
                    Placeholder = "Enter text and submit",
                    DefaultValue = string.Empty,
                    ButtonText = "鎻愪氦",
                    ButtonType = "primary",
                    Value = new { action = "submit_goal_quick_input" }
                }
            ]
        };

        await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "still have backlog",
            "AI 鍔╂墜",
            TestContext.Current.CancellationToken,
            chrome: chrome);

        using var createDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        using var cardDoc = JsonDocument.Parse(createDoc.RootElement.GetProperty("data").GetString()!);
        var elements = cardDoc.RootElement.GetProperty("body").GetProperty("elements");

        var firstFormButton = elements[4].GetProperty("elements")[0].GetProperty("columns")[1].GetProperty("elements")[0];
        var secondFormButton = elements[5].GetProperty("elements")[0].GetProperty("columns")[1].GetProperty("elements")[0];

        Assert.Equal("superpowers_quick_input_submit", firstFormButton.GetProperty("name").GetString());
        Assert.Equal("goal_quick_input_submit", secondFormButton.GetProperty("name").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_RendersTopChipGroupsBetweenStatusAndBody()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""")
        ]);

        var client = CreateClient(handler);
        var chrome = new FeishuStreamingCardChrome
        {
            StatusMarkdown = "当前会话"
        };
        chrome.TopChipGroups.Add(new FeishuStreamingCardTopChipGroup
        {
            Kind = "model",
            IsEnabled = true,
            SummaryMarkdown = "🤖 模型：`gpt-5.3-codex-spark`",
            OverflowOptions =
            [
                new FeishuStreamingCardOverflowOption
                {
                    Text = "gpt-5.3-codex-spark",
                    Value = new { action = "switch_streaming_card_model", session_id = "session-1", chat_key = "oc_stream_chat", model = "gpt-5.3-codex-spark" }
                },
                new FeishuStreamingCardOverflowOption
                {
                    Text = "gpt-5.2",
                    Value = new { action = "switch_streaming_card_model", session_id = "session-1", chat_key = "oc_stream_chat", model = "gpt-5.2" }
                }
            ]
        });

        await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "still have backlog",
            "AI Assistant",
            TestContext.Current.CancellationToken,
            chrome: chrome);

        using var createDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        using var cardDoc = JsonDocument.Parse(createDoc.RootElement.GetProperty("data").GetString()!);
        var elements = cardDoc.RootElement.GetProperty("body").GetProperty("elements");

        Assert.Equal("div", elements[0].GetProperty("tag").GetString());
        Assert.Equal("🟥🟥🟥 **思考等级**", elements[1].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("div", elements[2].GetProperty("tag").GetString());
        Assert.Equal("🟥🟥🟥 **回复内容**", elements[3].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("markdown", elements[4].GetProperty("tag").GetString());
        Assert.Equal("🤖 模型：`gpt-5.3-codex-spark`", elements[2].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("overflow", elements[2].GetProperty("extra").GetProperty("tag").GetString());
        var options = elements[2].GetProperty("extra").GetProperty("options");
        Assert.Equal(2, options.GetArrayLength());
        Assert.Equal("gpt-5.3-codex-spark", options[0].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("gpt-5.2", options[1].GetProperty("text").GetProperty("content").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_SplitsTopChipGroupIntoMultipleRowsWhenMoreThanSixItems()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""")
        ]);

        var client = CreateClient(handler);
        var chrome = new FeishuStreamingCardChrome
        {
            StatusMarkdown = "当前会话"
        };

        var items = Enumerable.Range(1, 7)
            .Select(index => new FeishuStreamingCardTopChipItem
            {
                Text = $"gpt-5.{index}",
                IsActive = index == 1,
                IsEnabled = true,
                Value = new
                {
                    action = "switch_streaming_card_model",
                    session_id = "session-1",
                    chat_key = "oc_stream_chat",
                    model = $"gpt-5.{index}"
                }
            })
            .ToList();

        chrome.TopChipGroups.Add(new FeishuStreamingCardTopChipGroup
        {
            Kind = "model",
            Items = items
        });

        await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "still have backlog",
            "AI 助手",
            TestContext.Current.CancellationToken,
            chrome: chrome);

        using var createDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        using var cardDoc = JsonDocument.Parse(createDoc.RootElement.GetProperty("data").GetString()!);
        var elements = cardDoc.RootElement.GetProperty("body").GetProperty("elements");

        Assert.Equal("🟥🟥🟥 **思考等级**", elements[1].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("column_set", elements[2].GetProperty("tag").GetString());
        Assert.Equal("column_set", elements[3].GetProperty("tag").GetString());
        Assert.Equal(6, elements[2].GetProperty("columns").GetArrayLength());
        Assert.Equal(1, elements[3].GetProperty("columns").GetArrayLength());
        Assert.Equal("gpt-5.1", elements[2].GetProperty("columns")[0].GetProperty("elements")[0].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("gpt-5.7", elements[3].GetProperty("columns")[0].GetProperty("elements")[0].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("🟥🟥🟥 **回复内容**", elements[4].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("markdown", elements[5].GetProperty("tag").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_RendersWorkflowSectionMarkerBeforeBottomActions()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""")
        ]);

        var client = CreateClient(handler);
        var chrome = new FeishuStreamingCardChrome
        {
            StatusMarkdown = "褰撳墠浼氳瘽"
        };
        chrome.BottomActions.Add(new FeishuStreamingCardBottomAction
        {
            Text = "鎵ц plan",
            Type = "primary",
            Value = new { action = "execute_superpowers_plan", session_id = "session-1" }
        });

        await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "still have backlog",
            "AI 鍔╂墜",
            TestContext.Current.CancellationToken,
            chrome: chrome);

        using var createDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        using var cardDoc = JsonDocument.Parse(createDoc.RootElement.GetProperty("data").GetString()!);
        var elements = cardDoc.RootElement.GetProperty("body").GetProperty("elements");

        Assert.Equal("🟥🟥🟥 **回复内容**", elements[1].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("markdown", elements[2].GetProperty("tag").GetString());
        Assert.Equal("🟥🟥🟥 **Superpowers 工作流/Goal不间断执行**", elements[3].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("column_set", elements[4].GetProperty("tag").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_RendersBottomActionsAcrossMultipleRows_WhenRowKeysDiffer()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""")
        ]);

        var client = CreateClient(handler);
        var chrome = new FeishuStreamingCardChrome
        {
            StatusMarkdown = "当前会话"
        };
        chrome.BottomActions.AddRange(
        [
            new FeishuStreamingCardBottomAction
            {
                Text = "/goal",
                RowKey = "goal_row_1",
                Value = new { action = "status_goal", session_id = "session-1" }
            },
            new FeishuStreamingCardBottomAction
            {
                Text = "/goal pause",
                RowKey = "goal_row_1",
                Value = new { action = "pause_goal", session_id = "session-1" }
            },
            new FeishuStreamingCardBottomAction
            {
                Text = "继续",
                RowKey = "execution_control_row",
                Value = new { action = "continue_superpowers", session_id = "session-1" }
            },
            new FeishuStreamingCardBottomAction
            {
                Text = "停止",
                RowKey = "execution_control_row",
                Value = new { action = "stop_streaming_execution", session_id = "session-1" }
            }
        ]);

        await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "still have backlog",
            "AI 助手",
            TestContext.Current.CancellationToken,
            chrome: chrome);

        using var createDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        using var cardDoc = JsonDocument.Parse(createDoc.RootElement.GetProperty("data").GetString()!);
        var elements = cardDoc.RootElement.GetProperty("body").GetProperty("elements");

        Assert.Equal("🟥🟥🟥 **Superpowers 工作流/Goal不间断执行**", elements[3].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("column_set", elements[4].GetProperty("tag").GetString());
        Assert.Equal("column_set", elements[5].GetProperty("tag").GetString());
        Assert.Equal(2, elements[4].GetProperty("columns").GetArrayLength());
        Assert.Equal(2, elements[5].GetProperty("columns").GetArrayLength());
        Assert.Equal("/goal", elements[4].GetProperty("columns")[0].GetProperty("elements")[0].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("/goal pause", elements[4].GetProperty("columns")[1].GetProperty("elements")[0].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("继续", elements[5].GetProperty("columns")[0].GetProperty("elements")[0].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("停止", elements[5].GetProperty("columns")[1].GetProperty("elements")[0].GetProperty("text").GetProperty("content").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_RendersLatestToolCallLineBelowReplyContent()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""")
        ]);

        var client = CreateClient(handler);
        var chrome = new FeishuStreamingCardChrome
        {
            StatusMarkdown = "褰撳墠浼氳瘽",
            LatestToolCallMarkdown = "**璋冪敤宸ュ叿锛?* `Bash 路 git status --short`"
        };

        await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "assistant output",
            "AI 鍔╂墜",
            TestContext.Current.CancellationToken,
            chrome: chrome);

        using var createDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        using var cardDoc = JsonDocument.Parse(createDoc.RootElement.GetProperty("data").GetString()!);
        var elements = cardDoc.RootElement.GetProperty("body").GetProperty("elements");

        Assert.Equal("🟥🟥🟥 **回复内容**", elements[1].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("markdown", elements[2].GetProperty("tag").GetString());
        Assert.Equal("assistant output", elements[2].GetProperty("content").GetString());
        Assert.Equal("div", elements[3].GetProperty("tag").GetString());
        Assert.Equal("**璋冪敤宸ュ叿锛?* `Bash 路 git status --short`", elements[3].GetProperty("text").GetProperty("content").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_RendersBottomNoticeAboveActions()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""")
        ]);

        var client = CreateClient(handler);
        var chrome = new FeishuStreamingCardChrome
        {
            StatusMarkdown = "褰撳墠浼氳瘽"
        };
        chrome.BottomNoticeMarkdowns.Add("Session binding changed");
        chrome.BottomActions.Add(new FeishuStreamingCardBottomAction
        {
            Text = "Continue original session",
            Type = "default",
            Value = new { action = "confirm_bound_superpowers_action", session_id = "session-1" }
        });

        await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "still have backlog",
            "AI 鍔╂墜",
            TestContext.Current.CancellationToken,
            chrome: chrome);

        using var createDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        using var cardDoc = JsonDocument.Parse(createDoc.RootElement.GetProperty("data").GetString()!);
        var elements = cardDoc.RootElement.GetProperty("body").GetProperty("elements");

        Assert.Equal("🟥🟥🟥 **Superpowers 工作流/Goal不间断执行**", elements[3].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("div", elements[4].GetProperty("tag").GetString());
        Assert.Equal("Session binding changed", elements[4].GetProperty("text").GetProperty("content").GetString());
        Assert.Equal("column_set", elements[5].GetProperty("tag").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_KeepsClientStreamingMode_WhenNoOverflowActionsExist()
    {
        var handler = new StubHttpMessageHandler(
        [
            CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""),
            CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""),
            CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""")
        ]);

        var client = CreateClient(handler);
        var chrome = new FeishuStreamingCardChrome
        {
            StatusMarkdown = "褰撳墠浼氳瘽"
        };

        await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "still have backlog",
            "AI 鍔╂墜",
            TestContext.Current.CancellationToken,
            chrome: chrome);

        using var createDoc = JsonDocument.Parse(handler.RequestBodies[1]);
        using var cardDoc = JsonDocument.Parse(createDoc.RootElement.GetProperty("data").GetString()!);

        Assert.True(cardDoc.RootElement.GetProperty("config").GetProperty("streaming_mode").GetBoolean());
    }

    [Fact]
    public async Task FeishuStreamingHandle_FinishAsync_WaitsForInflightUpdate_AndBlocksLaterUpdates()
    {
        var updateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpdate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new List<string>();

        var handle = new FeishuStreamingHandle(
            "card-1",
            "message-1",
            async (content, sequence) =>
            {
                operations.Add($"update:{sequence}:{content}");
                updateEntered.TrySetResult();
                await releaseUpdate.Task;
            },
            (content, sequence) =>
            {
                operations.Add($"finish:{sequence}:{content}");
                return Task.CompletedTask;
            },
            throttleMs: 0);

        var inflightUpdate = handle.UpdateAsync("streaming");
        await updateEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

        var finishTask = handle.FinishAsync("final");
        Assert.False(finishTask.IsCompleted);

        releaseUpdate.TrySetResult();

        await inflightUpdate;
        await finishTask;
        await handle.UpdateAsync("late");

        Assert.Equal(
            [
                "update:1:streaming",
                "finish:2:final"
            ],
            operations);
    }

    [Fact]
    public async Task FeishuStreamingHandle_UpdateAsync_HonorsQuietWindowAfterUpdate()
    {
        var operations = new List<string>();
        var handle = new FeishuStreamingHandle(
            "card-1",
            "message-1",
            (content, sequence) =>
            {
                operations.Add($"update:{sequence}:{content}");
                return Task.CompletedTask;
            },
            (content, sequence) => Task.CompletedTask,
            throttleMs: 0,
            quietWindowAfterUpdateMs: 120);

        await handle.UpdateAsync("first");
        await handle.UpdateAsync("second");
        await Task.Delay(160, TestContext.Current.CancellationToken);
        await handle.UpdateAsync("third");

        Assert.Equal(
            [
                "update:1:first",
                "update:2:third"
            ],
            operations);
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_RetriesTimedOutUpdateOnceWithSameSequenceAndUuid()
    {
        var handler = new TimeoutOnFirstCardUpdateHandler();
        var client = CreateClient(handler, new FeishuOptions
        {
            AppId = "app-id",
            AppSecret = "app-secret",
            HttpTimeoutSeconds = 1,
            StreamingThrottleMs = 0
        });

        var handle = await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "initial",
            "AI 鍔╂墜",
            TestContext.Current.CancellationToken);

        await handle.UpdateAsync("first update");
        await handle.UpdateAsync("second update");

        Assert.False(handle.AreCardUpdatesStopped);
        Assert.Equal(
            3,
            handler.RequestPaths.Count(path => string.Equals(path, "/open-apis/cardkit/v1/cards/card_123", StringComparison.Ordinal)));

        var updateBodies = handler.RequestPaths
            .Select((path, index) => new { path, body = handler.RequestBodies[index] })
            .Where(entry => string.Equals(entry.path, "/open-apis/cardkit/v1/cards/card_123", StringComparison.Ordinal))
            .Select(entry => JsonDocument.Parse(entry.body))
            .ToArray();

        Assert.Equal(1, updateBodies[0].RootElement.GetProperty("sequence").GetInt32());
        Assert.Equal(1, updateBodies[1].RootElement.GetProperty("sequence").GetInt32());
        Assert.Equal(2, updateBodies[2].RootElement.GetProperty("sequence").GetInt32());

        var firstUuid = updateBodies[0].RootElement.GetProperty("uuid").GetString();
        Assert.Equal(firstUuid, updateBodies[1].RootElement.GetProperty("uuid").GetString());
        Assert.NotEqual(firstUuid, updateBodies[2].RootElement.GetProperty("uuid").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_TreatsTimeoutThenSequenceConflictAsSuccessfulPriorWrite()
    {
        var handler = new TimeoutThenSequenceConflictCardUpdateHandler();
        var client = CreateClient(handler, new FeishuOptions
        {
            AppId = "app-id",
            AppSecret = "app-secret",
            HttpTimeoutSeconds = 1,
            StreamingThrottleMs = 0
        });

        var handle = await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "initial",
            "AI Assistant",
            TestContext.Current.CancellationToken);

        await handle.UpdateAsync("first update");
        await handle.UpdateAsync("second update");

        Assert.False(handle.AreCardUpdatesStopped);
        Assert.Equal(2, handler.SuccessfulLogicalUpdates);

        Assert.Equal(
            3,
            handler.RequestPaths.Count(path => string.Equals(path, "/open-apis/cardkit/v1/cards/card_123", StringComparison.Ordinal)));

        var updateBodies = handler.RequestPaths
            .Select((path, index) => new { path, body = handler.RequestBodies[index] })
            .Where(entry => string.Equals(entry.path, "/open-apis/cardkit/v1/cards/card_123", StringComparison.Ordinal))
            .Select(entry => JsonDocument.Parse(entry.body))
            .ToArray();

        Assert.Equal(1, updateBodies[0].RootElement.GetProperty("sequence").GetInt32());
        Assert.Equal(1, updateBodies[1].RootElement.GetProperty("sequence").GetInt32());
        Assert.Equal(2, updateBodies[2].RootElement.GetProperty("sequence").GetInt32());

        var firstUuid = updateBodies[0].RootElement.GetProperty("uuid").GetString();
        Assert.Equal(firstUuid, updateBodies[1].RootElement.GetProperty("uuid").GetString());
        Assert.NotEqual(firstUuid, updateBodies[2].RootElement.GetProperty("uuid").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_TreatsTimeoutThenDuplicateUuidAsSuccessfulPriorWrite()
    {
        var handler = new TimeoutThenDuplicateUuidCardUpdateHandler();
        var client = CreateClient(handler, new FeishuOptions
        {
            AppId = "app-id",
            AppSecret = "app-secret",
            HttpTimeoutSeconds = 1,
            StreamingThrottleMs = 0
        });

        var handle = await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "initial",
            "AI 鍔╂墜",
            TestContext.Current.CancellationToken);

        await handle.UpdateAsync("first update");
        await handle.UpdateAsync("second update");

        Assert.False(handle.AreCardUpdatesStopped);
        Assert.Equal(2, handler.SuccessfulLogicalUpdates);

        Assert.Equal(
            3,
            handler.RequestPaths.Count(path => string.Equals(path, "/open-apis/cardkit/v1/cards/card_123", StringComparison.Ordinal)));

        var updateBodies = handler.RequestPaths
            .Select((path, index) => new { path, body = handler.RequestBodies[index] })
            .Where(entry => string.Equals(entry.path, "/open-apis/cardkit/v1/cards/card_123", StringComparison.Ordinal))
            .Select(entry => JsonDocument.Parse(entry.body))
            .ToArray();

        Assert.Equal(1, updateBodies[0].RootElement.GetProperty("sequence").GetInt32());
        Assert.Equal(1, updateBodies[1].RootElement.GetProperty("sequence").GetInt32());
        Assert.Equal(2, updateBodies[2].RootElement.GetProperty("sequence").GetInt32());

        var firstUuid = updateBodies[0].RootElement.GetProperty("uuid").GetString();
        Assert.Equal(firstUuid, updateBodies[1].RootElement.GetProperty("uuid").GetString());
        Assert.NotEqual(firstUuid, updateBodies[2].RootElement.GetProperty("uuid").GetString());
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_TreatsPlainSequenceConflictAsCardFailure()
    {
        var handler = new PlainSequenceConflictCardUpdateHandler();
        var client = CreateClient(handler, new FeishuOptions
        {
            AppId = "app-id",
            AppSecret = "app-secret",
            HttpTimeoutSeconds = 1,
            StreamingThrottleMs = 0
        });

        var handle = await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "initial",
            "AI Assistant",
            TestContext.Current.CancellationToken);

        await handle.UpdateAsync("first update");

        Assert.True(handle.AreCardUpdatesStopped);
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_RetriesOverflowUpdateWithReducedReplyOnlyPayload()
    {
        var handler = new OverflowThenReducedCardUpdateHandler();
        var client = CreateClient(handler, new FeishuOptions
        {
            AppId = "app-id",
            AppSecret = "app-secret",
            HttpTimeoutSeconds = 30,
            StreamingThrottleMs = 0
        });

        var handle = await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "initial",
            "AI 鍔╂墜",
            TestContext.Current.CancellationToken,
            chrome: CreateVerboseStreamingChrome());

        await handle.UpdateAsync(BuildLargeStreamingContent());

        Assert.False(handle.AreCardUpdatesStopped);

        var updateBodies = handler.RequestPaths
            .Select((path, index) => new { path, body = handler.RequestBodies[index] })
            .Where(entry => string.Equals(entry.path, "/open-apis/cardkit/v1/cards/card_123", StringComparison.Ordinal))
            .Select(entry => JsonDocument.Parse(entry.body))
            .ToArray();

        Assert.Equal(2, updateBodies.Length);
        Assert.Equal(1, updateBodies[0].RootElement.GetProperty("sequence").GetInt32());
        Assert.Equal(1, updateBodies[1].RootElement.GetProperty("sequence").GetInt32());

        using var firstCardDoc = JsonDocument.Parse(updateBodies[0].RootElement.GetProperty("card").GetProperty("data").GetString()!);
        using var secondCardDoc = JsonDocument.Parse(updateBodies[1].RootElement.GetProperty("card").GetProperty("data").GetString()!);

        Assert.True(firstCardDoc.RootElement.GetProperty("body").GetProperty("elements").GetArrayLength() > 3);
        Assert.Equal(1, secondCardDoc.RootElement.GetProperty("body").GetProperty("elements").GetArrayLength());

        var reducedContent = secondCardDoc.RootElement
            .GetProperty("body")
            .GetProperty("elements")[0]
            .GetProperty("content")
            .GetString();

        Assert.Contains("卡片已精简", reducedContent);
        Assert.Contains("仅显示最新内容", reducedContent);
        Assert.Contains("line 359", reducedContent);
        Assert.DoesNotContain("line 000", reducedContent);
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_RetriesOverflowCardCreationWithReducedReplyOnlyPayload()
    {
        var handler = new OverflowThenReducedCardCreateHandler();
        var client = CreateClient(handler, new FeishuOptions
        {
            AppId = "app-id",
            AppSecret = "app-secret",
            HttpTimeoutSeconds = 30,
            StreamingThrottleMs = 0
        });

        var handle = await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            BuildLargeStreamingContent(),
            "AI 鍔╂墜",
            TestContext.Current.CancellationToken,
            chrome: CreateVerboseStreamingChrome());

        Assert.Equal("card_123", handle.CardId);

        var createBodies = handler.RequestPaths
            .Select((path, index) => new { path, body = handler.RequestBodies[index] })
            .Where(entry => string.Equals(entry.path, "/open-apis/cardkit/v1/cards", StringComparison.Ordinal))
            .Select(entry => JsonDocument.Parse(entry.body))
            .ToArray();

        Assert.Equal(2, createBodies.Length);

        using var firstCardDoc = JsonDocument.Parse(createBodies[0].RootElement.GetProperty("data").GetString()!);
        using var secondCardDoc = JsonDocument.Parse(createBodies[1].RootElement.GetProperty("data").GetString()!);

        Assert.True(firstCardDoc.RootElement.GetProperty("body").GetProperty("elements").GetArrayLength() > 3);
        Assert.Equal(1, secondCardDoc.RootElement.GetProperty("body").GetProperty("elements").GetArrayLength());

        var reducedContent = secondCardDoc.RootElement
            .GetProperty("body")
            .GetProperty("elements")[0]
            .GetProperty("content")
            .GetString();

        Assert.Contains("卡片已精简", reducedContent);
        Assert.Contains("仅显示最新内容", reducedContent);
        Assert.Contains("line 359", reducedContent);
        Assert.DoesNotContain("line 000", reducedContent);
    }

    [Fact]
    public async Task CreateStreamingHandleAsync_SticksToReducedPayloadAfterOverflowRecovery()
    {
        var handler = new OverflowRequiresReducedPayloadHandler();
        var client = CreateClient(handler, new FeishuOptions
        {
            AppId = "app-id",
            AppSecret = "app-secret",
            HttpTimeoutSeconds = 30,
            StreamingThrottleMs = 0
        });

        var handle = await client.CreateStreamingHandleAsync(
            "oc_stream_chat",
            null,
            "initial",
            "AI 鍔╂墜",
            TestContext.Current.CancellationToken,
            chrome: CreateVerboseStreamingChrome());

        await handle.UpdateAsync(BuildLargeStreamingContent());
        await handle.UpdateAsync(BuildLargeStreamingContent() + Environment.NewLine + "tail next");

        Assert.False(handle.AreCardUpdatesStopped);

        var updateBodies = handler.RequestPaths
            .Select((path, index) => new { path, body = handler.RequestBodies[index] })
            .Where(entry => string.Equals(entry.path, "/open-apis/cardkit/v1/cards/card_123", StringComparison.Ordinal))
            .Select(entry => JsonDocument.Parse(entry.body))
            .ToArray();

        Assert.Equal(3, updateBodies.Length);

        using var lastCardDoc = JsonDocument.Parse(updateBodies[^1].RootElement.GetProperty("card").GetProperty("data").GetString()!);
        Assert.Equal(1, lastCardDoc.RootElement.GetProperty("body").GetProperty("elements").GetArrayLength());

        var reducedContent = lastCardDoc.RootElement
            .GetProperty("body")
            .GetProperty("elements")[0]
            .GetProperty("content")
            .GetString();

        Assert.Contains("卡片已精简", reducedContent);
        Assert.Contains("tail next", reducedContent);
    }

    private static FeishuStreamingCardChrome CreateVerboseStreamingChrome()
    {
        var chrome = new FeishuStreamingCardChrome
        {
            StatusMarkdown = "Current session / processing",
            LatestToolCallMarkdown = "**璋冪敤宸ュ叿锛?* `powershell.exe -Command ...`"
        };

        chrome.BottomNoticeMarkdowns.Add("This is a long tool output card and should shrink on overflow.");
        chrome.BottomActions.Add(new FeishuStreamingCardBottomAction
        {
            Text = "缁х画",
            Type = "primary",
            Value = new { action = "continue" }
        });

        return chrome;
    }

    private static string BuildLargeStreamingContent()
    {
        return string.Join(
            Environment.NewLine,
            Enumerable.Range(0, 360).Select(index => $"line {index:000} {new string('x', 24)}"));
    }

    private static JsonElement[] ParseJsonElementArray(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .EnumerateArray()
            .Select(element => element.Clone())
            .ToArray();
    }

    private static FeishuCardKitClient CreateClient(HttpMessageHandler handler, FeishuOptions? optionsOverride = null)
    {
        var options = Options.Create(optionsOverride ?? new FeishuOptions
        {
            AppId = "app-id",
            AppSecret = "app-secret",
            HttpTimeoutSeconds = 30
        });

        return new FeishuCardKitClient(
            options,
            NullLogger<FeishuCardKitClient>.Instance,
            new StubHttpClientFactory(new HttpClient(handler)));
    }

    private static HttpResponseMessage CreateJsonResponse(string json, HttpStatusCode statusCode = HttpStatusCode.OK)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(json)
        };
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHttpMessageHandler(IEnumerable<HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<string> RequestPaths { get; } = [];
        public List<string> RequestQueries { get; } = [];
        public List<string> RequestMethods { get; } = [];
        public List<string> RequestBodies { get; } = [];
        public List<string?> RequestContentTypes { get; } = [];
        public List<DateTimeOffset> RequestTimestamps { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            RequestQueries.Add(request.RequestUri.Query.TrimStart('?'));
            RequestMethods.Add(request.Method.Method);
            RequestContentTypes.Add(request.Content?.Headers.ContentType?.MediaType);
            RequestTimestamps.Add(DateTimeOffset.UtcNow);
            RequestBodies.Add(request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            if (_responses.Count == 0)
            {
                throw new Xunit.Sdk.XunitException($"Unexpected request sent to {request.RequestUri}.");
            }

            return _responses.Dequeue();
        }
    }

    private sealed class TimeoutOnFirstCardUpdateHandler : HttpMessageHandler
    {
        private int _updateCount;

        public List<string> RequestPaths { get; } = [];
        public List<string> RequestBodies { get; } = [];
        public List<string?> RequestContentTypes { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            RequestContentTypes.Add(request.Content?.Headers.ContentType?.MediaType);
            RequestBodies.Add(request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            var path = request.RequestUri!.AbsolutePath;
            if (string.Equals(path, "/open-apis/auth/v3/tenant_access_token/internal", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}""");
            }

            if (request.Method == HttpMethod.Post &&
                string.Equals(path, "/open-apis/cardkit/v1/cards", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}""");
            }

            if (request.Method == HttpMethod.Post &&
                string.Equals(path, "/open-apis/im/v1/messages", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""");
            }

            if (request.Method == HttpMethod.Put &&
                string.Equals(path, "/open-apis/cardkit/v1/cards/card_123", StringComparison.Ordinal))
            {
                _updateCount++;
                if (_updateCount == 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);
                }

                return CreateJsonResponse("""{"code":0}""");
            }

            throw new Xunit.Sdk.XunitException($"Unexpected request sent to {request.RequestUri}.");
        }
    }

    private sealed class TimeoutThenSequenceConflictCardUpdateHandler : HttpMessageHandler
    {
        private int _updateCount;

        public int SuccessfulLogicalUpdates { get; private set; }
        public List<string> RequestPaths { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            RequestBodies.Add(request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            var path = request.RequestUri!.AbsolutePath;
            if (string.Equals(path, "/open-apis/auth/v3/tenant_access_token/internal", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}""");
            }

            if (request.Method == HttpMethod.Post &&
                string.Equals(path, "/open-apis/cardkit/v1/cards", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}""");
            }

            if (request.Method == HttpMethod.Post &&
                string.Equals(path, "/open-apis/im/v1/messages", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""");
            }

            if (request.Method == HttpMethod.Put &&
                string.Equals(path, "/open-apis/cardkit/v1/cards/card_123", StringComparison.Ordinal))
            {
                _updateCount++;
                if (_updateCount == 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);
                }

                if (_updateCount == 2)
                {
                    SuccessfulLogicalUpdates++;
                    return CreateJsonResponse("""{"code":300317,"msg":"sequence number compare failed"}""");
                }

                SuccessfulLogicalUpdates++;
                return CreateJsonResponse("""{"code":0}""");
            }

            throw new Xunit.Sdk.XunitException($"Unexpected request sent to {request.RequestUri}.");
        }
    }

    private sealed class PlainSequenceConflictCardUpdateHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (string.Equals(path, "/open-apis/auth/v3/tenant_access_token/internal", StringComparison.Ordinal))
            {
                return Task.FromResult(CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}"""));
            }

            if (request.Method == HttpMethod.Post &&
                string.Equals(path, "/open-apis/cardkit/v1/cards", StringComparison.Ordinal))
            {
                return Task.FromResult(CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}"""));
            }

            if (request.Method == HttpMethod.Post &&
                string.Equals(path, "/open-apis/im/v1/messages", StringComparison.Ordinal))
            {
                return Task.FromResult(CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}"""));
            }

            if (request.Method == HttpMethod.Put &&
                string.Equals(path, "/open-apis/cardkit/v1/cards/card_123", StringComparison.Ordinal))
            {
                return Task.FromResult(CreateJsonResponse("""{"code":300317,"msg":"sequence number compare failed"}"""));
            }

            throw new Xunit.Sdk.XunitException($"Unexpected request sent to {request.RequestUri}.");
        }
    }

    private sealed class TimeoutThenDuplicateUuidCardUpdateHandler : HttpMessageHandler
    {
        private int _updateCount;

        public int SuccessfulLogicalUpdates { get; private set; }

        public List<string> RequestPaths { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            RequestBodies.Add(request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            var path = request.RequestUri!.AbsolutePath;
            if (string.Equals(path, "/open-apis/auth/v3/tenant_access_token/internal", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}""");
            }

            if (request.Method == HttpMethod.Post &&
                string.Equals(path, "/open-apis/cardkit/v1/cards", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}""");
            }

            if (request.Method == HttpMethod.Post &&
                string.Equals(path, "/open-apis/im/v1/messages", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""");
            }

            if (request.Method == HttpMethod.Put &&
                string.Equals(path, "/open-apis/cardkit/v1/cards/card_123", StringComparison.Ordinal))
            {
                _updateCount++;
                if (_updateCount == 1)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1500), cancellationToken);
                }

                if (_updateCount == 2)
                {
                    SuccessfulLogicalUpdates++;
                    return CreateJsonResponse("""{"code":200770,"msg":"this UUID has been recently consumed"}""");
                }

                SuccessfulLogicalUpdates++;
                return CreateJsonResponse("""{"code":0}""");
            }

            throw new Xunit.Sdk.XunitException($"Unexpected request sent to {request.RequestUri}.");
        }
    }

    private sealed class OverflowThenReducedCardUpdateHandler : HttpMessageHandler
    {
        private int _updateCount;

        public List<string> RequestPaths { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            RequestBodies.Add(request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            var path = request.RequestUri!.AbsolutePath;
            if (string.Equals(path, "/open-apis/auth/v3/tenant_access_token/internal", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}""");
            }

            if (request.Method == HttpMethod.Post &&
                string.Equals(path, "/open-apis/cardkit/v1/cards", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}""");
            }

            if (request.Method == HttpMethod.Post &&
                string.Equals(path, "/open-apis/im/v1/messages", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""");
            }

            if (request.Method == HttpMethod.Put &&
                string.Equals(path, "/open-apis/cardkit/v1/cards/card_123", StringComparison.Ordinal))
            {
                _updateCount++;
                return _updateCount == 1
                    ? CreateJsonResponse("""{"code":200860,"msg":"card over max size"}""")
                    : CreateJsonResponse("""{"code":0}""");
            }

            throw new Xunit.Sdk.XunitException($"Unexpected request sent to {request.RequestUri}.");
        }
    }

    private sealed class OverflowThenReducedCardCreateHandler : HttpMessageHandler
    {
        private int _createCount;

        public List<string> RequestPaths { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            RequestBodies.Add(request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            var path = request.RequestUri!.AbsolutePath;
            if (string.Equals(path, "/open-apis/auth/v3/tenant_access_token/internal", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}""");
            }

            if (request.Method == HttpMethod.Post &&
                string.Equals(path, "/open-apis/cardkit/v1/cards", StringComparison.Ordinal))
            {
                _createCount++;
                return _createCount == 1
                    ? CreateJsonResponse("""{"code":200860,"msg":"card over max size"}""")
                    : CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}""");
            }

            if (request.Method == HttpMethod.Post &&
                string.Equals(path, "/open-apis/im/v1/messages", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""");
            }

            throw new Xunit.Sdk.XunitException($"Unexpected request sent to {request.RequestUri}.");
        }
    }

    private sealed class OverflowRequiresReducedPayloadHandler : HttpMessageHandler
    {
        public List<string> RequestPaths { get; } = [];
        public List<string> RequestBodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri!.AbsolutePath);
            RequestBodies.Add(request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            var path = request.RequestUri!.AbsolutePath;
            if (string.Equals(path, "/open-apis/auth/v3/tenant_access_token/internal", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""{"tenant_access_token":"token-123","expire":7200}""");
            }

            if (request.Method == HttpMethod.Post &&
                string.Equals(path, "/open-apis/cardkit/v1/cards", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""{"code":0,"data":{"card_id":"card_123"}}""");
            }

            if (request.Method == HttpMethod.Post &&
                string.Equals(path, "/open-apis/im/v1/messages", StringComparison.Ordinal))
            {
                return CreateJsonResponse("""{"code":0,"data":{"message_id":"om_stream_success"}}""");
            }

            if (request.Method == HttpMethod.Put &&
                string.Equals(path, "/open-apis/cardkit/v1/cards/card_123", StringComparison.Ordinal))
            {
                using var requestDoc = JsonDocument.Parse(RequestBodies[^1]);
                using var cardDoc = JsonDocument.Parse(requestDoc.RootElement.GetProperty("card").GetProperty("data").GetString()!);
                var elements = cardDoc.RootElement.GetProperty("body").GetProperty("elements");
                var isReducedPayload = elements.GetArrayLength() == 1
                    && elements[0].GetProperty("content").GetString()!.Contains("卡片已精简", StringComparison.Ordinal);

                return isReducedPayload
                    ? CreateJsonResponse("""{"code":0}""")
                    : CreateJsonResponse("""{"code":200860,"msg":"card over max size"}""");
            }

            throw new Xunit.Sdk.XunitException($"Unexpected request sent to {request.RequestUri}.");
        }
    }
}
