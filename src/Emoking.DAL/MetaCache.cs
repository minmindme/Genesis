// ANCHOR: dal.cache.meta-cache - Role=1/4 元数据内存缓存（自举加载）
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Emoking.DAL;

/// <summary>
/// 元数据缓存——原型期用 ConcurrentDictionary 全量加载到内存。
/// 真实部署时支持热更新：接收到 Role=1 变更事件后重新加载。
/// </summary>
/// <remarks>
/// 缓存两类元数据：
///   1. MetaType：实体类型定义（TypeId → 类型名、字段列表）
///   2. MetaEngine：引擎指令（TypeId → 路由目标、处理器类型）
/// </remarks>
public sealed class MetaType
{
    /// <summary>
    /// 元数据行 TypeId——所有同类型实例共享这个 TypeId。
    /// </summary>
    public Guid TypeId { get; init; }

    /// <summary>
    /// 实体类型名（如 "User"、"Order"）。
    /// </summary>
    public string TypeName { get; init; } = string.Empty;

    /// <summary>
    /// 该实体的属性 TypeId 字典：属性名 → 属性 Meta TypeId。
    /// 用于反向挂载时知道哪个属性值行对应哪个 C# 属性。
    /// </summary>
    public Dictionary<string, Guid> PropertyTypeIds { get; init; } = new();
}

/// <summary>
/// 引擎路由元数据——Role=4 行对应的内存结构。
/// </summary>
public sealed class MetaEngine
{
    /// <summary>
    /// 引擎指令行 TypeId。
    /// </summary>
    public Guid TypeId { get; init; }

    /// <summary>
    /// 路由目标类型名（如 "UserRepository"、"OrderProcessor"）。
    /// </summary>
    public string RouteTarget { get; init; } = string.Empty;

    /// <summary>
    /// 该引擎指令作用于哪个实体 TypeId。
    /// </summary>
    public Guid TargetEntityTypeId { get; init; }

    /// <summary>
    /// 指令行 Value 的完整键值对集合——承载任意扩展指令（如 StorageProvider）。
    /// </summary>
    public Dictionary<string, string> Settings { get; init; } = new(StringComparer.Ordinal);
}

/// <summary>
/// 元数据缓存服务，加载并维护 Role=1 和 Role=4 的内存快照。
/// </summary>
public sealed class MetaCache
{
    // 元类型 Guid——"实体类型定义"这类行自身的 TypeId 指针。
    private static readonly Guid MetaTypeTypeId = new("00000000-0000-0000-0000-000000000001");

    // 引擎指令类型 Guid——"引擎指令"这类行自身的 TypeId 指针。
    private static readonly Guid MetaEngineTypeId = new("00000000-0000-0000-0000-000000000002");

    // User 实体类型的固定 Guid——种子行 Id 与实例行 TypeId 都用它。
    private static readonly Guid UserTypeId = new("11111111-1111-1111-1111-111111111111");

    // User 四个属性的固定 Guid——种子属性行 Id 与实例属性行 TypeId 都用它们。
    private static readonly Guid UserNameTypeId = new("11111111-1111-1111-1111-111111111101");
    private static readonly Guid UserEmailTypeId = new("11111111-1111-1111-1111-111111111102");
    private static readonly Guid UserAgeTypeId = new("11111111-1111-1111-1111-111111111103");
    private static readonly Guid UserCreatedAtTypeId = new("11111111-1111-1111-1111-111111111104");

    // User 引擎指令的固定 Guid——种子指令行 Id。
    private static readonly Guid UserEngineId = new("44444444-4444-4444-4444-444444444444");

    // 存储引擎选择指令的固定 Guid——种子第 7 行。
    private static readonly Guid StorageProviderEngineId = new("00000000-0000-0000-0000-000000000902");

    // 安全策略行的固定 Guid——创世第 8/9/10 行（加密 / 租户隔离 / 限流）。
    private static readonly Guid EncryptPolicyRowId = new("00000000-0000-0000-0000-000000000904");
    private static readonly Guid TenantPolicyRowId = new("00000000-0000-0000-0000-000000000905");
    private static readonly Guid RateLimitPolicyRowId = new("00000000-0000-0000-0000-000000000906");

    // 池中解析出的存储引擎名——无指令时默认 SqlServer（唯一带完整 ACID 的保守选项）。
    private string? _storageProviderName;

    // 安全策略缓存：策略名 → 键值对集合（Role=5 解析结果）。
    private readonly ConcurrentDictionary<string, Dictionary<string, string>> _securityPolicies = new(StringComparer.Ordinal);

    // 实体类型缓存：TypeId → MetaType
    private readonly ConcurrentDictionary<Guid, MetaType> _types = new();

    // 引擎指令缓存：TypeId → MetaEngine
    private readonly ConcurrentDictionary<Guid, MetaEngine> _engines = new();

    // 仓储引用，用于启动时从数据库拉元数据与自播种。
    private readonly UniversalEntityRepository _repository;

    /// <summary>
    /// 注入仓储初始化缓存。
    /// </summary>
    /// <param name="repository">单表仓储实例。</param>
    public MetaCache(UniversalEntityRepository repository)
    {
        // 保存仓储引用到私有字段。
        _repository = repository;
    }

    // ============================================================================
    // 加载（自举）
    // ============================================================================

    /// <summary>
    /// 启动时从数据库加载 Role=1 与 Role=4 元数据到内存——引擎从同构池中读取自己的"创世法则"。
    /// 数据池为空时自动播种创世行（幂等），随后重新加载。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步加载操作完成的任务。</returns>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        // 清空旧缓存——支持热重载。
        _types.Clear();
        _engines.Clear();
        _securityPolicies.Clear();
        _storageProviderName = null;

        // 一次性拉取全部 Role=1 与 Role=4 行——安全策略（Role=5）按需增量拉取，见下方。
        var metaRows = await _repository.GetByRolesAsync(
            [UniversalEntity.RoleMeta, UniversalEntity.RoleEngine],
            cancellationToken);

        // 一次性拉取全部 Role=5 安全策略行。
        var policyRows = await _repository.GetByRolesAsync(
            [UniversalEntity.RoleSecurity],
            cancellationToken);

        // 没有任何元数据行说明数据池是空的——执行创世播种。
        if (!metaRows.Any(row => row.Role == UniversalEntity.RoleMeta))
        {
            // 把创世法则写进同构池。
            await SeedGenesisRowsAsync(cancellationToken);

            // 播种后重新拉取——保证内存缓存与库中内容严格一致。
            metaRows = await _repository.GetByRolesAsync(
                [UniversalEntity.RoleMeta, UniversalEntity.RoleEngine],
                cancellationToken);

            // 安全策略行同样重新拉取。
            policyRows = await _repository.GetByRolesAsync(
                [UniversalEntity.RoleSecurity],
                cancellationToken);
        }

        // 把同构行解析为内存元数据结构。
        ParseMetaRows(metaRows);

        // 解析安全策略行。
        ParseSecurityPolicies(policyRows);
    }

    /// <summary>
    /// 向同构池写入创世种子行：1 条实体类型定义 + 4 条属性定义 + 1 条引擎路由指令。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表示异步播种操作完成的任务。</returns>
    private async Task SeedGenesisRowsAsync(CancellationToken cancellationToken)
    {
        // 组装全部创世行——结构与实例行完全同构，只是 Role 与指针语义不同。
        var genesisRows = new List<UniversalEntity>
        {
            // 创世行 1：User 实体类型定义——ParentId 为空表示"类型"，Value 为类型名。
            new(
                Id: UserTypeId,
                TypeId: MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: "User"),

            // 创世行 2：Name 属性定义——ParentId 指向 User 类型。
            new(
                Id: UserNameTypeId,
                TypeId: MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: UserTypeId,
                Value: "Name"),

            // 创世行 3：Email 属性定义。
            new(
                Id: UserEmailTypeId,
                TypeId: MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: UserTypeId,
                Value: "Email"),

            // 创世行 4：Age 属性定义。
            new(
                Id: UserAgeTypeId,
                TypeId: MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: UserTypeId,
                Value: "Age"),

            // 创世行 5：CreatedAt 属性定义。
            new(
                Id: UserCreatedAtTypeId,
                TypeId: MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: UserTypeId,
                Value: "CreatedAt"),

            // 创世行 6：User 引擎路由指令——Value 用键值对编码路由目标与目标实体。
            new(
                Id: UserEngineId,
                TypeId: MetaEngineTypeId,
                Role: UniversalEntity.RoleEngine,
                ParentId: null,
                Value: $"RouteTarget=UserRepository|TargetTypeId={UserTypeId}"),

            // 创世行 7：存储引擎选择指令——决定物理引擎落位（改这一行 = 换引擎）。
            new(
                Id: StorageProviderEngineId,
                TypeId: MetaEngineTypeId,
                Role: UniversalEntity.RoleEngine,
                ParentId: null,
                Value: "StorageProvider=SqlServer"),

            // 创世行 8：加密策略——Value 列透明加密，算法与密钥引用全是数据。
            new(
                Id: EncryptPolicyRowId,
                TypeId: MetaEngineTypeId,
                Role: UniversalEntity.RoleSecurity,
                ParentId: null,
                Value: "Policy=EncryptValue|Target=Data|Algo=AesGcm|KeyRef=K1|Enabled=true"),

            // 创世行 9：租户隔离策略——租户 Id 参与加密认证绑定。
            new(
                Id: TenantPolicyRowId,
                TypeId: MetaEngineTypeId,
                Role: UniversalEntity.RoleSecurity,
                ParentId: null,
                Value: "Policy=TenantIsolation|Enabled=true"),

            // 创世行 10：批量限流策略——防内存炸弹。
            new(
                Id: RateLimitPolicyRowId,
                TypeId: MetaEngineTypeId,
                Role: UniversalEntity.RoleSecurity,
                ParentId: null,
                Value: "Policy=RateLimit|Target=BulkInsert|Max=100000")
        };

        // 单事务批量写入创世行。
        await _repository.InsertBatchAsync(genesisRows, cancellationToken);
    }

    /// <summary>
    /// 把同构池中的 Role=1/4 行解析为内存元数据结构。
    /// </summary>
    /// <param name="rows">Role=1 与 Role=4 的行集合。</param>
    private void ParseMetaRows(IReadOnlyList<UniversalEntity> rows)
    {
        // 第一遍：解析实体类型定义行（Role=1 且 ParentId 为空）。
        foreach (var row in rows)
        {
            // 只处理元数据行。
            if (row.Role != UniversalEntity.RoleMeta)
            {
                // 跳过非元数据行。
                continue;
            }

            // ParentId 为空是"类型"行。
            if (row.ParentId is null)
            {
                // 构造类型元数据——TypeId 即行 Id，类型名即 Value。
                var metaType = new MetaType
                {
                    // 类型 TypeId——实例行通过 TypeId 指向它。
                    TypeId = row.Id,
                    // 类型名。
                    TypeName = row.Value ?? string.Empty,
                    // 属性字典先置空，第二遍填充。
                    PropertyTypeIds = new Dictionary<string, Guid>()
                };

                // 加入类型缓存。
                _types.TryAdd(metaType.TypeId, metaType);
            }
        }

        // 第二遍：解析属性定义行（Role=1 且 ParentId 指向某个类型）。
        foreach (var row in rows)
        {
            // 只处理元数据行。
            if (row.Role != UniversalEntity.RoleMeta)
            {
                // 跳过非元数据行。
                continue;
            }

            // ParentId 非空才是属性行。
            if (row.ParentId is null)
            {
                // 跳过类型行（已在第一遍处理）。
                continue;
            }

            // 找到属性所属的类型——类型行必须已注册。
            if (!_types.TryGetValue(row.ParentId.Value, out var ownerType))
            {
                // 类型缺失时容错跳过——真实部署应告警。
                continue;
            }

            // 属性名 → 属性 TypeId 写入所属类型的字典。
            ownerType.PropertyTypeIds[row.Value ?? string.Empty] = row.Id;
        }

        // 第三遍：解析引擎指令行（Role=4）——包含路由指令与一般指令（如 StorageProvider）。
        foreach (var row in rows)
        {
            // 只处理引擎指令行。
            if (row.Role != UniversalEntity.RoleEngine)
            {
                // 跳过非指令行。
                continue;
            }

            // 解析 Value 中的键值对——格式 "Key1=V1|Key2=V2"。
            var command = ParseEngineCommand(row.Value);

            // 路由字段为可选——一般指令行（如 StorageProvider）不含 RouteTarget。
            command.TryGetValue("RouteTarget", out var routeTarget);

            // 目标实体 TypeId 同样可选——预赋值避免短路求值下的未赋值编译错误。
            var targetTypeId = Guid.Empty;
            var hasValidTarget = command.TryGetValue("TargetTypeId", out var targetTypeIdText)
                && Guid.TryParse(targetTypeIdText, out targetTypeId);

            // 构造引擎指令元数据——路由字段缺省为空值，不再静默丢弃一般指令行。
            var metaEngine = new MetaEngine
            {
                // 指令行 Id。
                TypeId = row.Id,
                // 路由目标类型名——无路由语义时为空串。
                RouteTarget = routeTarget ?? string.Empty,
                // 指令作用的实体 TypeId——无路由语义时为 Guid.Empty。
                TargetEntityTypeId = hasValidTarget ? targetTypeId : Guid.Empty,
                // 完整键值对集合——承载 StorageProvider 等扩展指令。
                Settings = command
            };

            // 发现存储引擎选择指令时记录引擎名。
            if (command.TryGetValue("StorageProvider", out var providerName))
            {
                // 记录到私有字段。
                _storageProviderName = providerName;
            }

            // 加入引擎缓存——所有 Role=4 行都进入缓存，不再按路由字段过滤。
            _engines.TryAdd(metaEngine.TypeId, metaEngine);
        }
    }

    /// <summary>
    /// 获取池中指令选择的存储引擎名——无指令时默认 SqlServer。
    /// </summary>
    /// <returns>引擎名：SqlServer / InMemory / FasterKv。</returns>
    public string GetStorageProviderName()
    {
        // 无指令回退保守默认。
        return _storageProviderName ?? "SqlServer";
    }

    /// <summary>
    /// 获取指定实体类型的 CSharpType 指令——类名同化的数据源。
    /// </summary>
    /// <remarks>
    /// 有 CSharpType 指令的实体走强类型反射轨；没有的实体由 <see cref="UniversalObject"/> 动态承载。
    /// 类名是指令行里的一条普通键值对——改一行数据即可让实体绑定或解绑 C# 类。
    /// </remarks>
    /// <param name="entityTypeId">实体类型的 Meta TypeId。</param>
    /// <returns>C# 类型全名；未绑定返回 null。</returns>
    public string? GetCSharpTypeName(Guid entityTypeId)
    {
        // 遍历全部引擎指令行。
        foreach (var engine in _engines.Values)
        {
            // 只看作用于该实体类型的指令。
            if (engine.TargetEntityTypeId != entityTypeId)
            {
                // 跳过。
                continue;
            }

            // 指令带 CSharpType 键时返回类名。
            if (engine.Settings.TryGetValue("CSharpType", out var csharpTypeName))
            {
                // 返回类名。
                return csharpTypeName;
            }
        }

        // 未绑定任何 C# 类。
        return null;
    }

    /// <summary>
    /// 获取指定实体类型的存储模式指令——快照模式与拆解图模式的分轨依据。
    /// </summary>
    /// <remarks>
    /// StorageMode=Snapshot → 整对象序列化进实体行 Value（"直接煮熟"，点查零拆解零组装）；
    /// 无指令或 Graph → 实体行 + 属性行拆解（保留图遍历能力）。
    /// 存储策略本身是数据——改一行指令即切换实体的存储形态。
    /// </remarks>
    /// <param name="entityTypeId">实体类型的 Meta TypeId。</param>
    /// <returns>存储模式名（"Snapshot"）；未配置返回 null（默认图模式）。</returns>
    public string? GetStorageMode(Guid entityTypeId)
    {
        // 遍历全部引擎指令行。
        foreach (var engine in _engines.Values)
        {
            // 只看作用于该实体类型的指令。
            if (engine.TargetEntityTypeId != entityTypeId)
            {
                // 跳过。
                continue;
            }

            // 指令带 StorageMode 键时返回模式名。
            if (engine.Settings.TryGetValue("StorageMode", out var mode))
            {
                // 返回模式。
                return mode;
            }
        }

        // 未配置——默认图模式。
        return null;
    }

    /// <summary>
    /// 解析 Role=5 安全策略行——策略名 → 键值对集合。
    /// </summary>
    /// <param name="rows">Role=5 行集合。</param>
    private void ParseSecurityPolicies(IReadOnlyList<UniversalEntity> rows)
    {
        // 逐行解析。
        foreach (var row in rows)
        {
            // 解析键值对——与引擎指令同格式 "Key1=V1|Key2=V2"。
            var settings = ParseEngineCommand(row.Value);

            // 缺 Policy 键的行视为非法，跳过。
            if (!settings.TryGetValue("Policy", out var policyName) || string.IsNullOrWhiteSpace(policyName))
            {
                // 跳过非法策略行。
                continue;
            }

            // 以策略名为键缓存——同名策略以最后一条为准（池中按 Id 升序，尾部覆盖头部）。
            _securityPolicies[policyName] = settings;
        }
    }

    /// <summary>
    /// 按策略名查询安全策略——安全规则同化后的唯一读取入口。
    /// </summary>
    /// <param name="policyName">策略名（如 EncryptValue / TenantIsolation / RateLimit）。</param>
    /// <returns>策略键值对集合；未配置返回 null。</returns>
    public IReadOnlyDictionary<string, string>? GetSecurityPolicy(string policyName)
    {
        // 从策略缓存取值。
        return _securityPolicies.TryGetValue(policyName, out var settings) ? settings : null;
    }

    /// <summary>
    /// 解析引擎指令的键值对文本——格式为 "Key1=V1|Key2=V2"。
    /// </summary>
    /// <param name="commandText">指令文本；可为 null。</param>
    /// <returns>键值对字典；文本为空或含空段时返回部分解析结果。</returns>
    private static Dictionary<string, string> ParseEngineCommand(string? commandText)
    {
        // 结果字典。
        var result = new Dictionary<string, string>(StringComparer.Ordinal);

        // 空文本直接返回空字典。
        if (string.IsNullOrWhiteSpace(commandText))
        {
            // 返回空字典。
            return result;
        }

        // 按竖线切分键值段。
        var segments = commandText.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // 逐段解析。
        foreach (var segment in segments)
        {
            // 每段按第一个等号切成键和值——限制 2 段防止值里含等号被误切。
            var parts = segment.Split('=', 2);

            // 缺等号的段格式非法，跳过。
            if (parts.Length != 2)
            {
                // 跳过非法段。
                continue;
            }

            // 键去空白后入字典。
            result[parts[0].Trim()] = parts[1].Trim();
        }

        // 返回解析结果。
        return result;
    }

    // ============================================================================
    // 查询
    // ============================================================================

    /// <summary>
    /// 按 TypeId 查实体类型元数据。
    /// </summary>
    /// <param name="typeId">实体或属性的 TypeId。</param>
    /// <returns>找到返回 <see cref="MetaType"/>；未找到返回 <see langword="null"/>。</returns>
    public MetaType? GetTypeByTypeId(Guid typeId)
    {
        // 从 ConcurrentDictionary 尝试取值。
        _types.TryGetValue(typeId, out var meta);

        // 返回结果。
        return meta;
    }

    /// <summary>
    /// 按属性 TypeId 反查属性名——反向挂载时用。
    /// </summary>
    /// <param name="propertyTypeId">属性的 TypeId。</param>
    /// <returns>属性名（如 "Name"）；未找到返回 <see langword="null"/>。</returns>
    public string? GetPropertyNameByTypeId(Guid propertyTypeId)
    {
        // 遍历所有实体类型——原型期实体数量很少，线性遍历足够。
        foreach (var metaType in _types.Values)
        {
            // 在 PropertyTypeIds 里找匹配的 value。
            foreach (var kv in metaType.PropertyTypeIds)
            {
                // 值匹配则返回 key（属性名）。
                if (kv.Value == propertyTypeId)
                {
                    // 返回属性名。
                    return kv.Key;
                }
            }
        }

        // 没找到返回 null。
        return null;
    }

    /// <summary>
    /// 按实体 TypeId 查引擎路由目标。
    /// </summary>
    /// <param name="entityTypeId">实体的 TypeId。</param>
    /// <returns>路由目标类型名；未找到返回 <see langword="null"/>。</returns>
    public string? GetEngineRouteTarget(Guid entityTypeId)
    {
        // 遍历所有引擎元数据，匹配 TargetEntityTypeId。
        foreach (var engine in _engines.Values)
        {
            // 匹配到则返回路由目标。
            if (engine.TargetEntityTypeId == entityTypeId)
            {
                // 返回路由目标。
                return engine.RouteTarget;
            }
        }

        // 没找到返回 null。
        return null;
    }

    /// <summary>
    /// 全量导出——供测试断言验证缓存内容。
    /// </summary>
    public IReadOnlyDictionary<Guid, MetaType> GetAllTypes() => _types;
}
