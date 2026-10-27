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

    // 内置系统动作：一次拉回全部类型、命令与 UI Schema——通用前端渲染器的数据源。
    public const string GetSystemSchemaAction = "GetSystemSchema";

    // 内置业务动作：级联软删实体行及其全部子行（属性、出边）。
    public const string DeleteDataAction = "DeleteData";

    // 内置业务动作：创建同构双向边（按边类型名查正反向 TypeId）。
    public const string InsertEdgeAction = "InsertEdge";

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

        // 路由二：内置业务/系统动作。
        return action switch
        {
            // 写入业务数据。
            InsertDataAction => await InsertDataAsync(args),

            // 读取业务数据。
            GetDataAction => await GetDataAsync(args),

            // 拉取系统 Schema（类型 + 命令 + UI）。
            GetSystemSchemaAction => GetSystemSchema(),

            // 级联软删。
            DeleteDataAction => await DeleteDataAsync(args),

            // 创建双向边。
            InsertEdgeAction => await InsertEdgeAsync(args),

            // 无法识别的动作。
            _ => throw new InvalidOperationException(
                $"动作 '{action}' 无法识别——它既不是 Role=7 工作流命令，也不是内置业务动作（{InsertDataAction}/{GetDataAction}/{GetSystemSchemaAction}/{DeleteDataAction}/{InsertEdgeAction}）。")
        };
    }

    /// <summary>
    /// 创建双向边——按边类型名查正/反向 TypeId（命名约定：反向名 = 正向名 + "By"），落库 2 行。
    /// </summary>
    /// <param name="args">参数：FromId、ToId、EdgeType（边类型名）、Props（可选 JSON）。</param>
    /// <returns>落库的行数（恒为 2）。</returns>
    private async Task<object> InsertEdgeAsync(IReadOnlyDictionary<string, string> args)
    {
        // 取起点实体 Id。
        var fromIdText = RequireArg(args, "FromId");

        // 取终点实体 Id。
        var toIdText = RequireArg(args, "ToId");

        // 取边类型名。
        var edgeTypeName = RequireArg(args, "EdgeType");

        // 解析 Guid。
        if (!Guid.TryParse(fromIdText, out var fromId) || !Guid.TryParse(toIdText, out var toId))
        {
            // 抛出格式异常。
            throw new InvalidOperationException("FromId/ToId 必须是合法的 Guid。");
        }

        // 按命名约定解析正反向 TypeId——ImportSystem 生成边类型时自动创建反向类型行（名 + "By"）。
        var forwardTypeId = _metaCache.GetTypeIdByName(edgeTypeName);
        var reverseTypeId = _metaCache.GetTypeIdByName(edgeTypeName + "By");

        // 任一缺失即失败。
        if (forwardTypeId is null || reverseTypeId is null)
        {
            // 抛出异常。
            throw new InvalidOperationException(
                $"边类型 '{edgeTypeName}'/'{edgeTypeName}By' 未在池中注册——请先通过 ImportSystem 定义它。");
        }

        // 解析边属性（可选 JSON）。
        Dictionary<string, object?>? props = null;

        // Props 存在时反序列化。
        if (args.TryGetValue("Props", out var propsJson) && !string.IsNullOrWhiteSpace(propsJson))
        {
            // 反序列化。
            props = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(propsJson);
        }

        // 网关生成双向行。
        var edgeRows = _gateway.CreateEdge(fromId, forwardTypeId.Value, reverseTypeId.Value, toId, props);

        // 落库。
        await _store.BulkInsertAsync(edgeRows);

        // 返回行数。
        return edgeRows.Count;
    }

    /// <summary>
    /// 级联软删——软删实体行 + 全部 ParentId 指向它的子行（属性行、出边行）。
    /// </summary>
    /// <remarks>
    /// 保持池子干净：实体消失后其属性行与出边不会成为孤儿。
    /// 诚实边界：反向边（挂在其他实体下的入边）本轮不清理——列为旁支。
    /// </remarks>
    /// <param name="args">参数：EntityId。</param>
    /// <returns>实际软删的行数。</returns>
    private async Task<object> DeleteDataAsync(IReadOnlyDictionary<string, string> args)
    {
        // 取实体行 Id。
        var entityIdText = RequireArg(args, "EntityId");

        // 解析 Guid。
        if (!Guid.TryParse(entityIdText, out var entityId))
        {
            // 抛出格式异常。
            throw new InvalidOperationException($"EntityId '{entityIdText}' 不是合法的 Guid。");
        }

        // 实体行必须存在。
        var entityRow = await _store.GetByIdAsync(entityId);

        // 不存在。
        if (entityRow is null)
        {
            // 抛出异常。
            throw new InvalidOperationException($"实体行 {entityId} 不存在或不可见。");
        }

        // 收集待删行——实体行 + 全部子行。
        var pendingDeletes = new List<Guid> { entityId };

        // 指针追逐取子行（属性行、出边行）。
        var children = await _store.GetByParentIdAsync(entityId);

        // 逐子行加入。
        foreach (var child in children)
        {
            // 加入待删。
            pendingDeletes.Add(child.Id);
        }

        // 逐行软删。
        var deleted = 0;

        // 遍历软删。
        foreach (var rowId in pendingDeletes)
        {
            // 软删命中计数。
            if (await _store.DeleteAsync(rowId))
            {
                // 累计。
                deleted++;
            }
        }

        // 返回删除行数。
        return deleted;
    }

    /// <summary>
    /// 拉取系统 Schema——类型清单、可用命令与全部 UI Schema，前端通用渲染器的唯一数据源。
    /// </summary>
    /// <returns>系统 Schema 字典。</returns>
    private object GetSystemSchema()
    {
        // 组装系统 Schema。
        return new Dictionary<string, object?>
        {
            // 全部实体类型。
            ["Types"] = _metaCache.GetAllTypes().Values
                .Select(metaType => new { metaType.TypeId, metaType.TypeName })
                .ToList(),

            // 全部 CLI/工作流命令。
            ["Commands"] = _metaCache.GetCommandNames().ToList(),

            // 全部 UI Schema。
            ["Uis"] = _metaCache.GetUiSchemas()
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
