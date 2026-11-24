namespace ZipperAIQQ.Agent.Llm;

/// <summary>
/// 对话增强开关（DeepSeek 全家桶的「服务端替你做」那批能力）。
/// </summary>
/// <remarks>
/// <para>
/// 四个键与官方请求体一一对应（<c>guides/build_a_chatbot</c>）：
/// <c>tools: [{type:"deepseek_web_search"}]</c>、<c>tools:[{type:"deepseek_read_url"}]</c>、
/// <c>safety_check: {level:"high"}</c>、<c>title: {enabled:true}</c>。
/// </para>
/// <para>
/// ⚠️ **代价**：任一项开启后，服务端会忽略 <c>temperature</c> / <c>top_p</c> / 采样类参数
/// （官方「被忽略的参数」表），本项目的采样策略会**静默失效**。所以默认全关，
/// 且状态页必须把这件事显式说出来 —— 不然调了半天温度没反应，没人查得出来。
/// </para>
/// </remarks>
public sealed record DeepSeekEnhanceOptions
{
    /// <summary>联网搜索：模型可按提问自动搜网页并附引用。</summary>
    public bool WebSearch { get; init; }

    /// <summary>阅读链接：模型可自动抓取消息里的链接。</summary>
    public bool ReadUrl { get; init; }

    /// <summary>
    /// 模型内容安全（<c>safety_check.level = high</c>）。
    /// </summary>
    /// <remarks>
    /// 命中风险时响应会以**拒答**结束（Responses：<c>status=incomplete / content_filter</c>），
    /// 已下发的文本收不回来。开启前要接受这个行为。
    /// </remarks>
    public bool SafetyCheckHigh { get; init; }

    /// <summary>会话标题：回答结束时让服务端生成一个短标题。</summary>
    public bool Title { get; init; }

    /// <summary>是否有任何一项开启（决定要不要挂注入层）。</summary>
    public bool AnyEnabled => WebSearch || ReadUrl || SafetyCheckHigh || Title;

    /// <summary>给状态页/日志用的一句话摘要。</summary>
    public string Describe()
    {
        if (!AnyEnabled)
        {
            return "对话增强：全关（采样参数按本地配置生效）";
        }

        var parts = new List<string>(4);
        if (WebSearch)
        {
            parts.Add("联网搜索");
        }

        if (ReadUrl)
        {
            parts.Add("阅读链接");
        }

        if (SafetyCheckHigh)
        {
            parts.Add("内容安全=high");
        }

        if (Title)
        {
            parts.Add("会话标题");
        }

        return "对话增强：" + string.Join(" / ", parts) + "（采样参数由服务端决定，本地配置被忽略）";
    }
}
