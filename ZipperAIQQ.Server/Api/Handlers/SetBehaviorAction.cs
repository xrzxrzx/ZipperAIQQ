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
/// payload：<c>{ "replyMode": "jev", "notice": { "Poke": true, "GroupBan": false } }</c>，两者都可省略。
/// 未知的通知类型直接拒绝 —— 静默忽略会让人以为"设置成功了"。
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

        if (payload.TryGetProperty("notice", out var noticeElement))
        {
            if (noticeElement.ValueKind != JsonValueKind.Object)
            {
                throw new ApiException(ApiResponse.BadRequest, "notice 必须是 { 类型: true/false } 对象。");
            }

            var pending = new List<(string Key, bool Enabled)>();

            foreach (var item in noticeElement.EnumerateObject())
            {
                if (!Enum.TryParse<NoticeType>(item.Name, ignoreCase: true, out var type) ||
                    !GetBehaviorAction.ReplyableNotices.Contains(type))
                {
                    throw new ApiException(
                        ApiResponse.BadRequest,
                        $"不支持的通知类型：{item.Name}。可选：{string.Join(" / ", GetBehaviorAction.ReplyableNotices)}");
                }

                if (item.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    throw new ApiException(ApiResponse.BadRequest, $"{item.Name} 必须是 true 或 false。");
                }

                pending.Add(($"notice.reply:{type}", item.Value.GetBoolean()));
            }

            // 全部校验完再写，避免半套状态
            foreach (var (key, enabled) in pending)
            {
                await _settings.SetAsync(key, enabled ? "1" : "0", ct).ConfigureAwait(false);
                changed.Add($"{key}={(enabled ? "1" : "0")}");
            }
        }

        if (changed.Count == 0)
        {
            throw new ApiException(ApiResponse.BadRequest, "没有可写入的字段（replyMode / notice 至少给一个）。");
        }

        await _configs
            .AppendAuditAsync("setBehavior", Actor, string.Join(",", changed), ct)
            .ConfigureAwait(false);

        return new { updated = changed.Count, changed };
    }
}
