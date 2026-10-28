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

    /// <summary>用同一份 FakeContacts 组一个注册表（默认平台 qq，可加别的平台）。</summary>
    private static SessionNameResolver NewResolver(
        FakeContacts qq,
        FakeContacts? douyin = null)
    {
        var map = new Dictionary<string, IContactDirectory>(StringComparer.OrdinalIgnoreCase)
        {
            ["qq"] = qq,
        };

        if (douyin is not null)
        {
            map["douyin"] = douyin;
        }

        return new SessionNameResolver(new ContactDirectoryRegistry(map), Logger);
    }

    private sealed class FakeContacts : IContactDirectory
    {
        public bool ThrowFriends { get; set; }
        public bool ThrowGroups { get; set; }

        /// <summary>可替换的候选（null = 用下面那份默认值）；测试里给抖音造一份。</summary>
        public IReadOnlyList<ContactInfo>? Friends { get; set; }
        public IReadOnlyList<GroupInfo>? Groups { get; set; }

        public Task<IReadOnlyList<ContactInfo>> GetFriendsAsync(CancellationToken ct = default)
            => ThrowFriends
                ? throw new InvalidOperationException("napcat down")
                : Task.FromResult(Friends ?? (IReadOnlyList<ContactInfo>)
                [
                    new ContactInfo("10001", "拉链", "主人"),
                    new ContactInfo("10002", "路人", null),
                ]);

        public Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken ct = default)
            => ThrowGroups
                ? throw new InvalidOperationException("napcat down")
                : Task.FromResult(Groups ?? (IReadOnlyList<GroupInfo>)
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
        var resolver = NewResolver(new FakeContacts());

        var names = await resolver.ResolveAsync(["qq:p10001", "qq:g20001"]);

        Assert.Equal("QQ · 拉链（主人）", names["qq:p10001"]);
        Assert.Equal("QQ群 · 摸鱼群", names["qq:g20001"]);
    }

    [Fact]
    public async Task Douyin_sessions_resolve_with_platform_label()
    {
        // 2026-10-28：以前只注入 QQ 目录，抖音会话永远回落原始 id（douyin:p888…）；
        // 现在按平台取目录，标签统一走 PlatformLabels（QQ / 抖音）。
        var douyin = new FakeContacts
        {
            Friends = [new ContactInfo("88892527967", "抖音好友", null)],
            Groups = [new GroupInfo("9001", "抖音群", 3, 500)],
        };

        var resolver = NewResolver(new FakeContacts(), douyin);

        var names = await resolver.ResolveAsync(["douyin:p88892527967", "douyin:g9001"]);

        Assert.Equal("抖音 · 抖音好友", names["douyin:p88892527967"]);
        Assert.Equal("抖音群 · 抖音群", names["douyin:g9001"]);
    }

    [Fact]
    public async Task Unknown_or_malformed_falls_back_to_id()
    {
        var resolver = NewResolver(new FakeContacts());

        var names = await resolver.ResolveAsync(["qq:p99999", "weird-id"]);

        Assert.Equal("qq:p99999", names["qq:p99999"]);
        Assert.Equal("weird-id", names["weird-id"]);
    }

    [Fact]
    public async Task Directory_failure_falls_back_to_id_instead_of_throwing()
    {
        var contacts = new FakeContacts { ThrowFriends = true, ThrowGroups = true };
        var resolver = NewResolver(contacts);

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
