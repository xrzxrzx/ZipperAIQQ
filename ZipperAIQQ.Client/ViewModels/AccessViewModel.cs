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
/// </remarks>
public sealed partial class AccessViewModel : ObservableObject
{
    private readonly ServerApiClient _api;
    private readonly Dictionary<string, string> _ownerDisplayToId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _adminDisplayToId = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _sessionDisplayToId = new(StringComparer.Ordinal);

    public AccessViewModel(ServerApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

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

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            var data = await _api.CallAsync<AccessDto>("getAccess").ConfigureAwait(true);
            if (data is null)
            {
                return;
            }

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
                ? "（尚未解析）"
                : string.Join("、", data.AlwaysAllow);

            PeopleStatus = "已加载";
            ListStatus = "已加载";
        }
        catch (Exception ex)
        {
            PeopleStatus = $"加载失败：{ex.Message}";
        }
    }

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
            var ownerId = _ownerDisplayToId.GetValueOrDefault(SelectedOwnerDisplay);
            if (!string.IsNullOrWhiteSpace(ownerId))
            {
                await _api.CallAsync<object>("setOwner", new
                {
                    userId = ownerId,
                    nickname = SelectedOwnerDisplay,
                }).ConfigureAwait(true);
            }

            var admins = AdminsSelected
                .Select(display => _adminDisplayToId.GetValueOrDefault(display))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct()
                .ToList();

            var result = await _api.CallAsync<SaveResultDto>("setAccess", new { admins })
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
