// ANCHOR: test.universal-store - IUniversalStore 抽象与工厂测试（改一行数据 = 换物理引擎）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 存储抽象测试——验证三种 Provider 经同一接口的行为一致性，以及工厂按池中指令动态落位引擎。
/// </summary>
public sealed class UniversalStoreTests
{
    // User 实体类型的 Meta TypeId——与库中创世行一致。
    private static readonly Guid UserTypeId = new("11111111-1111-1111-1111-111111111111");

    // 存储引擎选择指令行的固定 Id。
    private static readonly Guid StorageProviderRowId = new("00000000-0000-0000-0000-000000000902");

    /// <summary>
    /// 清空磁盘表——从空池开始。
    /// </summary>
    private static async Task TruncateTableAsync()
    {
        // 打开默认连接。
        using var connection = new EmokingDbContext().CreateConnection();

        // 构造清空命令。
        await using var command = connection.CreateCommand();

        // TRUNCATE 全表。
        command.CommandText = "TRUNCATE TABLE [dbo].[UniversalEntity]";

        // 执行。
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 直接改写池中 StorageProvider 指令行的 Value——验证"改一行数据 = 换物理引擎"。
    /// </summary>
    /// <param name="providerName">目标引擎名。</param>
    private static async Task RewriteProviderInstructionAsync(string providerName)
    {
        // 打开默认连接。
        using var connection = new EmokingDbContext().CreateConnection();

        // 构造参数化 UPDATE。
        await using var command = connection.CreateCommand();

        // 更新指令行 Value 并同步 UpdatedAt。
        command.CommandText = "UPDATE [dbo].[UniversalEntity] SET [Value] = @Value, [UpdatedAt] = SYSUTCDATETIME() WHERE [Id] = @Id";

        // 绑定新引擎名。
        command.Parameters.AddWithValue("@Value", $"StorageProvider={providerName}");

        // 绑定指令行 Id。
        command.Parameters.AddWithValue("@Id", StorageProviderRowId);

        // 执行更新。
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 构造自举完成的元数据缓存（先清池，让引擎重新播种）。
    /// </summary>
    private static async Task<MetaCache> BuildLoadedCacheAsync()
    {
        // 磁盘仓储 + 缓存。
        var cache = new MetaCache(new UniversalEntityRepository(new EmokingDbContext()));

        // 自举加载（池空时播种）。
        await cache.LoadAsync();

        // 返回缓存。
        return cache;
    }

    /// <summary>
    /// 统一的 Store 行为一致性链路：写入 → 点查 → 子图 → 软删 → 全部路径不可见。
    /// </summary>
    /// <param name="store">被测存储引擎。</param>
    private static async Task RunStoreConsistencyChainAsync(IUniversalStore store)
    {
        // 第 1 步：拆解一个 User 为 5 行并整包写入。
        var cache = new MetaCache(new UniversalEntityRepository(new EmokingDbContext()));
        await cache.LoadAsync();
        var decomposer = new NodeEdgeDecomposer(cache);
        var assembler = new ObjectAssembler(cache);

        // 原始对象。
        var original = new User
        {
            // Id 由拆解器生成。
            Id = Guid.Empty,
            // 中文字段。
            Name = "商店张三",
            // 邮箱。
            Email = "store@genesis.local",
            // 年龄。
            Age = 33,
            // 时间。
            CreatedAt = new DateTime(2026, 10, 27, 9, 0, 0, DateTimeKind.Utc)
        };

        // 拆解。
        var rows = decomposer.Decompose(original, UserTypeId);

        // 5 行。
        Assert.Equal(5, rows.Count);

        // 整包写入。
        var inserted = await store.BulkInsertAsync(rows);

        // 行数一致。
        Assert.Equal(5, inserted);

        // 第 2 步：点查实体行。
        var entityRow = rows.Single(row => row.ParentId == null);
        var fetched = await store.GetByIdAsync(entityRow.Id);

        // 命中。
        Assert.NotNull(fetched);

        // Id 一致。
        Assert.Equal(entityRow.Id, fetched!.Id);

        // 第 3 步：子图预取。
        var subgraph = await store.GetSubGraphAsync(UserTypeId, UniversalEntity.RoleData);

        // 5 行子图。
        Assert.Equal(5, subgraph.Count);

        // 组装属性一致。
        var restored = assembler.Assemble<User>(subgraph, UserTypeId);
        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.Email, restored.Email);
        Assert.Equal(original.Age, restored.Age);
        Assert.Equal(original.CreatedAt, restored.CreatedAt, TimeSpan.FromMilliseconds(1));

        // 第 4 步：软删实体行——所有路径不可见。
        var deleted = await store.DeleteAsync(entityRow.Id);

        // 命中。
        Assert.True(deleted);

        // 点查不可见。
        Assert.Null(await store.GetByIdAsync(entityRow.Id));

        // 子图为空。
        var afterDelete = await store.GetSubGraphAsync(UserTypeId, UniversalEntity.RoleData);
        Assert.Empty(afterDelete);
    }

    /// <summary>
    /// 工厂落位：池中指令为 SqlServer 时产出磁盘表 Store。
    /// </summary>
    [Fact]
    public async Task Factory_WithSqlServerInstruction_ProducesSqlServerStore()
    {
        // 清池 + 自举（播种 7 行，默认指令 SqlServer）。
        await TruncateTableAsync();
        var cache = await BuildLoadedCacheAsync();

        // 工厂创建——SqlServer 实现无 Dispose 需求，无需释放。
        var store = UniversalStoreFactory.Create(cache, new EmokingDbContext());

        // 落位为 SqlServer 实现。
        Assert.IsType<SqlServerUniversalStore>(store);
    }

    /// <summary>
    /// 核心验收：只改池中一行指令数据（不碰任何代码），引擎物理引擎即完成切换。
    /// </summary>
    [Fact]
    public async Task Factory_EditingInstructionRow_SwitchesEngineWithoutCodeChange()
    {
        // 第 1 步：清池 + 自举播种（指令默认 SqlServer）。
        await TruncateTableAsync();
        var cache = await BuildLoadedCacheAsync();
        var factoryStore = UniversalStoreFactory.Create(cache, new EmokingDbContext());

        // 默认落位 SqlServer。
        Assert.IsType<SqlServerUniversalStore>(factoryStore);

        // 第 2 步：改那一行数据——StorageProvider=InMemory。
        await RewriteProviderInstructionAsync("InMemory");
        cache = await BuildLoadedCacheAsync();
        var inMemoryStore = UniversalStoreFactory.Create(cache, new EmokingDbContext());

        // 落位切换为进程内字典。
        Assert.IsType<InMemoryUniversalStore>(inMemoryStore);

        // 第 3 步：再改一行数据——StorageProvider=FasterKv。
        await RewriteProviderInstructionAsync("FasterKv");
        cache = await BuildLoadedCacheAsync();
        var fasterStore = UniversalStoreFactory.Create(cache, new EmokingDbContext());

        // 落位切换为 FASTER。
        Assert.IsType<FasterKvUniversalStore>(fasterStore);

        // 清理 FASTER 日志目录。
        if (fasterStore is IDisposable disposable)
        {
            // 释放设备。
            disposable.Dispose();
        }
    }

    /// <summary>
    /// SqlServer Store 行为一致性链路。
    /// </summary>
    [Fact]
    public async Task Store_SqlServer_ConsistencyChain()
    {
        // 清池。
        await TruncateTableAsync();

        // 构造 Store。
        var store = new SqlServerUniversalStore(new UniversalEntityRepository(new EmokingDbContext()));

        // 跑一致性链路。
        await RunStoreConsistencyChainAsync(store);
    }

    /// <summary>
    /// 进程内字典 Store 行为一致性链路。
    /// </summary>
    [Fact]
    public async Task Store_InMemory_ConsistencyChain()
    {
        // 清池 + 构造 Store。
        await TruncateTableAsync();
        var store = new InMemoryUniversalStore();

        // 跑一致性链路。
        await RunStoreConsistencyChainAsync(store);
    }

    /// <summary>
    /// FASTER Store 行为一致性链路。
    /// </summary>
    [Fact]
    public async Task Store_FasterKv_ConsistencyChain()
    {
        // 清池 + 构造 Store。
        // 临时目录放在 bin 输出目录下——实测沙箱拒绝对 %TEMP% 子目录的创建（UnauthorizedAccess），工作目录树可写。
        await TruncateTableAsync();
        var tempDir = System.IO.Path.Combine(
            AppContext.BaseDirectory,
            $"genesis_store_test_{Guid.NewGuid():N}");

        // 显式管理生命周期——目录删除必须在设备释放之后，否则文件占用导致删除失败。
        var store = new FasterKvUniversalStore(tempDir);
        try
        {
            // 跑一致性链路。
            await RunStoreConsistencyChainAsync(store);
        }
        finally
        {
            // 先释放设备句柄。
            store.Dispose();

            // 尽力删除临时日志目录——FASTER 设备句柄释放存在延迟，失败时不影响测试结论，交给系统 Temp 回收。
            try
            {
                // 目录存在才删除。
                if (System.IO.Directory.Exists(tempDir))
                {
                    // 清理。
                    System.IO.Directory.Delete(tempDir, recursive: true);
                }
            }
            catch (IOException)
            {
                // 句柄未释放时忽略——Temp 目录由操作系统回收。
            }
            catch (UnauthorizedAccessException)
            {
                // 同上。
            }
        }
    }
}
