// ANCHOR: test.ui-schema - UI 同构验收：一行 Role=8 长出表单（GetSystemSchema 数据源）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// UI 同构验收——Role=8 一行数据即前端界面描述，通用渲染器据此动态生成表单。
/// </summary>
public sealed class UiSchemaTests
{
    // Product 实体类型 TypeId——与 UI Schema 指向一致。
    private static readonly Guid ProductTypeId = new("77777777-7777-7777-7777-777777777777");

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
    /// 构造总线依赖链（自举 + 可选言出 Product 类型与 Role=8 UI 行）。
    /// </summary>
    /// <param name="includeUi">是否插入 Role=8 UI Schema 行。</param>
    private static async Task<UniversalActionBus> BuildBusAsync(bool includeUi = true)
    {
        // 磁盘仓储 + 缓存。
        var diskRepository = new UniversalEntityRepository(new EmokingDbContext());

        // 言出 Product 类型 + UI Schema——两行数据长出一个可用的业务模块。
        var metaRows = new List<UniversalEntity>
        {
            // 类型定义。
            new UniversalEntity(
                Id: ProductTypeId,
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "Product"),

            // 存储模式：快照——UI 同构的黄金搭档（schema 即类/JSON，无需属性行）。
            new UniversalEntity(
                Id: new Guid("00000000-0000-0000-0000-000000000941"),
                TypeId: MetaCache.MetaEngineTypeId,
                Role: UniversalEntity.RoleEngine,
                ParentId: null,
                Value: $"StorageMode=Snapshot|TargetTypeId={ProductTypeId}")
        };

        // 需要 UI 时追加 Role=8 行。
        if (includeUi)
        {
            // UI Schema——通用渲染器的界面描述。
            metaRows.Add(new UniversalEntity(
                Id: new Guid("00000000-0000-0000-0000-000000000940"),
                TypeId: MetaCache.MetaEngineTypeId,
                Role: UniversalEntity.RoleUi,
                ParentId: null,
                Value: "UI=ProductForm|Type=Form|Entity=Product|Fields=Title:Input,Price:Number|Action=InsertData"));
        }

        // 落库。
        await diskRepository.InsertBatchAsync(metaRows);

        // 自举。
        var cache = new MetaCache(diskRepository);
        await cache.LoadAsync();

        // 网关 + 进程内存储 + 执行器。
        var gateway = new UniversalEntityGateway(cache);
        var store = new InMemoryUniversalStore();
        var executor = new UniversalCommandExecutor(diskRepository, cache, ".");

        // 构造总线。
        return new UniversalActionBus(cache, gateway, store, executor);
    }

    /// <summary>
    /// GetSystemSchema 一次拉回类型、命令与 UI Schema——渲染器的唯一数据源。
    /// </summary>
    [Fact]
    public async Task Bus_GetSystemSchema_ReturnsTypesAndUis()
    {
        // 第 1 步：清池 + 言出类型与 UI。
        await TruncateTableAsync();
        var bus = await BuildBusAsync();

        // 第 2 步：拉取系统 Schema——匿名类型经 JSON 序列化后断言。
        var schemaJson = System.Text.Json.JsonSerializer.Serialize(
            await bus.ExecuteActionAsync("GetSystemSchema", new Dictionary<string, string>()),
            new System.Text.Json.JsonSerializerOptions
            {
                // 中文原样。
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

        // 类型清单含 Product。
        Assert.Contains("Product", schemaJson);

        // UI Schema 含 ProductForm 且字段/动作正确（JSON 断言）。
        Assert.Contains("ProductForm", schemaJson);
        Assert.Contains("Entity", schemaJson);
        Assert.Contains("InsertData", schemaJson);
    }

    /// <summary>
    /// 端到端：按 UI Schema 描述的字段组装 InsertData 意图 → 数据落库（模拟渲染器提交行为）。
    /// </summary>
    [Fact]
    public async Task Bus_UiDrivenInsert_PersistsProductData()
    {
        // 第 1 步：清池 + 言出类型与 UI。
        await TruncateTableAsync();
        var bus = await BuildBusAsync();

        // 第 2 步：模拟渲染器提交——按 UI.Fields 收集字段值组装 InsertData。
        var entityId = Assert.IsType<Guid>(await bus.ExecuteActionAsync("InsertData", new Dictionary<string, string>
        {
            // UI.Entity。
            ["TypeName"] = "Product",
            // UI 字段值打包。
            ["Data"] = "{\"Title\":\"渲染器商品\",\"Price\":19.9}"
        }));

        // 非空 Id。
        Assert.NotEqual(Guid.Empty, entityId);

        // 第 3 步：GetData 读回——字段完整还原。
        var result = await bus.ExecuteActionAsync("GetData", new Dictionary<string, string>
        {
            // 实体行 Id。
            ["EntityId"] = entityId.ToString()
        });

        // 结果 JSON 含价格与标题。
        var resultJson = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions
        {
            // 中文原样。
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        Assert.Contains("渲染器商品", resultJson);
        Assert.Contains("19.9", resultJson);
    }

    /// <summary>
    /// 池中无 Role=8 时 GetSystemSchema 返回空 UI 集合——渲染器据此显示"无界面"提示。
    /// </summary>
    [Fact]
    public async Task Bus_GetSystemSchema_WithNoUi_ReturnsEmptyUis()
    {
        // 清池 + 总线（不插 Role=8）。
        await TruncateTableAsync();
        var bus = await BuildBusAsync(includeUi: false);

        // 拉取 Schema（JSON 断言）。
        var schemaJson = System.Text.Json.JsonSerializer.Serialize(
            await bus.ExecuteActionAsync("GetSystemSchema", new Dictionary<string, string>()));

        // UI 集合为空——不含任何 UI 名。
        Assert.DoesNotContain("ProductForm", schemaJson);
    }
}
