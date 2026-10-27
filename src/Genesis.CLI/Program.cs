// ANCHOR: cli.program - Genesis 哑巴壳子：命令树全部来自池中 Role=7 数据
using Emoking.DAL;

// 连接同构池。
var dbContext = new EmokingDbContext();

// 单表仓储。
var repository = new UniversalEntityRepository(dbContext);

// 元数据缓存自举——命令表与元数据一起加载。
var cache = new MetaCache(repository);
await cache.LoadAsync();

// 命令执行器——代理文件输出到当前目录。
var executor = new UniversalCommandExecutor(repository, cache, outputDirectory: ".");

// 无参数时打印命令表——菜单本身就是池中数据。
if (args.Length == 0)
{
    // 标题。
    Console.WriteLine("Genesis CLI（哑巴壳子）——命令树来自 UniversalEntity Role=7：");

    // 列出池中命令。
    var names = cache.GetCommandNames();

    // 无命令提示。
    if (names.Count == 0)
    {
        // 提示。
        Console.WriteLine("  （池中尚未注册任何命令——请插入 Role=7 行，例如：");
        Console.WriteLine("   Command=CreateEntity|Args=TypeName,Props,Mode|Steps=InsertTypeMeta|SetStorageMode|ReloadCache）");
        return 0;
    }

    // 逐命令展示定义。
    foreach (var name in names)
    {
        // 取定义。
        var command = cache.GetCommand(name)!;

        // 展示。
        Console.WriteLine($"  {name}  Args: {command.GetValueOrDefault("Args", "-")}  Steps: {command.GetValueOrDefault("Steps", "-")}");
    }

    // 用法提示。
    Console.WriteLine();
    Console.WriteLine("用法：genesis run <命令名> [K=V ...]");

    // 正常退出。
    return 0;
}

// 有参数——执行命令。
try
{
    // 执行。
    var report = await executor.RunAsync(args);

    // 输出报告。
    Console.WriteLine(report);

    // 成功退出。
    return 0;
}
catch (InvalidOperationException ex)
{
    // 业务异常输出。
    Console.WriteLine($"错误：{ex.Message}");

    // 失败退出。
    return 1;
}
