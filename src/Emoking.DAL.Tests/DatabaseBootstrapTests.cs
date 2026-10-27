// ANCHOR: test.db-bootstrap - 引擎自举集成测试（元数据真实落库 + 空池自播种）
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 引擎自举集成测试——验证 MetaCache 从 UniversalEntity 表加载 Role=1/4 创世法则，
/// 且数据池为空时自动幂等播种。引擎不再依赖硬编码元数据。
/// </summary>
/// <remarks>
/// 前置条件：GenesisIso 数据库已建表。每个测试前 TRUNCATE 全表，从空池开始自举。
/// </remarks>
public sealed class DatabaseBootstrapTests
{
    // User 实体类型的 Meta TypeId——与种子行 Id 一致。
    private static readonly Guid UserTypeId = new("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// 清空 UniversalEntity 全表——从"宇宙大爆炸前"的状态开始。
    /// </summary>
    /// <returns>表示异步清空操作完成的任务。</returns>
    private static async Task TruncateTableAsync()
    {
        // 用默认连接串打开连接。
        using var connection = new EmokingDbContext().CreateConnection();

        // 构造清空命令。
        await using var command = connection.CreateCommand();

        // TRUNCATE 清空全部行。
        command.CommandText = "TRUNCATE TABLE [dbo].[UniversalEntity]";

        // 执行清空。
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 构造依赖链（DbContext → Repository → MetaCache）。
    /// </summary>
    /// <returns>仓储与元数据缓存二元组。</returns>
    private static (UniversalEntityRepository repository, MetaCache cache) BuildSut()
    {
        // DbContext 走默认连接串。
        var dbContext = new EmokingDbContext();

        // 单表仓储。
        var repository = new UniversalEntityRepository(dbContext);

        // 元数据缓存。
        var cache = new MetaCache(repository);

        // 返回依赖对。
        return (repository, cache);
    }

    /// <summary>
    /// 空池自举：TRUNCATE 后 LoadAsync 应自动播种 6 行创世数据并成功构建内存元数据。
    /// </summary>
    /// <returns>表示异步测试的任务。</returns>
    [Fact]
    public async Task Bootstrap_EmptyPool_LoadAsync_SeedsAndBuildsMeta()
    {
        // 第 1 步：清空全表——模拟从未初始化的新数据库。
        await TruncateTableAsync();
        var (repository, cache) = BuildSut();

        // 第 2 步：自举——引擎从空池里长出自己的灵魂。
        await cache.LoadAsync();

        // 第 3 步：断言库中已播种 10 行创世数据。
        var metaRows = await repository.GetByRolesAsync([UniversalEntity.RoleMeta, UniversalEntity.RoleEngine]);

        // 1 类型 + 4 属性 + 2 指令 + 3 安全策略 = 10 行（Role=5 策略行另行拉取，此处仅统计 Role=1/4）。
        // 注意：Role=1/4 共 7 行；Role=5 的 3 行策略由 GetSecurityPolicy 验证。
        Assert.Equal(7, metaRows.Count);

        // 加密策略已配置。
        var encryptPolicy = cache.GetSecurityPolicy("EncryptValue");
        Assert.NotNull(encryptPolicy);
        Assert.Equal("AesGcm", encryptPolicy!["Algo"]);

        // 租户隔离策略已配置。
        Assert.NotNull(cache.GetSecurityPolicy("TenantIsolation"));

        // 限流策略已配置。
        Assert.NotNull(cache.GetSecurityPolicy("RateLimit"));

        // 第 4 步：断言内存元数据结构完整。
        // User 类型已注册。
        var userType = cache.GetTypeByTypeId(UserTypeId);
        Assert.NotNull(userType);

        // 类型名正确。
        Assert.Equal("User", userType!.TypeName);

        // 四个属性映射正确。
        Assert.Equal(4, userType.PropertyTypeIds.Count);
        Assert.True(userType.PropertyTypeIds.ContainsKey("Name"));
        Assert.True(userType.PropertyTypeIds.ContainsKey("Email"));
        Assert.True(userType.PropertyTypeIds.ContainsKey("Age"));
        Assert.True(userType.PropertyTypeIds.ContainsKey("CreatedAt"));

        // 引擎路由正确。
        Assert.Equal("UserRepository", cache.GetEngineRouteTarget(UserTypeId));
    }

    /// <summary>
    /// 幂等验证：连续两次 LoadAsync 不应重复播种，元数据行数保持 6。
    /// </summary>
    /// <returns>表示异步测试的任务。</returns>
    [Fact]
    public async Task Bootstrap_SecondLoad_DoesNotReseed()
    {
        // 第 1 步：清空 + 首次自举（播种）。
        await TruncateTableAsync();
        var (repository, cache) = BuildSut();
        await cache.LoadAsync();

        // 第 2 步：第二次加载——库里已有元数据，不应再播种。
        await cache.LoadAsync();

        // 第 3 步：断言行数仍为 7（Role=1/4 没有翻倍）。
        var metaRows = await repository.GetByRolesAsync([UniversalEntity.RoleMeta, UniversalEntity.RoleEngine]);

        // 7 行不变。
        Assert.Equal(7, metaRows.Count);

        // Role=5 安全策略同样没有重复（仍可解析且唯一）。
        Assert.NotNull(cache.GetSecurityPolicy("EncryptValue"));
        Assert.NotNull(cache.GetSecurityPolicy("TenantIsolation"));
        Assert.NotNull(cache.GetSecurityPolicy("RateLimit"));
    }

    /// <summary>
    /// 终极链路验证：纯靠库中自举出的元数据，完成 User 的拆解 → Bulk 写入 → 子图预取 → 组装往返。
    /// </summary>
    /// <returns>表示异步测试的任务。</returns>
    [Fact]
    public async Task Bootstrap_SeededMetadata_SupportsFullRoundTrip()
    {
        // 第 1 步：空池自举。
        await TruncateTableAsync();
        var (repository, cache) = BuildSut();
        await cache.LoadAsync();

        // 第 2 步：用自举出的元数据构造拆解器与组装器。
        var decomposer = new NodeEdgeDecomposer(cache);
        var assembler = new ObjectAssembler(cache);

        // 第 3 步：构造原始 User 并拆解。
        var original = new User
        {
            // Id 留空由引擎生成。
            Id = Guid.Empty,
            // 中文字段验证。
            Name = "自举张三",
            // 邮箱。
            Email = "bootstrap@genesis.local",
            // 年龄。
            Age = 42,
            // 时间。
            CreatedAt = new DateTime(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc)
        };
        var rows = decomposer.Decompose(original, UserTypeId);

        // 5 行。
        Assert.Equal(5, rows.Count);

        // 第 4 步：Bulk 写入单次往返落库。
        await repository.BulkInsertAsync(rows);

        // 第 5 步：子图预取单次往返拉回。
        var subgraph = await repository.GetByTypeIdWithChildrenAsync(UserTypeId, UniversalEntity.RoleData);

        // 5 行子图。
        Assert.Equal(5, subgraph.Count);

        // 第 6 步：组装并断言属性一致。
        var restored = assembler.Assemble<User>(subgraph, UserTypeId);

        // 姓名一致。
        Assert.Equal(original.Name, restored.Name);

        // 邮箱一致。
        Assert.Equal(original.Email, restored.Email);

        // 年龄一致。
        Assert.Equal(original.Age, restored.Age);

        // 时间一致。
        Assert.Equal(original.CreatedAt, restored.CreatedAt, TimeSpan.FromMilliseconds(1));

        // 组装出的 Id 等于库中实体行 Id。
        Assert.Equal(subgraph.Single(row => row.ParentId == null).Id, restored.Id);
    }
}
