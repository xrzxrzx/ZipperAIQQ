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
/// 过滤发生在**通道层**（FR-17.3）：未通过的聊天，消息**不落库、不进模型**。
/// 这比「先存后滤」更省存储，也意味着非白名单内容根本不存在于本地。
/// </remarks>
public interface IChatFilter
{
    /// <summary>白名单条目数（用于启动日志与 UI 展示）。</summary>
    int WhitelistCount { get; }

    /// <summary>黑名单条目数。</summary>
    int BlacklistCount { get; }

    /// <summary>当前白名单（只改黑名单时用于保留原值）。</summary>
    IReadOnlyCollection<string> Whitelist { get; }

    /// <summary>当前黑名单。</summary>
    IReadOnlyCollection<string> Blacklist { get; }

    /// <summary>无条件放行的会话（主人范围）。</summary>
    IReadOnlyCollection<string> AlwaysAllow { get; }

    /// <summary>
    /// 运行时更新名单（客户端改配置后**立即生效**，不必重启）。
    /// </summary>
    void Update(ChatFilterOptions options);

    /// <summary>只更新「无条件放行」集合（主人变更 / 群成员变化时用）。</summary>
    void UpdateAlwaysAllow(IEnumerable<string> entries);

    /// <summary>该聊天是否允许通过。</summary>
    bool IsAllowed(ChatRef chat);
}

/// <summary>
/// <see cref="IChatFilter"/> 的默认实现：精确匹配 <see cref="ChatRef.SessionId"/>。
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
/// 语义细节：白名单非空时，黑名单只对自己的条目有效（而条目互斥，所以白名单内的聊天
/// 不会被黑名单额外排除）。黑名单真正起作用的主要场景是**白名单为空**（默认全收、只排除几个）。
/// </para>
/// </remarks>
public sealed class ChatFilter : IChatFilter
{
    private readonly object _gate = new();
    private HashSet<string> _whitelist;
    private HashSet<string> _blacklist;
    private HashSet<string> _alwaysAllow;

    public ChatFilter(ChatFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Validate(options);
        _whitelist = ToSet(options.Whitelist);
        _blacklist = ToSet(options.Blacklist);
        _alwaysAllow = ToSet(options.AlwaysAllow);
    }

    public int WhitelistCount
    {
        get
        {
            lock (_gate)
            {
                return _whitelist.Count;
            }
        }
    }

    public int BlacklistCount
    {
        get
        {
            lock (_gate)
            {
                return _blacklist.Count;
            }
        }
    }

    public IReadOnlyCollection<string> Whitelist
    {
        get
        {
            lock (_gate)
            {
                return _whitelist.ToList();
            }
        }
    }

    public IReadOnlyCollection<string> Blacklist
    {
        get
        {
            lock (_gate)
            {
                return _blacklist.ToList();
            }
        }
    }

    public IReadOnlyCollection<string> AlwaysAllow
    {
        get
        {
            lock (_gate)
            {
                return _alwaysAllow.ToList();
            }
        }
    }

    public bool IsAllowed(ChatRef chat)
    {
        ArgumentNullException.ThrowIfNull(chat);

        var id = chat.SessionId;
        lock (_gate)
        {
            if (_alwaysAllow.Contains(id))
            {
                return true;
            }

            if (_blacklist.Contains(id))
            {
                return false;
            }

            // 白名单非空 = 只收名单内；白名单为空 = 不限定（黑名单仍生效）
            return _whitelist.Count == 0 || _whitelist.Contains(id);
        }
    }

    public void Update(ChatFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Validate(options);

        var whitelist = ToSet(options.Whitelist);
        var blacklist = ToSet(options.Blacklist);
        var alwaysAllow = ToSet(options.AlwaysAllow);

        lock (_gate)
        {
            _whitelist = whitelist;
            _blacklist = blacklist;
            _alwaysAllow = alwaysAllow;
        }
    }

    public void UpdateAlwaysAllow(IEnumerable<string> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var next = ToSet(entries);
        lock (_gate)
        {
            _alwaysAllow = next;
        }
    }

    private static HashSet<string> ToSet(IEnumerable<string> entries) =>
        new(entries.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()),
            StringComparer.OrdinalIgnoreCase);

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
}
