// ANCHOR: dal.store.faster-kv - FASTER 嵌入式 LSM 存储实现（可持久化、进程内）
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FASTER.core;

namespace Emoking.DAL;

/// <summary>
/// FASTER KV 存储实现——微软嵌入式 LSM 引擎，进程内速度 + 磁盘持久化。
/// </summary>
/// <remarks>
/// 实测（5 万行）：写入 61.7 万行/s、读回 131.6 万行/s。
/// Value 载荷为 <see cref="UniversalEntityCodec"/> 打包字节；子图预取通过 FASTER 迭代器全量扫描过滤。
/// 内存预算 32MB（2^25）为 5 万行规模的实测安全值，更大规模应按数据量调大。
/// </remarks>
public sealed class FasterKvUniversalStore : IUniversalStore, IDisposable
{
    // 哈希桶数——2^20 支撑到百万级行。
    private const long HashBucketCount = 1L << 20;

    // 内存预算位宽——2^27 字节 = 128MB 常驻内存窗口（第 13 轮 30 万行压测容量）。
    private const int MemorySizeBits = 27;

    // 页位宽——1MB 页减少页切换。
    private const int PageSizeBits = 20;

    // LSM 主日志设备。
    private readonly IDevice _logDevice;

    // 对象日志设备（byte[] 引用载荷需要）。
    private readonly IDevice _objectLogDevice;

    // FASTER KV 实例。
    private readonly FasterKV<Guid, byte[]> _store;

    /// <summary>
    /// 初始化 FASTER 存储——日志文件落在指定目录。
    /// </summary>
    /// <param name="logDirectory">LSM 日志目录；不存在时自动创建。</param>
    public FasterKvUniversalStore(string logDirectory)
    {
        // 保证目录存在。
        Directory.CreateDirectory(logDirectory);

        // 创建主日志与对象日志设备——文件名固定，便于重建时定位。
        _logDevice = Devices.CreateLogDevice(Path.Combine(logDirectory, "genesis_faster.log"));
        _objectLogDevice = Devices.CreateLogDevice(Path.Combine(logDirectory, "genesis_faster.obj.log"));

        // 创建 KV 实例。
        _store = new FasterKV<Guid, byte[]>(
            HashBucketCount,
            new LogSettings
            {
                // 主日志设备。
                LogDevice = _logDevice,
                // 对象日志设备。
                ObjectLogDevice = _objectLogDevice,
                // 内存预算。
                MemorySizeBits = MemorySizeBits,
                // 页大小。
                PageSizeBits = PageSizeBits
            });
    }

    /// <summary>
    /// 新建一个会话——session 类型是 FASTER 内部实现细节，按需创建用完即弃。
    /// </summary>
    /// <returns>新的客户端会话。</returns>
    private ClientSession<Guid, byte[], byte[], byte[], Empty, SimpleFunctions<Guid, byte[]>> NewSession()
    {
        // 从 store 打开会话——SimpleFunctions 提供 Guid/byte[] 的默认比较与拷贝。
        return _store
            .For(new SimpleFunctions<Guid, byte[]>())
            .NewSession<SimpleFunctions<Guid, byte[]>>();
    }

    /// <summary>
    /// 整包写入——纯插入语义：写入前做存在性检查，同 Id 已存在时显式失败。
    /// </summary>
    /// <remarks>
    /// 第 20 轮语义对齐：BulkInsert 是纯插入（FASTER 的 Upsert 天然覆盖，
    /// 纯插语义靠先 Read 检查实现）；覆盖写入请用 <see cref="UpsertAsync"/>。
    /// </remarks>
    /// <param name="entities">待写入的行集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入的行数。</returns>
    /// <exception cref="InvalidOperationException">同 Id 行已存在时抛出。</exception>
    public Task<int> BulkInsertAsync(
        IReadOnlyList<UniversalEntity> entities,
        CancellationToken cancellationToken = default)
    {
        // 打开会话。
        using var session = NewSession();

        // 逐行存在性检查 + 写入。
        foreach (var entity in entities)
        {
            // 准备 ref 参数。
            var key = entity.Id;
            var input = Array.Empty<byte>();
            var output = Array.Empty<byte>();

            // 先读——FASTER 的 Upsert 天然覆盖，纯插语义靠存在性检查保证。
            session.Read(ref key, ref input, ref output);

            // 命中即冲突（软删行按存在处理——物理记录仍在）。
            if (output.Length > 0)
            {
                // 抛出冲突异常。
                throw new InvalidOperationException(
                    $"行 {entity.Id} 已存在——BulkInsert 是纯插入语义，覆盖写入请使用 UpsertAsync。");
            }

            // 打包写入。
            var value = UniversalEntityCodec.Pack(entity);

            // 纯插入。
            session.Upsert(ref key, ref value);
        }

        // 返回写入行数。
        return Task.FromResult(entities.Count);
    }

    /// <summary>
    /// 子图预取——FASTER 无二级索引，用迭代器全量扫描后按同构指针语义过滤。
    /// </summary>
    /// <param name="entityTypeId">实体类型的 Meta TypeId。</param>
    /// <param name="role">实体行角色常量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>子图内全部未删除行。</returns>
    public Task<IReadOnlyList<UniversalEntity>> GetSubGraphAsync(
        Guid entityTypeId,
        byte role,
        CancellationToken cancellationToken = default)
    {
        // 打开会话。
        using var session = NewSession();

        // 结果集。
        var results = new List<UniversalEntity>();

        // 锚点集——本类型实体行 Id。
        var entityIds = new HashSet<Guid>();

        // 第一遍扫描：收集实体行。
        using (var iterator = session.Iterate())
        {
            // 逐条推进迭代器。
            while (iterator.GetNext(out var recordInfo, out var key, out var value))
            {
                // 已删除逻辑地址跳过（迭代器可能读到已删除记录）。
                if (recordInfo.Tombstone)
                {
                    // 跳过。
                    continue;
                }

                // 解包。
                var row = UniversalEntityCodec.Unpack(value);

                // 匹配实体行条件。
                if (row.TypeId == entityTypeId && row.Role == role && row.ParentId is null && !row.IsDeleted)
                {
                    // 加入锚点集与结果集。
                    entityIds.Add(row.Id);
                    results.Add(row);
                }
            }
        }

        // 第二遍扫描：收集属性行。
        using (var iterator = session.Iterate())
        {
            // 逐条推进。
            while (iterator.GetNext(out var recordInfo, out var key, out var value))
            {
                // 已删除记录跳过。
                if (recordInfo.Tombstone)
                {
                    // 跳过。
                    continue;
                }

                // 解包。
                var row = UniversalEntityCodec.Unpack(value);

                // 属性行命中：未删除、ParentId 指向锚点集、且非历史行（Role=6 排除）。
                if (!row.IsDeleted
                    && row.ParentId is not null
                    && entityIds.Contains(row.ParentId.Value)
                    && row.Role != UniversalEntity.RoleHistory)
                {
                    // 加入结果集。
                    results.Add(row);
                }
            }
        }

        // 返回只读子图。
        return Task.FromResult<IReadOnlyList<UniversalEntity>>(results);
    }

    /// <summary>
    /// 按主键点查——哈希索引 O(1)。
    /// </summary>
    /// <param name="id">行 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到且未删除返回行；否则返回 null。</returns>
    public Task<UniversalEntity?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        // 打开会话。
        using var session = NewSession();

        // 准备 ref 参数。
        var key = id;
        var input = Array.Empty<byte>();
        var output = Array.Empty<byte>();

        // FASTER 读取。
        session.Read(ref key, ref input, ref output);

        // 空数组表示未命中。
        if (output.Length == 0)
        {
            // 未命中。
            return Task.FromResult<UniversalEntity?>(null);
        }

        // 解包。
        var row = UniversalEntityCodec.Unpack(output);

        // 已软删视为不存在。
        if (row.IsDeleted)
        {
            // 返回 null。
            return Task.FromResult<UniversalEntity?>(null);
        }

        // 返回行。
        return Task.FromResult<UniversalEntity?>(row);
    }

    /// <summary>
    /// 指针追逐——FASTER 无二级索引，迭代器全扫描过滤 ParentId。
    /// </summary>
    /// <remarks>
    /// 诚实代价：FASTER 的 ParentId 查询是 O(n) 全扫描（每次迭代整个键空间），
    /// 与磁盘表的 B+ 树索引不同——压测数据将量化这一差异。
    /// </remarks>
    /// <param name="parentId">父节点 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全部 ParentId 命中的未删除行。</returns>
    public Task<IReadOnlyList<UniversalEntity>> GetByParentIdAsync(
        Guid parentId,
        CancellationToken cancellationToken = default)
    {
        // 打开会话。
        using var session = NewSession();

        // 结果集。
        var results = new List<UniversalEntity>();

        // 全量迭代。
        using (var iterator = session.Iterate())
        {
            // 逐条推进。
            while (iterator.GetNext(out var recordInfo, out var key, out var value))
            {
                // 已删除记录跳过。
                if (recordInfo.Tombstone)
                {
                    // 跳过。
                    continue;
                }

                // 解包。
                var row = UniversalEntityCodec.Unpack(value);

                // 匹配 ParentId、未删除、且非历史行（Role=6 排除，防历史污染活数据）。
                if (!row.IsDeleted && row.ParentId == parentId && row.Role != UniversalEntity.RoleHistory)
                {
                    // 命中。
                    results.Add(row);
                }
            }
        }

        // 返回只读结果。
        return Task.FromResult<IReadOnlyList<UniversalEntity>>(results);
    }

    /// <summary>
    /// 按 Id 软删——读出后打包 IsDeleted = true 的副本覆盖写回。
    /// </summary>
    /// <param name="id">行 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中返回 true。</returns>
    public async Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        // 先点查。
        var row = await GetByIdAsync(id, cancellationToken);

        // 未命中直接返回 false。
        if (row is null)
        {
            // 未命中。
            return false;
        }

        // 打开会话。
        using var session = NewSession();

        // 打包软删副本。
        var key = id;
        var value = UniversalEntityCodec.Pack(row with { IsDeleted = true });

        // 覆盖写回。
        session.Upsert(ref key, ref value);

        // 命中。
        return true;
    }

    /// <summary>
    /// 条件覆盖写入（CAS upsert）——行不存在则插入；存在则按 Version 乐观锁更新并落历史行。
    /// </summary>
    /// <remarks>
    /// FASTER 无事务与原生 CAS——本实现靠"读-校验-写"序列保证语义；
    /// 并发窗口内理论上存在竞态，与既有单会话语义水位一致（第 7 轮诚实边界）。
    /// 诚实边界：FASTER 的二进制打包不含 CreatedAt/UpdatedAt（codec 列限制），
    /// 历史行的时间戳审计能力以 SqlServer/InMemory 为准。
    /// </remarks>
    /// <param name="entities">待写入的行集合；更新行必须携带与存储一致的 Version。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>落库后的最终行集合。</returns>
    /// <exception cref="InvalidOperationException">Version 不匹配时抛出。</exception>
    public Task<IReadOnlyList<UniversalEntity>> UpsertAsync(
        IReadOnlyList<UniversalEntity> entities,
        CancellationToken cancellationToken = default)
    {
        // 打开会话。
        using var session = NewSession();

        // 收集落库后的最终行。
        var results = new List<UniversalEntity>(entities.Count);

        // 逐行处理。
        foreach (var entity in entities)
        {
            // 第 1 步：读现存行。
            var existing = ReadRow(session, entity.Id);

            // 分支一：行不存在（或已软删）→ 插入。
            if (existing is null || existing.IsDeleted)
            {
                // 打包写入。
                var insertKey = entity.Id;
                var insertValue = UniversalEntityCodec.Pack(entity);

                // 纯插入。
                session.Upsert(ref insertKey, ref insertValue);

                // 返回输入行。
                results.Add(entity);

                // 下一行。
                continue;
            }

            // 分支二前置校验：Version 快速比对。
            if (existing.Version != entity.Version)
            {
                // 抛出乐观锁冲突。
                throw new InvalidOperationException(
                    $"乐观锁冲突：行 {entity.Id} 存储版本为 {existing.Version}，请求版本为 {entity.Version}——请重读后重试。");
            }

            // 第 2 步：旧行落成历史行——Role=6、新 Id、ParentId 指向当前行。
            var historyRow = existing with
            {
                // 历史行 Id 换新。
                Id = Guid.NewGuid(),
                // 角色改为历史。
                Role = UniversalEntity.RoleHistory,
                // 挂到被更新的当前行下。
                ParentId = existing.Id
            };

            // 历史行打包写入（新 key 不冲突）。
            var historyKey = historyRow.Id;
            var historyValue = UniversalEntityCodec.Pack(historyRow);

            // 写入历史行。
            session.Upsert(ref historyKey, ref historyValue);

            // 第 3 步：构造更新后的行——Version 递增、CreatedAt 保留。
            var updatedRow = entity with
            {
                // 乐观锁版本递增。
                Version = existing.Version + 1,
                // 创建时间保留旧行值。
                CreatedAt = existing.CreatedAt,
                // 更新时间取当前。
                UpdatedAt = DateTime.UtcNow
            };

            // 覆盖主键。
            var mainKey = entity.Id;
            var mainValue = UniversalEntityCodec.Pack(updatedRow);

            // 覆盖写入。
            session.Upsert(ref mainKey, ref mainValue);

            // 返回落库后的最终行。
            results.Add(updatedRow);
        }

        // 返回只读结果。
        return Task.FromResult<IReadOnlyList<UniversalEntity>>(results);
    }

    /// <summary>
    /// 会话内按主键读一行（不做软删过滤——调用方自行判断）。
    /// </summary>
    /// <param name="session">FASTER 客户端会话。</param>
    /// <param name="id">行 Id。</param>
    /// <returns>读到的行；未命中返回 null。</returns>
    private static UniversalEntity? ReadRow(
        ClientSession<Guid, byte[], byte[], byte[], Empty, SimpleFunctions<Guid, byte[]>> session,
        Guid id)
    {
        // 准备 ref 参数。
        var key = id;
        var input = Array.Empty<byte>();
        var output = Array.Empty<byte>();

        // FASTER 读取。
        session.Read(ref key, ref input, ref output);

        // 空数组表示未命中。
        if (output.Length == 0)
        {
            // 未命中。
            return null;
        }

        // 解包返回。
        return UniversalEntityCodec.Unpack(output);
    }

    /// <summary>
    /// 批量软删——逐行委托单行软删（FASTER 无事务，与既有并发水位一致）。
    /// </summary>
    /// <param name="ids">待软删的行 Id 集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际软删的行数。</returns>
    public async Task<int> DeleteManyAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        // 累计命中数。
        var deleted = 0;

        // 逐行软删。
        foreach (var id in ids)
        {
            // 命中计数。
            if (await DeleteAsync(id, cancellationToken))
            {
                // 累计。
                deleted++;
            }
        }

        // 返回命中数。
        return deleted;
    }

    /// <summary>
    /// 实体审计历史链——迭代器全扫描过滤 Role=6 历史行（FASTER 无二级索引，诚实代价）。
    /// </summary>
    /// <param name="entityId">被审计的实体行 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>历史行集合，按更新时间升序。</returns>
    public Task<IReadOnlyList<UniversalEntity>> GetHistoryAsync(
        Guid entityId,
        CancellationToken cancellationToken = default)
    {
        // 打开会话。
        using var session = NewSession();

        // 结果集。
        var results = new List<UniversalEntity>();

        // 全量迭代。
        using (var iterator = session.Iterate())
        {
            // 逐条推进。
            while (iterator.GetNext(out var recordInfo, out var key, out var value))
            {
                // 已删除记录跳过。
                if (recordInfo.Tombstone)
                {
                    // 跳过。
                    continue;
                }

                // 解包。
                var row = UniversalEntityCodec.Unpack(value);

                // 历史行命中：未删除、ParentId 指向被审计实体、Role=6。
                if (!row.IsDeleted && row.ParentId == entityId && row.Role == UniversalEntity.RoleHistory)
                {
                    // 加入结果集。
                    results.Add(row);
                }
            }
        }

        // 时间线排序。
        var ordered = results.OrderBy(row => row.UpdatedAt).ToList();

        // 返回只读结果。
        return Task.FromResult<IReadOnlyList<UniversalEntity>>(ordered);
    }

    /// <summary>
    /// 释放 FASTER 存储与日志设备。
    /// </summary>
    public void Dispose()
    {
        // 存储先释放。
        _store.Dispose();

        // 设备最后。
        _logDevice.Dispose();
        _objectLogDevice.Dispose();
    }
}
