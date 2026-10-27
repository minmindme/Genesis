// ANCHOR: dal.store.contract - 同构池存储抽象：一种行结构，多种物理引擎
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Emoking.DAL;

/// <summary>
/// 同构池存储抽象——上层引擎只认这套接口，物理引擎（磁盘表/内存表/进程内字典/FASTER）可插拔替换。
/// </summary>
/// <remarks>
/// 接口只描述"同构池的四个基本动作"，不含任何物理引擎语义：
///   1. BulkInsertAsync —— 整包写入（各引擎自行选择单次往返策略）；
///   2. GetSubGraphAsync —— 子图预取（实体行 + 全部属性行一次取回）；
///   3. GetByIdAsync —— 主键点查；
///   4. DeleteAsync —— 按 Id 软删（IsDeleted = 1，物理行保留）。
/// 选择哪个引擎由 Role=4 的 StorageProvider 指令决定（见 <see cref="UniversalStoreFactory"/>）。
/// </remarks>
public interface IUniversalStore
{
    /// <summary>
    /// 整包写入多行同构数据。
    /// </summary>
    /// <param name="entities">待写入的行集合。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际写入的行数。</returns>
    Task<int> BulkInsertAsync(
        IReadOnlyList<UniversalEntity> entities,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 子图预取——按实体类型一次取回"实体行 + 全部属性行"的完整图切片。
    /// </summary>
    /// <param name="entityTypeId">实体类型的 Meta TypeId。</param>
    /// <param name="role">实体行角色常量（原型期为 2=Data）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>子图内全部未删除行，顺序不保证。</returns>
    Task<IReadOnlyList<UniversalEntity>> GetSubGraphAsync(
        Guid entityTypeId,
        byte role,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按主键 Id 点查一行。
    /// </summary>
    /// <param name="id">行 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到返回行；未找到或已软删返回 null。</returns>
    Task<UniversalEntity?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 指针追逐——取全部 ParentId 命中的未删除行。
    /// </summary>
    /// <remarks>
    /// Edge 同构（第 13 轮）后的核心读路径：正向边挂在 From 实体下、反向边挂在 To 实体下，
    /// 双向遍历都退化为"按 ParentId 取子行"这一种普通查询。
    /// </remarks>
    /// <param name="parentId">父节点 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>全部 ParentId 命中的未删除行。</returns>
    Task<IReadOnlyList<UniversalEntity>> GetByParentIdAsync(
        Guid parentId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 按 Id 软删一行（IsDeleted = 1）。
    /// </summary>
    /// <param name="id">行 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中并删除返回 true；行不存在或已删除返回 false。</returns>
    Task<bool> DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 条件覆盖写入（CAS upsert）——行不存在则插入；存在则按 Version 乐观锁更新。
    /// </summary>
    /// <remarks>
    /// 第 20 轮（洞 2 重做）语义契约，三引擎一致：
    ///   1. 输入行的 Id 在池中不存在 → 插入（Version 保持输入值）；
    ///   2. 存在且存储行 Version 等于输入行 Version → 旧行先复制为历史行（Role=6，ParentId 指向当前行，
    ///      CreatedAt/UpdatedAt/Version 原样保留），再覆盖更新（Version = 旧值 + 1，CreatedAt 保留旧行值）；
    ///   3. Version 不匹配（含并发抢先）→ 抛 <see cref="InvalidOperationException"/>——
    ///      并发两写一胜一败，静默覆盖不存在（write 侧乐观锁）。
    /// 与 <see cref="BulkInsertAsync"/> 的分工：BulkInsert 是纯插入（同 Id 显式失败），
    /// Upsert 是"在并发下可证明地覆盖，并且留下可审计的过去"。
    /// </remarks>
    /// <param name="entities">待写入的行集合；更新行必须携带与存储一致的 Version。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>落库后的最终行集合（Id 不变；更新行 Version 已递增、CreatedAt 为原始创建时间）。</returns>
    /// <exception cref="InvalidOperationException">Version 不匹配或并发抢先时抛出。</exception>
    Task<IReadOnlyList<UniversalEntity>> UpsertAsync(
        IReadOnlyList<UniversalEntity> entities,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 批量软删——单事务原子：集合内全部行要么都删要么都不删。
    /// </summary>
    /// <remarks>
    /// 第 20 轮半修：级联删除（实体 + 子行）从"逐行删"升级为"单事务删"，
    /// 消灭"中途失败留下删了一半的状态"。
    /// </remarks>
    /// <param name="ids">待软删的行 Id 集合；空集合直接返回 0。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际软删的行数。</returns>
    Task<int> DeleteManyAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default);
}
