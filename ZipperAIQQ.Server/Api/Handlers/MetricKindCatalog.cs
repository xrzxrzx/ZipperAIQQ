namespace ZipperAIQQ.Server.Api.Handlers;

/// <summary>
/// 指标类别（<c>Kind</c>）→ 中文名。
/// </summary>
/// <remarks>
/// <para>
/// 问题记录 #6：数据页的「类别」列只显示英文标识，用户看不懂。
/// 但英文名**不能丢** —— 排查日志、对代码时还要它，所以界面是「中文名 + 英文次行」。
/// </para>
/// <para>
/// 放在 Server 而不是 Storage：这是**展示映射**，存储层不该知道"怎么念"。
/// 未登记的新 Kind 原样返回，不报错（新增指标时不至于让页面空掉）。
/// </para>
/// </remarks>
public static class MetricKindCatalog
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["llm.call"] = "LLM 调用",
        ["llm.retry"] = "LLM 重试（漂移纠正）",
        ["retrieval"] = "记忆检索",
        ["retrieval.hit"] = "检索命中",
        ["jev.judge"] = "JEV 判断",
        ["jev.degrade"] = "JEV 降级",
        ["summary"] = "上下文压缩",
        ["tool.send_message"] = "发送消息",
        ["embedding.backfill"] = "向量回填",
        ["tool.web_search"] = "网页搜索",
        ["tool.fetch_url"] = "网页抓取",
        ["tool.search_history"] = "检索历史",
        ["tool.get_recent_history"] = "读取近期历史",
        ["tool.search_other_sessions"] = "跨会话检索",
        ["tool.send_to_other_session"] = "跨会话发消息",
    };

    /// <summary>取中文名；没登记的类别原样返回。</summary>
    public static string Label(string kind) =>
        Labels.TryGetValue(kind, out var label) ? label : kind;
}
