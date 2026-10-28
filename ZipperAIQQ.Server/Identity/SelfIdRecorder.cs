using Microsoft.Extensions.Hosting;
using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Identity;

/// <summary>
/// 把通道发现的机器人账号**按平台**持久化（问题记录 02 #3 / 决策六 / S3-A）。
/// </summary>
/// <remarks>
/// <para>
/// 动态 <c>self_id</c> 只在连接后才有；重启时如果配置里没写 selfId，读回这些键就能继续
/// 正确过滤自激，不必每次依赖环境变量。
/// </para>
/// <para>
/// **每个平台各存一份**（<c>bot.selfId:{platform}</c>）：多平台下 QQ 号与抖音号是两回事，
/// 只存一个键会让「拿 QQ 号去判抖音通知」这种错判出现在<i>持久化</i>这一步
/// （<see cref="IMessageChannel.SelfIdFor"/> 的注释里也提到了同一个风险）。
/// </para>
/// <para>
/// **同时继续写旧键 <c>bot.selfId</c> = QQ 的值**：<c>RuntimeSettingsOverrides</c> 与
/// <c>GetSelfInfoAction</c> 还在读它。单平台（QQ）部署的语义一个字都没变 —— 这是加性变更。
/// 非 QQ 平台的号**不写旧键**：旧键的含义是"QQ 那个号"，混进抖音号只会污染自激过滤。
/// </para>
/// <para>
/// ⚠️ **已知限制**：<see cref="IMessageChannel.SelfIdChanged"/> 的签名是
/// <c>Action&lt;string&gt;</c>，**不带平台** —— 收到事件时无法知道"是哪个平台的号变了"。
/// 兜法是：收到事件就把 <see cref="IMessageChannel.Platforms"/> 里**每个平台**当前的
/// <see cref="IMessageChannel.SelfIdFor"/> 全刷一遍（空值跳过）。代价是每次事件多写几个 KV，
/// 好处是不必改接口签名、也不会漏刷；重连风暴时只是一遍遍写同样的值，幂等无害。
/// </para>
/// </remarks>
public sealed class SelfIdRecorder : BackgroundService
{
    /// <summary>单平台时代的旧键（= QQ 的号）。**不要删**：还有两处在读它。</summary>
    public const string StorageKey = "bot.selfId";

    private readonly IMessageChannel _channel;
    private readonly IRuntimeSettingsStore _settings;
    private readonly ILogger _logger;

    public SelfIdRecorder(
        IMessageChannel channel,
        IRuntimeSettingsStore settings,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);

        _channel = channel;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>按平台存的键名：<c>bot.selfId:{platform}</c>（平台名统一小写）。</summary>
    /// <remarks>
    /// 平台名规范化放在这里，读取方（<c>GetSelfInfoAction</c> 等）可以直接复用 ——
    /// 各写一遍 <c>ToLowerInvariant</c> 迟早在某个大写的 <c>"QQ"</c> 上分岔。
    /// </remarks>
    public static string PlatformStorageKey(string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        return $"{StorageKey}:{platform.Trim().ToLowerInvariant()}";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 事件不带平台（见类型注释），所以这里只是"提醒器"，真正的平台清单从通道问
        void OnSelfId(string selfId) => _ = PersistAllPlatformsAsync(selfId);

        _channel.SelfIdChanged += OnSelfId;
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 正常停止
        }
        finally
        {
            _channel.SelfIdChanged -= OnSelfId;
        }
    }

    /// <summary>把当前每个平台的自身账号都写一遍（事件不带平台，只能全刷）。</summary>
    private async Task PersistAllPlatformsAsync(string changedSelfId)
    {
        // 停止过程里也可能收到事件：用 None 写库，宁可多写几次也要落盘
        var platforms = _channel.Platforms
            .Where(platform => !string.IsNullOrWhiteSpace(platform))
            .Select(platform => platform.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        foreach (var platform in platforms)
        {
            var selfId = NormalizeSelfId(_channel.SelfIdFor(platform));
            if (selfId is null)
            {
                // 这个平台还没握手 / 未登录：跳过，别把空串写进库（写进去等于"记住一个空账号"）
                continue;
            }

            try
            {
                await _settings
                    .SetAsync(PlatformStorageKey(platform), selfId, CancellationToken.None)
                    .ConfigureAwait(false);

                _logger.Information("机器人账号已持久化：{Platform} → {SelfId}", platform, selfId);
            }
            catch (Exception ex)
            {
                // 一个平台写失败不该拖累其他平台
                _logger.Warning(ex, "持久化机器人账号失败：{Platform} → {SelfId}", platform, selfId);
            }
        }

        await PersistLegacyKeyAsync(platforms, changedSelfId).ConfigureAwait(false);
    }

    /// <summary>旧键 <c>bot.selfId</c>：只写 QQ 的值（有 QQ 平台才写）。</summary>
    private async Task PersistLegacyKeyAsync(
        IReadOnlyList<string> platforms,
        string changedSelfId)
    {
        var qq = NoticeCatalog.QqPlatform;
        if (!platforms.Contains(qq, StringComparer.Ordinal))
        {
            return;
        }

        var selfId = NormalizeSelfId(_channel.SelfIdFor(qq));
        if (selfId is null)
        {
            return;
        }

        try
        {
            await _settings.SetAsync(StorageKey, selfId, CancellationToken.None).ConfigureAwait(false);
            _logger.Information("机器人账号旧键已更新（{Platform}）：{SelfId}", qq, selfId);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "持久化旧键 bot.selfId 失败：{SelfId}（事件参数：{ChangedSelfId}）",
                selfId, changedSelfId);
        }
    }

    /// <summary>空白一律当"没有"（平台可能返回 <c>""</c> 或 <c>" "</c>）。</summary>
    private static string? NormalizeSelfId(string? selfId) =>
        string.IsNullOrWhiteSpace(selfId) ? null : selfId.Trim();
}
