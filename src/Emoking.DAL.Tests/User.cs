// ANCHOR: test.user - 原型期测试用 User 实体（5 字段）
using System;

namespace Emoking.DAL.Tests;

/// <summary>
/// 原型期用的最简单业务实体——5 个字段，覆盖拆解与反向挂载。
/// </summary>
public sealed class User
{
    /// <summary>用户 Id——拆解时作为实体行 Id。</summary>
    public Guid Id { get; set; }

    /// <summary>用户姓名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>邮箱。</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>年龄。</summary>
    public int Age { get; set; }

    /// <summary>创建时间。</summary>
    public DateTime CreatedAt { get; set; }
}
