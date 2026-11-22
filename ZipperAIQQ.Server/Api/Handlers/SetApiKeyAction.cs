using System.Text.Json;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Server.Dto;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 写入唯一的 DeepSeek key（问题记录 06 §5：对外一把 key）。
/// </summary>
/// <remarks>
/// payload 形状：<code>{ "key": "sk-..." }</code>
/// 明文只在这一次请求里出现，落库前立刻加密；响应**只回掩码与状态**，永不回显明文。
/// </remarks>
public sealed class SetApiKeyAction : IApiAction
{
    /// <summary>没有用户体系，审计主体固定成"经 HTTP 配置的客户端"（与 setConfig 同口径）。</summary>
    private const string Actor = "http-client";

    private readonly RuntimeConfigService _config;

    public SetApiKeyAction(RuntimeConfigService config)
    {
        ArgumentNullException.ThrowIfNull(config);
        _config = config;
    }

    public string Name => "setApiKey";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiResponse.BadRequest, "payload 需要 { key }。");
        }

        var key = PayloadReader.GetString(payload, "key");
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new ApiException(ApiResponse.BadRequest, "key 不能为空。");
        }

        var updated = await _config.SetApiKeyAsync(key, Actor, ct).ConfigureAwait(false);

        var view = await _config.GetViewAsync(ct).ConfigureAwait(false);

        return new
        {
            updated,
            skipped = !updated,
            config = view,
        };
    }
}
