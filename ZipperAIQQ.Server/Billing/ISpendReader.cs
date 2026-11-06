namespace ZipperAIQQ.Server.Billing;

/// <summary>读「当前窗口已花多少」。</summary>
public interface ISpendReader
{
    Task<SpendSnapshot> ReadAsync(CancellationToken ct = default);
}
