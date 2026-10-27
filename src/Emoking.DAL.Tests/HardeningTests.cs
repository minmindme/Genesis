// ANCHOR: test.hardening - 工业级加固验收：DataType 显式化、级联删除、限流策略
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 工业级加固验收——DataType 显式化（消灭类型推断）、DeleteData 级联软删、RateLimit 策略生效。
/// </summary>
public sealed class HardeningTests
{
    // 演示类型 TypeId。
    private static readonly Guid ProductTypeId = new("77777777-7777-7777-7777-777777777777");

    /// <summary>
    /// 清空磁盘表。
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
    /// 言出 Product 类型——属性行用第 17 轮 "Name:Type" 格式（Title:String, Price:Decimal）。
    /// </summary>
    /// <param name="repository">单表仓储。</param>
    private static async Task SpeakTypedProductAsync(UniversalEntityRepository repository)
    {
        // 元数据行。
        await repository.InsertBatchAsync(new[]
        {
            // 类型行。
            new UniversalEntity(
                Id: ProductTypeId,
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "Product"),

            // Title 属性行——显式 String。
            new UniversalEntity(
                Id: new Guid("77777777-7777-7777-7777-777777777701"),
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: ProductTypeId,
                Value: "Title:String"),

            // Price 属性行——显式 Decimal。
            new UniversalEntity(
                Id: new Guid("77777777-7777-7777-7777-777777777702"),
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: ProductTypeId,
                Value: "Price:Decimal")
        });
    }

    /// <summary>
    /// DataType 显式化：组装时 Price 还原为 decimal（而非字符串或推断），Title 保持 string。
    /// </summary>
    [Fact]
    public async Task DataType_Explicit_Types_RoundTripWithoutInference()
    {
        // 第 1 步：清池 + 言出强类型 Product + 自举。
        await TruncateTableAsync();
        var diskRepository = new UniversalEntityRepository(new EmokingDbContext());
        await SpeakTypedProductAsync(diskRepository);
        var cache = new MetaCache(diskRepository);
        await cache.LoadAsync();

        // 元数据声明正确。
        var meta = cache.GetTypeByTypeId(ProductTypeId);
        Assert.NotNull(meta);
        Assert.Equal("Decimal", meta!.PropertyDataTypes["Price"]);
        Assert.Equal("String", meta.PropertyDataTypes["Title"]);

        // 第 2 步：动态轨组装（Title 值形似日期也会被 DataType 拦住不误判）。
        var assembler = new DynamicAssembler(cache);

        // 实体行 Id 与行集。
        var entityId = Guid.NewGuid();
        var rows = new List<UniversalEntity>
        {
            // 实体行。
            new(entityId, ProductTypeId, UniversalEntity.RoleData, null, null),

            // Title 是形似日期的字符串——若走推断会误判，DataType 必须拦住。
            new(Guid.NewGuid(), meta!.PropertyTypeIds["Title"], UniversalEntity.RoleData, entityId, "2026-10"),

            // Price 显式 Decimal。
            new(Guid.NewGuid(), meta.PropertyTypeIds["Price"], UniversalEntity.RoleData, entityId, "42.5")
        };

        // 组装。
        var restored = assembler.Assemble(rows, ProductTypeId);

        // DataType 拦截：Title 保持字符串（不误判为日期）。
        Assert.Equal("2026-10", restored.GetProperty("Title"));

        // Price 按声明还原为 decimal。
        Assert.Equal(42.5m, restored.GetProperty("Price"));
    }

    /// <summary>
    /// DeleteData 级联软删：实体行 + 全部子行（属性、出边）一起消失，池子不留孤儿。
    /// </summary>
    [Fact]
    public async Task DeleteData_Cascades_EntityAndChildren()
    {
        // 第 1 步：清池 + 自举 + 言出强类型 Product。
        await TruncateTableAsync();
        var diskRepository = new UniversalEntityRepository(new EmokingDbContext());
        await SpeakTypedProductAsync(diskRepository);
        var cache = new MetaCache(diskRepository);
        await cache.LoadAsync();

        // 第 2 步：图模式拆解落库（实体行 + 2 属性行）——用显式容器避免 dynamic 捕获进 lambda。
        var gateway = new UniversalEntityGateway(cache);
        var store = new InMemoryUniversalStore();
        var product = new UniversalObject("Product");
        product.SetProperty("Title", "待删商品");
        product.SetProperty("Price", 9.9m);
        var rows = gateway.Decompose(product, ProductTypeId);
        await store.BulkInsertAsync(rows);

        // 3 行。
        Assert.Equal(3, rows.Count);

        // 第 3 步：DeleteData 级联软删。
        var bus = new UniversalActionBus(cache, gateway, store, new UniversalCommandExecutor(diskRepository, cache));
        var entityId = rows.Single(row => row.ParentId == null).Id;
        var deleted = Assert.IsType<int>(await bus.ExecuteActionAsync("DeleteData", new Dictionary<string, string>
        {
            // 实体行 Id。
            ["EntityId"] = entityId.ToString()
        }));

        // 3 行全部软删。
        Assert.Equal(3, deleted);

        // 第 4 步：验证——实体行不可见，子行无孤儿。
        Assert.Null(await store.GetByIdAsync(entityId));
        var children = await store.GetByParentIdAsync(entityId);
        Assert.Empty(children);

        // 子图预取为空。
        var subgraph = await store.GetSubGraphAsync(ProductTypeId, UniversalEntity.RoleData);
        Assert.Empty(subgraph);
    }

    /// <summary>
    /// DeleteData 不存在的实体显式失败。
    /// </summary>
    [Fact]
    public async Task DeleteData_UnknownEntity_Throws()
    {
        // 清池 + 总线。
        await TruncateTableAsync();
        var cache = await BuildLoadedCacheAsync();
        var bus = new UniversalActionBus(
            cache,
            new UniversalEntityGateway(cache),
            new InMemoryUniversalStore(),
            new UniversalCommandExecutor(new UniversalEntityRepository(new EmokingDbContext()), cache));

        // 不存在的实体抛错。
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => bus.ExecuteActionAsync("DeleteData", new Dictionary<string, string>
            {
                // 随机 Guid。
                ["EntityId"] = Guid.NewGuid().ToString()
            }));
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
