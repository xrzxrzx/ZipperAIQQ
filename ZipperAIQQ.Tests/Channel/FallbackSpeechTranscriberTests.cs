using Serilog;
using ZipperAIQQ.Channel.Audio;

namespace ZipperAIQQ.Tests.Channel;

/// <summary>
/// B00001：NapCat 自转 → 外部 ASR 的回退链。
/// </summary>
/// <remarks>
/// 顺序不能反：自转免费且不用下载音频；ASR 要取音频 + 转码 + 按秒计费。
/// </remarks>
public sealed class FallbackSpeechTranscriberTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private sealed class Stub(string? text, bool available = true) : ISpeechTranscriber
    {
        public int Calls { get; private set; }

        public bool IsAvailable { get; } = available;

        public Task<string?> TranscribeAsync(AudioClip clip, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(text);
        }
    }

    private static AudioClip Clip() => new("a.amr", "https://x/a.amr", MessageId: "m1");

    [Fact]
    public async Task Primary_success_skips_fallback()
    {
        var primary = new Stub("你好呀");
        var fallback = new Stub("不该被调到");

        var chain = new FallbackSpeechTranscriber(primary, fallback, Logger);

        Assert.Equal("你好呀", await chain.TranscribeAsync(Clip()));
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task Primary_null_falls_back_to_asr()
    {
        var primary = new Stub(null);
        var fallback = new Stub("ASR 的文本");

        var chain = new FallbackSpeechTranscriber(primary, fallback, Logger);

        Assert.Equal("ASR 的文本", await chain.TranscribeAsync(Clip()));
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task Primary_breaker_open_goes_straight_to_fallback()
    {
        // NapCat 那条已经不是"这次失败"而是"熔断中"：再等它一次没有意义
        var primary = new Stub(null, available: false);
        var fallback = new Stub("ASR 的文本");

        var chain = new FallbackSpeechTranscriber(primary, fallback, Logger);

        Assert.Equal("ASR 的文本", await chain.TranscribeAsync(Clip()));
        Assert.Equal(0, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task Both_fail_returns_null()
    {
        var chain = new FallbackSpeechTranscriber(new Stub(null), new Stub("  "), Logger);

        Assert.Null(await chain.TranscribeAsync(Clip()));
    }

    [Fact]
    public async Task Fallback_breaker_open_returns_primary_null()
    {
        var chain = new FallbackSpeechTranscriber(new Stub(null), new Stub("x", available: false), Logger);

        Assert.Null(await chain.TranscribeAsync(Clip()));
    }

    [Fact]
    public void IsAvailable_is_true_when_either_side_available()
    {
        Assert.True(new FallbackSpeechTranscriber(new Stub(null), new Stub(null), Logger).IsAvailable);
        Assert.True(new FallbackSpeechTranscriber(
            new Stub(null, available: false), new Stub(null), Logger).IsAvailable);
        Assert.False(new FallbackSpeechTranscriber(
            new Stub(null, available: false), new Stub(null, available: false), Logger).IsAvailable);
    }
}
