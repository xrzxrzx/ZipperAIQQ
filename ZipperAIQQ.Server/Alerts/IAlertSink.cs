namespace ZipperAIQQ.Server.Alerts;

/// <summary>
/// 告警出口（窄接口）：谁想「报一条事」都走这里，不直接碰发送细节。
/// </summary>
/// <remarks>
/// 存在理由：<see cref="OwnerAlertService"/> 有完整实现（降级链、抑制、留档），
/// 但调用方只需要「报」，不该知道这些；也方便测试里换成假实现。
/// </remarks>
public interface IAlertSink
{
    /// <summary>投递一条告警；返回是否至少一个平台成功。</summary>
    Task<bool> RaiseAsync(AlertMessage alert, CancellationToken ct = default);
}
