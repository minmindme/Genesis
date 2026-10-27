// ANCHOR: test.dynamic-entity - 言出法随：动态实体与类名同化的闭环演示
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 动态实体闭环演示——引擎接住一个它从未见过的实体类型，全程零 C# 类、零编译、零重启。
/// </summary>
public sealed class DynamicEntityTests
{
    // User 实体类型的 Meta TypeId——与创世行一致。
    private static readonly Guid UserTypeId = new("11111111-1111-1111-1111-111111111111");

    // Order 实体类型的 Meta TypeId——演示用"从未存在"的类型。
    private static readonly Guid OrderTypeId = new("22222222-2222-2222-2222-222222222222");

    // Order 属性 TypeId——OrderNo / Amount / OrderDate。
    private static readonly Guid OrderNoTypeId = new("22222222-2222-2222-2222-222222222201");
    private static readonly Guid OrderAmountTypeId = new("22222222-2222-2222-2222-222222222202");
    private static readonly Guid OrderDateTypeId = new("22222222-2222-2222-2222-222222222203");

    // 元类型与引擎指令类型 Guid——与 MetaCache 内部常量一致。
    private static readonly Guid MetaTypeTypeId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid MetaEngineTypeId = new("00000000-0000-0000-0000-000000000002");

    // CSharpType 指令行的固定 Id。
    private static readonly Guid CSharpTypeRowId = new("00000000-0000-0000-0000-000000000903");

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
    /// 向池中"言出"Order 类型——4 行 Role=1 元数据，**C# 程序集里不存在 Order 类**。
    /// </summary>
    /// <param name="repository">单表仓储。</param>
    private static async Task SpeakOrderTypeIntoExistenceAsync(UniversalEntityRepository repository)
    {
        // 组装 Order 类型定义的元数据行——与 User 创世行完全同构。
        var orderMetaRows = new List<UniversalEntity>
        {
            // 类型行。
            new(
                Id: OrderTypeId,
                TypeId: MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "Order"),

            // OrderNo 属性。
            new(
                Id: OrderNoTypeId,
                TypeId: MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: OrderTypeId,
                Value: "OrderNo"),

            // Amount 属性。
            new(
                Id: OrderAmountTypeId,
                TypeId: MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: OrderTypeId,
                Value: "Amount"),

            // OrderDate 属性。
            new(
                Id: OrderDateTypeId,
                TypeId: MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: OrderTypeId,
                Value: "OrderDate")
        };

        // 写入池中——这就是"一句话建业务"的数据面。
        await repository.InsertBatchAsync(orderMetaRows);
    }

    /// <summary>
    /// 核心闭环：插元数据 → 热重载 → dynamic 点写 → 拆解落库 → 子图读回 → dynamic 点读。
    /// 全程没有任何 Order C# 类参与。
    /// </summary>
    [Fact]
    public async Task DynamicOrder_FullLifecycle_WithoutAnyOrderClass()
    {
        // 第 1 步：清池 + 自举（User 创世）。
        await TruncateTableAsync();
        var diskRepository = new UniversalEntityRepository(new EmokingDbContext());
        var cache = new MetaCache(diskRepository);
        await cache.LoadAsync();

        // 第 2 步：往池子里"言出"Order 类型——4 行元数据。
        await SpeakOrderTypeIntoExistenceAsync(diskRepository);

        // 第 3 步：热重载——引擎瞬间学会了 Order。
        await cache.LoadAsync();

        // Order 类型已注册。
        var orderMeta = cache.GetTypeByTypeId(OrderTypeId);
        Assert.NotNull(orderMeta);
        Assert.Equal("Order", orderMeta!.TypeName);
        Assert.Equal(3, orderMeta.PropertyTypeIds.Count);

        // 第 4 步：构造引擎依赖——网关 + 进程内存储。
        var gateway = new UniversalEntityGateway(cache);
        var store = new InMemoryUniversalStore();

        // 第 5 步：用 dynamic 语法点写——就像它真的是一个编译期类型。
        dynamic order = new UniversalObject("Order");
        order.OrderNo = "SO-2026-001";
        order.Amount = 99;
        order.OrderDate = new DateTime(2026, 10, 27, 10, 0, 0, DateTimeKind.Utc);

        // 第 6 步：网关拆解（Order 无 CSharpType 指令 → 动态轨）→ 落库。
        var rows = gateway.Decompose(order, OrderTypeId);

        // 1 实体行 + 3 属性行。
        Assert.Equal(4, rows.Count);

        // 整包写入进程内池。
        await store.BulkInsertAsync(rows);

        // 第 7 步：子图预取 + 网关组装（动态轨 → UniversalObject）。
        var subgraph = await store.GetSubGraphAsync(OrderTypeId, UniversalEntity.RoleData);

        // 4 行子图。
        Assert.Equal(4, subgraph.Count);

        // 统一组装。
        var assembled = gateway.Assemble(subgraph, OrderTypeId);

        // 无 CSharpType 指令 → 必然是万物容器，而非任何编译期类型。
        var restored = Assert.IsType<UniversalObject>(assembled);

        // 第 8 步：dynamic 点读断言——"类"不存在，但行为与真实类无异。
        // 单号原样还原（字符串不推断）。
        Assert.Equal("SO-2026-001", (string)restored.GetProperty("OrderNo")!);

        // 金额被推断还原为 int。
        Assert.Equal(99, (int)restored.GetProperty("Amount")!);

        // 时间被推断还原为 DateTime（毫秒精度）。
        Assert.Equal(
            new DateTime(2026, 10, 27, 10, 0, 0, DateTimeKind.Utc),
            (DateTime)restored.GetProperty("OrderDate")!,
            TimeSpan.FromMilliseconds(1));

        // 实体行 Id 回填正确。
        Assert.Equal(subgraph.Single(row => row.ParentId == null).Id, restored.Id);

        // dynamic 点读也能通过编译期动态绑定取值。
        Assert.Equal(99, (int)restored.GetProperty("Amount")!);
    }

    /// <summary>
    /// 类名同化路由：给 User 补一条 CSharpType 指令（改一行数据），网关即切换为强类型反射轨。
    /// </summary>
    [Fact]
    public async Task CSharpTypeInstruction_RoutesGatewayToStrongTypeAssembler()
    {
        // 第 1 步：清池 + 自举。
        await TruncateTableAsync();
        var diskRepository = new UniversalEntityRepository(new EmokingDbContext());
        var cache = new MetaCache(diskRepository);
        await cache.LoadAsync();

        // 确认自举后 User 尚未绑定 CSharpType。
        Assert.Null(cache.GetCSharpTypeName(UserTypeId));

        // 第 2 步：往池里插一条 CSharpType 指令——类名成了一行普通数据。
        var csharpTypeRow = new UniversalEntity(
            Id: CSharpTypeRowId,
            TypeId: MetaEngineTypeId,
            Role: UniversalEntity.RoleEngine,
            ParentId: null,
            Value: $"CSharpType=Emoking.DAL.Tests.User|TargetTypeId={UserTypeId}");
        await diskRepository.InsertBatchAsync(new[] { csharpTypeRow });

        // 第 3 步：热重载——User 与 C# 类完成绑定。
        await cache.LoadAsync();

        // 指令生效。
        Assert.Equal("Emoking.DAL.Tests.User", cache.GetCSharpTypeName(UserTypeId));

        // 第 4 步：构造网关与进程内存储。
        var gateway = new UniversalEntityGateway(cache);
        var store = new InMemoryUniversalStore();

        // 第 5 步：用动态数据源（UniversalObject）提供业务值——但目标类型绑定了强类型。
        dynamic source = new UniversalObject("User");
        source.Name = "绑定张三";
        source.Email = "bound@genesis.local";
        source.Age = 45;
        source.CreatedAt = new DateTime(2026, 10, 27, 11, 0, 0, DateTimeKind.Utc);

        // 网关拆解——检测到 CSharpType，动态数据被反射装进真实 User 实例后走强类型轨。
        var rows = gateway.Decompose(source, UserTypeId);

        // 5 行。
        Assert.Equal(5, rows.Count);

        // 落库 + 子图预取。
        await store.BulkInsertAsync(rows);
        var subgraph = await store.GetSubGraphAsync(UserTypeId, UniversalEntity.RoleData);

        // 第 6 步：统一组装——有 CSharpType 指令 → 返回真实 User 实例。
        var assembled = gateway.Assemble(subgraph, UserTypeId);

        // 强类型落位。
        var restored = Assert.IsType<User>(assembled);

        // 全属性一致。
        Assert.Equal("绑定张三", restored.Name);
        Assert.Equal("bound@genesis.local", restored.Email);
        Assert.Equal(45, restored.Age);
        Assert.Equal(
            new DateTime(2026, 10, 27, 11, 0, 0, DateTimeKind.Utc),
            restored.CreatedAt,
            TimeSpan.FromMilliseconds(1));

        // Id 回填。
        Assert.Equal(subgraph.Single(row => row.ParentId == null).Id, restored.Id);
    }

    /// <summary>
    /// 动态拆解拒绝元数据未声明的属性——言出法随的前提是元数据先行。
    /// </summary>
    [Fact]
    public async Task DynamicDecomposer_RejectsPropertyNotInMetadata()
    {
        // 清池 + 自举。
        await TruncateTableAsync();
        var diskRepository = new UniversalEntityRepository(new EmokingDbContext());
        var cache = new MetaCache(diskRepository);
        await cache.LoadAsync();

        // 言出 Order 类型。
        await SpeakOrderTypeIntoExistenceAsync(diskRepository);
        await cache.LoadAsync();

        // 构造网关。
        var gateway = new UniversalEntityGateway(cache);

        // 万物容器携带一个元数据里没有的属性。
        dynamic order = new UniversalObject("Order");
        order.OrderNo = "SO-2026-002";
        order.GhostProperty = "元数据未声明";

        // 拆解必须显式失败——不能静默丢数据。
        var exception = Assert.Throws<InvalidOperationException>(
            () => gateway.Decompose(order, OrderTypeId));

        // 异常消息点明哪个属性未注册。
        Assert.Contains("GhostProperty", exception.Message);
    }
}
