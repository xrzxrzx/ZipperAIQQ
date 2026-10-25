using ZipperAIQQ.Channel.Filtering;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Notices;

namespace ZipperAIQQ.Tests.Channel;

/// <summary>自身通知判定（问题记录 02 #3/#4）。</summary>
public sealed class NoticeSelfPolicyTests
{
    private const string Self = "10000";
    private const string A = "20001";
    private const string B = "20002";

    private static IncomingNotice Notice(NoticeType type, string? actor, string? target) => new()
    {
        Chat = new ChatRef { Platform = "qq", Type = ChatType.Group, Id = "g1" },
        OccurredAt = DateTimeOffset.Now,
        Type = type,
        RawType = "notice",
        Actor = actor is null ? null : new SenderInfo { UserId = actor, Nickname = actor },
        Target = target is null ? null : new SenderInfo { UserId = target, Nickname = target },
    };

    [Fact]
    public void Poke_A_to_B_is_kept_and_marked_as_not_you()
    {
        var notice = Notice(NoticeType.Poke, A, B);

        Assert.False(NoticeSelfPolicy.ShouldDrop(notice, Self));
        Assert.False(NoticeSelfPolicy.IsAboutSelf(notice, Self));

        var text = NoticeResponder.Describe(notice, Self);
        Assert.Contains("不是你", text);
        Assert.DoesNotContain("戳了你", text);
    }

    [Fact]
    public void Poke_A_to_self_is_kept_and_uses_second_person()
    {
        var notice = Notice(NoticeType.Poke, A, Self);

        Assert.False(NoticeSelfPolicy.ShouldDrop(notice, Self));
        Assert.True(NoticeSelfPolicy.IsAboutSelf(notice, Self));

        var text = NoticeResponder.Describe(notice, Self);
        Assert.Contains("戳了你", text);
    }

    [Fact]
    public void Poke_from_self_is_dropped()
    {
        var notice = Notice(NoticeType.Poke, Self, B);
        Assert.True(NoticeSelfPolicy.ShouldDrop(notice, Self));
    }

    [Fact]
    public void Unknown_self_id_never_matches_or_drops()
    {
        var notice = Notice(NoticeType.Poke, A, Self);

        Assert.False(NoticeSelfPolicy.IsAboutSelf(notice, null));
        Assert.False(NoticeSelfPolicy.ShouldDrop(notice, null));
    }

    [Fact]
    public void Group_increase_with_self_as_target_is_dropped()
    {
        var notice = Notice(NoticeType.GroupIncrease, A, Self);
        Assert.True(NoticeSelfPolicy.ShouldDrop(notice, Self));
    }

    [Fact]
    public void Group_increase_of_other_member_is_kept()
    {
        var notice = Notice(NoticeType.GroupIncrease, A, B);
        Assert.False(NoticeSelfPolicy.ShouldDrop(notice, Self));
    }
}
