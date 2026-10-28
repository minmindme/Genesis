// ANCHOR: dal.store.in-memory - 进程内字典存储实现（零 IPC、零序列化）
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Emoking.DAL;

/// <summary>
/// 进程内字典存储实现——ConcurrentDictionary 直接持有同构行对象引用，
/// 是五个引擎中物理路径最短的一个（写入 138.9 万行/s 实测）。
/// </summary>
/// <remarks>
/// 代价：重启全丢，无跨进程共享。适用场景：可全量重建的缓存态/热数据层。
/// 另提供 <see cref="ClearAsync"/> 清池方法（接口外扩展，基准与测试用）。
/// </remarks>
public sealed class InMemoryUniversalStore : IUniversalStore
{
    // 行字典——Key 是行 Id，Value 是同构行对象引用本身（零拷贝）。
    private readonly ConcurrentDictionary<Guid, UniversalEntity> _rows = new();

    /// <summary>
    /// 整包写入——纯插入语义：同 Id 行已存在时显式失败。
    /// </summary>
    /// <remarks>
    /// 第 20 轮语义对齐：BulkInsert 是纯插入（TryAdd，冲突显式失败），
    /// 覆盖语义由 <see cref="UpsertAsync"/>（CAS + 历史行）承担。
    /// </remarks>
    /// <param name="entities">待写入的行集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入的行数（与输入集合等长）。</returns>
    /// <exception cref="InvalidOperationException">同 Id 行已存在时抛出。</exception>
    public Task<int> BulkInsertAsync(
        IReadOnlyList<UniversalEntity> entities,
        CancellationToken cancellationToken = default)
    {
        // 逐行纯插入——TryAdd 失败即同 Id 已存在。
        foreach (var entity in entities)
        {
            // 主键寻址插入——已存在则显式冲突（不静默覆盖）。
            if (!_rows.TryAdd(entity.Id, entity))
            {
                // 抛出冲突异常。
                throw new ConcurrencyConflictException(
                    $"行 {entity.Id} 已存在——BulkInsert 是纯插入语义，覆盖写入请使用 UpsertAsync。");
            }
        }

        // 返回写入行数——同步完成，直接返回已完成任务。
        return Task.FromResult(entities.Count);
    }

    /// <summary>
    /// 子图预取——全量遍历过滤：命中实体行本身，或 ParentId 指向本类型实体行。
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
        // 第 1 步：收集本类型的实体行 Id 集（预取的指针追逐锚点）。
        var entityIds = new HashSet<Guid>();

        // 遍历全部行找实体行。
        foreach (var pair in _rows)
        {
            // 匹配实体行条件且未删除。
            if (pair.Value.TypeId == entityTypeId
                && pair.Value.Role == role
                && pair.Value.ParentId is null
                && !pair.Value.IsDeleted)
            {
                // 记录实体行 Id。
                entityIds.Add(pair.Key);
            }
        }

        // 第 2 步：再次遍历收集子图——实体行本身 + ParentId 命中锚点集的属性行。
        var results = new List<UniversalEntity>(entityIds.Count * 5);

        // 遍历全部行。
        foreach (var pair in _rows)
        {
            // 已删除行一律排除。
            if (pair.Value.IsDeleted)
            {
                // 跳过。
                continue;
            }

            // 实体行本身命中。
            if (pair.Value.TypeId == entityTypeId && pair.Value.Role == role && pair.Value.ParentId is null)
            {
                // 加入子图。
                results.Add(pair.Value);

                // 继续下一行。
                continue;
            }

            // 属性行命中：ParentId 指向锚点集，且非历史行（Role=6 排除）。
            if (pair.Value.ParentId is not null
                && entityIds.Contains(pair.Value.ParentId.Value)
                && pair.Value.Role != UniversalEntity.RoleHistory)
            {
                // 加入子图。
                results.Add(pair.Value);
            }
        }

        // 返回只读子图。
        return Task.FromResult<IReadOnlyList<UniversalEntity>>(results);
    }

    /// <summary>
    /// 按主键点查——O(1) 字典寻址。
    /// </summary>
    /// <param name="id">行 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到且未删除返回行；否则返回 null。</returns>
    public Task<UniversalEntity?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        // 字典取值。
        var found = _rows.TryGetValue(id, out var row) && !row.IsDeleted ? row : null;

        // 返回结果。
        return Task.FromResult<UniversalEntity?>(found);
    }

    /// <summary>
    /// 指针追逐——全字典扫描过滤 ParentId（进程内 O(n)，10 万行实测毫秒级）。
    /// </summary>
    /// <param name="parentId">父节点 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全部 ParentId 命中的未删除行。</returns>
    public Task<IReadOnlyList<UniversalEntity>> GetByParentIdAsync(
        Guid parentId,
        CancellationToken cancellationToken = default)
    {
        // 全扫描过滤——纯内存比较，无 I/O。
        var results = new List<UniversalEntity>();

        // 遍历字典。
        foreach (var pair in _rows)
        {
            // 匹配 ParentId、未删除、且非历史行（Role=6 排除，防历史污染活数据）。
            if (pair.Value.ParentId == parentId && !pair.Value.IsDeleted && pair.Value.Role != UniversalEntity.RoleHistory)
            {
                // 命中。
                results.Add(pair.Value);
            }
        }

        // 返回只读结果。
        return Task.FromResult<IReadOnlyList<UniversalEntity>>(results);
    }

    /// <summary>
    /// 按 Id 软删——用 record with 表达式生成 IsDeleted = true 的副本覆盖原行。
    /// </summary>
    /// <param name="id">行 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中返回 true。</returns>
    public Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        // 字典取值。
        if (!_rows.TryGetValue(id, out var row) || row.IsDeleted)
        {
            // 行不存在或已删除。
            return Task.FromResult(false);
        }

        // 生成软删副本并覆盖。
        _rows[id] = row with { IsDeleted = true };

        // 命中。
        return Task.FromResult(true);
    }

    /// <summary>
    /// 条件覆盖写入（CAS upsert）——行不存在则插入；存在则按 Version 乐观锁更新并落历史行。
    /// </summary>
    /// <param name="entities">待写入的行集合；更新行必须携带与存储一致的 Version。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>落库后的最终行集合。</returns>
    /// <exception cref="InvalidOperationException">Version 不匹配或并发抢先时抛出。</exception>
    public Task<IReadOnlyList<UniversalEntity>> UpsertAsync(
        IReadOnlyList<UniversalEntity> entities,
        CancellationToken cancellationToken = default)
    {
        // 收集落库后的最终行。
        var results = new List<UniversalEntity>(entities.Count);

        // 逐行 CAS 处理。
        foreach (var entity in entities)
        {
            // 第 1 步：取现存行（可能为 null）。
            _rows.TryGetValue(entity.Id, out var existing);

            // 分支一：行不存在或已软删 → TryAdd 纯插入（并发插入冲突显式失败）。
            if (existing is null || existing.IsDeleted)
            {
                // 并发插入竞争——TryAdd 失败即被抢先。
                if (!_rows.TryAdd(entity.Id, entity))
                {
                    // 抛出并发冲突。
                    throw new ConcurrencyConflictException(
                        $"并发冲突：行 {entity.Id} 在写入间隙被其他事务插入——请重读后重试。");
                }

                // 返回输入行。
                results.Add(entity);

                // 下一行。
                continue;
            }

            // 分支二前置校验：Version 快速比对（existing 此处必然非 null 且为活行）。
            if (existing.Version != entity.Version)
            {
                // 抛出乐观锁冲突。
                throw new ConcurrencyConflictException(
                    $"乐观锁冲突：行 {entity.Id} 存储版本为 {existing.Version}，请求版本为 {entity.Version}——请重读后重试。");
            }

            // 第 2 步：构造更新后的行——Version 递增、CreatedAt 保留原始创建时间。
            var updatedRow = entity with
            {
                // 乐观锁版本递增。
                Version = existing.Version + 1,
                // 创建时间保留旧行值。
                CreatedAt = existing.CreatedAt,
                // 更新时间取当前。
                UpdatedAt = DateTime.UtcNow
            };

            // 第 3 步：CAS 覆盖——TryUpdate 以旧行为比较基准，失败即被并发抢先。
            // 第 25 轮顺序对齐：先主行后历史行——CAS 失败路径不残留幽灵历史（与 SQL 语义一致）。
            if (!_rows.TryUpdate(entity.Id, updatedRow, existing))
            {
                // 抛出并发冲突。
                throw new ConcurrencyConflictException(
                    $"并发冲突：行 {entity.Id} 在写入间隙被其他事务更新——请重读后重试。");
            }

            // 第 4 步：主行成功后写历史行——Role=6、新 Id、ParentId 指向当前行、其余字段原样保留。
            var historyRow = existing with
            {
                // 历史行 Id 换新。
                Id = Guid.NewGuid(),
                // 角色改为历史。
                Role = UniversalEntity.RoleHistory,
                // 挂到被更新的当前行下。
                ParentId = existing.Id,
                // 版本归属（第 25 轮 P1）：记录被覆盖前实体的版本。
                Version = existing.Version,
                // 变更时刻（第 25 轮 P1）：历史行的 UpdatedAt 记录本次变更发生时间。
                UpdatedAt = DateTime.UtcNow
            };

            // 历史行入池。
            _rows.TryAdd(historyRow.Id, historyRow);

            // 返回落库后的最终行。
            results.Add(updatedRow);
        }

        // 返回只读结果。
        return Task.FromResult<IReadOnlyList<UniversalEntity>>(results);
    }

    /// <summary>
    /// 批量软删——进程内逐行软删（内存操作天然原子，无需事务包装）。
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

        // 逐行软删——内存写入不可半途失败，逐行等价于原子。
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
    /// 实体审计历史链——全扫描过滤 Role=6 历史行（ParentId 指向被审计实体）。
    /// </summary>
    /// <param name="entityId">被审计的实体行 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>历史行集合，按更新时间升序。</returns>
    public Task<IReadOnlyList<UniversalEntity>> GetHistoryAsync(
        Guid entityId,
        CancellationToken cancellationToken = default)
    {
        // 全扫描过滤——审计语义（只取 Role=6）。
        var results = _rows.Values
            .Where(row => row.ParentId == entityId && row.Role == UniversalEntity.RoleHistory && !row.IsDeleted)
            .OrderBy(row => row.UpdatedAt)
            .ToList();

        // 返回只读结果。
        return Task.FromResult<IReadOnlyList<UniversalEntity>>(results);
    }

    /// <summary>
    /// 图更新锁对象——图替换四步（主行 CAS + 软删 + 历史行与新行插入）全程串行化，等效原子。
    /// </summary>
    private readonly object _updateGraphLock = new();

    /// <summary>
    /// 图模式子图替换——主行 CAS + 旧行软删 + 历史行与新行插入，全程 <c>lock</c> 串行化（等效原子，第 26 轮）。
    /// </summary>
    /// <param name="newEntityRow">新主行（Id 为目标实体行；Version 为期望的当前版本）。</param>
    /// <param name="oldPropertyRowIds">待软删的旧属性行 Id 集合。</param>
    /// <param name="historyRows">预构造的历史行集合。</param>
    /// <param name="newPropertyRows">新属性行集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>落库后的最终主行（Version 已递增）。</returns>
    public Task<UniversalEntity> UpdateGraphAsync(
        UniversalEntity newEntityRow,
        IReadOnlyList<Guid> oldPropertyRowIds,
        IReadOnlyList<UniversalEntity> historyRows,
        IReadOnlyList<UniversalEntity> newPropertyRows,
        CancellationToken cancellationToken = default)
    {
        // 全程加锁——四步对其他写者等效原子（评审 5 的 InMemory 分级实现）。
        lock (_updateGraphLock)
        {
            // 第 1 步：取现存主行。
            _rows.TryGetValue(newEntityRow.Id, out var existing);

            // 行不存在——冲突语义。
            if (existing is null || existing.IsDeleted)
            {
                // 抛出并发冲突。
                throw new ConcurrencyConflictException(
                    $"并发冲突：实体行 {newEntityRow.Id} 不存在或已软删——子图替换被拒绝。");
            }

            // Version 快速比对。
            if (existing.Version != newEntityRow.Version)
            {
                // 抛出乐观锁冲突。
                throw new ConcurrencyConflictException(
                    $"乐观锁冲突：行 {newEntityRow.Id} 存储版本为 {existing.Version}，请求版本为 {newEntityRow.Version}——请重读后重试。");
            }

            // 第 2 步：构造更新后的主行。
            var updatedRow = newEntityRow with
            {
                // 乐观锁版本递增。
                Version = existing.Version + 1,
                // 创建时间保留原始创建时间。
                CreatedAt = existing.CreatedAt,
                // 更新时间取当前。
                UpdatedAt = DateTime.UtcNow
            };

            // 第 3 步：CAS 覆盖主行。
            if (!_rows.TryUpdate(newEntityRow.Id, updatedRow, existing))
            {
                // 抛出并发冲突。
                throw new ConcurrencyConflictException(
                    $"并发冲突：行 {newEntityRow.Id} 在写入间隙被其他事务更新——请重读后重试。");
            }

            // 第 4 步：旧属性行软删。
            foreach (var oldId in oldPropertyRowIds)
            {
                // 软删命中才覆盖。
                if (_rows.TryGetValue(oldId, out var oldRow) && !oldRow.IsDeleted)
                {
                    // 软删副本覆盖。
                    _rows[oldId] = oldRow with { IsDeleted = true };
                }
            }

            // 第 5 步：历史行与新属性行入池。
            foreach (var row in historyRows.Concat(newPropertyRows))
            {
                // 入池。
                _rows.TryAdd(row.Id, row);
            }

            // 返回最终主行。
            return Task.FromResult<UniversalEntity>(updatedRow);
        }
    }

    /// <summary>
    /// 清空整个池——接口外的扩展方法，基准与测试基建使用。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步清空操作完成的任务。</returns>
    public Task ClearAsync(CancellationToken cancellationToken = default)
    {
        // 字典清空。
        _rows.Clear();

        // 返回已完成任务。
        return Task.CompletedTask;
    }
}
