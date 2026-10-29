using Microsoft.Extensions.Hosting;
using Serilog;
using ZipperAIQQ.Channel.Filtering;
using ZipperAIQQ.Server.Access;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Contacts;

/// <summary>
/// 主人放行范围（问题记录 #22）：主人私聊 + **主人所在的每个群**，**按平台分别计算**。
/// </summary>
public interface IOwnerScope
{
    /// <summary>当前放行的会话（<c>qq:p123</c> / <c>qq:g456</c> / <c>douyin:g…</c>）。</summary>
    IReadOnlyCollection<string> Current { get; }

    /// <summary>重新解析并写入过滤器；失败保留上一次结果。</summary>
    Task RefreshAsync(CancellationToken ct = default);
}

/// <summary>
/// 解析主人放行范围，周期性刷新并写入 <see cref="IChatFilter"/>。
/// </summary>
/// <remarks>
/// <para>
/// 为什么是后台服务而不是启动时算一次：解析需要平台通道在线（群列表 + 群成员），
/// 而通道连接是异步的 —— 启动时算大概率拿不到，得能重试。
/// </para>
/// <para>
/// 为什么失败只降级到私聊：查询群成员失败不等于"主人被拉黑了"，
/// 但也不能因此让主人一条消息都收不到；私聊放行不依赖任何查询。
/// </para>
/// <para>
/// S3-C（决策四）起**按平台**：每个平台有自己的主人、自己的群、自己的放行集合。
/// 某个平台查询失败只影响该平台，其他平台照常。
/// </para>
/// <para>
/// 开销控制：群成员列表逐个查，超过 <see cref="MaxGroupsToInspect"/> 个群时只查前 N 个并记日志。
/// 成功后 10 分钟刷新一次，失败 60 秒后重试。
/// </para>
/// </remarks>
public sealed class OwnerScopeResolver : BackgroundService, IOwnerScope
{
    /// <summary>一次解析最多检查多少个群（防止几百个群时每分钟上百次 API 调用）。</summary>
    private const int MaxGroupsToInspect = 50;

    private static readonly TimeSpan SuccessInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(60);

    private readonly IRuntimeSettingsStore _settings;
    private readonly IContactDirectoryRegistry _directories;
    private readonly IChatFilter _filter;
    private readonly ILogger _logger;

    private volatile IReadOnlyCollection<string> _current = [];
    private volatile bool _groupsResolved;

    public OwnerScopeResolver(
        IRuntimeSettingsStore settings,
        IContactDirectoryRegistry directories,
        IChatFilter filter,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _directories = directories;
        _filter = filter;
        _logger = logger;
    }

    public IReadOnlyCollection<string> Current => _current;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var allow = new List<string>();
        var allResolved = true;

        foreach (var platform in _directories.Platforms)
        {
            ct.ThrowIfCancellationRequested();

            var directory = _directories.For(platform);
            if (directory is null)
            {
                continue;
            }

            string? owner;
            try
            {
                owner = (await AccessSettings.GetOwnerAsync(_settings, platform, ct)
                    .ConfigureAwait(false))?.Trim();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                allResolved = false;
                _logger.Warning(ex, "读取 {Platform} 平台的主人失败，本次不更新该平台放行范围", platform);
                continue;
            }

            var platformAllow = new List<string>();
            if (!string.IsNullOrWhiteSpace(owner))
            {
                // 黑名单优先：被显式拉黑的会话不再进「主人放行」，免得 UI 显示"已放行"但实际被拦
                var blacklist = _filter.Blacklist(platform);
                bool IsBlacklisted(string sessionId) =>
                    blacklist.Any(pattern => ChatFilter.IsMatch(pattern, sessionId));

                var ownerPrivate = $"{platform}:p{owner}";
                if (!IsBlacklisted(ownerPrivate))
                {
                    platformAllow.Add(ownerPrivate);
                }

                try
                {
                    var groups = await directory.GetGroupsAsync(ct).ConfigureAwait(false);
                    if (groups.Count > MaxGroupsToInspect)
                    {
                        _logger.Information(
                            "{Platform}：群数量 {Total} 超过上限 {Max}，只检查前 {Max} 个",
                            platform, groups.Count, MaxGroupsToInspect, MaxGroupsToInspect);
                    }

                    foreach (var group in groups.Take(MaxGroupsToInspect))
                    {
                        ct.ThrowIfCancellationRequested();

                        var groupSession = $"{platform}:g{group.GroupId}";
                        if (IsBlacklisted(groupSession))
                        {
                            continue;
                        }

                        var members = await directory.GetGroupMembersAsync(group.GroupId, ct).ConfigureAwait(false);
                        if (members.Any(m => string.Equals(m.UserId, owner, StringComparison.Ordinal)))
                        {
                            platformAllow.Add(groupSession);
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    allResolved = false;
                    _logger.Warning(ex,
                        "{Platform}：解析主人所在群失败，本次只保证主人私聊放行（群里可能仍会被名单过滤）",
                        platform);
                }
            }

            // 先写入该平台，再继续下一个：一个平台失败不影响另一个（决策四）
            _filter.UpdateAlwaysAllow(platform, platformAllow);
            allow.AddRange(platformAllow);
        }

        _groupsResolved = allResolved;
        Apply(allow);
    }

    private void Apply(IReadOnlyCollection<string> allow)
    {
        var before = _current;
        _current = allow;

        if (!before.SequenceEqual(allow, StringComparer.OrdinalIgnoreCase))
        {
            _logger.Information("主人放行范围已更新：{Count} 个会话", allow.Count);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _groupsResolved = false;
                _logger.Warning(ex, "刷新主人放行范围失败");
            }

            var delay = _groupsResolved ? SuccessInterval : RetryInterval;
            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
