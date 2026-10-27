using ZipperAIQQ.Channel.Filtering;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Tests.Channel;

/// <summary>
/// 接收过滤（黑白双名单 + 主人放行）。
/// </summary>
/// <remarks>
/// 规则来自问题记录 #20 / #22 的定稿：
/// 主人放行 → 黑名单拒绝 → 白名单要求 → 其余放行。
/// 同一条目不能同时出现在两份名单里（写入时就会被拒）。
/// </remarks>
public sealed class ChatFilterTests
{
    private static ChatRef Group(string id) =>
        new() { Platform = "qq", Type = ChatType.Group, Id = id };

    private static ChatRef Private(string id) =>
        new() { Platform = "qq", Type = ChatType.Private, Id = id };

    [Fact]
    public void Both_lists_empty_allows_everything()
    {
        var filter = new ChatFilter(new ChatFilterOptions());

        Assert.True(filter.IsAllowed(Group("1")));
        Assert.True(filter.IsAllowed(Private("2")));
    }

    [Fact]
    public void Non_empty_whitelist_denies_outsiders()
    {
        var filter = new ChatFilter(new ChatFilterOptions
        {
            Whitelist = ["qq:g1"],
        });

        Assert.True(filter.IsAllowed(Group("1")));
        Assert.False(filter.IsAllowed(Group("2")));
    }

    [Fact]
    public void Blacklist_denies_listed_but_allows_others()
    {
        var filter = new ChatFilter(new ChatFilterOptions
        {
            Blacklist = ["qq:g9"],
        });

        Assert.False(filter.IsAllowed(Group("9")));
        Assert.True(filter.IsAllowed(Group("8")));
    }

    [Fact]
    public void Whitelist_and_blacklist_can_coexist()
    {
        // 两份名单同时存在：白名单非空时负责「只收谁」，黑名单仍会排除自己的条目。
        // 注意：同一条目不允许两边都有（互斥），所以白名单内的聊天不会被黑名单额外排除。
        var filter = new ChatFilter(new ChatFilterOptions
        {
            Whitelist = ["qq:g1"],
            Blacklist = ["qq:g9"],
        });

        Assert.True(filter.IsAllowed(Group("1")));
        Assert.False(filter.IsAllowed(Group("9")));  // 黑名单
        Assert.False(filter.IsAllowed(Group("5")));  // 白名单外
        Assert.Equal(1, filter.WhitelistCount("qq"));
        Assert.Equal(1, filter.BlacklistCount("qq"));
    }

    [Fact]
    public void AlwaysAllow_bypasses_blacklist()
    {
        // 主人所在的群即使被写进黑名单，也仍然放行（#22：主人不该被自己的名单挡掉）
        var filter = new ChatFilter(new ChatFilterOptions
        {
            Blacklist = ["qq:g10", "qq:p10001"],
            AlwaysAllow = ["qq:g10", "qq:p10001"],
        });

        Assert.True(filter.IsAllowed(Group("10")));
        Assert.True(filter.IsAllowed(Private("10001")));
    }

    [Fact]
    public void AlwaysAllow_can_be_replaced_at_runtime()
    {
        var filter = new ChatFilter(new ChatFilterOptions { AlwaysAllow = ["qq:p1"] });
        Assert.Contains("qq:p1", filter.AlwaysAllow("qq"));

        filter.UpdateAlwaysAllow("qq", ["qq:g2"]);

        // 替换而不是追加：旧的主人私聊不再属于「无条件放行」
        Assert.DoesNotContain("qq:p1", filter.AlwaysAllow("qq"));
        Assert.Contains("qq:g2", filter.AlwaysAllow("qq"));
        Assert.True(filter.IsAllowed(Group("2")));
    }

    [Fact]
    public void Update_replaces_both_lists()
    {
        var filter = new ChatFilter(new ChatFilterOptions { Whitelist = ["qq:g1"] });
        Assert.False(filter.IsAllowed(Group("2")));

        filter.Update("qq", new ChatFilterOptions { Blacklist = ["qq:g2"] });
        Assert.True(filter.IsAllowed(Group("1")));
        Assert.False(filter.IsAllowed(Group("2")));
    }

    [Fact]
    public void Matching_is_case_insensitive()
    {
        var filter = new ChatFilter(new ChatFilterOptions { Blacklist = ["QQ:G42"] });

        Assert.False(filter.IsAllowed(new ChatRef
        {
            Platform = "qq",
            Type = ChatType.Group,
            Id = "42",
        }));
    }

    [Fact]
    public void Overlap_is_rejected_on_update_too()
    {
        var filter = new ChatFilter(new ChatFilterOptions());

        Assert.Throws<InvalidOperationException>(() => filter.Update("qq", new ChatFilterOptions
        {
            Whitelist = ["qq:g1"],
            Blacklist = ["qq:g1"],
        }));
    }

    [Fact]
    public void Counts_reflect_both_lists()
    {
        var filter = new ChatFilter(new ChatFilterOptions
        {
            Whitelist = ["qq:g1", "qq:g2"],
            Blacklist = ["qq:g9"],
            AlwaysAllow = ["qq:p1", "qq:g3"],
        });

        Assert.Equal(2, filter.WhitelistCount("qq"));
        Assert.Equal(1, filter.BlacklistCount("qq"));
        Assert.Equal(2, filter.AlwaysAllow("qq").Count);
    }

    // ─────────────────── S3-C：平台隔离（决策四） ───────────────────

    [Fact]
    public void Lists_are_isolated_per_platform()
    {
        // QQ 的白名单**不该**决定抖音收不收消息
        var filter = new ChatFilter(new ChatFilterOptions
        {
            Whitelist = ["qq:g1"],
        });

        Assert.False(filter.IsAllowed(new ChatRef { Platform = "qq", Type = ChatType.Group, Id = "2" }));
        Assert.True(filter.IsAllowed(new ChatRef { Platform = "douyin", Type = ChatType.Group, Id = "2" }));
    }

    [Fact]
    public void Update_affects_only_target_platform()
    {
        var filter = new ChatFilter(new ChatFilterOptions { Whitelist = ["qq:g1"] });

        filter.Update("douyin", new ChatFilterOptions { Blacklist = ["douyin:g7"] });

        // qq 名单原样保留
        Assert.True(filter.IsAllowed(new ChatRef { Platform = "qq", Type = ChatType.Group, Id = "1" }));
        Assert.False(filter.IsAllowed(new ChatRef { Platform = "qq", Type = ChatType.Group, Id = "2" }));

        // douyin 只有自己的黑名单
        Assert.False(filter.IsAllowed(new ChatRef { Platform = "douyin", Type = ChatType.Group, Id = "7" }));
        Assert.True(filter.IsAllowed(new ChatRef { Platform = "douyin", Type = ChatType.Group, Id = "8" }));
        Assert.Empty(filter.Whitelist("douyin"));
    }

    [Fact]
    public void AlwaysAllow_is_isolated_per_platform()
    {
        var filter = new ChatFilter(new ChatFilterOptions { Blacklist = ["qq:g10"] });

        filter.UpdateAlwaysAllow("qq", ["qq:g10"]);

        Assert.True(filter.IsAllowed(new ChatRef { Platform = "qq", Type = ChatType.Group, Id = "10" }));
        Assert.Empty(filter.AlwaysAllow("douyin"));
    }

    [Fact]
    public void Unconfigured_platform_allows_everything()
    {
        // 新平台刚接入时不该被"没配过"挡住
        var filter = new ChatFilter();

        Assert.True(filter.IsAllowed(new ChatRef { Platform = "telegram", Type = ChatType.Private, Id = "1" }));
        Assert.Equal(0, filter.WhitelistCount("telegram"));
    }
}
