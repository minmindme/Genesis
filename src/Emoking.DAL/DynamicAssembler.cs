// ANCHOR: dal.engine.dynamic-assembler - 动态轨组装器：同构行 → Dictionary（无需 C# 实体类）
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Emoking.DAL;

/// <summary>
/// 动态轨组装器——把同构行反向挂载为 <see cref="Dictionary{TKey,TValue}"/>，
/// **不要求存在任何 C# 实体类**。属性集完全由池中 Role=1 元数据决定。
/// </summary>
/// <remarks>
/// 类型策略（诚实边界）：动态轨不知道每个属性的目标 CLR 类型（元数据原型期未存 DataType），
/// 采用"按值推断"——可无损解析为 int/long/decimal/bool/DateTime/Guid 时返回强类型，否则保留原字符串。
/// 严格类型还原应走强类型轨或未来的元数据 DataType 指令（列为旁支）。
/// </remarks>
public sealed class DynamicAssembler
{
    // 元数据缓存——属性 TypeId → 属性名的唯一权威来源。
    private readonly MetaCache _metaCache;

    /// <summary>
    /// 注入元数据缓存初始化动态组装器。
    /// </summary>
    /// <param name="metaCache">已加载的元数据缓存。</param>
    public DynamicAssembler(MetaCache metaCache)
    {
        // 保存缓存引用。
        _metaCache = metaCache;
    }

    /// <summary>
    /// 将同构行集合反向组装为万物容器——属性集由元数据决定，无需 C# 实体类。
    /// </summary>
    /// <param name="rows">包含实体行 + 属性行的子图。</param>
    /// <param name="entityTypeId">实体类型的 Meta TypeId。</param>
    /// <returns>组装完成的 <see cref="UniversalObject"/>——支持 dynamic 点号访问。</returns>
    /// <exception cref="ArgumentNullException">rows 为 null 时抛出。</exception>
    /// <exception cref="InvalidOperationException">实体行缺失或类型元数据未注册时抛出。</exception>
    public UniversalObject Assemble(
        IReadOnlyList<UniversalEntity> rows,
        Guid entityTypeId)
    {
        // rows null 检查。
        if (rows is null)
        {
            // 抛 ArgumentNullException。
            throw new ArgumentNullException(nameof(rows));
        }

        // 在子图中定位实体行：TypeId 匹配、Role=Data、无父指针。
        UniversalEntity? entityRow = null;

        // 遍历查找。
        foreach (var row in rows)
        {
            // 三个条件同时成立才是实体行。
            if (row.TypeId == entityTypeId && row.Role == UniversalEntity.RoleData && row.ParentId is null)
            {
                // 命中，跳出。
                entityRow = row;
                break;
            }
        }

        // 实体行缺失时显式失败。
        if (entityRow is null)
        {
            // 抛出带行数的异常。
            throw new InvalidOperationException(
                $"在给定的 {rows.Count} 行中未找到 TypeId = {entityTypeId} 的实体实例行。");
        }

        // 快照行检测——StorageMode 切到 Graph 但存量数据仍是 SNAP1 快照行时显式失败。
        if (entityRow.Value is not null && entityRow.Value.StartsWith("SNAP1:", StringComparison.Ordinal))
        {
            // 抛出模式不匹配异常。
            throw new StorageModeMismatchException(
                $"TypeId = {entityTypeId} 的实体行是 SNAP1 快照形态，但当前按图模式（Graph）动态组装——" +
                "请将 StorageMode 指令改回 Snapshot 或先迁移数据。");
        }

        // 取实体类型的元数据。
        var metaType = _metaCache.GetTypeByTypeId(entityTypeId);

        // 元数据未注册时显式失败——动态轨不猜结构。
        if (metaType is null)
        {
            // 抛出异常。
            throw new InvalidOperationException(
                $"未找到 TypeId = {entityTypeId} 的实体元数据，无法确定属性集。");
        }

        // 凭空构建万物容器——这里没有任何 C# 实体类参与。
        var instance = new UniversalObject(metaType.TypeName);

        // 回填实体行 Id。
        instance.Id = entityRow.Id;

        // 按元数据的属性定义逐一取值——子图中多余的行被忽略。
        foreach (var pair in metaType.PropertyTypeIds)
        {
            // 找该属性的值行：ParentId 指向实体行且 TypeId 匹配。
            var valueRow = rows.FirstOrDefault(row =>
                row.ParentId == entityRow.Id
                && row.TypeId == pair.Value
                && row.Role == UniversalEntity.RoleData);

            // 值行按值推断类型后挂载——缺行时属性值为 null（元数据声明了但实例没写）。
            instance.SetProperty(pair.Key, InferValue(valueRow?.Value));
        }

        // 返回万物容器。
        return instance;
    }

    /// <summary>
    /// 按值推断属性类型——可无损解析为常见标量类型时返回强类型，否则保留原字符串。
    /// </summary>
    /// <param name="value">数据库存的字符串值（可为 null）。</param>
    /// <returns>推断后的对象；null 输入返回 null。</returns>
    internal static object? InferValue(string? value)
    {
        // null 直通。
        if (value is null)
        {
            // 返回 null。
            return null;
        }

        // int 推断——InvariantCulture 防区域性差异。
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
        {
            // 返回 int。
            return intValue;
        }

        // long 推断——覆盖超出 int 范围的整数。
        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
        {
            // 返回 long。
            return longValue;
        }

        // decimal 推断——覆盖金额等小数。
        if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var decimalValue))
        {
            // 返回 decimal。
            return decimalValue;
        }

        // bool 推断——true/false 大小写不敏感。
        if (bool.TryParse(value, out var boolValue))
        {
            // 返回 bool。
            return boolValue;
        }

        // DateTime 推断——只接受文化无关的 ISO 8601 形态：先精确匹配 "O" 格式，
        // 再回退到 InvariantCulture 的通用 ISO 解析（覆盖 JSON 序列化的省略小数秒形态）。
        if (DateTime.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var dateValue))
        {
            // 返回 DateTime。
            return dateValue;
        }

        // ISO 8601 回退解析——JsonSerializer 默认输出即此形态，文化无关。
        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var isoDateValue))
        {
            // 返回 DateTime。
            return isoDateValue;
        }

        // Guid 推断——标准 "D" 格式。
        if (Guid.TryParse(value, out var guidValue))
        {
            // 返回 Guid。
            return guidValue;
        }

        // 无法推断时保留原字符串——诚实降级，不硬猜。
        return value;
    }
}
