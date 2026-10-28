using ZipperAIQQ.Mail;
using ZipperAIQQ.Server.Config;

namespace ZipperAIQQ.Server.Mail;

/// <summary>
/// 把 Server 的 <see cref="ISecretProtector"/> 适配成 Mail 侧的 <see cref="ICredentialProtector"/>。
/// </summary>
/// <remarks>
/// <para>
/// 存在的唯一理由（邮件接入设计 §3.6）：<c>ICredentialProtector</c> 定义在
/// <c>ZipperAIQQ.Mail</c>，而真正的加解密实现（Data Protection）在 <c>ZipperAIQQ.Server</c>。
/// 让 Mail 反向依赖 Server 会形成项目环，所以由 Server 出一层薄适配 ——
/// <b>适配器不持有任何状态，也不碰明文</b>：明文只在 <see cref="Protect"/> 的调用瞬间经过一次。
/// </para>
/// <para>
/// ⚠️ 明文授权码**永不落库、永不回显、永不进日志**（验收标准 6）。
/// 这个类刻意不实现 <c>ToString</c> / 不暴露内部 protector 之外的任何东西。
/// </para>
/// </remarks>
public sealed class MailSecretAdapter : ICredentialProtector
{
    private readonly ISecretProtector _protector;

    public MailSecretAdapter(ISecretProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        _protector = protector;
    }

    /// <inheritdoc />
    public string Protect(string plaintext) => _protector.Protect(plaintext);

    /// <inheritdoc />
    public string? Unprotect(string? cipher) => _protector.Unprotect(cipher);
}
