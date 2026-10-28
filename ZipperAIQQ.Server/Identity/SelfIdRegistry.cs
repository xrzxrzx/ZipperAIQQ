using System.Collections.Concurrent;

namespace ZipperAIQQ.Server.Identity;

/// <summary>机器人自身账号的**进程内**按平台注册表。</summary>
/// <remarks>
/// 库里的 <c>bot.selfId:{platform}</c> 在启动时灌进这里，
/// 路由 / 通知 / 诊断优先用「通道运行时 → 配置 → 本注册表」的顺序取值。
/// 没有它的话，重启后到平台握手前，@ 判断会一直走保守分支。
/// </remarks>
public interface ISelfIdRegistry
{
    string? Get(string platform);

    void Set(string platform, string? selfId);
}

/// <summary>默认实现：大小写不敏感，空值 = 移除。</summary>
public sealed class SelfIdRegistry : ISelfIdRegistry
{
    private readonly ConcurrentDictionary<string, string> _ids = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return null;
        }

        return _ids.TryGetValue(platform.Trim(), out var selfId) ? selfId : null;
    }

    public void Set(string platform, string? selfId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        var key = platform.Trim();
        if (string.IsNullOrWhiteSpace(selfId))
        {
            _ids.TryRemove(key, out _);
            return;
        }

        _ids[key] = selfId.Trim();
    }
}
