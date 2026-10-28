// ANCHOR: gateway.program - 哑巴 HTTP 路由器：整个后端只有一个入口
using Emoking.DAL;

// 构建网关——WebRoot 指向 Genesis.Web/wwwroot（同构前端资产独立目录，CreateBuilder 选项方式设置）。
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    // 保留命令行参数（--urls 等）。
    Args = args,
    // WebRoot 指向 Genesis.Web/wwwroot。
    WebRootPath = Path.Combine("..", "Genesis.Web", "wwwroot")
});

// 清空默认日志提供程序——沙箱无 Windows 事件日志写权限（B30），控制台输出足够。
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

// 注册单表仓储与连接工厂。
builder.Services.AddSingleton<EmokingDbContext>();
builder.Services.AddSingleton<UniversalEntityRepository>();

// 注册元数据缓存——启动时自举。
builder.Services.AddSingleton<MetaCache>();

// 注册万物网关。
builder.Services.AddSingleton<UniversalEntityGateway>();

var app = builder.Build();

// 同构前端——静态托管 Genesis.Web/wwwroot（通用渲染器 index.html）。
app.UseDefaultFiles();
app.UseStaticFiles();

// 启动时自举元数据并按池中 StorageProvider 指令落位存储引擎——网关不关心是哪个引擎。
var cache = app.Services.GetRequiredService<MetaCache>();
await cache.LoadAsync();
var store = UniversalStoreFactory.Create(cache, app.Services.GetRequiredService<EmokingDbContext>());
var gateway = app.Services.GetRequiredService<UniversalEntityGateway>();
var repository = app.Services.GetRequiredService<UniversalEntityRepository>();

// 命令执行器。
var commandExecutor = new UniversalCommandExecutor(repository, cache, outputDirectory: ".");

// 意图总线——唯一动作入口。
var actionBus = new UniversalActionBus(cache, gateway, store, commandExecutor);

// 自愈冷却状态——距上次热重载不足 5 秒时拒绝再次重载（防刷）。
var lastHealAt = DateTime.MinValue;
var healCooldown = TimeSpan.FromSeconds(5);

// 唯一的 HTTP 入口——整个"后端"就这一个端点。
app.MapPost("/api/genesis", async (GatewayRequest request) =>
{
    // 请求体校验。
    if (request is null || string.IsNullOrWhiteSpace(request.Action))
    {
        // 返回错误。
        return Results.BadRequest(new { error = "action 不能为空。" });
    }

    // 第 1 道：Token 鉴权——池中 ApiSecurity 策略存在即启用校验，不匹配直接 401（不进总线）。
    var apiSecurity = cache.GetSecurityPolicy("ApiSecurity");
    if (apiSecurity is not null && apiSecurity.TryGetValue("Token", out var expectedToken))
    {
        // 比对请求 Token。
        if (!string.Equals(request.Token, expectedToken, StringComparison.Ordinal))
        {
            // 401。
            return Results.Json(new { ok = false, error = "Token 无效或缺失。" }, statusCode: 401);
        }
    }

    // 交给意图总线——失败按异常类型路由（第 25 轮自愈收窄：只有部署/配置问题触发 reload）。
    try
    {
        // 执行动作。
        var result = await actionBus.ExecuteActionAsync(request.Action, request.Args ?? new Dictionary<string, string>());

        // 返回结果。
        return Results.Ok(new { ok = true, result });
    }
    catch (ConcurrencyConflictException ex)
    {
        // 并发冲突——正常业务语义（两读者一胜一败），409 直接返回，绝不 reload 重试
        // （重试必然再次冲突；非幂等动作重试会重复写——评审 P0：网关自愈收窄）。
        return Results.Json(new { ok = false, error = ex.Message }, statusCode: 409);
    }
    catch (MetadataNotRegisteredException)
    {
        // 第 26 轮 Dry-run 前置：先做只读校验——若为工作流命令且校验通过，
        // 说明元数据实际齐备（异常来自运行时）——不 reload（杜绝部分写入后重试）。
        // 数据动作（InsertData 等）无步骤概念，类型未注册的 reload 仍然合理。
        var isWorkflowCommand = cache.GetCommand(request.Action) is not null;
        if (isWorkflowCommand
            && commandExecutor.ValidateCommand(request.Action, request.Args ?? new Dictionary<string, string>()).Count == 0)
        {
            // 校验通过——不 reload，按业务错误返回。
            return Results.BadRequest(new { ok = false, error = "动作执行失败（元数据校验通过——非部署问题）。" });
        }

        // 自愈防刷：冷却期内直接拒绝，不触发重载。
        if (DateTime.UtcNow - lastHealAt < healCooldown)
        {
            // 冷却拒绝。
            return Results.Json(new { ok = false, error = "系统配置缓存冷却中，请稍后重试。" }, statusCode: 429);
        }

        // 记录本次自愈时间。
        lastHealAt = DateTime.UtcNow;

        // 自愈：池中可能刚插入了新命令/新类型——热重载后重试一次。
        await cache.LoadAsync();
        try
        {
            // 重试动作。
            var retried = await actionBus.ExecuteActionAsync(request.Action, request.Args ?? new Dictionary<string, string>());

            // 返回结果。
            return Results.Ok(new { ok = true, result = retried });
        }
        catch (InvalidOperationException ex)
        {
            // 重试仍失败——返回业务错误。
            return Results.BadRequest(new { ok = false, error = ex.Message });
        }
    }
    catch (InvalidOperationException ex)
    {
        // 其他业务错误（缺参/不存在/载荷非法等客户端问题）——400 直接返回，不 reload。
        return Results.BadRequest(new { ok = false, error = ex.Message });
    }
});

// 网关启动。
app.Run();

/// <summary>
/// 网关请求契约——一切业务意图的统一形态。
/// </summary>
public sealed class GatewayRequest
{
    /// <summary>动作名（Role=7 工作流命令或内置业务动作）。</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>动作参数（K=V）。</summary>
    public Dictionary<string, string>? Args { get; set; }

    /// <summary>访问凭证——v1 预留字段，鉴权未实现（见 UPDATE_LOG 诚实边界）。</summary>
    public string? Token { get; set; }
}
