// ANCHOR: test.decomposer - NodeEdgeDecomposer 拆解单元测试
using System;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 拆解引擎的单元测试集——验证对象 → UniversalEntity 行集合的拆解正确性。
/// </summary>
public sealed class DecomposerTests
{
    // User 实体的 Meta TypeId——与 MetaCache 里硬编码一致。
    private static readonly Guid UserTypeId = new("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// 构造测试依赖——MetaCache（LoadAsync） + NodeEdgeDecomposer。
    /// </summary>
    private static async Task<(MetaCache cache, NodeEdgeDecomposer decomposer)> BuildSutAsync()
    {
        // DbContext + Repository 构造——原型期 MetaCache.LoadAsync 不查数据库，这里传 null 也不会用到。
        var dbContext = new EmokingDbContext();

        // 构造 Repository。
        var repo = new UniversalEntityRepository(dbContext);

        // 构造 MetaCache。
        var cache = new MetaCache(repo);

        // 调 LoadAsync 初始化硬编码元数据。
        await cache.LoadAsync();

        // 构造 Decomposer。
        var decomposer = new NodeEdgeDecomposer(cache);

        // 返回依赖对。
        return (cache, decomposer);
    }

    /// <summary>
    /// 一个 User 拆解后应该是 1 条实体行 + 4 条属性值行 = 5 行 UniversalEntity。
    /// </summary>
    [Fact]
    public async Task Decompose_User_ReturnsFiveRows()
    {
        // 第 1 步：构造被测对象。
        var (_, decomposer) = await BuildSutAsync();

        // 第 2 步：构造一个完整的 User 实例——原型期硬编码 Guid，避免每次 NewGuid 断言困难。
        var user = new User
        {
            Id = Guid.NewGuid(),
            Name = "张三",
            Email = "zhangsan@example.com",
            Age = 28,
            CreatedAt = new DateTime(2026, 10, 27, 10, 30, 0, DateTimeKind.Utc)
        };

        // 第 3 步：执行拆解。
        var rows = decomposer.Decompose(user, UserTypeId);

        // 第 4 步：断言总行数 = 1 实体 + 4 属性值 = 5 行。
        Assert.Equal(5, rows.Count);
    }

    /// <summary>
    /// 拆解后第一行是实体实例行：Role=Data，TypeId 指向 User Meta，ParentId=null。
    /// </summary>
    [Fact]
    public async Task Decompose_FirstRow_IsEntityRow()
    {
        // 第 1 步：构造被测对象。
        var (_, decomposer) = await BuildSutAsync();

        // 第 2 步：构造 User。
        var user = new User
        {
            Name = "张三",
            Email = "zhangsan@example.com",
            Age = 28,
            CreatedAt = DateTime.UtcNow
        };

        // 第 3 步：执行拆解。
        var rows = decomposer.Decompose(user, UserTypeId);

        // 第 4 步：取实体行（ParentId=null 且 TypeId=UserTypeId 且 Role=Data）。
        var entityRow = rows.Single(r => r.ParentId == null && r.TypeId == UserTypeId && r.Role == UniversalEntity.RoleData);

        // 实体行 Id 必须非空——Guid.NewGuid() 不会是 Empty。
        Assert.NotEqual(Guid.Empty, entityRow.Id);

        // 实体行 ParentId 必须 null——实体实例是根节点。
        Assert.Null(entityRow.ParentId);
    }

    /// <summary>
    /// 拆解后属性值行的 ParentId 全部指向实体行 Id。
    /// </summary>
    [Fact]
    public async Task Decompose_PropertyRows_HaveCorrectParentId()
    {
        // 第 1 步：构造被测对象。
        var (_, decomposer) = await BuildSutAsync();

        // 第 2 步：构造 User。
        var user = new User
        {
            Name = "张三",
            Email = "zhangsan@example.com",
            Age = 28,
            CreatedAt = DateTime.UtcNow
        };

        // 第 3 步：执行拆解。
        var rows = decomposer.Decompose(user, UserTypeId);

        // 第 4 步：找实体行 Id。
        var entityRowId = rows.Single(r => r.ParentId == null).Id;

        // 第 5 步：所有 ParentId != null 的行，ParentId 必须等于实体行 Id。
        var propertyRows = rows.Where(r => r.ParentId != null).ToList();

        // 属性行总数应为 4（Name、Email、Age、CreatedAt）。
        Assert.Equal(4, propertyRows.Count);

        // 每一行的 ParentId 都指向实体行 Id。
        Assert.All(propertyRows, row => Assert.Equal(entityRowId, row.ParentId));
    }

    /// <summary>
    /// 属性值的序列化验证——Name 和 Email 应该原样出现在 Value 列。
    /// </summary>
    [Fact]
    public async Task Decompose_PropertyValues_AreSerializedCorrectly()
    {
        // 第 1 步：构造被测对象。
        var (cache, decomposer) = await BuildSutAsync();

        // 第 2 步：构造 User。
        var user = new User
        {
            Name = "张三",
            Email = "zhangsan@example.com",
            Age = 28,
            CreatedAt = new DateTime(2026, 10, 27, 10, 30, 0, DateTimeKind.Utc)
        };

        // 第 3 步：执行拆解。
        var rows = decomposer.Decompose(user, UserTypeId);

        // 第 4 步：找 Name 属性 TypeId——通过 MetaCache 查属性名→TypeId 反向。
        // 先拿到 User MetaType。
        var userMeta = cache.GetTypeByTypeId(UserTypeId)!;

        // 找 Name 属性 TypeId。
        var nameTypeId = userMeta.PropertyTypeIds["Name"];

        // 找 Name 属性行。
        var nameRow = rows.Single(r => r.TypeId == nameTypeId);

        // 断言 Value 是 "张三"。
        Assert.Equal("张三", nameRow.Value);

        // 找 Age 属性 TypeId。
        var ageTypeId = userMeta.PropertyTypeIds["Age"];

        // 找 Age 属性行。
        var ageRow = rows.Single(r => r.TypeId == ageTypeId);

        // 断言 Value 是 "28"（InvariantCulture 序列化）。
        Assert.Equal("28", ageRow.Value);
    }
}
