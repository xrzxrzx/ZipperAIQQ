using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>
/// 「运行与行为」页里的一组通知开关：**一个平台一组**（S3-D / 决策六）。
/// </summary>
/// <remarks>
/// 组内仍按原来的「常用 / 其他」两列摆（服务端每项的 <c>group</c> 字段），
/// 组标题就是平台名——这样"关掉某平台的通知不影响另一个平台"一眼能看出来。
/// </remarks>
public sealed class NoticePlatformGroup
{
    public NoticePlatformGroup(
        string platformCode,
        IEnumerable<NoticeToggleItem> common,
        IEnumerable<NoticeToggleItem> other)
    {
        PlatformCode = platformCode;
        Title = NoticePlatformLabeling.TitleOf(platformCode);
        foreach (var item in common)
        {
            CommonNotices.Add(item);
        }

        foreach (var item in other)
        {
            OtherNotices.Add(item);
        }
    }

    /// <summary>平台码（<c>qq</c> / <c>douyin</c> / 未知平台原样）。</summary>
    public string PlatformCode { get; }

    /// <summary>组标题：「QQ 通知」/「抖音通知」。</summary>
    public string Title { get; }

    public ObservableCollection<NoticeToggleItem> CommonNotices { get; } = [];

    public ObservableCollection<NoticeToggleItem> OtherNotices { get; } = [];
}

/// <summary>平台码 → 界面文案（与「人员与名单」页 S3-C 同口径）。</summary>
internal static class NoticePlatformLabeling
{
    /// <summary>缺省平台：契约里 <c>platform</c> 省略时服务端按 <c>qq</c> 处理。</summary>
    internal const string DefaultPlatform = "qq";

    /// <summary>已知平台的展示顺序（未知平台排在后面，按码排）。</summary>
    private static readonly string[] KnownPlatformOrder = ["qq", "douyin"];

    private static readonly Dictionary<string, string> PlatformLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qq"] = "QQ",
        ["douyin"] = "抖音",
    };

    /// <summary>规范化平台码：空 → <see cref="DefaultPlatform"/>，统一小写（与服务端平台规范化同口径）。</summary>
    internal static string Normalize(string? code) =>
        string.IsNullOrWhiteSpace(code) ? DefaultPlatform : code.Trim().ToLowerInvariant();

    /// <summary>平台码 → 界面文案；未知平台原样显示，将来加平台不用改客户端。</summary>
    internal static string LabelOf(string? code)
    {
        var normalized = Normalize(code);
        return PlatformLabels.TryGetValue(normalized, out var label) ? label : normalized;
    }

    /// <summary>组标题：平台名 + 通知（中文后直接接，拉丁字母后留一个空格）。</summary>
    internal static string TitleOf(string? code)
    {
        var label = LabelOf(code);
        return char.IsAscii(label[^1]) ? $"{label} 通知" : $"{label}通知";
    }

    /// <summary>组的排序：QQ → 抖音 → 未知平台。</summary>
    internal static int OrderOf(string? code)
    {
        var normalized = Normalize(code);
        var index = Array.FindIndex(
            KnownPlatformOrder,
            known => string.Equals(known, normalized, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? KnownPlatformOrder.Length : index;
    }
}

/// <summary>
/// 运行与行为页：数值型运行参数 + 回复模式 / 通知响应。
/// </summary>
/// <remarks>
/// <para>从「端点与参数」里拆出来（用户 2026-10-23 反馈：AI 端点配置应独立一页）。</para>
/// <para>
/// S3-D（D2.4 / 决策六）：通知响应**按平台分组**。<c>getBehavior</c> 一次就把各平台的通知都带回来
/// （每项自带 <c>platform</c>），客户端按它分组显示；保存时用 <c>notices</c> 数组、**单项带 <c>platform</c>**，
/// 所以一次保存可以覆盖多个平台，且不会出现"一半平台写成功、另一半失败"的中间态。
/// </para>
/// </remarks>
public sealed partial class RuntimeViewModel : ObservableObject
{
    /// <summary>JEV 灵敏度在「运行与行为」里单独用滑条调，不混在通用参数列表里。</summary>
    public const string JudgeThresholdKey = "jev.threshold";

    private readonly ServerApiClient _api;
    private readonly List<NoticeToggleItem> _allNotices = [];

    /// <summary>
    /// 通知类型 → 平台码（来自服务端每项的 <c>platform</c>）。
    /// 不挂在 <see cref="NoticeToggleItem"/> 上：类型名是枚举名，**全局唯一**（一个类型只属于一个平台，
    /// 见服务端 <c>NoticeCatalog.PlatformOf</c>），所以一张表就够，行对象保持"只负责开关"。
    /// </summary>
    private readonly Dictionary<string, string> _noticePlatforms = new(StringComparer.Ordinal);

    /// <summary>
    /// 对端是单平台时代的老服务端（不返回 <c>platforms</c>）：它不认 <c>notices</c> 数组，
    /// 保存要退回老 payload（<c>notice</c> 字典），否则会 400 —— 老服务端也不能让人存不了。
    /// </summary>
    private bool _legacyBehaviorServer;

    public RuntimeViewModel(ServerApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    public ObservableCollection<SettingItem> Settings { get; } = [];

    public ObservableCollection<string> ReplyModes { get; } = [];

    /// <summary>通知开关：**每个平台一组**（S3-D）。</summary>
    public ObservableCollection<NoticePlatformGroup> NoticeGroups { get; } = [];

    /// <summary>没有任何通知类型 / 搜索无匹配时的提示（免得空白一片）。</summary>
    [ObservableProperty]
    public partial string NoticeEmptyHint { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ReplyMode { get; set; } = "every";

    [ObservableProperty]
    public partial string NoticeSearch { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SettingsStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial double JudgeThreshold { get; set; } = 0.55;

    [ObservableProperty]
    public partial string BehaviorStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool FeedbackEnabled { get; set; }

    [ObservableProperty]
    public partial bool FeedbackRecall { get; set; } = true;

    [ObservableProperty]
    public partial bool FeedbackExternal { get; set; }

    [ObservableProperty]
    public partial string FeedbackTarget { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FeedbackStatus { get; set; } = string.Empty;

    partial void OnNoticeSearchChanged(string value) => ApplyNoticeFilter();

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            var items = await _api.CallAsync<List<SettingDto>>("getSettings").ConfigureAwait(true) ?? [];

            Settings.Clear();
            foreach (var item in items)
            {
                if (item.Key == JudgeThresholdKey)
                {
                    JudgeThreshold = item.Value;
                    continue;
                }

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

            SettingsStatus = "已加载";
        }
        catch (Exception ex)
        {
            SettingsStatus = $"加载失败：{ex.Message}";
        }

        // 行为设置单独一段：读不到只让这一块报错，不把运行参数一起打成「加载失败」
        var behaviorError = await LoadBehaviorAsync().ConfigureAwait(true);
        BehaviorStatus = behaviorError is null ? string.Empty : $"加载失败：{behaviorError}";

        try
        {
            var feedback = await _api.CallAsync<FeedbackDto>("getFeedback").ConfigureAwait(true);
            FeedbackEnabled = feedback?.Enabled == true;
            var types = feedback?.Types ?? [];
            FeedbackRecall = types.Contains("MessageRecalled", StringComparer.OrdinalIgnoreCase);
            FeedbackExternal = types.Contains("ExternalMessage", StringComparer.OrdinalIgnoreCase);
            FeedbackTarget = feedback?.Target ?? string.Empty;
            FeedbackStatus = FeedbackEnabled ? "已开启" : "已关闭";
        }
        catch (Exception ex)
        {
            FeedbackStatus = $"加载失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 读一次 <c>getBehavior</c> 并整体刷新（平台分组 / 回复模式 / 通知开关）。
    /// </summary>
    /// <returns><c>null</c> = 成功；否则是可显示的错误原因（失败时旧数据原样留着）。</returns>
    private async Task<string?> LoadBehaviorAsync()
    {
        try
        {
            // 契约里 platform 可省略（缺省 qq）；getBehavior 本来就返回**全部平台**的通知，
            // 所以这里不传平台 —— 少一次来回，也不用为"切平台"重查一遍。
            var data = await _api.CallAsync<BehaviorDto>("getBehavior").ConfigureAwait(true);
            if (data is null)
            {
                return "服务端没有返回数据。";
            }

            ApplyBehavior(data);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>把一次 <c>getBehavior</c> 的结果铺到界面上：回复模式 + 按平台分组的通知开关。</summary>
    private void ApplyBehavior(BehaviorDto data)
    {
        ReplyModes.Clear();
        foreach (var mode in data.ReplyModes)
        {
            ReplyModes.Add(mode);
        }

        ReplyMode = data.ReplyMode;

        // 老服务端不返回 platforms → 保存退回老 payload（见 SaveBehaviorAsync）
        _legacyBehaviorServer = data.Platforms.Count == 0;

        _allNotices.Clear();
        _noticePlatforms.Clear();
        foreach (var item in data.Notices)
        {
            // 旧服务端不返回单项 platform → Normalize 归到 qq（现有通知类型全是 QQ 语义）
            _noticePlatforms[item.Type] = NoticePlatformLabeling.Normalize(item.Platform);
            _allNotices.Add(new NoticeToggleItem
            {
                Type = item.Type,
                Label = item.Label,
                Group = item.Group,
                Enabled = item.Enabled,
            });
        }

        ApplyNoticeFilter();
    }

    /// <summary>按搜索词过滤后**按平台分组**重铺：组 = 平台，组内仍分「常用 / 其他」两列。</summary>
    private void ApplyNoticeFilter()
    {
        var keyword = NoticeSearch.Trim();

        bool Match(NoticeToggleItem item) =>
            string.IsNullOrEmpty(keyword) ||
            item.Label.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
            item.Type.Contains(keyword, StringComparison.OrdinalIgnoreCase);

        NoticeGroups.Clear();

        var matched = _allNotices.Where(Match).ToList();
        var groups = matched
            .GroupBy(PlatformOf, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => NoticePlatformLabeling.OrderOf(g.Key))
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            NoticeGroups.Add(new NoticePlatformGroup(
                group.Key,
                group.Where(item => item.Group == "common"),
                group.Where(item => item.Group != "common")));
        }

        NoticeEmptyHint = _allNotices.Count == 0
            ? "服务端没有返回可配置的通知类型。"
            : matched.Count == 0
                ? "没有匹配的通知类型。"
                : string.Empty;
    }

    /// <summary>这条通知属于哪个平台（缺失 / 旧服务端 → 默认平台）。</summary>
    private string PlatformOf(NoticeToggleItem item) =>
        _noticePlatforms.TryGetValue(item.Type, out var platform)
            ? platform
            : NoticePlatformLabeling.DefaultPlatform;

    [RelayCommand]
    private async Task SaveSettingsAsync()
    {
        try
        {
            var outOfRange = Settings.FirstOrDefault(s => s.IsOutOfRange);
            if (outOfRange is not null)
            {
                SettingsStatus = $"「{outOfRange.Label}」超出范围（{outOfRange.Hint}）";
                return;
            }

            var payload = Settings.ToDictionary(s => s.Key, s => s.Value);
            payload[JudgeThresholdKey] = JudgeThreshold;

            var result = await _api.CallAsync<SaveResultDto>("setSettings", new { settings = payload })
                .ConfigureAwait(true);

            SettingsStatus = result?.Skipped == true ? "无变化" : $"已保存 {result?.Updated ?? 0} 项";
            await LoadAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SettingsStatus = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveBehaviorAsync()
    {
        try
        {
            // 一次保存覆盖**所有平台**：`notices` 数组里每项自带 platform（契约：单项优先于顶层），
            // 服务端会先全部校验再落库，所以跨平台保存也不会"一半成功一半失败"。
            // 老服务端（不返回 platforms）不认这个形态 → 退回单平台时代的老 payload，别让它 400。
            object payload;
            if (_legacyBehaviorServer)
            {
                payload = new
                {
                    replyMode = ReplyMode,
                    notice = _allNotices.ToDictionary(n => n.Type, n => n.Enabled),
                };
            }
            else
            {
                payload = new
                {
                    replyMode = ReplyMode,
                    notices = _allNotices
                        .Select(n => new { type = n.Type, enabled = n.Enabled, platform = PlatformOf(n) })
                        .ToList(),
                };
            }

            var result = await _api.CallAsync<SaveResultDto>("setBehavior", payload).ConfigureAwait(true);

            BehaviorStatus = result?.Skipped == true ? "无变化" : "行为设置已保存";
        }
        catch (Exception ex)
        {
            BehaviorStatus = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveFeedbackAsync()
    {
        try
        {
            var types = new List<string>();
            if (FeedbackRecall)
            {
                types.Add("MessageRecalled");
            }

            if (FeedbackExternal)
            {
                types.Add("ExternalMessage");
            }

            await _api.CallAsync<object>("setFeedback", new
            {
                enabled = FeedbackEnabled,
                types,
                target = FeedbackTarget.Trim(),
            }).ConfigureAwait(true);

            FeedbackStatus = FeedbackEnabled ? "已保存（已开启）" : "已保存（已关闭）";
        }
        catch (Exception ex)
        {
            FeedbackStatus = $"保存失败：{ex.Message}";
        }
    }
}
