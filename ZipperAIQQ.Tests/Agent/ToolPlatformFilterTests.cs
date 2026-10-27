using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Agent.Tools;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>
/// 工具按平台过滤（决策五 / S3-D 的 D1）。
/// </summary>
/// <remarks>
/// 只测**目录 + 平台转换**：<c>ChatAgent.BuildTools</c> 是私有方法且依赖一堆协作者，
/// 为它搭一整套假对象不划算，风险也主要集中在这张表而不是那两行过滤代码。
/// </remarks>
public sealed class ToolPlatformFilterTests
{
    [Fact]
    public void ToolCatalog_PlatformsOf_PokeIsQqOnly()
    {
        Assert.Equal(ToolPlatform.Qq, ToolCatalog.PlatformsOf(ProfileTools.PokeToolName));
        Assert.Equal(ToolPlatform.All, ToolCatalog.PlatformsOf(SendMessageTool.ToolName));
    }

    [Fact]
    public void ToolCatalog_PlatformsOf_UnknownToolDefaultsToAll()
    {
        // 新工具忘了登记时也要"处处可用"：默默禁掉比多给一个工具难查得多
        Assert.Equal(ToolPlatform.All, ToolCatalog.PlatformsOf("brand_new_tool"));
    }

    [Fact]
    public void ToolCatalog_IsAvailableOn_QqTool_HiddenOnDouyin()
    {
        Assert.False(ToolCatalog.IsAvailableOn(ProfileTools.PokeToolName, ToolPlatforms.DouyinId));
        Assert.True(ToolCatalog.IsAvailableOn(ProfileTools.PokeToolName, ToolPlatforms.QqId));
    }

    [Fact]
    public void ToolCatalog_IsAvailableOn_UnknownPlatform_HidesPlatformTools()
    {
        // 未知平台（将来的 telegram…）：平台专用工具一律不给，通用工具照给
        Assert.False(ToolCatalog.IsAvailableOn(ContactTools.ToolName, "telegram"));
        Assert.False(ToolCatalog.IsAvailableOn(ProfileTools.SetSignatureToolName, "telegram"));
        Assert.True(ToolCatalog.IsAvailableOn(SendMessageTool.ToolName, "telegram"));
    }

    [Theory]
    [InlineData(ToolPlatforms.QqId, ToolPlatform.Qq)]
    [InlineData("Douyin", ToolPlatform.Douyin)]
    [InlineData(" DOUYIN ", ToolPlatform.Douyin)]
    [InlineData(" ", ToolPlatform.None)]
    [InlineData("", ToolPlatform.None)]
    [InlineData(null, ToolPlatform.None)]
    public void ToolPlatforms_Parse_MapsPlatformId(string? platform, ToolPlatform expected)
    {
        Assert.Equal(expected, ToolPlatforms.Parse(platform));
    }

    /// <summary>
    /// 钉住这个坑：<c>All</c> 是「**不限定平台**」，不是"这两个平台的位组合"。
    /// </summary>
    /// <remarks>
    /// 曾经 <c>IsAvailable</c> 只有一句 <c>(declared &amp; Parse(platform)) != 0</c>，
    /// 未登记平台解析成 <c>None</c>（位 0）→ 通用工具跟着全灭。真实场景：
    /// <c>MailPipeline</c> 用 <c>platform = "mail"</c> 造 ChatRef，
    /// 那种会话会连 <c>send_message</c> 都拿不到，AI 直接哑巴。
    /// </remarks>
    [Fact]
    public void ToolPlatforms_IsAvailable_AllStaysAvailableOnUnknownPlatform()
    {
        // 未登记平台：通用工具仍然可用
        Assert.True(ToolPlatforms.IsAvailable(ToolPlatform.All, "telegram"));

        // mail 是项目里真实存在的第三个平台字符串（MailPipeline 的占位 ChatRef）
        Assert.True(ToolPlatforms.IsAvailable(ToolPlatform.All, "mail"));

        // None 是显式「哪儿都不用」：已知平台上也不给
        Assert.False(ToolPlatforms.IsAvailable(ToolPlatform.None, ToolPlatforms.QqId));
    }
}
