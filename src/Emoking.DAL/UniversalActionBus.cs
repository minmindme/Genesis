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

    // 内置系统动作：读取实体审计历史链（Role=6 历史行，第 24 轮）。
    public const string GetHistoryAction = "GetHistory";

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

            // 读取审计历史链。
            GetHistoryAction => await GetHistoryAsync(args),

            // 创建双向边。
            InsertEdgeAction => await InsertEdgeAsync(args),

            // 无法识别的动作。
            _ => throw new InvalidOperationException(
                $"动作 '{action}' 无法识别——它既不是 Role=7 工作流命令，也不是内置业务动作（{InsertDataAction}/{GetDataAction}/{GetSystemSchemaAction}/{DeleteDataAction}/{UpdateDataAction}/{GetHistoryAction}/{InsertEdgeAction}）。")
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

        // 任一缺失即失败——部署/配置语义（网关按此类型自愈 reload，第 25 轮收窄）。
        if (forwardTypeId is null || reverseTypeId is null)
        {
            // 抛出未注册异常。
            throw new MetadataNotRegisteredException(
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
    /// 更新业务数据——**PUT 语义（子图替换）**：快照轨直通 CAS 覆盖；图模式三步替换（主实体 CAS + 旧属性行批量软删 + 新属性行插入）。
    /// </summary>
    /// <remarks>
    /// 第 23 轮契约化（取代第 21 轮"级联 Diff"的误导命名）：
    ///   - **这是 PUT 不是 PATCH**——Data 必须是完整对象；未包含的属性行会被软删、未包含的属性字段即"删除该字段"。
    ///     客户端应先 GetData 读全量、改字段、再整体提交。
    ///   - **API 级乐观锁**：可选参数 <c>ExpectedVersion</c>——客户端从 GetData 拿到的 Version；
    ///     不匹配（含另一客户端抢先提交）→ 显式冲突异常，静默覆盖不存在（P0-2 修复：引擎 CAS 承诺穿透到 API 层）。
    ///     缺省时用引擎当前版本（单客户端场景向后兼容）。
    ///   - **边不丢**（P0-1 修复）：旧子行清理按 PropertyTypeIds 过滤——只软删属性行，挂在实体下的边行原样保留。
    /// 三步为跨 store 调用编排而非单事务（② ③ 失败概率极低），完整原子性由后续 store 层 UpdateGraphAsync 承接。
    /// </remarks>
    /// <param name="args">参数：EntityId、Data（完整 JSON 载荷）、ExpectedVersion（可选乐观锁）。</param>
    /// <returns>更新结果字典：Id（实体行 Id）与 Version（更新后的乐观锁版本）。</returns>
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

        // 乐观锁版本——缺省用引擎当前版本（单客户端兼容）；客户端显式携带 ExpectedVersion 时严格校验
        // （多客户端并发：两个读者各持 v1，先提交者胜，后提交者显式冲突——P0-2 修复）。
        var expectedVersion = entityRow.Version;

        // 客户端显式携带 ExpectedVersion 时采用之——非法值显式报错（第 25 轮 P0：
        // 静默回退会让客户端以为在做乐观锁，实际在裸奔）。
        if (args.TryGetValue("ExpectedVersion", out var expectedText))
        {
            // 严格解析——失败即拒绝。
            if (!int.TryParse(expectedText, out var clientExpected))
            {
                // 抛出参数异常。
                throw new InvalidOperationException($"ExpectedVersion '{expectedText}' 不是合法整数。");
            }

            // 采用客户端版本。
            expectedVersion = clientExpected;
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
            // 对齐真实 Id 与客户端期望版本——乐观锁更新。
            var snapshotRow = decomposed[0] with { Id = entityId, Version = expectedVersion };

            // CAS 覆盖。
            var upsertedSnapshot = (await _store.UpsertAsync(new[] { snapshotRow })).Single();

            // 统一信封（第 25 轮）：Id + 新版本——客户端下一次提交用。
            return BuildEnvelope(
                upsertedSnapshot.Id,
                _metaCache.GetTypeByTypeId(entityRow.TypeId)?.TypeName ?? string.Empty,
                upsertedSnapshot.Version,
                null);
        }

        // ===== 图模式轨：单次 UpdateGraphAsync（第 26 轮事务收口——三步编排不再由总线自行拼装）=====

        // 第 1 步：构造新主行——对齐真实 Id 与客户端期望版本。
        var newEntityRow = decomposed[0] with { Id = entityId, Version = expectedVersion };

        // 第 2 步：筛选旧属性行——**只筛属性行，边不动**（P0-1 修复）。
        // 区分依据：属性行 TypeId ∈ 本类型元数据的 PropertyTypeIds.Values。
        var metaType = _metaCache.GetTypeByTypeId(entityRow.TypeId);

        // 本类型的属性元 TypeId 集合。
        var propertyTypeIds = metaType?.PropertyTypeIds.Values.ToHashSet() ?? new HashSet<Guid>();

        // 指针追逐取全部子行。
        var oldChildren = await _store.GetByParentIdAsync(entityId);

        // 只筛属性行（保留完整行对象，历史复制要用）。
        var oldPropertyRows = oldChildren
            .Where(row => propertyTypeIds.Contains(row.TypeId))
            .ToList();

        // 第 3 步：构造历史行——旧属性行复制为 Role=6（第 24 轮评审 6）：
        // Id 换新、Role 改历史、ParentId 挂当前实体行、TypeId/Value 原样保留；
        // 版本归属（第 25 轮 P1）：Version = 被覆盖前实体的版本；UpdatedAt = 变更时刻。
        var propertyHistoryRows = oldPropertyRows
            .Select(row => row with
            {
                // 历史行 Id 换新。
                Id = Guid.NewGuid(),
                // 角色改为历史。
                Role = UniversalEntity.RoleHistory,
                // 挂到被更新的当前实体行下。
                ParentId = entityId,
                // 版本归属：被覆盖前实体的版本。
                Version = expectedVersion,
                // 变更时刻：本次更新发生时间。
                UpdatedAt = DateTime.UtcNow
            });

        // 第 3.5 步：主实体旧行同样落成历史行（第 24 轮行为对齐——原 UpsertAsync 内产生，
        // 现在显式构造随 UpdateGraph 事务插入）：TypeId = 实体元 TypeId、Value = 旧实体行值、Version = 旧实体版本。
        var entityHistoryRow = entityRow with
        {
            // 历史行 Id 换新。
            Id = Guid.NewGuid(),
            // 角色改为历史。
            Role = UniversalEntity.RoleHistory,
            // 挂到被更新的当前实体行下。
            ParentId = entityId,
            // 变更时刻。
            UpdatedAt = DateTime.UtcNow
        };

        // 汇合历史行——主实体历史 + 属性行历史。
        var historyRows = propertyHistoryRows.Concat(new[] { entityHistoryRow }).ToList();

        // 第 4 步：构造新属性行——重挂 ParentId 到真实实体行 Id。
        var newPropertyRows = decomposed
            .Skip(1)
            .Select(row => row with { ParentId = entityId })
            .ToList();

        // 第 5 步：单次 UpdateGraphAsync——主行 CAS + 旧属性行软删 + 历史行与新行插入，
        // 事务语义由引擎分级保证（SQL 单事务 / InMemory 锁 / FASTER 顺序——见接口契约）。
        var upsertedEntity = await _store.UpdateGraphAsync(
            newEntityRow,
            oldPropertyRows.Select(row => row.Id).ToList(),
            historyRows,
            newPropertyRows);

        // 返回统一信封（第 25 轮）：Id + 新版本——客户端下一次提交用。
        return BuildEnvelope(
            upsertedEntity.Id,
            metaType?.TypeName ?? string.Empty,
            upsertedEntity.Version,
            null);
    }

    /// <summary>
    /// 读取实体审计历史链——Role=6 历史行按时间线排序，属性名经元数据反查翻译。
    /// </summary>
    /// <remarks>
    /// 第 24 轮（评审 6）：审计能力从"raw SQL 的事"变成"引擎的事"。
    /// 快照历史行 Value=旧 SNAP1 包（Property 为 null）；图模式历史行 Value=旧属性值，
    /// Property 经 TypeId 反查还原属性名。加密策略下历史行由装饰器透明解密。
    /// </remarks>
    /// <param name="args">参数：EntityId。</param>
    /// <returns>审计条目列表：Property（属性名，快照行为 null）/Value/Version/TypeId/ChangedAt。</returns>
    private async Task<object> GetHistoryAsync(IReadOnlyDictionary<string, string> args)
    {
        // 取实体行 Id。
        var entityIdText = RequireArg(args, "EntityId");

        // 解析 Guid。
        if (!Guid.TryParse(entityIdText, out var entityId))
        {
            // 抛出格式异常。
            throw new InvalidOperationException($"EntityId '{entityIdText}' 不是合法的 Guid。");
        }

        // 取历史行（按时间线升序）。
        var historyRows = await _store.GetHistoryAsync(entityId);

        // 逐行翻译为审计条目。
        var entries = new List<Dictionary<string, object?>>(historyRows.Count);

        // 遍历翻译。
        foreach (var row in historyRows)
        {
            // 属性名反查——快照历史行 TypeId 是实体元 TypeId，反查为 null（客户端按 SNAP1 识别）。
            var propertyName = _metaCache.GetPropertyNameByTypeId(row.TypeId);

            // 组装审计条目。
            entries.Add(new Dictionary<string, object?>
            {
                // 属性名（快照行为 null）。
                ["Property"] = propertyName,
                // 旧值（快照行为旧 SNAP1 包）。
                ["Value"] = row.Value,
                // 旧版本。
                ["Version"] = row.Version,
                // 行类型指针（供高级消费方反查）。
                ["TypeId"] = row.TypeId,
                // 变更时间。
                ["ChangedAt"] = row.UpdatedAt
            });
        }

        // 返回审计链。
        return entries;
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

        // 类型未注册显式失败——部署/配置语义（网关按此类型自愈 reload，第 25 轮收窄）。
        if (typeId is null)
        {
            // 抛出未注册异常。
            throw new MetadataNotRegisteredException(
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

        // 统一信封（第 25 轮）：{Id, TypeName, Version, Data}——客户端凭 Version 做乐观锁闭环。
        return BuildEnvelope(rows[0].Id, typeName, rows[0].Version, null);
    }

    /// <summary>
    /// 构造统一返回信封——三个数据动作（InsertData/GetData/UpdateData）的统一响应形态。
    /// </summary>
    /// <param name="id">实体行 Id。</param>
    /// <param name="typeName">实体类型名。</param>
    /// <param name="version">乐观锁版本。</param>
    /// <param name="data">业务数据（GetData 为完整属性/实例；Insert/Update 为 null——详情走 GetData）。</param>
    /// <returns>可 JSON 序列化的信封字典。</returns>
    private static Dictionary<string, object?> BuildEnvelope(Guid id, string typeName, int version, object? data)
    {
        // 组装信封——四键齐。
        return new Dictionary<string, object?>
        {
            // 实体行 Id。
            ["Id"] = id,
            // 实体类型名。
            ["TypeName"] = typeName,
            // 乐观锁版本。
            ["Version"] = version,
            // 业务数据。
            ["Data"] = data
        };
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

        // 实体类型名——信封自描述用。
        var entityTypeName = _metaCache.GetTypeByTypeId(entityRow.TypeId)?.TypeName ?? string.Empty;

        // 网关组装（快照/强类型图/动态图自动分轨）。
        var assembled = _gateway.Assemble(subgraph, entityRow.TypeId);

        // 万物容器的动态属性不被 System.Text.Json 枚举——转统一信封（第 25 轮）：
        // Data 装属性字典视图，Id/TypeName/Version 在信封层——三动作形态对齐，
        // 客户端对任何模式都能拿 Version 做乐观锁闭环。
        if (assembled is UniversalObject universalObject)
        {
            // 动态轨——属性字典为 Data。
            return BuildEnvelope(entityId, universalObject.TypeName, entityRow.Version, universalObject.GetProperties());
        }

        // 快照/强类型轨——实例为 Data（强类型实例保留编译期形态）。
        return BuildEnvelope(entityId, entityTypeName, entityRow.Version, assembled);
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
