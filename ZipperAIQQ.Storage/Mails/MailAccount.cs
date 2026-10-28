namespace ZipperAIQQ.Storage.Mails;

/// <summary>
/// 一个收信账号（IMAP）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="SecretCipher"/> 是**加密后**的授权码 —— 明文永不落库、永不回显
/// （邮件接入设计 §4.1；对齐 M2-B 的 LLM key 做法）。
/// </para>
/// <para>
/// <see cref="LastUid"/> 是增量同步的水位：只拉 UID 大于它的邮件，避免每次重扫全箱。
/// </para>
/// </remarks>
public sealed record MailAccount
{
    /// <summary>短 ID（如 <c>qq-1</c>），同时用作文件夹 / 日志里的标识。</summary>
    public required string Id { get; init; }

    /// <summary>邮箱地址。</summary>
    public required string Address { get; init; }

    /// <summary>显示名（可空）。</summary>
    public string? DisplayName { get; init; }

    /// <summary>IMAP 主机，如 <c>imap.qq.com</c>。</summary>
    public required string ImapHost { get; init; }

    /// <summary>IMAP 端口，默认 993。</summary>
    public int ImapPort { get; init; } = 993;

    /// <summary>是否走 SSL（993 恒为 true，保留可配以便测试 / 内网代理）。</summary>
    public bool UseSsl { get; init; } = true;

    /// <summary>登录名（通常等于地址）。</summary>
    public required string Username { get; init; }

    /// <summary>加密后的授权码。</summary>
    public required string SecretCipher { get; init; }

    /// <summary>是否启用（禁用后不建连接）。</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>已同步到的最大 IMAP UID；0 表示还没同步过。</summary>
    public long LastUid { get; init; }

    /// <summary>上次成功同步时间（UTC）。</summary>
    public DateTimeOffset? LastSyncAt { get; init; }

    /// <summary>最近一次错误（可读原因，供客户端显示；**不含凭据**）。</summary>
    public string? LastError { get; init; }

    /// <summary>创建时间（UTC）。</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
