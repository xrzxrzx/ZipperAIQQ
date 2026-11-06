using System.Net.Sockets;
using System.Net.WebSockets;

namespace ZipperAIQQ.Channel.Failures;

/// <summary>
/// 把任意异常翻译成 <see cref="FailureKind"/>。
/// </summary>
/// <remarks>
/// <para>
/// 纯函数、不碰 IO —— 上层（Agent / 调度 / 邮件）都靠它回答一个问题：
/// 「这次失败要不要再试？还要不要叫模型？」
/// </para>
/// <para>
/// <b>判定顺序</b>：① 语义异常（<see cref="ChannelUnavailableException"/> 等）
/// → ② 底层异常类型兜底 → ③ <see cref="FailureKind.Unknown"/>。
/// ② 只是兜底：通道实现仍应在抛出点包语义异常，否则「目标非法」这类会被认成 Unknown。
/// </para>
/// </remarks>
public static class SendFailureClassifier
{
    public static FailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var actual = Unwrap(exception);

        return actual switch
        {
            ChannelUnavailableException => FailureKind.ChannelUnavailable,
            ChannelSendFailedException => FailureKind.ChannelSendFailed,
            TargetInvalidException => FailureKind.TargetInvalid,
            ContentInvalidException => FailureKind.ContentInvalid,

            // 连接层的"断"：socket 已经不可写 / 被释放 / 网络错误
            WebSocketException => FailureKind.ChannelUnavailable,
            ObjectDisposedException => FailureKind.ChannelUnavailable,
            SocketException => FailureKind.ChannelUnavailable,
            IOException => FailureKind.ChannelUnavailable,

            // 连上了但这一条没回来
            TimeoutException => FailureKind.ChannelSendFailed,

            // 取消不是"失败"，交给调用方按 ct 判断；这里保持 Unknown，避免被误报成故障
            OperationCanceledException => FailureKind.Unknown,

            _ => FailureKind.Unknown,
        };
    }

    /// <summary>
    /// 这次失败是否属于「通道自己坏了」—— 用来决定**要不要停下不调模型**。
    /// </summary>
    /// <remarks>
    /// 只有「不可用」算坏；「发送失败」可能是内容/目标问题，不能一概停手。
    /// </remarks>
    public static bool IsChannelDown(FailureKind kind) => kind == FailureKind.ChannelUnavailable;

    /// <summary>剥掉包裹层（<see cref="AggregateException"/> / <see cref="TargetInvocationException"/> / 内层更具体）。</summary>
    private static Exception Unwrap(Exception exception)
    {
        var current = exception;
        var depth = 0;

        while (depth++ < 8)
        {
            var next = current switch
            {
                AggregateException aggregate when aggregate.InnerExceptions.Count == 1 =>
                    aggregate.InnerExceptions[0],
                AggregateException aggregate => aggregate.Flatten().InnerExceptions.FirstOrDefault(),
                System.Reflection.TargetInvocationException { InnerException: { } inner } => inner,
                _ => null,
            };

            if (next is null)
            {
                break;
            }

            current = next;
        }

        return current;
    }
}
