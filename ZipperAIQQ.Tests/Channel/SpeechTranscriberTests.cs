using System.Text.Json.Nodes;
using Serilog;
using ZipperAIQQ.Channel.Audio;
using ZipperAIQQ.Channel.NapCat;

namespace ZipperAIQQ.Tests.Channel;

/// <summary>
/// NapCat 语音转写（<c>fetch_ptt_text</c>）的容错行为。
/// </summary>
/// <remarks>
/// 真实 action 是否存在要等 NapCat 在线才能验；这里测的是**失败不拖垮接收**：
/// 返回 null、连续失败后熔断、成功会重置计数。
/// </remarks>
public sealed class SpeechTranscriberTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private static NapCatPttTextTranscriber Create(
        Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> callApi,
        SpeechOptions? options = null) =>
        new(callApi, options ?? new SpeechOptions(), Logger);

    [Fact]
    public async Task Returns_text_from_object_payload()
    {
        var transcriber = Create((_, _, _) =>
            Task.FromResult<JsonNode?>(JsonNode.Parse("""{ "text": "你好呀" }""")));

        var text = await transcriber.TranscribeAsync(new AudioClip("a.amr", null));

        Assert.Equal("你好呀", text);
        Assert.True(transcriber.IsAvailable);
    }

    [Fact]
    public async Task Returns_text_from_plain_string_payload()
    {
        var transcriber = Create((_, _, _) =>
            Task.FromResult<JsonNode?>(JsonValue.Create("直接是字符串")));

        Assert.Equal("直接是字符串", await transcriber.TranscribeAsync(new AudioClip("a.amr", null)));
    }

    [Fact]
    public async Task Missing_file_id_does_not_call_api()
    {
        var calls = 0;
        var transcriber = Create((_, _, _) =>
        {
            calls++;
            return Task.FromResult<JsonNode?>(null);
        });

        Assert.Null(await transcriber.TranscribeAsync(new AudioClip(null, "https://x/a.amr")));
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
        Assert.Null(await transcriber.TranscribeAsync(new AudioClip("a.amr", null)));
        Assert.Equal(0, calls);
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
            new SpeechOptions { MaxConsecutiveFailures = 3 });

        for (var i = 0; i < 5; i++)
        {
            Assert.Null(await transcriber.TranscribeAsync(new AudioClip("a.amr", null)));
        }

        // 第 3 次失败后熔断，后两次不再发起调用（否则每条语音都白等一次超时）
        Assert.Equal(3, calls);
        Assert.False(transcriber.IsAvailable);
    }

    [Fact]
    public async Task Success_resets_failure_counter()
    {
        var calls = 0;
        var transcriber = Create(
            (_, _, _) =>
            {
                calls++;
                // 偶数次成功、奇数次失败
                return Task.FromResult<JsonNode?>(
                    calls % 2 == 0 ? JsonNode.Parse("""{ "text": "恢复" }""") : null);
            },
            new SpeechOptions { MaxConsecutiveFailures = 2 });

        Assert.Null(await transcriber.TranscribeAsync(new AudioClip("a.amr", null))); // 失败 1
        Assert.Equal("恢复", await transcriber.TranscribeAsync(new AudioClip("a.amr", null))); // 成功，清零
        Assert.Null(await transcriber.TranscribeAsync(new AudioClip("a.amr", null))); // 失败 1（重新计）

        Assert.True(transcriber.IsAvailable);
    }

    [Fact]
    public async Task Exception_is_swallowed_as_null()
    {
        var transcriber = Create((_, _, _) =>
            throw new InvalidOperationException("boom"));

        Assert.Null(await transcriber.TranscribeAsync(new AudioClip("a.amr", null)));
    }
}
