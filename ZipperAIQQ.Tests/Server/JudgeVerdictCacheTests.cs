using ZipperAIQQ.Server.Reply;

namespace ZipperAIQQ.Tests.Server;

/// <summary>K-3 前置：JEV 判定复用（同一消息只判一次）。</summary>
public sealed class JudgeVerdictCacheTests
{
    [Fact]
    public void Same_message_returns_cached_verdict()
    {
        var cache = new JudgeVerdictCache();
        var key = JudgeVerdictCache.KeyOf("qq:g1", "msg-1");

        cache.Set(key, true);

        Assert.True(cache.TryGet(key, out var verdict, out var steer));
        Assert.True(verdict);
        Assert.False(steer);
    }

    [Fact]
    public void Missing_message_id_is_not_cached()
    {
        var cache = new JudgeVerdictCache();
        var key = JudgeVerdictCache.KeyOf("qq:g1", null);

        Assert.Null(key);
        cache.Set(key, true);
        Assert.False(cache.TryGet(key, out _, out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Expired_entry_is_dropped()
    {
        var cache = new JudgeVerdictCache(TimeSpan.FromMilliseconds(30));
        var key = JudgeVerdictCache.KeyOf("qq:g1", "msg-2");

        cache.Set(key, false);
        Thread.Sleep(80);

        Assert.False(cache.TryGet(key, out _, out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Sessions_do_not_collide()
    {
        var cache = new JudgeVerdictCache();

        cache.Set(JudgeVerdictCache.KeyOf("qq:g1", "same"), true);
        cache.Set(JudgeVerdictCache.KeyOf("qq:g2", "same"), false);

        Assert.True(cache.TryGet(JudgeVerdictCache.KeyOf("qq:g1", "same"), out var a, out _) && a);
        Assert.True(cache.TryGet(JudgeVerdictCache.KeyOf("qq:g2", "same"), out var b, out _) && !b);
    }
}
