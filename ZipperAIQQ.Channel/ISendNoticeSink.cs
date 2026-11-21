namespace ZipperAIQQ.Channel;

/// <summary>
/// 「发送成功但有降级」的通知出口：通道发出去了，可有些东西**没能原样还原**
/// （如抖音的引用 / @），调用方要据此告诉模型，而不是一句「已发送」了事。
/// </summary>
/// <remarks>
/// <para>
/// 为什么单独一个窄接口，而不是改 <see cref="IMessageChannel.SendAsync"/> 的返回类型：
/// 全仓几十处调用点、三种包装（节流 / 健康统计 / 复合）都要跟着改，且绝大多数平台根本没有
/// 「降级」这个概念（QQ 发什么是什么）。窄接口按需实现、按需查询，代价最小。
/// </para>
/// <para>
/// <b>读即清</b>：<see cref="TakeLastDegraded"/> 取走就清空 —— 否则下一次发送成功时，
/// 上一次的降级说明会继续粘在结果里，模型会以为「这次也降级了」。
/// </para>
/// </remarks>
public interface ISendNoticeSink
{
    /// <summary>
    /// 取走最近一次发送的降级说明。
    /// </summary>
    /// <param name="platform">平台（<c>douyin</c> / <c>qq</c> …）。</param>
    /// <param name="sessionId">会话（<c>ChatRef.SessionId</c>）。</param>
    /// <returns>降级说明（如 <c>reply_to</c> / <c>mention</c>）；没有则 <c>null</c>。</returns>
    string? TakeLastDegraded(string platform, string sessionId);

    /// <summary>
    /// 取走最近一次发送的**投递详情**（如 <c>通道=api,status=0,图片=1</c>）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="TakeLastDegraded"/> 分开：前者说「不是原样」，这里说「怎么发出去的」。
    /// 默认返回 <c>null</c>（绝大多数平台没有这层信息），需要的通道自行实现。
    /// </remarks>
    string? TakeLastDelivery(string platform, string sessionId) => null;
}
