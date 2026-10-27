// ANCHOR: dal.engine.universal-object - 万物容器：运行期凭空长出的"类"
using System;
using System.Collections.Generic;
using System.Dynamic;

namespace Emoking.DAL;

/// <summary>
/// 万物容器——继承 <see cref="DynamicObject"/>，在没有 C# 实体类的前提下
/// 提供点号访问（dynamic 语法）的动态实体对象。
/// </summary>
/// <remarks>
/// "类名同化"的运行时形态：类不再必须存在于程序集中，
/// 池中没有 CSharpType 指令的实体一律实例化为本容器，属性集与元数据对齐。
/// </remarks>
public sealed class UniversalObject : DynamicObject
{
    // 属性字典——引擎组装/拆解的真正存储层。
    private readonly Dictionary<string, object?> _properties;

    /// <summary>
    /// 实体行 Id——组装时由引擎回填（对应同构池中的实体行主键）。
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// 实体类型名——来自池中 Role=1 元数据。
    /// </summary>
    public string TypeName { get; }

    /// <summary>
    /// 初始化万物容器。
    /// </summary>
    /// <param name="typeName">实体类型名（如 "Order"）。</param>
    public UniversalObject(string typeName)
    {
        // 类型名必填。
        TypeName = typeName ?? throw new ArgumentNullException(nameof(typeName));

        // 属性字典按序数比较——属性名大小写敏感，与元数据一致。
        _properties = new Dictionary<string, object?>(StringComparer.Ordinal);
    }

    /// <summary>
    /// 动态取成员——支撑 dynamic 语法点号读。
    /// </summary>
    /// <param name="binder">成员绑定器。</param>
    /// <param name="result">属性值。</param>
    /// <returns>属性存在返回 true。</returns>
    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        // 委托字典查找。
        return _properties.TryGetValue(binder.Name, out result);
    }

    /// <summary>
    /// 动态写成员——支撑 dynamic 语法点号写。
    /// </summary>
    /// <param name="binder">成员绑定器。</param>
    /// <param name="value">属性值。</param>
    /// <returns>恒返回 true——万物容器接受任意属性。</returns>
    public override bool TrySetMember(SetMemberBinder binder, object? value)
    {
        // 直接入字典。
        _properties[binder.Name] = value;

        // 接受写入。
        return true;
    }

    /// <summary>
    /// 引擎用属性写入入口——绕过 dynamic 绑定，直接设置。
    /// </summary>
    /// <param name="name">属性名。</param>
    /// <param name="value">属性值。</param>
    public void SetProperty(string name, object? value)
    {
        // 直接入字典。
        _properties[name] = value;
    }

    /// <summary>
    /// 引擎用属性读取入口。
    /// </summary>
    /// <param name="name">属性名。</param>
    /// <returns>属性值；不存在返回 null。</returns>
    public object? GetProperty(string name)
    {
        // 委托字典查找。
        return _properties.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>
    /// 全量导出属性——供动态拆解器消费。
    /// </summary>
    /// <returns>属性只读视图。</returns>
    public IReadOnlyDictionary<string, object?> GetProperties()
    {
        // 返回字典本身（内部约定不外部修改）。
        return _properties;
    }
}
