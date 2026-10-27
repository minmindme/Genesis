// ANCHOR: dal.engine.engine-router - Role=4 基础版引擎路由
using System;

namespace Emoking.DAL;

/// <summary>
/// 引擎路由——Role=4 行对应的内存路由解析器。
/// </summary>
/// <remarks>
/// 这是 Role=4 的基础版（Phase 1）：
///   - 输入：实体 TypeId
///   - 输出：路由目标类型名（如 "UserRepository"）
///   - 不做加密、不做自进化协议——那是 Phase 2+ 的事
/// 
/// 用户明确要求"这轮一起做 Role=4 基础版"，Advisor 提醒"自进化加密最复杂绝对不能先碰"。
/// 本轮定位：跑通"Type → 路由目标"这个链路，验证 Role=4 行从表到内存的寻址逻辑。
/// </remarks>
public sealed class EngineRouter
{
    // 元数据缓存，用于查 Role=4 的路由规则。
    private readonly MetaCache _metaCache;

    /// <summary>
    /// 注入 MetaCache 初始化路由器。
    /// </summary>
    /// <param name="metaCache">已加载 Role=4 元数据的缓存实例。</param>
    public EngineRouter(MetaCache metaCache)
    {
        // 保存引用到私有字段。
        _metaCache = metaCache;
    }

    /// <summary>
    /// 按实体 TypeId 查找路由目标。
    /// </summary>
    /// <param name="entityTypeId">业务实体的 Meta TypeId。</param>
    /// <returns>路由目标类型名（如 "UserRepository"）；未注册返回 <see langword="null"/>。</returns>
    public string? Route(Guid entityTypeId)
    {
        // 调 MetaCache 查路由目标。
        var target = _metaCache.GetEngineRouteTarget(entityTypeId);

        // 直接返回——没注册就是 null。
        return target;
    }

    /// <summary>
    /// 路由结果结构体——带路由元信息，供 Phase 2 扩展加密协议。
    /// </summary>
    /// <param name="TargetName">路由目标类型名。</param>
    /// <param name="EntityTypeId">原请求的实体 TypeId。</param>
    /// <param name="ResolvedAt">路由解析时刻（UTC）。</param>
    public readonly record struct RouteResult(
        string? TargetName,
        Guid EntityTypeId,
        DateTime ResolvedAt);

    /// <summary>
    /// 带元信息的路由查询——返回 RouteResult 结构体，便于未来扩展（加密盐、协议版本等）。
    /// </summary>
    /// <param name="entityTypeId">业务实体的 Meta TypeId。</param>
    /// <returns>包含路由结果的 RouteResult。</returns>
    public RouteResult RouteWithMeta(Guid entityTypeId)
    {
        // 先查路由目标。
        var targetName = Route(entityTypeId);

        // 构造并返回带元信息的结果。
        return new RouteResult(
            TargetName: targetName,
            EntityTypeId: entityTypeId,
            ResolvedAt: DateTime.UtcNow);
    }
}
