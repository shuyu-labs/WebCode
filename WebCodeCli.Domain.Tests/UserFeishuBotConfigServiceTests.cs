using System.Linq.Expressions;
using Microsoft.Extensions.Options;
using SqlSugar;
using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Domain.Model.Channels;
using WebCodeCli.Domain.Domain.Service;
using WebCodeCli.Domain.Model;
using WebCodeCli.Domain.Repositories.Base.UserFeishuBotConfig;

namespace WebCodeCli.Domain.Tests;

public class UserFeishuBotConfigServiceTests
{
    [Fact]
    public async Task SaveAsync_WhenLegacyReplyTtsEnabledTrue_MapsToFullReplyDocument()
    {
        var repository = new InMemoryUserFeishuBotConfigRepository();
        var service = CreateService(repository);

        await service.SaveAsync(new UserFeishuBotConfigEntity
        {
            Username = "alice",
            IsEnabled = true,
            AppId = "cli_alice",
            AppSecret = "secret",
            LegacyReplyTtsEnabled = true,
            LegacyReplyTtsVoiceId = "voice-1"
        });

        var stored = await service.GetByUsernameAsync("alice");

        Assert.NotNull(stored);
        Assert.True(stored!.FullReplyDocEnabled);
        Assert.False(stored.FinalReplyDocEnabled);
        Assert.True(stored.LegacyReplyTtsEnabled);
        Assert.Equal(ReplyTtsModes.FullReply, stored.LegacyReplyTtsMode);
        Assert.Null(stored.LegacyReplyTtsVoiceId);
    }

    [Fact]
    public async Task SaveAsync_WhenLegacyReplyTtsEnabledFalse_MapsToReplyDocumentsOff()
    {
        var repository = new InMemoryUserFeishuBotConfigRepository();
        var service = CreateService(repository);

        await service.SaveAsync(new UserFeishuBotConfigEntity
        {
            Username = "alice",
            IsEnabled = true,
            AppId = "cli_alice",
            AppSecret = "secret",
            LegacyReplyTtsEnabled = false
        });

        var stored = await service.GetByUsernameAsync("alice");

        Assert.NotNull(stored);
        Assert.False(stored!.FullReplyDocEnabled);
        Assert.False(stored.FinalReplyDocEnabled);
        Assert.False(stored.LegacyReplyTtsEnabled);
        Assert.Equal(ReplyTtsModes.Off, stored.LegacyReplyTtsMode);
    }

    [Fact]
    public async Task SaveAsync_WhenLegacyReplyTtsModeIsFinalOnly_MapsToFinalReplyDocument()
    {
        var repository = new InMemoryUserFeishuBotConfigRepository();
        var service = CreateService(repository);

        await service.SaveAsync(new UserFeishuBotConfigEntity
        {
            Username = "alice",
            IsEnabled = true,
            AppId = "cli_alice",
            AppSecret = "secret",
            LegacyReplyTtsMode = ReplyTtsModes.FinalOnly,
            LegacyReplyTtsVoiceId = "voice-1"
        });

        var stored = await service.GetByUsernameAsync("alice");

        Assert.NotNull(stored);
        Assert.False(stored!.FullReplyDocEnabled);
        Assert.True(stored.FinalReplyDocEnabled);
        Assert.True(stored.LegacyReplyTtsEnabled);
        Assert.Equal(ReplyTtsModes.FinalOnly, stored.LegacyReplyTtsMode);
        Assert.Null(stored.LegacyReplyTtsVoiceId);
    }

    [Fact]
    public async Task SaveAsync_PersistsReplyDocumentFieldsAndBackfillsLegacyCompatibility()
    {
        var repository = new InMemoryUserFeishuBotConfigRepository();
        var service = CreateService(repository);

        var result = await service.SaveAsync(new UserFeishuBotConfigEntity
        {
            Username = "alice",
            IsEnabled = true,
            AppId = "cli_alice",
            AppSecret = "secret",
            FullReplyDocEnabled = true,
            FinalReplyDocEnabled = true,
            LegacyReplyTtsVoiceId = " voice-1 "
        });

        var stored = await service.GetByUsernameAsync("alice");

        Assert.True(result.Success);
        Assert.NotNull(stored);
        Assert.Equal(1, repository.InsertCallCount);
        Assert.Equal(0, repository.UpdateCallCount);
        Assert.True(stored!.FullReplyDocEnabled);
        Assert.True(stored.FinalReplyDocEnabled);
        Assert.True(stored.LegacyReplyTtsEnabled);
        Assert.Equal(ReplyTtsModes.FullReply, stored.LegacyReplyTtsMode);
        Assert.Null(stored.LegacyReplyTtsVoiceId);
    }

    [Fact]
    public async Task SaveAsync_OverwritesExistingReplyDocumentValues()
    {
        var repository = new InMemoryUserFeishuBotConfigRepository();
        repository.Store(new UserFeishuBotConfigEntity
        {
            Username = "alice",
            IsEnabled = true,
            AppId = "cli_alice",
            AppSecret = "secret",
            FullReplyDocEnabled = true,
            FinalReplyDocEnabled = true,
            LegacyReplyTtsEnabled = true,
            LegacyReplyTtsMode = ReplyTtsModes.FullReply,
            LegacyReplyTtsVoiceId = "old-voice"
        });

        var service = CreateService(repository);

        var result = await service.SaveAsync(new UserFeishuBotConfigEntity
        {
            Username = "alice",
            IsEnabled = true,
            AppId = "cli_alice",
            AppSecret = "secret",
            FullReplyDocEnabled = false,
            FinalReplyDocEnabled = true,
            LegacyReplyTtsVoiceId = "new-voice"
        });

        var stored = await service.GetByUsernameAsync("alice");

        Assert.True(result.Success);
        Assert.NotNull(stored);
        Assert.Equal(0, repository.InsertCallCount);
        Assert.Equal(1, repository.UpdateCallCount);
        Assert.False(stored!.FullReplyDocEnabled);
        Assert.True(stored.FinalReplyDocEnabled);
        Assert.True(stored.LegacyReplyTtsEnabled);
        Assert.Equal(ReplyTtsModes.FinalOnly, stored.LegacyReplyTtsMode);
        Assert.Null(stored.LegacyReplyTtsVoiceId);
    }

    [Fact]
    public async Task SaveAsync_UpdateWithBlankLegacyReplyTtsVoiceId_ClearsPreviousVoice()
    {
        var repository = new InMemoryUserFeishuBotConfigRepository();
        repository.Store(new UserFeishuBotConfigEntity
        {
            Username = "alice",
            IsEnabled = true,
            AppId = "cli_alice",
            AppSecret = "secret",
            FullReplyDocEnabled = true,
            LegacyReplyTtsEnabled = true,
            LegacyReplyTtsMode = ReplyTtsModes.FullReply,
            LegacyReplyTtsVoiceId = "old-voice"
        });

        var service = CreateService(repository);

        var result = await service.SaveAsync(new UserFeishuBotConfigEntity
        {
            Username = "alice",
            IsEnabled = true,
            AppId = "cli_alice",
            AppSecret = "secret",
            FullReplyDocEnabled = true,
            LegacyReplyTtsVoiceId = "   "
        });

        var stored = await service.GetByUsernameAsync("alice");

        Assert.True(result.Success);
        Assert.NotNull(stored);
        Assert.Equal(0, repository.InsertCallCount);
        Assert.Equal(1, repository.UpdateCallCount);
        Assert.True(stored!.FullReplyDocEnabled);
        Assert.False(stored.FinalReplyDocEnabled);
        Assert.Null(stored.LegacyReplyTtsVoiceId);
    }

    [Fact]
    public async Task SaveAsync_NormalizesBlankLegacyReplyTtsVoiceIdToNull()
    {
        var repository = new InMemoryUserFeishuBotConfigRepository();
        var service = CreateService(repository);

        var result = await service.SaveAsync(new UserFeishuBotConfigEntity
        {
            Username = "alice",
            IsEnabled = true,
            AppId = "cli_alice",
            AppSecret = "secret",
            FullReplyDocEnabled = true,
            LegacyReplyTtsVoiceId = "   "
        });

        var stored = await service.GetByUsernameAsync("alice");

        Assert.True(result.Success);
        Assert.NotNull(stored);
        Assert.True(stored!.FullReplyDocEnabled);
        Assert.Null(stored.LegacyReplyTtsVoiceId);
    }

    [Fact]
    public async Task SaveAsync_PersistsDocumentAdminOpenId()
    {
        var repository = new InMemoryUserFeishuBotConfigRepository();
        var service = CreateService(repository);
        var config = new UserFeishuBotConfigEntity
        {
            Username = "alice",
            IsEnabled = true,
            AppId = "cli_alice",
            AppSecret = "secret"
        };
        SetStringProperty(config, "DocumentAdminOpenId", " ou_admin_alice ");

        var result = await service.SaveAsync(config);

        var stored = await service.GetByUsernameAsync("alice");
        Assert.True(result.Success);
        Assert.NotNull(stored);
        Assert.Equal("ou_admin_alice", GetStringProperty(stored!, "DocumentAdminOpenId"));
    }

    private static UserFeishuBotConfigService CreateService(InMemoryUserFeishuBotConfigRepository repository)
    {
        return new UserFeishuBotConfigService(repository, Options.Create(new FeishuOptions()));
    }

    private static string? GetStringProperty(object target, string propertyName)
    {
        return target
            .GetType()
            .GetProperty(propertyName)?
            .GetValue(target) as string;
    }

    private static void SetStringProperty(object target, string propertyName, string value)
    {
        target.GetType().GetProperty(propertyName)?.SetValue(target, value);
    }

    private sealed class InMemoryUserFeishuBotConfigRepository : IUserFeishuBotConfigRepository
    {
        private readonly Dictionary<string, UserFeishuBotConfigEntity> _configs = new(StringComparer.OrdinalIgnoreCase);

        public int InsertCallCount { get; private set; }
        public int UpdateCallCount { get; private set; }

        public void Store(UserFeishuBotConfigEntity entity)
        {
            _configs[entity.Username] = Clone(entity);
        }

        public Task<UserFeishuBotConfigEntity?> GetByUsernameAsync(string username)
        {
            return Task.FromResult(_configs.TryGetValue(username, out var entity) ? Clone(entity) : null);
        }

        public Task<List<UserFeishuBotConfigEntity>> GetListAsync(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression)
        {
            var predicate = whereExpression.Compile();
            return Task.FromResult(_configs.Values.Where(predicate).Select(Clone).ToList());
        }

        public Task<bool> InsertAsync(UserFeishuBotConfigEntity obj)
        {
            if (_configs.ContainsKey(obj.Username))
            {
                return Task.FromResult(false);
            }

            InsertCallCount++;
            Store(obj);
            return Task.FromResult(true);
        }

        public Task<bool> UpdateAsync(UserFeishuBotConfigEntity obj)
        {
            if (!_configs.ContainsKey(obj.Username))
            {
                return Task.FromResult(false);
            }

            UpdateCallCount++;
            Store(obj);
            return Task.FromResult(true);
        }

        public SqlSugarScope GetDB() => throw new NotSupportedException();
        public List<UserFeishuBotConfigEntity> GetList() => throw new NotSupportedException();
        public Task<List<UserFeishuBotConfigEntity>> GetListAsync() => throw new NotSupportedException();
        public List<UserFeishuBotConfigEntity> GetList(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression) => throw new NotSupportedException();
        public int Count(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression) => throw new NotSupportedException();
        public Task<int> CountAsync(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression) => throw new NotSupportedException();
        public PageList<UserFeishuBotConfigEntity> GetPageList(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public PageList<P> GetPageList<P>(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public Task<PageList<UserFeishuBotConfigEntity>> GetPageListAsync(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public Task<PageList<P>> GetPageListAsync<P>(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression, PageModel page) => throw new NotSupportedException();
        public PageList<UserFeishuBotConfigEntity> GetPageList(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression, PageModel page, Expression<Func<UserFeishuBotConfigEntity, object>> orderByExpression = null!, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<UserFeishuBotConfigEntity>> GetPageListAsync(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression, PageModel page, Expression<Func<UserFeishuBotConfigEntity, object>> orderByExpression = null!, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public PageList<P> GetPageList<P>(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression, PageModel page, Expression<Func<UserFeishuBotConfigEntity, object>> orderByExpression = null!, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<P>> GetPageListAsync<P>(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression, PageModel page, Expression<Func<UserFeishuBotConfigEntity, object>> orderByExpression = null!, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public PageList<UserFeishuBotConfigEntity> GetPageList(List<IConditionalModel> conditionalList, PageModel page) => throw new NotSupportedException();
        public Task<PageList<UserFeishuBotConfigEntity>> GetPageListAsync(List<IConditionalModel> conditionalList, PageModel page) => throw new NotSupportedException();
        public PageList<UserFeishuBotConfigEntity> GetPageList(List<IConditionalModel> conditionalList, PageModel page, Expression<Func<UserFeishuBotConfigEntity, object>> orderByExpression = null!, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public Task<PageList<UserFeishuBotConfigEntity>> GetPageListAsync(List<IConditionalModel> conditionalList, PageModel page, Expression<Func<UserFeishuBotConfigEntity, object>> orderByExpression = null!, OrderByType orderByType = OrderByType.Asc) => throw new NotSupportedException();
        public UserFeishuBotConfigEntity GetById(dynamic id) => throw new NotSupportedException();
        public Task<UserFeishuBotConfigEntity> GetByIdAsync(dynamic id) => throw new NotSupportedException();
        public UserFeishuBotConfigEntity GetSingle(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression) => throw new NotSupportedException();
        public Task<UserFeishuBotConfigEntity> GetSingleAsync(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression) => throw new NotSupportedException();
        public UserFeishuBotConfigEntity GetFirst(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression) => throw new NotSupportedException();
        public Task<UserFeishuBotConfigEntity> GetFirstAsync(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression) => throw new NotSupportedException();
        public bool Insert(UserFeishuBotConfigEntity obj) => throw new NotSupportedException();
        public bool InsertRange(List<UserFeishuBotConfigEntity> objs) => throw new NotSupportedException();
        public Task<bool> InsertRangeAsync(List<UserFeishuBotConfigEntity> objs) => throw new NotSupportedException();
        public int InsertReturnIdentity(UserFeishuBotConfigEntity obj) => throw new NotSupportedException();
        public Task<int> InsertReturnIdentityAsync(UserFeishuBotConfigEntity obj) => throw new NotSupportedException();
        public long InsertReturnBigIdentity(UserFeishuBotConfigEntity obj) => throw new NotSupportedException();
        public Task<long> InsertReturnBigIdentityAsync(UserFeishuBotConfigEntity obj) => throw new NotSupportedException();
        public bool DeleteByIds(dynamic[] ids) => throw new NotSupportedException();
        public Task<bool> DeleteByIdsAsync(dynamic[] ids) => throw new NotSupportedException();
        public bool Delete(dynamic id) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(dynamic id) => throw new NotSupportedException();
        public bool Delete(UserFeishuBotConfigEntity obj) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(UserFeishuBotConfigEntity obj) => throw new NotSupportedException();
        public bool Delete(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression) => throw new NotSupportedException();
        public bool Update(UserFeishuBotConfigEntity obj) => throw new NotSupportedException();
        public bool UpdateRange(List<UserFeishuBotConfigEntity> objs) => throw new NotSupportedException();
        public bool InsertOrUpdate(UserFeishuBotConfigEntity obj) => throw new NotSupportedException();
        public Task<bool> InsertOrUpdateAsync(UserFeishuBotConfigEntity obj) => throw new NotSupportedException();
        public Task<bool> UpdateRangeAsync(List<UserFeishuBotConfigEntity> objs) => throw new NotSupportedException();
        public bool IsAny(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression) => throw new NotSupportedException();
        public Task<bool> IsAnyAsync(Expression<Func<UserFeishuBotConfigEntity, bool>> whereExpression) => throw new NotSupportedException();

        private static UserFeishuBotConfigEntity Clone(UserFeishuBotConfigEntity entity)
        {
            var clone = new UserFeishuBotConfigEntity
            {
                Id = entity.Id,
                Username = entity.Username,
                IsEnabled = entity.IsEnabled,
                AutoStartEnabled = entity.AutoStartEnabled,
                AppId = entity.AppId,
                AppSecret = entity.AppSecret,
                EncryptKey = entity.EncryptKey,
                VerificationToken = entity.VerificationToken,
                DefaultCardTitle = entity.DefaultCardTitle,
                ThinkingMessage = entity.ThinkingMessage,
                HttpTimeoutSeconds = entity.HttpTimeoutSeconds,
                StreamingThrottleMs = entity.StreamingThrottleMs,
                FullReplyDocEnabled = entity.FullReplyDocEnabled,
                FinalReplyDocEnabled = entity.FinalReplyDocEnabled,
                LegacyReplyTtsEnabled = entity.LegacyReplyTtsEnabled,
                LegacyReplyTtsMode = entity.LegacyReplyTtsMode,
                LegacyReplyTtsVoiceId = entity.LegacyReplyTtsVoiceId,
                LastStartedAt = entity.LastStartedAt,
                CreatedAt = entity.CreatedAt,
                UpdatedAt = entity.UpdatedAt
            };

            var documentAdminProperty = typeof(UserFeishuBotConfigEntity).GetProperty("DocumentAdminOpenId");
            documentAdminProperty?.SetValue(clone, documentAdminProperty.GetValue(entity));
            return clone;
        }
    }
}
