using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>
/// 主窗口的全部交互（M2-D / M2-E）。
/// </summary>
/// <remarks>
/// 只有一个 VM：四个页面的字段都在这里。
/// 页面一多就拆 —— 但四块（状态 / 会话 / 配置 / 审计）共用同一个连接，拆开反而要传一堆参数。
/// </remarks>
public partial class MainViewModel : ObservableObject
{
    private readonly ServerApiClient _api;
    private readonly AppSettings _settings;

    public MainViewModel(ServerApiClient api, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(settings);

        _api = api;
        _settings = settings;

        ServerUrl = settings.ServerUrl;
        Token = settings.Token;
        IsPaneOpen = settings.IsPaneOpen;
        SelectedNavTag = settings.SelectedNavTag;
    }

    /// <summary>侧栏展开状态（改动即落盘，下次启动恢复）。</summary>
    [ObservableProperty]
    public partial bool IsPaneOpen { get; set; } = true;

    /// <summary>当前导航项 Tag（同样落盘）。</summary>
    [ObservableProperty]
    public partial string SelectedNavTag { get; set; } = "status";

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

    // ── 连接 ──
    //
    // ⚠️ 用 partial property 而不是 [ObservableProperty] 字段：
    // WinUI3（CsWinRT）下字段版生成器的代码不是 AOT 兼容的，会报 MVVMTK0045。

    [ObservableProperty]
    public partial string ServerUrl { get; set; } = "http://127.0.0.1:5180";

    [ObservableProperty]
    public partial string Token { get; set; } = string.Empty;

    /// <summary>底部状态条：成功 / 失败原因都落在这里。</summary>
    [ObservableProperty]
    public partial string StatusLine { get; set; } = "填好服务器地址后点「连接」。";

    // ── 状态页 ──

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    // ── 会话页 ──

    [ObservableProperty]
    public partial SessionDto? SelectedSession { get; set; }

    [ObservableProperty]
    public partial string SessionStatus { get; set; } = "点「刷新会话」获取列表。";

    public ObservableCollection<SessionDto> Sessions { get; } = [];

    public ObservableCollection<MessageDto> Messages { get; } = [];

    // ── 配置页 ──

    [ObservableProperty]
    public partial string ConfigText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfigName { get; set; } = "primary";

    [ObservableProperty]
    public partial string ConfigBaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ConfigModel { get; set; } = string.Empty;

    /// <summary>key 输入框：留空 = 不修改（服务端会保留旧密文）。</summary>
    [ObservableProperty]
    public partial string ConfigKey { get; set; } = string.Empty;

    /// <summary>数值型运行参数（发送间隔 / 连发上限 / 冷却 / 文件上限 / 热窗）。</summary>
    public ObservableCollection<SettingItem> Settings { get; } = [];

    // ── 审计页 ──

    public ObservableCollection<AuditDto> Audits { get; } = [];

    // ── 命令 ──

    [RelayCommand]
    private Task ConnectAsync() => GuardAsync("连接", async () =>
    {
        _settings.ServerUrl = ServerUrl.Trim();
        _settings.Token = Token.Trim();
        _settings.Save();

        var data = await _api.CallAsync<PingDto>("ping").ConfigureAwait(true);
        if (data is null)
        {
            throw new InvalidOperationException("ping 没有返回数据。");
        }

        await LoadStatusCoreAsync().ConfigureAwait(true);
        StatusLine = $"已连接（服务端版本 {data.ServerVersion}）";
    });

    [RelayCommand]
    private Task RefreshStatusAsync() => GuardAsync("刷新状态", async () =>
    {
        await LoadStatusCoreAsync().ConfigureAwait(true);
        StatusLine = "状态已刷新";
    });

    [RelayCommand]
    private Task LoadSessionsAsync() => GuardAsync("刷新会话", async () =>
    {
        var list = await _api.CallAsync<List<SessionDto>>("listSessions", new { limit = 100 }).ConfigureAwait(true);
        Sessions.Clear();
        foreach (var item in list ?? [])
        {
            Sessions.Add(item);
        }

        SessionStatus = $"共 {Sessions.Count} 个会话";
        StatusLine = "会话列表已刷新";
    });

    [RelayCommand]
    private Task LoadMessagesAsync() => GuardAsync("加载消息", async () =>
    {
        if (SelectedSession is null)
        {
            throw new InvalidOperationException("先在左边选一个会话。");
        }

        var list = await _api
            .CallAsync<List<MessageDto>>("getMessages", new { sessionId = SelectedSession.SessionId, limit = 100 })
            .ConfigureAwait(true);

        Messages.Clear();
        foreach (var item in list ?? [])
        {
            Messages.Add(item);
        }

        SessionStatus = $"{SelectedSession.SessionId}：{Messages.Count} 条";
        StatusLine = "消息已加载";
    });

    [RelayCommand]
    private Task ExportAsync() => GuardAsync("导出记录", async () =>
    {
        if (Messages.Count == 0)
        {
            throw new InvalidOperationException("先加载消息再导出。");
        }

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZipperAIQQ", "exports");
        System.IO.Directory.CreateDirectory(dir);

        var session = SelectedSession?.SessionId ?? "unknown";
        var safe = new string(session.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        var file = Path.Combine(dir, $"session-{safe}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

        var content = string.Join(Environment.NewLine, Messages.Select(m => m.Display));
        await File.WriteAllTextAsync(file, content, new UTF8Encoding(false)).ConfigureAwait(true);

        StatusLine = $"已导出到：{file}";
    });

    [RelayCommand]
    private Task LoadConfigAsync() => GuardAsync("读取配置", async () =>
    {
        var config = await _api.CallAsync<ConfigDto>("getConfig").ConfigureAwait(true);
        ConfigText = config?.Summary ?? "没有配置。";

        var first = config?.Endpoints.FirstOrDefault();
        if (first is not null)
        {
            ConfigName = first.Name;
            ConfigBaseUrl = first.BaseUrl;
            ConfigModel = first.Model;
        }

        ConfigKey = string.Empty;
        StatusLine = "配置已读取";
    });

    [RelayCommand]
    private Task SaveConfigAsync() => GuardAsync("保存配置", async () =>
    {
        if (string.IsNullOrWhiteSpace(ConfigName))
        {
            throw new InvalidOperationException("端点名不能为空。");
        }

        var endpoint = new Dictionary<string, object?>
        {
            ["name"] = ConfigName.Trim(),
        };

        if (!string.IsNullOrWhiteSpace(ConfigBaseUrl))
        {
            endpoint["baseUrl"] = ConfigBaseUrl.Trim();
        }

        if (!string.IsNullOrWhiteSpace(ConfigModel))
        {
            endpoint["model"] = ConfigModel.Trim();
        }

        // key 留空 = 不修改（服务端会保留旧密文）
        if (!string.IsNullOrWhiteSpace(ConfigKey))
        {
            endpoint["key"] = ConfigKey.Trim();
        }

        var config = await _api.CallAsync<ConfigDto>("setConfig", new { endpoint }).ConfigureAwait(true);
        ConfigText = config?.Summary ?? "已保存。";
        ConfigKey = string.Empty;
        StatusLine = "配置已保存（服务端已热更新）";
    });

    /// <summary>读取数值参数（schema 由服务端给，含范围）。</summary>
    [RelayCommand]
    private Task LoadSettingsAsync() => GuardAsync("读取运行参数", async () =>
    {
        var list = await _api.CallAsync<List<SettingDto>>("getSettings").ConfigureAwait(true);

        Settings.Clear();
        foreach (var item in list ?? [])
        {
            Settings.Add(new SettingItem
            {
                Key = item.Key,
                Label = item.Label,
                Min = item.Min,
                Max = item.Max,
                Unit = item.Unit,
                Stored = item.Stored,
                Value = item.Value,
            });
        }

        StatusLine = $"运行参数 {Settings.Count} 项已读取";
    });

    /// <summary>保存数值参数：客户端先预检，服务端再按 schema 校验。</summary>
    [RelayCommand]
    private Task SaveSettingsAsync() => GuardAsync("保存运行参数", async () =>
    {
        if (Settings.Count == 0)
        {
            throw new InvalidOperationException("先读取一次运行参数。");
        }

        var bad = Settings.FirstOrDefault(s => s.IsOutOfRange);
        if (bad is not null)
        {
            throw new InvalidOperationException(
                $"{bad.Label} 必须在 {bad.Min:F0} ~ {bad.Max:F0} {bad.Unit} 之间（当前 {bad.Value:F0}）");
        }

        var payload = new
        {
            settings = Settings.ToDictionary(s => s.Key, s => s.Value),
        };

        var result = await _api.CallAsync<SetSettingsResultDto>("setSettings", payload).ConfigureAwait(true);
        StatusLine = $"已保存 {result?.Updated ?? 0} 项运行参数（服务端已记录审计）";
    });

    [RelayCommand]
    private Task LoadAuditAsync() => GuardAsync("读取审计", async () =>
    {
        var list = await _api.CallAsync<List<AuditDto>>("getAudit", new { limit = 50 }).ConfigureAwait(true);
        Audits.Clear();
        foreach (var item in list ?? [])
        {
            Audits.Add(item);
        }

        StatusLine = $"审计 {Audits.Count} 条";
    });

    // ── 自动刷新（0.5s）──
    //
    // 为什么要自己轮询：/events 的 WebSocket 只推「有新消息」这类事件，
    // 但状态页的 uptime、审计条数这些**没有事件**，必须定时拉。
    // 0.5s 是用户明确要求的频率；消息用**增量追加**，不整表重载（否则列表会跳）。

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);

    private DispatcherQueueTimer? _timer;
    private bool _tickBusy;
    private string? _lastSessionId;

    /// <summary>启动 0.5s 自动刷新（在 UI 线程调用一次即可）。</summary>
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
        _timer.Interval = RefreshInterval;
        _timer.Tick += async (_, _) => await TickAsync().ConfigureAwait(true);
        _timer.Start();

        StatusLine = "自动刷新已启动（0.5 秒）";
    }

    public void StopAutoRefresh()
    {
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>一次刷新心跳：状态必刷，消息与审计按"有没有在看"决定。</summary>
    private async Task TickAsync()
    {
        // 上一拍还没跑完就跳过 —— 0.5s 太密，慢请求不能堆积
        if (_tickBusy)
        {
            return;
        }

        _tickBusy = true;
        try
        {
            if (string.IsNullOrEmpty(StatusText))
            {
                return;   // 还没连接，别刷
            }

            await LoadStatusCoreAsync().ConfigureAwait(true);
            await RefreshMessagesIncrementalAsync().ConfigureAwait(true);
            await RefreshAuditsIncrementalAsync().ConfigureAwait(true);
        }
        catch
        {
            // 心跳失败不打断用户（连接断了会在状态条上体现，不必每 0.5s 吼一次）
        }
        finally
        {
            _tickBusy = false;
        }
    }

    /// <summary>只把新消息追加到末尾（按 Id 去重），不整表重载。</summary>
    private async Task RefreshMessagesIncrementalAsync()
    {
        if (SelectedSession is null)
        {
            return;
        }

        // 切了会话 → 清空重来
        if (_lastSessionId != SelectedSession.SessionId)
        {
            _lastSessionId = SelectedSession.SessionId;
            Messages.Clear();
        }

        var list = await _api
            .CallAsync<List<MessageDto>>("getMessages", new { sessionId = SelectedSession.SessionId, limit = 50 })
            .ConfigureAwait(true);

        if (list is null || list.Count == 0)
        {
            return;
        }

        var known = Messages.Select(m => m.Id).ToHashSet();
        var fresh = list.Where(m => !known.Contains(m.Id)).OrderBy(m => m.Id).ToList();

        foreach (var item in fresh)
        {
            Messages.Add(item);
        }

        if (fresh.Count > 0)
        {
            SessionStatus = $"{SelectedSession.SessionId}：{Messages.Count} 条（+{fresh.Count}）";
        }
    }

    /// <summary>审计条数变化才刷新（避免每 0.5s 重建整个列表）。</summary>
    private async Task RefreshAuditsIncrementalAsync()
    {
        var list = await _api.CallAsync<List<AuditDto>>("getAudit", new { limit = 50 }).ConfigureAwait(true);
        if (list is null || list.Count == Audits.Count)
        {
            return;
        }

        Audits.Clear();
        foreach (var item in list)
        {
            Audits.Add(item);
        }
    }

    // ── 内部 ──

    private async Task LoadStatusCoreAsync()
    {
        var status = await _api.CallAsync<StatusDto>("getStatus").ConfigureAwait(true);
        if (status is null)
        {
            throw new InvalidOperationException("getStatus 没有返回数据。");
        }

        var uptime = TimeSpan.FromSeconds(status.UptimeSeconds);
        StatusText =
            $"模型：{status.Model}\n" +
            $"通道：{(status.ChannelConnected ? "已连接" : "未连接")}\n" +
            $"运行时长：{(int)uptime.TotalHours} 小时 {uptime.Minutes} 分\n" +
            $"会话数：{status.Sessions}\n" +
            $"待执行定时任务：{status.PendingTasks}\n" +
            $"监听地址：{status.HttpAddress}";
    }

    /// <summary>统一包一层：任何失败都变成状态条上的一行中文原因。</summary>
    private async Task GuardAsync(string action, Func<Task> work)
    {
        try
        {
            StatusLine = $"{action}中…";
            await work().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            StatusLine = $"{action}失败：{ex.Message}";
        }
    }
}
