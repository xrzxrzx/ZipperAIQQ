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
/// <para>
/// **按平台取目录**（2026-10-28 修）：以前只注入默认平台（QQ）的目录，
/// 抖音会话永远解析不出昵称 → 会话页显示 <c>douyin:p888…</c> 这种原始 id。
/// 现在每个平台各存一份好友/群表，标签用 <see cref="PlatformLabels"/>（QQ / 抖音）。
/// </para>
/// </remarks>
public sealed class SessionNameResolver : ISessionNameResolver
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

    private readonly IContactDirectoryRegistry _directories;
    private readonly ILogger _logger;
    private readonly object _gate = new();

    /// <summary>平台 → (id → 昵称)。外层键不区分大小写（<c>QQ</c>/<c>qq</c> 都算同一个）。</summary>
    private Dictionary<string, Dictionary<string, string>> _friends = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Dictionary<string, string>> _groups = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _friendsAt = DateTimeOffset.MinValue;
    private DateTimeOffset _groupsAt = DateTimeOffset.MinValue;

    public SessionNameResolver(IContactDirectoryRegistry directories, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(logger);

        _directories = directories;
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
        var platformLabel = PlatformLabels.Label(platform);

        if (isGroup)
        {
            var name = Lookup(_groups, platform, id);
            return string.IsNullOrWhiteSpace(name)
                ? sessionId
                : $"{platformLabel}群 · {name}";
        }

        var friend = Lookup(_friends, platform, id);
        return string.IsNullOrWhiteSpace(friend)
            ? sessionId
            : $"{platformLabel} · {friend}";
    }

    /// <summary>从"平台 → 表"里取值；平台没有表就当没查到（回落原始 id）。</summary>
    private string? Lookup(
        Dictionary<string, Dictionary<string, string>> source,
        string platform,
        string id)
    {
        lock (_gate)
        {
            return source.TryGetValue(platform, out var map) ? map.GetValueOrDefault(id) : null;
        }
    }

    private async Task EnsureAsync(bool needFriends, bool needGroups, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        if (needFriends && now - _friendsAt > Ttl)
        {
            var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var platform in _directories.Platforms)
            {
                var directory = _directories.For(platform);
                if (directory is null)
                {
                    continue;
                }

                try
                {
                    var friends = await directory.GetFriendsAsync(ct).ConfigureAwait(false);
                    map[platform] = friends.ToDictionary(
                        f => f.UserId,
                        f => string.IsNullOrWhiteSpace(f.Remark) ? f.Nickname : $"{f.Nickname}（{f.Remark}）",
                        StringComparer.Ordinal);
                }
                catch (Exception ex)
                {
                    // 单个平台失败不影响其它平台：保留它上一次的表（没有就回落 id）
                    _logger.Warning(ex, "解析 {Platform} 好友昵称失败，沿用上一次缓存（会话页会显示编号）", platform);
                    var previous = Snapshot(_friends).GetValueOrDefault(platform);
                    if (previous is not null)
                    {
                        map[platform] = previous;
                    }
                }
            }

            lock (_gate)
            {
                _friends = map;
                _friendsAt = now;
            }
        }

        if (needGroups && now - _groupsAt > Ttl)
        {
            var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var platform in _directories.Platforms)
            {
                var directory = _directories.For(platform);
                if (directory is null)
                {
                    continue;
                }

                try
                {
                    var groups = await directory.GetGroupsAsync(ct).ConfigureAwait(false);
                    map[platform] = groups.ToDictionary(g => g.GroupId, g => g.Name, StringComparer.Ordinal);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "解析 {Platform} 群名失败，沿用上一次缓存（会话页会显示编号）", platform);
                    var previous = Snapshot(_groups).GetValueOrDefault(platform);
                    if (previous is not null)
                    {
                        map[platform] = previous;
                    }
                }
            }

            lock (_gate)
            {
                _groups = map;
                _groupsAt = now;
            }
        }
    }

    private Dictionary<string, Dictionary<string, string>> Snapshot(
        Dictionary<string, Dictionary<string, string>> source)
    {
        lock (_gate)
        {
            return new Dictionary<string, Dictionary<string, string>>(source, StringComparer.OrdinalIgnoreCase);
        }
    }
}
