namespace ZipperAIQQ.Mail;

/// <summary>
/// 凭据加解密的抽象（邮件接入设计 §3.6）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不在 Mail 里直接用 Server 的 <c>ISecretProtector</c>：
/// 那个接口在 <c>ZipperAIQQ.Server</c>，Mail 反向依赖 Server 会造成项目环。
/// 由 Server 注册一个适配器（<c>MailSecretAdapter</c>）即可。
/// </para>
/// <para>
/// 明文**永不落库、永不回显、永不进日志**（邮件接入设计 §4.1 / 验收标准 6）。
/// </para>
/// </remarks>
public interface ICredentialProtector
{
    /// <summary>加密明文凭据（授权码）。</summary>
    string Protect(string plaintext);

    /// <summary>解密；密文损坏或密钥环变了返回 <c>null</c>（**不抛**，由调用方决定如何降级）。</summary>
    string? Unprotect(string? cipher);
}
