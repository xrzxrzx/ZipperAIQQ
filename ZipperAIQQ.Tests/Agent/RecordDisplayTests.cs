using ZipperAIQQ.Agent;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>B00001：语音段渲染的三种情形必须分开（模型据此决定要不要追问）。</summary>
public sealed class RecordDisplayTests
{
    [Fact]
    public void Transcribed_text_is_rendered_inline()
    {
        var text = RecordDisplay.Render(new RecordSegment("a.amr", null, "你好呀", "fetch_ptt_text"));

        Assert.Equal("[语音转写:你好呀]", text);
    }

    [Fact]
    public void Failed_transcription_says_it_failed()
    {
        // 与「没试」分开：这条该让模型追问「语音没听清，再说一次？」
        var text = RecordDisplay.Render(
            new RecordSegment("a.amr", null, null, null) { TranscriptionFailed = true });

        Assert.Equal("[语音（没能转成文字）]", text);
    }

    [Fact]
    public void Not_attempted_stays_plain_placeholder()
    {
        // 转写关闭 / 没试过：只是「对方发了条语音」，不该暗示"听不清"
        var text = RecordDisplay.Render(new RecordSegment("a.amr", null, null, null));

        Assert.Equal("[语音]", text);
    }

    [Fact]
    public void Blank_text_with_failure_flag_is_treated_as_failure()
    {
        var text = RecordDisplay.Render(
            new RecordSegment("a.amr", null, "   ", null) { TranscriptionFailed = true });

        Assert.Equal("[语音（没能转成文字）]", text);
    }
}
