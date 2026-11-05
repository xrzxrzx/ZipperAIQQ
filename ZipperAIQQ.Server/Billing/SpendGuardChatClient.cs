using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Server.Alerts;

namespace ZipperAIQQ.Server.Billing;

/// <summary>
/// 额度闸门：每次模型调用前问一句「这个窗口还能不能花」。
/// </summary>
/// <remarks>
/// <para>
/// 包在 <c>FunctionInvokingChatClient</c> **外层**：工具循环里的每次调用都会再经过这里，
/// 不会出现「一轮里被循环放大」的漏网。
/// </para>
/// <para>
/// 只拦**非关键**调用（<see cref="CallPurposeScope"/>）：真人消息与命令永远放行（D-03）。
/// 没有数据（<c>HasData=false</c>）时也放行 —— 宁可少拦，不可误拦。
/// </para>
/// </remarks>
public sealed class SpendGuardChatClient : DelegatingChatClient
{
    private readonly ISpendReader _spend;
    private readonly QuotaOptions _options;
    private readonly IAlertSink? _alerts;
    private readonly ILogger _logger;

    public SpendGuardChatClient(
        IChatClient inner,
        ISpendReader spend,
        QuotaOptions options,
        ILogger logger,
        IAlertSink? alerts = null)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(spend);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _spend = spend;
        _options = options;
        _logger = logger;
        _alerts = alerts;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        await CheckAsync(cancellationToken).ConfigureAwait(false);
        return await base.GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await CheckAsync(cancellationToken).ConfigureAwait(false);

        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return update;
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        if (!_options.Enabled || _options.LimitCny <= 0)
        {
            return;
        }

        var snapshot = await _spend.ReadAsync(ct).ConfigureAwait(false);
        if (!snapshot.HasData)
        {
            return;   // 没数据不拦（见类注释）
        }

        var purpose = CallPurposeScope.Current;
        var limit = _options.LimitCny;

        if (snapshot.SpentCny >= limit)
        {
            if (!CallPurposeScope.IsNonCritical(purpose))
            {
                _logger.Warning(
                    "额度已到硬线（{Spent:F2} / {Limit:F2}），但这是关键调用（{Purpose}），放行",
                    snapshot.SpentCny, limit, purpose);
                return;
            }

            Raise(AlertKind.QuotaHardStop, purpose, snapshot, limit);
            throw new QuotaExceededException(
                $"额度已到硬线（¥{limit:F2}，窗口花费 ¥{snapshot.SpentCny:F2}），非关键调用被拦：{purpose}");
        }

        if (snapshot.SpentCny >= limit * _options.WarnRatio)
        {
            Raise(AlertKind.QuotaWarn, purpose, snapshot, limit);
        }
    }

    private void Raise(AlertKind kind, CallPurpose purpose, SpendSnapshot snapshot, decimal limit)
    {
        // 抑制窗口由 OwnerAlertService 负责（同类型 10 分钟内只报一次），这里不重复判断
        _ = _alerts?.RaiseAsync(
            new AlertMessage(
                kind,
                null,
                kind == AlertKind.QuotaWarn
                    ? $"⚠️ 额度预警：窗口已用 ¥{snapshot.SpentCny:F2} / 上限 ¥{limit:F2}（{purpose}）"
                    : $"🛑 额度已到硬线 ¥{limit:F2}，已拦截非关键调用（{purpose}）",
                BeijingTime.Now),
            CancellationToken.None);
    }
}
