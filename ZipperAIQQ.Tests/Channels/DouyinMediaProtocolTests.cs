using System.Text;
using System.Text.Json.Nodes;
using ZipperAIQQ.Server.Channels;

namespace ZipperAIQQ.Tests.Channels;

/// <summary>
/// B4 视频媒体帧：请求拼装 + 三条结果解析。
/// </summary>
/// <remarks>
/// <para>
/// 这几条全是**纯函数**，而它们对面是一个跑在浏览器里的 sidecar —— 真机之前，
/// 这里就是唯一能证明「服务端看得懂它回了什么」的地方。夹具形状照 sidecar 的
/// `media.ts` + `main.ts` 实际输出（`media.video` / `media.frames` / `media.audio`）。
/// </para>
/// <para>
/// 夹具一律走 <see cref="JsonNode.Parse(string)"/> 而不是直接 <c>new JsonObject</c>：
/// 生产路径永远是「解析 sidecar 发来的 JSON」，直接构造的节点底层类型不同
/// （<c>JsonValue&lt;int&gt;</c> 取 <c>long</c> 会失败），用它当夹具会测出假绿。
/// </para>
/// </remarks>
public sealed class DouyinMediaProtocolTests
{
    private static string Base64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static JsonObject Data(string json) => JsonNode.Parse(json)!.AsObject();

    // ───────────── 请求拼装 ─────────────

    [Fact]
    public void BuildMediaRequest_CarriesTypeRequestIdAndPayload()
    {
        var payload = new JsonObject { ["itemId"] = "7706296015465071715" };

        var json = DouyinProtocol.BuildMediaRequest(DouyinFrameTypes.MediaVideo, "r-1", payload);
        var frame = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("media.video", frame["type"]!.GetValue<string>());
        var data = frame["data"]!.AsObject();
        Assert.Equal("r-1", data["requestId"]!.GetValue<string>());
        Assert.Equal("7706296015465071715", data["itemId"]!.GetValue<string>());
    }

    [Fact]
    public void BuildMediaRequest_ClonesNodes_SoTheSamePayloadCanBeReused()
    {
        // 直接挂到新对象上会因「节点已有父节点」抛出；这里连做两次证明是克隆
        var payload = new JsonObject { ["itemId"] = "1", ["maxWidth"] = 720 };

        var first = DouyinProtocol.BuildMediaRequest(DouyinFrameTypes.MediaFrames, "r-1", payload);
        var second = DouyinProtocol.BuildMediaRequest(DouyinFrameTypes.MediaFrames, "r-2", payload);

        Assert.Contains("\"maxWidth\":720", first, StringComparison.Ordinal);
        Assert.Contains("\"maxWidth\":720", second, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildMediaRequest_RejectsEmptyTypeOrRequestId()
    {
        Assert.Throws<ArgumentException>(() =>
            DouyinProtocol.BuildMediaRequest(" ", "r-1", new JsonObject()));
        Assert.Throws<ArgumentException>(() =>
            DouyinProtocol.BuildMediaRequest(DouyinFrameTypes.MediaVideo, "", new JsonObject()));
    }

    // ───────────── media.video ─────────────

    [Fact]
    public void ParseMediaVideo_ReadsIdTitleDurationAndUrls()
    {
        var data = Data("""
            {
              "requestId": "r-1",
              "ok": true,
              "video": {
                "itemId": "7399000000000000001",
                "title": "戒网瘾 测试",
                "durationMs": 28666,
                "playUrls": ["https://v26-web.douyinvod.com/a.mp4", "https://v3-web.douyinvod.com/b.m3u8"]
              }
            }
            """);

        var info = DouyinProtocol.ParseMediaVideo(data);

        Assert.NotNull(info);
        Assert.Equal("7399000000000000001", info.ItemId);
        Assert.Equal("戒网瘾 测试", info.Title);
        Assert.Equal(28_666, info.DurationMs);
        Assert.Equal(2, info.PlayUrls.Count);
    }

    [Fact]
    public void ParseMediaVideo_ToleratesMissingTitleAndUrls()
    {
        // 「时长取到了、但没地址」是中间状态：上层要能说人话，不该被当成解析失败
        var data = Data("""{ "video": { "itemId": "42", "durationMs": 5000 } }""");

        var info = DouyinProtocol.ParseMediaVideo(data);

        Assert.NotNull(info);
        Assert.Equal(string.Empty, info.Title);
        Assert.Equal(5_000, info.DurationMs);
        Assert.Empty(info.PlayUrls);
    }

    [Fact]
    public void ParseMediaVideo_ReturnsNullWhenVideoOrIdMissing()
    {
        Assert.Null(DouyinProtocol.ParseMediaVideo(null));
        Assert.Null(DouyinProtocol.ParseMediaVideo(Data("{}")));
        Assert.Null(DouyinProtocol.ParseMediaVideo(Data("""{ "video": "不是对象" }""")));
        Assert.Null(DouyinProtocol.ParseMediaVideo(Data("""{ "video": {} }""")));
    }

    [Fact]
    public void ParseMediaVideo_DropsNonStringUrls()
    {
        var data = Data("""
            { "video": { "itemId": "42", "playUrls": [null, 42, "https://x/a.mp4"] } }
            """);

        var info = DouyinProtocol.ParseMediaVideo(data);

        Assert.NotNull(info);
        Assert.Equal(["https://x/a.mp4"], info.PlayUrls);
    }

    // ───────────── media.frames ─────────────

    [Fact]
    public void ParseMediaFrames_AssignsOrderAndDecodesJpeg()
    {
        var data = Data($$"""
            {
              "ok": true,
              "durationMs": 28666,
              "width": 720,
              "height": 1400,
              "frames": [
                { "atMs": 40, "jpegBase64": "{{Base64("first")}}" },
                { "atMs": 17920, "jpegBase64": "{{Base64("third")}}" }
              ]
            }
            """);

        var frames = DouyinProtocol.ParseMediaFrames(data);

        Assert.NotNull(frames);
        Assert.Equal(28_666, frames.DurationMs);
        Assert.Equal(720, frames.Width);
        Assert.Equal(1400, frames.Height);
        Assert.Equal(2, frames.Frames.Count);
        Assert.Equal(0, frames.Frames[0].Index);
        Assert.Equal(1, frames.Frames[1].Index);
        Assert.Equal(40, frames.Frames[0].AtMs);
        Assert.Equal("first", Encoding.UTF8.GetString(frames.Frames[0].Jpeg));
    }

    [Fact]
    public void ParseMediaFrames_AcceptsJpegAliasKey()
    {
        var data = Data($$"""
            { "frames": [ { "atMs": 1, "jpeg": "{{Base64("x")}}" } ] }
            """);

        var frames = DouyinProtocol.ParseMediaFrames(data);

        Assert.NotNull(frames);
        Assert.Single(frames.Frames);
    }

    [Fact]
    public void ParseMediaFrames_ReturnsNullWhenAnyFrameIsBroken()
    {
        // 少一张的批次会被上层当成「这段视频就这样」，而张数与时间戳的不一致最难被发现
        var broken = Data($$"""
            {
              "frames": [
                { "atMs": 1, "jpegBase64": "{{Base64("ok")}}" },
                { "atMs": 2, "jpegBase64": "不是 base64!!" }
              ]
            }
            """);

        Assert.Null(DouyinProtocol.ParseMediaFrames(broken));
        Assert.Null(DouyinProtocol.ParseMediaFrames(Data("""{ "frames": [] }""")));
        Assert.Null(DouyinProtocol.ParseMediaFrames(Data("{}")));
    }

    [Fact]
    public void ParseMediaFrames_ReturnsNullWhenBase64IsEmpty()
    {
        var data = Data("""{ "frames": [ { "atMs": 1, "jpegBase64": "" } ] }""");

        Assert.Null(DouyinProtocol.ParseMediaFrames(data));
    }

    // ───────────── media.audio ─────────────

    private static byte[] FakeWav(int dataBytes = 320)
    {
        var wav = new byte[44 + dataBytes];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(dataBytes).CopyTo(wav, 40);
        return wav;
    }

    [Fact]
    public void ParseMediaAudio_ReadsWavSampleRateAndRange()
    {
        var data = Data($$"""
            {
              "ok": true,
              "wav": "{{Convert.ToBase64String(FakeWav())}}",
              "sampleRate": 16000,
              "startMs": 0,
              "endMs": 20000,
              "durationMs": 28666
            }
            """);

        var audio = DouyinProtocol.ParseMediaAudio(data);

        Assert.NotNull(audio);
        Assert.Equal(364, audio.Wav.Length);
        Assert.Equal(16_000, audio.SampleRate);
        Assert.Equal(0, audio.StartMs);
        Assert.Equal(20_000, audio.EndMs);
        Assert.Equal(28_666, audio.DurationMs);
    }

    [Fact]
    public void ParseMediaAudio_DefaultsSampleRateWhenMissing()
    {
        var data = Data($$"""
            { "wav": "{{Convert.ToBase64String(FakeWav())}}", "startMs": 0, "endMs": 1000 }
            """);

        var audio = DouyinProtocol.ParseMediaAudio(data);

        Assert.NotNull(audio);
        Assert.Equal(16_000, audio.SampleRate);
        Assert.Equal(0, audio.DurationMs);
    }

    [Fact]
    public void ParseMediaAudio_ReturnsNullOnMissingOrInvalidPayload()
    {
        var headerOnly = Convert.ToBase64String(new byte[44]);
        var wav = Convert.ToBase64String(FakeWav());

        Assert.Null(DouyinProtocol.ParseMediaAudio(null));
        Assert.Null(DouyinProtocol.ParseMediaAudio(Data("{}")));

        // 只有头（44 字节）不可能是音频
        Assert.Null(DouyinProtocol.ParseMediaAudio(
            Data($$"""{ "wav": "{{headerOnly}}", "startMs": 0, "endMs": 1000 }""")));

        // 区间反了
        Assert.Null(DouyinProtocol.ParseMediaAudio(
            Data($$"""{ "wav": "{{wav}}", "startMs": 2000, "endMs": 1000 }""")));

        // 坏 base64
        Assert.Null(DouyinProtocol.ParseMediaAudio(
            Data("""{ "wav": "***not base64***", "startMs": 0, "endMs": 1000 }""")));
    }

    // ───────────── result 帧携带原始 data ─────────────

    [Fact]
    public void ParseResult_KeepsRawDataForMediaQueries()
    {
        var data = Data("""
            { "requestId": "r-9", "ok": true, "video": { "itemId": "1" } }
            """);

        var result = DouyinProtocol.ParseResult(data);

        Assert.NotNull(result);
        Assert.True(result.Ok);
        Assert.NotNull(result.Data);
        Assert.Equal("1", DouyinProtocol.ParseMediaVideo(result.Data)!.ItemId);
    }
}
