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
public sealed class ActionBusTests
{
    // 演示命令行的固定 Id。
    private static readonly Guid CreateEntityCommandRowId = new("00000000-0000-0000-0000-000000000920");

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
        var entityId = Assert.IsType<Guid>(await bus.ExecuteActionAsync("InsertData", new Dictionary<string, string>
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
    /// 无法识别的动作显式失败——总线不猜意图。
    /// </summary>
    [Fact]
    public async Task Bus_UnknownAction_Throws()
    {
        // 清池 + 总线。
        await TruncateTableAsync();
        var bus = await BuildBusAsync(withCommand: false);

        // 未知动作抛错。
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => bus.ExecuteActionAsync("TeleportData", new Dictionary<string, string>()));

        // 异常消息点明动作名。
        Assert.Contains("TeleportData", exception.Message);
    }
}
