// ANCHOR: test.infra.bus-helpers - 总线动作返回值断言助手（第 25 轮统一信封适配）
using System;
using System.Collections.Generic;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 总线动作返回值断言助手——第 25 轮统一信封（{Id, TypeName, Version, Data}）后，
/// 测试从信封中提取实体行 Id 的统一入口。
/// </summary>
public static class TestBusHelpers
{
    /// <summary>
    /// 从动作返回的信封中提取实体行 Id。
    /// </summary>
    /// <param name="envelope">动作返回的信封字典。</param>
    /// <returns>实体行 Id。</returns>
    public static Guid ExtractId(object? envelope)
    {
        // 信封形态断言。
        var dict = Assert.IsType<Dictionary<string, object?>>(envelope);

        // 提取 Id。
        return Assert.IsType<Guid>(dict["Id"]);
    }
}
