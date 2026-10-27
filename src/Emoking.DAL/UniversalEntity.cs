// ANCHOR: dal.model.universal-entity - 同构单表行级模型
using System;

namespace Emoking.DAL;

/// <summary>
/// 同构单表行级模型，对应 dbo.UniversalEntity 的一行。
/// 所有业务实体、属性值、关系边、引擎指令全部拍扁成此表的行。
/// </summary>
/// <remarks>
/// Role 枚举值：
///   1 = Meta   元数据定义（实体类型、字段定义、路由规则）
///   2 = Data   业务数据实例（实体实例 + 属性值）
///   3 = Edge   关系边（图指针，连接 Node 到 Node）
///   4 = Engine 引擎指令（路由协议、安全指令）
/// </remarks>
public sealed record UniversalEntity(
    Guid Id,
    Guid TypeId,
    byte Role,
    Guid? ParentId,
    string? Value,
    int Version = 1,
    bool IsDeleted = false,
    DateTime CreatedAt = default,
    DateTime UpdatedAt = default)
{
    /// <summary>
    /// 角色常量：Meta 元数据定义。
    /// </summary>
    public const byte RoleMeta = 1;

    /// <summary>
    /// 角色常量：Data 业务数据实例。
    /// </summary>
    public const byte RoleData = 2;

    /// <summary>
    /// 角色常量：Edge 关系边。
    /// </summary>
    public const byte RoleEdge = 3;

    /// <summary>
    /// 角色常量：Engine 引擎指令。
    /// </summary>
    public const byte RoleEngine = 4;

    /// <summary>
    /// 角色常量：Security 安全策略（加密、租户隔离、限流等规则数据）。
    /// </summary>
    public const byte RoleSecurity = 5;

    /// <summary>
    /// 角色常量：Command CLI 命令与工作流定义（命令树即数据）。
    /// </summary>
    public const byte RoleCommand = 7;
}
