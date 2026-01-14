namespace WebCodeCli.Domain.Common.Options
{
    public class DBConnectionOption
    {

        /// <summary>
        /// 数据库类型（Sqlite, PostgreSQL, MySql, SqlServer）
        /// </summary>
        public static string DbType { get; set; } = "Sqlite";
        /// <summary>
        /// 数据库连接字符串
        /// </summary>
        public static string ConnectionStrings { get; set; } = "Data Source=WebCodeCli.db";
    }
}
