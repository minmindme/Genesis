// ANCHOR: dal.engine.node-edge-decomposer - 对象 → Node/Edge 拆解引擎
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Emoking.DAL;

/// <summary>
/// Node/Edge 拆解引擎——将内存对象拆解为 UniversalEntity 行集合。
/// </summary>
/// <remarks>
/// 原型期简化策略：
///   - 实体实例 → 1 条 Role=2 行（TypeId 指向实体 Meta，ParentId=null，Value=null）
///   - 每个属性 → 1 条 Role=2 行（TypeId 指向属性 Meta，ParentId 指向实体实例，Value=属性值字符串）
///   - Edge：原型期同实体内属性用 ParentId 直接挂，不生成 Edge 行；Edge 留给跨实体关联
/// 
/// 以 User{Name, Email, Age, CreatedAt} 为例，输出 1 + 4 = 5 条 UniversalEntity 行。
/// </remarks>
public sealed class NodeEdgeDecomposer
{
    // 元数据缓存，用于将 C# 属性名映射到属性 TypeId。
    private readonly MetaCache _metaCache;

    /// <summary>
    /// 注入 MetaCache 初始化拆解器。
    /// </summary>
    /// <param name="metaCache">已加载的元数据缓存实例。</param>
    public NodeEdgeDecomposer(MetaCache metaCache)
    {
        // 保存引用到私有字段。
        _metaCache = metaCache;
    }

    /// <summary>
    /// 将一个业务对象拆解为 UniversalEntity 行集合。
    /// </summary>
    /// <param name="entity">待拆解的业务对象（原型期支持 User）。</param>
    /// <param name="entityTypeId">实体类型的 Meta TypeId。</param>
    /// <returns>拆解后的 UniversalEntity 行集合，至少 1 行（实体实例本身）。</returns>
    /// <exception cref="ArgumentNullException">entity 为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="InvalidOperationException">元数据未加载或找不到对应属性时抛出。</exception>
    public IReadOnlyList<UniversalEntity> Decompose(object entity, Guid entityTypeId)
    {
        // 参数 null 检查——防止后续反射空引用。
        if (entity is null)
        {
            // 抛出带参数名的 ArgumentNullException。
            throw new ArgumentNullException(nameof(entity));
        }

        // 从元数据缓存拿实体类型定义。
        var metaType = _metaCache.GetTypeByTypeId(entityTypeId);

        // 找不到实体类型元数据时抛异常——说明 LoadAsync 没调或者 TypeId 错了。
        if (metaType is null)
        {
            // 抛 InvalidOperationException，提示哪个 TypeId 找不到。
            throw new InvalidOperationException(
                $"未找到 TypeId = {entityTypeId} 的实体元数据，请确认 MetaCache.LoadAsync 已执行且该 TypeId 已注册。");
        }

        // 用反射拿到实体的所有公共实例属性——原型期只用公共属性。
        var entityType = entity.GetType();
        var properties = entityType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        // 准备结果集——预估 1 + properties.Length 条，避免扩容。
        var result = new List<UniversalEntity>(capacity: 1 + properties.Length);

        // 为实体实例生成一个新的 Guid——原型期占位雪花 ID。
        var instanceId = Guid.NewGuid();

        // 添加实体实例行：Role=2 Data，TypeId 指向实体 Meta，ParentId=null，Value=null。
        result.Add(new UniversalEntity(
            Id: instanceId,
            TypeId: entityTypeId,
            Role: UniversalEntity.RoleData,
            ParentId: null,
            Value: null));

        // 遍历每个公共属性，生成属性值行。
        foreach (var prop in properties)
        {
            // 跳过属性名为 Id 的——实体实例本身的 Id 已经存为行的 Id 列了，属性 Id 不重复存。
            // 注意：这里假设业务实体有一个 Guid Id 属性，原型期 User 符合这个假设。
            if (string.Equals(prop.Name, "Id", StringComparison.Ordinal))
            {
                // 跳过 Id 属性。
                continue;
            }

            // 查该属性在 MetaCache 里有没有注册——没注册说明元数据不全。
            if (!metaType.PropertyTypeIds.TryGetValue(prop.Name, out var propertyTypeId))
            {
                // 抛异常——属性没有对应的元数据 TypeId。
                throw new InvalidOperationException(
                    $"实体 '{metaType.TypeName}' 的属性 '{prop.Name}' 未在 MetaCache 中注册。");
            }

            // 反射取属性值。
            var value = prop.GetValue(entity);

            // 把属性值序列化为字符串——原型期统一用 ToString + InvariantCulture，防止日期/数字区域性差异。
            var valueString = SerializeValue(value);

            // 为属性值行生成新 Guid。
            var propertyRowId = Guid.NewGuid();

            // 添加属性值行：Role=2 Data，TypeId 指向属性 Meta，ParentId 指向实体实例 Id。
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

    /// <summary>
    /// 将任意对象序列化为字符串——原型期极简实现，动态拆解轨复用同一规则。
    /// </summary>
    /// <param name="value">属性值（可能为 null）。</param>
    /// <returns>序列化后的字符串；null 返回 <see langword="null"/>。</returns>
    internal static string? SerializeValue(object? value)
    {
        // null 值直接返回 null——数据库列存 DBNull。
        if (value is null)
        {
            // 返回 null。
            return null;
        }

        // DateTime 用 O 格式（ISO 8601 带时区），CultureInfo.InvariantCulture 防止本地化。
        if (value is DateTime dt)
        {
            // 格式化为 "2026-10-27T10:30:00.0000000Z" 形式。
            return dt.ToString("O", CultureInfo.InvariantCulture);
        }

        // DateTimeOffset 同样 ISO 8601。
        if (value is DateTimeOffset dto)
        {
            // 格式化为 ISO 8601 带偏移。
            return dto.ToString("O", CultureInfo.InvariantCulture);
        }

        // Guid 直接 ToString()，标准格式 D。
        if (value is Guid g)
        {
            // 返回 Guid 字符串。
            return g.ToString();
        }

        // 其他值类型（int、string、bool 等）直接 ToString + InvariantCulture。
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }
}
