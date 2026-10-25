using System.Text.Json;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Server.Feedback;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Settings;

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
        var enabled = await _settings.GetAsync(OwnerFeedbackService.EnabledKey, ct).ConfigureAwait(false);
        var types = await _settings.GetAsync(OwnerFeedbackService.TypesKey, ct).ConfigureAwait(false);
        var target = await _settings.GetAsync(OwnerFeedbackService.TargetKey, ct).ConfigureAwait(false);

        return new
        {
            enabled = enabled is "1" or "true" or "True",
            types = (string.IsNullOrWhiteSpace(types) ? OwnerFeedbackService.DefaultTypes : types)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            target = target ?? string.Empty,
        };
    }
}

/// <summary>保存主人反馈设置。</summary>
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
            throw new ApiException(ApiResponse.BadRequest, "payload 需要 { enabled, types[], target }。");
        }

        var enabled = payload.TryGetProperty("enabled", out var enabledElement) &&
                      enabledElement.ValueKind is JsonValueKind.True;

        var types = new List<string>();
        if (payload.TryGetProperty("types", out var typesElement) &&
            typesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in typesElement.EnumerateArray())
            {
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

        var target = payload.TryGetProperty("target", out var targetElement) &&
                     targetElement.ValueKind == JsonValueKind.String
            ? targetElement.GetString()?.Trim() ?? string.Empty
            : string.Empty;

        if (target.Length > 0)
        {
            if (!SessionRefParser.TryParse(target, "qq", out var chat, out var error))
            {
                throw new ApiException(ApiResponse.BadRequest, error ?? "目标会话格式不对。");
            }

            target = chat.SessionId;
        }

        await _settings.SetAsync(OwnerFeedbackService.EnabledKey, enabled ? "true" : "false", ct)
            .ConfigureAwait(false);
        await _settings.SetAsync(OwnerFeedbackService.TypesKey, string.Join(',', types), ct)
            .ConfigureAwait(false);
        await _settings.SetAsync(OwnerFeedbackService.TargetKey, target, ct)
            .ConfigureAwait(false);

        await _configs.AppendAuditAsync(
            "setFeedback",
            Actor,
            $"enabled={enabled}, types={string.Join('|', types)}, target={target}",
            ct).ConfigureAwait(false);

        return new
        {
            enabled,
            types = types.Select(t => t.ToString()).ToList(),            target,
        };
    }
}
