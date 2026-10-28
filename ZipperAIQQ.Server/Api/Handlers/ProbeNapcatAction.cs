using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Apis;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// NapCat API 连通性诊断（开发/排障用）：逐个打白名单里的只读 action。
/// </summary>
/// <remarks>
/// 存在的理由：模型"不主动调工具"时，很难分清**是能力不通还是行为不对**。
/// 有了这个端点，一次请求就能看到每个 action 到底通不通、返回了什么。
/// </remarks>
public sealed class ProbeNapcatAction : IApiAction
{
    private readonly IContactDirectory _contacts;

    public ProbeNapcatAction(IContactDirectory contacts)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        _contacts = contacts;
    }

    public string Name => "probeNapcat";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var results = new List<object>();

        async Task ProbeAsync(string action, Func<Task<string>> run)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var summary = await run().ConfigureAwait(false);
                results.Add(new { action, ok = true, ms = (int)sw.ElapsedMilliseconds, summary, error = (string?)null });
            }
            catch (Exception ex)
            {
                results.Add(new { action, ok = false, ms = (int)sw.ElapsedMilliseconds, summary = (string?)null, error = ex.Message });
            }
        }

        var login = await SafeNodeAsync("get_login_info").ConfigureAwait(false);
        results.Add(new
        {
            action = "get_login_info",
            ok = login is not null,
            ms = 0,
            summary = login is null ? null : $"uin={login["user_id"]} nick={login["nickname"]}",
            error = (string?)null,
        });

        var version = await SafeNodeAsync("get_version_info").ConfigureAwait(false);
        results.Add(new
        {
            action = "get_version_info",
            ok = version is not null,
            ms = 0,
            summary = version?.ToJsonString(),
            error = (string?)null,
        });

        await ProbeAsync("get_friend_list", async () =>
        {
            var list = await _contacts.GetFriendsAsync(ct).ConfigureAwait(false);
            var sample = list.Take(3).Select(f => $"{f.Nickname}({f.UserId})");
            return $"{list.Count} 人；前几个：{string.Join("、", sample)}";
        }).ConfigureAwait(false);

        var groups = new List<GroupInfo>();
        await ProbeAsync("get_group_list", async () =>
        {
            var node = await _contacts
                .InvokeApiAsync("get_group_list", null, ct)
                .ConfigureAwait(false);

            if (node is not JsonArray array)
            {
                return "返回结构里没有 data 数组";
            }

            groups = array
                .OfType<JsonObject>()
                .Select(o => new GroupInfo(
                    o["group_id"]?.ToString() ?? "",
                    o["group_name"]?.ToString() ?? "",
                    o["member_count"] is JsonValue v && v.TryGetValue<int>(out var c) ? c : 0,
                    o["max_member_count"] is JsonValue m && m.TryGetValue<int>(out var mc) ? mc : 0))
                .ToList();

            return $"{groups.Count} 个群；前几个：{string.Join("、", groups.Take(3).Select(g => g.Name))}";
        }).ConfigureAwait(false);

        if (groups.Count > 0)
        {
            var groupId = groups[0].GroupId;

            await ProbeAsync("get_group_info", async () =>
            {
                var info = await _contacts.GetGroupAsync(groupId, ct).ConfigureAwait(false);
                return info is null ? "返回 null" : $"{info.Name}（{info.MemberCount}/{info.MaxMemberCount} 人）";
            }).ConfigureAwait(false);

            await ProbeAsync("get_group_member_list", async () =>
            {
                var members = await _contacts.GetGroupMembersAsync(groupId, ct).ConfigureAwait(false);
                var withTitle = members.Count(m => !string.IsNullOrWhiteSpace(m.Title));
                var admins = members.Count(m => m.Role is "admin" or "owner");
                return $"{members.Count} 人；管理/群主 {admins}；有头衔 {withTitle}";
            }).ConfigureAwait(false);
        }
        else
        {
            results.Add(new
            {
                action = "get_group_info / get_group_member_list",
                ok = false,
                ms = 0,
                summary = (string?)null,
                error = "机器人在 0 个群里，无法测试群相关 action",
            });
        }

        return new { allowlist = NapCatAllowlist, results };
    }

    /// <summary>只读 action 白名单：直接复用 <see cref="ChannelApiCatalog"/>（唯一清单）。</summary>
    private static readonly string[] NapCatAllowlist =
        ChannelApiCatalog.All.Where(a => !a.IsWrite).Select(a => a.Action).ToArray();

    private async Task<JsonNode?> SafeNodeAsync(string action)
    {
        try
        {
            return await _contacts.InvokeApiAsync(action, null, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }
}
