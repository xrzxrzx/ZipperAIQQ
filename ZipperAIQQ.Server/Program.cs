using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Filtering;
using ZipperAIQQ.Channel.NapCat;
using ZipperAIQQ.Server.Orchestration;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Messages;
using ZipperAIQQ.Transport.Client;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

// ── 配置：全部来自环境变量，代码里不写死任何 URL / 模型名（FR-8.13）──
string? Env(string name) => Environment.GetEnvironmentVariable(name);

var missing = new List<string>();
string Require(string name)
{
    var v = Env(name);
    if (string.IsNullOrWhiteSpace(v)) { missing.Add(name); return string.Empty; }
    return v;
}

var wsUrl       = Require("ZIPPER_NAPCAT_WS");
var apiKey      = Require("ZIPPER_LLM_APIKEY");
var baseUrl     = Require("ZIPPER_LLM_BASEURL");
var model       = Require("ZIPPER_LLM_MODEL");
var apiKindText = Env("ZIPPER_LLM_APIKIND");           // 可选：Responses | ChatCompletions
var napcatToken = Env("ZIPPER_NAPCAT_TOKEN");          // 可选
var dbPath      = Env("ZIPPER_DB") ?? "./data/zipperaiqq.db";
var whitelist   = (Env("ZIPPER_WHITELIST") ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

if (missing.Count > 0)
{
    Log.Fatal("缺少必需的环境变量：{Missing}", string.Join(", ", missing));
    Log.Information("示例：ZIPPER_NAPCAT_WS / ZIPPER_LLM_APIKEY / ZIPPER_LLM_BASEURL / ZIPPER_LLM_MODEL");
    return 1;
}

var apiKind = string.IsNullOrWhiteSpace(apiKindText)
    ? LlmApiKind.Responses
    : Enum.Parse<LlmApiKind>(apiKindText, ignoreCase: true);

// 白名单为空 → 用「黑名单 + 空」= 全放行，便于首次跑通；生产请显式配白名单
var filterOptions = whitelist.Length > 0
    ? new ChatFilterOptions { Mode = ChatListMode.Whitelist, Entries = whitelist }
    : new ChatFilterOptions { Mode = ChatListMode.Blacklist, Entries = [] };

var fullDbPath = Path.GetFullPath(dbPath);
var dbDir = Path.GetDirectoryName(fullDbPath);
if (!string.IsNullOrEmpty(dbDir)) { Directory.CreateDirectory(dbDir); }
var connectionString = $"Data Source={fullDbPath}";

await new DatabaseInitializer(connectionString, Log.Logger).InitializeAsync();

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton(Log.Logger);
builder.Services.AddSingleton<ISqliteDatabase>(_ => new SqliteDatabase(connectionString, Log.Logger));
builder.Services.AddSingleton<IMessageStore, SqliteMessageStore>();
builder.Services.AddSingleton<IChatFilter>(_ => new ChatFilter(filterOptions));

builder.Services.AddSingleton<IClientTransport>(_ => new ClientTransport(Log.Logger));
builder.Services.AddSingleton<IMessageChannel>(sp => new NapCatChannel(
    sp.GetRequiredService<IClientTransport>(),
    sp.GetRequiredService<IChatFilter>(),
    new NapCatOptions { WebSocketUrl = wsUrl, AccessToken = napcatToken },
    Log.Logger));

builder.Services.AddSingleton<IChatClient>(_ => ChatClientFactory.Create(
    new LlmEndpoint("primary", baseUrl, model, apiKind), apiKey));

var agentOptions = new ChatAgentOptions
{
    SystemPrompt =
        "你是一个角色扮演助手，在聊天软件里和人对话。" +
        "**你必须调用 send_message 工具来把话发出去** —— 直接输出文本对方是看不到的。" +
        "可以一次发多条（像真人把一句话分几条发）。回复要简短自然。",
};

builder.Services.AddSingleton<IChatAgentPool>(sp =>
{
    var client = sp.GetRequiredService<IChatClient>();
    var channel = sp.GetRequiredService<IMessageChannel>();
    return new ChatAgentPool(chat => new ChatAgent(
        chat.SessionId, client, channel, sp.GetRequiredService<IMessageStore>(), agentOptions, Log.Logger));
});

builder.Services.AddHostedService<MessageRouter>();

// ── 可观测性（FR-10）：追踪由 Agent 的 ActivitySource 提供，HTTP 调用自动追踪 ──
// Console 导出默认关闭（会把控制台刷满），需要时设 ZIPPER_OTEL_CONSOLE=1。
var otelConsole = Env("ZIPPER_OTEL_CONSOLE") == "1";
builder.Services.AddOpenTelemetry()
    // 没有它，接到 Grafana/Jaeger 后所有数据会显示成 unknown_service
    .ConfigureResource(resource => resource.AddService("ZipperAIQQ.Server"))
    .WithTracing(tracing =>
    {
        tracing.AddSource(AgentTelemetry.SourceName)
               .AddHttpClientInstrumentation();
        if (otelConsole)
        {
            tracing.AddConsoleExporter();
        }
    });

var app = builder.Build();

Log.Information("配置就绪：WS={Ws} 模型={Model} 库={Db}", wsUrl, model, fullDbPath);
Log.Information("过滤：{Mode}（{Count} 条）", filterOptions.Mode, filterOptions.Entries.Count);

try
{
    await app.RunAsync();
}
catch (OperationCanceledException)
{
    // 正常停止
}
finally
{
    Log.CloseAndFlush();
}

return 0;