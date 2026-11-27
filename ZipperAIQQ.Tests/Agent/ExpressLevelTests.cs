using ZipperAIQQ.Agent.Express;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>B6 T6.1：档位解析与优先级矩阵（表达档位设计 §2.1 / §11）。</summary>
public sealed class ExpressLevelTests
{
    private static ExpressSettings Settings(
        ExpressLevel level = ExpressLevel.Daily,
        ExpressLevel groupCap = ExpressLevel.Explicit,
        params (string SessionId, ExpressLevel Level)[] overrides)
    {
        var map = new Dictionary<string, ExpressLevel>(StringComparer.Ordinal);
        foreach (var (sessionId, over) in overrides)
        {
            map[sessionId] = over;
        }

        return new ExpressSettings { Level = level, GroupCap = groupCap, SessionOverrides = map };
    }

    [Theory]
    [InlineData("daily", ExpressLevel.Daily)]
    [InlineData("CLOSE", ExpressLevel.Close)]
    [InlineData(" explicit ", ExpressLevel.Explicit)]
    [InlineData("日常", ExpressLevel.Daily)]
    [InlineData("亲近", ExpressLevel.Close)]
    [InlineData("露骨", ExpressLevel.Explicit)]
    public void TryParse_KnownValues_Parses(string raw, ExpressLevel expected)
    {
        Assert.True(ExpressLevelText.TryParse(raw, out var level));
        Assert.Equal(expected, level);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("explicity")]
    [InlineData("high")]
    public void TryParse_UnknownOrEmpty_ReturnsFalse(string? raw)
    {
        // 认不出必须报错（设计 §7.1）：静默回落会让用户以为设置生效了
        Assert.False(ExpressLevelText.TryParse(raw, out var level));
        Assert.Equal(ExpressLevel.Daily, level);
    }

    [Fact]
    public void ToKey_RoundTrips_WithTryParse()
    {
        foreach (var level in Enum.GetValues<ExpressLevel>())
        {
            Assert.True(ExpressLevelText.TryParse(ExpressLevelText.ToKey(level), out var parsed));
            Assert.Equal(level, parsed);
            Assert.False(string.IsNullOrWhiteSpace(ExpressLevelText.ToLabel(level)));
        }
    }

    [Fact]
    public void Resolve_Private_UsesGlobal()
    {
        Assert.Equal(
            ExpressLevel.Close,
            ExpressLevelPolicy.Resolve(Settings(level: ExpressLevel.Close), "qq:p10001", isGroup: false));
    }

    [Fact]
    public void Resolve_SessionOverride_WinsOverGlobal()
    {
        var settings = Settings(
            level: ExpressLevel.Daily,
            groupCap: ExpressLevel.Explicit,
            ("douyin:p1000000000", ExpressLevel.Explicit));

        Assert.Equal(
            ExpressLevel.Explicit,
            ExpressLevelPolicy.Resolve(settings, "douyin:p1000000000", isGroup: false));

        // 别的会话不受影响
        Assert.Equal(
            ExpressLevel.Daily,
            ExpressLevelPolicy.Resolve(settings, "douyin:p2000000000", isGroup: false));
    }

    [Fact]
    public void Resolve_GroupCap_OnlyTightens()
    {
        // 全局 explicit + 群里 daily → 群里就是日常档
        Assert.Equal(
            ExpressLevel.Daily,
            ExpressLevelPolicy.Resolve(
                Settings(level: ExpressLevel.Explicit, groupCap: ExpressLevel.Daily),
                "qq:g10001",
                isGroup: true));

        // 全局 daily + 群里 explicit → 上限不能放宽全局
        Assert.Equal(
            ExpressLevel.Daily,
            ExpressLevelPolicy.Resolve(
                Settings(level: ExpressLevel.Daily, groupCap: ExpressLevel.Explicit),
                "qq:g10001",
                isGroup: true));
    }

    [Fact]
    public void Resolve_GroupCap_TightensSessionOverrideToo()
    {
        // 「groupCap=daily 一句话收紧群里尺度」必须真的收紧，否则被单独设过的群能绕过它
        var settings = Settings(
            level: ExpressLevel.Daily,
            groupCap: ExpressLevel.Close,
            ("qq:g10001", ExpressLevel.Explicit));

        Assert.Equal(
            ExpressLevel.Close,
            ExpressLevelPolicy.Resolve(settings, "qq:g10001", isGroup: true));

        // 同一个覆盖在私聊里照旧生效（上限只管群聊）
        Assert.Equal(
            ExpressLevel.Explicit,
            ExpressLevelPolicy.Resolve(settings, "qq:g10001", isGroup: false));
    }

    [Fact]
    public void Resolve_DefaultSettings_IsDailyEverywhere()
    {
        var settings = new ExpressSettings();

        Assert.Equal(ExpressLevel.Daily, ExpressLevelPolicy.Resolve(settings, "qq:p10001", isGroup: false));
        Assert.Equal(ExpressLevel.Daily, ExpressLevelPolicy.Resolve(settings, "qq:g10001", isGroup: true));
        Assert.Equal(ExpressLevel.Explicit, settings.GroupCap);   // 默认「都开放」= 不额外限制
        Assert.True(settings.HygieneEnabled);
        Assert.True(settings.HygieneRegenerate);
    }

    [Fact]
    public void Resolve_EmptySessionId_SkipsOverride()
    {
        var settings = Settings(level: ExpressLevel.Close, overrides: [("", ExpressLevel.Explicit)]);

        Assert.Equal(ExpressLevel.Close, ExpressLevelPolicy.Resolve(settings, null, isGroup: false));
        Assert.Equal(ExpressLevel.Close, ExpressLevelPolicy.Resolve(settings, "  ", isGroup: false));
    }
}
