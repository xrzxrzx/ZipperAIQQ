using ZipperAIQQ.Channel.Failures;

namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// 把失败分类翻译成**模型能执行的下一步**。
/// </summary>
/// <remarks>
/// 回一句「发送失败」等于没说：模型最可能的反应是换个说法再发一遍 ——
/// 通道坏的时候这纯属再烧一次调用（问题记录 04 #7）。
/// 这里的每一条都在回答「现在该怎么办」。
/// </remarks>
public static class FailureGuidance
{
    public static string Describe(FailureKind kind) => kind switch
    {
        FailureKind.ChannelUnavailable =>
            "通道不可用（不是你漏调工具）：这条没有发出去，换说法重发也没用。"
            + "请如实告诉对方你这边暂时发不出去，或稍后再试。",

        FailureKind.ChannelSendFailed =>
            "发送超时 / 平台返回失败：可以重试一次；仍然失败就直接告诉对方。",

        FailureKind.TargetInvalid =>
            "目标会话无效：不要再重发，请提示用户该会话可能不存在。",

        FailureKind.ContentInvalid =>
            "内容被平台拒绝：先改写内容再发，不要原样重发。",

        FailureKind.RateLimited =>
            "被限流：不要立刻重发，隔一会儿再说。",

        _ =>
            "发送失败：原因见上；不要原样无限重试。",
    };
}
