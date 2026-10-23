using System.Text.Json;
using ZipperAIQQ.Agent;
using ZipperAIQQ.Server.Dto;
using ZipperAIQQ.Storage.Config;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 系统提示词（与人设分开的基础行为规则）。
/// </summary>
/// <remarks>
/// 存储键 <c>prompt.system</c>；为空表示使用内置默认（<see cref="SystemPrompts.Default"/>）。
/// 这样"重置"就是清空键，不需要额外维护一份副本。
/// </remarks>
public sealed class GetSystemPromptAction : IApiAction
{
    private readonly IRuntimeSettingsStore _settings;

    public GetSystemPromptAction(IRuntimeSettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
    }

    public string Name => "getSystemPrompt";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var text = await _settings.GetAsync(SystemPrompts.StorageKey, ct).ConfigureAwait(false);
        var isDefault = string.IsNullOrWhiteSpace(text);

        return new
        {
            text = isDefault ? SystemPrompts.Default : text,
            isDefault,
            defaultText = SystemPrompts.Default,
        };
    }
}

/// <summary>写入系统提示词；提交空文本等同于恢复默认。</summary>
public sealed class SetSystemPromptAction : IApiAction
{
    private const string Actor = "http-client";

    private readonly IRuntimeSettingsStore _settings;
    private readonly IConfigStore _configs;

    public SetSystemPromptAction(IRuntimeSettingsStore settings, IConfigStore configs)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configs);

        _settings = settings;
        _configs = configs;
    }

    public string Name => "setSystemPrompt";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("text", out var textElement) ||
            textElement.ValueKind != JsonValueKind.String)
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 需要 { text: \"...\" }。");
        }

        var text = textElement.GetString()?.Trim() ?? string.Empty;
        if (text.Length > 20_000)
        {
            throw new ApiException(ApiResponse.BadRequest, "系统提示词太长（上限 20000 字）。");
        }

        await _settings.SetAsync(SystemPrompts.StorageKey, text, ct).ConfigureAwait(false);

        await _configs
            .AppendAuditAsync("setSystemPrompt", Actor, $"{(text.Length == 0 ? "恢复默认" : $"{text.Length} 字")}", ct)
            .ConfigureAwait(false);

        return new { isDefault = text.Length == 0, length = text.Length };
    }
}

/// <summary>恢复默认系统提示词（清空键）。</summary>
public sealed class ResetSystemPromptAction : IApiAction
{
    private const string Actor = "http-client";

    private readonly IRuntimeSettingsStore _settings;
    private readonly IConfigStore _configs;

    public ResetSystemPromptAction(IRuntimeSettingsStore settings, IConfigStore configs)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(configs);

        _settings = settings;
        _configs = configs;
    }

    public string Name => "resetSystemPrompt";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        await _settings.SetAsync(SystemPrompts.StorageKey, string.Empty, ct).ConfigureAwait(false);

        await _configs
            .AppendAuditAsync("resetSystemPrompt", Actor, "恢复默认", ct)
            .ConfigureAwait(false);

        return new { isDefault = true, defaultText = SystemPrompts.Default };
    }
}
