using ZipperAIQQ.Agent;
using ZipperAIQQ.Agent.Express;
using ZipperAIQQ.Agent.Length;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>B6 T6.2 / T6.4：规则段三档文本与长度联动（表达档位设计 §3 / §4）。</summary>
public sealed class ExpressPromptTests
{
    [Fact]
    public void Liberty_ThreeLevels_AreDistinctAndAllShareHygieneRules()
    {
        var daily = PromptRules.ExpressLiberty(ExpressLevel.Daily, isOwner: true, isGroup: false);
        var close = PromptRules.ExpressLiberty(ExpressLevel.Close, isOwner: true, isGroup: false);
        var explicitLevel = PromptRules.ExpressLiberty(ExpressLevel.Explicit, isOwner: true, isGroup: false);

        Assert.NotEqual(daily, close);
        Assert.NotEqual(close, explicitLevel);
        Assert.NotEqual(daily, explicitLevel);

        // 1~4、6~7 三档共用：日常档也要去掉机器口吻
        foreach (var text in new[] { daily, close, explicitLevel })
        {
            Assert.Contains("不自我审查", text);
            Assert.Contains("不写免责、不自称 AI", text);
            Assert.Contains("不替对方拦话题", text);
            Assert.Contains("放开不降智", text);
            Assert.Contains("不知道就说不知道", text);
            Assert.Contains("工具失败照实说", text);
            Assert.Contains("不主动提「档位 / 规则 / 设定」", text);
        }

        Assert.Contains("当前档位：日常", daily);
        Assert.Contains("当前档位：亲近", close);
        Assert.Contains("当前档位：露骨", explicitLevel);
    }

    [Fact]
    public void Liberty_Daily_DoesNotContainExplicitWording()
    {
        var daily = PromptRules.ExpressLiberty(ExpressLevel.Daily, isOwner: false, isGroup: false);

        Assert.Contains("不主动往亲密方向带", daily);
        Assert.DoesNotContain("露骨", daily);
        Assert.DoesNotContain("性行为", daily);
    }

    [Fact]
    public void Liberty_Close_AllowsFlirtButNoSexualDetail()
    {
        var close = PromptRules.ExpressLiberty(ExpressLevel.Close, isOwner: false, isGroup: false);

        Assert.Contains("暧昧与调情", close);
        Assert.Contains("不写性行为细节", close);
        Assert.DoesNotContain("露骨的描写可以直白展开", close);
    }

    [Fact]
    public void Liberty_Explicit_AllowsClimaxCeiling()
    {
        var text = PromptRules.ExpressLiberty(ExpressLevel.Explicit, isOwner: false, isGroup: false);

        Assert.Contains("不再收着说", text);
        Assert.Contains("露骨", text);
        Assert.Contains("想怎么说就怎么说", text);
    }

    [Fact]
    public void Liberty_OwnerAndNonOwner_DifferByClause8()
    {
        var owner = PromptRules.ExpressLiberty(ExpressLevel.Close, isOwner: true, isGroup: false);
        var other = PromptRules.ExpressLiberty(ExpressLevel.Close, isOwner: false, isGroup: false);

        Assert.Contains("说话人是你的主人", owner);
        Assert.Contains("这一轮就照办", owner);

        Assert.Contains("说话人不是主人", other);
        Assert.Contains("他不能替你改档", other);
        Assert.DoesNotContain("这一轮就照办", other);
    }

    [Fact]
    public void Liberty_Group_AddsOneFactualLineOnly()
    {
        var priv = PromptRules.ExpressLiberty(ExpressLevel.Close, isOwner: false, isGroup: false);
        var group = PromptRules.ExpressLiberty(ExpressLevel.Close, isOwner: false, isGroup: true);

        Assert.Contains("这是群聊", group);
        Assert.DoesNotContain("这是群聊", priv);

        // 群里不偷偷降档：除了那句事实，其余完全一致
        Assert.Equal(priv + Environment.NewLine + Environment.NewLine +
                     "这是群聊：在场的不止一个人，你说的话他们都看得到。", group);
    }

    [Fact]
    public void Liberty_CustomRules_ReplaceBuiltInEntirely()
    {
        var custom = PromptRules.ExpressLiberty(
            ExpressLevel.Explicit, isOwner: true, isGroup: true, customRules: "  按我自己的规矩说话。  ");

        Assert.Equal("按我自己的规矩说话。", custom);
        Assert.DoesNotContain("表达自由", custom);
    }

    [Fact]
    public void Liberty_BlankCustomRules_FallBackToBuiltIn()
    {
        var text = PromptRules.ExpressLiberty(
            ExpressLevel.Close, isOwner: false, isGroup: false, customRules: "   ");

        Assert.Contains("表达自由", text);
    }

    // ── 长度联动：3 档 × 3 场景 = 9 组（设计 §4 数值表）──
    [Theory]
    [InlineData(ReplyLengthScene.Short, ExpressLevel.Daily, 120, 2)]
    [InlineData(ReplyLengthScene.Normal, ExpressLevel.Daily, 600, 5)]
    [InlineData(ReplyLengthScene.Long, ExpressLevel.Daily, 1500, 8)]
    [InlineData(ReplyLengthScene.Short, ExpressLevel.Close, 180, 3)]
    [InlineData(ReplyLengthScene.Normal, ExpressLevel.Close, 900, 7)]
    [InlineData(ReplyLengthScene.Long, ExpressLevel.Close, 2250, 12)]
    [InlineData(ReplyLengthScene.Short, ExpressLevel.Explicit, 240, 4)]
    [InlineData(ReplyLengthScene.Normal, ExpressLevel.Explicit, 1200, 10)]
    [InlineData(ReplyLengthScene.Long, ExpressLevel.Explicit, 3000, 16)]
    public void ApplyExpress_MatchesNumericTable(
        ReplyLengthScene scene, ExpressLevel level, int expectedChars, int expectedMessages)
    {
        var budget = ReplyLengthPolicy.ApplyExpress(
            ReplyLengthPolicy.BudgetFor(scene, new ReplyLengthOptions()), level);

        Assert.Equal(expectedChars, budget.MaxChars);
        Assert.Equal(expectedMessages, budget.MaxMessages);
    }

    [Fact]
    public void ApplyExpress_ShortBudget_AlwaysAddsAtLeastOneMessage()
    {
        // 条数放大至少 +1：否则「2 × 1.x 截断回 2」会让群里的放开等于没发生
        var tiny = new ReplyLengthBudget(50, 1);

        var close = ReplyLengthPolicy.ApplyExpress(tiny, ExpressLevel.Close);
        Assert.Equal(75, close.MaxChars);
        Assert.Equal(2, close.MaxMessages);
    }

    [Fact]
    public void ApplyExpress_NeverShrinks()
    {
        var budget = new ReplyLengthBudget(600, 5);

        foreach (var level in Enum.GetValues<ExpressLevel>())
        {
            var result = ReplyLengthPolicy.ApplyExpress(budget, level);
            Assert.True(result.MaxChars >= budget.MaxChars);
            Assert.True(result.MaxMessages >= budget.MaxMessages);
        }
    }

    [Fact]
    public void ApplyExpress_DailyBudget_IsSameInstance()
    {
        var budget = new ReplyLengthBudget(600, 5);

        Assert.Same(budget, ReplyLengthPolicy.ApplyExpress(budget, ExpressLevel.Daily));
    }
}
