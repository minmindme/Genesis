// ANCHOR: dal.engine.snapshot-codec - 快照编解码器："直接煮熟"的整对象存储
using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Emoking.DAL;

/// <summary>
/// 快照编解码器——把完整对象一次性序列化进实体行的 Value（"米饭直接煮熟"）。
/// </summary>
/// <remarks>
/// 与拆解图模式的分工：
///   - 快照模式：1 行搞定（Role=2 + SNAP1:JSON），点查零拆解零组装，schema 即类定义；
///   - 图模式：实体行 + 属性行，保留图遍历与属性级查询。
/// 载荷前缀 SNAP1: 与加密层（AGC1:/PLN1:）正交嵌套——存储层标记在内、加密层标记在外。
/// 序列化用 BCL 自带 System.Text.Json，零第三方依赖；MemoryPack/MessagePack 二进制形态列为旁支。
/// </remarks>
public static class SnapshotCodec
{
    // 快照载荷前缀。
    public const string Prefix = "SNAP1:";

    /// <summary>
    /// 把任意业务对象（强类型实例 / UniversalObject / 字典）煮成快照行。
    /// </summary>
    /// <param name="payload">业务对象。</param>
    /// <param name="entityTypeId">实体类型的 Meta TypeId。</param>
    /// <returns>单行同构数据——实体行，Value 为 SNAP1:JSON。</returns>
    /// <exception cref="ArgumentNullException">payload 为 null 时抛出。</exception>
    public static UniversalEntity Decompose(object payload, Guid entityTypeId)
    {
        // payload null 检查。
        if (payload is null)
        {
            // 抛 ArgumentNullException。
            throw new ArgumentNullException(nameof(payload));
        }

        // 序列化源统一为 object——强类型实例直接序列化（保留完整类型信息），
        // 万物容器/字典序列化其属性视图。
        object source = payload switch
        {
            // 万物容器 → 属性字典视图。
            UniversalObject universalObject => universalObject.GetProperties(),

            // 其他对象原样（含强类型实例与字典）。
            _ => payload
        };

        // 整对象序列化。
        var json = JsonSerializer.Serialize(source);

        // 生成快照行——实体行，Value 为 SNAP1:JSON。
        return new UniversalEntity(
            Id: Guid.NewGuid(),
            TypeId: entityTypeId,
            Role: UniversalEntity.RoleData,
            ParentId: null,
            Value: Prefix + json);
    }

    /// <summary>
    /// 把快照行还原为强类型实例——有 CSharpType 指令时的零损耗路径。
    /// </summary>
    /// <typeparam name="T">目标业务类型。</typeparam>
    /// <param name="rows">子图行集合（取实体行快照）。</param>
    /// <param name="entityTypeId">实体类型的 Meta TypeId。</param>
    /// <returns>反序列化完成的强类型实例。</returns>
    /// <exception cref="InvalidOperationException">实体行缺失时抛出。</exception>
    public static T Assemble<T>(IReadOnlyList<UniversalEntity> rows, Guid entityTypeId)
    {
        // 取实体行。
        var entityRow = FindEntityRow(rows, entityTypeId);

        // 反序列化。
        var instance = JsonSerializer.Deserialize<T>(entityRow.Value![Prefix.Length..])!;

        // 若实例有 Guid Id 属性则回填实体行 Id（快照 JSON 里不含 Id，以行主键为准）。
        var idProperty = typeof(T).GetProperty("Id");

        // 属性可写且类型匹配时回填。
        if (idProperty is not null && idProperty.CanWrite && idProperty.PropertyType == typeof(Guid))
        {
            // 回填。
            idProperty.SetValue(instance, entityRow.Id);
        }

        // 返回实例。
        return instance;
    }

    /// <summary>
    /// 把快照行还原为万物容器——无 CSharpType 指令时的动态路径。
    /// </summary>
    /// <param name="rows">子图行集合。</param>
    /// <param name="entityTypeId">实体类型的 Meta TypeId。</param>
    /// <param name="typeName">类型名（来自元数据）。</param>
    /// <returns>组装完成的 UniversalObject。</returns>
    public static UniversalObject AssembleDynamic(
        IReadOnlyList<UniversalEntity> rows,
        Guid entityTypeId,
        string typeName)
    {
        // 取实体行。
        var entityRow = FindEntityRow(rows, entityTypeId);

        // 解析 JSON 文档。
        using var document = JsonDocument.Parse(entityRow.Value![Prefix.Length..]);
        var root = document.RootElement;

        // 构造万物容器。
        var instance = new UniversalObject(typeName);

        // 回填实体行 Id。
        instance.Id = entityRow.Id;

        // 逐属性提取——JsonElement 经类型推断还原为标量。
        foreach (var property in root.EnumerateObject())
        {
            // JsonElement → 推断对象后挂载。
            instance.SetProperty(property.Name, InferFromJson(property.Value));
        }

        // 返回容器。
        return instance;
    }

    /// <summary>
    /// 在子图中定位快照实体行。
    /// </summary>
    /// <param name="rows">子图行集合。</param>
    /// <param name="entityTypeId">实体类型 TypeId。</param>
    /// <returns>实体行。</returns>
    private static UniversalEntity FindEntityRow(IReadOnlyList<UniversalEntity> rows, Guid entityTypeId)
    {
        // 遍历查找。
        foreach (var row in rows)
        {
            // 快照实体行：TypeId 匹配、Role=Data、无父指针、Value 带 SNAP1 前缀。
            if (row.TypeId == entityTypeId
                && row.Role == UniversalEntity.RoleData
                && row.ParentId is null
                && row.Value is not null
                && row.Value.StartsWith(Prefix, StringComparison.Ordinal))
            {
                // 命中。
                return row;
            }
        }

        // 未命中——当前指令为 Snapshot 但找不到快照行（可能是图模式存量数据）。
        // 语义（第 12 轮定义）：读取以 StorageMode 指令为准，数据不迁移，mismatch 显式失败。
        throw new StorageModeMismatchException(
            $"StorageMode=Snapshot 指令下未找到 TypeId = {entityTypeId} 的快照实体行——" +
            "该类型的存量数据可能是图模式（实体行 + 属性行）。" +
            "请先迁移数据或将 StorageMode 指令改回 Graph。");
    }

    /// <summary>
    /// JsonElement → 推断对象——动态轨快照还原的值转换。
    /// </summary>
    /// <param name="element">JSON 值。</param>
    /// <returns>推断后的对象。</returns>
    private static object? InferFromJson(JsonElement element)
    {
        // 按值种类分发。
        return element.ValueKind switch
        {
            // 字符串经标量推断（还原 DateTime/bool/Guid 等）。
            JsonValueKind.String => DynamicAssembler.InferValue(element.GetString()),

            // 数字统一为 decimal（动态轨无 schema，保守选择）。
            JsonValueKind.Number => element.TryGetInt64(out var longValue) && longValue <= int.MaxValue && longValue >= int.MinValue
                ? (object)(int)longValue
                : element.GetDecimal(),

            // 布尔。
            JsonValueKind.True => true,

            // 布尔。
            JsonValueKind.False => false,

            // null。
            JsonValueKind.Null => null,

            // 嵌套结构原样字符串保留——嵌套对象还原列为旁支。
            _ => element.GetRawText()
        };
    }
}
