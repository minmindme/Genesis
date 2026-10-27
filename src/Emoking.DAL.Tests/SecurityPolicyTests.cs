// ANCHOR: test.security-policy - 策略驱动安全验收：密文落库、租户隔离、改一行数据换安全策略
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 策略驱动安全验收——安全规则全部来自池中 Role=5 数据：
/// 密文落库、透明解密、租户隔离、限流、以及"改一行策略数据 = 换安全策略"的热切换。
/// </summary>
public sealed class SecurityPolicyTests
{
    // User 实体类型的 Meta TypeId——与创世行一致。
    private static readonly Guid UserTypeId = new("11111111-1111-1111-1111-111111111111");

    // 加密策略行的固定 Id（创世第 8 行）。
    private static readonly Guid EncryptPolicyRowId = new("00000000-0000-0000-0000-000000000904");

    // 限流策略行的固定 Id（创世第 10 行）。
    private static readonly Guid RateLimitPolicyRowId = new("00000000-0000-0000-0000-000000000906");

    // 测试专用密钥环——仅用于本测试套件，非生产密钥。
    private static readonly IReadOnlyDictionary<string, byte[]> TestKeyRing =
        new Dictionary<string, byte[]>
        {
            // K1——与创世策略行 KeyRef=K1 对应。
            ["K1"] = Convert.FromBase64String("qX7wL0pZs2vB8nR4tE6yU1iO9aS3dF5gH7jK2lM4nQ8=")
        };

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
    /// 改写池中一条策略行的 Value——策略热切换的数据面操作。
    /// </summary>
    /// <param name="policyRowId">策略行 Id。</param>
    /// <param name="value">新的策略文本。</param>
    private static async Task RewritePolicyRowAsync(Guid policyRowId, string value)
    {
        // 打开默认连接。
        using var connection = new EmokingDbContext().CreateConnection();

        // 构造参数化 UPDATE。
        await using var command = connection.CreateCommand();

        // 更新策略文本。
        command.CommandText = "UPDATE [dbo].[UniversalEntity] SET [Value] = @Value, [UpdatedAt] = SYSUTCDATETIME() WHERE [Id] = @Id";

        // 绑定新策略。
        command.Parameters.AddWithValue("@Value", value);

        // 绑定策略行 Id。
        command.Parameters.AddWithValue("@Id", policyRowId);

        // 执行。
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 构造自举完成的元数据缓存。
    /// </summary>
    private static async Task<MetaCache> BuildLoadedCacheAsync()
    {
        // 磁盘仓储 + 缓存自举。
        var cache = new MetaCache(new UniversalEntityRepository(new EmokingDbContext()));
        await cache.LoadAsync();

        // 返回缓存。
        return cache;
    }

    /// <summary>
    /// 构造测试用 User 并拆解为 5 行。
    /// </summary>
    private static (UniversalEntity[] rows, User original) BuildUserRows(MetaCache cache, string name)
    {
        // 拆解器。
        var decomposer = new NodeEdgeDecomposer(cache);

        // 原始对象——姓名掺入调用参数便于多组数据并存。
        var original = new User
        {
            // Id 由拆解器生成。
            Id = Guid.Empty,
            // 姓名。
            Name = name,
            // 邮箱。
            Email = $"{name}@genesis.local",
            // 年龄。
            Age = 55,
            // 时间。
            CreatedAt = new DateTime(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc)
        };

        // 拆解。
        var rows = decomposer.Decompose(original, UserTypeId);

        // 返回。
        return (rows.ToArray(), original);
    }

    /// <summary>
    /// 核心验收：默认策略（AesGcm）下密文落库，经策略存储读回是明文。
    /// </summary>
    [Fact]
    public async Task PolicyDrivenStore_ValueIsCiphertextAtRest_PlaintextInMemory()
    {
        // 第 1 步：清池 + 自举（播种含 3 行 Role=5 策略）。
        await TruncateTableAsync();
        var cache = await BuildLoadedCacheAsync();

        // 加密策略已配置且启用。
        var encryptPolicy = cache.GetSecurityPolicy("EncryptValue");
        Assert.NotNull(encryptPolicy);
        Assert.Equal("AesGcm", encryptPolicy!["Algo"]);

        // 第 2 步：策略驱动存储包装磁盘表。
        var inner = new SqlServerUniversalStore(new UniversalEntityRepository(new EmokingDbContext()));
        var secured = new PolicyDrivenStore(inner, cache, "tenant-A", TestKeyRing);

        // 第 3 步：拆解并落库。
        var (rows, original) = BuildUserRows(cache, "密文张三");
        var inserted = await secured.BulkInsertAsync(rows);

        // 5 行写入。
        Assert.Equal(5, inserted);

        // 第 4 步：原始仓储（黑客视角）读回——必须是 AGC1 密文。
        var rawRepository = new UniversalEntityRepository(new EmokingDbContext());
        var entityRowId = rows.Single(row => row.ParentId == null).Id;

        // 逐行检查。
        foreach (var row in rows)
        {
            // 原始读取。
            var raw = await rawRepository.GetByIdAsync(row.Id);

            // 行存在。
            Assert.NotNull(raw);

            // 必须是 AGC1 密文（含实体行的空载荷密文）。
            Assert.StartsWith("AGC1:", raw!.Value);

            // 明文标记物绝不允许出现。
            Assert.DoesNotContain("密文张三", raw.Value);
            Assert.DoesNotContain("@genesis.local", raw.Value);
        }

        // 第 5 步：子图预取 + 强类型组装——对上层完全透明。
        var subgraph = await secured.GetSubGraphAsync(UserTypeId, UniversalEntity.RoleData);

        // 5 行明文子图。
        Assert.Equal(5, subgraph.Count);

        // 组装还原。
        var assembler = new ObjectAssembler(cache);
        var restored = assembler.Assemble<User>(subgraph, UserTypeId);

        // 明文断言。
        Assert.Equal(original.Name, restored.Name);
        Assert.Equal(original.Email, restored.Email);
        Assert.Equal(original.Age, restored.Age);
    }

    /// <summary>
    /// 租户隔离——租户 B 读不到租户 A 的数据（AAD 认证失败 → 不可见）。
    /// </summary>
    [Fact]
    public async Task PolicyDrivenStore_TenantB_CannotSeeTenantA_Data()
    {
        // 第 1 步：清池 + 自举 + 进程内存储。
        await TruncateTableAsync();
        var cache = await BuildLoadedCacheAsync();
        var inner = new InMemoryUniversalStore();

        // 第 2 步：租户 A 写入。
        var tenantA = new PolicyDrivenStore(inner, cache, "tenant-A", TestKeyRing);
        var (rows, _) = BuildUserRows(cache, "租户甲数据");
        await tenantA.BulkInsertAsync(rows);

        // 第 3 步：租户 B 读取——AAD 不匹配 → 全部不可见。
        var tenantB = new PolicyDrivenStore(inner, cache, "tenant-B", TestKeyRing);
        var entityRowId = rows.Single(row => row.ParentId == null).Id;
        Assert.Null(await tenantB.GetByIdAsync(entityRowId));
        var subgraphB = await tenantB.GetSubGraphAsync(UserTypeId, UniversalEntity.RoleData);
        Assert.Empty(subgraphB);

        // 第 4 步：租户 A 自己读回——一切如常。
        Assert.NotNull(await tenantA.GetByIdAsync(entityRowId));
        var subgraphA = await tenantA.GetSubGraphAsync(UserTypeId, UniversalEntity.RoleData);
        Assert.Equal(5, subgraphA.Count);

        // 第 5 步：错误密钥同样不可见。
        var wrongKeyRing = new Dictionary<string, byte[]>
        {
            // 换一把钥匙。
            ["K1"] = Convert.FromBase64String("rY8xM2nQ9pW4sZ7cV1bN6kJ3hG5fD0aL8oP2iU9eT4w=")
        };
        var wrongKeyStore = new PolicyDrivenStore(inner, cache, "tenant-A", wrongKeyRing);
        Assert.Null(await wrongKeyStore.GetByIdAsync(entityRowId));
    }

    /// <summary>
    /// 策略热切换核心演示：改一行 Role=5 数据（Algo=AesGcm → Plain），
    /// 新数据明文落库、历史密文仍可解——零代码零编译。
    /// </summary>
    [Fact]
    public async Task EditingRole5Row_SwitchesEncryptionStrategy_HistoryStillReadable()
    {
        // 第 1 步：清池 + 自举（默认策略 AesGcm）。
        await TruncateTableAsync();
        var cache = await BuildLoadedCacheAsync();
        var inner = new InMemoryUniversalStore();
        var secured = new PolicyDrivenStore(inner, cache, "tenant-A", TestKeyRing);

        // 第 2 步：第一组数据——密文写入。
        var (rowsA, originalA) = BuildUserRows(cache, "策略甲旧数据");
        await secured.BulkInsertAsync(rowsA);

        // 原始视角确认是 AGC1 密文——内层是进程内存储，直接读其原始内容。
        var firstRowRaw = await inner.GetByIdAsync(rowsA[0].Id);
        Assert.StartsWith("AGC1:", firstRowRaw!.Value);

        // 第 3 步：改一行策略数据——加密算法切换为 Plain。
        await RewritePolicyRowAsync(
            EncryptPolicyRowId,
            "Policy=EncryptValue|Target=Data|Algo=Plain|KeyRef=K1|Enabled=true");

        // 第 4 步：热重载策略。
        cache = await BuildLoadedCacheAsync();
        secured = new PolicyDrivenStore(inner, cache, "tenant-A", TestKeyRing);

        // 第 5 步：第二组数据——新策略下写入。
        var (rowsB, originalB) = BuildUserRows(cache, "策略乙新数据");
        await secured.BulkInsertAsync(rowsB);

        // 原始视角确认是 PLN1 明文标记（新策略生效）——内层是进程内存储，直接读其原始内容。
        var secondRowRaw = await inner.GetByIdAsync(rowsB[0].Id);
        Assert.StartsWith("PLN1:", secondRowRaw!.Value);

        // 第 6 步：混读——同一存储里 AGC1 历史密文与 PLN1 新明文都要能读回。
        var mixedSubgraph = await secured.GetSubGraphAsync(UserTypeId, UniversalEntity.RoleData);

        // 10 行混合子图。
        Assert.Equal(10, mixedSubgraph.Count);

        // 逐个实体组装验证——历史密文与新明文都完整还原。
        var assembler = new ObjectAssembler(cache);
        foreach (var (rows, original) in new[] { (rowsA, originalA), (rowsB, originalB) })
        {
            // 找该实体的实体行。
            var entityRow = rows.Single(row => row.ParentId == null);

            // 混合子图中筛选该实体的行。
            var own = mixedSubgraph.Where(row => row.Id == entityRow.Id || row.ParentId == entityRow.Id).ToList();

            // 5 行齐全。
            Assert.Equal(5, own.Count);

            // 组装并断言。
            var restored = assembler.Assemble<User>(own, UserTypeId);
            Assert.Equal(original.Name, restored.Name);
            Assert.Equal(original.Email, restored.Email);
        }
    }

    /// <summary>
    /// 关闭加密策略：Enabled=false 时新数据走 PLN1 明文——策略开关也是数据。
    /// </summary>
    [Fact]
    public async Task DisablingEncryptPolicy_SwitchesToPlainTextStorage()
    {
        // 第 1 步：清池 + 自举。
        await TruncateTableAsync();
        var cache = await BuildLoadedCacheAsync();
        var inner = new InMemoryUniversalStore();

        // 第 2 步：关闭加密策略。
        await RewritePolicyRowAsync(
            EncryptPolicyRowId,
            "Policy=EncryptValue|Target=Data|Algo=AesGcm|KeyRef=K1|Enabled=false");
        cache = await BuildLoadedCacheAsync();
        var secured = new PolicyDrivenStore(inner, cache, "tenant-A", TestKeyRing);

        // 第 3 步：写入数据。
        var (rows, original) = BuildUserRows(cache, "明文时代数据");
        await secured.BulkInsertAsync(rows);

        // 第 4 步：原始视角——PLN1 明文标记（内层是进程内存储，直接读其原始内容）。
        var firstRowRaw = await inner.GetByIdAsync(rows[0].Id);

        // PLN1 前缀。
        Assert.StartsWith("PLN1:", firstRowRaw!.Value);

        // 读回还原。
        var subgraph = await secured.GetSubGraphAsync(UserTypeId, UniversalEntity.RoleData);
        var restored = new ObjectAssembler(cache).Assemble<User>(subgraph, UserTypeId);
        Assert.Equal(original.Name, restored.Name);
    }

    /// <summary>
    /// 限流策略数据化：改 Role=5 的 Max 值，限流阈值即变。
    /// </summary>
    [Fact]
    public async Task EditingRateLimitPolicy_ChangesBulkInsertThreshold()
    {
        // 第 1 步：清池 + 自举。
        await TruncateTableAsync();
        var cache = await BuildLoadedCacheAsync();
        var secured = new PolicyDrivenStore(
            new InMemoryUniversalStore(), cache, "tenant-A", TestKeyRing);

        // 第 2 步：默认上限 10 万——4 行写入正常。
        var rows = Enumerable.Range(0, 4).Select(index => new UniversalEntity(
            Id: Guid.NewGuid(),
            TypeId: UserTypeId,
            Role: UniversalEntity.RoleData,
            ParentId: null,
            Value: $"row-{index}")).ToList();
        var inserted = await secured.BulkInsertAsync(rows);

        // 正常写入。
        Assert.Equal(4, inserted);

        // 第 3 步：改限流策略——Max=3。
        await RewritePolicyRowAsync(
            RateLimitPolicyRowId,
            "Policy=RateLimit|Target=BulkInsert|Max=3");
        cache = await BuildLoadedCacheAsync();
        secured = new PolicyDrivenStore(
            new InMemoryUniversalStore(), cache, "tenant-A", TestKeyRing);

        // 第 4 步：4 行超过新上限 3——拒绝。
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => secured.BulkInsertAsync(rows));

        // 第 5 步：3 行在上限内——通过。
        var inserted3 = await secured.BulkInsertAsync(rows.Take(3).ToList());
        Assert.Equal(3, inserted3);
    }

    /// <summary>
    /// 外来裸数据（无前缀行）在任何策略下都不可见。
    /// </summary>
    [Fact]
    public async Task PolicyDrivenStore_PlainTextRow_IsInvisible()
    {
        // 内层直接写明文行（绕过策略存储——模拟外来数据）。
        var inner = new InMemoryUniversalStore();
        var plainRow = new UniversalEntity(
            Id: Guid.NewGuid(),
            TypeId: UserTypeId,
            Role: UniversalEntity.RoleData,
            ParentId: null,
            Value: "明文外来行");
        await inner.BulkInsertAsync(new[] { plainRow });

        // 策略存储读取——不可见。
        var cache = await BuildLoadedCacheAsync();
        var secured = new PolicyDrivenStore(inner, cache, "tenant-A", TestKeyRing);
        Assert.Null(await secured.GetByIdAsync(plainRow.Id));
    }
}
