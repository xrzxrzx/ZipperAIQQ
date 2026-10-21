using Microsoft.AspNetCore.DataProtection;

namespace ZipperAIQQ.Server.Config;

/// <summary>
/// 敏感值的加解密（FR-8.8：ASP.NET Core Data Protection，跨平台）。
/// </summary>
/// <remarks>
/// 密钥环（主密钥）**只走引导层**（环境变量 / 本地目录），绝不入库；
/// 它一丢，所有已存的 key 都解不开 —— 启动时会打印路径提醒备份。
/// </remarks>
public interface ISecretProtector
{
    string Protect(string plaintext);

    /// <summary>解密；密文损坏或密钥环变了返回 <c>null</c>（**不抛**，由调用方决定怎么降级）。</summary>
    string? Unprotect(string? cipher);
}

/// <summary>Data Protection 实现。</summary>
public sealed class DataProtectionSecretProtector : ISecretProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _protector = provider.CreateProtector("ZipperAIQQ.Secrets.v1");
    }

    public string Protect(string plaintext)
    {
        ArgumentException.ThrowIfNullOrEmpty(plaintext);
        return _protector.Protect(plaintext);
    }

    public string? Unprotect(string? cipher)
    {
        if (string.IsNullOrEmpty(cipher))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(cipher);
        }
        catch
        {
            // 密钥环被换 / 密文被截断：解不开是事实，让它返回 null
            return null;
        }
    }
}
