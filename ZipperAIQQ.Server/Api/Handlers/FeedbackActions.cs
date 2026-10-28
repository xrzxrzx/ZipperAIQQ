using System.Text.Json;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Access;
using ZipperAIQQ.Server.Contacts;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Server.Feedback;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Settings;

using ZipperAIQQ.Server.Config;
namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>读取主人反馈设置（问题记录 03 #4）。</summary>
public sealed class GetFeedbackAction : IApiAction
{
    private readonly IRuntimeSettingsStore _settings;

    public GetFeedbackAction(IRuntimeSettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public string Name => "getFeedback";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var platform = AccessKeys.Normalize(PayloadReader.GetString(payload, "platform"));

        var enabled = await _settings.GetAsync(OwnerFeedbackService.EnabledKey(platform), ct).ConfigureAwait(false);
        if (enabled is null && AccessKeys.HasLegacyKeys(platform))
        {
            enabled = await _settings.GetAsync(OwnerFeedbackService.LegacyEnabledKey, ct).ConfigureAwait(false);
        }

        var types = await _settings.GetAsync(OwnerFeedbackService.TypesKey(platform), ct).ConfigureAwait(false);
        if (types is null && AccessKeys.HasLegacyKeys(platform))
        {
            types = await _settings.GetAsync(OwnerFeedbackService.LegacyTypesKey, ct).ConfigureAwait(false);
        }

        var target = await _settings.GetAsync(OwnerFeedbackService.TargetKey(platform), ct).ConfigureAwait(false);
        if (target is null && AccessKeys.HasLegacyKeys(platform))
        {
            target = await _settings.GetAsync(OwnerFeedbackService.LegacyTargetKey, ct).ConfigureAwait(false);
        }

        return new
        {
            platform,
            enabled = SettingsValue.IsTrue(enabled),
            types = (string.IsNullOrWhiteSpace(types) ? OwnerFeedbackService.DefaultTypes : types)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            target = target ?? string.Empty,
        };
    }
}

/// <summary>保存主人反馈设置（按平台）。</summary>
public sealed class SetFeedbackAction : IApiAction
{
    private const string Actor = "http-client";

    private readonly IRuntimeSettingsStore _settings;
    private readonly IConfigStore _configs;

    public SetFeedbackAction(IRuntimeSettingsStore settings, IConfigStore configs)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configs);

        _settings = settings;
        _configs = configs;
    }

    public string Name => "setFeedback";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 需要 { enabled, types[], target, platform? }。");
        }

        var platform = AccessKeys.Normalize(PayloadReader.GetString(payload, "platform"));

        var enabled = payload.TryGetProperty("enabled", out var enabledElement) &&
                      enabledElement.ValueKind is JsonValueKind.True;

        var types = new List<string>();
        if (payload.TryGetProperty("types", out var typesElement) &&
            typesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in typesElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    throw new ApiException(ApiResponse.BadRequest, "types 里的每一项都必须是字符串。");
                }

                var text = item.GetString()?.Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                if (string.Equals(text, OwnerFeedbackService.ExternalMessageType, StringComparison.OrdinalIgnoreCase))
                {
                    if (!types.Contains(OwnerFeedbackService.ExternalMessageType))
                    {
                        types.Add(OwnerFeedbackService.ExternalMessageType);
                    }
                }
                else if (Enum.TryParse<NoticeType>(text, ignoreCase: true, out var type) && Enum.IsDefined(type))
                {
                    if (!types.Contains(type.ToString()))
                    {
                        types.Add(type.ToString());
                    }
                }
                else
                {
                    throw new ApiException(ApiResponse.BadRequest, $"未知的通知类型：{text}");
                }
            }
        }

        var target = PayloadReader.GetString(payload, "target")?.Trim() ?? string.Empty;

        if (target.Length > 0)
        {
            // 平台无关解析：抖音 uid 不是纯数字，不能用只认数字的 SessionRefParser
            var parsed = SessionIds.Parse(target);
            if (parsed is null ||
                !string.Equals(parsed.Value.Platform, platform, StringComparison.OrdinalIgnoreCase))
            {
                throw new ApiException(
                    ApiResponse.BadRequest,
                    $"目标会话格式不对（应形如 {platform}:p<id> / {platform}:g<id>）：{target}");
            }

            target = $"{parsed.Value.Platform}:{(parsed.Value.IsGroup ? "g" : "p")}{parsed.Value.Id}";
        }

        var enabledText = enabled ? "true" : "false";
        var typesText = string.Join(',', types);

        await _settings.SetAsync(OwnerFeedbackService.EnabledKey(platform), enabledText, ct).ConfigureAwait(false);
        await _settings.SetAsync(OwnerFeedbackService.TypesKey(platform), typesText, ct).ConfigureAwait(false);
        await _settings.SetAsync(OwnerFeedbackService.TargetKey(platform), target, ct).ConfigureAwait(false);

        // qq 镜像旧键：回退到旧版程序时设置不会突然变空
        if (AccessKeys.HasLegacyKeys(platform))
        {
            await _settings.SetAsync(OwnerFeedbackService.LegacyEnabledKey, enabledText, ct).ConfigureAwait(false);
            await _settings.SetAsync(OwnerFeedbackService.LegacyTypesKey, typesText, ct).ConfigureAwait(false);
            await _settings.SetAsync(OwnerFeedbackService.LegacyTargetKey, target, ct).ConfigureAwait(false);
        }

        await _configs.AppendAuditAsync(
            "setFeedback",
            Actor,
            $"platform={platform}, enabled={enabled}, types={string.Join('|', types)}, target={target}",
            ct).ConfigureAwait(false);

        return new
        {
            platform,
            enabled,
            types = types.Select(t => t.ToString()).ToList(),
            target,
        };
    }
}
