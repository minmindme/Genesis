// ANCHOR: dal.store.factory - 存储引擎工厂：按池中 Role=4 指令动态落位物理引擎
using System;

namespace Emoking.DAL;

/// <summary>
/// 存储引擎工厂——读取元数据缓存中的 StorageProvider 指令，动态创建对应物理引擎实例。
/// </summary>
/// <remarks>
/// "逻辑即数据"的物理层落点：池中指令行 `StorageProvider=SqlServer|InMemory|FasterKv`
/// 决定系统跑在哪种引擎上；改那一行数据并重载元数据 = 换物理引擎，零代码变更。
/// 池中无该指令时保守回退 SqlServer（唯一完整 ACID 实现）。
/// </remarks>
public static class UniversalStoreFactory
{
    /// <summary>
    /// 按元数据指令创建存储引擎实例。
    /// </summary>
    /// <param name="metaCache">已加载的元数据缓存（须先执行 LoadAsync）。</param>
    /// <param name="dbContext">数据库连接工厂——SqlServer 引擎需要。</param>
    /// <returns>与指令匹配的存储引擎实例。</returns>
    /// <exception cref="InvalidOperationException">指令指定了未实现的引擎名时抛出。</exception>
    public static IUniversalStore Create(MetaCache metaCache, EmokingDbContext dbContext)
    {
        // 从池中指令读引擎名。
        var providerName = metaCache.GetStorageProviderName();

        // 按引擎名落位。
        return providerName switch
        {
            // SQL Server 磁盘表——完整 ACID。
            "SqlServer" => new SqlServerUniversalStore(new UniversalEntityRepository(dbContext)),

            // 进程内字典——最快、重启全丢。
            "InMemory" => new InMemoryUniversalStore(),

            // FASTER 嵌入式 LSM——可持久化。
            "FasterKv" => new FasterKvUniversalStore("genesis_faster_logs"),

            // 未知引擎名——显式失败，防止静默错配。
            _ => throw new InvalidOperationException(
                $"池中 StorageProvider 指令指定的引擎 '{providerName}' 未实现。" +
                "支持的值：SqlServer / InMemory / FasterKv。")
        };
    }
}
