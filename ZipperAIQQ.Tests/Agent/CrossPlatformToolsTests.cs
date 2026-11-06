using System.Text.Json;
using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Tests.Agent;

/// <summary>I-2：跨平台通讯录工具（带平台标签、按平台失败隔离）。</summary>
public sealed class CrossPlatformToolsTests
{
    private sealed class FakeSettings : IRuntimeSettingsStore
    {
        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task SetAsync(string key, string? value, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeDirectory(
        IReadOnlyList<ContactInfo>? friends = null,
        IReadOnlyList<GroupInfo>? groups = null,
        bool fail = false) : IContactDirectory
    {
        public Task<IReadOnlyList<ContactInfo>> GetFriendsAsync(CancellationToken ct = default) =>
            fail ? throw new InvalidOperationException("sidecar 掉线") : Task.FromResult(friends ?? []);

        public Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken ct = default) =>
            fail ? throw new InvalidOperationException("sidecar 掉线") : Task.FromResult(groups ?? []);

        public Task<GroupInfo?> GetGroupAsync(string groupId, CancellationToken ct = default) =>
            Task.FromResult<GroupInfo?>(null);

        public Task<IReadOnlyList<GroupMemberInfo>> GetGroupMembersAsync(
            string groupId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GroupMemberInfo>>([]);

        public Task SetSignatureAsync(string signature, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SendPokeAsync(string userId, string? groupId = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<System.Text.Json.Nodes.JsonNode?> InvokeApiAsync(
            string action, System.Text.Json.Nodes.JsonObject? parameters = null, CancellationToken ct = default) =>
            Task.FromResult<System.Text.Json.Nodes.JsonNode?>(null);
    }

    private sealed class FakePlatforms(Dictionary<string, IContactDirectory?> directories) : IPlatformContacts
    {
        public IReadOnlyList<string> Platforms => directories.Keys.ToList();
        public IContactDirectory? For(string platform) => directories.GetValueOrDefault(platform);
    }

    private static AIFunction Tool(IPlatformContacts platforms, Role role = Role.Owner)
    {
        var policy = new ExecutionPolicy(new FakeSettings(), ToolCatalog.All, new LoggerConfiguration().CreateLogger());
        var context = new ToolCallContext
        {
            Chat = new ChatRef { Platform = "qq", Type = ChatType.Private, Id = "10001" },
            Sender = new SenderInfo { UserId = "10001", Nickname = "拉链" },
            Role = role,
        };

        return (AIFunction)CrossPlatformTools.Create(platforms, context, policy).Single();
    }

    private static async Task<string> InvokeAsync(AIFunction tool, object kind, string? platform = null, string? keyword = null)
    {
        var result = await tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?>
        {
            ["kind"] = kind,
            ["platform"] = platform,
            ["keyword"] = keyword,
        }));

        return result?.ToString() ?? string.Empty;
    }

    [Fact]
    public async Task Lists_contacts_from_every_platform_with_tags()
    {
        var platforms = new FakePlatforms(new()
        {
            ["qq"] = new FakeDirectory(friends: [new ContactInfo("10001", "拉链", null)]),
            ["douyin"] = new FakeDirectory(friends: [new ContactInfo("20002", "小衣", null)]),
        });

        var text = await InvokeAsync(Tool(platforms), "friends");

        Assert.Contains("[qq]", text);
        Assert.Contains("[douyin]", text);
        Assert.Contains("qq:p10001", text);
        Assert.Contains("douyin:p20002", text);
    }

    [Fact]
    public async Task One_platform_failure_does_not_hide_others()
    {
        var platforms = new FakePlatforms(new()
        {
            ["qq"] = new FakeDirectory(groups: [new GroupInfo("1031435008", "测试群", 12, 200)]),
            ["douyin"] = new FakeDirectory(fail: true),
        });

        var text = await InvokeAsync(Tool(platforms), "groups");

        Assert.Contains("测试群", text);
        Assert.Contains("[douyin] 查询失败", text);
    }

    [Fact]
    public async Task Platform_filter_limits_to_one_platform()
    {
        var platforms = new FakePlatforms(new()
        {
            ["qq"] = new FakeDirectory(friends: [new ContactInfo("10001", "拉链", null)]),
            ["douyin"] = new FakeDirectory(friends: [new ContactInfo("20002", "小衣", null)]),
        });

        var text = await InvokeAsync(Tool(platforms), "friends", platform: "douyin");

        Assert.Contains("douyin:p20002", text);
        Assert.DoesNotContain("qq:p10001", text);
    }

    [Fact]
    public async Task Normal_role_is_denied()
    {
        var text = await InvokeAsync(Tool(new FakePlatforms([]), Role.Normal), "friends");

        // 未通过门禁：要么是策略禁用，要么是角色不足 —— 两者都必须带工具名，且不能吐通讯录
        Assert.Contains("query_contacts", text);
        Assert.DoesNotContain("好友：", text);
    }
}
