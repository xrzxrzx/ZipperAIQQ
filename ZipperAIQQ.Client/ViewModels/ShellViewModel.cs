using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>
/// 外壳 VM：侧栏状态 / 当前页 / 0.5s 心跳分频。
/// </summary>
/// <remarks>
/// 从原 <c>MainViewModel</c> 收尾拆出（#35）：页面各自有自己的 VM，
/// 这里只保留「窗口骨架 + 定时调度」，不再承载任何页面的业务字段。
/// </remarks>
public sealed partial class ShellViewModel : ObservableObject
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(500);

    private readonly AppSettings _settings;
    private DispatcherQueueTimer? _timer;
    private bool _tickBusy;
    private int _tickCount;

    public ShellViewModel(
        AppSettings settings,
        StatusViewModel status,
        StatsViewModel stats,
        SessionsViewModel sessions,
        AuditViewModel audit)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(stats);
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(audit);

        _settings = settings;
        Status = status;
        Stats = stats;
        Sessions = sessions;
        Audit = audit;

        IsPaneOpen = settings.IsPaneOpen;
        SelectedNavTag = settings.SelectedNavTag;
    }

    public StatusViewModel Status { get; }

    public StatsViewModel Stats { get; }

    public SessionsViewModel Sessions { get; }

    public AuditViewModel Audit { get; }

    [ObservableProperty]
    public partial bool IsPaneOpen { get; set; } = true;

    [ObservableProperty]
    public partial string SelectedNavTag { get; set; } = "status";

    /// <summary>当前显示的页面（决定心跳刷什么）。</summary>
    public string CurrentPage { get; set; } = "status";

    partial void OnIsPaneOpenChanged(bool value)
    {
        _settings.IsPaneOpen = value;
        _settings.Save();
    }

    partial void OnSelectedNavTagChanged(string value)
    {
        _settings.SelectedNavTag = value;
        _settings.Save();
    }

    public void StartAutoRefresh()
    {
        if (_timer is not null)
        {
            return;
        }

        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null)
        {
            return;
        }

        _timer = queue.CreateTimer();
        _timer.Interval = Interval;
        _timer.Tick += async (_, _) => await TickAsync().ConfigureAwait(true);
        _timer.Start();
    }

    public void StopAutoRefresh()
    {
        _timer?.Stop();
        _timer = null;
    }

    private async Task TickAsync()
    {
        if (_tickBusy)
        {
            return;
        }

        _tickBusy = true;
        try
        {
            if (string.IsNullOrEmpty(Status.StatusText))
            {
                return;
            }

            _tickCount++;

            if (CurrentPage == "data")
            {
                await Stats.RefreshQuietAsync().ConfigureAwait(true);
            }
            else if (_tickCount % 2 == 0 && Stats.Loaded)
            {
                await Stats.RefreshQuietAsync().ConfigureAwait(true);
            }

            if (CurrentPage == "sessions" && _tickCount % 2 == 0)
            {
                await Sessions.RefreshIncrementalAsync().ConfigureAwait(true);
            }

            if (CurrentPage == "audit" && _tickCount % 4 == 0)
            {
                await Audit.RefreshIncrementalAsync().ConfigureAwait(true);
            }

            if (_tickCount % 2 == 0)
            {
                await Status.RefreshQuietAsync().ConfigureAwait(true);
            }
        }
        catch
        {
            // 心跳失败不打断用户
        }
        finally
        {
            _tickBusy = false;
        }
    }
}
