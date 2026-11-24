namespace ZipperAIQQ.Server.Ai;

/// <summary>
/// 对话增强的**副作用说明**（给界面看，由服务端统一定义）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么放服务端：这两句是「你开了它，会失去什么」的事实陈述，
/// 客户端各写一份必然漂移（一处改了另一处没改，用户看到的提示就是错的）。
/// </para>
/// <para>
/// 内容来自官方文档（「被忽略的参数」表 + 内容安全的拒答语义），不是我们的实现细节。
/// </para>
/// </remarks>
public static class DialogEnhanceNotes
{
    /// <summary>开启任一项后必须让用户知道的两件事。</summary>
    public const string SideEffects =
        "开启任一项后，服务端会忽略 temperature / top_p 等采样参数（官方「被忽略的参数」表），本地采样配置随之失效；" +
        "开启内容安全 high 后，命中风控的回复会以拒答结束 —— 本轮不会发出任何消息。";
}
