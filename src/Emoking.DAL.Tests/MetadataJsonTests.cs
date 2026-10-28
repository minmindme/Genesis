// ANCHOR: test.metadata-json - 元数据 DSL 双格式验收：JSON: 前缀版本化（B25/B17 类分隔符裂缝根治）
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 元数据 DSL 双格式验收（第 22 轮）——值/键含 <c>|</c> <c>=</c> <c>:</c> 等分隔符时不再静默半解析：
/// JSON: 前缀（与 AGC1:/PLN1:/SNAP1: 同一版本化机制）承载，旧格式零破坏兼容。
/// </summary>
public sealed class MetadataJsonTests : IAsyncLifetime
{
    // 测试实体类型 TypeId。
    private static readonly Guid DocTypeId = new("99999999-9999-9999-9999-999999999999");

    /// <summary>
    /// 类级初始化——为本测试类创建独立数据库（第 21 轮测试基建）。
    /// </summary>
    public async Task InitializeAsync()
    {
        // 建独立库并注册连接串。
        await TestDatabase.CreateAsync(GetType().Name);
    }

    /// <summary>
    /// 类级清理——删除本测试类的独立数据库。
    /// </summary>
    public async Task DisposeAsync()
    {
        // 删库。
        await TestDatabase.DropAsync();
    }

    /// <summary>
    /// 方法级清池——TRUNCATE 本类独立库的全表（同类内多测试方法共享该库，保留方法间隔离——B37 教训）。
    /// </summary>
    private static async Task TruncateTableAsync()
    {
        // 打开当前测试库连接（经连接串覆盖解析点路由）。
        using var connection = new EmokingDbContext().CreateConnection();

        // 构造清空命令。
        await using var command = connection.CreateCommand();

        // TRUNCATE 全表。
        command.CommandText = "TRUNCATE TABLE [dbo].[UniversalEntity]";

        // 执行。
        await command.ExecuteNonQueryAsync();
    }

    // ============================================================================
    // 单元：键值对编解码
    // ============================================================================

    /// <summary>
    /// 旧格式回归——"K=V|K2=V2" 解析不变（存量数据零破坏）。
    /// </summary>
    [Fact]
    public void DecodePairs_LegacyFormat_Unchanged()
    {
        // 旧格式解析。
        var result = MetadataJson.DecodePairs("RouteTarget=UserRepository|TargetTypeId=abc|StorageProvider=SqlServer");

        // 逐键断言。
        Assert.Equal("UserRepository", result["RouteTarget"]);
        Assert.Equal("abc", result["TargetTypeId"]);
        Assert.Equal("SqlServer", result["StorageProvider"]);
    }

    /// <summary>
    /// JSON 格式——值内含竖线与等号时无损解析（旧格式会在此半解析）。
    /// </summary>
    [Fact]
    public void DecodePairs_JsonFormat_PreservesSeparatorsInValues()
    {
        // JSON 格式解析——值含 | 与 =。
        var result = MetadataJson.DecodePairs("JSON:{\"RouteTarget\":\"X|Y\",\"Note\":\"a=b\"}");

        // 分隔符无损。
        Assert.Equal("X|Y", result["RouteTarget"]);
        Assert.Equal("a=b", result["Note"]);
    }

    /// <summary>
    /// JSON 格式数组值 join 为分号串——Role=7 Steps 与既有执行器消费方（Split(';')）零改动兼容。
    /// </summary>
    [Fact]
    public void DecodePairs_JsonArray_JoinsWithSemicolon()
    {
        // Steps 数组解析。
        var result = MetadataJson.DecodePairs("JSON:{\"Command\":\"CreateEntity\",\"Steps\":[\"InsertTypeMeta\",\"SetStorageMode\",\"ReloadCache\"]}");

        // 数组 → 分号串（执行器 Split(';') 直接可用）。
        Assert.Equal("InsertTypeMeta;SetStorageMode;ReloadCache", result["Steps"]);
        Assert.Equal("CreateEntity", result["Command"]);
    }

    /// <summary>
    /// 编码路由——含分隔符自动 JSON 化，纯值保持旧格式（池中可读性优先）。
    /// </summary>
    [Fact]
    public void EncodePairs_RoutesBySeparatorPresence()
    {
        // 纯值——旧格式。
        var plain = MetadataJson.EncodePairs(new Dictionary<string, string> { ["A"] = "B", ["C"] = "D" });

        // 旧格式直出。
        Assert.Equal("A=B|C=D", plain);

        // 含竖线值——JSON 化。
        var jsoned = MetadataJson.EncodePairs(new Dictionary<string, string> { ["Path"] = "a|b" });

        // JSON 前缀。
        Assert.StartsWith("JSON:", jsoned);

        // 往返无损。
        Assert.Equal("a|b", MetadataJson.DecodePairs(jsoned)["Path"]);
    }

    // ============================================================================
    // 单元：属性规格编解码
    // ============================================================================

    /// <summary>
    /// 属性规格双格式——旧 "Name:Type" 回归 + 含冒号属性名 JSON 化往返。
    /// </summary>
    [Fact]
    public void PropertySpec_RoundTrips_BothFormats()
    {
        // 旧格式——第 17 轮 "Name:Type"。
        var (legacyName, legacyType) = MetadataJson.DecodeProperty("Price:Decimal");
        Assert.Equal("Price", legacyName);
        Assert.Equal("Decimal", legacyType);

        // 旧格式——无冒号 = 未声明类型。
        var (bareName, bareType) = MetadataJson.DecodeProperty("Memo");
        Assert.Equal("Memo", bareName);
        Assert.Null(bareType);

        // 含冒号属性名——编码自动 JSON 化。
        var encoded = MetadataJson.EncodeProperty("Ratio:Base", "String");

        // JSON 前缀。
        Assert.StartsWith("JSON:", encoded);

        // 往返无损。
        var (decodedName, decodedType) = MetadataJson.DecodeProperty(encoded);
        Assert.Equal("Ratio:Base", decodedName);
        Assert.Equal("String", decodedType);
    }

    // ============================================================================
    // 集成：MetaCache 与命令执行器的双格式链路
    // ============================================================================

    /// <summary>
    /// 集成：JSON 格式指令行经 LoadAsync 后路由正确——含竖线的 RouteTarget 不再半解析。
    /// </summary>
    [Fact]
    public async Task Cache_JsonInstructionRow_RoutesCorrectly()
    {
        // 第 1 步：清池 + 落一行 JSON 格式 Role=4 指令（RouteTarget 含竖线——旧格式必然半解析）。
        await TruncateTableAsync();
        var repository = new UniversalEntityRepository(new EmokingDbContext());
        await repository.InsertBatchAsync(new[]
        {
            new UniversalEntity(
                Id: Guid.NewGuid(),
                TypeId: MetaCache.MetaEngineTypeId,
                Role: UniversalEntity.RoleEngine,
                ParentId: null,
                Value: $"JSON:{{\"RouteTarget\":\"Custom|Repo\",\"TargetTypeId\":\"{DocTypeId}\"}}")
        });

        // 第 2 步：自举加载。
        var cache = new MetaCache(repository);
        await cache.LoadAsync();

        // 第 3 步：路由值无损（旧实现会把 "Custom|Repo" 切成 RouteTarget=Custom + 未知段）。
        Assert.Equal("Custom|Repo", cache.GetEngineRouteTarget(DocTypeId));
    }

    /// <summary>
    /// 集成：JSON 格式属性行（属性名含冒号）落库后反查正确。
    /// </summary>
    [Fact]
    public async Task Cache_JsonPropertyRow_ReverseLookupCorrect()
    {
        // 第 1 步：清池 + 类型行 + JSON 格式属性行（属性名含冒号——旧格式会按第一个冒号切成两半）。
        await TruncateTableAsync();
        var repository = new UniversalEntityRepository(new EmokingDbContext());
        await repository.InsertBatchAsync(new[]
        {
            new UniversalEntity(
                Id: DocTypeId,
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "Doc"),
            new UniversalEntity(
                Id: Guid.NewGuid(),
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: DocTypeId,
                Value: "JSON:{\"Name\":\"Ratio:Base\",\"Type\":\"String\"}")
        });

        // 第 2 步：自举加载。
        var cache = new MetaCache(repository);
        await cache.LoadAsync();

        // 第 3 步：属性 TypeId 反查属性名——完整名含冒号。
        var metaType = cache.GetTypeByTypeId(DocTypeId);
        Assert.NotNull(metaType);

        // 属性字典含完整名。
        var propertyTypeId = metaType!.PropertyTypeIds.Single(pair => pair.Key == "Ratio:Base").Value;

        // 反查无损。
        Assert.Equal("Ratio:Base", cache.GetPropertyNameByTypeId(propertyTypeId));

        // DataType 显式声明在案。
        Assert.Equal("String", metaType.PropertyDataTypes["Ratio:Base"]);
    }

    /// <summary>
    /// 集成：InsertTypeMeta 的 Props 支持 JSON 数组（含冒号属性名）——落库为 JSON 格式属性行，重载后可解析。
    /// </summary>
    [Fact]
    public async Task Executor_PropsJsonArray_HandlesColonInNames()
    {
        // 第 1 步：清池 + 言出 CreateEntity 命令定义（Role=7）。
        await TruncateTableAsync();
        var repository = new UniversalEntityRepository(new EmokingDbContext());
        await repository.InsertBatchAsync(new[]
        {
            new UniversalEntity(
                Id: Guid.NewGuid(),
                TypeId: MetaCache.MetaEngineTypeId,
                Role: UniversalEntity.RoleCommand,
                ParentId: null,
                Value: "Command=CreateEntity|Args=TypeName,Props,Mode|Steps=InsertTypeMeta;ReloadCache")
        });

        // 第 2 步：自举 + 执行器。
        var cache = new MetaCache(repository);
        await cache.LoadAsync();
        var executor = new UniversalCommandExecutor(repository, cache, ".");

        // 第 3 步：执行 CreateEntity——Props 用 JSON 数组，属性名含冒号。
        await executor.RunAsync(new[]
        {
            "run",
            "CreateEntity",
            "TypeName=Doc",
            "Props=JSON:[{\"Name\":\"Ratio:Base\",\"Type\":\"String\"},{\"Name\":\"Title\",\"Type\":\"String\"}]",
            "Mode=Graph"
        });

        // 第 4 步：重载后断言——属性名完整落库可反查。
        cache = new MetaCache(repository);
        await cache.LoadAsync();
        var docTypeId = cache.GetTypeIdByName("Doc");
        Assert.NotNull(docTypeId);
        var metaType = cache.GetTypeByTypeId(docTypeId!.Value);

        // 两个属性都在（含冒号名不半解析）。
        Assert.Contains("Ratio:Base", metaType!.PropertyTypeIds.Keys);
        Assert.Contains("Title", metaType.PropertyTypeIds.Keys);

        // DataType 显式声明。
        Assert.Equal("String", metaType.PropertyDataTypes["Ratio:Base"]);
    }
}
