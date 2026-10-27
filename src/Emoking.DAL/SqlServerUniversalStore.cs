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
}
