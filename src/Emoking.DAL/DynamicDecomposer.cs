// ANCHOR: dal.engine.dynamic-decomposer - 动态轨拆解器：Dictionary → 同构行（无需 C# 实体类）
using System;
using System.Collections.Generic;

namespace Emoking.DAL;

/// <summary>
/// 动态轨拆解器——把 <see cref="Dictionary{TKey,TValue}"/> 形态的业务数据拆解为同构行，
/// **不要求存在任何 C# 实体类**。这是"言出法随"的入口：先插元数据，再丢字典，业务即存在。
/// </summary>
/// <remarks>
/// 与强类型轨（<see cref="NodeEdgeDecomposer"/>）的分工：
///   - 强类型轨：业务对象是编译期已知类型，反射公共属性；
///   - 动态轨：数据形态是运行期字典，属性集由池中 Role=1 元数据决定。
/// payload 中出现元数据未注册的属性键时显式抛错——言出法随的前提是元数据先行，静默忽略会掩盖数据丢失。
/// </remarks>
public sealed class DynamicDecomposer
{
    // 元数据缓存——属性键 → 属性 TypeId 的唯一权威来源。
    private readonly MetaCache _metaCache;

    /// <summary>
    /// 注入元数据缓存初始化动态拆解器。
    /// </summary>
    /// <param name="metaCache">已加载的元数据缓存。</param>
    public DynamicDecomposer(MetaCache metaCache)
    {
        // 保存缓存引用。
        _metaCache = metaCache;
    }

    /// <summary>
    /// 将万物容器拆解为同构行集合——动态轨对 UniversalObject 的直接支持。
    /// </summary>
    /// <param name="payload">万物容器实例。</param>
    /// <param name="entityTypeId">实体类型的 Meta TypeId（须已在池中注册）。</param>
    /// <returns>拆解后的行集合：1 实体行 + N 属性行。</returns>
    /// <exception cref="ArgumentNullException">payload 为 null 时抛出。</exception>
    public IReadOnlyList<UniversalEntity> Decompose(
        UniversalObject payload,
        Guid entityTypeId)
    {
        // 委托字典版本——万物容器的属性字典即数据面。
        return Decompose(payload?.GetProperties()
            ?? throw new ArgumentNullException(nameof(payload)), entityTypeId);
    }

    /// <summary>
    /// 将字典形态的业务数据拆解为同构行集合。
    /// </summary>
    /// <param name="payload">业务数据字典——键为属性名，值为任意可序列化对象。</param>
    /// <param name="entityTypeId">实体类型的 Meta TypeId（须已在池中注册）。</param>
    /// <returns>拆解后的行集合：1 实体行 + N 属性行。</returns>
    /// <exception cref="ArgumentNullException">payload 为 null 时抛出。</exception>
    /// <exception cref="InvalidOperationException">实体类型未注册，或 payload 含元数据未声明的属性键时抛出。</exception>
    public IReadOnlyList<UniversalEntity> Decompose(
        IReadOnlyDictionary<string, object?> payload,
        Guid entityTypeId)
    {
        // payload null 检查。
        if (payload is null)
        {
            // 抛 ArgumentNullException。
            throw new ArgumentNullException(nameof(payload));
        }

        // 从元数据缓存取实体类型定义。
        var metaType = _metaCache.GetTypeByTypeId(entityTypeId);

        // 类型未注册时显式失败——动态轨同样要过元数据这一关。
        if (metaType is null)
        {
            // 抛出带 TypeId 的异常。
            throw new InvalidOperationException(
                $"未找到 TypeId = {entityTypeId} 的实体元数据，请先向池中插入 Role=1 元数据行并重载 MetaCache。");
        }

        // 准备结果集——容量预估 1 + 属性数。
        var result = new List<UniversalEntity>(1 + payload.Count);

        // 为实体实例生成新 Guid。
        var instanceId = Guid.NewGuid();

        // 实体行：Role=2 Data，ParentId 为空，Value 为空。
        result.Add(new UniversalEntity(
            Id: instanceId,
            TypeId: entityTypeId,
            Role: UniversalEntity.RoleData,
            ParentId: null,
            Value: null));

        // 遍历字典的每个键值对。
        foreach (var pair in payload)
        {
            // 查属性键是否在元数据中注册——未注册即拒绝。
            if (!metaType.PropertyTypeIds.TryGetValue(pair.Key, out var propertyTypeId))
            {
                // 抛出明确的属性未注册异常。
                throw new InvalidOperationException(
                    $"实体 '{metaType.TypeName}' 的属性 '{pair.Key}' 未在池中元数据注册，" +
                    "动态拆解拒绝未知属性——请先插入对应的 Role=1 属性行。");
            }

            // 复用强类型轨的序列化规则——同一种行结构必须遵循同一种值编码。
            var valueString = NodeEdgeDecomposer.SerializeValue(pair.Value);

            // 为属性行生成新 Guid。
            var propertyRowId = Guid.NewGuid();

            // 属性行：ParentId 指向实体实例行。
            result.Add(new UniversalEntity(
                Id: propertyRowId,
                TypeId: propertyTypeId,
                Role: UniversalEntity.RoleData,
                ParentId: instanceId,
                Value: valueString));
        }

        // 返回只读集合。
        return result;
    }
}
