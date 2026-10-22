using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>
/// 数据页的视图模型：Token 用量 / 检索命中率 / JEV 判断。
/// </summary>
/// <remarks>
/// 从 <see cref="MainViewModel"/> 抽出来（原本 819 行，这块占约 100 行且**只被 DataPage 使用**）。
/// 抽它收益明显、风险小：只有 DataPage 一个页面要改绑定。
/// </remarks>
public sealed partial class StatsViewModel : ObservableObject
{
    private readonly ServerApiClient _api;

    public StatsViewModel(ServerApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    public StatsDto? Stats { get; private set; }

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

    public ObservableCollection<KindStatsDto> KindRows { get; } = [];

    /// <summary>是否已经拉过一次（心跳靠它判断要不要后台刷新）。</summary>
    public bool Loaded => Stats is not null;

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            StatusLine = "读取统计中…";
            var stats = await _api.CallAsync<StatsDto>("getStats").ConfigureAwait(true);
            if (stats is null)
            {
                throw new InvalidOperationException("getStats 没有返回数据。");
            }

            Apply(stats);
            StatusLine = "统计已刷新";
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
            var stats = await _api.CallAsync<StatsDto>("getStats").ConfigureAwait(true);
            if (stats is not null)
            {
                Apply(stats);
            }
        }
        catch
        {
            // 心跳失败不提示（连接断了由状态条体现）
        }
    }

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

        HitRatePercent = day.Retrieval.HitRate * 100;
        HitRateText = $"{HitRatePercent:F0}%";
        RetrievalDetailText = $"近 24h 检索 {day.Retrieval.Count:N0} 次 · 命中 {day.Retrieval.HitCount:N0} 次 · " +
                              $"平均命中 {day.Retrieval.AvgHitsPerHit:F1} 条 · 平均 {day.Retrieval.AvgMs:F0}ms";

        JevReplyPercent = day.Jev.ReplyRate * 100;
        JevReplyText = $"{JevReplyPercent:F0}%";
        JevDetailText = $"近 24h 判断 {day.Jev.Count:N0} 次 · 回 {day.Jev.Replies:N0} · 不回 {day.Jev.Silences:N0} · " +
                        $"降级 {day.Jev.DegradeCount:N0} · 平均 {day.Jev.AvgMs:F0}ms";

        SummaryText = $"{day.Summary.Count:N0}";

        KindRows.Clear();
        foreach (var row in day.Kinds)
        {
            KindRows.Add(row);
        }
    }
}
