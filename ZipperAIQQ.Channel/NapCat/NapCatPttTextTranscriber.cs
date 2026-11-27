using System.Text.Json.Nodes;
using Serilog;
using ZipperAIQQ.Channel.Audio;

namespace ZipperAIQQ.Channel.NapCat;

/// <summary>
/// NapCat 自带的语音转文字（<c>fetch_ptt_text</c>）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么走这条路：需求 FR-1.5 要求语音在通道层转文本；
/// NapCat 扩展 API 里已有 <c>fetch_ptt_text</c>，比本地 ASR 模型轻得多（用户 Q5 定稿）。
/// </para>
/// <para>
/// **可用性探测不额外发请求**：直接尝试真实语音；
/// 连续失败 <see cref="SpeechOptions.MaxConsecutiveFailures"/> 次即熔断
/// （<see cref="SpeechOptions.CooldownMs"/> 后允许再试一次），之后不再尝试 ——
/// 每条语音都白等一次超时是不可接受的。
/// 判定结果与原因都会进日志，便于确认 NapCat / QQ 侧到底卡在哪一步。
/// </para>
/// <para>
/// ⚠️ <b>超时与全局 API 超时解耦</b>（B00001）：原先复用 30 秒的全局超时，
/// 而 NapCat 那条链路一步卡住就是 10 秒 ×3，于是「一条语音 = 接收停滞 30~60 秒」。
/// 现在单次最多等 <see cref="SpeechOptions.TimeoutMs"/>，且**超时不重试**。
/// </para>
/// </remarks>
public sealed class NapCatPttTextTranscriber : ISpeechTranscriber
{
    /// <summary>NapCat 的语音转文字 action 名。</summary>
    public const string ActionName = "fetch_ptt_text";

    private readonly Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> _callApi;
    private readonly SpeechOptions _options;
    private readonly ILogger _logger;

    private int _consecutiveFailures;

    /// <summary>熔断到期时间（UTC ms）；0 = 没有熔断。半开靠它，不需要重启。</summary>
    private long _disabledUntilMs;

    public NapCatPttTextTranscriber(
        Func<string, JsonObject?, CancellationToken, Task<JsonNode?>> callApi,
        SpeechOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(callApi);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _callApi = callApi;
        _options = options;
        _logger = logger;
    }

    public bool IsAvailable
    {
        get
        {
            if (!_options.Enabled)
            {
                return false;
            }

            var until = Interlocked.Read(ref _disabledUntilMs);
            return until == 0 || DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= until;
        }
    }

    public async Task<string?> TranscribeAsync(AudioClip clip, CancellationToken ct = default)
    {
        // message_id 是 NapCat 的必填项：没有它连 schema 都过不了（1400），
        // 所以直接跳过，不浪费一次往返。
        if (!IsAvailable || clip is null || string.IsNullOrWhiteSpace(clip.MessageId))
        {
            return null;
        }

        var parameters = new JsonObject
        {
            ["message_id"] = clip.MessageId,
        };

        if (!string.IsNullOrWhiteSpace(clip.FileId))
        {
            parameters["file"] = clip.FileId;
        }

        var attempts = Math.Max(1, _options.MaxAttempts);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            JsonNode? data;
            bool timedOut;
            try
            {
                (data, timedOut) = await TryOnceAsync(parameters, clip.MessageId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 调用方取消（停机 / 收轮被取消）：不是平台的错，不计失败
                return null;
            }

            if (data is not null)
            {
                var text = ExtractText(data);
                if (text is not null)
                {
                    Interlocked.Exchange(ref _consecutiveFailures, 0);
                    return text.Length == 0 ? null : text;
                }
            }

            if (timedOut)
            {
                // 超时 = NapCat 侧整条链路卡住（取消息 / 转文字 / 再取回，一步 10 秒）。
                // 再问一轮只是把等待翻倍，所以直接判失败。
                break;
            }

            if (attempt >= attempts)
            {
                break;
            }

            // 语音刚到就转写时，QQ 侧 ASR 可能还没出结果 —— 等一下再问（这是快速失败，不是超时）
            try
            {
                await Task.Delay(Math.Max(0, _options.RetryDelayMs), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }

        OnFailure();
        return null;
    }

    /// <summary>
    /// 带墙上时间上限地调一次 NapCat。
    /// </summary>
    /// <returns>
    /// <c>Data</c> = 返回的 data 节点；<c>TimedOut</c> = 是不是撞了时间上限（超时不重试）。
    /// </returns>
    /// <remarks>
    /// 用 <see cref="Task.WhenAny(Task, Task)"/> 而不是 <c>CancellationTokenSource</c>：
    /// 后者会取消**共享的** API 调用层，把「转写超时」变成「连接异常」，
    /// 而这里需要的只是「我不再等这一条了」。
    /// 被放弃的任务会在后台自行结束（NapCat 侧本来就卡着），异常吞掉避免 Unobserved。
    /// </remarks>
    private async Task<(JsonNode? Data, bool TimedOut)> TryOnceAsync(
        JsonObject parameters, string? messageId, CancellationToken ct)
    {
        Task<JsonNode?> call;
        try
        {
            call = _callApi(ActionName, parameters, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 同步抛出也算一次快速失败（真实实现是 async，但别指望调用方永远守规矩）
            _logger.Warning(ex, "语音转写异常（message={MessageId}）", messageId);
            return (null, false);
        }

        var timeoutMs = _options.TimeoutMs > 0 ? _options.TimeoutMs : 8_000;
        var finished = await Task.WhenAny(call, Task.Delay(timeoutMs, ct)).ConfigureAwait(false);

        if (finished != call)
        {
            // 先分清「我们放弃等待」与「调用方取消」：后者不该算平台失败
            ct.ThrowIfCancellationRequested();

            _ = call.ContinueWith(
                t => _ = t.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

            _logger.Warning(
                "语音转写超过 {TimeoutMs}ms 未返回（message={MessageId}）—— NapCat 侧无响应，本次放弃",
                timeoutMs, messageId);
            return (null, true);
        }

        try
        {
            return (await call.ConfigureAwait(false), false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "语音转写异常（message={MessageId}）", messageId);
            return (null, false);
        }
    }

    private void OnFailure()
    {
        var count = Interlocked.Increment(ref _consecutiveFailures);
        if (count < Math.Max(1, _options.MaxConsecutiveFailures))
        {
            return;
        }

        var cooldown = Math.Max(1, _options.CooldownMs);
        Interlocked.Exchange(
            ref _disabledUntilMs,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + cooldown);

        _logger.Warning(
            "语音转写连续失败 {Count} 次，已熔断 {Minutes:0.#} 分钟（{Action} 无响应；" +
            "语音将保留 [语音] 占位。到期会自动再试一次，无需重启）",
            count, cooldown / 60000.0, ActionName);
    }

    /// <summary>
    /// 从返回里取文本：兼容 <c>{"text": "..."}</c>、纯字符串、以及 <c>{"data": {...}}</c> 包装。
    /// </summary>
    private static string? ExtractText(JsonNode? data) => data switch
    {
        null => null,
        JsonValue value when value.TryGetValue<string>(out var text) => text.Trim(),
        JsonObject obj when obj["text"] is JsonValue text && text.TryGetValue<string>(out var t) => t.Trim(),
        JsonObject obj when obj["data"] is JsonValue inner && inner.TryGetValue<string>(out var d) => d.Trim(),
        JsonObject obj when obj["data"] is JsonObject innerObj => ExtractText(innerObj),
        _ => null,
    };
}
