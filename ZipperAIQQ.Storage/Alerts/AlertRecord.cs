namespace ZipperAIQQ.Storage.Alerts;

/// <summary>
/// 一条告警的投递记录（**不是告警本身**，而是「谁在什么时候投到哪、成没成」）。
/// </summary>
/// <remarks>
/// 用途有两个：① 抑制窗口去重（同类型 + 同平台 N 分钟内只报一次）；
/// ② 全通道失败时留档，恢复后可补发（问题记录 04 #7 / D-04）。
/// </remarks>
public sealed record AlertRecord(
    long Id,
    string Kind,
    string? Platform,
    string? Target,
    bool Ok,
    string? Error,
    DateTimeOffset CreatedAt);
