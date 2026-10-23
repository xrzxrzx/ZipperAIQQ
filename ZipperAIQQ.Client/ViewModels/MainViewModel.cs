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
    /// <summary>人设下拉里代表「全局人设」的特殊值。</summary>
    public const string GlobalPersonaTag = "【全局】";

    private readonly ServerApiClient _api;
    private readonly AppSettings _settings;

    public MainViewModel(
        ServerApiClient api,
        AppSettings settings,
        StatsViewModel stats,
        SessionsViewModel sessions)
    {
        ArgumentNullException.ThrowIfNull(api);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(sessions);

        _api = api;
        _settings = settings;
        Stats = stats;
        Sessions = sessions;

        // 会话列表刷新后，人设页的会话下拉跟着更新（B4 后由 PersonaViewModel 自己管）
        sessions.SessionsLoaded += RebuildPersonaSessionOptions;

        ServerUrl = settings.ServerUrl;
        Token = settings.Token;
        IsPaneOpen = settings.IsPaneOpen;
        SelectedNavTag = settings.SelectedNavTag;
    }

    /// <summary>会话页的 VM（心跳需要按页分频驱动它）。</summary>
    public SessionsViewModel Sessions { get; }

    /// <summary>侧栏展开状态（改动即落盘，下次启动恢复）。</summary>
    [ObservableProperty]
    public partial bool IsPaneOpen { get; set; } = true;

    /// <summary>当前导航项 Tag（同样落盘）。</summary>
    [ObservableProperty]
    public partial string SelectedNavTag { get; set; } = "status";

    partial void OnJudgeThresholdChanged(double value)
    {
        JudgeThresholdText = value switch
        {
            < 0.45 => $"{value:F2}（很爱插话）",
            < 0.55 => $"{value:F2}（偏主动）",
            < 0.65 => $"{value:F2}（默认，平衡）",
            _ => $"{value:F2}（很少开口）",
        };
    }

    partial void OnPersonaSessionIdChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _ = LoadPersonaCommand.ExecuteAsync(null);
        }
    }

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

    // ── 行为设置（回复模式 + 通知响应开关，第 14 项补齐）──

    [ObservableProperty]
    public partial string ReplyMode { get; set; } = "every";

    public ObservableCollection<string> ReplyModes { get; } = [];

    public ObservableCollection<NoticeToggleItem> NoticeToggles { get; } = [];

    // ── JEV / 判断模型（第 2 项：JEV 无法配置）──

    /// <summary>判断模型用的端点名（空 = 没配，jev 会降级为 mention）。</summary>
    [ObservableProperty]
    public partial string JudgeEndpoint { get; set; } = string.Empty;

    /// <summary>可选端点名（来自 getConfig）。</summary>
    public ObservableCollection<string> EndpointNames { get; } = [];

    // ── JEV 端点（独立于 LLM 端点的第二套配置）──
    // 用户要求：普通 LLM 与 JEV 各自独立配置，不再共用 primary

    [ObservableProperty]
    public partial string JudgeEndpointName { get; set; } = "judge";

    /// <summary>
    /// JEV 端点默认指向 **OpenRouter**（不是 DeepSeek）：
    /// 判断模型通常要便宜、快，且和对话模型分开计费/限流。
    /// </summary>
    [ObservableProperty]
    public partial string JudgeEndpointBaseUrl { get; set; } = "https://openrouter.ai/api/v1";

    /// <summary>OpenRouter 的模型 ID 形如 <c>provider/model</c>，这里留空由用户填。</summary>
    [ObservableProperty]
    public partial string JudgeEndpointModel { get; set; } = string.Empty;

    /// <summary>JEV 端点的 key：留空 = 不修改（与 LLM 端点同样的口径）。</summary>
    [ObservableProperty]
    public partial string JudgeEndpointKey { get; set; } = string.Empty;

    /// <summary>JEV 判断灵敏度（0.3~0.8，越大越少开口）。</summary>
    [ObservableProperty]
    public partial double JudgeThreshold { get; set; } = 0.55;

    [ObservableProperty]
    public partial string JudgeThresholdText { get; set; } = "0.55（默认）";

    [ObservableProperty]
    public partial string JudgeEndpointHint { get; set; } =
        "JEV 端点：默认 OpenRouter（baseUrl=https://openrouter.ai/api/v1，模型填 provider/model）";

    // ── 人设（第 21 项：形象 / 性格分开）──

    [ObservableProperty]
    public partial string PersonaName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PersonaImage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PersonaPersonality { get; set; } = string.Empty;

    /// <summary>人设编辑的目标会话（可在配置页直接选，不必回会话页）。</summary>
    [ObservableProperty]
    public partial string PersonaSessionId { get; set; } = string.Empty;

    /// <summary>可选会话列表（来自 listSessions）。</summary>
    public ObservableCollection<string> PersonaSessionOptions { get; } = [];

    [ObservableProperty]
    public partial string PersonaHint { get; set; } = "选一个会话，编辑它的人设（留空则用全局人设）";

    // ── 人员与名单（管理员 / 主人 / 黑白名单）──

    [ObservableProperty]
    public partial string AdminsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FilterMode { get; set; } = "Blacklist";

    [ObservableProperty]
    public partial string FilterEntriesText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial AccessCandidateDto? SelectedOwner { get; set; }

    [ObservableProperty]
    public partial string AccessHint { get; set; } = "点「读取」拉取当前管理员 / 主人 / 名单";

    public ObservableCollection<AccessCandidateDto> OwnerCandidates { get; } = [];

    // ── 审计页 ──

    public ObservableCollection<AuditDto> Audits { get; } = [];

    // ── 数据页（已抽到 StatsViewModel）──

    /// <summary>数据页的 VM（独立文件：StatsViewModel.cs）。</summary>
    public StatsViewModel Stats { get; }

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

        // 顺手拉一次会话列表：人设卡片要靠它出下拉候选，
        // 不然用户不点「刷新会话」就拿不到 target，保存人设会直接失败（实测）
        try
        {
            await Sessions.LoadSessionsCommand.ExecuteAsync(null).ConfigureAwait(true);
        }
        catch
        {
            // 会话列表拉不到不影响连接
        }

        StatusLine = $"已连接（服务端版本 {data.ServerVersion}）";
    });

    [RelayCommand]
    private Task RefreshStatusAsync() => GuardAsync("刷新状态", async () =>
    {
        await LoadStatusCoreAsync().ConfigureAwait(true);
        StatusLine = "状态已刷新";
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

    /// <summary>读取人设（形象 / 性格分开）。</summary>
    [RelayCommand]
    private Task LoadPersonaAsync() => GuardAsync("读取人设", async () =>
    {
        var target = ResolvePersonaTarget();

        var apiSessionId = string.Equals(target, GlobalPersonaTag, StringComparison.Ordinal) ? "global" : target;

        var dto = await _api
            .CallAsync<PersonaDto>("getPersona", new { sessionId = apiSessionId })
            .ConfigureAwait(true);

        if (dto is null)
        {
            throw new InvalidOperationException("getPersona 没有返回数据。");
        }

        PersonaName = dto.Name;
        PersonaImage = dto.Image ?? string.Empty;
        PersonaPersonality = dto.Personality ?? string.Empty;
        PersonaHint = dto.Split
            ? $"正在编辑：{dto.SessionId}"
            : $"正在编辑：{dto.SessionId}（旧格式人设，没有形象/性格小节，保存后会按新格式重写）";

        StatusLine = "人设已读取";
    });

    /// <summary>保存人设（服务端拼成完整正文）。</summary>
    [RelayCommand]
    private Task SavePersonaAsync() => GuardAsync("保存人设", async () =>
    {
        // 兜底：没选会话时先把列表拉回来（否则只会报"先选一个会话"，用户会误以为是保存失败）
        if (string.IsNullOrWhiteSpace(PersonaSessionId) &&
            Sessions.SelectedSession is null &&
            Sessions.Sessions.Count == 0)
        {
            await Sessions.LoadSessionsCommand.ExecuteAsync(null).ConfigureAwait(true);
        }

        var target = ResolvePersonaTarget();

        if (string.IsNullOrWhiteSpace(PersonaImage) && string.IsNullOrWhiteSpace(PersonaPersonality))
        {
            throw new InvalidOperationException("形象与性格至少要填一个（都空会让人设消失）。");
        }

        if (string.Equals(target, GlobalPersonaTag, StringComparison.Ordinal))
        {
            // 全局人设：写服务端的 config/persona.md
            await _api.CallAsync<object>("setGlobalPersona", new
            {
                name = PersonaName,
                image = PersonaImage,
                personality = PersonaPersonality,
            }).ConfigureAwait(true);

            StatusLine = "全局人设已保存（写入服务端 config/persona.md）";
            return;
        }

        await _api.CallAsync<object>("setPersona", new
        {
            sessionId = target,
            name = PersonaName,
            image = PersonaImage,
            personality = PersonaPersonality,
        }).ConfigureAwait(true);

        StatusLine = $"人设已保存（{target}）";
    });

    /// <summary>读取人员与名单（管理员 / 主人 / 黑白名单）。</summary>
    [RelayCommand]
    private Task LoadAccessAsync() => GuardAsync("读取人员与名单", async () =>
    {
        var dto = await _api.CallAsync<AccessDto>("getAccess").ConfigureAwait(true);
        if (dto is null)
        {
            throw new InvalidOperationException("getAccess 没有返回数据。");
        }

        AdminsText = string.Join(", ", dto.Admins);
        FilterMode = dto.FilterMode;
        FilterEntriesText = string.Join(", ", dto.FilterEntries);

        OwnerCandidates.Clear();
        foreach (var c in dto.Candidates)
        {
            OwnerCandidates.Add(c);
        }

        SelectedOwner = OwnerCandidates.FirstOrDefault(c => c.UserId == dto.OwnerId);
        AccessHint = $"主人：{dto.OwnerId ?? "未设置"} · 名单条目 {dto.FilterCount} 条 · 好友候选 {dto.Candidates.Count} 人";
        StatusLine = "人员与名单已读取";
    });

    /// <summary>保存人员与名单（管理员 / 名单 走 setAccess，主人走 setOwner）。</summary>
    [RelayCommand]
    private Task SaveAccessAsync() => GuardAsync("保存人员与名单", async () =>
    {
        var admins = SplitList(AdminsText);
        var entries = SplitList(FilterEntriesText);

        await _api.CallAsync<object>("setAccess", new
        {
            admins,
            filterMode = FilterMode,
            filterEntries = entries,
        }).ConfigureAwait(true);

        if (SelectedOwner is not null)
        {
            await _api.CallAsync<object>("setOwner", new
            {
                userId = SelectedOwner.UserId,
                nickname = SelectedOwner.Nickname,
            }).ConfigureAwait(true);
        }

        StatusLine = $"已保存：管理员 {admins.Count} 人 · 名单 {FilterMode}/{entries.Count} 条" +
                     (SelectedOwner is null ? "" : $" · 主人 {SelectedOwner.Nickname}");
    });

    /// <summary>逗号/分号/空格分隔的列表。</summary>
    private static List<string> SplitList(string text) =>
        text.Split([',', '，', ';', '；', ' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    [RelayCommand]
    private Task LoadAuditAsync() => GuardAsync("读取审计", async () =>
    {
        var list = await _api.CallAsync<List<AuditDto>>("getAudit", new { limit = 50 }).ConfigureAwait(true);

        // 服务端按 Id 倒序返回（最新在前）；界面要「旧→新」+ 滚到底看最新（#7）
        Audits.Clear();
        foreach (var item in (list ?? []).OrderBy(a => a.At))
        {
            Audits.Add(item);
        }

        StatusLine = $"审计 {Audits.Count} 条";
    });

    [RelayCommand]
    private async Task RestartServerAsync()
    {
        try
        {
            await _api.CallAsync<object>("restartServer").ConfigureAwait(true);
            StatusLine = "已请求重启服务端：约 0.5 秒后停止，由守护脚本重新拉起。";
        }
        catch (Exception ex)
        {
            StatusLine = $"重启失败：{ex.Message}";
        }
    }

    /// <summary>读取行为设置（回复模式 + 通知开关）。</summary>
    [RelayCommand]
    private Task LoadBehaviorAsync() => GuardAsync("读取行为设置", async () =>
    {
        var data = await _api.CallAsync<BehaviorDto>("getBehavior").ConfigureAwait(true);
        if (data is null)
        {
            throw new InvalidOperationException("getBehavior 没有返回数据。");
        }

        ReplyModes.Clear();
        foreach (var mode in data.ReplyModes)
        {
            ReplyModes.Add(mode);
        }

        ReplyMode = data.ReplyMode;

        NoticeToggles.Clear();
        foreach (var item in data.Notices)
        {
            NoticeToggles.Add(new NoticeToggleItem
            {
                Type = item.Type,
                Label = item.Label,
                Enabled = item.Enabled,
            });
        }

        await LoadJudgeConfigAsync().ConfigureAwait(true);
        StatusLine = "行为设置已读取";
    });

    /// <summary>保存行为设置（服务端会校验类型并写审计）。</summary>
    [RelayCommand]
    private Task SaveBehaviorAsync() => GuardAsync("保存行为设置", async () =>
    {
        if (ReplyModes.Count == 0)
        {
            throw new InvalidOperationException("先读取一次行为设置。");
        }

        var payload = new
        {
            replyMode = ReplyMode,
            notice = NoticeToggles.ToDictionary(n => n.Type, n => n.Enabled),
        };

        await _api.CallAsync<object>("setBehavior", payload).ConfigureAwait(true);
        StatusLine = $"行为设置已保存（回复模式 {ReplyMode}，通知 {NoticeToggles.Count(n => n.Enabled)} 类开启）";
    });

    // ── 自动刷新（0.5s）──
    //
    // 为什么要自己轮询：/events 的 WebSocket 只推「有新消息」这类事件，
    // 但状态页的 uptime、审计条数这些**没有事件**，必须定时拉。
    // 0.5s 是用户明确要求的频率；消息用**增量追加**，不整表重载（否则列表会跳）。

    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(500);

    private DispatcherQueueTimer? _timer;
    private bool _tickBusy;
    private int _tickCount;

    /// <summary>当前显示的页面（决定 0.5s 心跳刷什么，避免所有接口一起刷）。</summary>
    public string CurrentPage { get; set; } = "status";

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

    /// <summary>
    /// 一次刷新心跳（500ms 一拍）。
    /// </summary>
    /// <remarks>
    /// **按页面分频**，不是所有接口一起刷（4 个接口 × 2 次/秒 纯属浪费）：
    /// 数据页的统计 0.5s（用户要求的"统计数据实时"）；其余页面的数据 1~2s。
    /// </remarks>
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

            _tickCount++;

            // 统计：数据页 0.5s 一拍；别的页面 1s 一拍（让数字保持新鲜但不抢带宽）
            if (CurrentPage == "data")
            {
                await Stats.RefreshQuietAsync().ConfigureAwait(true);
            }
            else if (_tickCount % 2 == 0 && Stats.Loaded)
            {
                await Stats.RefreshQuietAsync().ConfigureAwait(true);
            }

            // 消息：只有会话页才拉，1s
            if (CurrentPage == "sessions" && _tickCount % 2 == 0)
            {
                await Sessions.RefreshIncrementalAsync().ConfigureAwait(true);
            }

            // 审计：审计页 2s
            if (CurrentPage == "audit" && _tickCount % 4 == 0)
            {
                await RefreshAuditsIncrementalAsync().ConfigureAwait(true);
            }

            // 状态：1s（很轻，任何页面都刷，保证连接状态是最新的）
            if (_tickCount % 2 == 0)
            {
                await LoadStatusCoreAsync().ConfigureAwait(true);
            }
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

    /// <summary>审计条数变化才刷新（避免每 0.5s 重建整个列表）。</summary>
    private async Task RefreshAuditsIncrementalAsync()
    {
        var list = await _api.CallAsync<List<AuditDto>>("getAudit", new { limit = 50 }).ConfigureAwait(true);
        if (list is null || list.Count == Audits.Count)
        {
            return;
        }

        Audits.Clear();
        foreach (var item in list.OrderBy(a => a.At))
        {
            Audits.Add(item);
        }
    }

    // ── 内部 ──

    /// <summary>人设目标会话：优先配置页选的那个，其次会话页选中的。</summary>
    private string ResolvePersonaTarget()
    {
        // 没显式选就用「全局」——这样"改全局人设"不需要先选会话
        if (string.IsNullOrWhiteSpace(PersonaSessionId))
        {
            return GlobalPersonaTag;
        }

        return PersonaSessionId;
    }

    private string ResolvePersonaTargetLegacy()
    {
        if (!string.IsNullOrWhiteSpace(PersonaSessionId))
        {
            return PersonaSessionId;
        }

        if (Sessions.SelectedSession is not null)
        {
            return Sessions.SelectedSession.SessionId;
        }

        throw new InvalidOperationException("还没选要编辑哪个会话：请在人设卡片顶部的下拉里选一个（下拉为空就先点「刷新会话」）。");
    }

    /// <summary>会话列表刷新后重建人设页的会话下拉（值仍是 sessionId，B4 换成显示名）。</summary>
    private void RebuildPersonaSessionOptions()
    {
        var previous = PersonaSessionId;

        PersonaSessionOptions.Clear();
        PersonaSessionOptions.Add(GlobalPersonaTag);   // 第一项永远是「全局」
        foreach (var s in Sessions.Sessions)
        {
            PersonaSessionOptions.Add(s.SessionId);
        }

        if (!string.IsNullOrWhiteSpace(previous))
        {
            PersonaSessionId = previous;
        }
        else if (Sessions.SelectedSession is not null)
        {
            PersonaSessionId = Sessions.SelectedSession.SessionId;
        }
    }

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

    /// <summary>读判断模型的端点配置（getConfig 的 roles.judge + 端点列表）。</summary>
    private async Task LoadJudgeConfigAsync()
    {
        var cfg = await _api.CallAsync<ConfigDto>("getConfig").ConfigureAwait(true);
        if (cfg is null)
        {
            return;
        }

        EndpointNames.Clear();
        foreach (var e in cfg.Endpoints)
        {
            EndpointNames.Add(e.Name);
        }

        // ConfigDto 的 roles 是字典：找 judge（大小写不敏感）
        JudgeEndpoint = cfg.Roles
            .FirstOrDefault(kv => string.Equals(kv.Key, "judge", StringComparison.OrdinalIgnoreCase))
            .Value ?? string.Empty;

        // JEV 端点的独立字段：按 role 绑定的名字找那个端点
        var judgeEp = cfg.Endpoints.FirstOrDefault(e =>
            string.Equals(e.Name, JudgeEndpoint, StringComparison.OrdinalIgnoreCase));

        if (judgeEp is not null)
        {
            JudgeEndpointName = judgeEp.Name;
            JudgeEndpointBaseUrl = judgeEp.BaseUrl;
            JudgeEndpointModel = judgeEp.Model;
            JudgeEndpointHint = $"JEV 端点：{judgeEp.Name}（{judgeEp.Model}，key {judgeEp.KeyMasked}）";
        }
        else
        {
            JudgeEndpointHint = JudgeEndpoint.Length > 0
                ? $"角色 judge → {JudgeEndpoint}（但端点列表里没找到它）"
                : "JEV 端点：未配置（jev 模式会降级为 mention）";
        }
    }

    /// <summary>保存判断模型（setConfig 的 roles.judge）。</summary>
    [RelayCommand]
    private Task SaveJudgeAsync() => GuardAsync("保存判断模型", async () =>
    {
        if (string.IsNullOrWhiteSpace(JudgeEndpoint))
        {
            throw new InvalidOperationException("先选一个端点作为判断模型（或保持为空表示不启用 JEV）。");
        }

        await _api.CallAsync<object>("setConfig", new
        {
            roles = new Dictionary<string, string> { ["judge"] = JudgeEndpoint },
        }).ConfigureAwait(true);

        StatusLine = $"判断模型已设为 {JudgeEndpoint}（JEV 模式可用）";
    });

    /// <summary>
    /// 保存 JEV 端点（独立于 LLM 端点）+ 把 judge 角色绑到它。
    /// </summary>
    /// <remarks>
    /// 用户要求「普通 LLM 与 JEV 各自独立配置」：
    /// 所以这里存的是**另一个端点**（默认名 judge），并把 roles.judge 指过去，
    /// 而不是复用 chat 那个 primary。
    /// </remarks>
    [RelayCommand]
    private Task SaveJudgeEndpointAsync() => GuardAsync("保存 JEV 端点", async () =>
    {
        if (string.IsNullOrWhiteSpace(JudgeEndpointName))
        {
            throw new InvalidOperationException("JEV 端点名不能为空。");
        }

        var endpoint = new Dictionary<string, object?>
        {
            ["name"] = JudgeEndpointName.Trim(),
            ["baseUrl"] = JudgeEndpointBaseUrl.Trim(),
            ["model"] = JudgeEndpointModel.Trim(),
            // OpenRouter 走 Chat Completions 兼容接口（枚举名是 ChatCompletions，不是 Chat）
            ["apiKind"] = "ChatCompletions",
        };

        // key 留空 = 不修改（服务端保留旧密文）
        if (!string.IsNullOrWhiteSpace(JudgeEndpointKey))
        {
            endpoint["key"] = JudgeEndpointKey.Trim();
        }

        var cfg = await _api.CallAsync<ConfigDto>("setConfig", new
        {
            endpoint,
            roles = new Dictionary<string, string> { ["judge"] = JudgeEndpointName.Trim() },
        }).ConfigureAwait(true);

        JudgeEndpointKey = string.Empty;
        JudgeEndpoint = JudgeEndpointName.Trim();

        if (cfg is not null)
        {
            var ep = cfg.Endpoints.FirstOrDefault(e =>
                string.Equals(e.Name, JudgeEndpointName, StringComparison.OrdinalIgnoreCase));
            if (ep is not null)
            {
                JudgeEndpointHint = $"JEV 端点：{ep.Name}（{ep.Model}，key {ep.KeyMasked}）";
            }
        }

        StatusLine = $"JEV 端点已保存：{JudgeEndpointName}（judge 角色已指向它）";
    });

    /// <summary>保存 JEV 判断灵敏度（写运行时设置 jev.threshold）。</summary>
    [RelayCommand]
    private Task SaveJudgeThresholdAsync() => GuardAsync("保存判断灵敏度", async () =>
    {
        await _api.CallAsync<object>("setSettings", new
        {
            settings = new Dictionary<string, double> { ["jev.threshold"] = JudgeThreshold },
        }).ConfigureAwait(true);

        StatusLine = $"判断灵敏度已设为 {JudgeThreshold:F2}";
    });
}
