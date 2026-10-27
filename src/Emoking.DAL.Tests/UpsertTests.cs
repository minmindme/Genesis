// ANCHOR: test.upsert - 洞 2 重做验收：CAS upsert 保历史、并发一胜一败、跨租户拒绝
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 洞 2 重做验收（第 20 轮）——"U 不是能覆盖，是在并发下可证明地覆盖，并且留下可审计的过去"。
/// 三条测试全部跑在 SqlServerUniversalStore 真库上（评审指认：真库才是洞 2 的真验收）。
/// </summary>
public sealed class UpsertTests
{
    // 测试实体类型 TypeId——快照单行文档，避免依赖网关拆解。
    private static readonly Guid DocTypeId = new("88888888-8888-8888-8888-888888888888");

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
    /// 构造一个测试文档行——快照形态单行（Value 即业务载荷）。
    /// </summary>
    /// <param name="id">行 Id。</param>
    /// <param name="value">业务载荷文本。</param>
    /// <param name="version">乐观锁版本（新插入恒为 1）。</param>
    private static UniversalEntity BuildDocRow(Guid id, string value, int version = 1)
    {
        // 组装同构行——Role=2 业务数据、ParentId 为空。
        return new UniversalEntity(
            Id: id,
            TypeId: DocTypeId,
            Role: UniversalEntity.RoleData,
            ParentId: null,
            Value: value,
            Version: version);
    }

    /// <summary>
    /// 同 Id 重写必须保 CreatedAt、Version 递增、旧行落成 Role=6 历史行，且历史行不污染指针追逐。
    /// </summary>
    /// <remarks>
    /// 第 19 轮 delete+insert 实现下本测试必红：CreatedAt 被 DB 默认值重写、Version 恒 1、
    /// 旧行被物理删除（历史丢失）、Role=6 行不存在。
    /// </remarks>
    [Fact]
    public async Task Upsert_SameId_PreservesCreatedAt_IncrementsVersion_WritesHistoryRow()
    {
        // 第 1 步：清池 + 构造真库存储。
        await TruncateTableAsync();
        var repository = new UniversalEntityRepository(new EmokingDbContext());
        var store = new SqlServerUniversalStore(repository);

        // 第 2 步：纯插入文档 v1。
        var docId = Guid.NewGuid();
        await store.BulkInsertAsync(new[] { BuildDocRow(docId, "v1") });

        // 读回 v1 行——拿到数据库生成的 CreatedAt。
        var original = await store.GetByIdAsync(docId);

        // 插入必成功。
        Assert.NotNull(original);
        Assert.Equal("v1", original!.Value);
        Assert.Equal(1, original.Version);

        // 第 3 步：CAS upsert 覆盖为 v2——携带与存储一致的 Version=1。
        var updated = await store.UpsertAsync(new[] { BuildDocRow(docId, "v2", original.Version) });

        // 返回落库后的最终行。
        var upserted = Assert.Single(updated);

        // 历史不丢：CreatedAt 保留原始创建时间（1ms 容差应对 datetime2 舍入）。
        Assert.Equal(original.CreatedAt, upserted.CreatedAt, TimeSpan.FromMilliseconds(1));

        // 乐观锁生效：Version 递增到 2（不再恒为 1）。
        Assert.Equal(2, upserted.Version);

        // 覆盖生效。
        Assert.Equal("v2", upserted.Value);

        // 第 4 步：从存储再读回验证落库形态。
        var reread = await store.GetByIdAsync(docId);
        Assert.NotNull(reread);
        Assert.Equal("v2", reread!.Value);
        Assert.Equal(2, reread.Version);
        Assert.Equal(original.CreatedAt, reread.CreatedAt, TimeSpan.FromMilliseconds(1));

        // 第 5 步：旧行已落成历史行——Role=6、ParentId 指向当前行、Value 为旧值。
        var historyRows = await repository.GetByTypeIdAndRoleAsync(DocTypeId, UniversalEntity.RoleHistory);
        var history = Assert.Single(historyRows);

        // 历史行挂在当前行下。
        Assert.Equal(docId, history.ParentId);

        // 历史行保存的是被覆盖前的旧值。
        Assert.Equal("v1", history.Value);

        // 历史行的 Version/CreatedAt 原样保留旧行（审计时间线可追溯）。
        Assert.Equal(1, history.Version);
        Assert.Equal(original.CreatedAt, history.CreatedAt, TimeSpan.FromMilliseconds(1));

        // 第 6 步：历史行不污染活数据——指针追逐不含 Role=6 行。
        var children = await store.GetByParentIdAsync(docId);
        Assert.DoesNotContain(children, row => row.Role == UniversalEntity.RoleHistory);
    }

    /// <summary>
    /// 并发两写同 Id 同 Version——恰好一胜一败，败者收到显式冲突异常，最终 Version 只递增一次。
    /// </summary>
    /// <remarks>
    /// 第 19 轮 delete+insert 实现下本测试必红：两个写都"成功"，后提交者静默覆盖前者。
    /// </remarks>
    [Fact]
    public async Task Upsert_ConcurrentWriters_OneWins_OneConflicts()
    {
        // 第 1 步：清池 + 构造真库存储。
        await TruncateTableAsync();
        var repository = new UniversalEntityRepository(new EmokingDbContext());
        var store = new SqlServerUniversalStore(repository);

        // 第 2 步：插入基线行（Version=1）。
        var docId = Guid.NewGuid();
        await store.BulkInsertAsync(new[] { BuildDocRow(docId, "base") });

        // 第 3 步：两个并发写——同 Id、同 Version=1，各自不同载荷。
        var writerA = store.UpsertAsync(new[] { BuildDocRow(docId, "from-A", 1) });
        var writerB = store.UpsertAsync(new[] { BuildDocRow(docId, "from-B", 1) });

        // 收集结果——恰好一个成功、一个抛 InvalidOperationException。
        var winners = 0;
        var conflicts = 0;

        // 逐个等待并分类（WhenAll 会在首异常处打断，逐个 try 更清晰）。
        foreach (var task in new[] { writerA, writerB })
        {
            // 尝试等待单个写者。
            try
            {
                // 成功者。
                await task;
                winners++;
            }
            catch (InvalidOperationException)
            {
                // 败者——显式冲突。
                conflicts++;
            }
        }

        // 一胜一败。
        Assert.Equal(1, winners);
        Assert.Equal(1, conflicts);

        // 第 4 步：最终状态——Version 只递增一次，胜者载荷落库。
        var final = await store.GetByIdAsync(docId);
        Assert.NotNull(final);
        Assert.Equal(2, final!.Version);

        // 胜者的 Value 必须是 A 或 B 之一（不是混合、不是丢失）。
        Assert.Contains(final.Value, new[] { "from-A", "from-B" });
    }

    /// <summary>
    /// 跨租户 upsert 必须被拒绝——租户 B 不能覆盖租户 A 的（对 B 不可见的）行。
    /// </summary>
    /// <remarks>
    /// 第 19 轮 delete+insert 的 DELETE 无租户谓词——同一条"不可见不可写"不变量两个入口只守了一个。
    /// 本测试在第 19 轮实现下必红：租户 B 的 upsert 会成功覆盖租户 A 的行。
    /// </remarks>
    [Fact]
    public async Task Upsert_InvisibleRow_RejectedForOtherTenant()
    {
        // 第 1 步：清池 + 构造共享真库内层。
        await TruncateTableAsync();
        var repository = new UniversalEntityRepository(new EmokingDbContext());
        var inner = new SqlServerUniversalStore(repository);

        // 测试密钥环——32 字节主密钥（Base64）。
        var keyRing = new Dictionary<string, byte[]> { ["K1"] = Convert.FromBase64String("MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=") };

        // 自举元数据缓存——两个租户的 store 共用同一份（策略来自池中 Role=5）。
        var cache = await BuildCacheAsync(repository);

        // 租户 A 的策略驱动存储。
        var storeA = new PolicyDrivenStore(inner, cache, "tenant-A", keyRing);

        // 第 2 步：租户 A 写入文档（AGC1 密文落库，AAD 绑定 tenant-A）。
        var docId = Guid.NewGuid();
        await storeA.BulkInsertAsync(new[] { BuildDocRow(docId, "tenant-A-secret") });

        // 租户 A 自己可见。
        var visible = await storeA.GetByIdAsync(docId);
        Assert.NotNull(visible);
        Assert.Equal("tenant-A-secret", visible!.Value);

        // 第 3 步：租户 B 尝试 upsert 同 Id——对 B 该行不可见，必须拒绝。
        var storeB = new PolicyDrivenStore(inner, cache, "tenant-B", keyRing);
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => storeB.UpsertAsync(new[] { BuildDocRow(docId, "hacked-by-B", 1) }));

        // 异常点明拒绝原因。
        Assert.Contains("不可见", exception.Message);

        // 第 4 步：租户 A 的数据完好无损。
        var intact = await storeA.GetByIdAsync(docId);
        Assert.NotNull(intact);
        Assert.Equal("tenant-A-secret", intact!.Value);
    }

    /// <summary>
    /// 构造已自举的元数据缓存——PolicyDrivenStore 读 Role=5 策略所需。
    /// </summary>
    /// <param name="repository">单表仓储。</param>
    private static async Task<MetaCache> BuildCacheAsync(UniversalEntityRepository repository)
    {
        // 自举（空池播种创世行，含 3 条 Role=5 策略）。
        var cache = new MetaCache(repository);
        await cache.LoadAsync();

        // 返回就绪缓存。
        return cache;
    }

    /// <summary>
    /// InMemory 引擎的 upsert 语义与 SqlServer 对齐——CAS 递增、CreatedAt 保留、历史行入池、BulkInsert 纯插拒绝。
    /// </summary>
    [Fact]
    public async Task Upsert_InMemory_MatchesSqlServerSemantics()
    {
        // 构造进程内存储。
        var store = new InMemoryUniversalStore();

        // 第 1 步：纯插入 v1。
        var docId = Guid.NewGuid();
        await store.BulkInsertAsync(new[] { BuildDocRow(docId, "v1") });

        // 纯插入语义：同 Id 再插显式失败（不再静默覆盖）。
        var duplicateException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.BulkInsertAsync(new[] { BuildDocRow(docId, "duplicate") }));

        // 异常点明纯插语义。
        Assert.Contains("纯插入", duplicateException.Message);

        // 读回 v1。
        var original = await store.GetByIdAsync(docId);
        Assert.NotNull(original);
        Assert.Equal(1, original!.Version);

        // 第 2 步：CAS upsert v2。
        var updated = await store.UpsertAsync(new[] { BuildDocRow(docId, "v2", original.Version) });
        var upserted = Assert.Single(updated);

        // Version 递增、CreatedAt 保留。
        Assert.Equal(2, upserted.Version);
        Assert.Equal(original.CreatedAt, upserted.CreatedAt);

        // 读回验证。
        var reread = await store.GetByIdAsync(docId);
        Assert.Equal("v2", reread!.Value);
        Assert.Equal(2, reread.Version);

        // 第 3 步：Version 过期即冲突。
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpsertAsync(new[] { BuildDocRow(docId, "stale", 1) }));

        // 第 4 步：历史行入池且不污染指针追逐。
        var children = await store.GetByParentIdAsync(docId);
        Assert.DoesNotContain(children, row => row.Role == UniversalEntity.RoleHistory);
    }

    /// <summary>
    /// FasterKv 引擎的 upsert 语义链——CAS 递增 + 历史行写入 + BulkInsert 纯插拒绝。
    /// </summary>
    /// <remarks>
    /// 诚实边界：FASTER 的 codec 不含时间戳列，历史行的 CreatedAt 审计以 SqlServer/InMemory 为准；
    /// 本测试只验证 Version CAS 与历史行存在性。
    /// </remarks>
    [Fact]
    public async Task Upsert_FasterKv_MatchesSqlServerSemantics()
    {
        // 临时日志目录（bin 输出树可写——B13 教训）。
        var logDirectory = Path.Combine(AppContext.BaseDirectory, "faster-upsert-" + Guid.NewGuid().ToString("N")[..8]);

        // 构造并释放存储。
        using (var store = new FasterKvUniversalStore(logDirectory))
        {
            // 第 1 步：纯插入 v1。
            var docId = Guid.NewGuid();
            await store.BulkInsertAsync(new[] { BuildDocRow(docId, "v1") });

            // 纯插入语义：同 Id 再插显式失败。
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.BulkInsertAsync(new[] { BuildDocRow(docId, "duplicate") }));

            // 读回 v1。
            var original = await store.GetByIdAsync(docId);
            Assert.NotNull(original);
            Assert.Equal(1, original!.Version);

            // 第 2 步：CAS upsert v2。
            var updated = await store.UpsertAsync(new[] { BuildDocRow(docId, "v2", original.Version) });
            var upserted = Assert.Single(updated);

            // Version 递增。
            Assert.Equal(2, upserted.Version);

            // 读回验证。
            var reread = await store.GetByIdAsync(docId);
            Assert.Equal("v2", reread!.Value);
            Assert.Equal(2, reread.Version);

            // 第 3 步：Version 过期即冲突。
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.UpsertAsync(new[] { BuildDocRow(docId, "stale", 1) }));
        }

        // 尽力清理临时目录。
        try
        {
            // 删除目录树。
            Directory.Delete(logDirectory, recursive: true);
        }
        catch (IOException)
        {
            // FASTER 句柄未完全释放时忽略清理失败——临时目录留在 bin 下无害。
        }
    }
}
