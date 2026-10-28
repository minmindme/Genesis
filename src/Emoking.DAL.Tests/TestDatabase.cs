// ANCHOR: test.infra.test-database - 测试基建：每测试类独立数据库（CREATE/DROP），消灭共享库 + TRUNCATE
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Emoking.DAL;
using Microsoft.Data.SqlClient;

namespace Emoking.DAL.Tests;

/// <summary>
/// 测试数据库作用域——每个测试类创建一个独立数据库，测试结束 DROP。
/// </summary>
/// <remarks>
/// 第 21 轮测试基建解放（方案 A 战役二）：
///   - 类间彻底隔离——不再共享 GenesisIso、不再 TRUNCATE、不再依赖字母序执行；
///   - 并行安全的基础——独立库消除共享状态后，测试类可并行（TestHost 可用后 dotnet test 直接并行）；
///   - 配合 <see cref="EmokingDbContext.ConnectionStringOverrideResolver"/>：
///     测试代码里的 new EmokingDbContext() 全部自动路由到当前独立库，零改动。
/// 诚实边界：连接串解析覆盖点是 static——TestRunner 单线程顺序执行下安全；
/// xunit 并行（TestHost）落地时需换 per-class fixture 注入（CI 落地轮处理）。
/// </remarks>
public static class TestDatabase
{
    // 管理连接（master 库）——CREATE/DROP 用。
    private const string AdminConnectionString =
        "Server=localhost;Database=master;User Id=sa;Password=123;TrustServerCertificate=True;";

    // 服务器连接（不带库名）——新库连接串的基底。
    private const string ServerConnectionString =
        "Server=localhost;User Id=sa;Password=123;TrustServerCertificate=True;";

    // 测试库名前缀。
    private const string DatabasePrefix = "GenesisTest_";

    // 当前测试库名。
    private static string? _currentDatabaseName;

    // 当前测试库连接串——EmokingDbContext 经覆盖解析点读到它。
    public static string? CurrentConnectionString;

    /// <summary>
    /// 静态构造——向 EmokingDbContext 注册连接串覆盖解析（只注册一次，生产环境不受影响）。
    /// </summary>
    static TestDatabase()
    {
        // 注册覆盖解析点——DAL 只依赖委托，不依赖测试程序集。
        EmokingDbContext.ConnectionStringOverrideResolver = () => CurrentConnectionString;
    }

    /// <summary>
    /// 为一个测试类创建独立数据库并注册连接串。
    /// </summary>
    /// <param name="ownerName">拥有者名（测试类名）——库名可读性用。</param>
    /// <returns>表示异步创建操作完成的任务。</returns>
    public static async Task CreateAsync(string ownerName)
    {
        // 库名：前缀 + 类名净化（只留字母数字）+ 短随机——防历史残留库冲突。
        var safeOwner = new string(ownerName.Where(char.IsLetterOrDigit).ToArray());
        _currentDatabaseName = $"{DatabasePrefix}{safeOwner}_{Guid.NewGuid().ToString("N")[..6]}";

        // 打开 master 管理连接。
        using var connection = new SqlConnection(AdminConnectionString);

        // 立即打开。
        connection.Open();

        // 第 1 步（第 26 轮改归属制）：同名残留库先强删——与本次将建的库名精确同名才算残留（随机后缀下不可能，纯兜底）。
        // **不再启动时盲删前缀匹配库**——孤儿清理由 CleanupStaleOrphansAsync（显式 `cleanup-stale-dbs` 参数）承担。

        // 第 2 步：创建全新库。
        await using (var createCommand = connection.CreateCommand())
        {
            // 建库。
            createCommand.CommandText = $"CREATE DATABASE [{_currentDatabaseName}]";

            // 执行。
            await createCommand.ExecuteNonQueryAsync();
        }

        // 第 3 步：注册连接串——此后测试内所有 new EmokingDbContext() 自动路由到本库。
        CurrentConnectionString =
            $"Server=localhost;Database={_currentDatabaseName};User Id=sa;Password=123;TrustServerCertificate=True;";

        // 第 4 步：建表——与 UniversalEntity.sql 同构的精简 DDL（9 列 + 主键 + 2 索引）。
        await ExecuteDdlAsync();

        // 第 5 步（第 26 轮归属制）：写存活标记——清理工具据此判定库的归属与年龄，**不靠启动时盲删**。
        // 注意：必须用**测试库连接**（master 连接会把 marker 建到 master——B42 教训）。
        await using var markerConnection = new SqlConnection(CurrentConnectionString);

        // 打开测试库连接。
        markerConnection.Open();

        // marker 表 + 当前进程标记。
        await using var markerCommand = markerConnection.CreateCommand();

        // marker 表 + 当前进程标记。
        markerCommand.CommandText = $@"
            CREATE TABLE [dbo].[__GenesisRunMarker__] (
                [ProcessId] INT NOT NULL,
                [MachineName] NVARCHAR(100) NOT NULL,
                [CreatedUtc] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
            );
            INSERT INTO [dbo].[__GenesisRunMarker__] ([ProcessId], [MachineName])
            VALUES ({Environment.ProcessId}, '{Environment.MachineName}');";

        // 执行。
        await markerCommand.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 清理僵尸测试库——**显式入口**（`dotnet run -- cleanup-stale-dbs`，人工确认后执行，第 26 轮）。
    /// </summary>
    /// <remarks>
    /// 僵尸判定（双条件）：库内存在本工具的 <c>__GenesisRunMarker__</c> 表 **且** CreatedUtc 距今超过 60 分钟——
    /// 测试库正常寿命在分钟级，超时即进程崩溃未 DROP 的残留。无 marker 表的同前缀库**永不触碰**。
    /// </remarks>
    /// <returns>清理的库数量。</returns>
    public static async Task<int> CleanupStaleOrphansAsync()
    {
        // 打开 master 管理连接。
        using var connection = new SqlConnection(AdminConnectionString);

        // 立即打开。
        connection.Open();

        // 枚举全部同前缀库。
        await using var listCommand = connection.CreateCommand();

        // 查询。
        listCommand.CommandText = "SELECT name FROM sys.databases WHERE name LIKE 'GenesisTest\\_%' ESCAPE '\\'";

        // 读取库清单。
        await using var reader = await listCommand.ExecuteReaderAsync();

        // 收集。
        var candidates = new List<string>();

        // 读取。
        while (await reader.ReadAsync())
        {
            // 收集。
            candidates.Add(reader.GetString(0));
        }

        // 关闭 Reader。
        await reader.CloseAsync();

        // 清理计数。
        var cleaned = 0;

        // 逐库判定。
        foreach (var candidate in candidates)
        {
            // 判定是否僵尸——marker 表存在且过老。
            try
            {
                // 打开库连接（存在性隐含检查）。
                using var dbConnection = new SqlConnection($"Server=localhost;Database={candidate};User Id=sa;Password=123;TrustServerCertificate=True;");

                // 打开。
                dbConnection.Open();

                // 读 marker 年龄。
                await using var ageCommand = dbConnection.CreateCommand();

                // 查询——无 marker 表的库直接抛错进 catch（永不触碰）。
                ageCommand.CommandText = "SELECT DATEDIFF(MINUTE, MAX([CreatedUtc]), SYSUTCDATETIME()) FROM [dbo].[__GenesisRunMarker__]";

                // 年龄（分钟）。
                var ageMinutes = Convert.ToInt32(await ageCommand.ExecuteScalarAsync());

                // 未超阈值——跳过（可能仍在运行）。
                if (ageMinutes < 60)
                {
                    // 跳过。
                    continue;
                }

                // 关闭库连接后删除。
                dbConnection.Close();

                // 强删僵尸库。
                await using var dropCommand = connection.CreateCommand();

                // 删除。
                dropCommand.CommandText =
                    $"EXEC('ALTER DATABASE [{candidate}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{candidate}]')";

                // 执行。
                await dropCommand.ExecuteNonQueryAsync();

                // 计数。
                cleaned++;

                // 输出。
                Console.WriteLine($">>> 已清理僵尸测试库：{candidate}（库龄 {ageMinutes} 分钟）");
            }
            catch
            {
                // 无 marker 表或连接失败——跳过（永不触碰无法确认归属的库）。
            }
        }

        // 返回清理数。
        return cleaned;
    }

    /// <summary>
    /// 在当前测试库中建 UniversalEntity 表——结构与 src/Emoking.DAL/Sql/UniversalEntity.sql 严格一致。
    /// </summary>
    /// <returns>表示异步建表操作完成的任务。</returns>
    private static async Task ExecuteDdlAsync()
    {
        // 打开当前测试库连接。
        using var connection = new SqlConnection(CurrentConnectionString);

        // 立即打开。
        connection.Open();

        // 构造建表命令。
        await using var command = connection.CreateCommand();

        // 精简 DDL——列定义与正式 DDL 一致：9 列 + Id 主键（NONCLUSTERED——聚集索引让位给
        // (TypeId,Role,IsDeleted)，与 UniversalEntity.sql 一致，1902 教训）+ ParentId 非聚集索引。
        command.CommandText = @"
            CREATE TABLE [dbo].[UniversalEntity] (
                [Id] UNIQUEIDENTIFIER NOT NULL CONSTRAINT [PK_UniversalEntity] PRIMARY KEY NONCLUSTERED DEFAULT NEWID(),
                [TypeId] UNIQUEIDENTIFIER NOT NULL,
                [Role] TINYINT NOT NULL,
                [ParentId] UNIQUEIDENTIFIER NULL,
                [Value] NVARCHAR(MAX) NULL,
                [Version] INT NOT NULL DEFAULT 1,
                [IsDeleted] BIT NOT NULL DEFAULT 0,
                [CreatedAt] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                [UpdatedAt] DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME()
            );
            CREATE CLUSTERED INDEX [IX_UniversalEntity_TypeId_Role_IsDeleted]
                ON [dbo].[UniversalEntity] ([TypeId], [Role], [IsDeleted]);
            CREATE NONCLUSTERED INDEX [IX_UniversalEntity_ParentId]
                ON [dbo].[UniversalEntity] ([ParentId]);";

        // 执行建表。
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 删除当前测试库并清空连接串注册。
    /// </summary>
    /// <returns>表示异步删除操作完成的任务。</returns>
    public static async Task DropAsync()
    {
        // 未创建过直接返回。
        if (_currentDatabaseName is null)
        {
            // 无库可删。
            return;
        }

        // 记住库名后立即清空注册——后续误用 EmokingDbContext 会显式回退默认库而不是误连测试库。
        var databaseName = _currentDatabaseName;
        CurrentConnectionString = null;
        _currentDatabaseName = null;

        // 打开 master 管理连接。
        using var connection = new SqlConnection(AdminConnectionString);

        // 立即打开。
        connection.Open();

        // 强断连接后删除测试库。
        await using var command = connection.CreateCommand();

        // DROP 语句。
        command.CommandText =
            $"IF DB_ID(N'{databaseName}') IS NOT NULL " +
            $"EXEC('ALTER DATABASE [{databaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"DROP DATABASE [{databaseName}]')";

        // 执行删除。
        await command.ExecuteNonQueryAsync();
    }
}
