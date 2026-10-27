// ANCHOR: dal.engine.universal-gateway - 双轨路由门面：类名同化的统一入口
using System;
using System.Collections.Generic;
using System.Reflection;

namespace Emoking.DAL;

/// <summary>
/// 万物网关——组装与拆解的统一入口，按池中 CSharpType 指令自动路由双轨：
/// 有 CSharpType 指令 → 强类型反射轨（真实 C# 实例）；
/// 无 CSharpType 指令 → 动态轨（<see cref="UniversalObject"/>，凭空长出的类）。
/// </summary>
/// <remarks>
/// 路由决策完全来自数据池——改一行指令数据即可让实体在"强类型"与"动态"之间切换，零代码变更。
/// </remarks>
public sealed class UniversalEntityGateway
{
    // 元数据缓存——CSharpType 指令与属性定义的来源。
    private readonly MetaCache _metaCache;

    // 动态轨拆解器。
    private readonly DynamicDecomposer _dynamicDecomposer;

    // 动态轨组装器。
    private readonly DynamicAssembler _dynamicAssembler;

    // 强类型轨拆解器——反射公共属性。
    private readonly NodeEdgeDecomposer _strongDecomposer;

    /// <summary>
    /// 注入元数据缓存初始化网关。
    /// </summary>
    /// <param name="metaCache">已加载的元数据缓存。</param>
    public UniversalEntityGateway(MetaCache metaCache)
    {
        // 保存缓存引用。
        _metaCache = metaCache;

        // 构造双轨组件——共享同一份元数据。
        _dynamicDecomposer = new DynamicDecomposer(metaCache);
        _dynamicAssembler = new DynamicAssembler(metaCache);
        _strongDecomposer = new NodeEdgeDecomposer(metaCache);
    }

    /// <summary>
    /// 统一拆解入口——把任意形态的业务数据拆为同构行。
    /// </summary>
    /// <remarks>
    /// 三轨路由（全部由池中 Role=4 指令数据决定）：
    ///   1. StorageMode=Snapshot → 快照轨（整对象煮进一行）；
    ///   2. CSharpType 指令 → 强类型反射轨；
    ///   3. 默认 → 动态轨（UniversalObject / 字典）。
    /// </remarks>
    /// <param name="payload">业务数据：UniversalObject / 字典 / 任意 C# 实例。</param>
    /// <param name="entityTypeId">实体类型的 Meta TypeId。</param>
    /// <returns>拆解后的同构行集合。</returns>
    public IReadOnlyList<UniversalEntity> Decompose(object payload, Guid entityTypeId)
    {
        // payload null 检查。
        if (payload is null)
        {
            // 抛 ArgumentNullException。
            throw new ArgumentNullException(nameof(payload));
        }

        // 分支零：快照模式——整对象煮进一行。
        if (_metaCache.GetStorageMode(entityTypeId) == "Snapshot")
        {
            // 快照轨。
            return new[] { SnapshotCodec.Decompose(payload, entityTypeId) };
        }

        // 查池中是否绑定了 CSharpType。
        var csharpTypeName = _metaCache.GetCSharpTypeName(entityTypeId);

        // 分支一：万物容器数据面。
        if (payload is UniversalObject universalObject)
        {
            // 有类名指令——动态数据反射装进真实 C# 实例后走强类型轨，保证行结构与强类型轨完全一致。
            if (csharpTypeName is not null)
            {
                // 物化为强类型实例。
                var instance = MaterializeStrongType(csharpTypeName, universalObject.GetProperties());

                // 强类型轨拆解。
                return _strongDecomposer.Decompose(instance, entityTypeId);
            }

            // 无类名指令——纯动态轨。
            return _dynamicDecomposer.Decompose(universalObject, entityTypeId);
        }

        // 分支二：字典数据面。
        if (payload is IReadOnlyDictionary<string, object?> payloadDictionary)
        {
            // 有类名指令——同样先物化为强类型实例。
            if (csharpTypeName is not null)
            {
                // 物化。
                var instance = MaterializeStrongType(csharpTypeName, payloadDictionary);

                // 强类型轨拆解。
                return _strongDecomposer.Decompose(instance, entityTypeId);
            }

            // 无类名指令——动态轨。
            return _dynamicDecomposer.Decompose(payloadDictionary, entityTypeId);
        }

        // 分支三：其余任意 C# 实例——强类型轨。
        return _strongDecomposer.Decompose(payload, entityTypeId);
    }

    /// <summary>
    /// 统一组装入口——把子图组装为强类型实例、万物容器或快照对象。
    /// </summary>
    /// <param name="rows">子图行集合。</param>
    /// <param name="entityTypeId">实体类型的 Meta TypeId。</param>
    /// <returns>按池中指令路由的组装结果：快照对象 / 强类型实例 / 万物容器。</returns>
    public object Assemble(IReadOnlyList<UniversalEntity> rows, Guid entityTypeId)
    {
        // 分支零：快照模式——优先检查（快照行与图模式行共存时按模式分派）。
        if (_metaCache.GetStorageMode(entityTypeId) == "Snapshot")
        {
            // 查 CSharpType——有则走零损耗强类型反序列化。
            var snapshotTypeName = _metaCache.GetCSharpTypeName(entityTypeId);

            // 有类名指令 → 强类型快照。
            if (snapshotTypeName is not null)
            {
                // 解析类型。
                var snapshotClrType = ResolveClrType(snapshotTypeName);

                // 通过泛型方法反射调用 Assemble<T>——快照 JSON 保留完整类型信息。
                var assembleMethod = typeof(SnapshotCodec)
                    .GetMethod(nameof(SnapshotCodec.Assemble))!
                    .MakeGenericMethod(snapshotClrType);

                // 调用并返回。
                return assembleMethod.Invoke(null, new object[] { rows, entityTypeId })!;
            }

            // 无类名指令 → 动态快照。
            var metaForSnapshot = _metaCache.GetTypeByTypeId(entityTypeId);

            // 类型元数据缺失时用占位名。
            return SnapshotCodec.AssembleDynamic(
                rows,
                entityTypeId,
                metaForSnapshot?.TypeName ?? "Unknown");
        }

        // 查池中是否绑定了 CSharpType（图模式）。
        var csharpTypeName = _metaCache.GetCSharpTypeName(entityTypeId);

        // 无类名指令——动态轨组装为万物容器。
        if (csharpTypeName is null)
        {
            // 动态轨。
            return _dynamicAssembler.Assemble(rows, entityTypeId);
        }

        // 解析 C# 类型——先 Type.GetType，失败时扫描已加载程序集按 FullName 匹配。
        var clrType = ResolveClrType(csharpTypeName);

        // 反射构造强类型实例。
        var instance = Activator.CreateInstance(clrType)!;

        // 取元数据属性集。
        var metaType = _metaCache.GetTypeByTypeId(entityTypeId);

        // 元数据必然存在（动态组装前置校验），仍防御一次。
        if (metaType is null)
        {
            // 抛出异常。
            throw new InvalidOperationException(
                $"未找到 TypeId = {entityTypeId} 的实体元数据。");
        }

        // 逐属性反射赋值——值先经动态轨推断，再 ChangeType 对齐属性类型。
        foreach (var pair in metaType.PropertyTypeIds)
        {
            // 反射取目标属性。
            var property = clrType.GetProperty(pair.Key, BindingFlags.Public | BindingFlags.Instance);

            // 属性不存在或不可写时跳过——类与元数据不一致由调用方修复。
            if (property is null || !property.CanWrite)
            {
                // 跳过。
                continue;
            }

            // 在子图中找该属性的值行。
            UniversalEntity? valueRow = null;

            // 先找实体行 Id。
            foreach (var row in rows)
            {
                // 命中实体行。
                if (row.TypeId == entityTypeId && row.Role == UniversalEntity.RoleData && row.ParentId is null)
                {
                    // 记录实体行 Id。
                    var entityRowId = row.Id;

                    // 再扫属性行。
                    foreach (var candidate in rows)
                    {
                        // 匹配属性行。
                        if (candidate.ParentId == entityRowId && candidate.TypeId == pair.Value && candidate.Role == UniversalEntity.RoleData)
                        {
                            // 记录值行。
                            valueRow = candidate;
                            break;
                        }
                    }

                    // 找到实体行后结束外层定位。
                    break;
                }
            }

            // 值行缺失时跳过赋值。
            if (valueRow is null)
            {
                // 跳过。
                continue;
            }

            // 推断值类型。
            var inferred = DynamicAssembler.InferValue(valueRow.Value);

            // 推断值与属性类型一致时直接赋值，否则 ChangeType 对齐。
            object? converted = inferred is null || property.PropertyType.IsInstanceOfType(inferred)
                ? inferred
                : Convert.ChangeType(inferred, property.PropertyType, System.Globalization.CultureInfo.InvariantCulture);

            // 反射赋值。
            property.SetValue(instance, converted);
        }

        // 回填 Id 属性（若存在）。
        var idProperty = clrType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance);

        // Id 属性存在且为 Guid 时回填。
        if (idProperty is not null && idProperty.CanWrite && idProperty.PropertyType == typeof(Guid))
        {
            // 找实体行 Id。
            foreach (var row in rows)
            {
                // 命中实体行。
                if (row.TypeId == entityTypeId && row.Role == UniversalEntity.RoleData && row.ParentId is null)
                {
                    // 回填。
                    idProperty.SetValue(instance, row.Id);

                    // 结束。
                    break;
                }
            }
        }

        // 返回强类型实例。
        return instance;
    }

    /// <summary>
    /// 把动态数据面的属性反射装进真实 C# 实例——动态数据与强类型轨的桥。
    /// </summary>
    /// <param name="csharpTypeName">C# 类型全名。</param>
    /// <param name="properties">动态属性字典。</param>
    /// <returns>填充完成的强类型实例。</returns>
    private object MaterializeStrongType(string csharpTypeName, IReadOnlyDictionary<string, object?> properties)
    {
        // 解析类型。
        var clrType = ResolveClrType(csharpTypeName);

        // 反射构造。
        var instance = Activator.CreateInstance(clrType)!;

        // 逐属性填充。
        foreach (var pair in properties)
        {
            // 反射取目标属性。
            var property = clrType.GetProperty(pair.Key, BindingFlags.Public | BindingFlags.Instance);

            // 属性缺失或不可写跳过。
            if (property is null || !property.CanWrite)
            {
                // 跳过。
                continue;
            }

            // 推断后对齐类型再赋值——动态值先按强类型轨同一序列化规则归一为字符串再推断。
            var inferred = DynamicAssembler.InferValue(NodeEdgeDecomposer.SerializeValue(pair.Value));
            object? converted = inferred is null || property.PropertyType.IsInstanceOfType(inferred)
                ? inferred
                : Convert.ChangeType(inferred, property.PropertyType, System.Globalization.CultureInfo.InvariantCulture);

            // 赋值。
            property.SetValue(instance, converted);
        }

        // 返回实例。
        return instance;
    }

    /// <summary>
    /// 按类全名解析 CLR 类型——先标准解析，失败时扫描已加载程序集。
    /// </summary>
    /// <param name="csharpTypeName">类型全名。</param>
    /// <returns>解析出的 Type。</returns>
    /// <exception cref="InvalidOperationException">程序集中找不到该类时抛出。</exception>
    private static Type ResolveClrType(string csharpTypeName)
    {
        // 先按标准方式解析（含程序集限定名场景）。
        var clrType = Type.GetType(csharpTypeName);

        // 命中直接返回。
        if (clrType is not null)
        {
            // 返回。
            return clrType;
        }

        // 扫描已加载程序集按 FullName 匹配——支持非限定名。
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            // 程序集内查找。
            var found = assembly.GetType(csharpTypeName);

            // 命中返回。
            if (found is not null)
            {
                // 返回。
                return found;
            }
        }

        // 全部未命中——指令指定了类名但程序集中不存在。
        throw new InvalidOperationException(
            $"CSharpType 指令指定的类 '{csharpTypeName}' 在已加载程序集中不存在。");
    }
}
