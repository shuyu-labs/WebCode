using WebCodeCli.Domain.Common.Options;
using WebCodeCli.Domain.Utils;
using WebCodeCli.Domain.Domain.Service.Channels;
using WebCodeCli.Repositories.Demo;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SqlSugar;
using System.Reflection;
using System;
using WebCodeCli.Domain.Domain.Service;
using WebCodeCli.Domain.Repositories.Base.ChatSession;

namespace WebCodeCli.Domain.Common.Extensions
{
    public static class ServiceCollectionExtensions
    {
        /// <summary>
        /// 从程序集中加载类型并添加到容器中
        /// </summary>
        /// <param name="services">容器</param>
        /// <param name="assemblies">程序集集合</param>
        /// <returns></returns>
        public static IServiceCollection AddServicesFromAssemblies(this IServiceCollection services, params string[] assemblies)
        {
            Type attributeType = typeof(ServiceDescriptionAttribute);
            //var refAssembyNames = Assembly.GetExecutingAssembly().GetReferencedAssemblies();
            foreach (var item in assemblies)
            {
                Assembly assembly = Assembly.Load(item);

                var types = assembly.GetTypes();

                foreach (var classType in types)
                {
                    if (!classType.IsAbstract && classType.IsClass && classType.IsDefined(attributeType, false))
                    {
                        ServiceDescriptionAttribute serviceAttribute = (classType.GetCustomAttribute(attributeType) as ServiceDescriptionAttribute);
                        switch (serviceAttribute.Lifetime)
                        {
                            case ServiceLifetime.Scoped:
                                services.AddScoped(serviceAttribute.ServiceType, classType);
                                break;

                            case ServiceLifetime.Singleton:
                                services.AddSingleton(serviceAttribute.ServiceType, classType);
                                break;

                            case ServiceLifetime.Transient:
                                services.AddTransient(serviceAttribute.ServiceType, classType);
                                break;
                        }
                    }
                }
            }

            services.AddScoped<IAttachmentStagingService, AttachmentStagingService>();
            services.AddScoped<IMessageSubmissionService, MessageSubmissionService>();
            services.AddScoped<IChatMessageAttachmentRepository, ChatMessageAttachmentRepository>();

            return services;
        }

        /// <summary>
        /// 添加飞书渠道服务
        /// </summary>
        /// <param name="services">服务集合</param>
        /// <param name="configuration">配置</param>
        /// <returns>服务集合</returns>
        public static IServiceCollection AddFeishuChannel(
            this IServiceCollection services,
            IConfiguration configuration)
        {
            var feishuSection = configuration.GetSection("Feishu");
            var feishuReplyTtsSection = configuration.GetSection("FeishuReplyTts");

            // 绑定配置选项
            services.Configure<FeishuOptions>(feishuSection);
            services.Configure<FeishuReplyTtsOptions>(feishuReplyTtsSection);

            // 注册 HttpClient 工厂（用于 CardKit API 调用）
            services.AddHttpClient("FeishuClient")
                .ConfigureHttpClient(client =>
                {
                    client.Timeout = TimeSpan.FromSeconds(30);
                });

            // 显式注册卡片动作事件处理器（让 FeishuNetSdk 能够发现它）
            services.AddSingleton<FeishuCardActionHandler>();

            // 注册消息处理器（Singleton，需要在整个应用生命周期内保持实例）
            services.AddSingleton<FeishuMessageHandler>();

            // 注册本地 CLI 配置检测服务（Singleton）
            services.AddSingleton<ILocalCliConfigDetector, LocalCliConfigDetector>();

            // 共享 Codex app-server 会话管理器，避免不同服务读取到不同的 turn 状态
            services.AddSingleton<ICodexAppServerSessionManager, CodexAppServerSessionManager>();

            // 注册 CardKit 客户端（Singleton）
            services.AddSingleton<IFeishuCardKitClient, FeishuCardKitClient>();

            // 注册主服务（Singleton，同时作为 HostedService 运行）
            services.AddSingleton<IFeishuChannelService, FeishuChannelService>();
            services.AddHostedService<FeishuChannelService>();

            // 注册帮助功能服务
            services.AddSingleton<FeishuCommandService>();
            services.AddSingleton<FeishuHelpCardBuilder>();
            services.AddSingleton<FeishuAttachmentDraftCardBuilder>();
            services.AddSingleton<FeishuCardActionService>();
            services.AddSingleton<IFeishuAttachmentDraftService, FeishuAttachmentDraftService>();
            services.AddSingleton<ReplyTtsStorageRootResolver>();

            services.AddSingleton<IUserFeishuBotRuntimeService, UserFeishuBotRuntimeService>();
            services.AddHostedService<ReplyTtsStartupHostedService>();
            services.AddHostedService(sp => (UserFeishuBotRuntimeService)sp.GetRequiredService<IUserFeishuBotRuntimeService>());

            return services;
        }

    }
}
