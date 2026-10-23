using System.Text.Json.Nodes;
using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Server.Contacts;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// 会话显示名解析（#2）：好友昵称 / 群名 / 失败回落编号。
/// </summary>
public sealed class SessionNameResolverTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    private sealed class FakeContacts : IContactDirectory
    {
        public bool ThrowFriends { get; set; }
        public bool ThrowGroups { get; set; }

        public Task<IReadOnlyList<ContactInfo>> GetFriendsAsync(CancellationToken ct = default)
            => ThrowFriends
                ? throw new InvalidOperationException("napcat down")
                : Task.FromResult<IReadOnlyList<ContactInfo>>(
                [
                    new ContactInfo("10001", "拉链", "主人"),
                    new ContactInfo("10002", "路人", null),
                ]);

        public Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken ct = default)
            => ThrowGroups
                ? throw new InvalidOperationException("napcat down")
                : Task.FromResult<IReadOnlyList<GroupInfo>>(
                [
                    new GroupInfo("20001", "摸鱼群", 10, 200),
                ]);

        public Task SetSignatureAsync(string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task SendPokeAsync(string userId, string? groupId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<GroupInfo?> GetGroupAsync(string groupId, CancellationToken ct = default) => Task.FromResult<GroupInfo?>(null);
        public Task<IReadOnlyList<GroupMemberInfo>> GetGroupMembersAsync(string groupId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<GroupMemberInfo>>([]);
        public Task<JsonNode?> InvokeApiAsync(string action, JsonObject? parameters, CancellationToken ct = default)
            => Task.FromResult<JsonNode?>(null);
    }

    [Fact]
    public async Task Private_and_group_get_readable_names()
    {
        var resolver = new SessionNameResolver(new FakeContacts(), Logger);

        var names = await resolver.ResolveAsync(["qq:p10001", "qq:g20001"]);

        Assert.Equal("QQ · 拉链（主人）", names["qq:p10001"]);
        Assert.Equal("QQ群 · 摸鱼群", names["qq:g20001"]);
    }

    [Fact]
    public async Task Unknown_or_malformed_falls_back_to_id()
    {
        var resolver = new SessionNameResolver(new FakeContacts(), Logger);

        var names = await resolver.ResolveAsync(["qq:p99999", "weird-id"]);

        Assert.Equal("qq:p99999", names["qq:p99999"]);
        Assert.Equal("weird-id", names["weird-id"]);
    }

    [Fact]
    public async Task Directory_failure_falls_back_to_id_instead_of_throwing()
    {
        var contacts = new FakeContacts { ThrowFriends = true, ThrowGroups = true };
        var resolver = new SessionNameResolver(contacts, Logger);

        var names = await resolver.ResolveAsync(["qq:p10001", "qq:g20001"]);

        Assert.Equal("qq:p10001", names["qq:p10001"]);
        Assert.Equal("qq:g20001", names["qq:g20001"]);
    }

    [Fact]
    public void Parse_rejects_bad_formats()
    {
        Assert.Null(SessionIds.Parse(""));
        Assert.Null(SessionIds.Parse("qq"));
        Assert.Null(SessionIds.Parse("qq:x1"));
        Assert.Equal(("qq", true, "20001"), SessionIds.Parse("qq:g20001"));
    }
}
