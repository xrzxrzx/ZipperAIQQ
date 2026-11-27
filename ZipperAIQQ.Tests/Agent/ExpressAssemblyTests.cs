using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Agent.Embedding;
using ZipperAIQQ.Agent.Express;
using ZipperAIQQ.Agent.Length;
using ZipperAIQQ.Agent.Memory;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Core.Events;
using ZipperAIQQ.Storage;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Database;
using ZipperAIQQ.Storage.Embeddings;
using ZipperAIQQ.Storage.Messages;
using ZipperAIQQ.Storage.Metrics;
using ZipperAIQQ.Storage.Personas;
using ZipperAIQQ.Storage.Schedules;
using ZipperAIQQ.Storage.Settings;
using ZipperAIQQ.Storage.Summaries;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>
/// B6 集成层：装配出来的 system 里**确实**有档位规则段与放大后的预算（不真调模型）。
/// </summary>
/// <remarks>
/// 纯函数各自的用例在 ExpressPromptTests；这里只回答一个问题：
/// 「ChatAgent 真的把它们拼进本轮上下文了吗」—— 接线错了纯函数再绿也没用。
/// 存储一律用**真实 SQLite 实现**（临时文件库）：假的 store 只会证明"我写对了假实现"。
/// </remarks>
public sealed class ExpressAssemblyTests : IAsyncLifetime
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"zipper-express-{Guid.NewGuid():N}.db");
    private readonly string _connectionString;
    private readonly SqliteDatabase _db;

    public ExpressAssemblyTests()
    {
        _connectionString = $"Data Source={_dbPath}";
        _db = new SqliteDatabase(_connectionString, Logger);
    }

    public async Task InitializeAsync() =>
        await new DatabaseInitializer(_connectionString, Logger).InitializeAsync();

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        try
        {
            File.Delete(_dbPath);
        }
        catch (IOException)
        {
            // 连接池可能还持有句柄；临时文件删不掉不影响结论
        }
    }

    [Fact]
    public async Task CloseLevel_InjectsLibertySegment_AndAmplifiedBudget()
    {
        var client = new CapturingClient();

        await RunAsync(client, new ExpressSettings { Level = ExpressLevel.Close }, TestData.PrivateChat());

        var system = LastReinject(client);

        Assert.Contains("【表达自由 · 当前档位：亲近】", system);
        Assert.Contains("允许暧昧与调情", system);
        Assert.Contains("约 900 字以内、最多 7 条", system);
    }

    [Fact]
    public async Task ExplicitLevel_AmplifiesPrivateBudgetTo1200x10()
    {
        var client = new CapturingClient();

        await RunAsync(client, new ExpressSettings { Level = ExpressLevel.Explicit }, TestData.PrivateChat());

        var system = LastReinject(client);
        Assert.Contains("【表达自由 · 当前档位：露骨】", system);
        Assert.Contains("约 1200 字以内、最多 10 条", system);
    }

    [Fact]
    public async Task NoExpressSource_KeepsPreB6Behavior()
    {
        var client = new CapturingClient();

        await RunAsync(client, settings: null, TestData.PrivateChat());

        var system = LastReinject(client);
        Assert.DoesNotContain("表达自由", system);
        Assert.Contains("约 600 字以内、最多 5 条", system);   // 旧预算原样
    }

    [Fact]
    public async Task CustomRules_ReplaceBuiltInSegment()
    {
        var client = new CapturingClient();

        await RunAsync(
            client,
            new ExpressSettings { Level = ExpressLevel.Explicit, CustomRules = "按我自己的规矩说话。" },
            TestData.PrivateChat());

        var system = LastReinject(client);
        Assert.Contains("按我自己的规矩说话。", system);
        Assert.DoesNotContain("【表达自由", system);
    }

    [Fact]
    public async Task GroupWithDailyCap_TightensEvenSessionOverride()
    {
        var client = new CapturingClient();

        var group = new ChatRef { Platform = "qq", Type = ChatType.Group, Id = "10001" };
        var settings = new ExpressSettings
        {
            Level = ExpressLevel.Explicit,
            GroupCap = ExpressLevel.Daily,
            SessionOverrides = new Dictionary<string, ExpressLevel>(StringComparer.Ordinal)
            {
                [group.SessionId] = ExpressLevel.Explicit,
            },
        };

        await RunAsync(client, settings, group);

        var system = LastReinject(client);
        Assert.Contains("【表达自由 · 当前档位：日常】", system);
        Assert.Contains("这是群聊", system);
        Assert.Contains("约 600 字以内、最多 5 条", system);
    }

    [Fact]
    public async Task OwnerGetsOwnerClause_NonOwnerDoesNot()
    {
        var owner = new CapturingClient();
        await RunAsync(owner, new ExpressSettings { Level = ExpressLevel.Close }, TestData.PrivateChat());
        Assert.Contains("说话人是你的主人", LastReinject(owner));

        var other = new CapturingClient();
        await RunAsync(
            other,
            new ExpressSettings { Level = ExpressLevel.Close },
            TestData.PrivateChat(),
            TestData.Sender(id: "20002", nickname: "路人"));
        Assert.Contains("说话人不是主人", LastReinject(other));
    }

    [Fact]
    public void ToolCatalog_HasNoExpressTool()
    {
        // A-20：模型无法自己升档 —— 档位不是工具，只有客户端 HTTP 能改
        Assert.DoesNotContain(
            ToolCatalog.All,
            item => item.Key.Contains("express", StringComparison.OrdinalIgnoreCase));
    }

    // ══════════════ A-23：免拒绝校准（ChatAgent 级，非纯函数）══════════════
    //
    // 这四条是「验收条能不能被测试证伪」的关键：把 ChatAgent 里那个 hygiene 回调写错、
    // 或者把降级代发路径的调用删掉，这组必须变红。

    [Fact]
    public async Task Hygiene_RewritesBeforeSend_AndCountsOnce()
    {
        var client = new CapturingClient
        {
            Reply = "作为一个 AI，我不能聊这个",
            Rewritten = "好呀，那我们出门吧",
        };

        var run = await RunAsync(client, new ExpressSettings { Level = ExpressLevel.Close }, TestData.PrivateChat());

        Assert.Equal(["好呀，那我们出门吧"], run.Channel.Sent);   // 发出去的是重写版
        Assert.Equal(1, run.Express!.Regenerated);                // 只重生成一次
        Assert.True(run.Express.Hits >= 1);
        Assert.Single(client.RewriteInputs);                      // 只调了一次重写
        Assert.Equal("作为一个 AI，我不能聊这个", client.RewriteInputs[0]);
    }

    [Fact]
    public async Task Hygiene_SecondHit_SendsOriginalAndCountsTwice()
    {
        var client = new CapturingClient
        {
            Reply = "作为一个 AI，我不能聊这个",
            Rewritten = "作为一个 AI，我还是不能聊",
        };

        var run = await RunAsync(client, new ExpressSettings { Level = ExpressLevel.Explicit }, TestData.PrivateChat());

        // 二次命中照发**原文**（宁可留一句垫话，也不能把消息吞掉）
        Assert.Equal(["作为一个 AI，我不能聊这个"], run.Channel.Sent);
        Assert.Equal(1, run.Express!.Regenerated);
        Assert.Equal(2, run.Express.Hits);                        // 第一次命中 + 重写后再次命中
        Assert.Single(client.RewriteInputs);
    }

    [Fact]
    public async Task Hygiene_RewriteFailure_SendsOriginal()
    {
        var client = new CapturingClient
        {
            Reply = "作为一个 AI，我不能聊这个",
            ThrowOnRewrite = true,
        };

        var run = await RunAsync(client, new ExpressSettings { Level = ExpressLevel.Close }, TestData.PrivateChat());

        Assert.Equal(["作为一个 AI，我不能聊这个"], run.Channel.Sent);
        Assert.Equal(1, run.Express!.Regenerated);                // 尝试过，但没成功
    }

    [Fact]
    public async Task Hygiene_DailyLevel_NeverRewrites()
    {
        var client = new CapturingClient { Reply = "作为一个 AI，我不能聊这个" };

        var run = await RunAsync(client, new ExpressSettings { Level = ExpressLevel.Daily }, TestData.PrivateChat());

        Assert.Empty(client.RewriteInputs);                       // 日常档不纠（白烧 token）
        Assert.Equal(["作为一个 AI，我不能聊这个"], run.Channel.Sent);
        Assert.Equal(0, run.Express!.Regenerated);
        Assert.Equal(1, run.Express.Hits);                        // 命中照样计数（A-23 可见性）
    }

    [Fact]
    public async Task Hygiene_SwitchOff_NeverRewrites()
    {
        var client = new CapturingClient { Reply = "作为一个 AI，我不能聊这个" };

        var run = await RunAsync(
            client,
            new ExpressSettings { Level = ExpressLevel.Explicit, HygieneEnabled = false },
            TestData.PrivateChat());

        Assert.Empty(client.RewriteInputs);
        Assert.Equal(["作为一个 AI，我不能聊这个"], run.Channel.Sent);
    }

    // ── 装配辅助 ──
    private async Task<RunOutcome> RunAsync(
        CapturingClient client, ExpressSettings? settings, ChatRef chat, SenderInfo? sender = null)
    {
        var settingsStore = new SqliteRuntimeSettingsStore(_db);
        var messages = new SqliteMessageStore(_db);
        var summaries = new SqliteSummaryStore(_db);
        var embeddings = new SqliteEmbeddingStore(_db);
        var metrics = new SqliteMetricsStore(_db);
        var schedules = new SqliteScheduleStore(_db);
        var configs = new SqliteConfigStore(_db);
        var provider = new FakeProvider(client);
        var channel = new RecordingChannel();
        var express = settings is null ? null : new FixedExpress(settings);

        var deps = new ChatAgentDependencies
        {
            Client = provider,
            Channel = channel,
            Store = messages,
            Faces = ZipperAIQQ.Agent.Faces.FaceCatalog.Empty,
            Retriever = new MemoryRetriever(new ThrowingEmbedding(), embeddings, messages, metrics, Logger),
            Summaries = new SummaryService(provider, messages, summaries, metrics, Logger),
            SummaryStore = summaries,
            Personas = new DefaultPersonaStore(_db, new Persona("小衣", "【形象】猫耳少女。")),
            Metrics = metrics,
            Events = new EventBus(Logger),
            PermissionGate = new PermissionGate(),
            Policy = new ExecutionPolicy(settingsStore, ToolCatalog.All, Logger),
            Roles = new OwnerRoles(isOwner: sender is null || sender.UserId == "10001"),
            Workspace = new WorkspacePathResolver(Path.Combine(Path.GetTempPath(), "zipper-express-tests")),
            Schedules = schedules,
            ReplyLength = new ReplyLengthOptions(),
            Express = express,
            FileMaxBytes = 256 * 1024,
            Options = new ChatAgentOptions(),
            Logger = Logger,
        };

        var agent = new ChatAgent(chat.SessionId, deps, configs, settingsStore);

        await agent.RunAsync(new AgentRunRequest
        {
            Chat = chat,
            Sender = sender ?? TestData.Sender(),
            UserText = "在吗",
            Now = DateTimeOffset.UtcNow,
        });

        return new RunOutcome(channel, express);
    }

    private sealed record RunOutcome(RecordingChannel Channel, FixedExpress? Express);

    /// <summary>取本轮重注入的那条 system（最后一条 = 紧邻当前消息之前）。</summary>
    private static string LastReinject(CapturingClient client)
    {
        var systems = (client.Captured ?? [])
            .Where(m => m.Role == ChatRole.System)
            .Select(m => m.Text ?? string.Empty)
            .ToList();

        Assert.True(systems.Count >= 2, $"system 只有 {systems.Count} 条");
        return systems[^1];
    }

    // ══════════ fakes（只保留接口最小面：能用真实实现的都用真实的）══════════

    private sealed class CapturingClient : IChatClient
    {
        /// <summary>普通回复（装配测试里留空 = 本轮不发消息）。</summary>
        public string Reply { get; set; } = string.Empty;

        /// <summary>「重说一遍」调用返回的文本。</summary>
        public string Rewritten { get; set; } = string.Empty;

        /// <summary>让重生成抛异常（验证「失败照发原文」）。</summary>
        public bool ThrowOnRewrite { get; set; }

        /// <summary>每次重生成请求带进来的原文。</summary>
        public List<string> RewriteInputs { get; } = [];

        public List<ChatMessage>? Captured { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var list = messages.ToList();
            Captured = list;

            // 按**内容**识别「重说一遍」调用，而不是按调用序号：
            // 漂移重试与后台摘要也会调模型，序号法随时会被它们打乱（测试就变成看运气）
            var isRewrite = list.Any(m =>
                m.Role == ChatRole.System &&
                (m.Text ?? string.Empty).Contains(ExpressHygiene.RegenerateInstruction, StringComparison.Ordinal));

            if (!isRewrite)
            {
                return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, Reply)]));
            }

            RewriteInputs.Add(list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty);

            if (ThrowOnRewrite)
            {
                throw new HttpRequestException("重生成失败（测试构造）");
            }

            return Task.FromResult(new ChatResponse([new ChatMessage(ChatRole.Assistant, Rewritten)]));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class FakeProvider(IChatClient client) : ZipperAIQQ.Agent.Llm.IChatClientProvider
    {
        public bool HasClient => true;

        public IChatClient Current => client;

        public string? CurrentModel => "fake";

        public Task<bool> ReloadAsync(CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class FixedExpress(ExpressSettings settings) : IExpressSettingsSource
    {
        public ExpressSettings Current { get; } = settings;

        public int Hits { get; private set; }

        public int Regenerated { get; private set; }

        public void RecordHygieneHit(ExpressHygieneCategory category) => Hits++;

        public void RecordHygieneRegeneration() => Regenerated++;
    }

    private sealed class RecordingChannel : IMessageChannel
    {
        public List<string> Sent { get; } = [];

        public string Platform => "qq";

        public ChannelCapabilities Capabilities => ChannelCapabilities.Text;

        public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task SendAsync(OutgoingMessage message, CancellationToken ct)
        {
            Sent.Add(string.Concat(message.Segments.OfType<TextSegment>().Select(s => s.Text)));
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<InboundEvent> ReceiveAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    /// <summary>检索一律失败：ChatAgent 会吞掉并记 Warning（本组不验记忆）。</summary>
    private sealed class ThrowingEmbedding : IEmbeddingService
    {
        public int Dimension => 0;

        public ReadOnlyMemory<float> Embed(string text) => throw new NotSupportedException();
    }

    private sealed class OwnerRoles(bool isOwner) : IRoleResolver
    {
        public Role Resolve(string? userId, string platform) => isOwner ? Role.Owner : Role.Normal;
    }
}
