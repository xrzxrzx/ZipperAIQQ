using System.Text.Json;
using Serilog;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Server.Ai;
using ZipperAIQQ.Server.Config;
using ZipperAIQQ.Server.Dto;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 写入对话增强开关（问题记录 06 T2.6）：**保存即重建 LLM 客户端**。
/// </summary>
/// <remarks>
/// <para>
/// payload：<c>{ webSearch, readUrl, safetyCheck, title }</c>，缺省的键保持当前值
/// （避免「只想开标题，结果把联网搜索顺手关了」）。
/// </para>
/// <para>
/// 改完必须 <c>ReloadAsync</c>：注入层是挂在客户端传输链上的，
/// 不重建就等于改了个寂寞（用户会以为开关坏了）。
/// </para>
/// </remarks>
public sealed class SetDialogEnhanceAction : IApiAction
{
    private readonly DialogEnhanceService _enhance;
    private readonly IChatClientProvider _clients;
    private readonly ILogger _logger;

    public SetDialogEnhanceAction(
        DialogEnhanceService enhance, IChatClientProvider clients, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(enhance);
        ArgumentNullException.ThrowIfNull(clients);
        ArgumentNullException.ThrowIfNull(logger);

        _enhance = enhance;
        _clients = clients;
        _logger = logger;
    }

    public string Name => "setDialogEnhance";

    public async Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        if (payload.ValueKind != JsonValueKind.Object)
        {
            throw new ApiException(ApiResponse.BadRequest,
                "payload 需要 { webSearch, readUrl, safetyCheck, title }（可只给其中几个）。");
        }

        var current = _enhance.Current;

        var webSearch = ReadFlag(payload, "webSearch", current.WebSearch);
        var readUrl = ReadFlag(payload, "readUrl", current.ReadUrl);
        var safetyCheck = ReadFlag(payload, "safetyCheck", current.SafetyCheckHigh);
        var title = ReadFlag(payload, "title", current.Title);

        var changed = await _enhance
            .UpdateAsync(webSearch, readUrl, safetyCheck, title, ct)
            .ConfigureAwait(false);

        var applied = true;
        if (changed)
        {
            // 注入层在传输链上：不重建客户端，新开关不会生效。
            // 重建失败时**必须如实回报**（旧客户端还在跑），否则界面会显示"已生效"而实际没变。
            applied = await _clients.ReloadAsync(ct).ConfigureAwait(false);
            if (!applied)
            {
                _logger.Warning(
                    "对话增强开关已保存，但 LLM 客户端重建失败 —— 旧客户端仍在用（重启或修好配置后才会生效）");
            }
        }

        var next = _enhance.Current;
        return new
        {
            updated = changed,
            skipped = !changed,
            effective = applied,
            reason = applied ? null : "已保存，但 LLM 客户端重建失败（旧客户端仍在用，重启后生效）",
            webSearch = next.WebSearch,
            readUrl = next.ReadUrl,
            safetyCheck = next.SafetyCheckHigh,
            title = next.Title,
            summary = next.Describe(),
            // 副作用说明也要回给客户端：用户可能开机后直接保存（没走过 getDialogEnhance）
            note = DialogEnhanceNotes.SideEffects,
        };
    }

    /// <summary>读布尔键：缺省 / 类型不对 → 保留当前值。</summary>
    private static bool ReadFlag(JsonElement payload, string name, bool fallback)
    {
        if (!payload.TryGetProperty(name, out var node))
        {
            return fallback;
        }

        return node.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }
}
