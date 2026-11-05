using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>
/// 数据页的视图模型：Token 用量（含缓存命中）/ 检索命中率 / JEV 判断 / 会话维度。
/// </summary>
/// <remarks>
/// 从 原 MainViewModel 抽出来（原本 819 行，这块占约 100 行且**只被 DataPage 使用**）。
/// 2026-10-23 起支持按会话筛选（问题记录 #5）与缓存命中率（#4）。
/// </remarks>
public sealed partial class StatsViewModel : ObservableObject
{
    /// <summary>「不筛选」的显示名。选它时请求不带 sessionId。</summary>
    public const string AllSessionsLabel = "全部会话";

    private readonly ServerApiClient _api;

    /// <summary>正在同步下拉选项时，不要把 SelectedSession 的变化当成用户操作。</summary>
    private bool _applying;

    public StatsViewModel(ServerApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    public StatsDto? Stats { get; private set; }

    /// <summary>最近告警（D-6：数据页直接能看「出过什么事」）。</summary>
    public ObservableCollection<AlertItemDto> Alerts { get; } = [];

    [ObservableProperty]
    public partial string AlertSummaryText { get; set; } = "暂无告警";

    /// <summary>最近定时任务执行（G-6）。</summary>
    public ObservableCollection<ScheduleExecutionDto> Executions { get; } = [];

    [ObservableProperty]
    public partial string ExecutionSummaryText { get; set; } = "暂无执行记录";

    // ── 额度看板（E-8）──

    [ObservableProperty]
    public partial bool QuotaEnabled { get; set; }

    [ObservableProperty]
    public partial string QuotaWindow { get; set; } = "day";

    [ObservableProperty]
    public partial string QuotaLimitText { get; set; } = "5";

    [ObservableProperty]
    public partial string QuotaWarnText { get; set; } = "0.7";

    [ObservableProperty]
    public partial string QuotaSpentText { get; set; } = "—";

    [ObservableProperty]
    public partial string QuotaRemainingText { get; set; } = "—";

    [ObservableProperty]
    public partial double QuotaProgressPercent { get; set; }

    [ObservableProperty]
    public partial string QuotaStatusLine { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusLine { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string TokenInText { get; set; } = "0";

    [ObservableProperty]
    public partial string TokenOutText { get; set; } = "0";

    [ObservableProperty]
    public partial string TokenTotalText { get; set; } = "0";

    [ObservableProperty]
    public partial string TokenDayText { get; set; } = "—";

    /// <summary>命中缓存的输入 token（全量）。</summary>
    [ObservableProperty]
    public partial string CacheHitText { get; set; } = "0";

    /// <summary>缓存命中率（全量）；没有输入时显示 —。</summary>
    [ObservableProperty]
    public partial string CacheRateText { get; set; } = "—";

    /// <summary>近 24h 的缓存明细。</summary>
    [ObservableProperty]
    public partial string CacheDetailText { get; set; } = "—";

    [ObservableProperty]
    public partial double HitRatePercent { get; set; }

    [ObservableProperty]
    public partial string HitRateText { get; set; } = "0%";

    [ObservableProperty]
    public partial string RetrievalDetailText { get; set; } = "—";

    [ObservableProperty]
    public partial double JevReplyPercent { get; set; }

    [ObservableProperty]
    public partial string JevReplyText { get; set; } = "0%";

    [ObservableProperty]
    public partial string JevDetailText { get; set; } = "—";

    [ObservableProperty]
    public partial string SummaryText { get; set; } = "0";

    [ObservableProperty]
    public partial string LlmCallsText { get; set; } = "0";

    /// <summary>会话筛选下拉的选项（第一项固定是「全部会话」）。</summary>
    public ObservableCollection<string> SessionOptions { get; } = [AllSessionsLabel];

    [ObservableProperty]
    public partial string SelectedSession { get; set; } = AllSessionsLabel;

    public ObservableCollection<KindStatsDto> KindRows { get; } = [];

    /// <summary>「各类调用」为空时的提示（近 24 小时没调用是正常现象，不是坏了）。</summary>
    [ObservableProperty]
    public partial string KindEmptyText { get; set; } = string.Empty;

    /// <summary>是否已经拉过一次（心跳靠它判断要不要后台刷新）。</summary>
    public bool Loaded => Stats is not null;

    /// <summary>用户切换会话 → 重新拉一次（选项同步期间不触发）。</summary>
    partial void OnSelectedSessionChanged(string value)
    {
        if (_applying)
        {
            return;
        }

        _ = LoadAsync();
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            StatusLine = "读取统计中…";
            var stats = await _api
                .CallAsync<StatsDto>("getStats", BuildPayload())
                .ConfigureAwait(true);

            if (stats is null)
            {
                throw new InvalidOperationException("getStats 没有返回数据。");
            }

            Apply(stats);
            await LoadAlertsAsync().ConfigureAwait(true);
            await LoadQuotaAsync().ConfigureAwait(true);
            await LoadExecutionsAsync().ConfigureAwait(true);
            StatusLine = SelectedSession == AllSessionsLabel
                ? "统计已刷新"
                : $"统计已刷新（{SelectedSession}）";
        }
        catch (Exception ex)
        {
            StatusLine = $"读取统计失败：{ex.Message}";
        }
    }

    /// <summary>心跳用：静默刷新，失败不打扰用户。</summary>
    public async Task RefreshQuietAsync()
    {
        if (Stats is null)
        {
            return;   // 没打开过数据页就不拉，省请求
        }

        try
        {
            var stats = await _api
                .CallAsync<StatsDto>("getStats", BuildPayload())
                .ConfigureAwait(true);

            if (stats is not null)
            {
                Apply(stats);
                await LoadAlertsAsync().ConfigureAwait(true);
                await LoadQuotaAsync().ConfigureAwait(true);
                await LoadExecutionsAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"客户端异常：{ex}");
            // 心跳失败不提示（连接断了由状态条体现）
        }
    }

    /// <summary>拉最近告警；失败不影响统计页其余内容。</summary>
    private async Task LoadAlertsAsync()
    {
        try
        {
            var response = await _api
                .CallAsync<AlertsResponseDto>("getAlerts", new { limit = 10 })
                .ConfigureAwait(true);

            Alerts.Clear();
            foreach (var alert in response?.Alerts ?? [])
            {
                Alerts.Add(alert);
            }

            AlertSummaryText = Alerts.Count == 0 ? "暂无告警" : $"最近 {Alerts.Count} 条";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"读取告警失败：{ex}");
            AlertSummaryText = "告警读取失败";
        }
    }

    /// <summary>拉额度看板（失败只影响这张卡）。</summary>
    private async Task LoadQuotaAsync()
    {
        try
        {
            var quota = await _api.CallAsync<QuotaDto>("getQuota", null).ConfigureAwait(true);
            if (quota is null)
            {
                QuotaStatusLine = "额度读取失败：服务端没有返回数据";
                return;
            }

            QuotaEnabled = quota.Enabled;
            QuotaWindow = quota.Window;
            QuotaLimitText = quota.LimitCny.ToString("0.##");
            QuotaWarnText = quota.WarnRatio.ToString("0.##");

            QuotaSpentText = quota.HasData
                ? $"¥{quota.SpentCny:F2} / ¥{quota.LimitCny:F2}"
                : $"无数据（上限 ¥{quota.LimitCny:F2}）";

            QuotaRemainingText = quota.RemainingCny is { } remaining
                ? $"¥{remaining:F2}"
                : "—";

            QuotaProgressPercent = quota.LimitCny <= 0
                ? 0
                : Math.Clamp((double)(quota.SpentCny / quota.LimitCny) * 100, 0, 100);

            var source = quota.Source switch
            {
                "balance" => "余额差",
                "estimate" => "token 估算",
                _ => "无数据",
            };
            QuotaStatusLine = $"{quota.Window} 窗口 · 口径：{source}";
        }
        catch (Exception ex)
        {
            QuotaStatusLine = $"额度读取失败：{ex.Message}";
        }
    }

    /// <summary>保存额度配置（服务端热更新，不用重启）。</summary>
    [RelayCommand]
    private async Task SaveQuotaAsync()
    {
        try
        {
            if (!decimal.TryParse(QuotaLimitText, out var limit) || limit <= 0)
            {
                QuotaStatusLine = "上限必须是大于 0 的数字";
                return;
            }

            if (!decimal.TryParse(QuotaWarnText, out var warn) || warn <= 0 || warn >= 1)
            {
                QuotaStatusLine = "预警比例必须在 0~1 之间";
                return;
            }

            var saved = await _api.CallAsync<QuotaSavedDto>("setQuota", new
            {
                enabled = QuotaEnabled,
                window = QuotaWindow,
                limitCny = limit,
                warnRatio = warn,
            }).ConfigureAwait(true);

            if (saved is null)
            {
                QuotaStatusLine = "保存失败：服务端没有返回数据";
                return;
            }

            QuotaStatusLine = $"已保存并生效：{saved.Window} 窗口 / ¥{saved.LimitCny:F2} / 预警 {saved.WarnRatio:P0}";
            await LoadQuotaAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            QuotaStatusLine = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>拉最近定时任务执行（失败只影响这张卡）。</summary>
    private async Task LoadExecutionsAsync()
    {
        try
        {
            var response = await _api
                .CallAsync<ScheduleExecutionsResponseDto>("getScheduleExecutions", new { limit = 10 })
                .ConfigureAwait(true);

            Executions.Clear();
            foreach (var item in response?.Executions ?? [])
            {
                Executions.Add(item);
            }

            ExecutionSummaryText = Executions.Count == 0 ? "暂无执行记录" : $"最近 {Executions.Count} 条";
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"读取定时执行记录失败：{ex}");
            ExecutionSummaryText = "执行记录读取失败";
        }
    }

    private object? BuildPayload() =>
        SelectedSession == AllSessionsLabel ? null : new { sessionId = SelectedSession };

    private void Apply(StatsDto stats)
    {
        Stats = stats;

        var day = stats.Last24h;
        var all = stats.AllTime;

        TokenInText = $"{all.Llm.InputTokens:N0}";
        TokenOutText = $"{all.Llm.OutputTokens:N0}";
        TokenTotalText = $"{all.Llm.InputTokens + all.Llm.OutputTokens:N0}";
        TokenDayText = $"近 24h：{day.Llm.InputTokens + day.Llm.OutputTokens:N0} token（{day.Llm.Calls} 次调用）";
        LlmCallsText = $"{all.Llm.Calls:N0}";

        CacheHitText = $"{all.Llm.CachedInputTokens:N0}";
        CacheRateText = all.Llm.InputTokens > 0
            ? $"{all.Llm.CacheHitRate:P1}"
            : "—";
        CacheDetailText = $"近 24h 命中 {day.Llm.CachedInputTokens:N0} token" +
                          (day.Llm.InputTokens > 0 ? $"（{day.Llm.CacheHitRate:P1}）" : "（暂无输入）");

        HitRatePercent = day.Retrieval.HitRate * 100;
        HitRateText = $"{HitRatePercent:F0}%";
        RetrievalDetailText = $"近 24h 检索 {day.Retrieval.Count:N0} 次 · 命中 {day.Retrieval.HitCount:N0} 次 · " +
                              $"平均命中 {day.Retrieval.AvgHitsPerHit:F1} 条 · 平均 {day.Retrieval.AvgMs:F0}ms";

        JevReplyPercent = day.Jev.ReplyRate * 100;
        JevReplyText = $"{JevReplyPercent:F0}%";
        JevDetailText = $"近 24h 判断 {day.Jev.Count:N0} 次 · 回 {day.Jev.Replies:N0} · 不回 {day.Jev.Silences:N0} · " +
                        $"降级 {day.Jev.DegradeCount:N0} · 平均 {day.Jev.AvgMs:F0}ms";

        SummaryText = $"{day.Summary.Count:N0}";

        SyncSessionOptions(stats);

        KindRows.Clear();
        foreach (var row in day.Kinds)
        {
            KindRows.Add(row);
        }

        KindEmptyText = KindRows.Count == 0
            ? "近 24 小时没有调用记录（不是坏了；有对话/检索后这里会出现明细）"
            : string.Empty;
    }

    /// <summary>
    /// 同步会话下拉：只有选项集合真的变了才重建，避免 0.5s 心跳把用户正在点的下拉刷掉。
    /// </summary>
    private void SyncSessionOptions(StatsDto stats)
    {
        var desired = new List<string> { AllSessionsLabel };
        desired.AddRange(stats.Sessions.Select(s => s.SessionId));

        var same = SessionOptions.Count == desired.Count &&
                   SessionOptions.SequenceEqual(desired);

        if (!same)
        {
            _applying = true;
            try
            {
                SessionOptions.Clear();
                foreach (var item in desired)
                {
                    SessionOptions.Add(item);
                }
            }
            finally
            {
                _applying = false;
            }
        }

        // 服务端回显的口径优先（比如刷新期间有人删了会话）
        var wanted = stats.SessionId ?? AllSessionsLabel;
        if (!string.Equals(SelectedSession, wanted, StringComparison.Ordinal) &&
            SessionOptions.Contains(wanted))
        {
            _applying = true;
            try
            {
                SelectedSession = wanted;
            }
            finally
            {
                _applying = false;
            }
        }
    }
}

