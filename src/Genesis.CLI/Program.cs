// ANCHOR: cli.program - Genesis 哑巴壳子：命令树与业务动作全部来自池中数据，经意图总线分发
using Emoking.DAL;

// 连接同构池。
var dbContext = new EmokingDbContext();

// 单表仓储。
var repository = new UniversalEntityRepository(dbContext);

// 元数据缓存自举——命令表与元数据一起加载。
var cache = new MetaCache(repository);
await cache.LoadAsync();

// 同构池存储——按池中 StorageProvider 指令落位。
var store = UniversalStoreFactory.Create(cache, dbContext);

// 万物网关。
var gateway = new UniversalEntityGateway(cache);

// 命令执行器——代理文件输出到当前目录。
var commandExecutor = new UniversalCommandExecutor(repository, cache, outputDirectory: ".");

// 意图总线——CLI 与 HTTP 网关共用的唯一动作入口。
var actionBus = new UniversalActionBus(cache, gateway, store, commandExecutor);

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
        Console.WriteLine("   Command=CreateEntity|Args=TypeName,Props,Mode|Steps=InsertTypeMeta;SetStorageMode;ReloadCache）");
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
    Console.WriteLine("内置业务动作：InsertData（TypeName/Data）、GetData（EntityId）");

    // 正常退出。
    return 0;
}

// argv[0] = run/InsertData/GetData → 动作名；其余为 K=V 参数。
var action = args[0];

// 解析参数。
var actionArgs = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var token in args.Skip(1))
{
    // 按第一个等号切分。
    var parts = token.Split('=', 2);

    // 缺等号的参数拒绝。
    if (parts.Length != 2)
    {
        // 报错退出。
        Console.WriteLine($"错误：参数 '{token}' 缺少 '='——格式应为 K=V。");
        return 1;
    }

    // 收集。
    actionArgs[parts[0]] = parts[1];
}

// 经意图总线执行。
try
{
    // 执行动作。
    var result = await actionBus.ExecuteActionAsync(action, actionArgs);

    // 输出结果。
    Console.WriteLine(result is System.Text.Json.JsonElement or string
        ? result.ToString()
        : System.Text.Json.JsonSerializer.Serialize(result));

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
