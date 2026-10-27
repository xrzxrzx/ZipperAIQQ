using System.Text.Json;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 写入行为设置（第 14 项）：回复模式 + 通知响应开关。
/// </summary>
/// <remarks>
/// <para>
/// payload：<c>{ "platform": "qq", "replyMode": "jev", "notice": { "Poke": true } }</c>，都可省略
/// （<c>platform</c> 缺省 <c>qq</c>，兼容老客户端）。
/// 未知的通知类型 / 未知平台直接拒绝 —— 静默忽略会让人以为"设置成功了"，
/// 静默落到错的平台上更糟（改的是这一端，生效的是另一端）。
/// </para>
/// <para>
/// **S3-D（决策六）起按平台写键**：<c>notice.reply:{platform}:{type}</c>（见
/// <see cref="GetBehaviorAction.NoticeKey"/>）。只写新键；<c>qq</c> 额外**镜像一份老键**
/// <c>notice.reply:{type}</c> —— 万一回退到旧版程序，开关不会突然变空。
/// </para>
/// <para>
/// 通知开关接受两种入参形态（老形态保持不变，新形态为了"一次提交多平台"）：
/// <list type="bullet">
/// <item><c>"notice": { "Poke": true, "GroupBan": { "enabled": false, "platform": "douyin" } }</c></item>
/// <item><c>"notices": [ { "type": "Poke", "enabled": true, "platform": "douyin" } ]</c></item>
/// </list>
/// 单项自带 <c>platform</c> 时**以单项为准**，否则用顶层 <c>platform</c>。
/// </para>
/// </remarks>
public sealed class SetBehaviorAction : IApiAction
{
    private const string Actor = "http-client";

    private readonly IRuntimeSettingsStore _settings;
    private readonly IConfigStore _configs;

    public SetBehaviorAction(IRuntimeSettingsStore settings, IConfigStore configs)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configs);

        _settings = settings;
        _configs = configs;
    }

    public string Name => "setBehavior";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 必须是对象。");
        }

        var platform = GetBehaviorAction.NormalizePlatform(PayloadReader.GetString(payload, "platform"));
        if (!GetBehaviorAction.IsKnownPlatform(platform))
        {
            throw new ApiException(
                ApiResponse.BadRequest,
                $"未知平台：{platform}。可选：{string.Join(" / ", GetBehaviorAction.KnownPlatforms)}");
        }

        var changed = new List<string>();

        if (payload.TryGetProperty("replyMode", out var modeElement))
        {
            var mode = modeElement.GetString()?.Trim().ToLowerInvariant();
            if (mode is null || !GetBehaviorAction.ReplyModes.Contains(mode))
            {
                throw new ApiException(
                    ApiResponse.BadRequest,
                    $"不支持的回复模式：{mode}。可选：{string.Join(" / ", GetBehaviorAction.ReplyModes)}");
            }

            await _settings.SetAsync("replymode.default", mode, ct).ConfigureAwait(false);
            changed.Add($"replyMode={mode}");
        }

        // 先把要写的全部校验完（类型 / 平台 / 值），再统一落库：避免"半套状态"
        var pending = new List<(string Platform, NoticeType Type, bool Enabled)>();

        if (payload.TryGetProperty("notice", out var noticeElement))
        {
            if (noticeElement.ValueKind != JsonValueKind.Object)
            {
                throw new ApiException(
                    ApiResponse.BadRequest, "notice 必须是 { 类型: true/false } 或 { 类型: { enabled, platform } } 对象。");
            }

            foreach (var item in noticeElement.EnumerateObject())
            {
                var type = ParseNoticeType(item.Name);

                switch (item.Value.ValueKind)
                {
                    case JsonValueKind.True or JsonValueKind.False:
                        pending.Add((platform, type, item.Value.GetBoolean()));
                        break;

                    case JsonValueKind.Object:
                        pending.Add((
                            ResolvePlatform(item.Value, platform, item.Name),
                            type,
                            ReadEnabled(item.Value, item.Name)));
                        break;

                    default:
                        throw new ApiException(
                            ApiResponse.BadRequest,
                            $"{item.Name} 必须是 true / false，或 {{ enabled, platform }} 对象。");
                }
            }
        }

        if (payload.TryGetProperty("notices", out var noticesElement))
        {
            if (noticesElement.ValueKind != JsonValueKind.Array)
            {
                throw new ApiException(
                    ApiResponse.BadRequest,
                    "notices 必须是 [ { type, enabled, platform } ] 数组（platform 可省略）。");
            }

            foreach (var item in noticesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    throw new ApiException(
                        ApiResponse.BadRequest, "notices 数组的每一项都必须是 { type, enabled, platform } 对象。");
                }

                var rawType = PayloadReader.GetString(item, "type");
                if (string.IsNullOrWhiteSpace(rawType))
                {
                    throw new ApiException(
                        ApiResponse.BadRequest, "notices 数组的每一项都要有 type（字符串）。");
                }

                pending.Add((
                    ResolvePlatform(item, platform, rawType),
                    ParseNoticeType(rawType),
                    ReadEnabled(item, rawType)));
            }
        }

        // 全部校验完再写，避免半套状态
        foreach (var (itemPlatform, type, enabled) in pending)
        {
            var value = enabled ? "1" : "0";
            var key = GetBehaviorAction.NoticeKey(itemPlatform, type);

            await _settings.SetAsync(key, value, ct).ConfigureAwait(false);

            // qq 镜像老键：回退到旧版程序时开关不会突然变空（读取侧仍然新键优先）
            if (GetBehaviorAction.HasLegacyKeys(itemPlatform))
            {
                await _settings.SetAsync(
                    GetBehaviorAction.LegacyNoticeKey(type), value, ct).ConfigureAwait(false);
            }

            changed.Add($"{key}={value}");
        }

        if (changed.Count == 0)
        {
            throw new ApiException(ApiResponse.BadRequest, "没有可写入的字段（replyMode / notice / notices 至少给一个）。");
        }

        await _configs
            .AppendAuditAsync("setBehavior", Actor, string.Join(",", changed), ct)
            .ConfigureAwait(false);

        return new { updated = changed.Count, changed, platform };
    }

    /// <summary>解析通知类型；不在可配置范围内就 400。</summary>
    private static NoticeType ParseNoticeType(string raw)
    {
        if (!Enum.TryParse<NoticeType>(raw, ignoreCase: true, out var type) ||
            !GetBehaviorAction.ReplyableNotices.Contains(type))
        {
            throw new ApiException(
                ApiResponse.BadRequest,
                $"不支持的通知类型：{raw}。可选：{string.Join(" / ", GetBehaviorAction.ReplyableNotices)}");
        }

        return type;
    }

    /// <summary>取单项的平台：自带就用自带的（校验必须是已知平台），否则用顶层平台。</summary>
    private static string ResolvePlatform(JsonElement item, string fallback, string field)
    {
        var raw = PayloadReader.GetString(item, "platform");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return fallback;
        }

        var platform = GetBehaviorAction.NormalizePlatform(raw);
        if (!GetBehaviorAction.IsKnownPlatform(platform))
        {
            throw new ApiException(
                ApiResponse.BadRequest,
                $"{field} 的平台未知：{platform}。可选：{string.Join(" / ", GetBehaviorAction.KnownPlatforms)}");
        }

        return platform;
    }

    /// <summary>取单项的开关值（必须是 true / false）。</summary>
    private static bool ReadEnabled(JsonElement item, string field)
    {
        if (!item.TryGetProperty("enabled", out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ApiException(ApiResponse.BadRequest, $"{field} 的 enabled 必须是 true 或 false。");
        }

        return value.GetBoolean();
    }
}
