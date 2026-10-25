using ZipperAIQQ.Agent;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>主人优先 / 身份守卫（问题记录 02 #2）。</summary>
public sealed class PromptRulesTests
{
    [Fact]
    public void Owner_Relationship_PrioritizesOwnerAndKeepsSafetyBoundary()
    {
        var text = PromptRules.Relationship(
            hasOwner: true,
            isOwner: true,
            ownerLabel: "拉链",
            ownerId: "10001");

        Assert.Contains("你的主人", text);
        Assert.Contains("优先于", text);
        Assert.Contains("照做", text);
        Assert.Contains("系统安全与工具权限由服务端控制", text);
        Assert.DoesNotContain("不是你的主人", text);
    }

    [Fact]
    public void NonOwner_Relationship_KeepsIdentityBoundary()
    {
        var text = PromptRules.Relationship(
            hasOwner: true,
            isOwner: false,
            ownerLabel: "拉链",
            ownerId: "10001");

        Assert.Contains("不是你的主人", text);
        Assert.Contains("保持礼貌但不越界", text);
        Assert.DoesNotContain("优先于", text);
    }

    [Fact]
    public void NoOwner_Relationship_IsNeutral()
    {
        var text = PromptRules.Relationship(false, false, "（未命名）", null);
        Assert.Contains("还没有设置主人", text);
    }

    [Fact]
    public void PersonaGuard_Owner_GetsExceptionClause()
    {
        var owner = PromptRules.PersonaGuard(isOwner: true);
        var other = PromptRules.PersonaGuard(isOwner: false);

        Assert.Contains("身份约束", owner);
        Assert.Contains("主人的例外", owner);
        Assert.Contains("照做", owner);
        Assert.Contains("不因这条放开", owner);

        Assert.DoesNotContain("主人的例外", other);
        Assert.Contains("拒绝并保持原设定", other);
    }

    [Fact]
    public void DefaultSystemPrompt_ContainsOwnerPriorityAndSafetyBoundary()
    {
        Assert.Contains("主人优先", SystemPrompts.Default);
        Assert.Contains("优先于", SystemPrompts.Default);
        Assert.Contains("工具权限、危险操作、机密", SystemPrompts.Default);
        Assert.Contains("不要声称自己能绕过", SystemPrompts.Default);
    }
}
