namespace ZipperAIQQ.Channel.Models;

/// <summary>
/// 消息发送者信息。
/// </summary>
/// <remarks>
/// <para>
/// 群昵称（<see cref="GroupCard"/>）经常被改，而 QQ 昵称（<see cref="Nickname"/>）相对稳定。
/// 只给模型其中一个，必然出现「认错人」或「换个群昵称就不认识了」。
/// </para>
/// <para>
/// 因此 <see cref="DisplayName"/> **双名并列**（FR-13.2）—— 这是成本最低的准确率提升。
/// </para>
/// </remarks>
public sealed record SenderInfo
{
    /// <summary>发送者账号 ID。</summary>
    public required string UserId { get; init; }

    /// <summary>群昵称（私聊时通常为 null）。</summary>
    public string? GroupCard { get; init; }

    /// <summary>平台昵称（QQ 昵称）。</summary>
    public string? Nickname { get; init; }

    /// <summary>是否与机器人互为好友（影响好友联想能力，FR-13.4）。</summary>
    public bool IsFriend { get; init; }

    /// <summary>呈现给模型的显示名：优先「群昵称（本名）」，缺失则退化。</summary>
    public string DisplayName =>
        GroupCard is null
            ? Nickname ?? UserId
            : Nickname is null
                ? GroupCard
                : $"{GroupCard}（{Nickname}）";
}
