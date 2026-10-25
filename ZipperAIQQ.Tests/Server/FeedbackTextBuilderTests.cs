using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Feedback;
using ZipperAIQQ.Storage.Messages;

namespace ZipperAIQQ.Tests.Server;

/// <summary>主人反馈文案（问题记录 03 #4）。</summary>
public sealed class FeedbackTextBuilderTests
{
    private static ChatRef Group() => new() { Platform = "qq", Type = ChatType.Group, Id = "123" };

    private static IncomingNotice Recall(string actor = "20001") => new()
    {
        Chat = Group(),
        OccurredAt = DateTimeOffset.Now,
        Type = NoticeType.MessageRecalled,
        RawType = "notice.group_recall",
        Actor = new SenderInfo { UserId = actor, Nickname = "小明" },
        RelatedMessageId = "m1",
    };

    private static StoredMessage Stored(string text, params MessageSegment[] extra)
    {
        var message = new IncomingMessage
        {
            Chat = Group(),
            OccurredAt = DateTimeOffset.Now,
            Sender = new SenderInfo { UserId = "20001", Nickname = "小明" },
            Segments = [.. extra, new TextSegment(text)],
        };

        return new StoredMessage(1, message, DateTimeOffset.Now, false, false);
    }

    [Fact]
    public void Recall_IncludesOriginalText()
    {
        var text = FeedbackTextBuilder.Build(Recall(), Stored("被撤回的内容"), "10000");

        Assert.Contains("撤回了", text);
        Assert.Contains("被撤回的内容", text);
        Assert.Contains("小明", text);
    }

    [Fact]
    public void Recall_ImageOnly_UsesPlaceholder()
    {
        var stored = Stored(string.Empty, new ImageSegment("https://example.com/a.jpg", "a.jpg"));
        var text = FeedbackTextBuilder.Build(Recall(), stored, "10000");

        Assert.Contains("[图片]", text);
    }

    [Fact]
    public void Recall_MissingOriginal_SaysUnavailable()
    {
        var text = FeedbackTextBuilder.Build(Recall(), null, "10000");
        Assert.Contains("原文不可得", text);
    }

    [Fact]
    public void OtherNotice_UsesStandardDescription()
    {
        var notice = new IncomingNotice
        {
            Chat = Group(),
            OccurredAt = DateTimeOffset.Now,
            Type = NoticeType.Poke,
            RawType = "notice.poke",
            Actor = new SenderInfo { UserId = "20001", Nickname = "小明" },
            Target = new SenderInfo { UserId = "10000", Nickname = "我" },
        };

        var text = FeedbackTextBuilder.Build(notice, null, "10000");

        Assert.Contains("[反馈]", text);
        Assert.Contains("戳了你", text);
    }
}
