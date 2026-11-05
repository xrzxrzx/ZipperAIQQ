namespace ZipperAIQQ.Server.Alerts;

/// <summary>系统级告警类型（与业务通知 <c>NoticeType</c> 分开）。</summary>
public enum AlertKind
{
    /// <summary>通道熔断（连续失败达阈值）。</summary>
    ChannelDown = 0,

    /// <summary>通道恢复。</summary>
    ChannelRecovered = 1,

    /// <summary>模型连续失败（额度 / 服务异常）。</summary>
    LlmFailBurst = 2,

    /// <summary>额度预警（达到 70%）。</summary>
    QuotaWarn = 3,

    /// <summary>额度硬线（停非关键调用）。</summary>
    QuotaHardStop = 4,

    /// <summary>定时任务连续失败。</summary>
    ScheduleFailBurst = 5,

    /// <summary>邮件转发卡住（多轮失败）。</summary>
    MailForwardStuck = 6,
}
