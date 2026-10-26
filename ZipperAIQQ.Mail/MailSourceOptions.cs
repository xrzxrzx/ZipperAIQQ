namespace ZipperAIQQ.Mail;

/// <summary>
/// 邮件源的运行参数（由 Server 的 <c>mail</c> 配置节绑定，见邮件接入设计 §5.2）。
/// </summary>
/// <remarks>
/// 用 init 属性而非构造函数参数：配置绑定与测试里的 <c>new MailSourceOptions { ... }</c> 都舒服，
/// 且默认值即「能直接用的配置」（设计文档 §5.2 的默认值）。
/// </remarks>
public sealed class MailSourceOptions
{
    /// <summary>首次同步只拉最近多少天（默认 7，避免拉全量）。</summary>
    public int InitialDays { get; init; } = 7;

    /// <summary>轮询间隔秒数（默认 60；IDLE 未实现，本期统一靠轮询）。</summary>
    public int PollIntervalSeconds { get; init; } = 60;

    /// <summary>正文最大字符数（默认 20000，超出截断）。</summary>
    public int MaxBodyChars { get; init; } = 20000;

    /// <summary>轮询间隔（秒），兜底不小于 1 秒，避免配置成 0 时把 CPU 转满。</summary>
    internal TimeSpan PollInterval => TimeSpan.FromSeconds(Math.Max(1, PollIntervalSeconds));

    /// <summary>
    /// 把正文截断到 <see cref="MaxBodyChars"/>，**且不切断代理对**。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 代理对（emoji、部分生僻字）占两个 <c>char</c>：若截断点正好落在高代理上，
    /// 会留下半个字符 —— JSON 序列化后是 <c>\ud83d</c> 这种非法序列，
    /// 客户端解析与日志都会显示成乱码。所以退一格而不是硬切。
    /// </para>
    /// <para>
    /// <see cref="MaxBodyChars"/> &lt;= 0 视为「不限长」（配置写错时宁可全存，也不要静默存空串）。
    /// </para>
    /// </remarks>
    internal string TruncateBody(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var max = MaxBodyChars;
        if (max <= 0 || text.Length <= max)
        {
            return text;
        }

        var length = max;
        if (char.IsHighSurrogate(text[length - 1]))
        {
            length--;
        }

        return length <= 0 ? string.Empty : text[..length];
    }
}
