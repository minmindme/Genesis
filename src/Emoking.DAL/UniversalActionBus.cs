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

    // 内置业务动作：更新业务数据（快照轨 CAS 覆盖 / 图模式级联 Diff）。
    public const string UpdateDataAction = "UpdateData";

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

            // 更新业务数据。
            UpdateDataAction => await UpdateDataAsync(args),

            // 创建双向边。
            InsertEdgeAction => await InsertEdgeAsync(args),

            // 无法识别的动作。
            _ => throw new InvalidOperationException(
                $"动作 '{action}' 无法识别——它既不是 Role=7 工作流命令，也不是内置业务动作（{InsertDataAction}/{GetDataAction}/{GetSystemSchemaAction}/{DeleteDataAction}/{UpdateDataAction}/{InsertEdgeAction}）。")
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

        // 第 20 轮半修：单事务批量软删——消灭"中途失败留下删了一半的状态"。
        var deleted = await _store.DeleteManyAsync(pendingDeletes);

        // 返回删除行数。
        return deleted;
    }

    /// <summary>
    /// 更新业务数据——快照轨直通 CAS 覆盖；图模式级联 Diff（主实体 Upsert + 旧属性行批量软删 + 新属性行插入）。
    /// </summary>
    /// <remarks>
    /// 第 21 轮（方案 A 战役一）：补齐图模式实体的"状态机变更"短板。
    /// 快照轨：新载荷拆解为 1 行 → 对齐 Id/Version → UpsertAsync（历史行由存储层自动产生）。
    /// 图模式轨（含强类型图与动态图——Decompose 自动分）：三步编排——
    ///   1. 主实体 CAS Upsert（Version 校验失败 = 零残留，旧属性行仍完整）；
    ///   2. 旧属性行 DeleteMany（单事务软删——变更历史经软删行可审计）；
    ///   3. 新属性行重挂 ParentId 到真实实体行 Id 后 BulkInsert。
    /// 诚实边界：三步是跨 store 调用的编排而非单事务——第 2/3 步失败会留下中间态
    /// （概率极低：软删与插入几乎不会失败），完整原子性列为 store 层后续演进。
    /// </remarks>
    /// <param name="args">参数：EntityId、Data（JSON 载荷，与 InsertData 同形态）。</param>
    /// <returns>实体行 Id。</returns>
    private async Task<object> UpdateDataAsync(IReadOnlyDictionary<string, string> args)
    {
        // 取实体行 Id。
        var entityIdText = RequireArg(args, "EntityId");

        // 解析 Guid。
        if (!Guid.TryParse(entityIdText, out var entityId))
        {
            // 抛出格式异常。
            throw new InvalidOperationException($"EntityId '{entityIdText}' 不是合法的 Guid。");
        }

        // 取数据载荷。
        var dataJson = RequireArg(args, "Data");

        // 实例级定位——实体行必须存在。
        var entityRow = await _store.GetByIdAsync(entityId);

        // 行不存在。
        if (entityRow is null)
        {
            // 抛出异常。
            throw new InvalidOperationException($"实体行 {entityId} 不存在或不可见。");
        }

        // 解析 JSON 载荷。
        var payload = JsonSerializer.Deserialize<Dictionary<string, object?>>(dataJson);

        // 载荷非法时失败。
        if (payload is null)
        {
            // 抛出异常。
            throw new InvalidOperationException("Data 载荷不是合法的 JSON 对象。");
        }

        // 按当前类型拆解新子图（快照/强类型图/动态图由网关自动分轨）。
        var decomposed = _gateway.Decompose(payload, entityRow.TypeId);

        // 快照轨：单行直通 CAS 覆盖——历史可审计，零写放大。
        if (string.Equals(_metaCache.GetStorageMode(entityRow.TypeId), "Snapshot", StringComparison.Ordinal))
        {
            // 对齐真实 Id 与当前 Version——乐观锁更新。
            var snapshotRow = decomposed[0] with { Id = entityId, Version = entityRow.Version };

            // CAS 覆盖。
            var upsertedSnapshot = (await _store.UpsertAsync(new[] { snapshotRow })).Single();

            // 返回实体行 Id。
            return upsertedSnapshot.Id;
        }

        // ===== 图模式轨：三步级联 Diff =====

        // 第 1 步：主实体 CAS Upsert——对齐真实 Id 与当前 Version。
        var newEntityRow = decomposed[0] with { Id = entityId, Version = entityRow.Version };

        // CAS 覆盖主实体。
        var upsertedEntity = (await _store.UpsertAsync(new[] { newEntityRow })).Single();

        // 第 2 步：旧属性行批量软删（单事务）——旧值经软删行可审计。
        var oldChildren = await _store.GetByParentIdAsync(entityId);

        // 有旧子行才删。
        if (oldChildren.Count > 0)
        {
            // 单事务批量软删。
            await _store.DeleteManyAsync(oldChildren.Select(row => row.Id).ToList());
        }

        // 第 3 步：新属性行重挂 ParentId 后插入——Decompose 生成的 ParentId 指向拆解时的临时实体行 Id。
        var newPropertyRows = decomposed
            .Skip(1)
            .Select(row => row with { ParentId = entityId })
            .ToList();

        // 有新子行才插。
        if (newPropertyRows.Count > 0)
        {
            // 批量插入新属性行。
            await _store.BulkInsertAsync(newPropertyRows);
        }

        // 返回实体行 Id。
        return upsertedEntity.Id;
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
    /// 读取业务数据——**实例级**子图（实体行 + 该实体的子行），按类型 StorageMode 组装。
    /// </summary>
    /// <remarks>
    /// 第 19 轮修复（洞 1）：此前走类型级 GetSubGraph 再取"第一条实体行"，
    /// 同类型多实体时返回任意一条。现改为实例级：GetById 定位实体行 + GetByParentId 指针追逐子行。
    /// </remarks>
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

        // 实例级定位——实体行本身。
        var entityRow = await _store.GetByIdAsync(entityId);

        // 行不存在。
        if (entityRow is null)
        {
            // 抛出异常。
            throw new InvalidOperationException($"实体行 {entityId} 不存在或不可见。");
        }

        // 实例级子图——实体行 + 该实体的全部子行（只属于这一个实体，不含同类型其他实体）。
        var subgraph = new List<UniversalEntity> { entityRow };
        subgraph.AddRange(await _store.GetByParentIdAsync(entityId));

        // 网关组装（快照/强类型图/动态图自动分轨）。
        var assembled = _gateway.Assemble(subgraph, entityRow.TypeId);

        // 万物容器的动态属性不被 System.Text.Json 枚举——转为属性字典视图再返回。
        // 第 20 轮半修：字典视图补 Id 与 TypeName——通用 API 的返回值必须能自证身份，
        // agent 拿到它才能发起后续操作（DeleteData/InsertEdge 都需要实体行 Id）。
        return assembled is UniversalObject universalObject
            ? BuildSelfDescribingView(universalObject)
            : assembled;
    }

    /// <summary>
    /// 构造自描述属性视图——动态属性字典 + 实体行 Id 与类型名。
    /// </summary>
    /// <param name="universalObject">组装完成的万物容器。</param>
    /// <returns>可 JSON 序列化的属性字典（含 Id/TypeName 两个身份键）。</returns>
    private static Dictionary<string, object?> BuildSelfDescribingView(UniversalObject universalObject)
    {
        // 复制属性字典——不污染容器内部状态。
        var view = new Dictionary<string, object?>(universalObject.GetProperties(), StringComparer.Ordinal);

        // 身份键：实体行 Id。
        view["Id"] = universalObject.Id;

        // 身份键：实体类型名。
        view["TypeName"] = universalObject.TypeName;

        // 返回自描述视图。
        return view;
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
