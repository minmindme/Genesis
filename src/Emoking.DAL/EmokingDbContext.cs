// ANCHOR: dal.context.db-context - DbConnection 工厂与事务入口
using System;
using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Emoking.DAL;

/// <summary>
/// Emoking DAL 的数据库连接工厂，负责创建 <see cref="SqlConnection"/> 实例。
/// 原型期采用最薄封装：每条仓储方法内部打开连接、执行、关闭。
/// </summary>
/// <remarks>
/// 刻意不引入 Dapper/EF Core 等 ORM——保持"纯寻址、零复杂计算"的哲学。
/// 真实部署时可在 <see cref="CreateConnection"/> 内部加入连接池、重试、熔断。
/// </remarks>
public sealed class EmokingDbContext
{
    // 连接串环境变量名——部署环境通过它注入真实连接串，代码内不含生产凭据。
    public const string ConnectionStringEnvVar = "GENESIS_SQL_CONN";

    // 连接字符串，由 <see cref="ResolveDefaultConnectionString"/> 在构造时解析。
    private readonly string _connectionString;

    /// <summary>
    /// 使用默认连接串初始化：优先读环境变量，未设置时回退到本机 SQL Server 2019 开发库。
    /// </summary>
    public EmokingDbContext()
        : this(ResolveDefaultConnectionString())
    {
        // 默认构造委托给字符串构造，解析逻辑集中在 ResolveDefaultConnectionString。
    }

    /// <summary>
    /// 使用指定连接串初始化。
    /// </summary>
    /// <param name="connectionString">完整 SQL Server 连接串。</param>
    public EmokingDbContext(string connectionString)
    {
        // 保存连接串到私有字段。
        _connectionString = connectionString;
    }

    /// <summary>
    /// 创建并打开一个新的数据库连接。
    /// 原型期每次调用都 new + Open，由调用方负责 Dispose。
    /// </summary>
    /// <returns>已打开的 <see cref="SqlConnection"/>，调用方 using 释放。</returns>
    public SqlConnection CreateConnection()
    {
        // 基于连接串构造新的 SqlConnection 实例。
        var connection = new SqlConnection(_connectionString);

        // 立即打开连接，避免调用方忘记 Open 导致的异常。
        connection.Open();

        // 返回已打开的连接对象。
        return connection;
    }

    /// <summary>
    /// 解析默认连接串：优先读环境变量 <see cref="ConnectionStringEnvVar"/>，
    /// 未设置时回退到本机 SQL Server 2019 开发库（本地原型专用，生产环境必须注入环境变量）。
    /// </summary>
    /// <returns>完整的 SQL Server 连接串。</returns>
    private static string ResolveDefaultConnectionString()
    {
        // 先读环境变量——部署环境用此入口注入真实连接串，代码内不落生产凭据。
        var fromEnv = Environment.GetEnvironmentVariable(ConnectionStringEnvVar);

        // 环境变量非空白时直接采用。
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            // 返回环境变量提供的连接串。
            return fromEnv;
        }

        // 未设置环境变量时回退到本地开发库——仅限原型验证，凭据为用户提供的本机 sa 账号。
        return "Server=localhost;Database=GenesisIso;User Id=sa;Password=123;TrustServerCertificate=True;";
    }
}
