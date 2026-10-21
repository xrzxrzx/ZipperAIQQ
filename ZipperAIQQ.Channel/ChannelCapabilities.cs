namespace ZipperAIQQ.Channel;

/// <summary>
/// 通道能力矩阵：声明这个平台**能做什么**。
/// </summary>
/// <remarks>
/// <para>
/// 存在的意义（FR-1.4）：让工具的参数描述**按能力动态生成**。
/// 否则模型会给一个不支持图片的平台发图片，白费一次调用，还可能被平台静默丢弃。
/// </para>
/// <para>
/// ⚠️ 这是**客观事实**，不是配置项 —— 平台支不支持图片是它的事，
/// 配成「支持」也发不出去。所以这里用不可变的标志位，而不是让用户改。
/// </para>
/// </remarks>
[Flags]
public enum ChannelCapabilities
{
    None = 0,

    /// <summary>文本消息。</summary>
    Text = 1 << 0,

    /// <summary>图片消息。</summary>
    Image = 1 << 1,

    /// <summary>@ 提及。</summary>
    Mention = 1 << 2,

    /// <summary>引用回复。</summary>
    Reply = 1 << 3,

    /// <summary>常见组合：文本 + 图片 + @ + 引用（QQ 通道即此）。</summary>
    Standard = Text | Image | Mention | Reply,
}
