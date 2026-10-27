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
            ["ReloadCache"] = ReloadCacheAsync
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

        // 未注册的命令显式失败。
        if (command is null)
        {
            // 抛出未注册异常。
            throw new InvalidOperationException(
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
                // 未注册的步骤显式失败。
                throw new InvalidOperationException(
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

        // 解析属性行——Props 格式 "Name:String,Amount:Decimal"。
        if (context.Args.TryGetValue("Props", out var propsText) && !string.IsNullOrWhiteSpace(propsText))
        {
            // 逐属性拆分。
            foreach (var propSpec in propsText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // 属性规格按冒号切：属性名:类型。
                var propParts = propSpec.Split(':');

                // 属性名必填。
                if (string.IsNullOrWhiteSpace(propParts[0]))
                {
                    // 跳过空属性。
                    continue;
                }

                // 第 17 轮格式：池中落 "Name:Type"（DataType 显式化，代理与组装共用）。
                context.PendingRows.Add(new UniversalEntity(
                    Id: Guid.NewGuid(),
                    TypeId: MetaCache.MetaTypeTypeId,
                    Role: UniversalEntity.RoleMeta,
                    ParentId: context.NewTypeId,
                    Value: propSpec));
            }
        }

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

        // 写入指令行。
        var instructionRow = new UniversalEntity(
            Id: Guid.NewGuid(),
            TypeId: MetaCache.MetaEngineTypeId,
            Role: UniversalEntity.RoleEngine,
            ParentId: null,
            Value: $"StorageMode={mode}|TargetTypeId={context.NewTypeId}");

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
            Value: $"CSharpType={csharpType}|TargetTypeId={context.NewTypeId}");

        // 落库。
        await _repository.InsertBatchAsync(new[] { instructionRow });

        // 记录。
        context.PendingRows.Add(instructionRow);
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
