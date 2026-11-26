namespace ZipperAIQQ.Agent.Comments;

/// <summary>
/// 读抖音评论区（问题记录 06 / B5）：把某条作品下的评论读成结构化列表。
/// </summary>
/// <remarks>
/// <para>
/// 接口放 Agent、实现放 Server —— 与 <c>IVideoReader</c> / <c>IMailReader</c> 同一条依赖方向：
/// 浏览器底座的帧协议、页面滚动、DOM 解析全在 Server，Agent 不知道它们存在。
/// </para>
/// <para>
/// **读与写拆成两个接口**（本接口只读，<see cref="ICommentReplier"/> 才写）：
/// 读是「看一眼大家在说什么」，写是「代替主人对外发言」。合成一个接口会让
/// 「底座能读」自动蕴含「底座能写」，而写能力默认是关的 —— 那种蕴含迟早有人当真。
/// </para>
/// <para>
/// <see cref="ListAsync"/> 取不到要**抛异常**（带人话原因），不要回空列表冒充「没有评论」。
/// </para>
/// </remarks>
public interface ICommentReader
{
    /// <summary>能不能读（底座已连 + 开关开着）。</summary>
    bool CanRead { get; }

    /// <summary>不可用的人话原因（给模型与日志看）；可用时为空串。</summary>
    string UnavailableReason { get; }

    /// <summary>
    /// 读一条作品下的评论。
    /// </summary>
    /// <param name="itemId">作品 id（纯数字）。</param>
    /// <param name="pages">最多翻几页（<c>&lt;= 0</c> 用服务端配置）。</param>
    /// <param name="limit">最多回几条（<c>&lt;= 0</c> 用服务端配置）。</param>
    /// <param name="ct">取消令牌。</param>
    Task<CommentPage> ListAsync(
        string itemId, int pages, int limit, CancellationToken ct = default);
}
