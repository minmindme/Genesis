// ANCHOR: test.in-process-kv - 去数据库化基准：进程内原生 KV 引擎
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using FASTER.core;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 去数据库化基准——把同构行直接存进进程内 KV 引擎，彻底消灭进程边界/协议解析/网络往返。
/// </summary>
/// <remarks>
/// 对比两个引擎（同规模 5 万行，与 SQL Server 三版基准可比）：
///   1. ConcurrentDictionary —— "终极同化一"字面实现：引擎内的 static 字典，纯内存对象引用。
///   2. FASTER KV —— 微软嵌入式 LSM KV（NuGet 即装、可持久化），"带持久化的进程内引擎"。
/// 行值以手工二进制打包（无 JSON、无反射、无序列化器），读回后复用 ObjectAssembler 组装。
/// Redis 基准未纳入：本机实测无 Redis 二进制且 6379 端口关闭，安装属系统级操作未获授权。
/// </remarks>
public sealed class InProcessKvBenchmarkTests
{
    // User 实体类型的 Meta TypeId——与库中创世行一致。
    private static readonly Guid UserTypeId = new("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// 同构行的手工二进制打包/解包——无任何框架序列化器的极简格式。
    /// </summary>
    private static class EntityBytes
    {
        /// <summary>
        /// 把一行 UniversalEntity 压成字节数组。
        /// </summary>
        /// <param name="entity">同构行。</param>
        /// <returns>打包后的字节。</returns>
        public static byte[] Pack(UniversalEntity entity)
        {
            // 值文本的 UTF-8 字节（可为空）。
            var valueBytes = entity.Value is null ? null : System.Text.Encoding.UTF8.GetBytes(entity.Value);

            // 总长度：Id16 + TypeId16 + Role1 + HasParent1 + (ParentId16?) + ValueLen4 + ValueBytes + Version4 + IsDeleted1。
            var length = 16 + 16 + 1 + 1 + (entity.ParentId is null ? 0 : 16) + 4 + (valueBytes?.Length ?? 0) + 4 + 1;

            // 分配缓冲。
            var buffer = new byte[length];

            // 写入偏移游标。
            var offset = 0;

            // Id 写入 16 字节。
            entity.Id.ToByteArray().CopyTo(buffer, offset);
            offset += 16;

            // TypeId 写入 16 字节。
            entity.TypeId.ToByteArray().CopyTo(buffer, offset);
            offset += 16;

            // Role 写入 1 字节。
            buffer[offset++] = entity.Role;

            // 父指针标志 + 可选 ParentId。
            if (entity.ParentId is null)
            {
                // 无父指针标志。
                buffer[offset++] = 0;
            }
            else
            {
                // 有父指针标志 + 16 字节。
                buffer[offset++] = 1;
                entity.ParentId.Value.ToByteArray().CopyTo(buffer, offset);
                offset += 16;
            }

            // 值长度（-1 表示 null）+ 值字节。
            if (valueBytes is null)
            {
                // null 值标志。
                WriteInt32(buffer, offset, -1);
                offset += 4;
            }
            else
            {
                // 值长度 + 值字节。
                WriteInt32(buffer, offset, valueBytes.Length);
                offset += 4;
                valueBytes.CopyTo(buffer, offset);
                offset += valueBytes.Length;
            }

            // Version 写入 4 字节。
            WriteInt32(buffer, offset, entity.Version);
            offset += 4;

            // IsDeleted 写入 1 字节。
            buffer[offset] = entity.IsDeleted ? (byte)1 : (byte)0;

            // 返回打包结果。
            return buffer;
        }

        /// <summary>
        /// 从字节数组解包一行 UniversalEntity。
        /// </summary>
        /// <param name="buffer">打包字节。</param>
        /// <returns>解包完成的同构行。</returns>
        public static UniversalEntity Unpack(byte[] buffer)
        {
            // 读取偏移游标。
            var offset = 0;

            // 读 Id。
            var id = new Guid(buffer.AsSpan(0, 16));
            offset += 16;

            // 读 TypeId。
            var typeId = new Guid(buffer.AsSpan(16, 16));
            offset += 16;

            // 读 Role。
            var role = buffer[offset++];

            // 读父指针。
            Guid? parentId = null;

            // 有父指针标志时读 16 字节。
            if (buffer[offset++] == 1)
            {
                // 读 ParentId。
                parentId = new Guid(buffer.AsSpan(offset, 16));
                offset += 16;
            }

            // 读值长度。
            var valueLength = ReadInt32(buffer, offset);
            offset += 4;

            // 读值文本。
            string? value = null;

            // 长度非 -1 时解码 UTF-8。
            if (valueLength >= 0)
            {
                // 解码。
                value = System.Text.Encoding.UTF8.GetString(buffer, offset, valueLength);
            }

            // 读 Version。
            var version = ReadInt32(buffer, offset + (valueLength >= 0 ? valueLength : 0));

            // 读 IsDeleted（尾部 1 字节）。
            var isDeleted = buffer[^1] == 1;

            // 构造行。
            return new UniversalEntity(
                Id: id,
                TypeId: typeId,
                Role: role,
                ParentId: parentId,
                Value: value,
                Version: version,
                IsDeleted: isDeleted);
        }

        /// <summary>
        /// 小端写入 Int32。
        /// </summary>
        private static void WriteInt32(byte[] buffer, int offset, int value)
        {
            // 逐字节小端写入。
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
            buffer[offset + 2] = (byte)(value >> 16);
            buffer[offset + 3] = (byte)(value >> 24);
        }

        /// <summary>
        /// 小端读取 Int32。
        /// </summary>
        private static int ReadInt32(byte[] buffer, int offset)
        {
            // 逐字节小端拼装。
            return buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24);
        }
    }

    /// <summary>
    /// 进程内字典基准——拆解、打包、Upsert、读回、解包、组装全链路计时。
    /// </summary>
    /// <returns>表示异步基准测试的任务。</returns>
    [Fact]
    public async Task Benchmark_InProcess_ConcurrentDictionary()
    {
        // 第 1 步：同规模参数与依赖链（元数据从库中自举）。
        var (entityCount, decomposer, assembler) = await BuildDependenciesAsync();

        // 第 2 步：阶段一——拆解。
        var decomposeSw = Stopwatch.StartNew();
        var allRows = new List<UniversalEntity>(entityCount * 5);
        for (var index = 0; index < entityCount; index++)
        {
            // 拆解单个 User 并累积。
            allRows.AddRange(decomposer.Decompose(BuildUser(index), UserTypeId));
        }
        decomposeSw.Stop();

        // 第 3 步：阶段二——打包并 Upsert 进进程内字典。
        var store = new ConcurrentDictionary<Guid, byte[]>(Environment.ProcessorCount, entityCount * 5);
        var writeSw = Stopwatch.StartNew();
        foreach (var row in allRows)
        {
            // 打包为二进制后插入——Key 是行 Id，Value 是同构数据包。
            store[row.Id] = EntityBytes.Pack(row);
        }
        writeSw.Stop();

        // 第 4 步：阶段三——全量读回并解包（无任何 I/O，纯字典遍历）。
        var readSw = Stopwatch.StartNew();
        var rows = new List<UniversalEntity>(store.Count);
        foreach (var pair in store)
        {
            // 解包为同构行。
            rows.Add(EntityBytes.Unpack(pair.Value));
        }
        readSw.Stop();

        // 第 5 步：阶段四——内存分组与组装。
        var assembleSw = Stopwatch.StartNew();
        var usersByName = AssembleAllUsers(rows, assembler, entityCount);
        assembleSw.Stop();

        // 第 6 步：抽样断言。
        AssertStoreCorrectness(usersByName, entityCount);

        // 第 7 步：输出报告。
        PrintReport("CONCURRENTDICTIONARY (IN-PROCESS)", entityCount, rows.Count,
            decomposeSw.ElapsedMilliseconds, writeSw.ElapsedMilliseconds,
            readSw.ElapsedMilliseconds, assembleSw.ElapsedMilliseconds);
    }

    /// <summary>
    /// FASTER KV 基准——嵌入式 LSM 引擎（可持久化），同样全链路计时。
    /// </summary>
    /// <returns>表示异步基准测试的任务。</returns>
    [Fact]
    public async Task Benchmark_InProcess_FasterKv()
    {
        // 第 1 步：同规模参数与依赖链。
        var (entityCount, decomposer, assembler) = await BuildDependenciesAsync();

        // 临时日志设备——FASTER 的 LSM 落盘位置（用完清理）。
        var logPath = Path.Combine(Path.GetTempPath(), $"genesis_faster_{Guid.NewGuid():N}.log");
        var objLogPath = Path.Combine(Path.GetTempPath(), $"genesis_faster_{Guid.NewGuid():N}.obj.log");

        // 创建日志设备——key 为 Guid（blittable），value 为 byte[]（需对象日志）。
        var logDevice = Devices.CreateLogDevice(logPath);
        var objectLogDevice = Devices.CreateLogDevice(objLogPath);

        // 创建 FASTER KV 实例——1M 哈希桶、32MB 内存预算（2^25；5 万行约 5-8MB，1MB 会强制刷盘卡死）。
        using var store = new FasterKV<Guid, byte[]>(
            1L << 20,
            new LogSettings
            {
                // 主日志设备。
                LogDevice = logDevice,
                // 对象日志设备（byte[] 引用需要）。
                ObjectLogDevice = objectLogDevice,
                // 内存位宽：2^25 字节 = 32MB 常驻内存窗口。
                MemorySizeBits = 25,
                // 页位宽：1MB 页，减少页切换开销。
                PageSizeBits = 20
            });

        // 创建会话——SimpleFunctions 提供 Guid/byte[] 的默认比较与拷贝。
        using var session = store
            .For(new SimpleFunctions<Guid, byte[]>())
            .NewSession<SimpleFunctions<Guid, byte[]>>();

        // 第 2 步：阶段一——拆解。
        var decomposeSw = Stopwatch.StartNew();
        var allRows = new List<UniversalEntity>(entityCount * 5);
        for (var index = 0; index < entityCount; index++)
        {
            // 拆解单个 User 并累积。
            allRows.AddRange(decomposer.Decompose(BuildUser(index), UserTypeId));
        }
        decomposeSw.Stop();

        // 第 3 步：阶段二——打包并 Upsert 进 FASTER。
        var writeSw = Stopwatch.StartNew();
        foreach (var row in allRows)
        {
            // 打包。
            var packed = EntityBytes.Pack(row);

            // 准备 ref 局部变量——FASTER API 需要 ref 传参。
            var key = row.Id;
            var value = packed;

            // 插入。
            session.Upsert(ref key, ref value);
        }
        writeSw.Stop();

        // 第 4 步：阶段三——全量读回并解包。
        var readSw = Stopwatch.StartNew();
        var rows = new List<UniversalEntity>(allRows.Count);
        foreach (var row in allRows)
        {
            // 准备 key 与 ref 输入/输出——FASTER 2.6 的 Read 要求全部 ref 传参。
            var key = row.Id;
            var input = Array.Empty<byte>();
            var output = Array.Empty<byte>();

            // FASTER 读取——数据全在内存 tail；未命中时 output 保持空数组直接跳过。
            session.Read(ref key, ref input, ref output);
            if (output.Length > 0)
            {
                // 解包。
                rows.Add(EntityBytes.Unpack(output));
            }
        }
        readSw.Stop();

        // 第 5 步：阶段四——分组组装。
        var assembleSw = Stopwatch.StartNew();
        var usersByName = AssembleAllUsers(rows, assembler, entityCount);
        assembleSw.Stop();

        // 第 6 步：抽样断言。
        AssertStoreCorrectness(usersByName, entityCount);

        // 第 7 步：输出报告。
        PrintReport("FASTER KV (IN-PROCESS EMBEDDED LSM)", entityCount, rows.Count,
            decomposeSw.ElapsedMilliseconds, writeSw.ElapsedMilliseconds,
            readSw.ElapsedMilliseconds, assembleSw.ElapsedMilliseconds);

        // 第 8 步：清理临时设备文件。
        session.Dispose();
        store.Dispose();
        logDevice.Dispose();
        objectLogDevice.Dispose();
        foreach (var path in new[] { logPath, objLogPath })
        {
            // 文件存在则删除。
            if (File.Exists(path))
            {
                // 删除。
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// 构建基准依赖：元数据自举加载 + 拆解器/组装器。
    /// </summary>
    /// <returns>实体数、拆解器与组装器。</returns>
    private static async Task<(int entityCount, NodeEdgeDecomposer decomposer, ObjectAssembler assembler)> BuildDependenciesAsync()
    {
        // 行数配置——默认 10000，与其他基准同规模。
        var rowCountEnv = Environment.GetEnvironmentVariable("GENESIS_BENCH_ROWS");
        var entityCount = int.TryParse(rowCountEnv, out var parsed) && parsed > 0 ? parsed : 10000;

        // 磁盘表仓储 + 元数据缓存自举。
        var cache = new MetaCache(new UniversalEntityRepository(new EmokingDbContext()));
        await cache.LoadAsync();

        // 拆解器与组装器共享自举元数据。
        var decomposer = new NodeEdgeDecomposer(cache);
        var assembler = new ObjectAssembler(cache);

        // 返回依赖。
        return (entityCount, decomposer, assembler);
    }

    /// <summary>
    /// 构造基准 User——各字段掺入序号。
    /// </summary>
    private static User BuildUser(int index)
    {
        // 各字段掺序号。
        return new User
        {
            // Id 由拆解器生成。
            Id = Guid.Empty,
            // 姓名含序号。
            Name = $"张三_{index}",
            // 邮箱唯一。
            Email = $"user{index}@genesis.local",
            // 年龄取模。
            Age = index % 100,
            // 时间固定基准偏移。
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(index)
        };
    }

    /// <summary>
    /// 把行集合按父子关系分组并组装为 User 字典。
    /// </summary>
    /// <param name="rows">全部同构行。</param>
    /// <param name="assembler">组装器。</param>
    /// <param name="entityCount">预期实体数（预分配容量）。</param>
    /// <returns>姓名 → User 字典。</returns>
    private static Dictionary<string, User> AssembleAllUsers(
        List<UniversalEntity> rows,
        ObjectAssembler assembler,
        int entityCount)
    {
        // 实体行与属性行分拣。
        var propertyRowsByParent = new Dictionary<Guid, List<UniversalEntity>>(entityCount);
        var entityRows = new List<UniversalEntity>(entityCount);

        // 遍历分拣。
        foreach (var row in rows)
        {
            // 父指针为空的是实体行。
            if (row.ParentId is null)
            {
                // 收集实体行。
                entityRows.Add(row);
            }
            else
            {
                // 属性行入桶。
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

            // 收集。
            usersByName[user.Name] = user;
        }

        // 返回结果。
        return usersByName;
    }

    /// <summary>
    /// 组装结果抽样断言——首/中/尾实体属性一致。
    /// </summary>
    private static void AssertStoreCorrectness(Dictionary<string, User> usersByName, int entityCount)
    {
        // 组装数量必须等于实体数。
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
    }

    /// <summary>
    /// 输出统一格式的基准报告。
    /// </summary>
    private static void PrintReport(
        string engineName,
        int entityCount,
        int rowCount,
        long decomposeMs,
        long writeMs,
        long readMs,
        long assembleMs)
    {
        // 汇总。
        var totalMs = decomposeMs + writeMs + readMs + assembleMs;

        // 打印。
        Console.WriteLine();
        Console.WriteLine($"=== GENESIS BENCHMARK REPORT ({engineName}) ===");
        Console.WriteLine($"Entities: {entityCount}, Rows: {rowCount}, IPC: NONE (in-process)");
        Console.WriteLine($"[1] Decompose    : {decomposeMs,8} ms");
        Console.WriteLine($"[2] Write        : {writeMs,8} ms  ({rowCount * 1000 / Math.Max(writeMs, 1)} rows/s)");
        Console.WriteLine($"[3] ReadBack     : {readMs,8} ms  ({rowCount * 1000 / Math.Max(readMs, 1)} rows/s)");
        Console.WriteLine($"[4] Assemble     : {assembleMs,8} ms");
        Console.WriteLine($"[Total]          : {totalMs,8} ms  ({entityCount * 1000 / Math.Max(totalMs, 1)} entities/s)");
        Console.WriteLine("=== END REPORT ===");
    }
}
