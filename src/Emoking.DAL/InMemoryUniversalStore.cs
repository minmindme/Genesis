// ANCHOR: dal.store.in-memory - 进程内字典存储实现（零 IPC、零序列化）
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
    /// 整包写入——逐行放入字典，主键即行 Id。
    /// </summary>
    /// <param name="entities">待写入的行集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入的行数（与输入集合等长）。</returns>
    public Task<int> BulkInsertAsync(
        IReadOnlyList<UniversalEntity> entities,
        CancellationToken cancellationToken = default)
    {
        // 逐行放入字典——同 Id 后写覆盖前写。
        foreach (var entity in entities)
        {
            // 主键寻址插入。
            _rows[entity.Id] = entity;
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

            // 属性行命中：ParentId 指向锚点集。
            if (pair.Value.ParentId is not null && entityIds.Contains(pair.Value.ParentId.Value))
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
            // 匹配 ParentId 且未删除。
            if (pair.Value.ParentId == parentId && !pair.Value.IsDeleted)
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
