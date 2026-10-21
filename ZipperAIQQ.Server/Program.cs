using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using ZipperAIQQ.Agent.Events;
using ZipperAIQQ.Agent.Faces;
using ZipperAIQQ.Core.Events;
using ZipperAIQQ.Server.Api;
using ZipperAIQQ.Server.Composition;
using ZipperAIQQ.Server.Configuration;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Metrics;

// 中文日志在 GBK 代码页的控制台里会乱码（文件日志是 UTF-8，不受影响）
try { Console.OutputEncoding = System.Text.Encoding.UTF8; } catch { }

var builder = WebApplication.CreateBuilder(args);

// 日志统一交给 Serilog：关掉 ASP.NET Core 默认的 Console 提供程序，避免双份日志
builder.Logging.ClearProviders();
builder.AddZipperLogging();

ServerOptions options;
try
{
    // 只解析一次：缺配置就在这里一次性说清，不带半个配置继续跑
    options = ServerOptions.FromEnvironment();
}
catch (InvalidOperationException ex)
{
    Log.Fatal("启动失败：{Message}", ex.Message);
    await Log.CloseAndFlushAsync();
    return 1;
}

builder.AddZipperTelemetry(options);

try
{
    return await RunAsync(builder, options);
}
catch (Exception ex)
{
    Log.Fatal(ex, "启动失败");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static async Task<int> RunAsync(WebApplicationBuilder builder, ServerOptions options)
{
    // 建库必须在 DI 之前完成：仓储假定表已存在
    var databasePath = Path.GetFullPath(options.DatabasePath);
    var directory = Path.GetDirectoryName(databasePath);
    if (!string.IsNullOrEmpty(directory))
    {
        Directory.CreateDirectory(directory);
    }

    var connectionString = $"Data Source={databasePath}";
    await new DatabaseInitializer(connectionString, Log.Logger).InitializeAsync();

    builder.Services.AddZipperServices(options, connectionString);

    // M2-A：Kestrel 只监听配置里的地址
    builder.WebHost.UseUrls($"http://{options.HttpAddress}");

    var app = builder.Build();

    // 强制解析鉴权策略：非 loopback 又没 token → 这里直接抛，拒绝启动（不裸奔）
    _ = app.Services.GetRequiredService<HttpAccessPolicy>();

    ApiEndpoint.Map(app);
    EventEndpoint.Map(app);

    SubscribeAgentEvents(app.Services);

    await LogStartupSummaryAsync(app.Services, options, databasePath);

    await app.RunAsync();
    return 0;
}

/// <summary>
/// 订阅 Agent 事件（§8.3：漂移 / 降级要能被看见）。
/// </summary>
/// <remarks>
/// M1 的消费者就是日志；M2 客户端接上 <c>WS /events</c> 后，
/// 再加一个订阅者把事件转成帧即可 —— **发布点不用改**。
/// </remarks>
static void SubscribeAgentEvents(IServiceProvider services)
{
    var bus = services.GetRequiredService<IEventBus>();

    bus.Subscribe<DriftDetectedEvent>(e => Log.Warning(
        "Agent 事件：指令遵循漂移 {Kind}/{Detail}（会话 {SessionId}）",
        e.Kind, e.Detail, e.SessionId));

    bus.Subscribe<SendDegradedEvent>(e => Log.Warning(
        "Agent 事件：降级代发 {Count} 条（会话 {SessionId}）",
        e.SentCount, e.SessionId));
}

static async Task LogStartupSummaryAsync(
    IServiceProvider services, ServerOptions options, string databasePath)
{
    var faces = services.GetRequiredService<FaceCatalog>();
    var mode = options.Whitelist.Count > 0 ? "Whitelist" : "Blacklist（空=全放行）";

    Log.Information("通道={Ws} 模型={Model}", options.NapCatWebSocketUrl, options.LlmModel);
    Log.Information("数据库={Db}", databasePath);
    Log.Information("过滤={Mode}（{Count} 条）", mode, options.Whitelist.Count);
    Log.Information("发送限速={Interval} ms，连发 {Max} 条后冷却 {Cooldown} ms",
        options.SendIntervalMs, options.MaxConsecutive, options.CooldownMs);
    Log.Information("表情目录={Count} 个（{Path}）", faces.Count, options.FacesPath);
    Log.Information("人设文件={Path}", Path.GetFullPath(options.PersonaPath));
    Log.Information("权限：管理员 {Admins} 人 / 工具白名单 {Whitelist} 人；工作区={Workspace}；单文件上限 {Max} 字节",
        options.Admins.Count, options.ToolWhitelist.Count,
        Path.GetFullPath(options.WorkspacePath), options.FileMaxBytes);
    Log.Information("HTTP 端点=http://{Addr}（{Auth}）", options.HttpAddress,
        HttpAccessPolicy.IsLoopback(options.HttpAddress) ? "loopback 免验" : "非 loopback，token 强制");
    Log.Information("向量模型目录={Dir}", options.EmbeddingModelDirectory);

    // 历史指标：让「上次跑得快不快」一开机就能看见，而不用等客户端接上
    var metrics = services.GetRequiredService<IMetricsStore>();
    var summary = await metrics.GetSummaryAsync(windowHours: 0).ConfigureAwait(false);
    if (summary.Count == 0)
    {
        Log.Information("调用指标：暂无数据");
        return;
    }

    Log.Information("调用指标（按平均耗时降序）：");
    foreach (var item in summary)
    {
        Log.Information("  {Kind,-22} {Count,6} 次  平均 {Avg,8:F1}ms  范围 {Min:F1}~{Max:F1}ms  token {In}/{Out}",
            item.Kind, item.Count, item.AvgMs, item.MinMs, item.MaxMs,
            item.TotalInputTokens, item.TotalOutputTokens);
    }
}
