using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Server.Reply;
using ZipperAIQQ.Storage.Config;

namespace ZipperAIQQ.Tests.Server;

/// <summary>
/// judge 能否借用全局 DeepSeek key 的判据（<see cref="JudgeKeyPolicy"/>）。
/// </summary>
/// <remarks>
/// 这条判据决定「服务端会不会把 DeepSeek key 当 Bearer 发给第三方」，
/// 所以重点不是「官方域名能过」，而是**各种伪装都要过不去**。
/// </remarks>
public sealed class JudgeKeyPolicyTests
{
    private const string GlobalKey = "sk-global";

    [Fact]
    public void Decisions_endpoint_never_borrows_the_global_key()
    {
        var judge = Endpoint("judge", "https://api.typesafe.example/api/alpha", LlmApiKind.Decisions);

        var (borrow, reason) = JudgeKeyPolicy.Decide(judge, null, hasGlobalKey: true);

        Assert.False(borrow);
        Assert.Contains("decisions", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://api.deepseek.com")]
    [InlineData("https://api.deepseek.com/v1")]
    [InlineData("https://deepseek.com")]
    [InlineData("https://some.deepseek.com:8443/v1")]
    public void Official_deepseek_hosts_may_borrow(string baseUrl)
    {
        var (borrow, reason) = JudgeKeyPolicy.Decide(
            Endpoint("judge", baseUrl, LlmApiKind.Responses), null, hasGlobalKey: true);

        Assert.True(borrow, reason);
        Assert.Null(reason);
    }

    [Theory]
    // 后缀伪装 / 路径伪装 / userinfo 伪装 / 第三方中转 / 明文 http：以前靠 Contains("deepseek") 全都会放行
    [InlineData("https://api.deepseek.com.evil.com/v1")]
    [InlineData("https://evil.com/api.deepseek.com")]
    [InlineData("https://api.deepseek.com@evil.com/v1")]
    [InlineData("https://deepseek-proxy.example.cn/v1")]
    [InlineData("http://api.deepseek.com")]
    public void Look_alike_hosts_are_rejected(string baseUrl)
    {
        var (borrow, reason) = JudgeKeyPolicy.Decide(
            Endpoint("judge", baseUrl, LlmApiKind.Responses), null, hasGlobalKey: true);

        Assert.False(borrow);
        Assert.NotNull(reason);
    }

    [Fact]
    public void Same_origin_as_chat_endpoint_may_borrow()
    {
        var primary = Endpoint("primary", "https://llm-gateway.internal:8443/v1", LlmApiKind.Responses);
        var judge = Endpoint("judge", "https://llm-gateway.internal:8443/v1", LlmApiKind.Responses);

        var (borrow, reason) = JudgeKeyPolicy.Decide(judge, primary, hasGlobalKey: true);

        Assert.True(borrow, reason);
    }

    [Theory]
    [InlineData("https://llm-gateway.internal:8444/v1")]   // 端口不同
    [InlineData("http://llm-gateway.internal:8443/v1")]    // 协议不同
    [InlineData("https://other.example:8443/v1")]          // 主机不同
    public void Different_origin_from_chat_endpoint_is_rejected(string judgeBaseUrl)
    {
        var primary = Endpoint("primary", "https://llm-gateway.internal:8443/v1", LlmApiKind.Responses);
        var judge = Endpoint("judge", judgeBaseUrl, LlmApiKind.Responses);

        var (borrow, reason) = JudgeKeyPolicy.Decide(judge, primary, hasGlobalKey: true);

        Assert.False(borrow);
        Assert.NotNull(reason);
    }

    [Fact]
    public void No_global_key_means_nothing_to_borrow_and_no_noise()
    {
        var (borrow, reason) = JudgeKeyPolicy.Decide(
            Endpoint("judge", "https://api.deepseek.com", LlmApiKind.Responses), null, hasGlobalKey: false);

        Assert.False(borrow);
        // 没得借不需要解释：日志里不该刷「拒绝把全局 key 发过去」这种吓人的话
        Assert.Null(reason);
    }

    [Fact]
    public void Reject_reason_never_echoes_userinfo()
    {
        // SetEndpointAsync 只校验「是绝对 URI」，带账号密码的地址能入库；
        // 拒绝时把 baseUrl 原样打进日志 = 把密码写进文件
        var judge = Endpoint("judge", "https://user:secret@evil.com/v1", LlmApiKind.Responses);

        var (borrow, reason) = JudgeKeyPolicy.Decide(judge, null, hasGlobalKey: true);

        Assert.False(borrow);
        Assert.NotNull(reason);
        Assert.DoesNotContain("secret", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("user:", reason, StringComparison.Ordinal);
        Assert.Contains("evil.com", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Trailing_dot_host_is_conservatively_rejected()
    {
        // 拒绝是保守的合法选择；这条测试的作用是**把行为钉死** ——
        // 别让后来的人为了「支持 deepseek.com.」把它改回字符串包含
        var judge = Endpoint("judge", "https://deepseek.com./v1", LlmApiKind.Responses);

        var (borrow, _) = JudgeKeyPolicy.Decide(judge, null, hasGlobalKey: true);

        Assert.False(borrow);
    }

    private static ModelEndpointSetting Endpoint(string name, string baseUrl, LlmApiKind kind) =>
        new(name, baseUrl, "model", (int)kind, null, DateTimeOffset.UnixEpoch);
}
