namespace ZipperAIQQ.Server.Alerts;

/// <summary>
/// 一条待投递的告警。
/// </summary>
/// <param name="Kind">类型（决定抑制窗口的去重键）。</param>
/// <param name="Platform">相关平台（可空：跨平台/全局告警）。</param>
/// <param name="Text">正文（已脱敏，可直接发给人看）。</param>
/// <param name="At">发生时间（北京时间）。</param>
public sealed record AlertMessage(AlertKind Kind, string? Platform, string Text, DateTimeOffset At);
