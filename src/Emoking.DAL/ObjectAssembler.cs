// ANCHOR: dal.engine.object-assembler - Node/Edge → C# 对象 反向挂载
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace Emoking.DAL;

/// <summary>
/// 对象组装引擎——将 UniversalEntity 行集合反向挂载回 C# 业务对象。
/// </summary>
/// <remarks>
/// 反向挂载流程（原型期单实体简化版，不遍历 Edge）：
///   1. 找到 TypeId = entityTypeId 的实体行（Role=2 Data）——确定实体实例 Id。
///   2. 找所有 ParentId = 实体行 Id 的属性值行——确定每个属性的 TypeId + Value。
///   3. 按 TypeId 反查属性名 → 反射赋值到新 C# 实例。
/// 
/// Advisor 警告"零计算纯寻址是 myth"——这里就是证明：遍历 + 反射赋值就是计算。
/// 原型期验证的核心假设：指针跳转（GetByParentId）+ 反射赋值 的速度 vs SQL JOIN。
/// </remarks>
public sealed class ObjectAssembler
{
    // 元数据缓存，用于 TypeId → 属性名 的反查。
    private readonly MetaCache _metaCache;

    /// <summary>
    /// 注入 MetaCache 初始化组装器。
    /// </summary>
    /// <param name="metaCache">已加载的元数据缓存实例。</param>
    public ObjectAssembler(MetaCache metaCache)
    {
        // 保存引用到私有字段。
        _metaCache = metaCache;
    }

    /// <summary>
    /// 将 UniversalEntity 行集合反向组装为指定类型的 C# 对象。
    /// </summary>
    /// <typeparam name="T">目标业务类型（如 User）。</typeparam>
    /// <param name="rows">包含实体行 + 属性值行的 UniversalEntity 集合。</param>
    /// <param name="entityTypeId">实体类型的 Meta TypeId——用于定位实体实例行。</param>
    /// <returns>组装完成的 T 实例；实体行不存在时抛异常。</returns>
    /// <exception cref="ArgumentNullException">rows 为 <see langword="null"/> 时抛出。</exception>
    /// <exception cref="InvalidOperationException">找不到实体行或属性反序列化失败时抛出。</exception>
    public T Assemble<T>(IReadOnlyList<UniversalEntity> rows, Guid entityTypeId)
        where T : class, new()
    {
        // rows null 检查。
        if (rows is null)
        {
            // 抛 ArgumentNullException。
            throw new ArgumentNullException(nameof(rows));
        }

        // 在 rows 里找实体实例行：TypeId = entityTypeId 且 Role = Data。
        UniversalEntity? entityRow = null;

        // 遍历 rows 查找实体行。
        foreach (var row in rows)
        {
            // 两个条件都匹配才是实体行。
            if (row.TypeId == entityTypeId && row.Role == UniversalEntity.RoleData && row.ParentId == null)
            {
                // 找到，跳出循环。
                entityRow = row;
                break;
            }
        }

        // 没找到实体行说明 rows 不全或 TypeId 错了。
        if (entityRow is null)
        {
            // 抛 InvalidOperationException，提示哪个 TypeId 找不到。
            throw new InvalidOperationException(
                $"在给定的 {rows.Count} 行中未找到 TypeId = {entityTypeId} 的实体实例行。");
        }

        // 快照行检测——StorageMode 切到 Graph 但存量数据仍是 SNAP1 快照行时显式失败（语义见 StorageModeMismatchException）。
        if (entityRow.Value is not null && entityRow.Value.StartsWith("SNAP1:", StringComparison.Ordinal))
        {
            // 抛出模式不匹配异常。
            throw new StorageModeMismatchException(
                $"TypeId = {entityTypeId} 的实体行是 SNAP1 快照形态，但当前按图模式（Graph）组装——" +
                "请将 StorageMode 指令改回 Snapshot 或先迁移数据。");
        }

        // 反射构造 T 的新实例——要求 T 有参数less 构造函数（new() 约束已保证）。
        var instance = new T();

        // 把实体行 Id 赋给实例的 Id 属性——如果存在。
        AssignEntityId(instance, entityRow.Id);

        // 遍历 rows 找所有属性值行——ParentId = 实体行 Id 且 Role = Data。
        foreach (var row in rows)
        {
            // 跳过实体行自身。
            if (row.Id == entityRow.Id)
            {
                // 跳过。
                continue;
            }

            // 必须是属性值行：Role=Data 且 ParentId 指向实体行。
            if (row.Role != UniversalEntity.RoleData || row.ParentId != entityRow.Id)
            {
                // 跳过非属性值行。
                continue;
            }

            // 通过 TypeId 反查属性名。
            var propertyName = _metaCache.GetPropertyNameByTypeId(row.TypeId);

            // 没查到属性名说明元数据不全。
            if (propertyName is null)
            {
                // 跳过——原型期容错处理，不抛异常防止测试里漏注册属性直接挂掉。
                // 真实部署时应该 throw。
                continue;
            }

            // 反射获取目标属性。
            var prop = typeof(T).GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance);

            // 属性不存在（可能业务实体改名了但元数据没同步）。
            if (prop is null || !prop.CanWrite)
            {
                // 跳过。
                continue;
            }

            // 反序列化 Value 到属性类型。
            var deserialized = DeserializeValue(row.Value, prop.PropertyType);

            // 赋值——原型期直接 SetValue，不做类型检查（DeserializeValue 已处理）。
            prop.SetValue(instance, deserialized);
        }

        // 返回组装完成的实例。
        return instance;
    }

    /// <summary>
    /// 将实体行 Id 赋给实例的 Id 属性（如果存在）。
    /// </summary>
    /// <param name="instance">业务对象实例。</param>
    /// <param name="entityId">实体行 Id。</param>
    private static void AssignEntityId(object instance, Guid entityId)
    {
        // 反射找名为 Id 的公共属性。
        var idProp = instance.GetType().GetProperty(
            "Id",
            BindingFlags.Public | BindingFlags.Instance);

        // 属性存在且可写。
        if (idProp is not null && idProp.CanWrite && idProp.PropertyType == typeof(Guid))
        {
            // 赋值。
            idProp.SetValue(instance, entityId);
        }
    }

    /// <summary>
    /// 将字符串反序列化为指定类型——原型期极简实现，覆盖常见类型。
    /// </summary>
    /// <param name="value">数据库存的字符串值。</param>
    /// <param name="targetType">目标 CLR 类型。</param>
    /// <returns>反序列化后的对象；value 为 null 或空时返回 default。</returns>
    /// <exception cref="InvalidOperationException">不支持的类型或解析失败时抛出。</exception>
    private static object? DeserializeValue(string? value, Type targetType)
    {
        // null 或空字符串返回 default——可空值类型会得到 null，非可空得到 default(T)。
        if (string.IsNullOrEmpty(value))
        {
            // 可空值类型和引用类型直接返回 null。
            if (Nullable.GetUnderlyingType(targetType) is not null || !targetType.IsValueType)
            {
                // 返回 null。
                return null;
            }

            // 非可空值类型返回 Activator.CreateInstance 的 default。
            return Activator.CreateInstance(targetType);
        }

        // string 类型直接返回原值。
        if (targetType == typeof(string))
        {
            // 返回原字符串。
            return value;
        }

        // Guid 解析。
        if (targetType == typeof(Guid))
        {
            // Parse 成功返回 Guid，失败抛 FormatException。
            return Guid.Parse(value);
        }

        // DateTime 解析——ISO 8601 O 格式，InvariantCulture。
        if (targetType == typeof(DateTime))
        {
            // DateTimeStyles.RoundtripKind 匹配 O 格式。
            return DateTime.ParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
        }

        // DateTimeOffset 同理。
        if (targetType == typeof(DateTimeOffset))
        {
            return DateTimeOffset.ParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind);
        }

        // 整数类型——TryParse 比 Convert 更安全。
        if (targetType == typeof(int))
        {
            // InvariantCulture 防止区域性数字格式差异。
            return int.Parse(value, CultureInfo.InvariantCulture);
        }

        if (targetType == typeof(long))
        {
            // long 解析。
            return long.Parse(value, CultureInfo.InvariantCulture);
        }

        // bool 解析——接受 "True"/"False"/"true"/"false"。
        if (targetType == typeof(bool))
        {
            // bool.Parse 内部已处理大小写不敏感。
            return bool.Parse(value);
        }

        // 兜底：用 Convert.ChangeType——覆盖 decimal、float、double、byte 等。
        try
        {
            // InvariantCulture 确保数字格式统一。
            return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            // 捕获解析异常，包装为更清晰的 InvalidOperationException。
            throw new InvalidOperationException(
                $"无法将值 '{value}' 反序列化为类型 '{targetType.Name}'。",
                ex);
        }
    }
}
