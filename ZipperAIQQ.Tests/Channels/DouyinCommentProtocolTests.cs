using System.Text.Json.Nodes;
using ZipperAIQQ.Server.Channels;

namespace ZipperAIQQ.Tests.Channels;

/// <summary>
/// B5 评论帧：请求拼装 + 两条结果解析 + 帧名锁值。
/// </summary>
/// <remarks>
/// 与 B4 的媒体帧同一条理由：这几条都是**纯函数**，对面是跑在浏览器里的 sidecar ——
/// 真机之前，这里就是唯一能证明「服务端看得懂它回了什么」的地方。
/// 夹具形状照 sidecar 的 `comment.ts` + `main.ts` 实际输出。
/// </remarks>
public sealed class DouyinCommentProtocolTests
{
    private static JsonObject Data(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Frame_names_are_locked()
    {
        // 帧名是跨语言的契约：改名必须两边一起改，改了就该红
        Assert.Equal("comment.list", DouyinFrameTypes.CommentList);
        Assert.Equal("comment.reply", DouyinFrameTypes.CommentReply);
    }

    [Fact]
    public void Reply_payload_skips_empty_locators()
    {
        var payload = new ZipperAIQQ.Server.Douyin.DouyinCommentReplyRequest(
            ItemId: "7706296015465071715",
            Text: "在的",
            Commit: true,
            CommentId: "7412345678901234567",
            Author: "  ",
            TextMatch: null).ToPayload();

        Assert.Equal("7706296015465071715", payload["itemId"]!.GetValue<string>());
        Assert.Equal("7412345678901234567", payload["commentId"]!.GetValue<string>());
        Assert.True(payload["commit"]!.GetValue<bool>());

        // 空字段**不下发**：空串会参与匹配，把定位条件变成一个永远匹配不上的谓词
        Assert.False(payload.ContainsKey("author"));
        Assert.False(payload.ContainsKey("textMatch"));
    }

    [Fact]
    public void Reply_payload_carries_author_and_text_probe_when_no_id()
    {
        var payload = new ZipperAIQQ.Server.Douyin.DouyinCommentReplyRequest(
            ItemId: "7706296015465071715",
            Text: "在的",
            Commit: false,
            Author: " 拉链 ",
            TextMatch: "@戒网瘾").ToPayload();

        Assert.Equal("拉链", payload["author"]!.GetValue<string>());
        Assert.Equal("@戒网瘾", payload["textMatch"]!.GetValue<string>());
        Assert.False(payload["commit"]!.GetValue<bool>());
        Assert.False(payload.ContainsKey("commentId"));
    }

    // ───────────── comment.list ─────────────

    [Fact]
    public void ParseCommentList_reads_every_field()
    {
        var data = Data("""
            {
              "requestId": "r-1",
              "ok": true,
              "itemId": "7706296015465071715",
              "pages": 2,
              "truncated": true,
              "note": "只回了前 30 条（后面还有）",
              "comments": [
                {
                  "id": "7412345678901234567",
                  "author": "拉链",
                  "authorId": "88892527967",
                  "text": "@戒网瘾 这条视频到底在讲什么？",
                  "time": "2小时前",
                  "likes": 128,
                  "replyCount": 3,
                  "pinned": true,
                  "mentions": ["戒网瘾"],
                  "key": "7412345678901234567"
                }
              ]
            }
            """);

        var list = DouyinProtocol.ParseCommentList(data);

        Assert.NotNull(list);
        Assert.Equal("7706296015465071715", list.ItemId);
        Assert.Equal(2, list.Pages);
        Assert.True(list.Truncated);
        Assert.Equal("只回了前 30 条（后面还有）", list.Note);

        var comment = Assert.Single(list.Comments);
        Assert.Equal("7412345678901234567", comment.Id);
        Assert.Equal("拉链", comment.Author);
        Assert.Equal("88892527967", comment.AuthorId);
        Assert.Equal("@戒网瘾 这条视频到底在讲什么？", comment.Text);
        Assert.Equal("2小时前", comment.Time);
        Assert.Equal(128, comment.Likes);
        Assert.Equal(3, comment.ReplyCount);
        Assert.True(comment.Pinned);
        Assert.Equal(["戒网瘾"], comment.Mentions);
    }

    [Fact]
    public void ParseCommentList_keeps_the_good_ones_when_one_is_broken()
    {
        // 单条解析不出来只跳过那一条：整包失败会让模型一直重试到超时
        var data = Data("""
            {
              "itemId": "7706296015465071715",
              "comments": [
                { "author": "甲" },
                { "author": "乙", "text": "第一条", "likes": 3 },
                "不是对象"
              ]
            }
            """);

        var list = DouyinProtocol.ParseCommentList(data);

        Assert.NotNull(list);
        var comment = Assert.Single(list.Comments);
        Assert.Equal("乙", comment.Author);
        Assert.Null(comment.Id);
        Assert.Equal(3, comment.Likes);
        Assert.Equal(1, list.Pages);
        Assert.False(list.Truncated);
    }

    [Fact]
    public void ParseCommentList_requires_item_id()
    {
        Assert.Null(DouyinProtocol.ParseCommentList(Data("""{ "comments": [] }""")));
        Assert.Null(DouyinProtocol.ParseCommentList(null));
    }

    [Fact]
    public void ParseCommentList_tolerates_missing_comments_array()
    {
        var list = DouyinProtocol.ParseCommentList(Data("""{ "itemId": "7706296015465071715" }"""));

        Assert.NotNull(list);
        Assert.Empty(list.Comments);
    }

    // ───────────── comment.reply ─────────────

    [Fact]
    public void ParseCommentReply_reads_dry_run_and_readback()
    {
        var data = Data("""
            {
              "requestId": "r-2",
              "ok": true,
              "itemId": "7706296015465071715",
              "dryRun": true,
              "matchedBy": "author+text",
              "readback": false,
              "note": "干跑：已点开回复、写入并校验 @ 前缀，但没有提交"
            }
            """);

        var reply = DouyinProtocol.ParseCommentReply(data);

        Assert.NotNull(reply);
        Assert.True(reply.DryRun);
        Assert.Equal("author+text", reply.MatchedBy);
        Assert.False(reply.Readback);
        Assert.Contains("干跑", reply.Note!, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCommentReply_requires_item_id()
    {
        Assert.Null(DouyinProtocol.ParseCommentReply(Data("""{ "ok": true }""")));
    }
}
