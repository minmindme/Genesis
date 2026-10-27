// ANCHOR: dal.repository.memory-optimized - 内存优化表仓储（TVP 批量写入 + 子图预取）
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace Emoking.DAL;

/// <summary>
/// 内存优化表（In-Memory OLTP）仓储——与磁盘表仓储同构，但物理引擎为无锁内存结构。
/// </summary>
/// <remarks>
/// 关键事实（SQL Server 2019 实测）：
///   1. 内存优化表不支持 <see cref="SqlBulkCopy"/>，批量写入用 TVP + 存储过程单次往返。
///   2. 内存优化表不支持 TRUNCATE，清表用 DELETE。
///   3. Value 为 NVARCHAR(MAX)，TVP 类型列与目标表列严格对齐。
/// 查询 SQL 与磁盘表仓储同构，仅表名不同——"换引擎不换逻辑"。
/// </remarks>
public sealed class MemoryOptimizedEntityRepository
{
    // DbContext 实例，用于创建连接。
    private readonly EmokingDbContext _dbContext;

    /// <summary>
    /// 注入连接工厂初始化仓储。
    /// </summary>
    /// <param name="dbContext">连接工厂。</param>
    public MemoryOptimizedEntityRepository(EmokingDbContext dbContext)
    {
        // 保存连接工厂到私有字段。
        _dbContext = dbContext;
    }

    // ============================================================================
    // 写入
    // ============================================================================

    /// <summary>
    /// TVP 批量装载——把全部行压成表值参数，经存储过程单次往返写入内存优化表。
    /// </summary>
    /// <param name="entities">待写入的行集合；空集合直接返回 0。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>写入的行数。</returns>
    public async Task<int> BulkInsertAsync(
        IReadOnlyList<UniversalEntity> entities,
        CancellationToken cancellationToken = default)
    {
        // 空集合短路。
        if (entities.Count == 0)
        {
            // 返回 0 行。
            return 0;
        }

        // 第 1 步：把全部行压成 TVP 数据包（9 列全量，TVP 类型不支持 DEFAULT）。
        var table = BuildTvpTable(entities);

        // 拿到已打开的连接。
        using var connection = _dbContext.CreateConnection();

        // 第 2 步：构造存储过程调用——单次往返整包写入。
        await using var command = connection.CreateCommand();

        // 写入存储过程名。
        command.CommandText = "[dbo].[usp_BulkInsertMemoryEntities]";

        // 声明为存储过程调用。
        command.CommandType = CommandType.StoredProcedure;

        // 构造 TVP 参数——Structured 类型绑定表值。
        var tvpParameter = command.Parameters.AddWithValue("@Rows", table);

        // 指定 TVP 类型名。
        tvpParameter.SqlDbType = SqlDbType.Structured;

        // 绑定类型全名。
        tvpParameter.TypeName = "[dbo].[UniversalEntityTableType]";

        // 第 3 步：执行整包写入。
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);

        // 返回写入行数——TVP 行数即写入行数。
        return table.Rows.Count;
    }

    /// <summary>
    /// 清空内存优化表——内存表不支持 TRUNCATE，用 DELETE 全行删除。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步清空操作完成的任务。</returns>
    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        // 拿到已打开的连接。
        using var connection = _dbContext.CreateConnection();

        // 构造 DELETE 命令。
        await using var command = connection.CreateCommand();

        // 内存表无锁全行删除——乐观并发下无阻塞。
        command.CommandText = "DELETE FROM [dbo].[MemoryOptimizedEntity]";

        // 执行清空。
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// 把行集合压成 TVP 数据表——9 列全量填充，列类型与 TYPE 定义严格对齐。
    /// </summary>
    /// <param name="entities">待写入的行集合，非空。</param>
    /// <returns>可绑定 TVP 参数的数据表。</returns>
    private static DataTable BuildTvpTable(IReadOnlyList<UniversalEntity> entities)
    {
        // 构造空表——TVP 类型不含 DEFAULT，全部列必须显式提供。
        var table = new DataTable();

        // Id 列。
        table.Columns.Add("Id", typeof(Guid));

        // TypeId 列。
        table.Columns.Add("TypeId", typeof(Guid));

        // Role 列。
        table.Columns.Add("Role", typeof(byte));

        // ParentId 列。
        table.Columns.Add("ParentId", typeof(Guid));

        // Value 列。
        table.Columns.Add("Value", typeof(string));

        // Version 列。
        table.Columns.Add("Version", typeof(int));

        // IsDeleted 列。
        table.Columns.Add("IsDeleted", typeof(bool));

        // CreatedAt 列。
        table.Columns.Add("CreatedAt", typeof(DateTime));

        // UpdatedAt 列。
        table.Columns.Add("UpdatedAt", typeof(DateTime));

        // 统一时间戳——同一批次写入的行共享落库时刻。
        var nowUtc = DateTime.UtcNow;

        // 逐行填充。
        foreach (var entity in entities)
        {
            // 新行结构跟随 TYPE 定义。
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

            // 创建时间。
            row["CreatedAt"] = nowUtc;

            // 更新时间。
            row["UpdatedAt"] = nowUtc;

            // 加入表。
            table.Rows.Add(row);
        }

        // 返回数据包。
        return table;
    }

    // ============================================================================
    // 读取
    // ============================================================================

    /// <summary>
    /// 按主键 Id 取一行——哈希索引等值点查。
    /// </summary>
    /// <param name="id">行 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>找到返回行；未找到返回 null。</returns>
    public async Task<UniversalEntity?> GetByIdAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        // 拿到已打开的连接。
        using var connection = _dbContext.CreateConnection();

        // 哈希索引等值查询。
        const string sql = @"
            SELECT [Id], [TypeId], [Role], [ParentId], [Value], [Version], [IsDeleted], [CreatedAt], [UpdatedAt]
            FROM [dbo].[MemoryOptimizedEntity]
            WHERE [Id] = @Id AND [IsDeleted] = 0";

        // 构造命令。
        await using var command = connection.CreateCommand();

        // 写入 SQL。
        command.CommandText = sql;

        // 绑定 Id。
        command.Parameters.AddWithValue("@Id", id);

        // 单行读取。
        await using var reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleRow,
            cancellationToken);

        // 命中则映射返回。
        if (await reader.ReadAsync(cancellationToken))
        {
            // 映射单行。
            return MapReader(reader);
        }

        // 未命中返回 null。
        return null;
    }

    /// <summary>
    /// 子图预取——按实体 TypeId 一次往返拉回"实体行 + 全部属性行"的完整图切片。
    /// </summary>
    /// <param name="entityTypeId">实体类型 TypeId。</param>
    /// <param name="role">实体行角色常量。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>子图全部行。</returns>
    public async Task<IReadOnlyList<UniversalEntity>> GetByTypeIdWithChildrenAsync(
        Guid entityTypeId,
        byte role,
        CancellationToken cancellationToken = default)
    {
        // 拿到已打开的连接。
        using var connection = _dbContext.CreateConnection();

        // 子图预取 SQL——与磁盘表版本同构，仅表名不同。
        const string sql = @"
            SELECT [Id], [TypeId], [Role], [ParentId], [Value], [Version], [IsDeleted], [CreatedAt], [UpdatedAt]
            FROM [dbo].[MemoryOptimizedEntity]
            WHERE [IsDeleted] = 0
              AND ([TypeId] = @TypeId AND [Role] = @Role
                   OR [ParentId] IN (SELECT [Id]
                                     FROM [dbo].[MemoryOptimizedEntity]
                                     WHERE [TypeId] = @TypeId AND [Role] = @Role AND [IsDeleted] = 0))";

        // 构造命令。
        await using var command = connection.CreateCommand();

        // 写入 SQL。
        command.CommandText = sql;

        // 绑定 TypeId。
        command.Parameters.AddWithValue("@TypeId", entityTypeId);

        // 绑定 Role。
        command.Parameters.AddWithValue("@Role", role);

        // 执行 Reader。
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // 收集子图行。
        var results = new List<UniversalEntity>();

        // 逐行映射。
        while (await reader.ReadAsync(cancellationToken))
        {
            // 加入结果集。
            results.Add(MapReader(reader));
        }

        // 返回只读子图。
        return results;
    }

    /// <summary>
    /// 按 ParentId 取属性行——指针追逐单跳。
    /// </summary>
    /// <param name="parentId">父节点 Id。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>属性行集合。</returns>
    public async Task<IReadOnlyList<UniversalEntity>> GetByParentIdAsync(
        Guid parentId,
        CancellationToken cancellationToken = default)
    {
        // 拿到已打开的连接。
        using var connection = _dbContext.CreateConnection();

        // ParentId 索引查询。
        const string sql = @"
            SELECT [Id], [TypeId], [Role], [ParentId], [Value], [Version], [IsDeleted], [CreatedAt], [UpdatedAt]
            FROM [dbo].[MemoryOptimizedEntity]
            WHERE [ParentId] = @ParentId AND [IsDeleted] = 0
            ORDER BY [Id]";

        // 构造命令。
        await using var command = connection.CreateCommand();

        // 写入 SQL。
        command.CommandText = sql;

        // 绑定 ParentId。
        command.Parameters.AddWithValue("@ParentId", parentId);

        // 执行 Reader。
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        // 收集结果。
        var results = new List<UniversalEntity>();

        // 逐行映射。
        while (await reader.ReadAsync(cancellationToken))
        {
            // 加入结果集。
            results.Add(MapReader(reader));
        }

        // 返回只读集合。
        return results;
    }

    // ============================================================================
    // 工具
    // ============================================================================

    /// <summary>
    /// 将 Reader 当前行映射为 <see cref="UniversalEntity"/>。
    /// </summary>
    /// <param name="reader">已定位到一行的 Reader。</param>
    /// <returns>映射完成的行。</returns>
    private static UniversalEntity MapReader(SqlDataReader reader)
    {
        // 取各列 ordinal 一次，按 ordinal 取值避免字符串查找。
        var idOrdinal = reader.GetOrdinal("Id");
        var typeIdOrdinal = reader.GetOrdinal("TypeId");
        var roleOrdinal = reader.GetOrdinal("Role");
        var parentIdOrdinal = reader.GetOrdinal("ParentId");
        var valueOrdinal = reader.GetOrdinal("Value");
        var versionOrdinal = reader.GetOrdinal("Version");
        var isDeletedOrdinal = reader.GetOrdinal("IsDeleted");
        var createdAtOrdinal = reader.GetOrdinal("CreatedAt");
        var updatedAtOrdinal = reader.GetOrdinal("UpdatedAt");

        // 构造 record——可空列先判 DBNull。
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
