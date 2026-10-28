// ANCHOR: dal.store.sql-server - SQL Server 磁盘表存储实现
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Emoking.DAL;

/// <summary>
/// SQL Server 磁盘表存储实现——包装 <see cref="UniversalEntityRepository"/>，
/// 是三个 Provider 中唯一带完整 ACID 保证的实现。
/// </summary>
public sealed class SqlServerUniversalStore : IUniversalStore
{
    // 磁盘表仓储——具体 SQL 执行者。
    private readonly UniversalEntityRepository _repository;

    /// <summary>
    /// 注入磁盘表仓储初始化。
    /// </summary>
    /// <param name="repository">单表仓储实例。</param>
    public SqlServerUniversalStore(UniversalEntityRepository repository)
    {
        // 保存仓储引用。
        _repository = repository;
    }

    /// <summary>
    /// 整包写入——委托给仓储的 SqlBulkCopy 单次往返实现。
    /// </summary>
    /// <param name="entities">待写入的行集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际写入的行数。</returns>
    public Task<int> BulkInsertAsync(
        IReadOnlyList<UniversalEntity> entities,
        CancellationToken cancellationToken = default)
    {
        // 直接委托仓储批量装载。
        return _repository.BulkInsertAsync(entities, cancellationToken);
    }

    /// <summary>
    /// 子图预取——委托给仓储的子图预取 SQL。
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
        // 委托仓储子图预取。
        return _repository.GetByTypeIdWithChildrenAsync(entityTypeId, role, cancellationToken);
    }

    /// <summary>
    /// 按主键点查。
    /// </summary>
    /// <param name="id">行 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到返回行；未找到返回 null。</returns>
    public Task<UniversalEntity?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        // 委托仓储主键查询。
        return _repository.GetByIdAsync(id, cancellationToken);
    }

    /// <summary>
    /// 指针追逐——委托给仓储的 ParentId 查询（磁盘表走 ParentId 非聚集索引）。
    /// </summary>
    /// <param name="parentId">父节点 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全部 ParentId 命中的未删除行。</returns>
    public Task<IReadOnlyList<UniversalEntity>> GetByParentIdAsync(
        Guid parentId,
        CancellationToken cancellationToken = default)
    {
        // 委托仓储指针追逐。
        return _repository.GetByParentIdAsync(parentId, cancellationToken);
    }

    /// <summary>
    /// 按 Id 软删一行——委托给仓储的 SoftDelete。
    /// </summary>
    /// <param name="id">行 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中返回 true。</returns>
    public Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        // 委托仓储软删除。
        return _repository.SoftDeleteAsync(id, cancellationToken);
    }

    /// <summary>
    /// 条件覆盖写入（CAS upsert）——委托仓储的乐观锁实现。
    /// </summary>
    /// <param name="entities">待写入的行集合；更新行必须携带与存储一致的 Version。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>落库后的最终行集合。</returns>
    public Task<IReadOnlyList<UniversalEntity>> UpsertAsync(
        IReadOnlyList<UniversalEntity> entities,
        CancellationToken cancellationToken = default)
    {
        // 委托仓储 CAS upsert。
        return _repository.UpsertAsync(entities, cancellationToken);
    }

    /// <summary>
    /// 批量软删——委托仓储的单事务实现。
    /// </summary>
    /// <param name="ids">待软删的行 Id 集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际软删的行数。</returns>
    public Task<int> DeleteManyAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        // 委托仓储单事务批量软删。
        return _repository.SoftDeleteManyAsync(ids, cancellationToken);
    }

    /// <summary>
    /// 实体审计历史链——委托仓储的 ParentId+Role 查询（Role=6 历史行）。
    /// </summary>
    /// <param name="entityId">被审计的实体行 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>历史行集合，按更新时间升序。</returns>
    public Task<IReadOnlyList<UniversalEntity>> GetHistoryAsync(
        Guid entityId,
        CancellationToken cancellationToken = default)
    {
        // 委托仓储审计查询。
        return _repository.GetByParentIdAndRoleAsync(entityId, UniversalEntity.RoleHistory, cancellationToken);
    }
}
