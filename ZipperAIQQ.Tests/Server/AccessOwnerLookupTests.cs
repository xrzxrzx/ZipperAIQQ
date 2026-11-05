using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Server.Contacts;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Tests.Server;

/// <summary>R-08：主人跨平台账号一次问全。</summary>
public sealed class AccessOwnerLookupTests
{
    private sealed class FakeSettings(Dictionary<string, string> values) : IRuntimeSettingsStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string? value, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakePlatforms(params string[] platforms) : IPlatformContacts
    {
        public IReadOnlyList<string> Platforms => platforms;
        public IContactDirectory? For(string platform) => null;
    }

    [Fact]
    public async Task Returns_owner_for_every_platform_that_has_one()
    {
        var settings = new FakeSettings(new Dictionary<string, string>
        {
            ["access.qq.owner"] = "10001",
            ["access.qq.ownerNickname"] = "拉链",
            ["access.douyin.owner"] = "20002",
            ["access.douyin.ownerNickname"] = "拉链（抖音）",
        });

        var lookup = new AccessOwnerLookup(settings, new FakePlatforms("qq", "douyin", "file"));

        var accounts = await lookup.GetAllAsync();

        Assert.Equal(2, accounts.Count);
        Assert.Contains(accounts, a => a.Platform == "qq" && a.OwnerId == "10001");
        Assert.Contains(accounts, a => a.Platform == "douyin" && a.OwnerName == "拉链（抖音）");
    }

    [Fact]
    public async Task Missing_owner_is_skipped_not_fabricated()
    {
        var lookup = new AccessOwnerLookup(
            new FakeSettings([]),
            new FakePlatforms("qq", "douyin"));

        Assert.Empty(await lookup.GetAllAsync());
    }
}
