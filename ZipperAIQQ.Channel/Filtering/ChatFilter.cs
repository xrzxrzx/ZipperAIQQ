using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Channel.Filtering;

/// <summary>名单模式。</summary>
public enum ChatListMode
{
    /// <summary>白名单：**只有**名单内的聊天会被接收。默认。</summary>
    Whitelist,

    /// <summary>黑名单：名单内的聊天**不**被接收，其余都收。</summary>
    Blacklist,
}

/// <summary>接收过滤配置（FR-17）。</summary>
public sealed record ChatFilterOptions
{
    /// <summary>模式，默认白名单。</summary>
    public ChatListMode Mode { get; init; } = ChatListMode.Whitelist;

    /// <summary>
    /// 名单条目，格式为 <see cref="ChatRef.SessionId"/>（如 <c>qq:g123456</c> / <c>qq:p98765</c>）。
    /// </summary>
    /// <remarks>
    /// ⚠️ **默认空**。白名单模式下空名单意味着**不接收任何聊天** —— 这是刻意的（FR-17.1）。
    /// 首次部署必须显式配置，否则机器人完全沉默。
    /// </remarks>
    public IReadOnlyList<string> Entries { get; init; } = [];
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
    /// <summary>当前模式。</summary>
    ChatListMode Mode { get; }

    /// <summary>名单条目数量（用于启动日志与 UI 展示）。</summary>
    int Count { get; }

    /// <summary>该聊天是否允许通过。</summary>
    bool IsAllowed(ChatRef chat);
}

/// <summary>
/// <see cref="IChatFilter"/> 的默认实现：精确匹配 <see cref="ChatRef.SessionId"/>。
/// </summary>
/// <remarks>
/// <para>
/// 空名单的行为（FR-17.1）：
/// </para>
/// <list type="bullet">
///   <item>白名单 + 空 → **全部拒绝**（deny-by-default）</item>
///   <item>黑名单 + 空 → 全部放行（没有要排除的）</item>
/// </list>
/// <para>
/// ⚠️ 不提供「空 = 不过滤」这种模式 —— 那会让「我配了名单」和「我没配」表现一样，
/// 而用户永远分不清自己到底配没配。
/// </para>
/// </remarks>
public sealed class ChatFilter : IChatFilter
{
    private readonly HashSet<string> _entries;

    public ChatFilter(ChatFilterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        Mode = options.Mode;
        _entries = new HashSet<string>(
            options.Entries.Where(e => !string.IsNullOrWhiteSpace(e)),
            StringComparer.OrdinalIgnoreCase);
        Count = _entries.Count;
    }

    public ChatListMode Mode { get; }

    public int Count { get; }

    public bool IsAllowed(ChatRef chat)
    {
        ArgumentNullException.ThrowIfNull(chat);

        var hit = _entries.Contains(chat.SessionId);

        return Mode switch
        {
            ChatListMode.Whitelist => hit,
            ChatListMode.Blacklist => !hit,
            _ => false,
        };
    }
}
