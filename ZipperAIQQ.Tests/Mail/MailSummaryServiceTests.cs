using System.Net;
using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Mail;

namespace ZipperAIQQ.Tests.Mail;

/// <summary>
/// 邮件摘要服务（设计 §6.3 / §6.4）：重试边界、解析容错，以及**失败绝不丢信**。
/// </summary>
/// <remarks>
/// 全部用假 <see cref="IChatClient"/>：单测**不打真实 LLM**（编码规范 §13）。
/// 退避等待被压成 0（子类覆盖 <c>RetryDelay</c>），否则一个重试用例要白等好几秒。
/// </remarks>
public sealed class MailSummaryServiceTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private static MailOptions Options(int retryCount = 2, int concurrency = 2) => new()
    {
        Enabled = true,
        SummaryEnabled = true,
        SummaryRetryCount = retryCount,
        MaxConcurrentSummaries = concurrency,
        MaxBodyChars = 20_000,
    };

    private static IncomingMail Mail(string subject = "本周账单", string body = "请于周五前确认账单金额。") => new()
    {
        Chat = new ChatRef
        {
            Platform = "mail",
            Type = ChatType.Private,
            Id = "qq-1/<msg-1@example.com>",
        },
        OccurredAt = new DateTimeOffset(2026, 10, 27, 9, 30, 0, TimeSpan.FromHours(8)),
        EventId = "<msg-1@example.com>",
        AccountId = "qq-1",
        Subject = subject,
        FromAddress = "billing@example.com",
        FromName = "财务部",
        BodyText = body,
        HasAttachments = false,
    };

    private static ChatResponse Json(string summary, bool needsAction) => new(
        new ChatMessage(
            ChatRole.Assistant,
            $"{{\"summary\":\"{summary}\",\"needsAction\":{(needsAction ? "true" : "false")}}}"));

    private static ChatResponse Raw(string text) => new(new ChatMessage(ChatRole.Assistant, text));

    private static MailSummaryService Service(ScriptedChatClient client, MailOptions? options = null) =>
        new FastRetrySummaryService(new FakeProvider(client), options ?? Options(), Logger);

    // ─────────────────────────── 重试策略 ───────────────────────────

    [Fact]
    public async Task SummarizeAsync_RetriesOn429_ThenReturnsSummary()
    {
        var client = new ScriptedChatClient()
            .Then(() => throw new HttpRequestException("上游限流", null, HttpStatusCode.TooManyRequests))
            .Then(() => Json("周五前确认账单。", true));

        var result = await Service(client).SummarizeAsync(Mail());

        Assert.Equal(2, client.CallCount);
        Assert.Equal("周五前确认账单。", result.Text);
        Assert.True(result.NeedsAction);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task SummarizeAsync_DoesNotRetryOn400_ReturnsReadableError()
    {
        var client = new ScriptedChatClient()
            .Then(() => throw new HttpRequestException("参数错", null, HttpStatusCode.BadRequest))
            .Then(() => Json("不该被用到", false));

        var result = await Service(client, Options(retryCount: 3)).SummarizeAsync(Mail());

        // 4xx 是参数/认证问题：重试只浪费配额，必须一次就停
        Assert.Equal(1, client.CallCount);
        Assert.Equal(string.Empty, result.Text);
        Assert.False(result.NeedsAction);
        Assert.Contains("400", result.Error);
    }

    [Fact]
    public async Task SummarizeAsync_ServerErrorExhausted_StopsAtRetryCountAndReturnsFailure()
    {
        var client = new ScriptedChatClient()
            .Then(() => throw new HttpRequestException("炸了", null, HttpStatusCode.BadGateway))
            .Then(() => throw new HttpRequestException("还是炸了", null, HttpStatusCode.ServiceUnavailable))
            .Then(() => Json("不该被用到", false));

        // SummaryRetryCount = 1 → 首次 + 1 次重试 = 2 次调用
        var result = await Service(client, Options(retryCount: 1)).SummarizeAsync(Mail());

        Assert.Equal(2, client.CallCount);
        Assert.Equal(string.Empty, result.Text);
        Assert.False(result.NeedsAction);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    // ─────────────────────────── 解析容错 ───────────────────────────

    [Fact]
    public async Task SummarizeAsync_InvalidJson_RetriesOnceThenReturnsFailureWithoutThrowing()
    {
        var client = new ScriptedChatClient()
            .Then(() => Raw("抱歉，我没法摘要这封邮件。"))
            .Then(() => Raw("真的不行。"));

        var result = await Service(client).SummarizeAsync(Mail());

        Assert.Equal(2, client.CallCount);
        Assert.Equal(string.Empty, result.Text);
        Assert.False(result.NeedsAction);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task SummarizeAsync_JsonInsideCodeFence_IsParsed()
    {
        var client = new ScriptedChatClient()
            .Then(() => Raw("```json\n{\"summary\":\"发票待报销。\",\"needsAction\":true}\n```"));

        var result = await Service(client).SummarizeAsync(Mail());

        Assert.Equal(1, client.CallCount);
        Assert.Equal("发票待报销。", result.Text);
        Assert.True(result.NeedsAction);
        Assert.Null(result.Error);
    }

    // ─────────────────────────── 降级与输入边界 ───────────────────────────

    [Fact]
    public async Task SummarizeAsync_NoClientConfigured_ReturnsReadableError()
    {
        var service = new FastRetrySummaryService(new FakeProvider(null), Options(), Logger);

        var result = await service.SummarizeAsync(Mail());

        Assert.Equal(string.Empty, result.Text);
        Assert.False(result.NeedsAction);
        Assert.Contains("LLM", result.Error);
    }

    [Fact]
    public async Task SummarizeAsync_LongBody_IsTruncatedButKeepsSubjectAndSender()
    {
        var body = new string('甲', 8_000);
        var client = new ScriptedChatClient().Then(() => Json("正文很长。", false));

        await Service(client).SummarizeAsync(Mail(body: body));

        var request = client.Requests[0];
        var userText = request[^1].Text;

        Assert.Contains("本周账单", userText);
        Assert.Contains("billing@example.com", userText);
        Assert.Contains("已截断", userText);

        // 8000 字的正文不该整段送进模型（上限 4000 + 少量抬头）
        Assert.True(userText.Length < 5_000, $"请求正文没被截断，长度 {userText.Length}");
    }

    // ─────────────────────────── 测试替身 ───────────────────────────

    /// <summary>把退避等待压成 0 的摘要服务：测的是分支，不是睡眠。</summary>
    private sealed class FastRetrySummaryService : MailSummaryService
    {
        public FastRetrySummaryService(IChatClientProvider clients, MailOptions options, ILogger logger)
            : base(clients, options, logger)
        {
        }

        protected override TimeSpan RetryDelay(int attempt) => TimeSpan.Zero;
    }

    /// <summary>假的客户端提供者（<c>null</c> = 还没配置端点）。</summary>
    private sealed class FakeProvider : IChatClientProvider
    {
        private readonly IChatClient? _client;

        public FakeProvider(IChatClient? client) => _client = client;

        public bool HasClient => _client is not null;

        public string? CurrentModel => _client is null ? null : "fake-model";

        public IChatClient Current => _client
            ?? throw new InvalidOperationException("测试里没有配置客户端");

        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    /// <summary>按脚本逐个回应的假 <see cref="IChatClient"/>；脚本用完还调 = 调多了，直接失败。</summary>
    private sealed class ScriptedChatClient : IChatClient
    {
        private readonly Queue<Func<ChatResponse>> _steps = new();

        public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

        public int CallCount => Requests.Count;

        public ScriptedChatClient Then(Func<ChatResponse> step)
        {
            _steps.Enqueue(step);
            return this;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(messages.ToList());

            if (_steps.Count == 0)
            {
                throw new InvalidOperationException("测试脚本已用尽：实际调用次数比预期多。");
            }

            return Task.FromResult(_steps.Dequeue()());
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("邮件摘要不走流式调用。");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
