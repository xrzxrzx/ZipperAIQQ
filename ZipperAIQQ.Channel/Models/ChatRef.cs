namespace ZipperAIQQ.Channel.Models;

/// <summary>会话类型。</summary>
public enum ChatType
{
    /// <summary>私聊。</summary>
    Private,

    /// <summary>群聊。</summary>
    Group,
}

/// <summary>
/// 平台无关的会话标识。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SessionId"/> 是会话隔离的唯一依据，格式为 <c>{Platform}:{g|p}{Id}</c>，
/// 例如 <c>qq:g123456</c>（群）、<c>qq:p98765</c>（私聊）。
/// </para>
/// <para>
/// ⚠️ **必须带平台前缀**：不同平台的群号完全可能重号，
/// 若沿用旧的 <c>g123456</c> 格式，跨平台接入后两个群会共用同一个上下文。
/// </para>
/// </remarks>
public sealed record ChatRef
{
    /// <summary>平台标识，如 <c>qq</c>。由通道实现提供。</summary>
    public required string Platform { get; init; }

    /// <summary>群聊还是私聊。</summary>
    public required ChatType Type { get; init; }

    /// <summary>平台内的会话 ID（QQ 下是群号 / 对方 QQ 号）。</summary>
    public required string Id { get; init; }

    /// <summary>会话隔离键。见类型注释中的格式说明。</summary>
    public string SessionId =>
        $"{Platform}:{(Type == ChatType.Group ? "g" : "p")}{Id}";
}
