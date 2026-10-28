// ANCHOR: test.command-executor - 同构 CLI 验收：命令即数据、工作流编排、代理生成
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Emoking.DAL.Tests;

/// <summary>
/// 同构 CLI 验收——Role=7 命令行落库后，执行器按 Steps 编排完成
/// "言出类型 → 模式指令 → 代理生成 → 热重载"全工作流。
/// </summary>
public sealed class CommandExecutorTests : IAsyncLifetime
{
    /// <summary>
    /// 类级初始化——为本测试类创建独立数据库（第 21 轮测试基建）。
    /// 类内保留的 TruncateTableAsync 在独立空库上是幂等空操作（历史注释语义不变）。
    /// </summary>
    public async Task InitializeAsync()
    {
        // 建独立库并注册连接串——类内所有 new EmokingDbContext() 自动路由到这里。
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
    // 演示命令行的固定 Id。
    private static readonly Guid CreateEntityCommandRowId = new("00000000-0000-0000-0000-000000000920");

    // 演示类型的 TypeId（执行器生成，测试内先固定用于断言）。
    // 注意：执行器用 Guid.NewGuid 生成类型行 Id，测试通过池中查询定位而非预知。
    
    /// <summary>
    /// 清空磁盘表——从空池开始。
    /// </summary>
    private static async Task TruncateTableAsync()
    {
        // 打开默认连接。
        using var connection = new EmokingDbContext().CreateConnection();

        // 构造清空命令。
        await using var command = connection.CreateCommand();

        // TRUNCATE 全表。
        command.CommandText = "TRUNCATE TABLE [dbo].[UniversalEntity]";

        // 执行。
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 言出 CreateEntity 命令定义——一行 Role=7 数据。
    /// </summary>
    /// <param name="repository">单表仓储。</param>
    private static async Task SpeakCreateEntityCommandAsync(UniversalEntityRepository repository)
    {
        // 命令定义行。
        var commandRow = new UniversalEntity(
            Id: CreateEntityCommandRowId,
            TypeId: MetaCache.MetaEngineTypeId,
            Role: UniversalEntity.RoleCommand,
            ParentId: null,
            Value: "Command=CreateEntity|Args=TypeName,Props,Mode,CSharpType|Steps=InsertTypeMeta;SetStorageMode;GenerateProxy;ReloadCache");

        // 落库。
        await repository.InsertBatchAsync(new[] { commandRow });
    }

    /// <summary>
    /// 构造自举完成的元数据缓存。
    /// </summary>
    private static async Task<MetaCache> BuildLoadedCacheAsync()
    {
        // 磁盘仓储 + 缓存自举。
        var cache = new MetaCache(new UniversalEntityRepository(new EmokingDbContext()));
        await cache.LoadAsync();

        // 返回缓存。
        return cache;
    }

    /// <summary>
    /// 核心闭环：池中一行 Role=7 → 执行器按 Steps 完成"言出类型 + 模式指令 + 代理生成 + 热重载"。
    /// </summary>
    [Fact]
    public async Task Run_CreateEntityWorkflow_InsertsMetadata_GeneratesProxy_Reloads()
    {
        // 第 1 步：清池 + 言出命令定义 + 自举。
        await TruncateTableAsync();
        var diskRepository = new UniversalEntityRepository(new EmokingDbContext());
        await SpeakCreateEntityCommandAsync(diskRepository);
        var cache = await BuildLoadedCacheAsync();

        // 命令已注册。
        Assert.NotNull(cache.GetCommand("CreateEntity"));

        // 第 2 步：构造执行器（代理输出到 bin 输出目录下——沙箱拒绝 %TEMP% 子目录创建，见 B13）。
        var tempDir = Path.Combine(AppContext.BaseDirectory, $"genesis_cli_{Guid.NewGuid():N}");
        var executor = new UniversalCommandExecutor(diskRepository, cache, tempDir);

        // 第 3 步：执行命令——argv 形态与 CLI 一致。
        var report = await executor.RunAsync(new[]
        {
            "run",
            "CreateEntity",
            "TypeName=SmokeOrder",
            "Props=OrderNo:String,Amount:Decimal,OrderDate:DateTime",
            "Mode=Snapshot",
            "CSharpType=Genesis.Generated.SmokeOrder"
        });

        // 报告含命令名。
        Assert.Contains("CreateEntity", report);

        // 第 4 步：断言池中已长出 SmokeOrder 类型（热重载后元数据可见）。
        var smokeTypes = await diskRepository.GetByTypeIdAndRoleAsync(MetaCache.MetaTypeTypeId, UniversalEntity.RoleMeta);
        var typeRow = smokeTypes.Single(row => row.Value == "SmokeOrder");

        // 3 条属性行挂在类型下。
        var propRows = smokeTypes.Where(row => row.ParentId == typeRow.Id).ToList();
        Assert.Equal(3, propRows.Count);

        // 第 5 步：断言 StorageMode=Snapshot 指令已落库。
        Assert.Equal("Snapshot", cache.GetStorageMode(typeRow.Id));

        // 第 6 步：断言代理文件已生成且包含属性。
        var proxyPath = Path.Combine(tempDir, "SmokeOrder.cs");
        Assert.True(File.Exists(proxyPath));
        var proxyContent = File.ReadAllText(proxyPath);
        Assert.Contains("public sealed class SmokeOrder", proxyContent);
        Assert.Contains("public string OrderNo", proxyContent);
        Assert.Contains("public decimal Amount", proxyContent);
        Assert.Contains("public DateTime OrderDate", proxyContent);

        // 清理临时目录。
        Directory.Delete(tempDir, recursive: true);
    }

    /// <summary>
    /// 未知命令显式失败——哑巴壳子不猜意图。
    /// </summary>
    [Fact]
    public async Task Run_UnknownCommand_ThrowsWithRegisteredNames()
    {
        // 清池 + 自举（池中无任何 Role=7 命令）。
        await TruncateTableAsync();
        var diskRepository = new UniversalEntityRepository(new EmokingDbContext());
        var cache = await BuildLoadedCacheAsync();
        var executor = new UniversalCommandExecutor(diskRepository, cache);

        // 未注册命令抛错。
        var exception = await Assert.ThrowsAsync<MetadataNotRegisteredException>(
            () => executor.RunAsync(new[] { "run", "CreateOrder" }));

        // 异常消息点明命令未注册。
        Assert.Contains("未在池中注册", exception.Message);
    }

    /// <summary>
    /// 未注册的步骤名显式失败——编排数据里的步骤必须存在于注册表。
    /// </summary>
    [Fact]
    public async Task Run_UnregisteredStep_Throws()
    {
        // 第 1 步：清池 + 言出带未知步骤的命令。
        await TruncateTableAsync();
        var diskRepository = new UniversalEntityRepository(new EmokingDbContext());
        await diskRepository.InsertBatchAsync(new[]
        {
            new UniversalEntity(
                Id: new Guid("00000000-0000-0000-0000-000000000921"),
                TypeId: MetaCache.MetaEngineTypeId,
                Role: UniversalEntity.RoleCommand,
                ParentId: null,
                Value: "Command=BadFlow|Args=TypeName|Steps=InsertTypeMeta;TeleportToMars")
        });
        var cache = await BuildLoadedCacheAsync();
        var executor = new UniversalCommandExecutor(diskRepository, cache);

        // 第 2 步：执行——TeleportToMars 步骤不存在，显式失败。
        var exception = await Assert.ThrowsAsync<MetadataNotRegisteredException>(
            () => executor.RunAsync(new[] { "run", "BadFlow", "TypeName=Whatever" }));

        // 异常消息点明哪个步骤未注册。
        Assert.Contains("TeleportToMars", exception.Message);
    }
}
