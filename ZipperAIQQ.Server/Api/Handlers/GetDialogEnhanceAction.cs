using System.Text.Json;
using ZipperAIQQ.Server.Ai;

namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 读取对话增强开关（问题记录 06 T2.6）。
/// </summary>
/// <remarks>
/// 响应里带 <c>note</c>：开启后 <c>temperature</c> / <c>top_p</c> 等采样参数由服务端决定。
/// 这句话必须**由服务端给出**（而不是客户端写死），否则两边口径迟早不一致。
/// </remarks>
public sealed class GetDialogEnhanceAction : IApiAction
{
    private readonly DialogEnhanceService _enhance;

    public GetDialogEnhanceAction(DialogEnhanceService enhance)
    {
        ArgumentNullException.ThrowIfNull(enhance);
        _enhance = enhance;
    }

    public string Name => "getDialogEnhance";

    public Task<object?> HandleAsync(JsonElement payload, CancellationToken ct)
    {
        var current = _enhance.Current;

        return Task.FromResult<object?>(new
        {
            webSearch = current.WebSearch,
            readUrl = current.ReadUrl,
            safetyCheck = current.SafetyCheckHigh,
            title = current.Title,
            anyEnabled = current.AnyEnabled,
            summary = current.Describe(),
            note = DialogEnhanceNotes.SideEffects,
        });
    }
}
