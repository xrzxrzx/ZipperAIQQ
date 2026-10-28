using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Channel.Filtering;

/// <summary>
/// 名单模式。
/// </summary>
/// <remarks>
/// ⚠️ 新模型是**黑白两份名单并存**（问题记录 #20），这个枚举只用于
/// <c>access.filterMode</c> 旧配置的惰性迁移与旧客户端兼容，**不再参与运行时判定**。
/// </remarks>
public enum ChatListMode
{
    /// <summary>白名单：名单内的聊天才会被接收（旧的单名单语义）。</summary>
    Whitelist,

    /// <summary>黑名单：名单内的聊天不被接收（旧的单名单语义）。</summary>
    Blacklist,
}

/// <summary>接收过滤配置（FR-17）。</summary>
/// <remarks>
/// <para>
/// 黑白两份名单**可以同时存在**，但同一条目不能同时出现在两边（互斥）。
/// 判定顺序见 <see cref="ChatFilter.IsAllowed"/>：主人放行 → 黑名单拒绝 → 白名单要求。
/// </para>
/// <para>
/// 空名单语义：**两份都空 = 全放行**（与旧的「黑名单 + 空」等价）。
/// 白名单非空时是 deny-by-default —— 这是刻意的（FR-17.1）。
/// </para>
/// </remarks>
public sealed record ChatFilterOptions
{
    /// <summary>白名单条目（<see cref="ChatRef.SessionId"/>，如 <c>qq:g123</c>）。</summary>
    public IReadOnlyList<string> Whitelist { get; init; } = [];

    /// <summary>黑名单条目。与白名单**互斥**：同一条目不能两边都有。</summary>
    public IReadOnlyList<string> Blacklist { get; init; } = [];

    /// <summary>
    /// 无条件放行的会话（主人私聊 + 主人所在的群）。
    /// </summary>
    /// <remarks>
    /// 由 Server 的 <c>OwnerScopeResolver</c> 计算并写入（问题记录 #22）：
    /// 主人不该因为「忘了把自己加进白名单」而收不到消息。
    /// </remarks>
    public IReadOnlyList<string> AlwaysAllow { get; init; } = [];
}

/// <summary>
/// 决定一条聊天是否被接收。
/// </summary>
/// <remarks>
/// <para>
/// 过滤发生在**通道层**（FR-17.3）：未通过的聊天，消息**不落库、不进模型**。
/// 这比「先存后滤」更省存储，也意味着非白名单内容根本不存在于本地。
/// </para>
/// <para>
/// ⚠️ S3-C 起名单**按平台**存（决策四）：QQ 的白名单不该决定抖音收不收消息。
/// 查询与更新一律带 <c>platform</c> 参数；<see cref="IsAllowed"/> 从
/// <see cref="ChatRef.Platform"/> 自己取，所以通道层调用点不用改。
/// </para>
/// </remarks>
public interface IChatFilter
{
    /// <summary>该平台的白名单条目数（用于启动日志与 UI 展示）。</summary>
    int WhitelistCount(string platform);

    /// <summary>该平台的黑名单条目数。</summary>
    int BlacklistCount(string platform);

    /// <summary>该平台当前白名单（只改黑名单时用于保留原值）。</summary>
    IReadOnlyCollection<string> Whitelist(string platform);

    /// <summary>该平台当前黑名单。</summary>
    IReadOnlyCollection<string> Blacklist(string platform);

    /// <summary>该平台无条件放行的会话（主人范围）。</summary>
    IReadOnlyCollection<string> AlwaysAllow(string platform);

    /// <summary>
    /// 运行时更新某平台的名单（客户端改配置后**立即生效**，不必重启）。
    /// </summary>
    void Update(string platform, ChatFilterOptions options);

    /// <summary>只更新某平台「无条件放行」集合（主人变更 / 群成员变化时用）。</summary>
    void UpdateAlwaysAllow(string platform, IEnumerable<string> entries);

    /// <summary>该聊天是否允许通过（平台取自 <see cref="ChatRef.Platform"/>）。</summary>
    bool IsAllowed(ChatRef chat);
}

/// <summary>
/// <see cref="IChatFilter"/> 的默认实现：精确匹配 <see cref="ChatRef.SessionId"/>，**按平台分桶**。
/// </summary>
/// <remarks>
/// <para>判定顺序（问题记录 #20 / #22 定稿）：</para>
/// <list type="number">
///   <item>在 <c>AlwaysAllow</c> 里 → **放行**</item>
///   <item>在黑名单里 → **拒绝**（黑名单优先于白名单）</item>
///   <item>白名单非空且不在白名单 → **拒绝**</item>
///   <item>其余 → **放行**</item>
/// </list>
/// <para>
/// ⚠️ 不提供「空 = 不过滤」这种模式 —— 那会让「我配了名单」和「我没配」表现一样，
/// 而用户永远分不清自己到底配没配。两份都空是唯一放行全体的情形。
/// </para>
/// <para>
/// **未配置的平台**一律按「两份都空 = 全放行」处理：新平台刚接入时不该被另一个平台的名单挡住。
/// </para>
/// </remarks>
public sealed class ChatFilter : IChatFilter
{
    /// <summary>单平台时代的默认平台（旧构造函数把种子喂给它）。</summary>
    public const string LegacyPlatform = "qq";

    private readonly object _gate = new();
    private readonly Dictionary<string, Bucket> _buckets = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 空过滤器：所有平台都全放行。
    /// </summary>
    public ChatFilter()
    {
    }

    /// <summary>
    /// 用一份配置给指定平台做初始种子。
    /// </summary>
    /// <param name="options">初始名单。</param>
    /// <param name="platform">平台；缺省 <see cref="LegacyPlatform"/>（兼容单平台时代的调用与测试）。</param>
    public ChatFilter(ChatFilterOptions options, string platform = LegacyPlatform)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        Validate(options);
        _buckets[platform] = Bucket.From(options);
    }

    public int WhitelistCount(string platform) => For(platform).Whitelist.Count;

    public int BlacklistCount(string platform) => For(platform).Blacklist.Count;

    public IReadOnlyCollection<string> Whitelist(string platform) => For(platform).Whitelist.ToList();

    public IReadOnlyCollection<string> Blacklist(string platform) => For(platform).Blacklist.ToList();

    public IReadOnlyCollection<string> AlwaysAllow(string platform) => For(platform).AlwaysAllow.ToList();

    public bool IsAllowed(ChatRef chat)
    {
        ArgumentNullException.ThrowIfNull(chat);

        var id = chat.SessionId;
        var bucket = For(chat.Platform);

        if (Matches(bucket.AlwaysAllow, id))
        {
            return true;
        }

        if (Matches(bucket.Blacklist, id))
        {
            return false;
        }

        // 白名单非空 = 只收名单内；白名单为空 = 不限定（黑名单仍生效）
        return bucket.Whitelist.Count == 0 || Matches(bucket.Whitelist, id);
    }

    /// <summary>
    /// 名单条目是否命中会话：**整串相等**，或以 <c>*</c> 结尾时按前缀匹配。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 通配是 2026-10-28 补的：抖音原先有一套自己的 <c>allowedSessions</c>（支持 <c>douyin:p*</c>），
    /// 合并进本类后若只做整串比较，那批 <c>...p*</c> 的条目会**永远不命中** ——
    /// 而白名单非空是 deny-by-default，结果就是"抖音一条消息都收不到"，且没有任何报错。
    /// </para>
    /// <para>
    /// 只支持**后缀** <c>*</c>（前缀匹配），不做正则：名单是给人填的，
    /// 通配太自由反而容易写出"以为收窄了其实放开了"的规则。
    /// </para>
    /// </remarks>
    private static bool Matches(IReadOnlyCollection<string> entries, string sessionId) =>
        entries.Any(entry => IsMatch(entry, sessionId));

    /// <summary>
    /// 单条名单是否命中会话：整串相等，或后缀 <c>*</c> 的前缀匹配（大小写不敏感）。
    /// </summary>
    /// <remarks>
    /// 外部 WS 白名单也用这一套（以前手写了一遍，边界语义容易漂移）。
    /// </remarks>
    public static bool IsMatch(string? entry, string sessionId)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return false;
        }

        var pattern = entry.Trim();
        if (pattern.EndsWith('*'))
        {
            return sessionId.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(pattern, sessionId, StringComparison.OrdinalIgnoreCase);
    }

    public void Update(string platform, ChatFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        Validate(options);

        var bucket = Bucket.From(options);
        lock (_gate)
        {
            _buckets[platform.Trim()] = bucket;
        }
    }

    public void UpdateAlwaysAllow(string platform, IEnumerable<string> entries)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);
        ArgumentNullException.ThrowIfNull(entries);

        var alwaysAllow = ToSet(entries);
        lock (_gate)
        {
            var key = platform.Trim();
            var current = _buckets.TryGetValue(key, out var existing) ? existing : Bucket.Empty;
            _buckets[key] = current with { AlwaysAllow = alwaysAllow };
        }
    }

    /// <summary>
    /// 互斥校验（#20）：同一条目不能同时出现在白名单与黑名单。
    /// </summary>
    /// <remarks>
    /// 这里抛异常而不是静默取一个：规则自相矛盾时，"猜一个"会让用户完全无法预测过滤结果。
    /// 调用方（<c>setAccess</c>）会先做同样的校验并返回 400，这里只是最后一道防线。
    /// </remarks>
    public static void Validate(ChatFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var overlap = options.Whitelist
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Intersect(
                options.Blacklist.Where(e => !string.IsNullOrWhiteSpace(e)),
                StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (overlap.Count > 0)
        {
            throw new InvalidOperationException(
                $"同一条目不能同时在白名单与黑名单：{string.Join(", ", overlap)}");
        }
    }

    /// <summary>取某平台的桶；没配过 → 全空（全放行）。</summary>
    private Bucket For(string? platform)
    {
        if (string.IsNullOrWhiteSpace(platform))
        {
            return Bucket.Empty;
        }

        lock (_gate)
        {
            return _buckets.TryGetValue(platform.Trim(), out var bucket) ? bucket : Bucket.Empty;
        }
    }

    private static HashSet<string> ToSet(IEnumerable<string> entries) =>
        new(entries.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>一个平台的名单快照（不可变：读路径拿到的是引用，不会被并发改到）。</summary>
    private sealed record Bucket(
        HashSet<string> Whitelist,
        HashSet<string> Blacklist,
        HashSet<string> AlwaysAllow)
    {
        public static readonly Bucket Empty = new([], [], []);

        public static Bucket From(ChatFilterOptions options) => new(
            ToSet(options.Whitelist),
            ToSet(options.Blacklist),
            ToSet(options.AlwaysAllow));
    }
}
