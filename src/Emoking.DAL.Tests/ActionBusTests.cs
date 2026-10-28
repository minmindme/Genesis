// ANCHOR: test.action-bus - 意图总线验收：工作流命令与业务数据动作双路由
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 意图总线验收——一个入口双路由：Role=7 工作流命令 + 内置业务动作（InsertData/GetData）。
/// </summary>
public sealed class ActionBusTests : IAsyncLifetime
{
    // 演示命令行的固定 Id。
    private static readonly Guid CreateEntityCommandRowId = new("00000000-0000-0000-0000-000000000920");

    /// <summary>
    /// 类级初始化——为本测试类创建独立数据库（第 21 轮测试基建，替代共享库 + TRUNCATE）。
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

    /// <summary>
    /// 方法级清池——TRUNCATE 本类独立库的全表（第 21 轮起库名为类专属，但同类内多测试方法
    /// 共享该库，方法级 TRUNCATE 保留以维持方法间隔离；在独立库上执行是安全的）。
    /// </summary>
    private static async Task TruncateTableAsync()
    {
        // 打开当前测试库连接（经连接串覆盖解析点路由）。
        using var connection = new EmokingDbContext().CreateConnection();

        // 构造清空命令。
        await using var command = connection.CreateCommand();

        // TRUNCATE 全表。
        command.CommandText = "TRUNCATE TABLE [dbo].[UniversalEntity]";

        // 执行。
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 构造完整的总线依赖链（自举 + 可选言出 CreateEntity 命令定义）。
    /// </summary>
    /// <param name="withCommand">是否插入 CreateEntity 命令定义行。</param>
    private static async Task<UniversalActionBus> BuildBusAsync(bool withCommand)
    {
        // 磁盘仓储 + 缓存。
        var diskRepository = new UniversalEntityRepository(new EmokingDbContext());

        // 言出命令定义。
        if (withCommand)
        {
            // 命令定义行。
            await diskRepository.InsertBatchAsync(new[]
            {
                new UniversalEntity(
                    Id: CreateEntityCommandRowId,
                    TypeId: MetaCache.MetaEngineTypeId,
                    Role: UniversalEntity.RoleCommand,
                    ParentId: null,
                    Value: "Command=CreateEntity|Args=TypeName,Props,Mode|Steps=InsertTypeMeta;SetStorageMode;ReloadCache")
            });
        }

        // 自举。
        var cache = new MetaCache(diskRepository);
        await cache.LoadAsync();

        // 网关与进程内存储。
        var gateway = new UniversalEntityGateway(cache);
        var store = new InMemoryUniversalStore();
        var executor = new UniversalCommandExecutor(diskRepository, cache, ".");

        // 构造总线。
        return new UniversalActionBus(cache, gateway, store, executor);
    }

    /// <summary>
    /// 路由一：action 命中 Role=7 工作流 → 建类型 + 模式指令 + 热重载。
    /// </summary>
    [Fact]
    public async Task Bus_WorkflowAction_CreatesTypeInPool()
    {
        // 第 1 步：清池 + 言出命令 + 构造总线。
        await TruncateTableAsync();
        var bus = await BuildBusAsync(withCommand: true);

        // 第 2 步：执行工作流动作。
        var report = Assert.IsType<string>(await bus.ExecuteActionAsync("CreateEntity", new Dictionary<string, string>
        {
            // 类型名。
            ["TypeName"] = "BusProduct",
            // 属性。
            ["Props"] = "Title:String,Price:Decimal",
            // 模式。
            ["Mode"] = "Graph"
        }));

        // 报告含命令名。
        Assert.Contains("CreateEntity", report);

        // 第 3 步：断言类型已长出。
        var diskRepository = new UniversalEntityRepository(new EmokingDbContext());
        var metaRows = await diskRepository.GetByTypeIdAndRoleAsync(MetaCache.MetaTypeTypeId, UniversalEntity.RoleMeta);
        var typeRow = metaRows.Single(row => row.Value == "BusProduct");

        // 2 条属性行。
        Assert.Equal(2, metaRows.Count(row => row.ParentId == typeRow.Id));
    }

    /// <summary>
    /// 路由二：InsertData → 网关拆解落库；GetData → 组装读回。快照模式往返。
    /// </summary>
    [Fact]
    public async Task Bus_InsertData_And_GetData_RoundTrip()
    {
        // 第 1 步：清池 + 言出 BusProduct 快照类型。
        await TruncateTableAsync();
        var diskRepository = new UniversalEntityRepository(new EmokingDbContext());

        // Order 快照类型三件套（类型 + Snapshot 模式）。
        var orderTypeId = new Guid("33333333-3333-3333-3333-333333333333");
        await diskRepository.InsertBatchAsync(new[]
        {
            new UniversalEntity(
                Id: orderTypeId,
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "BusProduct"),
            new UniversalEntity(
                Id: new Guid("00000000-0000-0000-0000-000000000930"),
                TypeId: MetaCache.MetaEngineTypeId,
                Role: UniversalEntity.RoleEngine,
                ParentId: null,
                Value: $"StorageMode=Snapshot|TargetTypeId={orderTypeId}")
        });

        // 第 2 步：构造总线。
        var bus = await BuildBusAsync(withCommand: false);

        // 第 3 步：InsertData——业务数据 JSON 直接进总线，总线以 Guid 形态返回实体行 Id。
        var entityId = TestBusHelpers.ExtractId(await bus.ExecuteActionAsync("InsertData", new Dictionary<string, string>
        {
            // 类型名。
            ["TypeName"] = "BusProduct",
            // 数据载荷。
            ["Data"] = "{\"Title\":\"总线订单\",\"Price\":88.8}"
        }));

        // 非空 Id。
        Assert.NotEqual(Guid.Empty, entityId);

        // 第 4 步：GetData——按 Id 读回组装结果。
        var result = await bus.ExecuteActionAsync("GetData", new Dictionary<string, string>
        {
            // 实体行 Id。
            ["EntityId"] = entityId.ToString()
        });

        // 结果 JSON 含标题与价格——用不转义编码器保留中文原文便于断言。
        var resultJson = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions
        {
            // 中文原样输出。
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        Assert.Contains("总线订单", resultJson);
        Assert.Contains("88.8", resultJson);
    }

    /// <summary>
    /// 洞 1 回归护栏：同类型两个实体各自 GetData 必须返回各自的数据。
    /// </summary>
    /// <remarks>
    /// 第 19 轮修复前（类型级 GetSubGraph + 挑第一条实体行），两次 GetData 都会返回先插入的 Alice——
    /// 本测试在该实现下必红（Bob 的结果含 Alice 不含 Bob）。图模式（有属性行）是洞 1 的暴露主场。
    /// </remarks>
    [Fact]
    public async Task Bus_GetData_SameTypeTwoEntities_ReturnsOwnSubgraphOnly()
    {
        // 第 1 步：清池 + 言出 Student 图模式类型（无 StorageMode 指令 = 默认图模式）。
        await TruncateTableAsync();
        var diskRepository = new UniversalEntityRepository(new EmokingDbContext());

        // Student 类型行 + Name 属性行（第 17 轮 "Name:Type" 格式）。
        var studentTypeId = new Guid("55555555-5555-5555-5555-555555555555");
        await diskRepository.InsertBatchAsync(new[]
        {
            new UniversalEntity(
                Id: studentTypeId,
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "Student"),
            new UniversalEntity(
                Id: new Guid("55555555-5555-5555-5555-555555555501"),
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: studentTypeId,
                Value: "Name:String")
        });

        // 第 2 步：构造总线。
        var bus = await BuildBusAsync(withCommand: false);

        // 第 3 步：同类型插入两个实体——Alice 先、Bob 后。
        var aliceId = TestBusHelpers.ExtractId(await bus.ExecuteActionAsync("InsertData", new Dictionary<string, string>
        {
            // 类型名。
            ["TypeName"] = "Student",
            // Alice 载荷。
            ["Data"] = "{\"Name\":\"Alice\"}"
        }));
        var bobId = TestBusHelpers.ExtractId(await bus.ExecuteActionAsync("InsertData", new Dictionary<string, string>
        {
            // 同类型。
            ["TypeName"] = "Student",
            // Bob 载荷。
            ["Data"] = "{\"Name\":\"Bob\"}"
        }));

        // 两个实体行 Id 必须不同。
        Assert.NotEqual(aliceId, bobId);

        // 第 4 步：各自 GetData——序列化用不转义编码器保留中文与原文断言。
        var aliceJson = System.Text.Json.JsonSerializer.Serialize(
            await bus.ExecuteActionAsync("GetData", new Dictionary<string, string> { ["EntityId"] = aliceId.ToString() }),
            new System.Text.Json.JsonSerializerOptions
            {
                // 中文原样输出。
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        var bobJson = System.Text.Json.JsonSerializer.Serialize(
            await bus.ExecuteActionAsync("GetData", new Dictionary<string, string> { ["EntityId"] = bobId.ToString() }),
            new System.Text.Json.JsonSerializerOptions
            {
                // 中文原样输出。
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

        // Alice 只见自己的数据。
        Assert.Contains("Alice", aliceJson);
        Assert.DoesNotContain("Bob", aliceJson);

        // Bob 只见自己的数据——旧实现下这里会看到 Alice，必红。
        Assert.Contains("Bob", bobJson);
        Assert.DoesNotContain("Alice", bobJson);
    }

    /// <summary>
    /// 第 21 轮：UpdateData 图模式级联 Diff——主实体 CAS 递增、旧属性行软删、新属性行在位、历史行留痕。
    /// 全程跑在 SqlServerUniversalStore 真库上（更新语义的真验收）。
    /// </summary>
    [Fact]
    public async Task Bus_UpdateData_GraphMode_CascadesPropertyDiff()
    {
        // 第 1 步：清池 + 言出 Student 图模式类型（Name/Age 两个属性）。
        await TruncateTableAsync();
        var repository = new UniversalEntityRepository(new EmokingDbContext());
        var studentTypeId = new Guid("55555555-5555-5555-5555-555555555555");
        await repository.InsertBatchAsync(new[]
        {
            new UniversalEntity(
                Id: studentTypeId,
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "Student"),
            new UniversalEntity(
                Id: new Guid("55555555-5555-5555-5555-555555555501"),
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: studentTypeId,
                Value: "Name:String"),
            new UniversalEntity(
                Id: new Guid("55555555-5555-5555-5555-555555555502"),
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: studentTypeId,
                Value: "Age:Int")
        });

        // 第 2 步：真库存储构造总线——UpdateData 的验收场景必须落在 SQL（历史行/软删行可查）。
        var cache = new MetaCache(repository);
        await cache.LoadAsync();
        var bus = new UniversalActionBus(
            cache,
            new UniversalEntityGateway(cache),
            new SqlServerUniversalStore(repository),
            new UniversalCommandExecutor(repository, cache, "."));

        // 第 3 步：插入 Alice（Age 18）。
        var aliceId = TestBusHelpers.ExtractId(await bus.ExecuteActionAsync("InsertData", new Dictionary<string, string>
        {
            // 类型名。
            ["TypeName"] = "Student",
            // 初始载荷。
            ["Data"] = "{\"Name\":\"Alice\",\"Age\":18}"
        }));

        // 第 4 步：UpdateData——Name 改值、Age 18 → 20（返回 {Id, Version}——第 23 轮 PUT 契约）。
        var updated = Assert.IsType<Dictionary<string, object>>(await bus.ExecuteActionAsync("UpdateData", new Dictionary<string, string>
        {
            // 实体行 Id。
            ["EntityId"] = aliceId.ToString(),
            // 新载荷。
            ["Data"] = "{\"Name\":\"Alice-2\",\"Age\":20}"
        }));

        // Id 不变（更新不是重插）。
        Assert.Equal(aliceId, updated["Id"]);

        // 返回新版本（乐观锁可见）。
        Assert.Equal(2, updated["Version"]);

        // 第 5 步：GetData 读回——新值生效、旧值消失（含 Id/TypeName 自描述键）。
        var resultJson = System.Text.Json.JsonSerializer.Serialize(
            await bus.ExecuteActionAsync("GetData", new Dictionary<string, string> { ["EntityId"] = aliceId.ToString() }),
            new System.Text.Json.JsonSerializerOptions
            {
                // 中文原样输出。
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
        Assert.Contains("Alice-2", resultJson);
        Assert.Contains("\"Age\":20", resultJson);
        Assert.DoesNotContain("Alice\"", resultJson);
        Assert.DoesNotContain("\"Age\":18", resultJson);

        // 第 6 步：主实体 CAS 生效——Version 递增到 2（真库断言）。
        var updatedRow = await repository.GetByIdAsync(aliceId);
        Assert.NotNull(updatedRow);
        Assert.Equal(2, updatedRow!.Version);

        // 第 7 步：属性行 Diff——指针追逐只见 2 个新属性行（旧行已软删不可见）。
        var children = await repository.GetByParentIdAsync(aliceId);
        Assert.Equal(2, children.Count);
        Assert.Contains(children, row => row.Value == "Alice-2");
        Assert.Contains(children, row => row.Value == "20");

        // 第 8 步：主实体旧行已落成历史行（Role=6，system as history）。
        var history = await repository.GetByTypeIdAndRoleAsync(studentTypeId, UniversalEntity.RoleHistory);
        var historyRow = Assert.Single(history);
        Assert.Equal(aliceId, historyRow.ParentId);
    }

    /// <summary>
    /// 第 21 轮：UpdateData 快照轨直通 CAS——单行覆盖、Version 递增、零写放大（真库验收）。
    /// </summary>
    [Fact]
    public async Task Bus_UpdateData_SnapshotMode_IncrementsVersion()
    {
        // 第 1 步：清池 + 言出 BusProduct 快照类型。
        await TruncateTableAsync();
        var repository = new UniversalEntityRepository(new EmokingDbContext());
        var productTypeId = new Guid("33333333-3333-3333-3333-333333333333");
        await repository.InsertBatchAsync(new[]
        {
            new UniversalEntity(
                Id: productTypeId,
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "BusProduct"),
            new UniversalEntity(
                Id: new Guid("00000000-0000-0000-0000-000000000930"),
                TypeId: MetaCache.MetaEngineTypeId,
                Role: UniversalEntity.RoleEngine,
                ParentId: null,
                Value: $"StorageMode=Snapshot|TargetTypeId={productTypeId}")
        });

        // 第 2 步：真库存储构造总线。
        var cache = new MetaCache(repository);
        await cache.LoadAsync();
        var bus = new UniversalActionBus(
            cache,
            new UniversalEntityGateway(cache),
            new SqlServerUniversalStore(repository),
            new UniversalCommandExecutor(repository, cache, "."));

        // 第 3 步：插入商品。
        var entityId = TestBusHelpers.ExtractId(await bus.ExecuteActionAsync("InsertData", new Dictionary<string, string>
        {
            // 类型名。
            ["TypeName"] = "BusProduct",
            // 初始载荷。
            ["Data"] = "{\"Title\":\"旧标题\",\"Price\":10}"
        }));

        // 第 4 步：UpdateData——整快照覆盖（返回 {Id, Version}——第 23 轮 PUT 契约）。
        var updated = Assert.IsType<Dictionary<string, object>>(await bus.ExecuteActionAsync("UpdateData", new Dictionary<string, string>
        {
            // 实体行 Id。
            ["EntityId"] = entityId.ToString(),
            // 新载荷。
            ["Data"] = "{\"Title\":\"新标题\",\"Price\":99.5}"
        }));

        // Id 不变。
        Assert.Equal(entityId, updated["Id"]);

        // 返回新版本。
        Assert.Equal(2, updated["Version"]);

        // 第 5 步：读回——新值生效、Version 递增、SNAP1 形态（真库断言）。
        // 注意：快照 JSON 默认 Unicode 转义，中文标题以 \uXXXX 形态存储——断言用 ASCII 数字载荷。
        var updatedRow = await repository.GetByIdAsync(entityId);
        Assert.NotNull(updatedRow);
        Assert.Equal(2, updatedRow!.Version);
        Assert.StartsWith("SNAP1:", updatedRow.Value);
        Assert.Contains("99.5", updatedRow.Value);
        Assert.DoesNotContain("\"Price\":10", updatedRow.Value);

        // 第 6 步：历史行留痕（主实体旧行）。
        var history = await repository.GetByTypeIdAndRoleAsync(productTypeId, UniversalEntity.RoleHistory);
        Assert.Single(history);
    }

    /// <summary>
    /// 第 23 轮 P0-1 红测试：带出边的实体 UpdateData 后，正向边必须保留。
    /// </summary>
    /// <remarks>
    /// 现状缺陷：图模式轨的 DeleteMany 删全部子行——边是 Role=2、ParentId=实体行，与属性行同形态，
    /// 会被一并软删（数据损失）。修复后 DeleteMany 按 PropertyTypeIds 过滤只删属性行。
    /// </remarks>
    [Fact]
    public async Task Bus_UpdateData_PreservesOutgoingEdges()
    {
        // 第 1 步：清池 + 言出 Student 图模式类型 + Follows 边类型（正反向类型行 + EdgeType 指令）。
        await TruncateTableAsync();
        var repository = new UniversalEntityRepository(new EmokingDbContext());
        var studentTypeId = new Guid("55555555-5555-5555-5555-555555555555");
        var followsTypeId = new Guid("66666666-6666-6666-6666-666666666666");
        var followsByTypeId = new Guid("66666666-6666-6666-6666-666666666601");
        await repository.InsertBatchAsync(new[]
        {
            // Student 类型 + Name 属性行。
            new UniversalEntity(
                Id: studentTypeId,
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "Student"),
            new UniversalEntity(
                Id: new Guid("55555555-5555-5555-5555-555555555501"),
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: studentTypeId,
                Value: "Name:String"),

            // Follows 正/反向边类型行（ImportSystem 命名约定：反向名 = 正向名 + "By"）。
            new UniversalEntity(
                Id: followsTypeId,
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "Follows"),
            new UniversalEntity(
                Id: followsByTypeId,
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "FollowsBy"),

            // EdgeType 指令（Role=4）。
            new UniversalEntity(
                Id: Guid.NewGuid(),
                TypeId: MetaCache.MetaEngineTypeId,
                Role: UniversalEntity.RoleEngine,
                ParentId: null,
                Value: $"EdgeType=Follows|From={studentTypeId}|To={studentTypeId}"),
            new UniversalEntity(
                Id: Guid.NewGuid(),
                TypeId: MetaCache.MetaEngineTypeId,
                Role: UniversalEntity.RoleEngine,
                ParentId: null,
                Value: $"EdgeType=FollowsBy|From={studentTypeId}|To={studentTypeId}")
        });

        // 第 2 步：真库存储构造总线。
        var cache = new MetaCache(repository);
        await cache.LoadAsync();
        var bus = new UniversalActionBus(
            cache,
            new UniversalEntityGateway(cache),
            new SqlServerUniversalStore(repository),
            new UniversalCommandExecutor(repository, cache, "."));

        // 第 3 步：建 Alice + Bob + Alice→Bob 正向边。
        var aliceId = TestBusHelpers.ExtractId(await bus.ExecuteActionAsync("InsertData", new Dictionary<string, string>
        {
            // 类型名。
            ["TypeName"] = "Student",
            // Alice 载荷。
            ["Data"] = "{\"Name\":\"Alice\"}"
        }));
        var bobId = TestBusHelpers.ExtractId(await bus.ExecuteActionAsync("InsertData", new Dictionary<string, string>
        {
            // 类型名。
            ["TypeName"] = "Student",
            // Bob 载荷。
            ["Data"] = "{\"Name\":\"Bob\"}"
        }));
        await bus.ExecuteActionAsync("InsertEdge", new Dictionary<string, string>
        {
            // 起点。
            ["FromId"] = aliceId.ToString(),
            // 终点。
            ["ToId"] = bobId.ToString(),
            // 边类型名。
            ["EdgeType"] = "Follows"
        });

        // 建边成功基线：Alice 下有 1 行正向边。
        var edgesBefore = (await repository.GetByParentIdAsync(aliceId))
            .Where(row => row.TypeId == followsTypeId && !row.IsDeleted)
            .ToList();
        Assert.Single(edgesBefore);

        // 第 4 步：UpdateData Alice（PUT 换 Name）。
        await bus.ExecuteActionAsync("UpdateData", new Dictionary<string, string>
        {
            // 实体行 Id。
            ["EntityId"] = aliceId.ToString(),
            // 新载荷。
            ["Data"] = "{\"Name\":\"Alice-2\"}"
        });

        // 第 5 步：边必须还在（修复前 DeleteMany 全删子行 → 此处空，必红）。
        var edgesAfter = (await repository.GetByParentIdAsync(aliceId))
            .Where(row => row.TypeId == followsTypeId && !row.IsDeleted)
            .ToList();

        // 边未丢。
        Assert.Single(edgesAfter);
    }

    /// <summary>
    /// 第 23 轮 P0-2 红测试：两客户端各读 v1，先后 UpdateData——第二个必须显式冲突，不得静默覆盖。
    /// </summary>
    /// <remarks>
    /// 现状缺陷：UpdateData 不接受 ExpectedVersion（用引擎内部读到的最新 Version）——
    /// 客户端 B 拿着过期的 v1 更新仍会成功，静默覆盖 A 的写入（引擎 CAS 承诺在 API 层被偷走）。
    /// 修复后：ExpectedVersion 不匹配 → InvalidOperationException。
    /// </remarks>
    [Fact]
    public async Task Bus_UpdateData_ExpectedVersion_SecondWriterMustConflict()
    {
        // 第 1 步：清池 + 言出 Student 图模式类型。
        await TruncateTableAsync();
        var repository = new UniversalEntityRepository(new EmokingDbContext());
        var studentTypeId = new Guid("55555555-5555-5555-5555-555555555555");
        await repository.InsertBatchAsync(new[]
        {
            new UniversalEntity(
                Id: studentTypeId,
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "Student"),
            new UniversalEntity(
                Id: new Guid("55555555-5555-5555-5555-555555555501"),
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: studentTypeId,
                Value: "Name:String")
        });

        // 第 2 步：真库存储构造总线。
        var cache = new MetaCache(repository);
        await cache.LoadAsync();
        var bus = new UniversalActionBus(
            cache,
            new UniversalEntityGateway(cache),
            new SqlServerUniversalStore(repository),
            new UniversalCommandExecutor(repository, cache, "."));

        // 第 3 步：插入 Alice——两个客户端各自读到的都是 v1。
        var aliceId = TestBusHelpers.ExtractId(await bus.ExecuteActionAsync("InsertData", new Dictionary<string, string>
        {
            // 类型名。
            ["TypeName"] = "Student",
            // 初始载荷。
            ["Data"] = "{\"Name\":\"base\"}"
        }));

        // 第 4 步：客户端 A 携带 ExpectedVersion=1 更新——成功。
        await bus.ExecuteActionAsync("UpdateData", new Dictionary<string, string>
        {
            // 实体行 Id。
            ["EntityId"] = aliceId.ToString(),
            // A 的载荷。
            ["Data"] = "{\"Name\":\"from-A\"}",
            // A 读到的版本。
            ["ExpectedVersion"] = "1"
        });

        // 第 5 步：客户端 B 仍持过期 v1 更新——必须冲突（修复前忽略 ExpectedVersion → 成功覆盖，必红）。
        await Assert.ThrowsAsync<ConcurrencyConflictException>(
            () => bus.ExecuteActionAsync("UpdateData", new Dictionary<string, string>
            {
                // 实体行 Id。
                ["EntityId"] = aliceId.ToString(),
                // B 的载荷。
                ["Data"] = "{\"Name\":\"from-B\"}",
                // B 读到的过期版本。
                ["ExpectedVersion"] = "1"
            }));

        // 第 6 步：终态——A 的写入胜出，Version=2（静默覆盖不存在）。
        // 图模式载荷在属性行——A 的 Name 落库、B 的载荷从未落库。
        var finalRow = await repository.GetByIdAsync(aliceId);
        Assert.NotNull(finalRow);
        Assert.Equal(2, finalRow!.Version);

        // A 的属性在位。
        var finalChildren = await repository.GetByParentIdAsync(aliceId);
        Assert.Contains(finalChildren, row => row.Value == "from-A");

        // B 的载荷从未落库（冲突被拒）。
        Assert.DoesNotContain(finalChildren, row => row.Value == "from-B");
    }

    /// <summary>
    /// 第 24 轮红测试（评审 6）：图模式更新必须复制旧属性行为 Role=6 历史行，GetHistory 动作返回审计链。
    /// </summary>
    /// <remarks>
    /// 现状缺陷：图模式历史行是空壳——主实体历史行 Value=null，旧属性行只软删不挂历史版本，
    /// "system as history" 只对快照成立；GetHistory 动作不存在。
    /// </remarks>
    [Fact]
    public async Task Bus_UpdateData_GraphMode_WritesPropertyHistory_GetHistoryReadsIt()
    {
        // 第 1 步：清池 + 言出 Student 图模式类型。
        await TruncateTableAsync();
        var repository = new UniversalEntityRepository(new EmokingDbContext());
        var studentTypeId = new Guid("55555555-5555-5555-5555-555555555555");
        await repository.InsertBatchAsync(new[]
        {
            new UniversalEntity(
                Id: studentTypeId,
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "Student"),
            new UniversalEntity(
                Id: new Guid("55555555-5555-5555-5555-555555555501"),
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: studentTypeId,
                Value: "Name:String")
        });

        // 第 2 步：真库存储构造总线。
        var cache = new MetaCache(repository);
        await cache.LoadAsync();
        var bus = new UniversalActionBus(
            cache,
            new UniversalEntityGateway(cache),
            new SqlServerUniversalStore(repository),
            new UniversalCommandExecutor(repository, cache, "."));

        // 第 3 步：插入 Alice → 更新为 Alice-2。
        var aliceId = TestBusHelpers.ExtractId(await bus.ExecuteActionAsync("InsertData", new Dictionary<string, string>
        {
            // 类型名。
            ["TypeName"] = "Student",
            // 初始载荷。
            ["Data"] = "{\"Name\":\"Alice\"}"
        }));
        await bus.ExecuteActionAsync("UpdateData", new Dictionary<string, string>
        {
            // 实体行 Id。
            ["EntityId"] = aliceId.ToString(),
            // 新载荷。
            ["Data"] = "{\"Name\":\"Alice-2\"}"
        });

        // 第 4 步：旧属性值已复制为 Role=6 历史行（ParentId 指向当前行，Value=旧值，TypeId=属性元 TypeId）。
        // 指针追逐排除 Role=6——用 repository 直查历史行。
        var historyRows = await repository.GetByParentIdAndRoleAsync(aliceId, UniversalEntity.RoleHistory);

        // 旧值 "Alice" 留痕（修复前无历史复制，必红）。
        var nameHistory = historyRows.Where(row => row.Value == "Alice").ToList();
        Assert.Single(nameHistory);

        // 历史行挂在当前实体下且保留属性元 TypeId（可反查属性名）。
        Assert.Equal(aliceId, nameHistory[0].ParentId);

        // 第 5 步：GetHistory 动作返回审计链（修复前动作不存在，必红）。
        var historyJson = System.Text.Json.JsonSerializer.Serialize(
            await bus.ExecuteActionAsync("GetHistory", new Dictionary<string, string> { ["EntityId"] = aliceId.ToString() }),
            new System.Text.Json.JsonSerializerOptions
            {
                // 中文原样输出。
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

        // 审计链含属性名翻译与旧值。
        Assert.Contains("Name", historyJson);
        Assert.Contains("Alice", historyJson);
    }

    /// <summary>
    /// 第 25 轮红测试（评审 P0）：ExpectedVersion 非法值必须显式报错，不得静默回退到当前版本。
    /// </summary>
    /// <remarks>
    /// 现状缺陷：int.TryParse 失败即回退"引擎当前版本"——客户端以为在做乐观锁，实际在裸奔。
    /// </remarks>
    [Fact]
    public async Task Bus_UpdateData_InvalidExpectedVersion_Throws()
    {
        // 第 1 步：清池 + 言出 Student 图模式类型。
        await TruncateTableAsync();
        var repository = new UniversalEntityRepository(new EmokingDbContext());
        var studentTypeId = new Guid("55555555-5555-5555-5555-555555555555");
        await repository.InsertBatchAsync(new[]
        {
            new UniversalEntity(
                Id: studentTypeId,
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "Student"),
            new UniversalEntity(
                Id: new Guid("55555555-5555-5555-5555-555555555501"),
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: studentTypeId,
                Value: "Name:String")
        });

        // 第 2 步：真库总线 + 插入 Alice。
        var cache = new MetaCache(repository);
        await cache.LoadAsync();
        var bus = new UniversalActionBus(
            cache,
            new UniversalEntityGateway(cache),
            new SqlServerUniversalStore(repository),
            new UniversalCommandExecutor(repository, cache, "."));
        var aliceId = Assert.IsType<Guid>(((Dictionary<string, object?>?)await bus.ExecuteActionAsync("InsertData", new Dictionary<string, string>
        {
            // 类型名。
            ["TypeName"] = "Student",
            // 初始载荷。
            ["Data"] = "{\"Name\":\"Alice\"}"
        }))!["Id"]);

        // 第 3 步：非法 ExpectedVersion——显式报错（修复前静默回退到当前版本成功提交，必红）。
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => bus.ExecuteActionAsync("UpdateData", new Dictionary<string, string>
            {
                // 实体行 Id。
                ["EntityId"] = aliceId.ToString(),
                // 载荷。
                ["Data"] = "{\"Name\":\"Hacked\"}",
                // 非法版本。
                ["ExpectedVersion"] = "abc"
            }));

        // 数据未被篡改。
        var intact = await repository.GetByIdAsync(aliceId);
        Assert.Equal(1, intact!.Version);
    }

    /// <summary>
    /// 第 25 轮红测试（评审 P0）：统一信封——InsertData 返回 {Id, TypeName, Version, Data}。
    /// </summary>
    /// <remarks>
    /// 现状缺陷：InsertData 只回裸 Guid——客户端拿不到 TypeName/Version，无法做乐观锁闭环。
    /// </remarks>
    [Fact]
    public async Task Bus_InsertData_ReturnsEnvelope()
    {
        // 第 1 步：清池 + 言出 Student 图模式类型。
        await TruncateTableAsync();
        var repository = new UniversalEntityRepository(new EmokingDbContext());
        var studentTypeId = new Guid("55555555-5555-5555-5555-555555555555");
        await repository.InsertBatchAsync(new[]
        {
            new UniversalEntity(
                Id: studentTypeId,
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "Student"),
            new UniversalEntity(
                Id: new Guid("55555555-5555-5555-5555-555555555501"),
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: studentTypeId,
                Value: "Name:String")
        });

        // 第 2 步：总线。
        var cache = new MetaCache(repository);
        await cache.LoadAsync();
        var bus = new UniversalActionBus(
            cache,
            new UniversalEntityGateway(cache),
            new SqlServerUniversalStore(repository),
            new UniversalCommandExecutor(repository, cache, "."));

        // 第 3 步：InsertData——信封断言（修复前返回裸 Guid，IsType 失败必红）。
        var envelope = Assert.IsType<Dictionary<string, object?>>(await bus.ExecuteActionAsync("InsertData", new Dictionary<string, string>
        {
            // 类型名。
            ["TypeName"] = "Student",
            // 载荷。
            ["Data"] = "{\"Name\":\"Alice\"}"
        }));

        // 四键齐。
        Assert.Equal("Student", envelope["TypeName"]);
        Assert.Equal(1, envelope["Version"]);
        Assert.NotEqual(Guid.Empty, (Guid)envelope["Id"]!);
    }

    /// <summary>
    /// 第 25 轮红测试（评审 P1）：历史版本归属——历史行 Version 记录被覆盖前实体的版本。
    /// </summary>
    /// <remarks>
    /// 现状缺陷：图模式属性行历史 Version 恒为属性行自身版本（1）——与实体版本链脱节。
    /// 修复后第 N 次更新产生的历史行 Version = 被覆盖前实体版本（N-1...）。
    /// </remarks>
    [Fact]
    public async Task Bus_GetHistory_VersionsFollowEntityVersions()
    {
        // 第 1 步：清池 + 言出 Student 图模式类型。
        await TruncateTableAsync();
        var repository = new UniversalEntityRepository(new EmokingDbContext());
        var studentTypeId = new Guid("55555555-5555-5555-5555-555555555555");
        await repository.InsertBatchAsync(new[]
        {
            new UniversalEntity(
                Id: studentTypeId,
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "Student"),
            new UniversalEntity(
                Id: new Guid("55555555-5555-5555-5555-555555555501"),
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: studentTypeId,
                Value: "Name:String")
        });

        // 第 2 步：真库总线 + 插入 + 两次更新（实体版本 1→2→3）。
        var cache = new MetaCache(repository);
        await cache.LoadAsync();
        var bus = new UniversalActionBus(
            cache,
            new UniversalEntityGateway(cache),
            new SqlServerUniversalStore(repository),
            new UniversalCommandExecutor(repository, cache, "."));
        var envelope1 = Assert.IsType<Dictionary<string, object?>>(await bus.ExecuteActionAsync("InsertData", new Dictionary<string, string>
        {
            // 类型名。
            ["TypeName"] = "Student",
            // 初始载荷。
            ["Data"] = "{\"Name\":\"gen-1\"}"
        }));
        var aliceId = (Guid)envelope1["Id"]!;

        // 第一次更新（期望版本 1 → 实体到 2，历史行应归属版本 1）。
        var envelope2 = Assert.IsType<Dictionary<string, object?>>(await bus.ExecuteActionAsync("UpdateData", new Dictionary<string, string>
        {
            // 实体行 Id。
            ["EntityId"] = aliceId.ToString(),
            // 第二代载荷。
            ["Data"] = "{\"Name\":\"gen-2\"}",
            // 乐观锁。
            ["ExpectedVersion"] = "1"
        }));

        // 第二次更新（期望版本 2 → 实体到 3，历史行应归属版本 2）。
        await bus.ExecuteActionAsync("UpdateData", new Dictionary<string, string>
        {
            // 实体行 Id。
            ["EntityId"] = aliceId.ToString(),
            // 第三代载荷。
            ["Data"] = "{\"Name\":\"gen-3\"}",
            // 乐观锁。
            ["ExpectedVersion"] = envelope2["Version"]!.ToString()
        });

        // 第 4 步：审计链——历史行版本与实体版本链一致（修复前恒为 1，必红）。
        var historyJson = System.Text.Json.JsonSerializer.Serialize(
            await bus.ExecuteActionAsync("GetHistory", new Dictionary<string, string> { ["EntityId"] = aliceId.ToString() }),
            new System.Text.Json.JsonSerializerOptions
            {
                // 中文原样输出。
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

        // 两个历史版本都在（1 与 2）。
        Assert.Contains("\"Version\":1", historyJson);
        Assert.Contains("\"Version\":2", historyJson);
    }

    /// <summary>
    /// 无法识别的动作显式失败——总线不猜意图。
    /// </summary>
    [Fact]
    public async Task Bus_UnknownAction_Throws()
    {
        // 构造总线（独立测试库由 InitializeAsync 创建）。
        var bus = await BuildBusAsync(withCommand: false);

        // 未知动作抛错。
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bus.ExecuteActionAsync("TeleportData", new Dictionary<string, string>()));

        // 异常消息点明动作名。
        Assert.Contains("TeleportData", exception.Message);
    }
}
