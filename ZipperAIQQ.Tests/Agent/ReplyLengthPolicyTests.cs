using ZipperAIQQ.Agent;
using ZipperAIQQ.Agent.Length;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>R-11 / J-1：场景判据、预算与句末裁剪。</summary>
public sealed class ReplyLengthPolicyTests
{
    private static readonly ReplyLengthOptions Options = new();

    [Fact]
    public void Group_interjection_in_jev_mode_gets_short_budget() =>
        Assert.Equal(
            ReplyLengthScene.Short,
            ReplyLengthPolicy.Decide(ReplyMode.Jev, ChatType.Group, mentioned: false, "哈哈哈"));

    [Fact]
    public void Private_chat_gets_normal_budget() =>
        Assert.Equal(
            ReplyLengthScene.Normal,
            ReplyLengthPolicy.Decide(ReplyMode.Jev, ChatType.Private, mentioned: true, "在吗"));

    [Theory]
    [InlineData("详细说说这个方案")]
    [InlineData("展开讲讲")]
    [InlineData("能具体说说吗")]
    public void Detail_keyword_unlocks_long_budget(string text) =>
        Assert.Equal(
            ReplyLengthScene.Long,
            ReplyLengthPolicy.Decide(ReplyMode.Jev, ChatType.Group, mentioned: false, text));

    [Fact]
    public void Budgets_are_ordered_and_configurable()
    {
        Assert.True(Options.Short.MaxChars < Options.Normal.MaxChars);
        Assert.True(Options.Normal.MaxChars < Options.Long.MaxChars);

        var custom = Options with { Short = new ReplyLengthBudget(50, 1) };
        Assert.Equal(50, ReplyLengthPolicy.BudgetFor(ReplyLengthScene.Short, custom).MaxChars);
    }

    [Fact]
    public void Trim_cuts_at_last_sentence_end_inside_budget()
    {
        var text = "第一句话在这里。第二句话在这里。第三句话超出去了一点点。";

        var trimmed = ReplyLengthPolicy.TrimToBudget(text, 16);

        Assert.True(trimmed.Length <= 16);
        Assert.EndsWith("。", trimmed);
    }

    [Fact]
    public void Short_text_is_left_untouched()
    {
        Assert.Equal("好的喵", ReplyLengthPolicy.TrimToBudget("好的喵", 100));
    }
}
