// ANCHOR: test.end-to-end-demo - 端到端演示：快照状态机 + 图遍历在同一池内共存
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 端到端业务演示——订单（快照模式，"直接煮熟"）与支付流水（图模式，拆解挂载）
/// 在同一个同构池内共存、各自走最优路径、可联合查询。
/// </summary>
public sealed class EndToEndDemoTests
{
    // Order 实体类型 TypeId——快照模式。
    private static readonly Guid OrderTypeId = new("33333333-3333-3333-3333-333333333333");

    // PaymentFlow 实体类型 TypeId——图模式。
    private static readonly Guid PaymentFlowTypeId = new("44444444-4444-4444-4444-555555555555");

    // 元类型与引擎指令类型 Guid——与 MetaCache 内部常量一致。
    private static readonly Guid MetaTypeTypeId = new("00000000-0000-0000-0000-000000000001");
    private static readonly Guid MetaEngineTypeId = new("00000000-0000-0000-0000-000000000002");

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
    /// 言出两种业务类型：Order（快照）与 PaymentFlow（图）——全部是池中数据。
    /// </summary>
    /// <param name="repository">单表仓储。</param>
    private static async Task SpeakBusinessTypesAsync(UniversalEntityRepository repository)
    {
        // 组装元数据行——Order 类型定义 + 存储模式指令 + 类名绑定指令 + PaymentFlow 图模式全套。
        var metaRows = new List<UniversalEntity>
        {
            // Order 类型定义。
            new(
                Id: OrderTypeId,
                TypeId: MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "PurchaseOrder"),

            // Order 存储模式指令——快照（直接煮熟）。
            new(
                Id: new Guid("00000000-0000-0000-0000-000000000910"),
                TypeId: MetaEngineTypeId,
                Role: UniversalEntity.RoleEngine,
                ParentId: null,
                Value: $"StorageMode=Snapshot|TargetTypeId={OrderTypeId}"),

            // Order 类名绑定指令——快照强类型反序列化。
            new(
                Id: new Guid("00000000-0000-0000-0000-000000000911"),
                TypeId: MetaEngineTypeId,
                Role: UniversalEntity.RoleEngine,
                ParentId: null,
                Value: $"CSharpType=Emoking.DAL.Tests.PurchaseOrder|TargetTypeId={OrderTypeId}"),

            // PaymentFlow 类型定义。
            new(
                Id: PaymentFlowTypeId,
                TypeId: MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "PaymentFlow"),

            // PaymentFlow 存储模式指令——显式图模式。
            new(
                Id: new Guid("00000000-0000-0000-0000-000000000912"),
                TypeId: MetaEngineTypeId,
                Role: UniversalEntity.RoleEngine,
                ParentId: null,
                Value: $"StorageMode=Graph|TargetTypeId={PaymentFlowTypeId}"),

            // PaymentFlow 属性：FromUser。
            new(
                Id: new Guid("44444444-4444-4444-4444-555555555501"),
                TypeId: MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: PaymentFlowTypeId,
                Value: "FromUser"),

            // PaymentFlow 属性：Amount。
            new(
                Id: new Guid("44444444-4444-4444-4444-555555555502"),
                TypeId: MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: PaymentFlowTypeId,
                Value: "Amount"),

            // PaymentFlow 属性：State。
            new(
                Id: new Guid("44444444-4444-4444-4444-555555555503"),
                TypeId: MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: PaymentFlowTypeId,
                Value: "State")
        };

        // 写入池中。
        await repository.InsertBatchAsync(metaRows);
    }

    /// <summary>
    /// 端到端主流程：订单快照状态机（Created → Paid → Shipped，整快照覆盖）
    /// 与支付流水图遍历在同一池内共存，混合查询互不干扰。
    /// </summary>
    [Fact]
    public async Task EndToEnd_SnapshotStateMachine_And_GraphTraversal_Coexist()
    {
        // 第 1 步：清池 + 自举 + 言出业务类型 + 热重载。
        await TruncateTableAsync();
        var diskRepository = new UniversalEntityRepository(new EmokingDbContext());
        var cache = new MetaCache(diskRepository);
        await cache.LoadAsync();
        await SpeakBusinessTypesAsync(diskRepository);
        await cache.LoadAsync();

        // 存储模式指令生效。
        Assert.Equal("Snapshot", cache.GetStorageMode(OrderTypeId));
        Assert.Equal("Graph", cache.GetStorageMode(PaymentFlowTypeId));

        // 第 2 步：构造网关与**真库存储**——第 20 轮起端到端验收走 SqlServer（洞 2 的真验收场景）。
        var gateway = new UniversalEntityGateway(cache);
        var store = new SqlServerUniversalStore(diskRepository);

        // ===== 场景 A：订单快照状态机 =====

        // 第 3 步：创建订单（State=Created）——快照轨。
        var order = new PurchaseOrder
        {
            // 订单号。
            OrderNo = "SO-2026-777",
            // 初始状态。
            State = "Created",
            // 金额。
            Amount = 299.9m,
            // 归属用户。
            Owner = "user-U1",
            // 创建时间。
            CreatedAt = new DateTime(2026, 10, 27, 14, 0, 0, DateTimeKind.Utc)
        };

        // 拆解——快照轨单行。
        var createdRows = gateway.Decompose(order, OrderTypeId);

        // 快照模式恒为 1 行。
        Assert.Single(createdRows);

        // 落库（纯插入）。
        await store.BulkInsertAsync(createdRows);

        // 读回初始行——拿到数据库生成的 CreatedAt 与 Version=1。
        var originalRow = await store.GetByIdAsync(createdRows[0].Id);
        Assert.NotNull(originalRow);
        Assert.Equal(1, originalRow!.Version);

        // 原始视角：SNAP1 前缀。
        Assert.StartsWith("SNAP1:", originalRow.Value);

        // 第 4 步：状态流转——支付。**CAS upsert 覆盖**（第 20 轮：不再 delete+insert，历史可审计）。
        var paidOrder = new PurchaseOrder
        {
            // 同单号。
            OrderNo = order.OrderNo,
            // 状态推进。
            State = "Paid",
            // 金额不变。
            Amount = order.Amount,
            // 用户不变。
            Owner = order.Owner,
            // 时间不变。
            CreatedAt = order.CreatedAt
        };

        // 重新拆解并对齐旧行 Id 与当前 Version——乐观锁更新。
        var paidRows = gateway.Decompose(paidOrder, OrderTypeId);
        var paidRow = paidRows[0] with { Id = createdRows[0].Id, Version = originalRow.Version };
        var paidUpserted = (await store.UpsertAsync(new[] { paidRow })).Single();

        // Version 递增（乐观锁生效）。
        Assert.Equal(2, paidUpserted.Version);

        // CreatedAt 保留原始创建时间（历史不因覆盖重写）。
        Assert.Equal(originalRow.CreatedAt, paidUpserted.CreatedAt, TimeSpan.FromMilliseconds(1));

        // 读回验证状态。
        var subgraphAfterPaid = await store.GetSubGraphAsync(OrderTypeId, UniversalEntity.RoleData);
        var paid = Assert.IsType<PurchaseOrder>(gateway.Assemble(subgraphAfterPaid, OrderTypeId));
        Assert.Equal("Paid", paid.State);

        // 第 5 步：状态流转——发货（携带上一次递增后的 Version）。
        var shippedOrder = new PurchaseOrder
        {
            // 同单号。
            OrderNo = paid.OrderNo,
            // 状态推进。
            State = "Shipped",
            // 金额不变。
            Amount = paid.Amount,
            // 用户不变。
            Owner = paid.Owner,
            // 时间不变。
            CreatedAt = paid.CreatedAt
        };
        var shippedRows = gateway.Decompose(shippedOrder, OrderTypeId);
        var shippedRow = shippedRows[0] with { Id = createdRows[0].Id, Version = paidUpserted.Version };
        var shippedUpserted = (await store.UpsertAsync(new[] { shippedRow })).Single();

        // Version 再递增。
        Assert.Equal(3, shippedUpserted.Version);

        // 读回验证。
        var subgraphAfterShipped = await store.GetSubGraphAsync(OrderTypeId, UniversalEntity.RoleData);
        var shipped = Assert.IsType<PurchaseOrder>(gateway.Assemble(subgraphAfterShipped, OrderTypeId));

        // 最终状态。
        Assert.Equal("Shipped", shipped.State);

        // 金额与时间无损（强类型反序列化）。
        Assert.Equal(299.9m, shipped.Amount);
        Assert.Equal(order.CreatedAt, shipped.CreatedAt, TimeSpan.FromMilliseconds(1));

        // 快照模式下订单**活行**数恒为 1——零写放大到多行（历史行不计入活子图）。
        Assert.Single(subgraphAfterShipped);

        // 第 20 轮新增断言：两次流转各留一行历史——system as history 在真库上可审计。
        var historyRows = await diskRepository.GetByTypeIdAndRoleAsync(OrderTypeId, UniversalEntity.RoleHistory);
        Assert.Equal(2, historyRows.Count);

        // 历史行挂在当前行下且保留旧状态值（Created → Paid 两代快照可追溯）。
        Assert.All(historyRows, row => Assert.Equal(createdRows[0].Id, row.ParentId));
        Assert.Single(historyRows, row => row.Value!.Contains("\"State\":\"Created\""));
        Assert.Single(historyRows, row => row.Value!.Contains("\"State\":\"Paid\""));

        // ===== 场景 B：支付流水图模式 =====

        // 第 6 步：写入 3 条支付流水（图模式，动态轨拆解）——两条属于 user-U1。
        foreach (var (flowUser, flowState) in new[] { ("user-U1", "Success"), ("user-U1", "Refunded"), ("user-U2", "Success") })
        {
            // 动态数据面。
            dynamic flow = new UniversalObject("PaymentFlow");
            flow.FromUser = flowUser;
            flow.Amount = 99.5m;
            flow.State = flowState;

            // 网关拆解（图模式动态轨）。
            var flowRows = gateway.Decompose(flow, PaymentFlowTypeId);

            // 落库。
            await store.BulkInsertAsync(flowRows);
        }

        // 第 7 步：图遍历——按用户查询支付流水（子图预取 + 内存过滤）。
        var flowGraph = await store.GetSubGraphAsync(PaymentFlowTypeId, UniversalEntity.RoleData);

        // 3 条流水 = 3 实体行 + 9 属性行。
        Assert.Equal(12, flowGraph.Count);

        // 过滤 user-U1 的流水实体行。
        var u1FlowEntities = flowGraph
            .Where(row => row.ParentId == null)
            .Where(row => flowGraph.Any(prop =>
                prop.ParentId == row.Id
                && prop.TypeId == new Guid("44444444-4444-4444-4444-555555555501")
                && prop.Value == "user-U1"))
            .ToList();

        // user-U1 有 2 条流水。
        Assert.Equal(2, u1FlowEntities.Count);

        // ===== 场景 C：混合查询共存验证 =====

        // 第 8 步：两种类型在同一池中互不干扰。
        // 订单快照点查仍然完好。
        var orderSnapshot = await store.GetByIdAsync(createdRows[0].Id);
        Assert.StartsWith("SNAP1:", orderSnapshot!.Value);

        // 订单子图仍为 1 行（PaymentFlow 行不会混入 Order 子图——TypeId 隔离）。
        var orderSubgraphFinal = await store.GetSubGraphAsync(OrderTypeId, UniversalEntity.RoleData);
        Assert.Single(orderSubgraphFinal);

        // 支付流水图仍为 12 行。
        Assert.Equal(12, (await store.GetSubGraphAsync(PaymentFlowTypeId, UniversalEntity.RoleData)).Count);
    }
}
