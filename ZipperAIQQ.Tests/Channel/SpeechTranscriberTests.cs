using System.Text.Json.Nodes;
using Serilog;
using ZipperAIQQ.Channel.Audio;
using ZipperAIQQ.Channel.NapCat;

namespace ZipperAIQQ.Tests.Channel;

/// <summary>
/// NapCat 语音转写（<c>fetch_ptt_text</c>）的容错行为。
/// </summary>
/// <remarks>
/// 参数契约来自 2026-10-23 的实测：该 action 的必填项是 <c>message_id</c>（只给 file 会报
/// <c>Schema compilation error</c>）；这里测的是**失败不拖垮接收**：返回 null、
/// 连续失败后熔断、成功会重置计数。
/// </remarks>
public sealed class SpeechTranscriberTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private static AudioClip Clip(string? messageId = "m1", string? fileId = "a.amr") =>
        new(fileId, null, MessageId: messageId);

    private static NapCatPttTextTranscriber Create(
        Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> callApi,
        SpeechOptions? options = null) =>
        new(callApi, options ?? new SpeechOptions { RetryDelayMs = 0 }, Logger);

    [Fact]
    public async Task Returns_text_from_object_payload()
    {
        var transcriber = Create((_, _, _) =>
            Task.FromResult<JsonNode?>(JsonNode.Parse("""{ "text": "你好呀" }""")));

        var text = await transcriber.TranscribeAsync(Clip());

        Assert.Equal("你好呀", text);
        Assert.True(transcriber.IsAvailable);
    }

    [Fact]
    public async Task Returns_text_from_plain_string_payload()
    {
        var transcriber = Create((_, _, _) =>
            Task.FromResult<JsonNode?>(JsonValue.Create("直接是字符串")));

        Assert.Equal("直接是字符串", await transcriber.TranscribeAsync(Clip()));
    }

    [Fact]
    public async Task Sends_message_id_and_file()
    {
        JsonObject? captured = null;
        var transcriber = Create((_, parameters, _) =>
        {
            captured = parameters;
            return Task.FromResult<JsonNode?>(JsonNode.Parse("""{ "text": "ok" }"""));
        });

        await transcriber.TranscribeAsync(Clip("m-42", "voice.amr"));

        Assert.NotNull(captured);
        Assert.Equal("m-42", captured!["message_id"]?.GetValue<string>());
        Assert.Equal("voice.amr", captured["file"]?.GetValue<string>());
    }

    [Fact]
    public async Task Missing_message_id_does_not_call_api()
    {
        // message_id 是必填项：没有它连 schema 都过不了，不该白跑一次往返
        var calls = 0;
        var transcriber = Create((_, _, _) =>
        {
            calls++;
            return Task.FromResult<JsonNode?>(null);
        });

        Assert.Null(await transcriber.TranscribeAsync(Clip(messageId: null)));
        Assert.Null(await transcriber.TranscribeAsync(new AudioClip("a.amr", "https://x/a.amr")));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Disabled_option_short_circuits()
    {
        var calls = 0;
        var transcriber = Create(
            (_, _, _) =>
            {
                calls++;
                return Task.FromResult<JsonNode?>(null);
            },
            new SpeechOptions { Enabled = false });

        Assert.False(transcriber.IsAvailable);
        Assert.Null(await transcriber.TranscribeAsync(Clip()));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Transient_failure_is_retried_once()
    {
        // 实测：语音刚到时 QQ 侧 ASR 可能还没出结果（1200），等一下再问就成功
        var calls = 0;
        var transcriber = Create((_, _, _) =>
        {
            calls++;
            return Task.FromResult<JsonNode?>(calls == 1
                ? null
                : JsonNode.Parse("""{ "text": "你能听懂我说的话吗？" }"""));
        });

        var text = await transcriber.TranscribeAsync(Clip());

        Assert.Equal("你能听懂我说的话吗？", text);
        Assert.Equal(2, calls);
        Assert.True(transcriber.IsAvailable);
    }

    [Fact]
    public async Task Consecutive_failures_disable_transcriber()
    {
        var calls = 0;
        var transcriber = Create(
            (_, _, _) =>
            {
                calls++;
                return Task.FromResult<JsonNode?>(null);
            },
            new SpeechOptions { MaxConsecutiveFailures = 3, RetryDelayMs = 0 });

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(await transcriber.TranscribeAsync(Clip()));
        }

        // 每次尝试内部重试 1 次（共 2 次调用）；3 次失败后熔断，后两次不再发起调用
        Assert.Equal(6, calls);
        Assert.False(transcriber.IsAvailable);
    }

    [Fact]
    public async Task Success_resets_failure_counter()
    {
        var calls = 0;
        var succeed = false;
        var transcriber = Create(
            (_, _, _) =>
            {
                calls++;
                return Task.FromResult<JsonNode?>(
                    succeed ? JsonNode.Parse("""{ "text": "恢复" }""") : null);
            },
            new SpeechOptions { MaxConsecutiveFailures = 2, RetryDelayMs = 0 });

        Assert.Null(await transcriber.TranscribeAsync(Clip())); // 1 次失败（内部重试 2 次调用）
        Assert.Equal(2, calls);

        succeed = true;
        Assert.Equal("恢复", await transcriber.TranscribeAsync(Clip())); // 成功，清零

        succeed = false;
        Assert.Null(await transcriber.TranscribeAsync(Clip())); // 重新从 1 开始计

        Assert.True(transcriber.IsAvailable);
    }

    [Fact]
    public async Task Exception_is_swallowed_as_null()
    {
        var transcriber = Create(
            (_, _, _) => throw new InvalidOperationException("boom"),
            new SpeechOptions { RetryDelayMs = 0 });

        Assert.Null(await transcriber.TranscribeAsync(Clip()));
    }

    // ══════════ B00001：超时与熔断（NapCat 侧无响应时别再卡整条接收链）══════════

    [Fact]
    public async Task Timeout_gives_up_without_retry()
    {
        // NapCat 那条链路一步卡住就是 10 秒 ×3：超时后再问一轮只是把等待翻倍
        var calls = 0;
        var transcriber = Create(
            async (_, _, ct) =>
            {
                calls++;
                await Task.Delay(2_000, ct).ConfigureAwait(false);
                return JsonNode.Parse("""{ "text": "太晚了" }""");
            },
            new SpeechOptions { TimeoutMs = 40, MaxAttempts = 2, RetryDelayMs = 0 });

        var started = DateTimeOffset.UtcNow;
        Assert.Null(await transcriber.TranscribeAsync(Clip()));
        var elapsed = DateTimeOffset.UtcNow - started;

        Assert.Equal(1, calls);                       // 没有第二次
        Assert.True(elapsed < TimeSpan.FromSeconds(1), $"实际等了 {elapsed.TotalMilliseconds:0}ms");
        Assert.True(transcriber.IsAvailable);          // 一次超时还不至于熔断（默认阈值 2）
    }

    [Fact]
    public async Task Timeouts_count_as_failures_and_trip_the_breaker()
    {
        var calls = 0;
        var transcriber = Create(
            async (_, _, ct) =>
            {
                calls++;
                await Task.Delay(2_000, ct).ConfigureAwait(false);
                return null;
            },
            new SpeechOptions { TimeoutMs = 40, MaxConsecutiveFailures = 2, RetryDelayMs = 0 });

        Assert.Null(await transcriber.TranscribeAsync(Clip("m1")));
        Assert.Null(await transcriber.TranscribeAsync(Clip("m2")));

        Assert.False(transcriber.IsAvailable);         // 两次超时 → 熔断

        Assert.Null(await transcriber.TranscribeAsync(Clip("m3")));
        Assert.Equal(2, calls);                        // 熔断后不再发起调用
    }

    [Fact]
    public async Task Breaker_recovers_after_cooldown_without_restart()
    {
        // 没有半开的话，一次熔断要等进程重启 —— 用户看到的是「语音永远坏着」
        var calls = 0;
        var transcriber = Create(
            async (_, _, ct) =>
            {
                calls++;
                await Task.Delay(2_000, ct).ConfigureAwait(false);
                return null;
            },
            new SpeechOptions
            {
                TimeoutMs = 50,
                MaxConsecutiveFailures = 1,
                CooldownMs = 40,
                RetryDelayMs = 0,
            });

        Assert.Null(await transcriber.TranscribeAsync(Clip("m1")));
        Assert.False(transcriber.IsAvailable);

        // 等待给足余量：这条用例碰真实时钟，机器满负载（同时在 build）时 60ms 会抖
        await Task.Delay(400);                        // 等冷却到期

        Assert.True(transcriber.IsAvailable);
        Assert.Null(await transcriber.TranscribeAsync(Clip("m2")));
        Assert.Equal(2, calls);                        // 到期后又试了一次
    }

    [Fact]
    public async Task Cancellation_is_not_counted_as_platform_failure()
    {
        // 停机 / 收轮取消不该把 NapCat 判成"不支持语音"
        var transcriber = Create(
            async (_, _, ct) =>
            {
                await Task.Delay(2_000, ct).ConfigureAwait(false);
                return null;
            },
            new SpeechOptions { TimeoutMs = 5_000, MaxConsecutiveFailures = 1 });

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        for (var i = 0; i < 3; i++)
        {
            Assert.Null(await transcriber.TranscribeAsync(Clip($"c{i}"), cts.Token));
        }

        Assert.True(transcriber.IsAvailable);          // 三次取消，仍然可用
    }
}
