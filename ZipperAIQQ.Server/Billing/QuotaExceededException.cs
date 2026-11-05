namespace ZipperAIQQ.Server.Billing;

/// <summary>额度到硬线且这次调用非关键 —— 被闸门拦下。</summary>
/// <remarks>
/// 继承 <see cref="InvalidOperationException"/>：调用方不需要认识这个类型也能正常冒泡；
/// 但想区分「被额度拦了」时可以直接 catch。
/// </remarks>
public sealed class QuotaExceededException : InvalidOperationException
{
    public QuotaExceededException(string message) : base(message)
    {
    }
}
