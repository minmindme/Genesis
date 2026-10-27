// ANCHOR: test.engine-router - Role=4 基础版路由测试
using System;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// EngineRouter 的单元测试集——验证 Role=4 基础路由。
/// </summary>
public sealed class EngineRouterTests
{
    // User 实体的 Meta TypeId。
    private static readonly Guid UserTypeId = new("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// 构造 EngineRouter + MetaCache。
    /// </summary>
    private static async Task<EngineRouter> BuildSutAsync()
    {
        // DbContext + Repository。
        var dbContext = new EmokingDbContext();
        var repo = new UniversalEntityRepository(dbContext);

        // MetaCache + LoadAsync。
        var cache = new MetaCache(repo);
        await cache.LoadAsync();

        // 构造路由器。
        return new EngineRouter(cache);
    }

    /// <summary>
    /// UserTypeId 应该路由到 "UserRepository"——MetaCache 里硬编码的。
    /// </summary>
    [Fact]
    public async Task Route_UserTypeId_ReturnsUserRepository()
    {
        // 第 1 步：构造路由器。
        var router = await BuildSutAsync();

        // 第 2 步：执行路由。
        var target = router.Route(UserTypeId);

        // 第 3 步：断言——路由目标是 "UserRepository"。
        Assert.Equal("UserRepository", target);
    }

    /// <summary>
    /// 未注册的 TypeId 应该返回 null。
    /// </summary>
    [Fact]
    public async Task Route_UnknownTypeId_ReturnsNull()
    {
        // 第 1 步：构造路由器。
        var router = await BuildSutAsync();

        // 第 2 步：用一个随机 Guid——MetaCache 里不会注册。
        var unknownTypeId = Guid.NewGuid();

        // 第 3 步：执行路由。
        var target = router.Route(unknownTypeId);

        // 第 4 步：断言——返回 null。
        Assert.Null(target);
    }

    /// <summary>
    /// RouteWithMeta 返回完整的 RouteResult 结构体。
    /// </summary>
    [Fact]
    public async Task RouteWithMeta_ReturnsRouteResult()
    {
        // 第 1 步：构造路由器。
        var router = await BuildSutAsync();

        // 第 2 步：执行带元信息的路由。
        var result = router.RouteWithMeta(UserTypeId);

        // 第 3 步：断言路由目标正确。
        Assert.Equal("UserRepository", result.TargetName);

        // 原请求的 TypeId 被保留。
        Assert.Equal(UserTypeId, result.EntityTypeId);

        // ResolvedAt 是 UTC 时间且接近当前时刻。
        Assert.True((DateTime.UtcNow - result.ResolvedAt).TotalSeconds < 5);
    }
}
