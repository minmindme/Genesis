// ANCHOR: test.edge-isomorphism - Edge 同构验收：双向边 + 索引即数据 + 100K 混合压测
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// Edge 同构验收——边是 Role=2 普通行（Role=3 废弃），双向边让反向索引数据化，
/// 正反向遍历都退化为普通的 ParentId 查询。
/// </summary>
public sealed class EdgeIsomorphismTests
{
    // User 实体类型 TypeId——快照模式压测载体。
    private static readonly Guid UserTypeId = new("11111111-1111-1111-1111-111111111111");

    // Follows 正向边类型 TypeId。
    private static readonly Guid FollowsTypeId = new("66666666-6666-6666-6666-666666666666");

    // FollowedBy 反向边类型 TypeId。
    private static readonly Guid FollowedByIdTypeId = new("66666666-6666-6666-6666-666666666667");

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

    /// <summary>
    /// 构造第 index 个快照用户的载荷字典。
    /// </summary>
    private static Dictionary<string, object?> BuildUserPayload(int index)
    {
        // 纯字典快照——动态轨，无需任何 C# 类。
        return new Dictionary<string, object?>
        {
            // 序号。
            ["No"] = index,
            // 名称。
            ["Name"] = $"U{index}"
        };
    }

    /// <summary>
    /// 功能验证：创建双向边 → 正向遍历（A 关注了谁）→ 反向遍历（谁关注了 A）→ 边属性还原。
    /// </summary>
    [Fact]
    public async Task Edge_Isomorphism_BidirectionalTraverse_WithProperties()
    {
        // 第 1 步：清池 + 自举 + 网关 + 进程内存储。
        await TruncateTableAsync();
        var cache = await BuildLoadedCacheAsync();
        var gateway = new UniversalEntityGateway(cache);
        var store = new InMemoryUniversalStore();

        // 第 2 步：造 3 个快照用户。
        var userIds = new List<Guid>();
        foreach (var index in Enumerable.Range(0, 3))
        {
            // 快照行。
            var userRow = SnapshotCodec.Decompose(BuildUserPayload(index), UserTypeId);
            userIds.Add(userRow.Id);
            await store.BulkInsertAsync(new[] { userRow });
        }

        // 第 3 步：建边——user0 关注 user1（带属性），user1 关注 user2。
        var edgeRows01 = gateway.CreateEdge(
            userIds[0], FollowsTypeId, FollowedByIdTypeId, userIds[1],
            new Dictionary<string, object?> { ["Since"] = "2026-01", ["IsMutual"] = false });
        var edgeRows12 = gateway.CreateEdge(
            userIds[1], FollowsTypeId, FollowedByIdTypeId, userIds[2],
            new Dictionary<string, object?> { ["Since"] = "2026-06", ["IsMutual"] = true });

        // 双向边各 2 行。
        Assert.Equal(2, edgeRows01.Count);
        Assert.Equal(2, edgeRows12.Count);

        // 落库。
        var allEdgeRows = new List<UniversalEntity>(edgeRows01);
        allEdgeRows.AddRange(edgeRows12);
        await store.BulkInsertAsync(allEdgeRows);

        // 第 4 步：正向遍历——user0 关注了谁。
        var outgoing0 = await gateway.GetOutgoingEdgesAsync(store, userIds[0], FollowsTypeId);

        // 恰好 1 条出边。
        Assert.Single(outgoing0);

        // 指向 user1。
        Assert.Equal(userIds[1], outgoing0[0].To);

        // 边属性还原。
        Assert.Equal("2026-01", outgoing0[0].Props["Since"]);
        Assert.Equal(false, outgoing0[0].Props["IsMutual"]);

        // 第 5 步：反向遍历——谁关注了 user1（数据即索引，无需外挂反向索引结构）。
        var incoming1 = await gateway.GetOutgoingEdgesAsync(store, userIds[1], FollowedByIdTypeId);

        // 恰好 1 条反向边。
        Assert.Single(incoming1);

        // 指回 user0。
        Assert.Equal(userIds[0], incoming1[0].To);

        // 反向边共享属性。
        Assert.Equal("2026-01", incoming1[0].Props["Since"]);

        // 第 6 步：user1 的正向与反向视图并存（Follows 出边 + FollowedBy 入边挂在同一实体下，TypeId 区分）。
        var children = await store.GetByParentIdAsync(userIds[1]);
        var edgeChildren = children.Where(row => row.TypeId == FollowsTypeId || row.TypeId == FollowedByIdTypeId).ToList();

        // 2 条边行挂在 user1 下。
        Assert.Equal(2, edgeChildren.Count);
    }

    /// <summary>
    /// 统一的 100K 混合压测：10 万快照用户 + 10 万 Follows 双向边（30 万行），
    /// 点查抽样 + 正向遍历抽样 + 反向遍历抽样分别计时。
    /// </summary>
    /// <param name="engineName">引擎名（报告用）。</param>
    /// <param name="store">被测存储。</param>
    /// <param name="sampleCount">遍历抽样次数（FASTER 全扫描代价高，抽样数更少）。</param>
    /// <param name="disposeStore">压测后是否释放存储。</param>
    private static async Task RunEdgeBenchmarkAsync(string engineName, IUniversalStore store, int sampleCount, bool disposeStore)
    {
        // 第 1 步：解析规模——环境变量优先，默认 10 万（电脑资源紧张时可调低）。
        var rowCountEnv = Environment.GetEnvironmentVariable("GENESIS_BENCH_ROWS");
        var userCount = int.TryParse(rowCountEnv, out var parsed) && parsed > 0 ? parsed : 100_000;
        var gateway = new UniversalEntityGateway(await BuildLoadedCacheAsync());

        // 第 2 步：阶段一——生成 10 万快照用户行。
        var decomposeSw = Stopwatch.StartNew();
        var userRows = new List<UniversalEntity>(userCount);
        var userIds = new Guid[userCount];
        for (var index = 0; index < userCount; index++)
        {
            // 快照行。
            var userRow = SnapshotCodec.Decompose(BuildUserPayload(index), UserTypeId);

            // 记录 Id 供建边。
            userIds[index] = userRow.Id;
            userRows.Add(userRow);
        }
        decomposeSw.Stop();

        // 第 3 步：阶段二——生成 10 万 Follows 双向边（环形 i → (i+1) % N，共 20 万行）。
        var edgeBuildSw = Stopwatch.StartNew();
        var edgeRows = new List<UniversalEntity>(userCount * 2);
        for (var index = 0; index < userCount; index++)
        {
            // 环形关注——每个用户恰好 1 条正向出边 + 1 条反向入边。
            edgeRows.AddRange(gateway.CreateEdge(
                userIds[index],
                FollowsTypeId,
                FollowedByIdTypeId,
                userIds[(index + 1) % userCount],
                new Dictionary<string, object?> { ["Since"] = "2026" }));
        }
        edgeBuildSw.Stop();

        // 第 4 步：阶段三——整包写入（30 万行）。
        var writeSw = Stopwatch.StartNew();
        await store.BulkInsertAsync(userRows);
        await store.BulkInsertAsync(edgeRows);
        writeSw.Stop();

        // 第 5 步：阶段四——点查抽样（100 次）。
        var pointSw = Stopwatch.StartNew();
        for (var sample = 0; sample < 100; sample++)
        {
            // 均匀抽样点查。
            await store.GetByIdAsync(userIds[sample * (userCount / 100)]);
        }
        pointSw.Stop();

        // 第 6 步：阶段五——正向遍历抽样（A 关注了谁）。
        var forwardSw = Stopwatch.StartNew();
        var forwardHits = 0;
        for (var sample = 0; sample < sampleCount; sample++)
        {
            // 抽样遍历。
            var edges = await gateway.GetOutgoingEdgesAsync(store, userIds[sample * (userCount / sampleCount)], FollowsTypeId);

            // 每个用户恰 1 条正向出边。
            forwardHits += edges.Count == 1 ? 1 : 0;
        }
        forwardSw.Stop();

        // 第 7 步：阶段六——反向遍历抽样（谁关注了 A）。
        var reverseSw = Stopwatch.StartNew();
        var reverseHits = 0;
        for (var sample = 0; sample < sampleCount; sample++)
        {
            // 抽样反向遍历。
            var edges = await gateway.GetOutgoingEdgesAsync(store, userIds[sample * (userCount / sampleCount)], FollowedByIdTypeId);

            // 每个用户恰 1 条反向边。
            reverseHits += edges.Count == 1 ? 1 : 0;
        }
        reverseSw.Stop();

        // 第 8 步：正确性断言——抽样遍历必须全部命中。
        Assert.Equal(sampleCount, forwardHits);
        Assert.Equal(sampleCount, reverseHits);

        // 第 9 步：输出报告。
        var totalRows = userCount + userCount * 2;
        Console.WriteLine();
        Console.WriteLine($"=== EDGE BENCHMARK REPORT ({engineName}) ===");
        Console.WriteLine($"Users: {userCount}, Edge rows: {userCount * 2}, Total rows: {totalRows}");
        Console.WriteLine($"[1] DecomposeUsers : {decomposeSw.ElapsedMilliseconds,8} ms");
        Console.WriteLine($"[2] BuildEdges     : {edgeBuildSw.ElapsedMilliseconds,8} ms  (bidirectional, index-as-data)");
        Console.WriteLine($"[3] BulkWrite      : {writeSw.ElapsedMilliseconds,8} ms  ({totalRows * 1000 / Math.Max(writeSw.ElapsedMilliseconds, 1)} rows/s)");
        Console.WriteLine($"[4] PointLookup x100 : {pointSw.ElapsedMilliseconds,8} ms");
        Console.WriteLine($"[5] Forward x{sampleCount}   : {forwardSw.ElapsedMilliseconds,8} ms  (avg {forwardSw.ElapsedMilliseconds / Math.Max(sampleCount, 1)} ms/query)");
        Console.WriteLine($"[6] Reverse x{sampleCount}   : {reverseSw.ElapsedMilliseconds,8} ms  (avg {reverseSw.ElapsedMilliseconds / Math.Max(sampleCount, 1)} ms/query)");
        Console.WriteLine("=== END REPORT ===");

        // 释放存储（FASTER 需要释放设备句柄）。
        if (disposeStore && store is IDisposable disposable)
        {
            // 释放。
            disposable.Dispose();
        }
    }

    /// <summary>
    /// InMemory 引擎 100K 边压测。
    /// </summary>
    [Fact]
    public async Task EdgeBenchmark_InMemory_100K()
    {
        // 进程内存储——抽样 200 次（O(n) 内存扫描快）。
        await RunEdgeBenchmarkAsync("IN-MEMORY DICTIONARY", new InMemoryUniversalStore(), 200, disposeStore: false);
    }

    /// <summary>
    /// FASTER 引擎 100K 边压测——ParentId 查询是全扫描，抽样次数减少以控制时长。
    /// </summary>
    [Fact]
    public async Task EdgeBenchmark_FasterKv_100K()
    {
        // 临时目录 + 压测后清理。
        var tempDir = System.IO.Path.Combine(AppContext.BaseDirectory, $"genesis_edge_bench_{Guid.NewGuid():N}");
        var store = new FasterKvUniversalStore(tempDir);

        try
        {
            // FASTER 全扫描代价高——抽样 20 次（诚实量化 O(n) 差异）。
            await RunEdgeBenchmarkAsync("FASTER KV (EMBEDDED LSM)", store, 20, disposeStore: false);
        }
        finally
        {
            // 释放设备。
            store.Dispose();

            // 尽力清理临时目录。
            try
            {
                // 目录存在才删。
                if (System.IO.Directory.Exists(tempDir))
                {
                    // 删除。
                    System.IO.Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (System.IO.IOException)
            {
                // 句柄延迟释放时忽略。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }

    /// <summary>
    /// SQL Server 磁盘表引擎 100K 边压测——走 ParentId 非聚集索引。
    /// </summary>
    [Fact]
    public async Task EdgeBenchmark_SqlServer_100K()
    {
        // 清池——磁盘表压测从干净状态开始。
        await TruncateTableAsync();

        // 磁盘表存储——抽样 200 次（B+ 树索引查询快）。
        await RunEdgeBenchmarkAsync(
            "SQL SERVER DISK TABLE",
            new SqlServerUniversalStore(new UniversalEntityRepository(new EmokingDbContext())),
            200,
            disposeStore: false);

        // 压测后清池——30 万行不留磁盘。
        await TruncateTableAsync();
    }
}
