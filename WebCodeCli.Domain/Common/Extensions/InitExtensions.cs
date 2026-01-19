using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using SqlSugar;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using WebCodeCli.Domain.Repositories.Base.SessionShare;
using WebCodeCli.Repositories.Demo;

namespace WebCodeCli.Domain.Common.Extensions
{
    public static class InitExtensions
    {

        /// <summary>
        /// 根据程序集中的实体类创建数据库表
        /// </summary>
        /// <param name="services"></param>
        public static void CodeFirst(this WebApplication app)
        {
            using (var scope = app.Services.CreateScope())
            {
                // 获取仓储服务
                var _repository = scope.ServiceProvider.GetRequiredService<ISettingsRepository>();
                //// 创建数据库（如果不存在）
                //_repository.GetDB().DbMaintenance.CreateDatabase();
                
                // 扫描多个程序集中的实体类
                var assemblies = new[]
                {
                    Assembly.GetExecutingAssembly(), // 当前程序集
                    typeof(WebCodeCli.Domain.Domain.Model.CliToolEnvironmentVariable).Assembly // Domain程序集
                };
                
                // 在所有程序集中查找具有[SugarTable]特性的类
                var entityTypes = assemblies
                    .SelectMany(assembly => assembly.GetTypes())
                    .Where(type => TypeIsEntity(type))
                    .Distinct();
                
                // 为每个找到的类型初始化数据库表
                foreach (var type in entityTypes)
                {
                    try
                    {
                        _repository.GetDB().CodeFirst.InitTables(type);
                        Console.WriteLine($"成功初始化数据库表: {type.Name}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"初始化数据库表失败 {type.Name}: {ex.Message}");
                    }
                }
                
                // 额外确保CLI工具相关表已创建
                try
                {
                    Console.WriteLine("开始初始化 CLI 工具环境变量表...");
                    _repository.GetDB().CodeFirst.InitTables<WebCodeCli.Domain.Domain.Model.CliToolEnvironmentVariable>();
                    Console.WriteLine("CLI 工具环境变量表初始化成功");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"初始化CLI工具表失败: {ex.Message}");
                }
                
                // 确保会话分享表已创建
                try
                {
                    Console.WriteLine("开始初始化会话分享表...");
                    _repository.GetDB().CodeFirst.InitTables<SessionShare>();
                    Console.WriteLine("会话分享表初始化成功");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"初始化会话分享表失败: {ex.Message}");
                }
            }
        }

        static bool TypeIsEntity(Type type)
        {
            // 检查类型是否具有SugarTable特性
            return type.GetCustomAttributes(typeof(SugarTable), inherit: false).Length > 0;
        }
    }
}
