using Serilog;
using ZipperAIQQ.Channel;

namespace ZipperAIQQ.Server.Contacts;

/// <summary>
/// 会话 ID（<c>{platform}:{g|p}{id}</c>）→ 可读名称。
/// </summary>
public interface ISessionNameResolver
{
    /// <summary>
    /// 批量解析显示名；解析失败 / 未知的会话回落原始 ID，**不抛异常**。
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        IReadOnlyCollection<string> sessionIds,
        CancellationToken ct = default);

    /// <summary>清掉缓存（联系人变化后调用）。</summary>
    void Invalidate();
}

/// <summary>会话 ID 的解析工具。</summary>
public static class SessionIds
{
    /// <summary>解析成（平台，是否群聊，平台内 ID）；格式不对返回 <c>null</c>。</summary>
    public static (string Platform, bool IsGroup, string Id)? Parse(string? sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var colon = sessionId.IndexOf(':');
        if (colon <= 0 || colon >= sessionId.Length - 2)
        {
            return null;
        }

        var platform = sessionId[..colon];
        var rest = sessionId[(colon + 1)..];
        var prefix = rest[0];
        if (prefix is not ('p' or 'g'))
        {
            return null;
        }

        return (platform, prefix == 'g', rest[1..]);
    }
}

/// <summary>
/// 用联系人目录解析昵称 / 群名，带短期缓存。
/// </summary>
/// <remarks>
/// <para>
/// 为什么放 Server 而不是 Channel：这是**展示**职责（客户端要「平台 + 昵称」），
/// 平台无关的通道层不该关心"怎么拼给人看"。
/// </para>
/// <para>
/// 缓存 5 分钟：好友/群列表一次要拉全量，0.5 秒心跳里反复查会把 NapCat 打满。
/// 拿不到时**保留旧缓存并记 Warning**，不让页面因为一次查询失败就变回编号。
/// </para>
/// </remarks>
public sealed class SessionNameResolver : ISessionNameResolver
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly IContactDirectory _contacts;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    private Dictionary<string, string> _friends = new(StringComparer.Ordinal);
    private Dictionary<string, string> _groups = new(StringComparer.Ordinal);
    private DateTimeOffset _friendsAt = DateTimeOffset.MinValue;
    private DateTimeOffset _groupsAt = DateTimeOffset.MinValue;

    public SessionNameResolver(IContactDirectory contacts, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(contacts);
        ArgumentNullException.ThrowIfNull(logger);

        _contacts = contacts;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, string>> ResolveAsync(
        IReadOnlyCollection<string> sessionIds,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sessionIds);

        var parsed = sessionIds
            .Select(id => (SessionId: id, Parsed: SessionIds.Parse(id)))
            .Where(x => x.Parsed is not null)
            .ToList();

        var needFriends = parsed.Any(x => x.Parsed!.Value.IsGroup == false);
        var needGroups = parsed.Any(x => x.Parsed!.Value.IsGroup);

        await EnsureAsync(needFriends, needGroups, ct).ConfigureAwait(false);

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sessionId in sessionIds)
        {
            result[sessionId] = BuildDisplay(sessionId);
        }

        return result;
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _friendsAt = DateTimeOffset.MinValue;
            _groupsAt = DateTimeOffset.MinValue;
        }
    }

    private string BuildDisplay(string sessionId)
    {
        var parsed = SessionIds.Parse(sessionId);
        if (parsed is null)
        {
            return sessionId;
        }

        var (platform, isGroup, id) = parsed.Value;
        var platformLabel = platform.Equals("qq", StringComparison.OrdinalIgnoreCase)
            ? "QQ"
            : platform.ToUpperInvariant();

        if (isGroup)
        {
            var name = Snapshot(_groups).GetValueOrDefault(id);
            return string.IsNullOrWhiteSpace(name)
                ? sessionId
                : $"{platformLabel}群 · {name}";
        }

        var friend = Snapshot(_friends).GetValueOrDefault(id);
        return string.IsNullOrWhiteSpace(friend)
            ? sessionId
            : $"{platformLabel} · {friend}";
    }

    private Dictionary<string, string> Snapshot(Dictionary<string, string> source)
    {
        lock (_gate)
        {
            return new Dictionary<string, string>(source, StringComparer.Ordinal);
        }
    }

    private async Task EnsureAsync(bool needFriends, bool needGroups, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        if (needFriends && now - _friendsAt > Ttl)
        {
            try
            {
                var friends = await _contacts.GetFriendsAsync(ct).ConfigureAwait(false);
                var map = friends.ToDictionary(
                    f => f.UserId,
                    f => string.IsNullOrWhiteSpace(f.Remark) ? f.Nickname : $"{f.Nickname}（{f.Remark}）",
                    StringComparer.Ordinal);

                lock (_gate)
                {
                    _friends = map;
                    _friendsAt = now;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "解析好友昵称失败，沿用上一次缓存（会话页会显示编号）");
            }
        }

        if (needGroups && now - _groupsAt > Ttl)
        {
            try
            {
                var groups = await _contacts.GetGroupsAsync(ct).ConfigureAwait(false);
                var map = groups.ToDictionary(g => g.GroupId, g => g.Name, StringComparer.Ordinal);

                lock (_gate)
                {
                    _groups = map;
                    _groupsAt = now;
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "解析群名失败，沿用上一次缓存（会话页会显示编号）");
            }
        }
    }
}
