using Serilog;
using ZipperAIQQ.Server.Ai;
using ZipperAIQQ.Server.Config;

namespace ZipperAIQQ.Tests.Ai;

/// <summary>
/// 「全局 key 优先、legacy 端点密文回落」的唯一实现（<see cref="KeyFallback"/>）。
/// </summary>
/// <remarks>
/// 这条顺序被独立审查抓过一次回归（BalanceMonitor 只解端点密文），
/// 所以它必须有自己的锁：换 key 后旧密文不能再被当成生效 key。
/// </remarks>
public sealed class KeyFallbackTests
{
    private static readonly ILogger Logger = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task Global_key_wins_over_the_legacy_cipher()
    {
        var cipherRead = false;

        var key = await KeyFallback.ResolveAsync(
            new FakeKeys("sk-global"),
            new FakeProtector(),
            _ =>
            {
                cipherRead = true;
                return Task.FromResult<string?>("enc:sk-legacy");
            });

        Assert.Equal("sk-global", key);
        // 全局 key 在时**不该碰端点表**：搜索是热路径，多一次库读没必要（读失败还会连带拿不到 key）
        Assert.False(cipherRead);
    }

    [Fact]
    public async Task Legacy_cipher_is_used_when_no_global_key_exists()
    {
        var key = await KeyFallback.ResolveAsync(
            new FakeKeys(null), new FakeProtector(), _ => Task.FromResult<string?>("enc:sk-legacy"));

        Assert.Equal("sk-legacy", key);
    }

    [Fact]
    public async Task Nothing_anywhere_returns_null()
    {
        Assert.Null(await KeyFallback.ResolveAsync(
            new FakeKeys(null), new FakeProtector(), _ => Task.FromResult<string?>(null)));

        Assert.Null(await KeyFallback.ResolveAsync(
            new FakeKeys(null), new FakeProtector(), _ => Task.FromResult<string?>("GARBAGE")));
    }

    private sealed class FakeKeys(string? key) : IDeepSeekKeyProvider
    {
        public bool HasKey => !string.IsNullOrEmpty(key);

        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> GetAsync(CancellationToken ct = default) => Task.FromResult(key);

        public DeepSeekKeyStatus GetStatus() =>
            new(HasKey, HasKey ? 1 : 0, HasKey ? "sk-****" : string.Empty);

        public Task<bool> SetKeyAsync(string plainKey, string actor, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task ReloadAsync(CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class FakeProtector : ISecretProtector
    {
        private const string Prefix = "enc:";

        public string Protect(string plaintext) => Prefix + plaintext;

        public string? Unprotect(string? cipher) =>
            cipher is not null && cipher.StartsWith(Prefix, StringComparison.Ordinal)
                ? cipher[Prefix.Length..]
                : null;
    }
}
