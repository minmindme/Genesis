// ANCHOR: dal.cli.command-executor - 命令执行器：哑巴壳子的大脑，步骤是插件、编排是数据
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Emoking.DAL;

/// <summary>
/// CLI 命令执行器——匹配池中 Role=7 命令定义，按 Steps 编排执行内置原子步骤。
/// </summary>
/// <remarks>
/// 同构边界（与第 10 轮 PolicyDrivenStore 同一哲学）：
///   - **被同化的**：命令树、参数表、步骤编排顺序——全部是池中 Role=7 数据；
///   - **不能被同化的**：原子步骤的实现（插元数据、生成代理、热重载）——它们是引擎的"手和脚"。
/// 新增一种命令 = 插一行 Role=7 + （可选）注册新步骤；CLI 壳子零改动。
/// </remarks>
public sealed class UniversalCommandExecutor
{
    // 单表仓储——步骤落库的执行通道。
    private readonly UniversalEntityRepository _repository;

    // 元数据缓存——命令定义来源与热重载目标。
    private readonly MetaCache _metaCache;

    // 代码代理输出目录。
    private readonly string _outputDirectory;

    // 步骤注册表：步骤名 → 执行逻辑。
    private readonly Dictionary<string, Func<StepContext, Task>> _steps;

    /// <summary>
    /// 初始化命令执行器。
    /// </summary>
    /// <param name="repository">单表仓储。</param>
    /// <param name="metaCache">元数据缓存（须已 LoadAsync）。</param>
    /// <param name="outputDirectory">代码代理输出目录（默认当前目录）。</param>
    public UniversalCommandExecutor(
        UniversalEntityRepository repository,
        MetaCache metaCache,
        string outputDirectory = ".")
    {
        // 保存依赖。
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _metaCache = metaCache ?? throw new ArgumentNullException(nameof(metaCache));
        _outputDirectory = outputDirectory ?? ".";

        // 注册内置原子步骤——步骤名与 Role=7 行的 Steps 字段对应。
        _steps = new Dictionary<string, Func<StepContext, Task>>(StringComparer.Ordinal)
        {
            // 插入类型元数据（类型行 + 属性行）。
            ["InsertTypeMeta"] = InsertTypeMetaAsync,

            // 写入存储模式指令。
            ["SetStorageMode"] = SetStorageModeAsync,

            // 写入类名绑定指令。
            ["BindCSharpType"] = BindCSharpTypeAsync,

            // 生成强类型代理文件。
            ["GenerateProxy"] = GenerateProxyAsync,

            // 元数据热重载。
            ["ReloadCache"] = ReloadCacheAsync,

            // 一把导入整套系统定义（实体 + 边类型 + UI）——终局演示步骤。
            ["ImportSystem"] = ImportSystemAsync
        };
    }

    /// <summary>
    /// 步骤执行上下文——承载命令定义、解析后的参数与步骤间产物。
    /// </summary>
    public sealed class StepContext
    {
        /// <summary>命令定义键值对（Role=7 行）。</summary>
        public IReadOnlyDictionary<string, string> Command { get; init; } = new Dictionary<string, string>();

        /// <summary>用户输入参数（K=V 解析结果）。</param>
        public IReadOnlyDictionary<string, string> Args { get; init; } = new Dictionary<string, string>();

        /// <summary>本命令生成的待落库行——步骤间共享。</summary>
        public List<UniversalEntity> PendingRows { get; } = new();

        /// <summary>新建类型的 TypeId——步骤间传递。</summary>
        public Guid NewTypeId { get; set; }

        /// <summary>输出流（CLI 回显）。</summary>
        public TextWriter Output { get; init; } = System.Console.Out;
    }

    /// <summary>
    /// 执行一条 CLI 命令——匹配 Role=7 定义并按 Steps 编排。
    /// </summary>
    /// <param name="argv">命令行参数：["run", "命令名", "K=V", ...]。</param>
    /// <returns>执行报告文本。</returns>
    /// <exception cref="InvalidOperationException">命令未注册、步骤未注册或参数缺失时抛出。</exception>
    public async Task<string> RunAsync(IReadOnlyList<string> argv)
    {
        // 参数形态校验：run <Command> K=V...
        if (argv.Count < 2 || !string.Equals(argv[0], "run", StringComparison.OrdinalIgnoreCase))
        {
            // 抛出用法异常。
            throw new InvalidOperationException("用法：run <命令名> [K=V ...]。可用命令：" + string.Join("/", _metaCache.GetCommandNames()));
        }

        // 命令名。
        var commandName = argv[1];

        // 从池中查命令定义——命令树即数据。
        var command = _metaCache.GetCommand(commandName);

        // 未注册的命令显式失败——部署/配置语义（网关按此类型自愈 reload，第 25 轮收窄）。
        if (command is null)
        {
            // 抛出未注册异常。
            throw new MetadataNotRegisteredException(
                $"命令 '{commandName}' 未在池中注册（Role=7）。已注册：{string.Join("/", _metaCache.GetCommandNames())}。");
        }

        // 解析用户参数 K=V。
        var args = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in argv.Skip(2))
        {
            // 按第一个等号切分。
            var parts = token.Split('=', 2);

            // 缺等号的参数拒绝。
            if (parts.Length != 2)
            {
                // 抛出参数格式异常。
                throw new InvalidOperationException($"参数 '{token}' 缺少 '='——格式应为 K=V。");
            }

            // 收集。
            args[parts[0]] = parts[1];
        }

        // 构造步骤上下文。
        var context = new StepContext { Command = command, Args = args };

        // 解析 Steps 编排——步骤分隔符用分号（竖线已被键值对格式占用）。
        var steps = command.TryGetValue("Steps", out var stepsText)
            ? stepsText.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : Array.Empty<string>();

        // 无步骤的命令无事可做。
        if (steps.Length == 0)
        {
            // 返回提示。
            return $"命令 '{commandName}' 未定义 Steps，已跳过执行。";
        }

        // 按序执行步骤。
        foreach (var stepName in steps)
        {
            // 查步骤注册表。
            if (!_steps.TryGetValue(stepName, out var step))
            {
                // 未注册的步骤显式失败——部署/配置语义（网关按此类型自愈 reload，第 25 轮收窄）。
                throw new MetadataNotRegisteredException(
                    $"命令 '{commandName}' 的步骤 '{stepName}' 未注册。已注册：{string.Join("/", _steps.Keys)}。");
            }

            // 执行步骤。
            await step(context);
        }

        // 汇总报告。
        return $"命令 '{commandName}' 执行完成：{steps.Length} 个步骤，生成 {context.PendingRows.Count} 行元数据。";
    }

    /// <summary>
    /// 步骤：InsertTypeMeta——插入类型行与属性行（Role=1）。
    /// </summary>
    /// <param name="context">步骤上下文。</param>
    private async Task InsertTypeMetaAsync(StepContext context)
    {
        // 取类型名参数。
        var typeName = RequireArg(context, "TypeName");

        // 类型行 Id——新 Guid。
        context.NewTypeId = Guid.NewGuid();

        // 类型行。
        context.PendingRows.Add(new UniversalEntity(
            Id: context.NewTypeId,
            TypeId: MetaCache.MetaTypeTypeId,
            Role: UniversalEntity.RoleMeta,
            ParentId: null,
            Value: typeName));

        // 解析属性行——第 22 轮双格式：Props 支持 JSON 数组（属性名含冒号等分隔符）与旧逗号格式。
        if (context.Args.TryGetValue("Props", out var propsText) && !string.IsNullOrWhiteSpace(propsText))
        {
            // JSON 数组分支——"JSON:[{\"Name\":..,\"Type\":..}, ...]"。
            if (propsText.StartsWith(MetadataJson.Prefix, StringComparison.Ordinal))
            {
                // 去前缀反序列化。
                var propList = System.Text.Json.JsonSerializer.Deserialize<List<Dictionary<string, string>>>(
                    propsText[MetadataJson.Prefix.Length..]);

                // 非法 JSON 显式失败（fail fast，不静默半解析）。
                if (propList is null)
                {
                    // 抛出格式异常。
                    throw new InvalidOperationException($"Props JSON 格式非法：{propsText}");
                }

                // 逐属性落库——编码路由自动选择 JSON 或旧格式。
                foreach (var prop in propList)
                {
                    // 取属性名（兼容 Name/name）。
                    var propName = prop.TryGetValue("Name", out var nameValue) ? nameValue
                        : prop.TryGetValue("name", out var nameLower) ? nameLower
                        : string.Empty;

                    // 属性名必填。
                    if (string.IsNullOrWhiteSpace(propName))
                    {
                        // 跳过空属性。
                        continue;
                    }

                    // 取类型（兼容 Type/type；未声明为 null）。
                    var propType = prop.TryGetValue("Type", out var typeValue) ? typeValue
                        : prop.TryGetValue("type", out var typeLower) ? typeLower
                        : null;

                    // 落库——EncodeProperty 自动路由（含分隔符的属性名 JSON 化）。
                    context.PendingRows.Add(new UniversalEntity(
                        Id: Guid.NewGuid(),
                        TypeId: MetaCache.MetaTypeTypeId,
                        Role: UniversalEntity.RoleMeta,
                        ParentId: context.NewTypeId,
                        Value: MetadataJson.EncodeProperty(propName, propType)));
                }

                // JSON 分支处理完毕。
                goto SaveRows;
            }

            // 旧格式分支——逐属性逗号拆分，decode-encode 往返（含冒号的属性名自动 JSON 化落库）。
            foreach (var propSpec in propsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // 属性规格解码（旧格式按第一个冒号切；无冒号 = 未声明类型）。
                var (propName, propType) = MetadataJson.DecodeProperty(propSpec);

                // 属性名必填。
                if (string.IsNullOrWhiteSpace(propName))
                {
                    // 跳过空属性。
                    continue;
                }

                // 落库——第 17 轮格式（含分隔符时自动 JSON 化，DataType 显式化语义不变）。
                context.PendingRows.Add(new UniversalEntity(
                    Id: Guid.NewGuid(),
                    TypeId: MetaCache.MetaTypeTypeId,
                    Role: UniversalEntity.RoleMeta,
                    ParentId: context.NewTypeId,
                    Value: MetadataJson.EncodeProperty(propName, propType)));
            }
        }

    SaveRows:
        // 落库。
        await _repository.InsertBatchAsync(context.PendingRows);
    }

    /// <summary>
    /// 步骤：SetStorageMode——写入 StorageMode 指令行（Role=4）。
    /// </summary>
    /// <param name="context">步骤上下文。</param>
    private async Task SetStorageModeAsync(StepContext context)
    {
        // 取模式参数。
        var mode = RequireArg(context, "Mode");

        // 写入指令行——EncodePairs 自动路由（mode 含分隔符时 JSON 化，第 24 轮写侧全面化）。
        var instructionRow = new UniversalEntity(
            Id: Guid.NewGuid(),
            TypeId: MetaCache.MetaEngineTypeId,
            Role: UniversalEntity.RoleEngine,
            ParentId: null,
            Value: MetadataJson.EncodePairs(new Dictionary<string, string>
            {
                // 存储模式。
                ["StorageMode"] = mode,
                // 作用目标实体。
                ["TargetTypeId"] = context.NewTypeId.ToString()
            }));

        // 落库。
        await _repository.InsertBatchAsync(new[] { instructionRow });

        // 记录到待缓冲（不计入元数据统计口径——指令行单独归类）。
        context.PendingRows.Add(instructionRow);
    }

    /// <summary>
    /// 步骤：BindCSharpType——写入 CSharpType 指令行（Role=4）。
    /// </summary>
    /// <param name="context">步骤上下文。</param>
    private async Task BindCSharpTypeAsync(StepContext context)
    {
        // 取类名参数。
        var csharpType = RequireArg(context, "CSharpType");

        // 写入指令行。
        var instructionRow = new UniversalEntity(
            Id: Guid.NewGuid(),
            TypeId: MetaCache.MetaEngineTypeId,
            Role: UniversalEntity.RoleEngine,
            ParentId: null,
            Value: MetadataJson.EncodePairs(new Dictionary<string, string>
            {
                // 类名绑定。
                ["CSharpType"] = csharpType,
                // 作用目标实体。
                ["TargetTypeId"] = context.NewTypeId.ToString()
            }));

        // 落库。
        await _repository.InsertBatchAsync(new[] { instructionRow });

        // 记录。
        context.PendingRows.Add(instructionRow);
    }

    /// <summary>
    /// 步骤：ImportSystem——解析系统定义 JSON，一把生成"实体 + 边类型 + UI"全套元数据行。
    /// </summary>
    /// <remarks>
    /// SystemJson 三段式契约：
    ///   Entities: [{ Name, Mode, Props }]（Props 为 "字段:类型" 逗号串；Mode 非 Graph 时生成 StorageMode 指令）
    ///   EdgeTypes: [{ Name, From, To }]（自动生成正向 + 反向两个类型行与 EdgeType 指令行）
    ///   Uis: [{ Name, Entity, Fields, Action }]（Role=8 通用渲染器描述）
    /// </remarks>
    /// <param name="context">步骤上下文。</param>
    private async Task ImportSystemAsync(StepContext context)
    {
        // 取系统定义 JSON。
        var systemJson = RequireArg(context, "SystemJson");

        // 反序列化——属性名大小写不敏感。
        var definition = System.Text.Json.JsonSerializer.Deserialize<SystemDefinition>(systemJson, new System.Text.Json.JsonSerializerOptions
        {
            // 大小写不敏感。
            PropertyNameCaseInsensitive = true
        });

        // 定义非法时失败。
        if (definition is null)
        {
            // 抛出异常。
            throw new InvalidOperationException("SystemJson 不是合法的系统定义 JSON。");
        }

        // 类型名 → TypeId 映射——供边类型的 From/To 与 UI 的 Entity 引用。
        var typeIds = new Dictionary<string, Guid>(StringComparer.Ordinal);

        // 第一段：实体。
        foreach (var entity in definition.Entities ?? new List<EntityDef>())
        {
            // 类型行 Id。
            var typeId = Guid.NewGuid();

            // 记录映射。
            typeIds[entity.Name] = typeId;

            // 类型行。
            context.PendingRows.Add(new UniversalEntity(
                Id: typeId,
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: entity.Name));

            // 属性行——第 22 轮：decode-encode 往返落库（旧 "字段:类型" 语义不变；含分隔符的属性名自动 JSON 化）。
            foreach (var propSpec in (entity.Props ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // 跳过空属性。
                if (propSpec.Length == 0)
                {
                    // 跳过。
                    continue;
                }

                // 属性规格解码——按前缀路由双格式。
                var (propName, propType) = MetadataJson.DecodeProperty(propSpec);

                // 属性行落库——EncodeProperty 自动路由（含冒号属性名 JSON 化）。
                context.PendingRows.Add(new UniversalEntity(
                    Id: Guid.NewGuid(),
                    TypeId: MetaCache.MetaTypeTypeId,
                    Role: UniversalEntity.RoleMeta,
                    ParentId: typeId,
                    Value: MetadataJson.EncodeProperty(propName, propType)));
            }

            // 存储模式指令——非 Graph 模式时生成。
            if (!string.IsNullOrEmpty(entity.Mode) && !string.Equals(entity.Mode, "Graph", StringComparison.OrdinalIgnoreCase))
            {
                // 指令行。
                context.PendingRows.Add(new UniversalEntity(
                    Id: Guid.NewGuid(),
                    TypeId: MetaCache.MetaEngineTypeId,
                    Role: UniversalEntity.RoleEngine,
                    ParentId: null,
                    Value: MetadataJson.EncodePairs(new Dictionary<string, string>
                    {
                        // 存储模式。
                        ["StorageMode"] = entity.Mode,
                        // 作用目标实体。
                        ["TargetTypeId"] = typeId.ToString()
                    })));
            }
        }

        // 第二段：边类型——正向 + 反向两个类型行与两条 EdgeType 指令行。
        foreach (var edge in definition.EdgeTypes ?? new List<EdgeTypeDef>())
        {
            // From/To 实体必须已定义。
            if (!typeIds.TryGetValue(edge.From, out var fromTypeId) || !typeIds.TryGetValue(edge.To, out var toTypeId))
            {
                // 抛出引用异常。
                throw new InvalidOperationException(
                    $"边类型 '{edge.Name}' 引用的实体 '{edge.From}'/'{edge.To}' 未在 Entities 段定义。");
            }

            // 正向类型行 Id。
            var forwardTypeId = Guid.NewGuid();

            // 反向类型行 Id——命名约定：名称 + "By"。
            var reverseTypeId = Guid.NewGuid();
            var reverseName = edge.Name + "By";

            // 正向类型行。
            context.PendingRows.Add(new UniversalEntity(
                Id: forwardTypeId,
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: edge.Name));

            // 反向类型行。
            context.PendingRows.Add(new UniversalEntity(
                Id: reverseTypeId,
                TypeId: MetaCache.MetaTypeTypeId,
                Role: UniversalEntity.RoleMeta,
                ParentId: null,
                Value: reverseName));

            // 正向 EdgeType 指令——EncodePairs 自动路由（边名含分隔符时 JSON 化）。
            context.PendingRows.Add(new UniversalEntity(
                Id: Guid.NewGuid(),
                TypeId: MetaCache.MetaEngineTypeId,
                Role: UniversalEntity.RoleEngine,
                ParentId: null,
                Value: MetadataJson.EncodePairs(new Dictionary<string, string>
                {
                    // 边类型名。
                    ["EdgeType"] = edge.Name,
                    // 作用目标边类型。
                    ["TargetTypeId"] = forwardTypeId.ToString()
                })));

            // 反向 EdgeType 指令。
            context.PendingRows.Add(new UniversalEntity(
                Id: Guid.NewGuid(),
                TypeId: MetaCache.MetaEngineTypeId,
                Role: UniversalEntity.RoleEngine,
                ParentId: null,
                Value: MetadataJson.EncodePairs(new Dictionary<string, string>
                {
                    // 反向边类型名。
                    ["EdgeType"] = reverseName,
                    // 作用目标边类型。
                    ["TargetTypeId"] = reverseTypeId.ToString()
                })));

            // 记录正反向 TypeId——InsertEdge 按名称约定查找。
            typeIds[edge.Name] = forwardTypeId;
            typeIds[reverseName] = reverseTypeId;
        }

        // 第三段：UI Schema。
        foreach (var ui in definition.Uis ?? new List<UiDef>())
        {
            // Role=8 行——EncodePairs 自动路由（UI 名/字段值含分隔符时 JSON 化）。
            context.PendingRows.Add(new UniversalEntity(
                Id: Guid.NewGuid(),
                TypeId: MetaCache.MetaEngineTypeId,
                Role: UniversalEntity.RoleUi,
                ParentId: null,
                Value: MetadataJson.EncodePairs(new Dictionary<string, string>
                {
                    // UI 名。
                    ["UI"] = ui.Name,
                    // 控件类型。
                    ["Type"] = "Form",
                    // 绑定实体。
                    ["Entity"] = ui.Entity,
                    // 字段描述。
                    ["Fields"] = ui.Fields,
                    // 提交动作。
                    ["Action"] = ui.Action
                })));
        }

        // 整包落库。
        await _repository.InsertBatchAsync(context.PendingRows);
    }

    /// <summary>
    /// 系统定义 DTO——ImportSystem 的 JSON 契约。
    /// </summary>
    private sealed class SystemDefinition
    {
        /// <summary>实体定义列表。</summary>
        public List<EntityDef>? Entities { get; set; }

        /// <summary>边类型定义列表。</summary>
        public List<EdgeTypeDef>? EdgeTypes { get; set; }

        /// <summary>UI 定义列表。</summary>
        public List<UiDef>? Uis { get; set; }
    }

    /// <summary>
    /// 实体定义——名称、存储模式、属性规格。
    /// </summary>
    private sealed class EntityDef
    {
        /// <summary>实体类型名。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>存储模式（Snapshot/Graph）。</summary>
        public string? Mode { get; set; }

        /// <summary>属性规格串（"字段:类型" 逗号分隔）。</summary>
        public string? Props { get; set; }
    }

    /// <summary>
    /// 边类型定义——名称与两端实体。
    /// </summary>
    private sealed class EdgeTypeDef
    {
        /// <summary>边类型名。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>起点实体名。</summary>
        public string From { get; set; } = string.Empty;

        /// <summary>终点实体名。</summary>
        public string To { get; set; } = string.Empty;
    }

    /// <summary>
    /// UI 定义——名称、实体、字段、提交动作。
    /// </summary>
    private sealed class UiDef
    {
        /// <summary>UI 名。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>指向的实体名。</summary>
        public string Entity { get; set; } = string.Empty;

        /// <summary>字段规格串。</summary>
        public string Fields { get; set; } = string.Empty;

        /// <summary>提交动作名。</summary>
        public string Action { get; set; } = "InsertData";
    }

    /// <summary>
    /// 步骤：GenerateProxy——按 Props 参数生成强类型代理文件。
    /// </summary>
    /// <param name="context">步骤上下文。</param>
    private Task GenerateProxyAsync(StepContext context)
    {
        // 类型名。
        var typeName = RequireArg(context, "TypeName");

        // 属性规格。
        var propsText = context.Args.TryGetValue("Props", out var props) ? props : string.Empty;

        // 组装代理代码。
        var builder = new StringBuilder();

        // 文件头。
        builder.AppendLine("// 由 Genesis CLI 自动生成——强类型代理（快照模式 schema 即类定义）。");
        builder.AppendLine("// ANCHOR: generated.proxy." + typeName.ToLowerInvariant() + " - 自动生成的强类型代理");

        // 命名空间与类骨架。
        builder.AppendLine("using System;");
        builder.AppendLine();
        builder.AppendLine("namespace Genesis.Generated;");
        builder.AppendLine();
        builder.AppendLine($"public sealed class {typeName}");

        // 类体。
        builder.AppendLine("{");

        // 逐属性生成。
        foreach (var propSpec in propsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // 属性名:类型。
            var propParts = propSpec.Split(':');

            // 属性名。
            var propName = propParts[0].Trim();

            // CLR 类型——默认 string。
            var clrType = propParts.Length > 1 ? MapClrType(propParts[1].Trim()) : "string";

            // 属性。
            builder.AppendLine($"    public {clrType} {propName} {{ get; set; }}");
        }

        // 类结束。
        builder.AppendLine("}");

        // 保证输出目录存在。
        Directory.CreateDirectory(_outputDirectory);

        // 写文件。
        File.WriteAllText(Path.Combine(_outputDirectory, typeName + ".cs"), builder.ToString());

        // 回显。
        context.Output.WriteLine($"已生成代理：{Path.Combine(_outputDirectory, typeName + ".cs")}");

        // 完成。
        return Task.CompletedTask;
    }

    /// <summary>
    /// 步骤：ReloadCache——元数据热重载。
    /// </summary>
    /// <param name="context">步骤上下文。</param>
    private async Task ReloadCacheAsync(StepContext context)
    {
        // 热重载。
        await _metaCache.LoadAsync();

        // 回显。
        context.Output.WriteLine("MetaCache 已热重载。");
    }

    /// <summary>
    /// 取必填参数——缺失时抛出。
    /// </summary>
    /// <param name="context">步骤上下文。</param>
    /// <param name="name">参数名。</param>
    /// <returns>参数值。</returns>
    private static string RequireArg(StepContext context, string name)
    {
        // 查参数。
        if (context.Args.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            // 返回。
            return value;
        }

        // 缺参显式失败。
        throw new InvalidOperationException(
            $"命令 '{context.Command["Command"]}' 缺少必填参数 '{name}'。");
    }

    /// <summary>
    /// 简化类型名 → CLR 类型映射。
    /// </summary>
    /// <param name="shortName">简短类型名（String/Decimal/Int/DateTime/Bool/Guid）。</param>
    /// <returns>CLR 类型名。</returns>
    private static string MapClrType(string shortName)
    {
        // 映射表——大小写不敏感。
        return shortName.ToLowerInvariant() switch
        {
            // 字符串。
            "string" => "string",

            // 小数。
            "decimal" => "decimal",

            // 整数。
            "int" => "int",

            // 长整数。
            "long" => "long",

            // 布尔。
            "bool" => "bool",

            // 时间。
            "datetime" => "DateTime",

            // Guid。
            "guid" => "Guid",

            // 未知类型按字符串兜底。
            _ => "string"
        };
    }
}
