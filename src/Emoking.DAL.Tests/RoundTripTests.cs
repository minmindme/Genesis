// ANCHOR: test.round-trip - 完整往返测试：User → 拆解 → 组装 → 断言相等
using System;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 往返验证——拆解器 + 组装器的集成测试。
/// 这是 Advisor 说的"最小垂直切片原型"的核心：验证同构模型是否能正确 round-trip。
/// </summary>
public sealed class RoundTripTests
{
    // User 实体的 Meta TypeId。
    private static readonly Guid UserTypeId = new("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// 构造完整依赖链。
    /// </summary>
    private static async Task<(NodeEdgeDecomposer decomposer, ObjectAssembler assembler)> BuildSutAsync()
    {
        // DbContext + Repository。
        var dbContext = new EmokingDbContext();
        var repo = new UniversalEntityRepository(dbContext);

        // MetaCache + LoadAsync。
        var cache = new MetaCache(repo);
        await cache.LoadAsync();

        // 构造拆解器 + 组装器，共享 MetaCache。
        var decomposer = new NodeEdgeDecomposer(cache);
        var assembler = new ObjectAssembler(cache);

        // 返回依赖对。
        return (decomposer, assembler);
    }

    /// <summary>
    /// 完整往返：User → 拆解 → 组装，验证所有属性值一致。
    /// </summary>
    [Fact]
    public async Task RoundTrip_User_ShouldPreserveAllProperties()
    {
        // 第 1 步：构造被测对象。
        var (decomposer, assembler) = await BuildSutAsync();

        // 第 2 步：构造原始 User——每个属性赋不同值，方便断言。
        var original = new User
        {
            // 原始 Guid 留空——拆解时会被覆盖为实体行 Id。
            Id = Guid.Empty,
            Name = "张三",
            Email = "zhangsan@example.com",
            Age = 28,
            CreatedAt = new DateTime(2026, 10, 27, 10, 30, 0, DateTimeKind.Utc)
        };

        // 第 3 步：执行拆解。
        var rows = decomposer.Decompose(original, UserTypeId);

        // 行数断言。
        Assert.Equal(5, rows.Count);

        // 第 4 步：执行组装。
        var restored = assembler.Assemble<User>(rows, UserTypeId);

        // 第 5 步：断言——Name 应该完全相等。
        Assert.Equal(original.Name, restored.Name);

        // Email 相等。
        Assert.Equal(original.Email, restored.Email);

        // Age 相等。
        Assert.Equal(original.Age, restored.Age);

        // CreatedAt——DateTime 精度到毫秒，用 Assert.Equal 带比较范围。
        Assert.Equal(original.CreatedAt, restored.CreatedAt, TimeSpan.FromMilliseconds(1));

        // Id 应该被赋值——从 Guid.Empty 变成拆解时生成的新 Guid。
        Assert.NotEqual(Guid.Empty, restored.Id);
    }

    /// <summary>
    /// 往返后的 Id 与拆解时生成的实体行 Id 一致。
    /// </summary>
    [Fact]
    public async Task RoundTrip_IdMatchesEntityRow()
    {
        // 第 1 步：构造被测对象。
        var (decomposer, assembler) = await BuildSutAsync();

        // 第 2 步：构造 User。
        var user = new User
        {
            Name = "李四",
            Email = "lisi@example.com",
            Age = 30,
            CreatedAt = DateTime.UtcNow
        };

        // 第 3 步：拆解。
        var rows = decomposer.Decompose(user, UserTypeId);

        // 找实体行 Id。
        var entityRowId = rows.Single(r => r.ParentId == null).Id;

        // 第 4 步：组装。
        var restored = assembler.Assemble<User>(rows, UserTypeId);

        // 第 5 步：断言——组装后的 Id 就是实体行 Id。
        Assert.Equal(entityRowId, restored.Id);
    }
}
