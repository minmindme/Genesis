// ANCHOR: dal.cli.action-bus - 意图总线：一个入口，双路由分发（工作流命令 / 业务数据动作）
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace Emoking.DAL;

/// <summary>
/// 意图总线——前端/CLI/第三方的唯一动作入口，按池中数据双路由分发：
///   1. action 命中 Role=7 工作流命令 → <see cref="UniversalCommandExecutor"/>（建类型、生成代理等）；
///   2. action 为内置业务动作（InsertData / GetData）→ <see cref="UniversalEntityGateway"/>（拆解/组装，快照与图模式自动分轨）。
/// </summary>
/// <remarks>
/// "消灭后端"的落点：HTTP 网关只做一件事——把 JSON 转成本总线的调用。
/// 新增业务动作 = 往池子里插数据（Role=7 工作流或 Role=1 类型），网关零改动。
/// </remarks>
public sealed class UniversalActionBus
{
    // 内置业务动作：向池中写入一条业务数据。
    public const string InsertDataAction = "InsertData";

    // 内置业务动作：按实体行 Id 读回业务数据。
    public const string GetDataAction = "GetData";

    // 元数据缓存。
    private readonly MetaCache _metaCache;

    // 万物网关——拆解/组装双轨。
    private readonly UniversalEntityGateway _gateway;

    // 同构池存储——由 StorageProvider 指令落位。
    private readonly IUniversalStore _store;

    // 命令执行器——Role=7 工作流。
    private readonly UniversalCommandExecutor _commandExecutor;

    /// <summary>
    /// 初始化意图总线。
    /// </summary>
    /// <param name="metaCache">元数据缓存（须已 LoadAsync）。</param>
    /// <param name="gateway">万物网关。</param>
    /// <param name="store">同构池存储。</param>
    /// <param name="commandExecutor">命令执行器。</param>
    public UniversalActionBus(
        MetaCache metaCache,
        UniversalEntityGateway gateway,
        IUniversalStore store,
        UniversalCommandExecutor commandExecutor)
    {
        // 保存依赖。
        _metaCache = metaCache ?? throw new ArgumentNullException(nameof(metaCache));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _commandExecutor = commandExecutor ?? throw new ArgumentNullException(nameof(commandExecutor));
    }

    /// <summary>
    /// 执行一个动作——意图总线的唯一入口。
    /// </summary>
    /// <param name="action">动作名（Role=7 命令名或内置业务动作）。</param>
    /// <param name="args">动作参数（K=V）。</param>
    /// <returns>执行结果（JSON 可序列化对象或文本报告）。</returns>
    /// <exception cref="InvalidOperationException">动作无法识别时抛出。</exception>
    public async Task<object> ExecuteActionAsync(string action, IReadOnlyDictionary<string, string> args)
    {
        // action 空检查。
        if (string.IsNullOrWhiteSpace(action))
        {
            // 抛出异常。
            throw new InvalidOperationException("action 不能为空。");
        }

        // 路由一：Role=7 工作流命令（命令树即数据）。
        if (_metaCache.GetCommand(action) is not null)
        {
            // 把字典参数转成 argv 形态交给命令执行器。
            var argv = new List<string> { "run", action };
            foreach (var pair in args)
            {
                // 拼装 K=V。
                argv.Add($"{pair.Key}={pair.Value}");
            }

            // 执行工作流。
            return await _commandExecutor.RunAsync(argv);
        }

        // 路由二：内置业务动作。
        return action switch
        {
            // 写入业务数据。
            InsertDataAction => await InsertDataAsync(args),

            // 读取业务数据。
            GetDataAction => await GetDataAsync(args),

            // 无法识别的动作。
            _ => throw new InvalidOperationException(
                $"动作 '{action}' 无法识别——它既不是 Role=7 工作流命令，也不是内置业务动作（{InsertDataAction}/{GetDataAction}）。")
        };
    }

    /// <summary>
    /// 写入业务数据——按类型的 StorageMode 自动分轨拆解落库。
    /// </summary>
    /// <param name="args">参数：TypeName（类型名）、Data（JSON 载荷）。</param>
    /// <returns>实体行 Id。</returns>
    private async Task<object> InsertDataAsync(IReadOnlyDictionary<string, string> args)
    {
        // 取类型名。
        var typeName = RequireArg(args, "TypeName");

        // 取数据载荷。
        var dataJson = RequireArg(args, "Data");

        // 类型名翻译为 TypeId。
        var typeId = _metaCache.GetTypeIdByName(typeName);

        // 类型未注册显式失败。
        if (typeId is null)
        {
            // 抛出异常。
            throw new InvalidOperationException(
                $"类型 '{typeName}' 未在池中注册——请先通过 CreateEntity 等工作流创建它。");
        }

        // 解析 JSON 载荷为动态字典。
        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(dataJson);

        // 载荷非法时失败。
        if (payload is null)
        {
            // 抛出异常。
            throw new InvalidOperationException("Data 载荷不是合法的 JSON 对象。");
        }

        // 网关拆解（快照/图模式自动分轨）。
        var rows = _gateway.Decompose(payload, typeId.Value);

        // 落库。
        await _store.BulkInsertAsync(rows);

        // 返回实体行 Id。
        return rows[0].Id;
    }

    /// <summary>
    /// 读取业务数据——按实体行 Id 取回并按类型 StorageMode 组装。
    /// </summary>
    /// <param name="args">参数：EntityId。</param>
    /// <returns>组装后的业务对象（强类型实例 / UniversalObject）。</returns>
    private async Task<object> GetDataAsync(IReadOnlyDictionary<string, string> args)
    {
        // 取实体行 Id。
        var entityIdText = RequireArg(args, "EntityId");

        // 解析 Guid。
        if (!Guid.TryParse(entityIdText, out var entityId))
        {
            // 抛出格式异常。
            throw new InvalidOperationException($"EntityId '{entityIdText}' 不是合法的 Guid。");
        }

        // 点查。
        var entityRow = await _store.GetByIdAsync(entityId);

        // 行不存在。
        if (entityRow is null)
        {
            // 抛出异常。
            throw new InvalidOperationException($"实体行 {entityId} 不存在或不可见。");
        }

        // 子图预取——组装需要完整行集。
        var subgraph = await _store.GetSubGraphAsync(entityRow.TypeId, entityRow.Role);

        // 网关组装（快照/强类型图/动态图自动分轨）。
        var assembled = _gateway.Assemble(subgraph, entityRow.TypeId);

        // 万物容器的动态属性不被 System.Text.Json 枚举——转为属性字典视图再返回。
        return assembled is UniversalObject universalObject
            ? universalObject.GetProperties()
            : assembled;
    }

    /// <summary>
    /// 取必填参数——缺失时抛出。
    /// </summary>
    /// <param name="args">参数字典。</param>
    /// <param name="name">参数名。</param>
    /// <returns>参数值。</returns>
    private static string RequireArg(IReadOnlyDictionary<string, string> args, string name)
    {
        // 查参数。
        if (args.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            // 返回。
            return value;
        }

        // 缺参显式失败。
        throw new InvalidOperationException($"动作缺少必填参数 '{name}'。");
    }
}
