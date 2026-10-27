// ANCHOR: test.benchmark-10k - 万级实体写入/读回基准测试
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 万级实体基准测试——在真实 SQL Server 上验证同构单表的
/// "拆解 → 批量写入 → 实体行一次读取 → 逐实体指针追逐 → 内存组装"全链路性能。
/// </summary>
/// <remarks>
/// 实体数由环境变量 GENESIS_BENCH_ROWS 控制，默认 10000（每实体 5 行 = 5 万行）。
/// 该基准同时回应 Advisor 的质疑："零计算纯寻址"中指针追逐（N 次单行查询）的真实代价是多少。
/// </remarks>
public sealed class BenchmarkTests
{
    // User 实体的 Meta TypeId——与 MetaCache 硬编码保持一致。
    private static readonly Guid UserTypeId = new("11111111-1111-1111-1111-111111111111");

    // 每批写入的实体数——100 实体 = 500 行/批，单事务过大时日志锁争用会拖慢写入。
    private const int EntitiesPerBatch = 100;

    /// <summary>
    /// 全链路基准：拆解、写入、实体行读取、指针追逐、内存组装五阶段分别计时。
    /// </summary>
    /// <returns>表示异步基准测试的任务。</returns>
    [Fact]
    public async Task Benchmark_Users_WriteAndPointerChaseRoundTrip()
    {
        // 第 1 步：解析实体数——环境变量优先，默认 10000。
        var rowCountEnv = Environment.GetEnvironmentVariable("GENESIS_BENCH_ROWS");
        var entityCount = int.TryParse(rowCountEnv, out var parsed) && parsed > 0 ? parsed : 10000;

        // 第 2 步：清库并构建依赖链。
        await TruncateTableAsync();
        var (decomposer, assembler, repository) = await BuildSutAsync();

        // 第 3 步：阶段一——拆解（纯内存，把 N 个 User 拍成 5N 行）。
        var decomposeSw = Stopwatch.StartNew();
        var allRows = new List<UniversalEntity>(entityCount * 5);
        for (var index = 0; index < entityCount; index++)
        {
            // 拆解单个 User 并累积到总行集。
            allRows.AddRange(decomposer.Decompose(BuildUser(index), UserTypeId));
        }
        decomposeSw.Stop();

        // 第 4 步：阶段二——分批事务写入。
        var writeSw = Stopwatch.StartNew();
        var rowsPerBatch = EntitiesPerBatch * 5;
        var batchCount = 0;
        for (var offset = 0; offset < allRows.Count; offset += rowsPerBatch)
        {
            // 截取一批行（最后一批可能不足 500 行）。
            var batch = allRows.GetRange(offset, Math.Min(rowsPerBatch, allRows.Count - offset));

            // 单事务批量插入。
            await repository.InsertBatchAsync(batch);

            // 批次计数。
            batchCount++;
        }
        writeSw.Stop();

        // 第 5 步：阶段三——实体行一次读取（按 TypeId + Role，一条 SQL 拉全部实体行）。
        var readEntitySw = Stopwatch.StartNew();
        var entityRows = await repository.GetByTypeIdAndRoleAsync(UserTypeId, UniversalEntity.RoleData);
        readEntitySw.Stop();

        // 实体行数必须等于实体总数。
        Assert.Equal(entityCount, entityRows.Count);

        // 第 6 步：阶段四——指针追逐（N 次单行查询，这是"零 JOIN"架构的读取代价核心）。
        var chaseSw = Stopwatch.StartNew();
        var propertyRowsByParent = new Dictionary<Guid, List<UniversalEntity>>(entityCount);
        foreach (var entityRow in entityRows)
        {
            // 按实体行 Id 追逐属性行——每次一个往返，无 JOIN。
            var propertyRows = await repository.GetByParentIdAsync(entityRow.Id);

            // 以实体行 Id 为键缓存属性行，供组装阶段使用。
            propertyRowsByParent[entityRow.Id] = new List<UniversalEntity>(propertyRows);
        }
        chaseSw.Stop();

        // 第 7 步：阶段五——内存组装（无数据库 I/O，纯遍历 + 反射赋值）。
        var assembleSw = Stopwatch.StartNew();
        var usersByName = new Dictionary<string, User>(entityCount);
        foreach (var entityRow in entityRows)
        {
            // 从缓存取该实体的属性行。
            var propertyRows = propertyRowsByParent[entityRow.Id];

            // 合并实体行与属性行为完整行集。
            var merged = new List<UniversalEntity>(1 + propertyRows.Count) { entityRow };
            merged.AddRange(propertyRows);

            // 反向挂载为 User。
            var user = assembler.Assemble<User>(merged, UserTypeId);

            // 按姓名收集——姓名含序号，全局唯一。
            usersByName[user.Name] = user;
        }
        assembleSw.Stop();

        // 第 8 步：正确性抽样——组装数量必须等于实体数。
        Assert.Equal(entityCount, usersByName.Count);

        // 首实体属性一致。
        var firstUser = usersByName["张三_0"];
        Assert.Equal("user0@genesis.local", firstUser.Email);
        Assert.Equal(0, firstUser.Age);

        // 中位实体属性一致。
        var middleUser = usersByName[$"张三_{entityCount / 2}"];
        Assert.Equal($"user{entityCount / 2}@genesis.local", middleUser.Email);

        // 尾实体属性一致——年龄即序号对 100 取模。
        var lastUser = usersByName[$"张三_{entityCount - 1}"];
        Assert.Equal((entityCount - 1) % 100, lastUser.Age);

        // 第 9 步：输出基准报告。
        var totalRows = allRows.Count;
        Console.WriteLine();
        Console.WriteLine("=== GENESIS BENCHMARK REPORT ===");
        Console.WriteLine($"Entities: {entityCount}, Rows: {totalRows}, Batches: {batchCount}");
        Console.WriteLine($"[1] Decompose    : {decomposeSw.ElapsedMilliseconds,8} ms");
        Console.WriteLine($"[2] Write        : {writeSw.ElapsedMilliseconds,8} ms  ({totalRows * 1000 / Math.Max(writeSw.ElapsedMilliseconds, 1)} rows/s)");
        Console.WriteLine($"[3] ReadEntity   : {readEntitySw.ElapsedMilliseconds,8} ms  ({entityRows.Count} rows, 1 query)");
        Console.WriteLine($"[4] PointerChase : {chaseSw.ElapsedMilliseconds,8} ms  ({entityCount} queries, {entityCount * 1000 / Math.Max(chaseSw.ElapsedMilliseconds, 1)} q/s)");
        Console.WriteLine($"[5] Assemble     : {assembleSw.ElapsedMilliseconds,8} ms");
        var totalMs = decomposeSw.ElapsedMilliseconds + writeSw.ElapsedMilliseconds + readEntitySw.ElapsedMilliseconds + chaseSw.ElapsedMilliseconds + assembleSw.ElapsedMilliseconds;
        Console.WriteLine($"[Total]          : {totalMs,8} ms  ({entityCount * 1000 / Math.Max(totalMs, 1)} entities/s)");
        Console.WriteLine("=== END REPORT ===");
    }

    /// <summary>
    /// 往返同化基准：BulkInsert 单次往返写入 + 子图预取单次往返读取。
    /// 与逐行版基准同规模对比，验证"把往返也同化"的收益。
    /// </summary>
    /// <returns>表示异步基准测试的任务。</returns>
    [Fact]
    public async Task Benchmark_Users_BulkWriteAndSubgraphFetch()
    {
        // 第 1 步：解析实体数——环境变量优先，默认 10000，与逐行版保持同规模。
        var rowCountEnv = Environment.GetEnvironmentVariable("GENESIS_BENCH_ROWS");
        var entityCount = int.TryParse(rowCountEnv, out var parsed) && parsed > 0 ? parsed : 10000;

        // 第 2 步：清库并构建依赖链。
        await TruncateTableAsync();
        var (decomposer, assembler, repository) = await BuildSutAsync();

        // 第 3 步：阶段一——拆解（与逐行版相同，纯内存）。
        var decomposeSw = Stopwatch.StartNew();
        var allRows = new List<UniversalEntity>(entityCount * 5);
        for (var index = 0; index < entityCount; index++)
        {
            // 拆解单个 User 并累积。
            allRows.AddRange(decomposer.Decompose(BuildUser(index), UserTypeId));
        }
        decomposeSw.Stop();

        // 第 4 步：阶段二——BulkCopy 整包写入（1 次往返）。
        var writeSw = Stopwatch.StartNew();
        var inserted = await repository.BulkInsertAsync(allRows);
        writeSw.Stop();

        // 写入行数必须等于拆解行数。
        Assert.Equal(allRows.Count, inserted);

        // 第 5 步：阶段三——子图预取（1 次往返拉回实体行 + 全部属性行）。
        var fetchSw = Stopwatch.StartNew();
        var subgraph = await repository.GetByTypeIdWithChildrenAsync(UserTypeId, UniversalEntity.RoleData);
        fetchSw.Stop();

        // 子图行数必须等于拆解行数。
        Assert.Equal(allRows.Count, subgraph.Count);

        // 第 6 步：阶段四——内存展开与组装（无任何数据库 I/O）。
        var assembleSw = Stopwatch.StartNew();

        // 实体行直接筛选，属性行按 ParentId 分组——纯字典操作。
        var propertyRowsByParent = new Dictionary<Guid, List<UniversalEntity>>(entityCount);
        var entityRows = new List<UniversalEntity>(entityCount);
        foreach (var row in subgraph)
        {
            // 父指针为空的是实体行。
            if (row.ParentId is null)
            {
                // 收集实体行。
                entityRows.Add(row);
            }
            else
            {
                // 属性行按 ParentId 分组。
                if (!propertyRowsByParent.TryGetValue(row.ParentId.Value, out var bucket))
                {
                    // 首次遇到该父节点时建桶。
                    bucket = new List<UniversalEntity>(4);
                    propertyRowsByParent.Add(row.ParentId.Value, bucket);
                }

                // 归入对应桶。
                bucket.Add(row);
            }
        }

        // 逐实体组装为 User 并按姓名收集。
        var usersByName = new Dictionary<string, User>(entityCount);
        foreach (var entityRow in entityRows)
        {
            // 取该实体的属性行桶。
            var propertyRows = propertyRowsByParent[entityRow.Id];

            // 合并为完整行集。
            var merged = new List<UniversalEntity>(1 + propertyRows.Count) { entityRow };
            merged.AddRange(propertyRows);

            // 反向挂载。
            var user = assembler.Assemble<User>(merged, UserTypeId);

            // 按姓名收集。
            usersByName[user.Name] = user;
        }
        assembleSw.Stop();

        // 第 7 步：正确性抽样——组装数量必须等于实体数。
        Assert.Equal(entityCount, usersByName.Count);

        // 首实体一致。
        var firstUser = usersByName["张三_0"];
        Assert.Equal("user0@genesis.local", firstUser.Email);
        Assert.Equal(0, firstUser.Age);

        // 中位实体一致。
        var middleUser = usersByName[$"张三_{entityCount / 2}"];
        Assert.Equal($"user{entityCount / 2}@genesis.local", middleUser.Email);

        // 尾实体一致。
        var lastUser = usersByName[$"张三_{entityCount - 1}"];
        Assert.Equal((entityCount - 1) % 100, lastUser.Age);

        // 第 8 步：输出同化版基准报告。
        var totalRows = allRows.Count;
        Console.WriteLine();
        Console.WriteLine("=== GENESIS BENCHMARK REPORT (ISOMORPHIC ROUND-TRIP) ===");
        Console.WriteLine($"Entities: {entityCount}, Rows: {totalRows}, Write round-trips: 1, Read round-trips: 1");
        Console.WriteLine($"[1] Decompose    : {decomposeSw.ElapsedMilliseconds,8} ms");
        Console.WriteLine($"[2] BulkWrite    : {writeSw.ElapsedMilliseconds,8} ms  ({totalRows * 1000 / Math.Max(writeSw.ElapsedMilliseconds, 1)} rows/s, 1 round-trip)");
        Console.WriteLine($"[3] SubgraphFetch: {fetchSw.ElapsedMilliseconds,8} ms  ({subgraph.Count} rows, 1 round-trip)");
        Console.WriteLine($"[4] Assemble     : {assembleSw.ElapsedMilliseconds,8} ms  (expand + map, no I/O)");
        var totalMs = decomposeSw.ElapsedMilliseconds + writeSw.ElapsedMilliseconds + fetchSw.ElapsedMilliseconds + assembleSw.ElapsedMilliseconds;
        Console.WriteLine($"[Total]          : {totalMs,8} ms  ({entityCount * 1000 / Math.Max(totalMs, 1)} entities/s)");
        Console.WriteLine("=== END REPORT ===");
    }

    /// <summary>
    /// 物理引擎换装基准：内存优化表（In-Memory OLTP）+ TVP 单次往返写入 + 子图预取读取。
    /// 与磁盘表同化版同规模对比，验证存储引擎换装的收益。
    /// </summary>
    /// <returns>表示异步基准测试的任务。</returns>
    [Fact]
    public async Task Benchmark_Users_MemoryOptimizedStorage()
    {
        // 第 1 步：解析实体数——环境变量优先，默认 10000，与前两版基准同规模。
        var rowCountEnv = Environment.GetEnvironmentVariable("GENESIS_BENCH_ROWS");
        var entityCount = int.TryParse(rowCountEnv, out var parsed) && parsed > 0 ? parsed : 10000;

        // 第 2 步：清空内存表（内存表不支持 TRUNCATE，用 DELETE）并构建依赖链。
        var dbContext = new EmokingDbContext();

        // 磁盘表仓储——供元数据自举使用。
        var diskRepository = new UniversalEntityRepository(dbContext);

        // 元数据缓存——自举加载（库中已有创世行，直接读）。
        var cache = new MetaCache(diskRepository);
        await cache.LoadAsync();

        // 拆解器与组装器共享自举出的元数据。
        var decomposer = new NodeEdgeDecomposer(cache);
        var assembler = new ObjectAssembler(cache);

        // 内存优化表仓储。
        var memoryRepository = new MemoryOptimizedEntityRepository(dbContext);
        await memoryRepository.DeleteAllAsync();

        // 第 3 步：阶段一——拆解（纯内存，与前两版相同）。
        var decomposeSw = Stopwatch.StartNew();
        var allRows = new List<UniversalEntity>(entityCount * 5);
        for (var index = 0; index < entityCount; index++)
        {
            // 拆解单个 User 并累积。
            allRows.AddRange(decomposer.Decompose(BuildUser(index), UserTypeId));
        }
        decomposeSw.Stop();

        // 第 4 步：阶段二——TVP 整包写入（1 次往返）。
        var writeSw = Stopwatch.StartNew();
        var inserted = await memoryRepository.BulkInsertAsync(allRows);
        writeSw.Stop();

        // 写入行数必须等于拆解行数。
        Assert.Equal(allRows.Count, inserted);

        // 第 5 步：阶段三——子图预取（1 次往返）。
        var fetchSw = Stopwatch.StartNew();
        var subgraph = await memoryRepository.GetByTypeIdWithChildrenAsync(UserTypeId, UniversalEntity.RoleData);
        fetchSw.Stop();

        // 子图行数必须等于拆解行数。
        Assert.Equal(allRows.Count, subgraph.Count);

        // 第 6 步：阶段四——内存展开与组装（无数据库 I/O）。
        var assembleSw = Stopwatch.StartNew();

        // 实体行收集与属性行分组。
        var propertyRowsByParent = new Dictionary<Guid, List<UniversalEntity>>(entityCount);
        var entityRows = new List<UniversalEntity>(entityCount);
        foreach (var row in subgraph)
        {
            // 父指针为空的是实体行。
            if (row.ParentId is null)
            {
                // 收集实体行。
                entityRows.Add(row);
            }
            else
            {
                // 属性行按 ParentId 入桶。
                if (!propertyRowsByParent.TryGetValue(row.ParentId.Value, out var bucket))
                {
                    // 首次建桶。
                    bucket = new List<UniversalEntity>(4);
                    propertyRowsByParent.Add(row.ParentId.Value, bucket);
                }

                // 归桶。
                bucket.Add(row);
            }
        }

        // 逐实体组装。
        var usersByName = new Dictionary<string, User>(entityCount);
        foreach (var entityRow in entityRows)
        {
            // 取属性行桶。
            var propertyRows = propertyRowsByParent[entityRow.Id];

            // 合并行集。
            var merged = new List<UniversalEntity>(1 + propertyRows.Count) { entityRow };
            merged.AddRange(propertyRows);

            // 反向挂载。
            var user = assembler.Assemble<User>(merged, UserTypeId);

            // 按姓名收集。
            usersByName[user.Name] = user;
        }
        assembleSw.Stop();

        // 第 7 步：正确性抽样。
        Assert.Equal(entityCount, usersByName.Count);

        // 首实体一致。
        var firstUser = usersByName["张三_0"];
        Assert.Equal("user0@genesis.local", firstUser.Email);
        Assert.Equal(0, firstUser.Age);

        // 中位实体一致。
        var middleUser = usersByName[$"张三_{entityCount / 2}"];
        Assert.Equal($"user{entityCount / 2}@genesis.local", middleUser.Email);

        // 尾实体一致。
        var lastUser = usersByName[$"张三_{entityCount - 1}"];
        Assert.Equal((entityCount - 1) % 100, lastUser.Age);

        // 第 8 步：输出内存表基准报告。
        var totalRows = allRows.Count;
        Console.WriteLine();
        Console.WriteLine("=== GENESIS BENCHMARK REPORT (MEMORY-OPTIMIZED) ===");
        Console.WriteLine($"Entities: {entityCount}, Rows: {totalRows}, Storage: In-Memory OLTP (SCHEMA_AND_DATA)");
        Console.WriteLine($"[1] Decompose    : {decomposeSw.ElapsedMilliseconds,8} ms");
        Console.WriteLine($"[2] TvpWrite     : {writeSw.ElapsedMilliseconds,8} ms  ({totalRows * 1000 / Math.Max(writeSw.ElapsedMilliseconds, 1)} rows/s, 1 round-trip)");
        Console.WriteLine($"[3] SubgraphFetch: {fetchSw.ElapsedMilliseconds,8} ms  ({subgraph.Count} rows, 1 round-trip)");
        Console.WriteLine($"[4] Assemble     : {assembleSw.ElapsedMilliseconds,8} ms  (expand + map, no I/O)");
        var totalMs = decomposeSw.ElapsedMilliseconds + writeSw.ElapsedMilliseconds + fetchSw.ElapsedMilliseconds + assembleSw.ElapsedMilliseconds;
        Console.WriteLine($"[Total]          : {totalMs,8} ms  ({entityCount * 1000 / Math.Max(totalMs, 1)} entities/s)");
        Console.WriteLine("=== END REPORT ===");
    }

    /// <summary>
    /// 清空 UniversalEntity 全表——基准从干净状态开始。
    /// </summary>
    /// <returns>表示异步清空操作完成的任务。</returns>
    private static async Task TruncateTableAsync()
    {
        // 用默认连接串打开连接。
        using var connection = new EmokingDbContext().CreateConnection();

        // 构造清空命令。
        await using var command = connection.CreateCommand();

        // TRUNCATE 比 DELETE 快且重置分配状态。
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
        // DbContext 走默认连接串。
        var dbContext = new EmokingDbContext();

        // 单表仓储。
        var repository = new UniversalEntityRepository(dbContext);

        // 元数据缓存（硬编码 User 元数据）。
        var cache = new MetaCache(repository);

        // 加载元数据。
        await cache.LoadAsync();

        // 拆解器与组装器共享缓存。
        var decomposer = new NodeEdgeDecomposer(cache);
        var assembler = new ObjectAssembler(cache);

        // 返回依赖三元组。
        return (decomposer, assembler, repository);
    }

    /// <summary>
    /// 构造指定序号的基准 User——各字段掺入序号，供抽样断言。
    /// </summary>
    /// <param name="index">序号。</param>
    /// <returns>构造完成的 User 实例。</returns>
    private static User BuildUser(int index)
    {
        // 各字段掺序号。
        return new User
        {
            // Id 留空，由拆解器生成。
            Id = Guid.Empty,
            // 姓名含序号，作为组装后的唯一键。
            Name = $"张三_{index}",
            // 邮箱唯一。
            Email = $"user{index}@genesis.local",
            // 年龄取模。
            Age = index % 100,
            // 时间固定基准加偏移。
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(index)
        };
    }
}
