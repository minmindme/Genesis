// ANCHOR: dal.repository.universal-entity - UniversalEntity 单表增删改查
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace Emoking.DAL;

/// <summary>
/// 同构单表的基础数据访问层。
/// 只做最原始的参数化读写——不引入 Dapper/EF Core，保持"纯寻址"哲学。
/// </summary>
/// <remarks>
/// 原型期方法全部 async + CancellationToken；真实部署时可按需加同步版本。
/// 所有查询一律带 IsDeleted = 0 过滤，软删除是本系统默认策略。
/// 批量路径：写入用 <see cref="SqlBulkCopy"/> 单次往返，读取用子图预取 SQL 单次往返，
/// 将"逐行 I/O 往返"同化为"图拓扑整体搬运"。
/// </remarks>
public sealed class UniversalEntityRepository
{
    // DbContext 实例，用于创建连接。
    private readonly EmokingDbContext _dbContext;

    /// <summary>
    /// 初始化仓储，注入 <see cref="EmokingDbContext"/>。
    /// </summary>
    /// <param name="dbContext">连接工厂，原型期由调用方传入。</param>
    public UniversalEntityRepository(EmokingDbContext dbContext)
    {
        // 保存连接工厂到私有字段。
        _dbContext = dbContext;
    }

    // ============================================================================
    // 写入
    // ============================================================================

    /// <summary>
    /// 插入一行 UniversalEntity。
    /// </summary>
    /// <param name="entity">待插入的行，Id 必须由调用方预先生成。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>插入完成返回 <see langword="true"/>。</returns>
    public async Task<bool> InsertAsync(
        UniversalEntity entity,
        CancellationToken cancellationToken = default)
    {
        // 拿到已打开的连接，用 using 保证释放。
        using var connection = _dbContext.CreateConnection();

        // 构造参数化 INSERT SQL，全部列显式声明防止表结构变更时静默出错。
        const string sql = @"
            INSERT INTO [dbo].[UniversalEntity]
                ([Id], [TypeId], [Role], [ParentId], [Value], [Version], [IsDeleted], [CreatedAt], [UpdatedAt])
            VALUES
                (@Id, @TypeId, @Role, @ParentId, @Value, @Version, @IsDeleted, SYSUTCDATETIME(), SYSUTCDATETIME())";

        // 构造 SqlCommand，绑定连接和 SQL 文本。
        await using var command = connection.CreateCommand();

        // 写入 SQL 文本。
        command.CommandText = sql;

        // 添加参数——顺序与 SQL @param 保持一致。
        command.Parameters.AddWithValue("@Id", entity.Id);
        command.Parameters.AddWithValue("@TypeId", entity.TypeId);
        command.Parameters.AddWithValue("@Role", entity.Role);
        command.Parameters.AddWithValue("@ParentId", (object?)entity.ParentId ?? DBNull.Value);
        command.Parameters.AddWithValue("@Value", (object?)entity.Value ?? DBNull.Value);
        command.Parameters.AddWithValue("@Version", entity.Version);
        command.Parameters.AddWithValue("@IsDeleted", entity.IsDeleted);

        // 执行非查询，拿到影响行数。
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);

        // 影响行数为 1 表示插入成功。
        return affected == 1;
    }

    /// <summary>
    /// 批量插入多行——拆解后一次性写入，避免 N+1 往返。
    /// </summary>
    /// <param name="entities">待插入的行集合，至少 1 行。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际插入的行数总和。</returns>
    public async Task<int> InsertBatchAsync(
        IEnumerable<UniversalEntity> entities,
        CancellationToken cancellationToken = default)
    {
        // 拿到已打开的连接。
        using var connection = _dbContext.CreateConnection();

        // 开一个显式事务——批量写入要么全成要么全败。
        using var transaction = connection.BeginTransaction();

        // 累计插入行数。
        var totalAffected = 0;

        try
        {
            // 构造参数化 INSERT SQL。
            const string sql = @"
                INSERT INTO [dbo].[UniversalEntity]
                    ([Id], [TypeId], [Role], [ParentId], [Value], [Version], [IsDeleted], [CreatedAt], [UpdatedAt])
                VALUES
                    (@Id, @TypeId, @Role, @ParentId, @Value, @Version, @IsDeleted, SYSUTCDATETIME(), SYSUTCDATETIME())";

            // 对每一行构造独立 Command 执行。
            foreach (var entity in entities)
            {
                // 构造 SqlCommand，绑定连接和事务。
                await using var command = connection.CreateCommand();

                // 设置事务——必须在执行前赋值。
                command.Transaction = transaction;

                // 写入 SQL 文本。
                command.CommandText = sql;

                // 添加参数。
                command.Parameters.AddWithValue("@Id", entity.Id);
                command.Parameters.AddWithValue("@TypeId", entity.TypeId);
                command.Parameters.AddWithValue("@Role", entity.Role);
                command.Parameters.AddWithValue("@ParentId", (object?)entity.ParentId ?? DBNull.Value);
                command.Parameters.AddWithValue("@Value", (object?)entity.Value ?? DBNull.Value);
                command.Parameters.AddWithValue("@Version", entity.Version);
                command.Parameters.AddWithValue("@IsDeleted", entity.IsDeleted);

                // 执行，累加到 totalAffected。
                totalAffected += await command.ExecuteNonQueryAsync(cancellationToken);
            }

            // 全部成功则提交事务。
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // 任何一行失败则回滚——using 块结束前显式 Rollback，防止连接池拿到未完成事务的连接。
            await transaction.RollbackAsync(cancellationToken);

            // 向上抛异常，调用方决定重试或放弃。
            throw;
        }

        // 返回实际插入的总行数。
        return totalAffected;
    }

    /// <summary>
    /// 批量装载（Bulk Load）——用 <see cref="SqlBulkCopy"/> 单次网络往返写入任意行数。
    /// </summary>
    /// <remarks>
    /// 这是"写入动作同化"的核心：N 行不再产生 N 次往返，
    /// 而是先把全部行在内存中拍扁为同构 DataTable（同构数据包），再一口气灌入数据库。
    /// CreatedAt/UpdatedAt 未映射，由表上的 DEFAULT 约束（SYSUTCDATETIME）填充。
    /// 第 20 轮语义回归**纯插入**：同 Id 行已存在时主键冲突显式失败——
    /// 覆盖语义由 <see cref="UpsertAsync"/>（CAS + 历史行）承担，BulkInsert 不再先删后插。
    /// </remarks>
    /// <param name="entities">待写入的行集合；空集合直接返回 0。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际写入的行数。</returns>
    public async Task<int> BulkInsertAsync(
        IReadOnlyList<UniversalEntity> entities,
        CancellationToken cancellationToken = default)
    {
        // 空集合短路——避免无意义的连接创建。
        if (entities.Count == 0)
        {
            // 返回 0 行。
            return 0;
        }

        // 第 1 步：把全部行在内存中压缩为同构数据包（DataTable）。
        var table = BuildBulkTable(entities);

        // 拿到已打开的连接。
        using var connection = _dbContext.CreateConnection();

        // 第 2 步：构造 SqlBulkCopy——单次往返整包灌入。
        // SqlBulkCopy 仅实现 IDisposable（无 DisposeAsync），故用同步 using。
        // TableLock：批量装载期间取表锁，避免逐行/逐页锁升级开销。
        using var bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.TableLock, null)
        {
            // 目标表名。
            DestinationTableName = "[dbo].[UniversalEntity]",
            // 流式写入——数据从 DataTable 边读边发，不整体驻留缓冲。
            EnableStreaming = true
        };

        // 按列名显式映射——7 列进包，CreatedAt/UpdatedAt 走数据库 DEFAULT。
        bulkCopy.ColumnMappings.Add("Id", "Id");
        bulkCopy.ColumnMappings.Add("TypeId", "TypeId");
        bulkCopy.ColumnMappings.Add("Role", "Role");
        bulkCopy.ColumnMappings.Add("ParentId", "ParentId");
        bulkCopy.ColumnMappings.Add("Value", "Value");
        bulkCopy.ColumnMappings.Add("Version", "Version");
        bulkCopy.ColumnMappings.Add("IsDeleted", "IsDeleted");

        // 第 3 步：执行整包写入——同 Id 已存在时主键冲突异常冒出（纯插入语义，显式失败）。
        await bulkCopy.WriteToServerAsync(table, cancellationToken);

        // 返回写入行数——DataTable 行数即写入行数。
        return table.Rows.Count;
    }

    /// <summary>
    /// 把行集合压缩为与目标表列结构对齐的 <see cref="DataTable"/>。
    /// </summary>
    /// <param name="entities">待写入的行集合，非空。</param>
    /// <returns>可直接交给 <see cref="SqlBulkCopy"/> 的数据表。</returns>
    private static DataTable BuildBulkTable(IReadOnlyList<UniversalEntity> entities)
    {
        // 构造空表——只声明 BulkCopy 要映射的 7 列，类型与表定义严格对齐。
        var table = new DataTable();

        // Id 列——Guid 主键。
        table.Columns.Add("Id", typeof(Guid));

        // TypeId 列——类型指针。
        table.Columns.Add("TypeId", typeof(Guid));

        // Role 列——角色标识。
        table.Columns.Add("Role", typeof(byte));

        // ParentId 列——可空图指针。
        table.Columns.Add("ParentId", typeof(Guid));

        // Value 列——可空纯值。
        table.Columns.Add("Value", typeof(string));

        // Version 列——乐观锁版本。
        table.Columns.Add("Version", typeof(int));

        // IsDeleted 列——软删标记。
        table.Columns.Add("IsDeleted", typeof(bool));

        // 逐行填充——按 UniversalEntity 的属性名访问，顺序与列声明一致。
        foreach (var entity in entities)
        {
            // 新行结构完全跟随表定义。
            var row = table.NewRow();

            // 主键。
            row["Id"] = entity.Id;

            // 类型指针。
            row["TypeId"] = entity.TypeId;

            // 角色。
            row["Role"] = entity.Role;

            // 图指针——null 转 DBNull。
            row["ParentId"] = (object?)entity.ParentId ?? DBNull.Value;

            // 纯值——null 转 DBNull。
            row["Value"] = (object?)entity.Value ?? DBNull.Value;

            // 版本号。
            row["Version"] = entity.Version;

            // 软删标记。
            row["IsDeleted"] = entity.IsDeleted;

            // 加入表。
            table.Rows.Add(row);
        }

        // 返回压缩完成的同构数据包。
        return table;
    }

    /// <summary>
    /// 条件覆盖写入（CAS upsert）——单事务内逐行"读现状 → 插入或乐观锁更新 + 历史行"。
    /// </summary>
    /// <remarks>
    /// 第 20 轮洞 2 重做的核心实现：
    ///   1. 行不存在 → 纯插入（Version 保持输入值）；
    ///   2. 行存在且 Version 匹配 → 旧行先复制为历史行（Role=6，Id 换新、ParentId 指向当前行、
    ///      CreatedAt/UpdatedAt/Version 原样保留），再 UPDATE 覆盖（Version = 旧值 + 1，CreatedAt 不动）；
    ///   3. Version 不匹配或 UPDATE 命中 0 行（并发抢先）→ 抛出冲突异常——一胜一败，静默覆盖不存在。
    /// 全程单事务：多行输入要么全成要么全败。
    /// </remarks>
    /// <param name="entities">待写入的行集合；更新行必须携带与存储一致的 Version。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>落库后的最终行集合（Id 不变；更新行 Version 已递增、CreatedAt 为原始创建时间）。</returns>
    /// <exception cref="InvalidOperationException">Version 不匹配或并发抢先时抛出。</exception>
    public async Task<IReadOnlyList<UniversalEntity>> UpsertAsync(
        IReadOnlyList<UniversalEntity> entities,
        CancellationToken cancellationToken = default)
    {
        // 空集合短路。
        if (entities.Count == 0)
        {
            // 返回空结果。
            return [];
        }

        // 拿到已打开的连接与显式事务——多行 upsert 原子生效。
        using var connection = _dbContext.CreateConnection();
        using var transaction = connection.BeginTransaction();

        // 收集落库后的最终行。
        var results = new List<UniversalEntity>(entities.Count);

        try
        {
            // 逐行处理——更新场景行数少，逐行 CAS 优于复杂 staging（原型期取舍）。
            foreach (var entity in entities)
            {
                // 第 1 步：读存储中的现存行（含 IsDeleted = 0 过滤）。
                var existing = await GetRowInTransactionAsync(connection, transaction, entity.Id, cancellationToken);

                // 分支一：行不存在 → 纯插入。
                if (existing is null)
                {
                    // 插入输入行原样。
                    await InsertNewRowInTransactionAsync(connection, transaction, entity, cancellationToken);

                    // 返回输入行（插入行 Version 保持输入值）。
                    results.Add(entity);

                    // 下一行。
                    continue;
                }

                // 分支二前置校验：Version 快速比对——不匹配直接显式冲突（友好错误信息）。
                if (existing.Version != entity.Version)
                {
                    // 抛出乐观锁冲突。
                    throw new InvalidOperationException(
                        $"乐观锁冲突：行 {entity.Id} 存储版本为 {existing.Version}，请求版本为 {entity.Version}——请重读后重试。");
                }

                // 第 2 步：CAS UPDATE——WHERE 带 Version 兜底并发窗口，命中 0 行即被抢先。
                var affected = await UpdateCasInTransactionAsync(connection, transaction, entity, entity.Version, cancellationToken);

                // 0 行 = SELECT 之后、UPDATE 之前被其他事务抢先更新。
                if (affected == 0)
                {
                    // 抛出并发冲突。
                    throw new InvalidOperationException(
                        $"并发冲突：行 {entity.Id} 在写入间隙被其他事务更新——请重读后重试。");
                }

                // 第 3 步：旧行落成历史行——Role=6、新 Id、ParentId 指向当前行、时间戳与版本原样保留。
                var historyRow = existing with
                {
                    // 历史行 Id 换新——主键不冲突。
                    Id = Guid.NewGuid(),
                    // 角色改为历史。
                    Role = UniversalEntity.RoleHistory,
                    // 挂到被更新的当前行下（审计链入口）。
                    ParentId = existing.Id
                };

                // 历史行全列显式插入（保留原始 CreatedAt/UpdatedAt）。
                await InsertHistoryRowInTransactionAsync(connection, transaction, historyRow, cancellationToken);

                // 第 4 步：构造落库后的最终行——Version 递增、CreatedAt 保留原始创建时间。
                results.Add(entity with
                {
                    // 乐观锁版本递增。
                    Version = existing.Version + 1,
                    // 创建时间保留旧行值（历史不因覆盖而重写）。
                    CreatedAt = existing.CreatedAt,
                    // 更新时间取当前（与 DB SYSUTCDATETIME 毫秒级近似）。
                    UpdatedAt = DateTime.UtcNow
                });
            }

            // 全部成功则提交事务。
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            // 任何一行失败则整批回滚。
            await transaction.RollbackAsync(cancellationToken);

            // 向上抛异常。
            throw;
        }

        // 返回落库后的最终行集合。
        return results;
    }

    /// <summary>
    /// 事务内按 Id 读一行（含 IsDeleted = 0 过滤）。
    /// </summary>
    /// <param name="connection">事务所属连接。</param>
    /// <param name="transaction">当前事务。</param>
    /// <param name="id">行 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到返回行；不存在或已软删返回 null。</returns>
    private static async Task<UniversalEntity?> GetRowInTransactionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        Guid id,
        CancellationToken cancellationToken)
    {
        // 构造命令。
        await using var command = connection.CreateCommand();

        // 绑定事务。
        command.Transaction = transaction;

        // 参数化点查。
        command.CommandText = @"
            SELECT [Id], [TypeId], [Role], [ParentId], [Value], [Version], [IsDeleted], [CreatedAt], [UpdatedAt]
            FROM [dbo].[UniversalEntity]
            WHERE [Id] = @Id AND [IsDeleted] = 0";

        // 绑定 Id。
        command.Parameters.AddWithValue("@Id", id);

        // 执行 Reader。
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        // 读到即映射。
        if (await reader.ReadAsync(cancellationToken))
        {
            // 映射返回。
            return MapReader(reader);
        }

        // 未找到。
        return null;
    }

    /// <summary>
    /// 事务内插入一行——CreatedAt/UpdatedAt 走数据库 DEFAULT（新行语义）。
    /// </summary>
    /// <param name="connection">事务所属连接。</param>
    /// <param name="transaction">当前事务。</param>
    /// <param name="entity">待插入的行。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步插入操作完成的任务。</returns>
    private static async Task InsertNewRowInTransactionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        UniversalEntity entity,
        CancellationToken cancellationToken)
    {
        // 构造命令。
        await using var command = connection.CreateCommand();

        // 绑定事务。
        command.Transaction = transaction;

        // 参数化 INSERT——时间戳列由 DEFAULT 约束填充。
        command.CommandText = @"
            INSERT INTO [dbo].[UniversalEntity]
                ([Id], [TypeId], [Role], [ParentId], [Value], [Version], [IsDeleted], [CreatedAt], [UpdatedAt])
            VALUES
                (@Id, @TypeId, @Role, @ParentId, @Value, @Version, @IsDeleted, SYSUTCDATETIME(), SYSUTCDATETIME())";

        // 绑定全部参数。
        AddEntityParameters(command, entity);

        // 执行插入。
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 事务内插入历史行——CreatedAt/UpdatedAt 全列显式（保留旧行的时间线）。
    /// </summary>
    /// <param name="connection">事务所属连接。</param>
    /// <param name="transaction">当前事务。</param>
    /// <param name="historyRow">已构造好的历史行（Role=6、ParentId 指向当前行）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步插入操作完成的任务。</returns>
    private static async Task InsertHistoryRowInTransactionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        UniversalEntity historyRow,
        CancellationToken cancellationToken)
    {
        // 构造命令。
        await using var command = connection.CreateCommand();

        // 绑定事务。
        command.Transaction = transaction;

        // 参数化 INSERT——时间戳全列显式，历史行的审计时间线原样保留。
        command.CommandText = @"
            INSERT INTO [dbo].[UniversalEntity]
                ([Id], [TypeId], [Role], [ParentId], [Value], [Version], [IsDeleted], [CreatedAt], [UpdatedAt])
            VALUES
                (@Id, @TypeId, @Role, @ParentId, @Value, @Version, @IsDeleted, @CreatedAt, @UpdatedAt)";

        // 绑定业务列参数。
        AddEntityParameters(command, historyRow);

        // 绑定时间戳参数——显式 DateTime2（第 22 轮 B38：AddWithValue 推断为 datetime（3.33ms 精度），
        // 历史行 CreatedAt 舍入后与原行差异可达毫秒级，破坏审计时间线无损承诺）。
        var createdAtParameter = command.Parameters.Add("@CreatedAt", System.Data.SqlDbType.DateTime2);

        // 原始创建时间全精度。
        createdAtParameter.Value = historyRow.CreatedAt;

        // 绑定更新时间。
        var updatedAtParameter = command.Parameters.Add("@UpdatedAt", System.Data.SqlDbType.DateTime2);

        // 原始更新时间全精度。
        updatedAtParameter.Value = historyRow.UpdatedAt;

        // 执行插入。
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 事务内 CAS UPDATE——WHERE 带 Version 谓词，乐观锁兜底并发窗口。
    /// </summary>
    /// <param name="connection">事务所属连接。</param>
    /// <param name="transaction">当前事务。</param>
    /// <param name="entity">覆盖内容（TypeId/Role/ParentId/Value 按输入更新）。</param>
    /// <param name="expectedVersion">期望的当前版本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际更新的行数（0 = 并发抢先）。</returns>
    private static async Task<int> UpdateCasInTransactionAsync(
        SqlConnection connection,
        SqlTransaction transaction,
        UniversalEntity entity,
        int expectedVersion,
        CancellationToken cancellationToken)
    {
        // 构造命令。
        await using var command = connection.CreateCommand();

        // 绑定事务。
        command.Transaction = transaction;

        // 参数化 CAS UPDATE——CreatedAt 不在 SET 列表（历史不因覆盖重写）；
        // Version 由数据库侧 +1，IsDeleted 固定为活行。
        command.CommandText = @"
            UPDATE [dbo].[UniversalEntity]
            SET [TypeId] = @TypeId, [Role] = @Role, [ParentId] = @ParentId,
                [Value] = @Value, [Version] = [Version] + 1,
                [IsDeleted] = 0, [UpdatedAt] = SYSUTCDATETIME()
            WHERE [Id] = @Id AND [Version] = @ExpectedVersion AND [IsDeleted] = 0";

        // 绑定覆盖内容参数。
        AddEntityParameters(command, entity);

        // 绑定期望版本。
        command.Parameters.AddWithValue("@ExpectedVersion", expectedVersion);

        // 执行更新，返回命中行数。
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 为 INSERT/UPDATE 命令绑定行的业务列参数（不含时间戳列）。
    /// </summary>
    /// <param name="command">目标命令。</param>
    /// <param name="entity">参数来源行。</param>
    private static void AddEntityParameters(SqlCommand command, UniversalEntity entity)
    {
        // 主键。
        command.Parameters.AddWithValue("@Id", entity.Id);

        // 类型指针。
        command.Parameters.AddWithValue("@TypeId", entity.TypeId);

        // 角色。
        command.Parameters.AddWithValue("@Role", entity.Role);

        // 图指针——null 转 DBNull。
        command.Parameters.AddWithValue("@ParentId", (object?)entity.ParentId ?? DBNull.Value);

        // 纯值——null 转 DBNull。
        command.Parameters.AddWithValue("@Value", (object?)entity.Value ?? DBNull.Value);

        // 版本号。
        command.Parameters.AddWithValue("@Version", entity.Version);

        // 软删标记。
        command.Parameters.AddWithValue("@IsDeleted", entity.IsDeleted);
    }

    /// <summary>
    /// 批量软删（单事务）——集合内全部行要么都删要么都不删。
    /// </summary>
    /// <remarks>
    /// 第 20 轮半修：级联删除（实体 + 子行）从"逐行删"升级为"单事务删"，
    /// 消灭"中途失败留下删了一半的状态"。
    /// </remarks>
    /// <param name="ids">待软删的行 Id 集合；空集合直接返回 0。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>实际软删的行数。</returns>
    public async Task<int> SoftDeleteManyAsync(
        IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        // 空集合短路。
        if (ids.Count == 0)
        {
            // 返回 0 行。
            return 0;
        }

        // 拿到已打开的连接与显式事务。
        using var connection = _dbContext.CreateConnection();
        using var transaction = connection.BeginTransaction();

        try
        {
            // 构造命令。
            await using var command = connection.CreateCommand();

            // 绑定事务。
            command.Transaction = transaction;

            // 参数化批量软删——OPENJSON 展开 Id 集合，零 SQL 拼接。
            command.CommandText = @"
                UPDATE [dbo].[UniversalEntity]
                SET [IsDeleted] = 1, [UpdatedAt] = SYSUTCDATETIME()
                WHERE [IsDeleted] = 0
                  AND [Id] IN (SELECT CAST([value] AS UNIQUEIDENTIFIER) FROM OPENJSON(@IdsJson))";

            // 序列化 Id 集合为 JSON 数组。
            var idsJson = System.Text.Json.JsonSerializer.Serialize(ids.ToList());

            // 绑定参数。
            command.Parameters.AddWithValue("@IdsJson", idsJson);

            // 执行批量软删。
            var affected = await command.ExecuteNonQueryAsync(cancellationToken);

            // 提交事务。
            await transaction.CommitAsync(cancellationToken);

            // 返回命中行数。
            return affected;
        }
        catch
        {
            // 失败整批回滚。
            await transaction.RollbackAsync(cancellationToken);

            // 向上抛异常。
            throw;
        }
    }

    /// <summary>
    /// 按 Id 软删除一行——只置 IsDeleted = 1，物理行保留。
    /// </summary>
    /// <param name="id">待软删除的行 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中并更新返回 <see langword="true"/>；行不存在或已删除返回 <see langword="false"/>。</returns>
    public async Task<bool> SoftDeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        // 拿到已打开的连接。
        using var connection = _dbContext.CreateConnection();

        // 构造参数化 UPDATE——同时过滤 IsDeleted = 0，防止重复删除重复计数。
        const string sql = @"
            UPDATE [dbo].[UniversalEntity]
            SET [IsDeleted] = 1, [UpdatedAt] = SYSUTCDATETIME()
            WHERE [Id] = @Id AND [IsDeleted] = 0";

        // 构造 SqlCommand。
        await using var command = connection.CreateCommand();

        // 写入 SQL 文本。
        command.CommandText = sql;

        // 绑定 Id 参数。
        command.Parameters.AddWithValue("@Id", id);

        // 执行更新，命中 1 行即成功。
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);

        // 返回是否命中。
        return affected == 1;
    }

    // ============================================================================
    // 读取
    // ============================================================================

    /// <summary>
    /// 按主键 Id 取一行，只取未软删除的。
    /// </summary>
    /// <param name="id">要查找的行 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到返回 <see cref="UniversalEntity"/>；未找到返回 <see langword="null"/>。</returns>
    public async Task<UniversalEntity?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        // 拿到已打开的连接。
        using var connection = _dbContext.CreateConnection();

        // 构造参数化 SELECT，加 IsDeleted = 0 过滤。
        const string sql = @"
            SELECT [Id], [TypeId], [Role], [ParentId], [Value], [Version], [IsDeleted], [CreatedAt], [UpdatedAt]
            FROM [dbo].[UniversalEntity]
            WHERE [Id] = @Id AND [IsDeleted] = 0";

        // 构造 SqlCommand。
        await using var command = connection.CreateCommand();

        // 写入 SQL。
        command.CommandText = sql;

        // 绑定参数。
        command.Parameters.AddWithValue("@Id", id);

        // 执行 Reader，只取一行。
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        // 读到数据行就映射返回。
        if (await reader.ReadAsync(cancellationToken))
        {
            // 从 Ordinal 取列值，避免字符串列名反射开销。
            return MapReader(reader);
        }

        // 没找到返回 null。
        return null;
    }

    /// <summary>
    /// 按 TypeId + Role 取所有子节点——元数据全量加载时用。
    /// </summary>
    /// <param name="typeId">类型指针。</param>
    /// <param name="role">角色常量（1 Meta / 2 Data / 3 Edge / 4 Engine）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>匹配的 UniversalEntity 行集合。</returns>
    public async Task<IReadOnlyList<UniversalEntity>> GetByTypeIdAndRoleAsync(
        Guid typeId,
        byte role,
        CancellationToken cancellationToken = default)
    {
        // 拿到已打开的连接。
        using var connection = _dbContext.CreateConnection();

        // 构造参数化查询，同时过滤 TypeId + Role + IsDeleted。
        const string sql = @"
            SELECT [Id], [TypeId], [Role], [ParentId], [Value], [Version], [IsDeleted], [CreatedAt], [UpdatedAt]
            FROM [dbo].[UniversalEntity]
            WHERE [TypeId] = @TypeId AND [Role] = @Role AND [IsDeleted] = 0
            ORDER BY [Id]";

        // 构造 SqlCommand。
        await using var command = connection.CreateCommand();

        // 写入 SQL。
        command.CommandText = sql;

        // 绑定两个参数。
        command.Parameters.AddWithValue("@TypeId", typeId);
        command.Parameters.AddWithValue("@Role", role);

        // 执行 Reader。
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // 用 List 收集结果。
        var results = new List<UniversalEntity>();

        // 逐行读取并映射。
        while (await reader.ReadAsync(cancellationToken))
        {
            // 映射单行并加入结果集。
            results.Add(MapReader(reader));
        }

        // 返回只读集合。
        return results;
    }

    /// <summary>
    /// 按 ParentId 取所有子节点——反向挂载时沿 Edge 跳转的核心方法。
    /// </summary>
    /// <remarks>
    /// 第 20 轮起排除 Role=6 历史行——历史行 ParentId 也指向实体行，
    /// 不排除会污染属性行/边查询；历史链走专门的审计查询（按 Role=6）。
    /// </remarks>
    /// <param name="parentId">父节点 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>所有 ParentId = parentId 的未删除行集合（不含历史行）。</returns>
    public async Task<IReadOnlyList<UniversalEntity>> GetByParentIdAsync(
        Guid parentId,
        CancellationToken cancellationToken = default)
    {
        // 拿到已打开的连接。
        using var connection = _dbContext.CreateConnection();

        // 构造参数化查询，过滤 ParentId + IsDeleted + 排除历史行。
        const string sql = @"
            SELECT [Id], [TypeId], [Role], [ParentId], [Value], [Version], [IsDeleted], [CreatedAt], [UpdatedAt]
            FROM [dbo].[UniversalEntity]
            WHERE [ParentId] = @ParentId AND [IsDeleted] = 0 AND [Role] <> @HistoryRole
            ORDER BY [Id]";

        // 构造 SqlCommand。
        await using var command = connection.CreateCommand();

        // 写入 SQL。
        command.CommandText = sql;

        // 绑定 ParentId 参数。
        command.Parameters.AddWithValue("@ParentId", parentId);

        // 绑定历史角色参数。
        command.Parameters.AddWithValue("@HistoryRole", UniversalEntity.RoleHistory);

        // 执行 Reader。
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // 收集结果。
        var results = new List<UniversalEntity>();

        // 逐行读取映射。
        while (await reader.ReadAsync(cancellationToken))
        {
            // 加入结果集。
            results.Add(MapReader(reader));
        }

        // 返回只读集合。
        return results;
    }

    /// <summary>
    /// 按多个 Role 批量取行——引擎自举时加载全部 Role=1（元数据）与 Role=4（引擎指令）行。
    /// </summary>
    /// <remarks>
    /// Role 集合通过 JSON 数组参数化传入（OPENJSON 展开），不拼接 SQL 字符串。
    /// </remarks>
    /// <param name="roles">角色常量集合，如 [1, 4]；空集合返回空结果。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>命中任一角色的未删除行集合，按 Id 排序。</returns>
    public async Task<IReadOnlyList<UniversalEntity>> GetByRolesAsync(
        IReadOnlyCollection<byte> roles,
        CancellationToken cancellationToken = default)
    {
        // 空集合短路——避免无意义的往返。
        if (roles.Count == 0)
        {
            // 返回空集合。
            return [];
        }

        // 拿到已打开的连接。
        using var connection = _dbContext.CreateConnection();

        // 构造多角色查询 SQL——OPENJSON 展开参数化 JSON 数组。
        const string sql = @"
            SELECT [Id], [TypeId], [Role], [ParentId], [Value], [Version], [IsDeleted], [CreatedAt], [UpdatedAt]
            FROM [dbo].[UniversalEntity]
            WHERE [IsDeleted] = 0
              AND [Role] IN (SELECT CAST([value] AS TINYINT) FROM OPENJSON(@RolesJson))
            ORDER BY [Id]";

        // 构造 SqlCommand。
        await using var command = connection.CreateCommand();

        // 写入 SQL。
        command.CommandText = sql;

        // 把角色集合序列化为 JSON 数组（如 "[1,4]"）。
        var rolesJson = JsonSerializer.Serialize(roles);

        // 绑定 JSON 参数。
        command.Parameters.AddWithValue("@RolesJson", rolesJson);

        // 执行 Reader。
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // 收集结果。
        var results = new List<UniversalEntity>();

        // 逐行读取映射。
        while (await reader.ReadAsync(cancellationToken))
        {
            // 加入结果集。
            results.Add(MapReader(reader));
        }

        // 返回只读集合。
        return results;
    }

    /// <summary>
    /// 子图预取——按实体 TypeId 一次往返拉回"实体行 + 全部属性行"的完整图切片。
    /// </summary>
    /// <remarks>
    /// 这是"空间同化"的核心：数据库被当作"图的序列化器"，
    /// 一条 SQL 返回整块子图，引擎在内存里按 ParentId 分组展开，彻底消灭 N+1 往返。
    /// </remarks>
    /// <param name="entityTypeId">实体类型的 Meta TypeId。</param>
    /// <param name="role">实体行角色常量（原型期为 2=Data）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>子图内的全部行（实体行 + 属性行），顺序不保证。</returns>
    public async Task<IReadOnlyList<UniversalEntity>> GetByTypeIdWithChildrenAsync(
        Guid entityTypeId,
        byte role,
        CancellationToken cancellationToken = default)
    {
        // 拿到已打开的连接。
        using var connection = _dbContext.CreateConnection();

        // 构造子图预取 SQL：命中"实体行本身"或"ParentId 指向本类型实体行"的属性行，一次返回。
        // 第 20 轮：子行分支排除 Role=6 历史行（历史行 ParentId 也指向实体行，会混入子图）。
        const string sql = @"
            SELECT [Id], [TypeId], [Role], [ParentId], [Value], [Version], [IsDeleted], [CreatedAt], [UpdatedAt]
            FROM [dbo].[UniversalEntity]
            WHERE [IsDeleted] = 0
              AND ([TypeId] = @TypeId AND [Role] = @Role
                   OR [ParentId] IN (SELECT [Id]
                                     FROM [dbo].[UniversalEntity]
                                     WHERE [TypeId] = @TypeId AND [Role] = @Role AND [IsDeleted] = 0))
              AND [Role] <> @HistoryRole";

        // 构造 SqlCommand。
        await using var command = connection.CreateCommand();

        // 写入 SQL。
        command.CommandText = sql;

        // 绑定 TypeId 参数——两个子条件共用同一参数。
        command.Parameters.AddWithValue("@TypeId", entityTypeId);

        // 绑定 Role 参数。
        command.Parameters.AddWithValue("@Role", role);

        // 绑定历史角色参数。
        command.Parameters.AddWithValue("@HistoryRole", UniversalEntity.RoleHistory);

        // 执行 Reader。
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // 收集子图全部行。
        var results = new List<UniversalEntity>();

        // 逐行读取映射。
        while (await reader.ReadAsync(cancellationToken))
        {
            // 加入结果集。
            results.Add(MapReader(reader));
        }

        // 返回只读子图。
        return results;
    }

    /// <summary>
    /// 按 ParentId 集合批量取子节点——任意一组父节点的属性行单次往返拉回。
    /// </summary>
    /// <remarks>
    /// 父节点 Id 集合通过 JSON 数组参数化传入（OPENJSON 展开），
    /// 不拼接 SQL 字符串，保持防注入底线。
    /// </remarks>
    /// <param name="parentIds">父节点 Id 集合；空集合返回空结果。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>所有 ParentId 命中集合内任一 Id 的未删除行。</returns>
    public async Task<IReadOnlyList<UniversalEntity>> GetByParentIdsAsync(
        IReadOnlyCollection<Guid> parentIds,
        CancellationToken cancellationToken = default)
    {
        // 空集合短路——避免无意义的往返。
        if (parentIds.Count == 0)
        {
            // 返回空集合。
            return [];
        }

        // 拿到已打开的连接。
        using var connection = _dbContext.CreateConnection();

        // 构造批量查询 SQL——OPENJSON 展开参数化 JSON 数组，无字符串拼接。
        // 第 20 轮：排除 Role=6 历史行（与单 ParentId 查询语义对齐）。
        const string sql = @"
            SELECT [Id], [TypeId], [Role], [ParentId], [Value], [Version], [IsDeleted], [CreatedAt], [UpdatedAt]
            FROM [dbo].[UniversalEntity]
            WHERE [IsDeleted] = 0
              AND [Role] <> @HistoryRole
              AND [ParentId] IN (SELECT CAST([value] AS UNIQUEIDENTIFIER) FROM OPENJSON(@ParentIdsJson))
            ORDER BY [Id]";

        // 构造 SqlCommand。
        await using var command = connection.CreateCommand();

        // 写入 SQL。
        command.CommandText = sql;

        // 把 Id 集合序列化为 JSON 数组——Guid 序列化为标准 "D" 格式字符串。
        var parentIdsJson = JsonSerializer.Serialize(parentIds);

        // 绑定 JSON 参数——NVARCHAR 长度由驱动按内容推导。
        command.Parameters.AddWithValue("@ParentIdsJson", parentIdsJson);

        // 绑定历史角色参数。
        command.Parameters.AddWithValue("@HistoryRole", UniversalEntity.RoleHistory);

        // 执行 Reader。
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // 收集结果。
        var results = new List<UniversalEntity>();

        // 逐行读取映射。
        while (await reader.ReadAsync(cancellationToken))
        {
            // 加入结果集。
            results.Add(MapReader(reader));
        }

        // 返回只读集合。
        return results;
    }

    /// <summary>
    /// 将 <see cref="SqlDataReader"/> 的当前行映射为 <see cref="UniversalEntity"/>。
    /// </summary>
    /// <param name="reader">已定位到一行的 Reader。</param>
    /// <returns>映射完成的实体行。</returns>
    private static UniversalEntity MapReader(SqlDataReader reader)
    {
        // 取各列 ordinal 一次，后续按 ordinal 取值避免字符串查找。
        var idOrdinal = reader.GetOrdinal("Id");
        var typeIdOrdinal = reader.GetOrdinal("TypeId");
        var roleOrdinal = reader.GetOrdinal("Role");
        var parentIdOrdinal = reader.GetOrdinal("ParentId");
        var valueOrdinal = reader.GetOrdinal("Value");
        var versionOrdinal = reader.GetOrdinal("Version");
        var isDeletedOrdinal = reader.GetOrdinal("IsDeleted");
        var createdAtOrdinal = reader.GetOrdinal("CreatedAt");
        var updatedAtOrdinal = reader.GetOrdinal("UpdatedAt");

        // 构造 record 并返回——值类型直接取，可空列用 IsDBNull 判空。
        return new UniversalEntity(
            Id: reader.GetGuid(idOrdinal),
            TypeId: reader.GetGuid(typeIdOrdinal),
            Role: reader.GetByte(roleOrdinal),
            ParentId: reader.IsDBNull(parentIdOrdinal) ? null : reader.GetGuid(parentIdOrdinal),
            Value: reader.IsDBNull(valueOrdinal) ? null : reader.GetString(valueOrdinal),
            Version: reader.GetInt32(versionOrdinal),
            IsDeleted: reader.GetBoolean(isDeletedOrdinal),
            CreatedAt: reader.GetDateTime(createdAtOrdinal),
            UpdatedAt: reader.GetDateTime(updatedAtOrdinal));
    }
}
