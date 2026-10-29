using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Filtering;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Access;
using ZipperAIQQ.Server.Contacts;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// 主人放行范围：黑名单优先（D75）。
/// </summary>
/// <remarks>
/// 实际 bug：主人所在的群被拉黑后，仍因为「主人放行」收到回复。
/// 解析器现在不再把被拉黑的群写进 <c>AlwaysAllow</c>，过滤器本身也把黑名单排在最前。
/// </remarks>
public sealed class OwnerScopeResolverTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task Blacklisted_groups_are_not_added_to_owner_scope()
    {
        var settings = new MemorySettings();
        await settings.SetAsync(AccessKeys.Owner("qq"), "10001");

        var filter = new ChatFilter(new ChatFilterOptions { Blacklist = ["qq:g10"] });
        var registry = new ContactDirectoryRegistry(new Dictionary<string, IContactDirectory>
        {
            ["qq"] = new FakeDirectory(),
        });

        var resolver = new OwnerScopeResolver(settings, registry, filter, Logger);
        await resolver.RefreshAsync();

        Assert.Contains("qq:p10001", filter.AlwaysAllow("qq"));
        Assert.DoesNotContain("qq:g10", filter.AlwaysAllow("qq"));
        Assert.Contains("qq:g11", filter.AlwaysAllow("qq"));

        Assert.False(filter.IsAllowed(new ChatRef { Platform = "qq", Type = ChatType.Group, Id = "10" }));
        Assert.True(filter.IsAllowed(new ChatRef { Platform = "qq", Type = ChatType.Group, Id = "11" }));
    }

    private sealed class MemorySettings : IRuntimeSettingsStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_values.TryGetValue(key, out var value) ? value : null);

        public Task SetAsync(string key, string value, CancellationToken ct = default)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }
    }

    /// <summary>两个群：g10（主人在，但被拉黑）与 g11（主人在，正常）。</summary>
    private sealed class FakeDirectory : IContactDirectory
    {
        public Task<IReadOnlyList<ContactInfo>> GetFriendsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ContactInfo>>([]);

        public Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GroupInfo>>(
            [
                new GroupInfo("10", "拉黑群", 2, 200),
                new GroupInfo("11", "正常群", 2, 200),
            ]);

        public Task SetSignatureAsync(string text, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SendPokeAsync(string userId, string? groupId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<GroupInfo?> GetGroupAsync(string groupId, CancellationToken ct = default) =>
            Task.FromResult<GroupInfo?>(null);

        public Task<IReadOnlyList<GroupMemberInfo>> GetGroupMembersAsync(
            string groupId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GroupMemberInfo>>(
                [new GroupMemberInfo("10001", "主人", null, "member", null)]);

        public Task<System.Text.Json.Nodes.JsonNode?> InvokeApiAsync(
            string action,
            System.Text.Json.Nodes.JsonObject? parameters,
            CancellationToken ct = default) =>
            Task.FromResult<System.Text.Json.Nodes.JsonNode?>(null);
    }
}
