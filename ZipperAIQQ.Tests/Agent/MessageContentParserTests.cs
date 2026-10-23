using ZipperAIQQ.Agent;
using ZipperAIQQ.Agent.Faces;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>
/// 出站文本解析：<c>[表情:名字]</c> / <c>[表情包:摘要|emoji=id]</c> / 纯文本。
/// </summary>
/// <remarks>
/// 这块曾经出过两个实测 bug：
/// 1. 查不到的表情名会把 <c>[表情:摊手]</c> **字面量**发出去（丑）
/// 2. 表情包根本没有出站通道（用户发表情包 AI 复读不了）
/// </remarks>
public sealed class MessageContentParserTests : IDisposable
{
    private readonly string _catalogPath;
    private readonly FaceCatalog _catalog;

    public MessageContentParserTests()
    {
        _catalogPath = Path.Combine(Path.GetTempPath(), $"faces-{Guid.NewGuid():N}.json");

        // 必须是 { faces: [...] } 的对象结构（与 config/faces.qq.json 一致）
        File.WriteAllText(_catalogPath, """
            {
              "platform": "qq",
              "faces": [
                { "kind": "system", "id": "13", "name": "笑哭", "tags": ["开心"], "common": true },
                { "kind": "system", "id": "76", "name": "摊手", "tags": ["无奈"], "common": true },
                { "kind": "custom", "name": "猫猫头", "file": "https://example.com/cat.png", "common": false }
              ]
            }
            """);

        _catalog = FaceCatalog.Load(_catalogPath);
    }

    public void Dispose() => File.Delete(_catalogPath);

    // ─────────────────────────── 纯文本 ───────────────────────────

    [Fact]
    public void Plain_text_becomes_single_text_segment()
    {
        var segments = MessageContentParser.Parse("今天天气不错", _catalog);

        var text = Assert.Single(segments);
        Assert.Equal("今天天气不错", Assert.IsType<TextSegment>(text).Text);
    }

    [Fact]
    public void Empty_text_yields_no_segments()
        => Assert.Empty(MessageContentParser.Parse("", _catalog));

    // ─────────────────────────── 系统表情 ───────────────────────────

    [Fact]
    public void Explicit_face_marker_becomes_face_segment()
    {
        var segments = MessageContentParser.Parse("哈哈哈[表情:笑哭]", _catalog);

        Assert.Equal(2, segments.Count);
        Assert.IsType<TextSegment>(segments[0]);
        Assert.Equal("13", Assert.IsType<FaceSegment>(segments[1]).Id);
    }

    [Fact]
    public void Bare_bracket_face_is_also_recognized()
    {
        // 模型经常直接写 [笑哭]，这是"更自然"的写法，也必须认
        var segments = MessageContentParser.Parse("好耶[笑哭]", _catalog);

        Assert.Equal(2, segments.Count);
        Assert.Equal("13", Assert.IsType<FaceSegment>(segments[1]).Id);
    }

    [Fact]
    public void Unknown_explicit_face_is_dropped_not_echoed()
    {
        // [表情:xxx] 是明确"想发表情"的意图；查不到就丢掉标记，
        // 绝不能把 [表情:不存在的表情] 这个字面量发到聊天里
        var segments = MessageContentParser.Parse("嗯[表情:完全不存在的表情]", _catalog);

        Assert.DoesNotContain(segments, s => s is TextSegment t && t.Text.Contains("表情:"));
        Assert.Contains(segments, s => s is TextSegment t && t.Text == "嗯");
    }

    [Fact]
    public void Unknown_bare_bracket_is_kept_as_text()
    {
        // 裸写的 [xxx] 可能只是普通方括号（如 [重要]/[1]），原样保留更安全
        var segments = MessageContentParser.Parse("参考[注释]", _catalog);

        Assert.Contains(segments, s => s is TextSegment t && t.Text.Contains("[注释]"));
    }

    [Fact]
    public void Custom_face_becomes_image_segment()
    {
        var segments = MessageContentParser.Parse("[表情:猫猫头]", _catalog);

        var image = Assert.Single(segments.OfType<ImageSegment>());
        // 自定义表情走 ImageSegment(Url, FileId)，配置文件里的 file 落在 Url 上
        Assert.Equal("https://example.com/cat.png", image.Url);
    }

    // ─────────────────────────── 表情包（新增）───────────────────────────

    [Fact]
    public void Mface_marker_with_emoji_id_becomes_mface_segment()
    {
        // 渲染给模型的形式：[表情包:笑哭|emoji=39]，模型原样复制就能发回同一张
        var segments = MessageContentParser.Parse("[表情包:笑哭|emoji=39]", _catalog);

        var mface = Assert.Single(segments.OfType<MfaceSegment>());
        Assert.Equal("39", mface.EmojiId);
        Assert.Equal("笑哭", mface.Summary);
    }

    [Fact]
    public void Mface_marker_without_id_says_so_instead_of_silently_dropping()
    {
        // 没有 emoji id 就发不出去 —— 必须说清楚，不能静默吞掉，
        // 否则模型会以为自己发出去了
        var segments = MessageContentParser.Parse("[表情包:猫猫头]", _catalog);

        Assert.Empty(segments.OfType<MfaceSegment>());
        Assert.Contains(segments, s => s is TextSegment t && t.Text.Contains("没有 id"));
    }

    // ─────────────────────────── 分段 ───────────────────────────

    [Fact]
    public void SplitByBlankLine_splits_on_double_newline()
    {
        var parts = MessageContentParser.SplitByBlankLine("第一句\n\n第二句\n\n第三句", maxCount: 10);

        Assert.Equal(3, parts.Count);
        Assert.Equal("第一句", parts[0]);
    }

    [Fact]
    public void SplitByBlankLine_respects_max_count()
    {
        var parts = MessageContentParser.SplitByBlankLine("一\n\n二\n\n三\n\n四", maxCount: 2);

        Assert.Equal(2, parts.Count);
    }

    [Fact]
    public void SplitByBlankLine_on_single_line_returns_one()
    {
        var parts = MessageContentParser.SplitByBlankLine("只有一句", maxCount: 5);

        Assert.Single(parts);
    }
}
