using ZipperAIQQ.Channel;

namespace ZipperAIQQ.Server.Contacts;

/// <summary>
/// 按平台取联系人目录（决策四第 6 条）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么需要注册表：<c>IContactDirectory</c> 是"这个平台上谁能当主人 / 群有哪些"的数据源，
/// 而每个平台的实现不同（QQ → NapCat；抖音 → sidecar 的 <c>query.contacts</c>）。
/// 上层（配置页、主人放行范围）只认平台名，不认实现。
/// </para>
/// <para>
/// 取不到时返回 <c>null</c> 而不是抛：新平台还没接入时，配置页应当显示"该平台暂无候选人"，
/// 而不是整页 500。
/// </para>
/// </remarks>
public interface IContactDirectoryRegistry
{
    /// <summary>已注册的平台。</summary>
    IReadOnlyList<string> Platforms { get; }

    /// <summary>取某平台的目录；未注册返回 <c>null</c>。</summary>
    IContactDirectory? For(string platform);
}

/// <summary>
/// 简单实现：构造时给一份"平台 → 目录"的只读映射。
/// </summary>
public sealed class ContactDirectoryRegistry : IContactDirectoryRegistry
{
    private readonly Dictionary<string, IContactDirectory> _byPlatform;

    public ContactDirectoryRegistry(IReadOnlyDictionary<string, IContactDirectory> byPlatform)
    {
        ArgumentNullException.ThrowIfNull(byPlatform);

        _byPlatform = new Dictionary<string, IContactDirectory>(StringComparer.OrdinalIgnoreCase);
        foreach (var (platform, directory) in byPlatform)
        {
            if (string.IsNullOrWhiteSpace(platform) || directory is null)
            {
                continue;
            }

            _byPlatform[platform.Trim()] = directory;
        }
    }

    public IReadOnlyList<string> Platforms => [.. _byPlatform.Keys];

    public IContactDirectory? For(string platform) =>
        string.IsNullOrWhiteSpace(platform) || !_byPlatform.TryGetValue(platform.Trim(), out var directory)
            ? null
            : directory;
}
