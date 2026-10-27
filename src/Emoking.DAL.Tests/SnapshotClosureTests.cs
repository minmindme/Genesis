// ANCHOR: test.snapshot-closure - 闭合回路验收：快照+加密叠加、模式切换双向语义
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 闭合回路验收——补齐第 11 轮承认但未解决的三处隐患：
/// 快照+加密叠加实测、StorageMode 切换双向语义、快照 schema 边界确认。
/// </summary>
public sealed class SnapshotClosureTests
{
    // Order 实体类型 TypeId——快照模式演示类型。
    private static readonly Guid OrderTypeId = new("33333333-3333-3333-3333-333333333333");

    // User 实体类型 TypeId——图模式类型。
    private static readonly Guid UserTypeId = new("11111111-1111-1111-1111-111111111111");

    // 元类型与引擎指令类型 Guid——与 MetaCache 内部常量一致。
    private static readonly Guid MetaTypeTypeId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid MetaEngineTypeId = new("00000000-0000-0000-0000-000000000002");

    // 测试专用密钥环。
    private static readonly IReadOnlyDictionary<string, byte[]> TestKeyRing =
        new Dictionary<string, byte[]>
        {
            // K1——与创世加密策略行对应。
            ["K1"] = Convert.FromBase64String("qX7wL0pZs2vB8nR4tE6yU1iO9aS3dF5gH7jK2lM4nQ8=")
        };

    /// <summary>
    /// 清空磁盘表——从空池开始。
    /// </summary>
    private static async Task TruncateTableAsync()
    {
        // 打开默认连接。
        using var connection = new EmokingDbContext().CreateConnection();

        // 构造清空命令。
        await using var command = connection.CreateCommand();

        // TRUNCATE 全表。
        command.CommandText = "TRUNCATE TABLE [dbo].[UniversalEntity]";

        // 执行。
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 言出 Order 快照类型：类型行 + StorageMode=Snapshot + CSharpType 绑定。
    /// </summary>
    /// <param name="repository">单表仓储。</param>
    private static async Task SpeakSnapshotOrderAsync(UniversalEntityRepository repository)
    {
        // 元数据行。
        var metaRows = new List<UniversalEntity>
        {
            // 类型定义。
            new(
                Id: OrderTypeId,
                TypeId: MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "PurchaseOrder"),

            // 存储模式：快照。
            new(
                Id: new Guid("00000000-0000-0000-0000-000000000910"),
                TypeId: MetaEngineTypeId,
                Role: UniversalEntity.RoleEngine,
                ParentId: null,
                Value: $"StorageMode=Snapshot|TargetTypeId={OrderTypeId}"),

            // 类名绑定。
            new(
                Id: new Guid("00000000-0000-0000-0000-000000000911"),
                TypeId: MetaEngineTypeId,
                Role: UniversalEntity.RoleEngine,
                ParentId: null,
                Value: $"CSharpType=Emoking.DAL.Tests.PurchaseOrder|TargetTypeId={OrderTypeId}")
        };

        // 写入。
        await repository.InsertBatchAsync(metaRows);
    }

    /// <summary>
    /// 改写 Order 的 StorageMode 指令。
    /// </summary>
    /// <param name="mode">目标模式。</param>
    private static async Task RewriteStorageModeAsync(string mode)
    {
        // 打开默认连接。
        using var connection = new EmokingDbContext().CreateConnection();

        // 构造参数化 UPDATE。
        await using var command = connection.CreateCommand();

        // 更新指令行。
        command.CommandText = "UPDATE [dbo].[UniversalEntity] SET [Value] = @Value, [UpdatedAt] = SYSUTCDATETIME() WHERE [Id] = @Id";

        // 绑定新模式。
        command.Parameters.AddWithValue("@Value", $"StorageMode={mode}|TargetTypeId={OrderTypeId}");

        // 绑定指令行 Id。
        command.Parameters.AddWithValue("@Id", new Guid("00000000-0000-0000-0000-000000000910"));

        // 执行。
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 闭合回路一：快照 + 加密叠加——AGC1 外层包 SNAP1，拖库不可读，读回无损，租户隔离生效。
    /// </summary>
    [Fact]
    public async Task Snapshot_Plus_Encryption_FullClosure()
    {
        // 第 1 步：清池 + 自举 + 言出 Order 快照类型 + 热重载。
        await TruncateTableAsync();
        var diskRepository = new UniversalEntityRepository(new EmokingDbContext());
        var cache = new MetaCache(diskRepository);
        await cache.LoadAsync();
        await SpeakSnapshotOrderAsync(diskRepository);
        await cache.LoadAsync();

        // 第 2 步：策略驱动存储（加密来自池中 Role=5）包装进程内存储。
        var inner = new InMemoryUniversalStore();
        var secured = new PolicyDrivenStore(inner, cache, "tenant-A", TestKeyRing);
        var gateway = new UniversalEntityGateway(cache);

        // 第 3 步：快照拆解并经铁布衫落库。
        var order = new PurchaseOrder
        {
            // 订单号。
            OrderNo = "SO-SECURE-001",
            // 状态。
            State = "Created",
            // 金额。
            Amount = 599.5m,
            // 用户。
            Owner = "user-SEC",
            // 时间。
            CreatedAt = new DateTime(2026, 10, 27, 15, 0, 0, DateTimeKind.Utc)
        };
        var rows = gateway.Decompose(order, OrderTypeId);

        // 快照单行。
        Assert.Single(rows);

        // 落库。
        await secured.BulkInsertAsync(rows);

        // 第 4 步：黑客视角——内层原始行必须是 AGC1 密文。
        var rawRow = await inner.GetByIdAsync(rows[0].Id);

        // 行存在。
        Assert.NotNull(rawRow);

        // AGC1 密文（SNAP1 与 JSON 全部被加密隐藏）。
        Assert.StartsWith("AGC1:", rawRow!.Value);

        // 密文中不允许出现任何明文标记。
        Assert.DoesNotContain("SNAP1:", rawRow.Value);
        Assert.DoesNotContain("OrderNo", rawRow.Value);
        Assert.DoesNotContain("SO-SECURE-001", rawRow.Value);

        // 第 5 步：本租户读回——解密 + 剥 SNAP1 + 强类型反序列化，全链路透明。
        var subgraph = await secured.GetSubGraphAsync(OrderTypeId, UniversalEntity.RoleData);
        var restored = Assert.IsType<PurchaseOrder>(gateway.Assemble(subgraph, OrderTypeId));

        // 全属性还原。
        Assert.Equal("SO-SECURE-001", restored.OrderNo);
        Assert.Equal("Created", restored.State);
        Assert.Equal(599.5m, restored.Amount);
        Assert.Equal(order.CreatedAt, restored.CreatedAt, TimeSpan.FromMilliseconds(1));

        // 第 6 步：租户 B 不可见。
        var tenantB = new PolicyDrivenStore(inner, cache, "tenant-B", TestKeyRing);
        Assert.Null(await tenantB.GetByIdAsync(rows[0].Id));
        Assert.Empty(await tenantB.GetSubGraphAsync(OrderTypeId, UniversalEntity.RoleData));
    }

    /// <summary>
    /// 闭合回路二（Graph → Snapshot）：存量图模式数据在指令切到 Snapshot 后组装显式失败，不静默变砖。
    /// </summary>
    [Fact]
    public async Task ModeSwitch_GraphToSnapshot_OldGraphRows_RaiseMismatch()
    {
        // 第 1 步：清池 + 自举——User 是图模式类型（实体行 + 属性行）。
        await TruncateTableAsync();
        var diskRepository = new UniversalEntityRepository(new EmokingDbContext());
        var cache = new MetaCache(diskRepository);
        await cache.LoadAsync();

        // 第 2 步：写入一个图模式 User（标准拆解轨）。
        var decomposer = new NodeEdgeDecomposer(cache);
        var userRows = decomposer.Decompose(new User
        {
            // Id 由引擎生成。
            Id = Guid.Empty,
            // 姓名。
            Name = "图模式存量用户",
            // 邮箱。
            Email = "graph@genesis.local",
            // 年龄。
            Age = 30,
            // 时间。
            CreatedAt = new DateTime(2026, 10, 27, 16, 0, 0, DateTimeKind.Utc)
        }, UserTypeId);
        var inner = new InMemoryUniversalStore();
        await inner.BulkInsertAsync(userRows);

        // 第 3 步：言出 User 的 StorageMode=Snapshot 指令（数据不迁移，直接切指令）。
        await diskRepository.InsertBatchAsync(new[]
        {
            new UniversalEntity(
                Id: new Guid("00000000-0000-0000-0000-000000000913"),
                TypeId: MetaEngineTypeId,
                Role: UniversalEntity.RoleEngine,
                ParentId: null,
                Value: $"StorageMode=Snapshot|TargetTypeId={UserTypeId}")
        });

        // 热重载。
        cache = await BuildLoadedCacheAsync();

        // 指令已切换。
        Assert.Equal("Snapshot", cache.GetStorageMode(UserTypeId));

        // 第 4 步：快照轨读旧图数据——必须显式失败。
        var gateway = new UniversalEntityGateway(cache);
        var subgraph = await inner.GetSubGraphAsync(UserTypeId, UniversalEntity.RoleData);

        // 组装抛出模式不匹配异常。
        Assert.Throws<StorageModeMismatchException>(
            () => gateway.Assemble(subgraph, UserTypeId));
    }

    /// <summary>
    /// 闭合回路三（Snapshot → Graph）：存量快照数据在指令切到 Graph 后组装显式失败，不静默返回空对象。
    /// </summary>
    [Fact]
    public async Task ModeSwitch_SnapshotToGraph_OldSnapshotRows_RaiseMismatch()
    {
        // 第 1 步：清池 + 自举 + 言出 Order 快照类型。
        await TruncateTableAsync();
        var diskRepository = new UniversalEntityRepository(new EmokingDbContext());
        var cache = new MetaCache(diskRepository);
        await cache.LoadAsync();
        await SpeakSnapshotOrderAsync(diskRepository);
        await cache.LoadAsync();

        // 第 2 步：写入快照订单。
        var inner = new InMemoryUniversalStore();
        var gateway = new UniversalEntityGateway(cache);
        var rows = gateway.Decompose(new PurchaseOrder
        {
            // 订单号。
            OrderNo = "SO-SWITCH-001",
            // 状态。
            State = "Created",
            // 金额。
            Amount = 100m,
            // 用户。
            Owner = "user-SW",
            // 时间。
            CreatedAt = new DateTime(2026, 10, 27, 17, 0, 0, DateTimeKind.Utc)
        }, OrderTypeId);
        await inner.BulkInsertAsync(rows);

        // 第 3 步：把 StorageMode 指令切成 Graph（数据不迁移）。
        await RewriteStorageModeAsync("Graph");
        cache = await BuildLoadedCacheAsync();

        // 指令已切换。
        Assert.Equal("Graph", cache.GetStorageMode(OrderTypeId));

        // 第 4 步：动态图组装器读旧快照行——必须显式失败。
        var dynamicAssembler = new DynamicAssembler(cache);
        var subgraph = await inner.GetSubGraphAsync(OrderTypeId, UniversalEntity.RoleData);

        // 组装抛出模式不匹配异常。
        Assert.Throws<StorageModeMismatchException>(
            () => dynamicAssembler.Assemble(subgraph, OrderTypeId));
    }

    /// <summary>
    /// 构造自举完成的元数据缓存。
    /// </summary>
    private static async Task<MetaCache> BuildLoadedCacheAsync()
    {
        // 磁盘仓储 + 缓存自举。
        var cache = new MetaCache(new UniversalEntityRepository(new EmokingDbContext()));
        await cache.LoadAsync();

        // 返回缓存。
        return cache;
    }
}
