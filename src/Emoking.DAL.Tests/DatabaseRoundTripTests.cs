// ANCHOR: test.db-round-trip - 真实 SQL Server 往返集成测试
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 真实库集成测试——连接本机 SQL Server 2019（GenesisIso 库），
/// 验证"拆解 → 落库 → 读回 → 挂载"全链路在物理数据库上成立。
/// </summary>
/// <remarks>
/// 前置条件：GenesisIso 数据库已执行 Sql/UniversalEntity.sql 建表。
/// 连接串由 EmokingDbContext 默认解析（环境变量优先，回退本地 GenesisIso）。
/// 每个测试开始前 TRUNCATE 全表，保证可重复运行。
/// </remarks>
public sealed class DatabaseRoundTripTests : IAsyncLifetime
{
    /// <summary>
    /// 类级初始化——为本测试类创建独立数据库（第 21 轮测试基建）。
    /// 类内保留的 TruncateTableAsync 在独立空库上是幂等空操作（历史注释语义不变）。
    /// </summary>
    public async Task InitializeAsync()
    {
        // 建独立库并注册连接串——类内所有 new EmokingDbContext() 自动路由到这里。
        await TestDatabase.CreateAsync(GetType().Name);
    }

    /// <summary>
    /// 类级清理——删除本测试类的独立数据库。
    /// </summary>
    public async Task DisposeAsync()
    {
        // 删库。
        await TestDatabase.DropAsync();
    }
    // User 实体的 Meta TypeId——与 MetaCache 硬编码保持一致。
    private static readonly Guid UserTypeId = new("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// 清空 UniversalEntity 全表——原型库专用，保证测试可重复执行。
    /// </summary>
    /// <returns>表示异步清空操作完成的任务。</returns>
    private static async Task TruncateTableAsync()
    {
        // 用默认连接串打开连接（指向 GenesisIso 开发库）。
        using var connection = new EmokingDbContext().CreateConnection();

        // 构造 TRUNCATE 命令——清空所有行且重置分配状态，比 DELETE 更快。
        await using var command = connection.CreateCommand();

        // 写入清空语句。
        command.CommandText = "TRUNCATE TABLE [dbo].[UniversalEntity]";

        // 执行清空。
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 构造完整依赖链（DbContext → Repository → MetaCache → 拆解器/组装器）。
    /// </summary>
    /// <returns>拆解器、组装器与仓储三元组。</returns>
    private static async Task<(NodeEdgeDecomposer decomposer, ObjectAssembler assembler, UniversalEntityRepository repository)> BuildSutAsync()
    {
        // DbContext 走默认连接串（环境变量优先，回退 GenesisIso）。
        var dbContext = new EmokingDbContext();

        // 单表仓储基于该连接工厂。
        var repository = new UniversalEntityRepository(dbContext);

        // 元数据缓存（原型期硬编码 User 元数据，不依赖库中 Role=1 行）。
        var cache = new MetaCache(repository);

        // 加载元数据到内存。
        await cache.LoadAsync();

        // 拆解器与组装器共享同一份缓存。
        var decomposer = new NodeEdgeDecomposer(cache);
        var assembler = new ObjectAssembler(cache);

        // 返回依赖三元组。
        return (decomposer, assembler, repository);
    }

    /// <summary>
    /// 构造指定序号的测试 User——各字段值掺入序号，便于往返后精确定位断言。
    /// </summary>
    /// <param name="index">测试序号。</param>
    /// <returns>构造完成的 User 实例。</returns>
    private static User BuildUser(int index)
    {
        // 每个字段都掺入序号与中文，覆盖乱码与值定位两类验证。
        return new User
        {
            // Id 留空——拆解时由引擎生成实体行 Guid 并回填。
            Id = Guid.Empty,
            // 姓名带中文，验证中文值落库读回不乱码。
            Name = $"张三_{index}",
            // 邮箱唯一化。
            Email = $"user{index}@genesis.local",
            // 年龄取模防止大数溢出。
            Age = index % 100,
            // 创建时间用固定基准加偏移，保证断言可比。
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(index)
        };
    }

    /// <summary>
    /// 落库 5 行后逐行按主键读回，验证所有列值与拆解结果完全一致。
    /// </summary>
    /// <returns>表示异步测试的任务。</returns>
    [Fact]
    public async Task Db_Insert5Rows_ThenGetById_ReturnsIdenticalRows()
    {
        // 第 1 步：清库 + 构建依赖链。
        await TruncateTableAsync();
        var (decomposer, _, repository) = await BuildSutAsync();

        // 第 2 步：拆解一个 User 为 5 行。
        var original = BuildUser(1);
        var rows = decomposer.Decompose(original, UserTypeId);

        // 行数断言：1 实体行 + 4 属性行。
        Assert.Equal(5, rows.Count);

        // 第 3 步：批量落库。
        var inserted = await repository.InsertBatchAsync(rows);

        // 影响行数应为 5。
        Assert.Equal(5, inserted);

        // 第 4 步：逐行按主键读回并比对每个字段。
        foreach (var row in rows)
        {
            // 按主键读回该行。
            var fetched = await repository.GetByIdAsync(row.Id);

            // 行必须存在。
            Assert.NotNull(fetched);

            // Id 一致。
            Assert.Equal(row.Id, fetched!.Id);

            // TypeId 一致。
            Assert.Equal(row.TypeId, fetched.TypeId);

            // Role 一致。
            Assert.Equal(row.Role, fetched.Role);

            // ParentId 一致（属性行指向实体行，实体行为 null）。
            Assert.Equal(row.ParentId, fetched.ParentId);

            // Value 一致——中文与 ISO 8601 日期字符串均应原样读回。
            Assert.Equal(row.Value, fetched.Value);

            // 软删除标记应为未删除。
            Assert.False(fetched.IsDeleted);
        }
    }

    /// <summary>
    /// 核心验收：落库后走"实体行查询 + ParentId 指针追逐"读回，
    /// 无任何 JOIN，反向挂载出的 User 与原始对象属性完全一致。
    /// </summary>
    /// <returns>表示异步测试的任务。</returns>
    [Fact]
    public async Task Db_RoundTrip_ViaParentIdPointerChase_Assemble_MatchesOriginal()
    {
        // 第 1 步：清库 + 构建依赖链。
        await TruncateTableAsync();
        var (decomposer, assembler, repository) = await BuildSutAsync();

        // 第 2 步：构造原始 User 并落库。
        var original = BuildUser(42);
        var rows = decomposer.Decompose(original, UserTypeId);
        await repository.InsertBatchAsync(rows);

        // 第 3 步：从库中读实体行——按 TypeId + Role 查询，属性行 TypeId 不同不会混入。
        var entityRows = await repository.GetByTypeIdAndRoleAsync(UserTypeId, UniversalEntity.RoleData);

        // 只有 1 条实体行。
        Assert.Single(entityRows);

        // 记录实体行 Id——指针追逐的起点。
        var entityRowId = entityRows[0].Id;

        // 第 4 步：指针追逐——按 ParentId 取全部属性行（SQL 里没有 JOIN，就是这一跳）。
        var propertyRows = await repository.GetByParentIdAsync(entityRowId);

        // 4 条属性行。
        Assert.Equal(4, propertyRows.Count);

        // 第 5 步：合并行集并反向挂载。
        var merged = new List<UniversalEntity>(entityRows.Count + propertyRows.Count);
        merged.AddRange(entityRows);
        merged.AddRange(propertyRows);
        var restored = assembler.Assemble<User>(merged, UserTypeId);

        // 第 6 步：断言——所有属性与原始对象一致。
        // 姓名一致（中文）。
        Assert.Equal(original.Name, restored.Name);

        // 邮箱一致。
        Assert.Equal(original.Email, restored.Email);

        // 年龄一致。
        Assert.Equal(original.Age, restored.Age);

        // 创建时间一致（毫秒精度）。
        Assert.Equal(original.CreatedAt, restored.CreatedAt, TimeSpan.FromMilliseconds(1));

        // 组装后的 Id 必须等于库中实体行 Id。
        Assert.Equal(entityRowId, restored.Id);
    }

    /// <summary>
    /// 软删除行不应被任何查询路径读出——验证 IsDeleted 过滤在三条查询路径上都生效。
    /// </summary>
    /// <returns>表示异步测试的任务。</returns>
    [Fact]
    public async Task Db_SoftDeletedRow_IsFilteredFromAllQueries()
    {
        // 第 1 步：清库 + 构建依赖链。
        await TruncateTableAsync();
        var (decomposer, _, repository) = await BuildSutAsync();

        // 第 2 步：拆解并落库一个 User。
        var original = BuildUser(7);
        var rows = decomposer.Decompose(original, UserTypeId);
        await repository.InsertBatchAsync(rows);

        // 第 3 步：把该 User 的全部 5 行（实体行 + 属性行）逐一软删除。
        // 注意：原型期没有级联软删入口，引擎层删除语义是"逐行标记"——级联策略列为旁支。
        foreach (var row in rows)
        {
            // 逐行软删除。
            var deleted = await repository.SoftDeleteAsync(row.Id);

            // 每行都必须命中。
            Assert.True(deleted);
        }

        // 第 4 步：三条查询路径都不应返回任何行。
        // 主键查询返回 null。
        Assert.Null(await repository.GetByIdAsync(rows[0].Id));

        // TypeId + Role 查询为空。
        var byType = await repository.GetByTypeIdAndRoleAsync(UserTypeId, UniversalEntity.RoleData);
        Assert.Empty(byType);

        // ParentId 指针追逐查询为空——属性行已随实体行一起软删。
        var entityRowId = rows.Single(row => row.ParentId == null).Id;
        var byParent = await repository.GetByParentIdAsync(entityRowId);
        Assert.Empty(byParent);
    }

    /// <summary>
    /// 写入同化 + 读取同化的核心验收：BulkInsert 单次往返落库，
    /// 子图预取一条 SQL 拉回整块图切片，内存组装后属性与原始对象一致。
    /// </summary>
    /// <returns>表示异步测试的任务。</returns>
    [Fact]
    public async Task Db_BulkInsert_ThenSubgraphFetch_Assemble_MatchesOriginal()
    {
        // 第 1 步：清库 + 构建依赖链。
        await TruncateTableAsync();
        var (decomposer, assembler, repository) = await BuildSutAsync();

        // 第 2 步：构造原始 User 并拆解。
        var original = BuildUser(9);
        var rows = decomposer.Decompose(original, UserTypeId);

        // 第 3 步：BulkInsert 单次往返整包写入。
        var inserted = await repository.BulkInsertAsync(rows);

        // 5 行全部写入。
        Assert.Equal(5, inserted);

        // 第 4 步：子图预取——一条 SQL 拉回实体行 + 属性行的完整图切片。
        var subgraph = await repository.GetByTypeIdWithChildrenAsync(UserTypeId, UniversalEntity.RoleData);

        // 子图应有 5 行：1 实体行 + 4 属性行。
        Assert.Equal(5, subgraph.Count);

        // 第 5 步：内存展开——实体行直接用，属性行按 ParentId 挂到实体行 Id 下。
        var entityRow = subgraph.Single(row => row.ParentId == null);
        var propertyRows = subgraph.Where(row => row.ParentId == entityRow.Id).ToList();

        // 4 条属性行。
        Assert.Equal(4, propertyRows.Count);

        // 第 6 步：组装并断言全部属性一致。
        var restored = assembler.Assemble<User>(subgraph, UserTypeId);

        // 姓名一致。
        Assert.Equal(original.Name, restored.Name);

        // 邮箱一致。
        Assert.Equal(original.Email, restored.Email);

        // 年龄一致。
        Assert.Equal(original.Age, restored.Age);

        // 时间一致（毫秒精度）。
        Assert.Equal(original.CreatedAt, restored.CreatedAt, TimeSpan.FromMilliseconds(1));

        // Id 等于库中实体行 Id。
        Assert.Equal(entityRow.Id, restored.Id);
    }

    /// <summary>
    /// 按 ParentId 集合批量取子节点——多个实体的属性行一次往返全部拉回。
    /// </summary>
    /// <returns>表示异步测试的任务。</returns>
    [Fact]
    public async Task Db_GetByParentIds_BatchFetch_ReturnsAllPropertyRows()
    {
        // 第 1 步：清库 + 构建依赖链。
        await TruncateTableAsync();
        var (decomposer, _, repository) = await BuildSutAsync();

        // 第 2 步：拆解两个 User 并全部 Bulk 写入。
        var rowsA = decomposer.Decompose(BuildUser(1), UserTypeId);
        var rowsB = decomposer.Decompose(BuildUser(2), UserTypeId);
        var allRows = new List<UniversalEntity>(rowsA.Count + rowsB.Count);
        allRows.AddRange(rowsA);
        allRows.AddRange(rowsB);
        await repository.BulkInsertAsync(allRows);

        // 第 3 步：收集两个实体行 Id 作为父节点集合。
        var entityIds = allRows
            .Where(row => row.ParentId == null)
            .Select(row => row.Id)
            .ToList();

        // 两个实体行。
        Assert.Equal(2, entityIds.Count);

        // 第 4 步：单次往返批量取回两个实体的属性行。
        var propertyRows = await repository.GetByParentIdsAsync(entityIds);

        // 2 实体 × 4 属性 = 8 条。
        Assert.Equal(8, propertyRows.Count);

        // 每条属性行的 ParentId 都必须落在父节点集合内。
        Assert.All(propertyRows, row => Assert.Contains(row.ParentId!.Value, entityIds));
    }
}
