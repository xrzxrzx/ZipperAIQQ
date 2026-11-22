using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Storage.Config;

namespace ZipperAIQQ.Server.Reply;

/// <summary>
/// 「judge 端点自己没有 key 时，能不能借用全局那把 DeepSeek key」的判据。
/// </summary>
/// <remarks>
/// <para>
/// 抽成**纯函数**的唯一理由是它必须可测：这条判据决定「服务端会不会把 DeepSeek key
/// 当 Bearer 发给第三方」。放在 <c>RunAsync</c> 的私有方法里，改坏了没人发现得了。
/// </para>
/// <para>
/// ⚠️ **绝不用「字符串包含 deepseek」当身份判据**。实测这几个都会命中包含匹配：
/// <c>api.deepseek.com.evil.com</c>（后缀伪装）、<c>evil.com/api.deepseek.com</c>（路径）、
/// <c>api.deepseek.com@evil.com</c>（userinfo，真实 host 是 evil.com）、
/// <c>deepseek-proxy.example.cn</c>（第三方中转）。所以只认：**https** + 解析后的 host
/// **全等** <c>deepseek.com</c> 或其后缀 <c>.deepseek.com</c>；或者与 Chat 端点**同源**。
/// </para>
/// </remarks>
public static class JudgeKeyPolicy
{
    /// <summary>DeepSeek 官方域名（含其子域）。</summary>
    private const string DeepSeekDomain = "deepseek.com";

    /// <summary>
    /// 判定 judge 能否借用全局 key。
    /// </summary>
    /// <param name="judge">judge 端点（自己的密文解不开或无密文时才会走到这里）。</param>
    /// <param name="chatEndpoint">Chat 角色指向的端点（用于同源判定）；没有则传 <c>null</c>。</param>
    /// <param name="hasGlobalKey">全局槽位里是否真有一把能用的 key。</param>
    /// <returns>
    /// <c>Borrow = true</c> 表示可以借用；否则 <c>Reason</c> 说明拒绝原因（会被写进日志）。
    /// </returns>
    public static (bool Borrow, string? Reason) Decide(
        ModelEndpointSetting judge, ModelEndpointSetting? chatEndpoint, bool hasGlobalKey)
    {
        ArgumentNullException.ThrowIfNull(judge);

        // ① decisions 是**外部供应商**（typesafe/jev），它的 key 与 DeepSeek 无关，混用就是串号
        if ((LlmApiKind)judge.ApiKind == LlmApiKind.Decisions)
        {
            return (false,
                $"judge 端点 {judge.Name} 是 decisions 对外部供应商，不能借用 DeepSeek 全局 key；" +
                "请用 setConfig 给它写一把自己的 key");
        }

        if (!hasGlobalKey)
        {
            return (false, null);   // 没得借，不需要解释
        }

        // ② 官方域名（只认 https，否则 key 会在网线上裸奔）
        if (IsDeepSeekHost(judge.BaseUrl))
        {
            return (true, null);
        }

        // ③ 与 Chat 端点同源：同源才可能同一个账号
        if (chatEndpoint is not null && IsSameOrigin(chatEndpoint.BaseUrl, judge.BaseUrl))
        {
            return (true, null);
        }

        return (false,
            $"judge 端点 {judge.Name}（{Describe(judge.BaseUrl)}）既不是 DeepSeek 官方域名，也与 Chat 端点不同源，" +
            "拒绝把全局 key 发过去");
    }

    /// <summary>
    /// 日志用的地址描述：**剥掉 userinfo**（<c>https://user:pass@host/</c> 这种）与路径。
    /// </summary>
    /// <remarks>
    /// 端点写入只校验「是绝对 URI」，所以带账号密码的地址是能被存进来的；
    /// 原样打进日志就等于把密码写进文件（规范 §7「绝不记录机密」）。
    /// </remarks>
    private static string Describe(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
        {
            return "(无法解析的地址)";
        }

        return uri.IsDefaultPort
            ? $"{uri.Scheme}://{uri.Host}"
            : $"{uri.Scheme}://{uri.Host}:{uri.Port}";
    }

    /// <summary>
    /// 是否 DeepSeek 官方 host（<c>deepseek.com</c> 或其子域，**且必须是 https**）。
    /// </summary>
    public static bool IsDeepSeekHost(string? baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        var host = uri.Host;
        return host.Equals(DeepSeekDomain, StringComparison.OrdinalIgnoreCase)
               || host.EndsWith("." + DeepSeekDomain, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>同源判定（scheme + host + port；userinfo 与路径不参与）。</summary>
    public static bool IsSameOrigin(string? left, string? right)
    {
        if (!Uri.TryCreate(left, UriKind.Absolute, out var a) ||
            !Uri.TryCreate(right, UriKind.Absolute, out var b))
        {
            return false;
        }

        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
               && a.Port == b.Port;
    }
}
