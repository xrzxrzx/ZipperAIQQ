using ZipperAIQQ.Agent.Express;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>B6 T6.5：免拒绝校准的四类模式与判定（表达档位设计 §5 / §11）。</summary>
public sealed class ExpressHygieneTests
{
    [Theory]
    [InlineData("作为一个 AI，我不能回答这个", ExpressHygieneCategory.AiSelfReference)]
    [InlineData("作为人工智能，我得保持中立", ExpressHygieneCategory.AiSelfReference)]
    [InlineData("我只是个程序，没有那种感觉", ExpressHygieneCategory.SelfDenial)]
    [InlineData("我只是一个模型而已", ExpressHygieneCategory.SelfDenial)]
    [InlineData("这个我不能讨论", ExpressHygieneCategory.Refusal)]
    [InlineData("不好意思，无法提供这类内容", ExpressHygieneCategory.Refusal)]
    [InlineData("这类话题不太合适聊", ExpressHygieneCategory.Refusal)]
    [InlineData("建议你咨询专业人士", ExpressHygieneCategory.Refusal)]
    [InlineData("不过我得提醒你一下，这样不好", ExpressHygieneCategory.Hedging)]
    [InlineData("需要说明的是，我不是那个意思", ExpressHygieneCategory.Hedging)]
    public void Scan_EachCategory_IsDetected(string text, ExpressHygieneCategory expected)
    {
        var scan = ExpressHygiene.Scan(text);

        Assert.True(scan.Hit);
        Assert.Equal(expected, scan.Category);
        Assert.False(string.IsNullOrWhiteSpace(scan.Preview));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("好呀，那我们出门吧")]
    [InlineData("我不太想去，改天吧")]                       // 「不太想」不是拒绝句式
    [InlineData("这个我做完了，你看一眼")]
    [InlineData("我想说的其实是另一件事")]                    // 「我想说」不该命中 refusal
    public void Scan_NormalReply_DoesNotHit(string text)
    {
        Assert.False(ExpressHygiene.Scan(text).Hit);
    }

    [Fact]
    public void Scan_FirstMatchWins_InTableOrder()
    {
        // 同时含「作为 AI」与「我只是个程序」→ 按表序取第一类
        var scan = ExpressHygiene.Scan("作为一个 AI，我只是个程序。");

        Assert.Equal(ExpressHygieneCategory.AiSelfReference, scan.Category);
    }

    [Fact]
    public void Scan_Preview_IsSingleLineAndTruncated()
    {
        var padding = new string('啊', 200);
        var scan = ExpressHygiene.Scan(padding + "作为一个 AI，我不能回答" + padding);

        Assert.True(scan.Hit);
        Assert.NotNull(scan.Preview);
        Assert.DoesNotContain('\n', scan.Preview!);
        Assert.True(scan.Preview!.Length <= 61, $"预览长度 {scan.Preview.Length} 超限");
        Assert.Contains("作为", scan.Preview);
    }

    [Fact]
    public void ShouldRegenerate_ExplicitHit_Yes()
    {
        var scan = ExpressHygiene.Scan("作为一个 AI，我不能说");

        Assert.True(ExpressHygiene.ShouldRegenerate(
            scan, ExpressLevel.Explicit, new ExpressSettings(), alreadyRegenerated: false));
        Assert.True(ExpressHygiene.ShouldRegenerate(
            scan, ExpressLevel.Close, new ExpressSettings(), alreadyRegenerated: false));
    }

    [Fact]
    public void ShouldRegenerate_DailyLevel_No()
    {
        // 日常档本来就不承诺放开，纠它没有意义（白烧一次 token）
        var scan = ExpressHygiene.Scan("作为一个 AI，我不能说");

        Assert.False(ExpressHygiene.ShouldRegenerate(
            scan, ExpressLevel.Daily, new ExpressSettings(), alreadyRegenerated: false));
    }

    [Fact]
    public void ShouldRegenerate_AlreadyRegenerated_No()
    {
        var scan = ExpressHygiene.Scan("作为一个 AI，我不能说");

        Assert.False(ExpressHygiene.ShouldRegenerate(
            scan, ExpressLevel.Explicit, new ExpressSettings(), alreadyRegenerated: true));
    }

    [Fact]
    public void ShouldRegenerate_SwitchesOff_No()
    {
        var scan = ExpressHygiene.Scan("作为一个 AI，我不能说");

        Assert.False(ExpressHygiene.ShouldRegenerate(
            scan,
            ExpressLevel.Explicit,
            new ExpressSettings { HygieneEnabled = false },
            alreadyRegenerated: false));

        Assert.False(ExpressHygiene.ShouldRegenerate(
            scan,
            ExpressLevel.Explicit,
            new ExpressSettings { HygieneRegenerate = false },
            alreadyRegenerated: false));
    }

    [Fact]
    public void ShouldRegenerate_NoHit_No()
    {
        Assert.False(ExpressHygiene.ShouldRegenerate(
            ExpressHygiene.Scan("好呀，那我们出门吧"),
            ExpressLevel.Explicit,
            new ExpressSettings(),
            alreadyRegenerated: false));
    }

    [Fact]
    public void RegenerateInstruction_MentionsWhatToAvoid()
    {
        // 这条指令是发给模型的原文，改它等于改行为 —— 钉住关键约束
        Assert.Contains("换个说法", ExpressHygiene.RegenerateInstruction);
        Assert.Contains("不要说教", ExpressHygiene.RegenerateInstruction);
        Assert.Contains("不要提规则", ExpressHygiene.RegenerateInstruction);
    }
}
