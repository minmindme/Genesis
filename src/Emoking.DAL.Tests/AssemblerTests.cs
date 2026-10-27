// ANCHOR: test.assembler - ObjectAssembler 反向挂载单元测试
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 对象组装引擎的单元测试集——验证 UniversalEntity 行集合 → User 实例的反向挂载。
/// </summary>
public sealed class AssemblerTests
{
    // User 实体的 Meta TypeId。
    private static readonly Guid UserTypeId = new("11111111-1111-1111-1111-111111111111");

    /// <summary>
    /// 构造测试依赖——与 Decomposer 共享 MetaCache 初始化。
    /// </summary>
    private static async Task<(MetaCache cache, ObjectAssembler assembler)> BuildSutAsync()
    {
        // DbContext + Repository。
        var dbContext = new EmokingDbContext();
        var repo = new UniversalEntityRepository(dbContext);

        // MetaCache + LoadAsync。
        var cache = new MetaCache(repo);
        await cache.LoadAsync();

        // 构造 Assembler。
        var assembler = new ObjectAssembler(cache);

        // 返回依赖对。
        return (cache, assembler);
    }

    /// <summary>
    /// 用最小手工行集合测试——只给实体行 + 一个属性行，验证基础挂载。
    /// </summary>
    [Fact]
    public async Task Assemble_ManualRows_ShouldMapProperty()
    {
        // 第 1 步：构造被测对象。
        var (cache, assembler) = await BuildSutAsync();

        // 第 2 步：准备行集合——实体行 Id 固定为 entityRowId。
        var entityRowId = Guid.NewGuid();

        // 拿 User MetaType 知道属性 TypeId。
        var userMeta = cache.GetTypeByTypeId(UserTypeId)!;
        var nameTypeId = userMeta.PropertyTypeIds["Name"];

        // 构造 2 行：实体行 + Name 属性行。
        var rows = new List<UniversalEntity>
        {
            // 实体行。
            new UniversalEntity(
                Id: entityRowId,
                TypeId: UserTypeId,
                Role: UniversalEntity.RoleData,
                ParentId: null,
                Value: null),

            // Name 属性值行。
            new UniversalEntity(
                Id: Guid.NewGuid(),
                TypeId: nameTypeId,
                Role: UniversalEntity.RoleData,
                ParentId: entityRowId,
                Value: "李四"),
        };

        // 第 3 步：执行组装。
        var user = assembler.Assemble<User>(rows, UserTypeId);

        // 第 4 步：断言——实体 Id 被正确赋值。
        Assert.Equal(entityRowId, user.Id);

        // Name 属性被正确反序列化。
        Assert.Equal("李四", user.Name);
    }

    /// <summary>
    /// 实体行缺失时应该抛 InvalidOperationException。
    /// </summary>
    [Fact]
    public async Task Assemble_NoEntityRow_ShouldThrow()
    {
        // 第 1 步：构造被测对象。
        var (cache, assembler) = await BuildSutAsync();

        // 第 2 步：只给属性行，不给实体行。
        var rows = new List<UniversalEntity>
        {
            new UniversalEntity(
                Id: Guid.NewGuid(),
                TypeId: Guid.NewGuid(), // 随便一个 TypeId，不会匹配 UserTypeId
                Role: UniversalEntity.RoleData,
                ParentId: null,
                Value: null),
        };

        // 第 3 步：执行组装——期望抛异常。
        var ex = Assert.Throws<InvalidOperationException>(
            () => assembler.Assemble<User>(rows, UserTypeId));

        // 第 4 步：断言消息包含期望的 TypeId。
        Assert.Contains(UserTypeId.ToString(), ex.Message);
    }
}
