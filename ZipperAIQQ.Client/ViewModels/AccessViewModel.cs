using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>
/// 人员与名单页：主人 / 管理员 + 白名单 / 黑名单（#14 #19 #20 #22 #23 #24 #26 #27）。
/// </summary>
/// <remarks>
/// 两张卡各一个保存：人员（权限）与名单（接收过滤）本来就是两件事。
/// 候选用「显示名」给用户看，保存时再映射回账号 / 会话 ID。
/// <para>
/// S3-C（C6）：人员与名单按平台分开配置。页面顶部一个平台下拉，切换就整体重拉
/// （主人 / 管理员 / 名单 / 候选），保存时都带上当前平台。
/// </para>
/// </remarks>
public sealed partial class AccessViewModel : ObservableObject
{
    /// <summary>默认平台：S3-C 契约里请求省略 <c>platform</c> 时服务端按 qq 处理。</summary>
    private const string DefaultPlatform = "qq";

    /// <summary>平台代码 → 界面标签。未知平台原样显示，将来加平台不用改客户端。</summary>
    private static readonly Dictionary<string, string> PlatformLabels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["qq"] = "QQ",
        ["douyin"] = "抖音",
    };

    private readonly ServerApiClient _api;
    private readonly Dictionary<string, string> _ownerDisplayToId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _adminDisplayToId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionDisplayToId = new(StringComparer.Ordinal);

    /// <summary>下拉框标签 → 平台代码（界面显示标签，请求要代码）。</summary>
    private readonly Dictionary<string, string> _platformLabelToCode = new(StringComparer.Ordinal);

    /// <summary>上一次成功加载的平台标签——切换失败要退回它。</summary>
    private string _lastGoodPlatform = PlatformLabel(DefaultPlatform);

    /// <summary>正在同步平台下拉时，别把 <see cref="SelectedPlatform"/> 的变化当成用户切换。</summary>
    private bool _applyingPlatform;

    public AccessViewModel(ServerApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
        _platformLabelToCode[PlatformLabel(DefaultPlatform)] = DefaultPlatform;
    }

    /// <summary>可选平台（界面标签）。首次响应回来前先放默认项，下拉不会空着。</summary>
    public ObservableCollection<string> Platforms { get; } = [PlatformLabel(DefaultPlatform)];

    [ObservableProperty]
    public partial string SelectedPlatform { get; set; } = PlatformLabel(DefaultPlatform);

    /// <summary>平台行右侧的状态 / 错误提示；成功且无话说时为空（不占地方）。</summary>
    [ObservableProperty]
    public partial string PlatformStatus { get; set; } = string.Empty;

    /// <summary>当前选中的平台代码（下拉框里是标签，请求要代码）。</summary>
    private string CurrentPlatform =>
        string.IsNullOrWhiteSpace(SelectedPlatform)
            ? DefaultPlatform
            : _platformLabelToCode.GetValueOrDefault(SelectedPlatform, DefaultPlatform);

    public ObservableCollection<string> OwnerOptions { get; } = [];

    [ObservableProperty]
    public partial string SelectedOwnerDisplay { get; set; } = string.Empty;

    public ObservableCollection<string> AdminCandidates { get; } = [];

    /// <summary>已选管理员（显示名）。</summary>
    public ObservableCollection<string> AdminsSelected { get; } = [];

    public ObservableCollection<string> ListCandidates { get; } = [];

    /// <summary>已选白名单条目（显示名）。</summary>
    public ObservableCollection<string> WhitelistSelected { get; } = [];

    /// <summary>已选黑名单条目（显示名）。</summary>
    public ObservableCollection<string> BlacklistSelected { get; } = [];

    [ObservableProperty]
    public partial string AlwaysAllowText { get; set; } = "—";

    [ObservableProperty]
    public partial string PeopleStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ListStatus { get; set; } = string.Empty;

    /// <summary>服务端要求二次确认（拉黑管理员）。</summary>
    [ObservableProperty]
    public partial bool NeedsAdminConfirm { get; set; }

    [ObservableProperty]
    public partial string ConfirmMessage { get; set; } = string.Empty;

    /// <summary>用户在上方下拉里换了平台 → 用新平台整体重拉；失败退回原选择并给可见错误。</summary>
    partial void OnSelectedPlatformChanged(string value)
    {
        if (_applyingPlatform || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        _ = SwitchPlatformAsync(value);
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        var error = await LoadForPlatformAsync(CurrentPlatform).ConfigureAwait(true);
        if (error is not null)
        {
            PeopleStatus = $"加载失败：{error}";
            PlatformStatus = $"加载失败：{error}";
            return;
        }

        PeopleStatus = "已加载";
        ListStatus = "已加载";
        PlatformStatus = string.Empty;
    }

    /// <summary>
    /// 切平台：**先拉数据，成功了才改选择**——失败时旧数据还在、下拉也退回原来那项，
    /// 同时把原因写到 <see cref="PlatformStatus"/>，不静默。
    /// </summary>
    private async Task SwitchPlatformAsync(string label)
    {
        var previous = _lastGoodPlatform;
        var code = _platformLabelToCode.GetValueOrDefault(label, DefaultPlatform);

        PlatformStatus = $"正在切到 {label}…";
        var error = await LoadForPlatformAsync(code).ConfigureAwait(true);
        if (error is null)
        {
            PlatformStatus = $"已切到 {label}";
            return;
        }

        _applyingPlatform = true;
        try
        {
            SelectedPlatform = previous;
        }
        finally
        {
            _applyingPlatform = false;
        }

        PlatformStatus = $"切换平台失败（{label}）：{error}——已保持 {previous}";
    }

    /// <summary>
    /// 按指定平台拉一次 <c>getAccess</c> 并整体刷新；**成功才动数据**，
    /// 失败时旧的主人 / 名单 / 候选原样留着，由调用方决定怎么提示。
    /// </summary>
    /// <returns><c>null</c> = 成功；否则是可显示的错误原因。</returns>
    private async Task<string?> LoadForPlatformAsync(string platformCode)
    {
        try
        {
            var data = await _api
                .CallAsync<AccessDto>("getAccess", new PlatformQueryDto { Platform = platformCode })
                .ConfigureAwait(true);

            if (data is null)
            {
                return "服务端没有返回数据。";
            }

            Apply(data, platformCode);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    /// <summary>把一次 <c>getAccess</c> 的结果铺到界面上：平台 / 主人 / 管理员 / 名单 / 候选。</summary>
    private void Apply(AccessDto data, string requestedPlatform)
    {
        SyncPlatforms(data, requestedPlatform);

        // 主人候选
        _ownerDisplayToId.Clear();
        OwnerOptions.Clear();
        foreach (var candidate in data.Candidates)
        {
            _ownerDisplayToId[candidate.Display] = candidate.UserId;
            OwnerOptions.Add(candidate.Display);
        }

        SelectedOwnerDisplay = _ownerDisplayToId
            .FirstOrDefault(kv => kv.Value == data.OwnerId).Key ?? string.Empty;

        // 管理员：好友候选 + 已有值（可能不在候选里）
        _adminDisplayToId.Clear();
        AdminCandidates.Clear();
        foreach (var candidate in data.Candidates)
        {
            _adminDisplayToId[candidate.Display] = candidate.UserId;
            AdminCandidates.Add(candidate.Display);
        }

        AdminsSelected.Clear();
        foreach (var admin in data.Admins)
        {
            AddDisplay(AdminCandidates, _adminDisplayToId, AdminsSelected, admin, $"账号 {admin}");
        }

        // 名单：私聊会话 + 群会话
        _sessionDisplayToId.Clear();
        ListCandidates.Clear();
        foreach (var candidate in data.Candidates)
        {
            var sessionId = $"qq:p{candidate.UserId}";
            var display = $"私聊 · {candidate.Nickname}（{sessionId}）";
            _sessionDisplayToId[display] = sessionId;
            ListCandidates.Add(display);
        }

        foreach (var group in data.GroupCandidates)
        {
            _sessionDisplayToId[group.Display] = group.SessionId;
            ListCandidates.Add(group.Display);
        }

        WhitelistSelected.Clear();
        foreach (var entry in data.Whitelist)
        {
            AddDisplay(ListCandidates, _sessionDisplayToId, WhitelistSelected, entry, entry);
        }

        BlacklistSelected.Clear();
        foreach (var entry in data.Blacklist)
        {
            AddDisplay(ListCandidates, _sessionDisplayToId, BlacklistSelected, entry, entry);
        }

        AlwaysAllowText = data.AlwaysAllow.Count == 0
            ? "（服务端未返回 —— 可能是旧版本；本地新服务端会显示主人私聊与所在群）"
            : string.Join("、", data.AlwaysAllow);
    }

    /// <summary>
    /// 同步平台下拉：服务端回显优先，没回显就用请求时带的平台；
    /// **旧服务端不返回 <c>platforms</c> 时退化成只有默认平台，不报错**。
    /// </summary>
    private void SyncPlatforms(AccessDto data, string requestedPlatform)
    {
        var effective = string.IsNullOrWhiteSpace(data.Platform)
            ? requestedPlatform
            : data.Platform.Trim();

        // 去重 + 保证回显的平台一定能选中（服务端理论上不会重复，防一手）
        List<string> codes = data.Platforms.Count > 0
            ? [.. data.Platforms.Distinct(StringComparer.OrdinalIgnoreCase)]
            : [effective];
        if (!codes.Contains(effective, StringComparer.OrdinalIgnoreCase))
        {
            codes = [.. codes, effective];
        }

        var labels = codes.Select(PlatformLabel).ToList();
        if (!Platforms.SequenceEqual(labels, StringComparer.Ordinal))
        {
            _applyingPlatform = true;
            try
            {
                Platforms.Clear();
                _platformLabelToCode.Clear();
                foreach (var code in codes)
                {
                    var label = PlatformLabel(code);
                    _platformLabelToCode[label] = code;
                    if (!Platforms.Contains(label))
                    {
                        Platforms.Add(label);
                    }
                }
            }
            finally
            {
                _applyingPlatform = false;
            }
        }

        var wanted = PlatformLabel(effective);
        if (!string.Equals(SelectedPlatform, wanted, StringComparison.Ordinal))
        {
            _applyingPlatform = true;
            try
            {
                SelectedPlatform = wanted;
            }
            finally
            {
                _applyingPlatform = false;
            }
        }

        _lastGoodPlatform = wanted;
    }

    /// <summary>平台代码 → 界面标签；空值按默认平台，未知平台原样显示。</summary>
    private static string PlatformLabel(string code) =>
        string.IsNullOrWhiteSpace(code)
            ? PlatformLabels[DefaultPlatform]
            : PlatformLabels.TryGetValue(code, out var label) ? label : code;

    /// <summary>把 ID 转成显示名；候选里没有就补一条「原值」。</summary>
    private static void AddDisplay(
        ObservableCollection<string> candidates,
        Dictionary<string, string> map,
        ObservableCollection<string> selected,
        string value,
        string fallbackLabel)
    {
        var display = map.FirstOrDefault(kv => kv.Value == value).Key;
        if (string.IsNullOrEmpty(display))
        {
            display = fallbackLabel;
            map[display] = value;
            candidates.Add(display);
        }

        if (!selected.Contains(display))
        {
            selected.Add(display);
        }
    }

    [RelayCommand]
    private async Task SavePeopleAsync()
    {
        try
        {
            // 保存的是「当前下拉选中的平台」的人员，别的平台不受影响（S3-C）
            var platform = CurrentPlatform;

            var ownerId = _ownerDisplayToId.GetValueOrDefault(SelectedOwnerDisplay);
            if (!string.IsNullOrWhiteSpace(ownerId))
            {
                await _api.CallAsync<object>("setOwner", new SetOwnerRequestDto
                {
                    Platform = platform,
                    UserId = ownerId,
                    Nickname = SelectedOwnerDisplay,
                }).ConfigureAwait(true);
            }

            var admins = AdminsSelected
                .Select(display => _adminDisplayToId.GetValueOrDefault(display))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToList();

            // 请求体刻意只带本次要改的字段（部分更新语义），所以还是匿名对象
            var result = await _api.CallAsync<SaveResultDto>("setAccess", new { platform, admins })
                .ConfigureAwait(true);

            PeopleStatus = result?.Skipped == true ? "无变化" : "人员已保存";
        }
        catch (ApiCallException ex) when (ex.Code == 409)
        {
            // 管理员在黑名单里：交给页面弹确认，用户确认后调 SaveListsAsync(true)
            NeedsAdminConfirm = true;
            ConfirmMessage = ex.Message;
            PeopleStatus = "需要确认：有管理员在黑名单里";
        }
        catch (Exception ex)
        {
            PeopleStatus = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 保存两份名单；<paramref name="confirmDemote"/> = true 表示用户已确认「剥夺管理员并拉黑」。
    /// </summary>
    public async Task<bool> SaveListsAsync(bool confirmDemote = false)
    {
        try
        {
            var whitelist = Map(_sessionDisplayToId, WhitelistSelected);
            var blacklist = Map(_sessionDisplayToId, BlacklistSelected);

            var overlap = whitelist.Intersect(blacklist, StringComparer.OrdinalIgnoreCase).ToList();
            if (overlap.Count > 0)
            {
                ListStatus = $"同一条目不能同时在白名单与黑名单：{string.Join("、", overlap)}";
                return false;
            }

            var result = await _api.CallAsync<SaveResultDto>("setAccess", new
            {
                platform = CurrentPlatform,
                whitelist,
                blacklist,
                confirmDemoteAdmins = confirmDemote,
            }).ConfigureAwait(true);

            NeedsAdminConfirm = false;
            ListStatus = result?.Skipped == true ? "无变化" : "名单已保存";
            await LoadAsync().ConfigureAwait(true);
            return true;
        }
        catch (ApiCallException ex) when (ex.Code == 409)
        {
            NeedsAdminConfirm = true;
            ConfirmMessage = ex.Message;
            ListStatus = "需要确认：有管理员在黑名单里";
            return false;
        }
        catch (Exception ex)
        {
            ListStatus = $"保存失败：{ex.Message}";
            return false;
        }
    }

    private static List<string> Map(
        Dictionary<string, string> map,
        ObservableCollection<string> selected) =>
        selected
            .Select(display => map.TryGetValue(display, out var value) ? value : display)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct()
            .ToList();
}
