using System.Text.Json.Nodes;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Channel.NapCat;

namespace ZipperAIQQ.Tests.Channel;

/// <summary>会话标识与入站事件解析。</summary>
/// <remarks>
/// <c>SessionId</c> 是会话隔离的唯一依据，格式错了会导致：
/// 会话级人设查不到、历史串到别的会话、名单匹配不上。
/// </remarks>
public sealed class ChatRefTests
{
    [Fact]
    public void Private_chat_session_id_uses_p_prefix()
        => Assert.Equal("qq:p123456", new ChatRef
        {
            Platform = "qq",
            Type = ChatType.Private,
            Id = "123456",
        }.SessionId);

    [Fact]
    public void Group_chat_session_id_uses_g_prefix()
        => Assert.Equal("qq:g20001", new ChatRef
        {
            Platform = "qq",
            Type = ChatType.Group,
            Id = "20001",
        }.SessionId);

    [Fact]
    public void Different_platforms_do_not_collide()
    {
        // 不同平台的同号群必须区分开（否则跨平台接入后会共用上下文）
        var qq = new ChatRef { Platform = "qq", Type = ChatType.Group, Id = "1" };
        var tg = new ChatRef { Platform = "tg", Type = ChatType.Group, Id = "1" };

        Assert.NotEqual(qq.SessionId, tg.SessionId);
    }

    [Fact]
    public void Private_and_group_with_same_id_differ()
    {
        var priv = new ChatRef { Platform = "qq", Type = ChatType.Private, Id = "1" };
        var grp = new ChatRef { Platform = "qq", Type = ChatType.Group, Id = "1" };

        Assert.NotEqual(priv.SessionId, grp.SessionId);
    }
}

/// <summary>
/// OneBot 事件 → 平台无关消息。
/// </summary>
/// <remarks>
/// 这里最容易漏的是 <b>mface</b>（表情包）：漏了就等于 AI 看不见表情包；
/// 还有"未专门处理的段"必须转成占位（不能丢），否则模型以为对方什么都没发。
/// </remarks>
public sealed class OneBotEventMapperTests
{
    private static IncomingMessage MapPrivate(string messageJson)
    {
        var raw = $$"""
            {
              "post_type": "message",
              "message_type": "private",
              "message_id": 1001,
              "user_id": 10001,
              "self_id": 30001,
              "time": 1700000000,
              "raw_message": "",
              "sender": { "user_id": 10001, "nickname": "示例用户" },
              "message": {{messageJson}}
            }
            """;

        var map = OneBotEventMapper.Map(JsonNode.Parse(raw), "qq");
        Assert.NotNull(map);
        return Assert.IsType<IncomingMessage>(map);
    }

    [Fact]
    public void Plain_text_segment()
    {
        var msg = MapPrivate("""[ { "type": "text", "data": { "text": "在吗" } } ]""");

        var text = Assert.Single(msg.Segments.OfType<TextSegment>());
        Assert.Equal("在吗", text.Text);
        Assert.Equal("qq:p10001", msg.Chat.SessionId);
    }

    [Fact]
    public void At_segment_becomes_mention()
    {
        var msg = MapPrivate("""[ { "type": "at", "data": { "qq": "30001" } } ]""");

        var mention = Assert.Single(msg.Segments.OfType<MentionSegment>());
        Assert.Equal("30001", mention.UserId);
    }

    [Fact]
    public void Face_segment_keeps_id()
    {
        var msg = MapPrivate("""[ { "type": "face", "data": { "id": "13" } } ]""");

        var face = Assert.Single(msg.Segments.OfType<FaceSegment>());
        Assert.Equal("13", face.Id);
    }

    [Fact]
    public void Mface_segment_is_kept_with_emoji_id()
    {
        // 表情包必须单独成段（带 emoji_id），否则出站复读不回去
        var msg = MapPrivate(
            """[ { "type": "mface", "data": { "emoji_id": "39", "summary": "笑哭", "url": "https://x/y.gif" } } ]""");

        var mface = Assert.Single(msg.Segments.OfType<MfaceSegment>());
        Assert.Equal("39", mface.EmojiId);
        Assert.Equal("笑哭", mface.Summary);
        Assert.Equal("https://x/y.gif", mface.Url);
    }

    [Fact]
    public void Image_segment_keeps_url()
    {
        var msg = MapPrivate(
            """[ { "type": "image", "data": { "url": "https://x/pic.png", "file": "abc.image" } } ]""");

        var image = Assert.Single(msg.Segments.OfType<ImageSegment>());
        Assert.Equal("https://x/pic.png", image.Url);
    }

    [Theory]
    [InlineData("video", "[视频]")]
    [InlineData("file", "[文件]")]
    [InlineData("forward", "[合并转发]")]
    public void Unhandled_segments_become_placeholders(string type, string expected)
    {
        // 关键：**不能丢**。丢了模型会以为对方什么都没发，
        // 用户看到的就是"它不知道我发了东西"
        var msg = MapPrivate($$"""[ { "type": "{{type}}", "data": { } } ]""");

        var text = Assert.Single(msg.Segments.OfType<TextSegment>());
        Assert.Contains(expected, text.Text);
    }

    [Fact]
    public void Record_segment_keeps_file_id_instead_of_placeholder()
    {
        // 语音不再降级成字面量「[语音]」：通道层要拿 file 去转写（问题记录 #1）
        var msg = MapPrivate(
            """[ { "type": "record", "data": { "file": "abc.amr", "url": "https://x/abc.amr" } } ]""");

        var record = Assert.Single(msg.Segments.OfType<RecordSegment>());
        Assert.Equal("abc.amr", record.FileId);
        Assert.Equal("https://x/abc.amr", record.Url);
        Assert.Null(record.Text);
        Assert.Null(record.Transcriber);
    }

    [Fact]
    public void Group_increase_without_target_id_uses_user_id_as_target()
    {
        // 群成员增减事件没有 target_id：不补这一步，文案会退化成「某人」、
        // 自激过滤也没有靶子（问题记录 #39）
        var raw = """
            {
              "post_type": "notice",
              "notice_type": "group_increase",
              "sub_type": "approve",
              "group_id": 20001,
              "user_id": 10001,
              "operator_id": 30001,
              "time": 1700000000
            }
            """;

        var notice = Assert.IsType<IncomingNotice>(OneBotEventMapper.Map(JsonNode.Parse(raw), "qq"));
        Assert.Equal(NoticeType.GroupIncrease, notice.Type);
        Assert.Equal("10001", notice.Target?.UserId);
        Assert.Equal("30001", notice.Actor?.UserId);
    }

    [Fact]
    public void Group_increase_without_operator_does_not_pretend_user_is_actor()
    {
        // 自己入群时没有 operator_id：不能把 user_id 同时当操作者（否则「谁入群」和
        // 「谁操作的」混成一个，自激过滤也没法只看 Target）
        var raw = """
            {
              "post_type": "notice",
              "notice_type": "group_increase",
              "sub_type": "invite",
              "group_id": 20001,
              "user_id": 30001,
              "time": 1700000000
            }
            """;

        var notice = Assert.IsType<IncomingNotice>(OneBotEventMapper.Map(JsonNode.Parse(raw), "qq"));
        Assert.Null(notice.Actor);
        Assert.Equal("30001", notice.Target?.UserId);
    }

    [Fact]
    public void Reply_segment_is_captured()
    {
        var raw = """
            {
              "post_type": "message",
              "message_type": "group",
              "group_id": 20001,
              "message_id": 2002,
              "user_id": 10001,
              "self_id": 30001,
              "time": 1700000000,
              "raw_message": "",
              "sender": { "user_id": 10001, "nickname": "示例成员" },
              "message": [
                { "type": "reply", "data": { "id": "1999" } },
                { "type": "text", "data": { "text": "这个怎么说" } }
              ]
            }
            """;

        var map = OneBotEventMapper.Map(JsonNode.Parse(raw), "qq");
        var msg = Assert.IsType<IncomingMessage>(map);

        Assert.Equal("qq:g20001", msg.Chat.SessionId);
        Assert.Equal("1999", msg.ReplyToMessageId);
        Assert.Contains(msg.Segments, s => s is TextSegment t && t.Text == "这个怎么说");
    }

    [Fact]
    public void Non_message_event_returns_null()
    {
        // 只映射 message / notice；心跳、元事件之类一律忽略
        var raw = """{ "post_type": "meta_event", "meta_event_type": "heartbeat" }""";

        Assert.Null(OneBotEventMapper.Map(JsonNode.Parse(raw), "qq"));
    }

    [Fact]
    public void Notice_event_maps_to_notice_not_message()
    {
        // 打字状态、群文件变动这类是 notice：会记录，但不是"消息"
        var raw = """
            {
              "post_type": "notice",
              "notice_type": "notify",
              "sub_type": "input_status",
              "user_id": 10001,
              "time": 1700000000
            }
            """;

        var map = OneBotEventMapper.Map(JsonNode.Parse(raw), "qq");

        Assert.NotNull(map);
        Assert.IsNotType<IncomingMessage>(map);
    }
}
