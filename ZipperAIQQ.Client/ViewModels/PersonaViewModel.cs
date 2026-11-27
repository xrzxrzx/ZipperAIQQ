using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>
/// 人设页：形象 / 性格 / 当前会话是否启用预设（#14 #16 #17 #27）。
/// </summary>
/// <remarks>
/// 会话下拉用「显示名」（#15/#17），内部再映射回 sessionId；
/// 全局项永远排第一，并支持一键切回全局（用户 2026-10-23 的澄清）。
/// </remarks>
public sealed partial class PersonaViewModel : ObservableObject
{
    public const string GlobalLabel = "【全局】";

    private readonly ServerApiClient _api;
    private readonly Dictionary<string, string> _displayToSession = new(StringComparer.Ordinal);

    public PersonaViewModel(ServerApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    public ObservableCollection<string> SessionOptions { get; } = [GlobalLabel];

    [ObservableProperty]
    public partial string SelectedSession { get; set; } = GlobalLabel;

    [ObservableProperty]
    public partial string PersonaImage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PersonaPersonality { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Enabled { get; set; } = true;

    /// <summary>有会话级覆盖时才允许切开关；全局永远「始终生效」。</summary>
    [ObservableProperty]
    public partial bool CanToggle { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; } = string.Empty;

    /// <summary>系统提示词（基础行为规则）：与人设分开放，但都属于「这个角色怎么表现」。</summary>
    [ObservableProperty]
    public partial string SystemPrompt { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PromptStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Hint { get; set; } = "选一个会话编辑它的人设；不选会话时可改全局人设。";

    partial void OnSelectedSessionChanged(string value)
    {
        _ = LoadPersonaAsync();

        // 会话覆盖是「这个会话单独一档」，换会话必须重新读 —— 否则界面上显示的会是上一个会话的档位
        _ = RefreshExpressOnSessionChangeAsync();
    }

    private async Task RefreshExpressOnSessionChangeAsync()
    {
        try
        {
            await RefreshExpressAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ExpressMessage = $"读取档位失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            var sessions = await _api.CallAsync<List<SessionDto>>("listSessions", new { limit = 500 })
                .ConfigureAwait(true) ?? [];

            _displayToSession.Clear();
            var desired = new List<string> { GlobalLabel };
            foreach (var session in sessions)
            {
                var display = session.Title;
                if (_displayToSession.ContainsKey(display))
                {
                    display = $"{display}（{session.SessionId}）";
                }

                _displayToSession[display] = session.SessionId;
                desired.Add(display);
            }

            if (!SessionOptions.SequenceEqual(desired))
            {
                SessionOptions.Clear();
                foreach (var item in desired)
                {
                    SessionOptions.Add(item);
                }
            }

            if (!SessionOptions.Contains(SelectedSession))
            {
                SelectedSession = GlobalLabel;
            }

            await LoadPersonaAsync().ConfigureAwait(true);
            await LoadExpressAsync().ConfigureAwait(true);

            var prompt = await _api.CallAsync<SystemPromptDto>("getSystemPrompt").ConfigureAwait(true);
            SystemPrompt = prompt?.Text ?? string.Empty;
            PromptStatus = prompt?.IsDefault == true ? "当前是默认提示词" : "已自定义";

            Status = "已加载";
        }
        catch (Exception ex)
        {
            Status = $"加载失败：{ex.Message}";
        }
    }

    private async Task LoadPersonaAsync()
    {
        try
        {
            var isGlobal = SelectedSession == GlobalLabel;
            var sessionId = isGlobal ? "global" : _displayToSession.GetValueOrDefault(SelectedSession, "global");

            var persona = await _api.CallAsync<PersonaDto>("getPersona", new { sessionId }).ConfigureAwait(true);
            if (persona is null)
            {
                return;
            }

            PersonaImage = persona.Image ?? persona.Content;
            PersonaPersonality = persona.Personality ?? string.Empty;
            Enabled = persona.Enabled;
            CanToggle = !isGlobal && persona.HasOverride;

            Hint = isGlobal
                ? "正在编辑全局人设（始终生效）。选了别的会话后可以在这里一键切回全局。"
                : CanToggle
                    ? "这个会话有自己的预设；关掉开关即回落到全局人设。"
                    : "这个会话还没有自己的预设，保存后会生成。";

            Status = string.Empty;
        }
        catch (Exception ex)
        {
            Status = $"读取人设失败：{ex.Message}";
        }
    }

    /// <summary>快捷切回全局（#16 的补充要求）。</summary>
    [RelayCommand]
    private void BackToGlobal() => SelectedSession = GlobalLabel;

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(PersonaImage) && string.IsNullOrWhiteSpace(PersonaPersonality))
            {
                Status = "形象与性格至少要填一个（都空会让人设消失）。";
                return;
            }

            if (SelectedSession == GlobalLabel)
            {
                await _api.CallAsync<PersonaDto>("setGlobalPersona", new
                {
                    image = PersonaImage,
                    personality = PersonaPersonality,
                }).ConfigureAwait(true);

                Status = "全局人设已保存";
                return;
            }

            var sessionId = _displayToSession.GetValueOrDefault(SelectedSession);
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                Status = "这个会话没有有效的 sessionId，先刷新一下。";
                return;
            }

            var result = await _api.CallAsync<PersonaDto>("setPersona", new
            {
                sessionId,
                image = PersonaImage,
                personality = PersonaPersonality,
                enabled = Enabled,
            }).ConfigureAwait(true);

            Status = result?.Skipped == true ? "无变化" : "会话人设已保存";
            CanToggle = true;
        }
        catch (Exception ex)
        {
            Status = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ClearAsync()
    {        try
        {
            if (SelectedSession == GlobalLabel)
            {
                Status = "全局人设不能清除（那会让 AI 失去角色）。";
                return;
            }

            var sessionId = _displayToSession.GetValueOrDefault(SelectedSession);
            if (string.IsNullOrWhiteSpace(sessionId))
            {
                return;
            }

            await _api.CallAsync<object>("clearPersona", new { sessionId }).ConfigureAwait(true);
            Status = "已清除会话预设，回落到全局";
            await LoadPersonaAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Status = $"清除失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SavePromptAsync()
    {
        try
        {
            await _api.CallAsync<object>("setSystemPrompt", new { text = SystemPrompt }).ConfigureAwait(true);
            PromptStatus = string.IsNullOrWhiteSpace(SystemPrompt) ? "已恢复默认" : "系统提示词已保存";
        }
        catch (Exception ex)
        {
            PromptStatus = $"保存失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ResetPromptAsync()
    {
        try
        {
            var result = await _api.CallAsync<SystemPromptDto>("resetSystemPrompt").ConfigureAwait(true);
            SystemPrompt = result?.DefaultText ?? string.Empty;
            PromptStatus = "已恢复默认提示词";
        }
        catch (Exception ex)
        {
            PromptStatus = $"恢复失败：{ex.Message}";
        }
    }

    // ══════════════ 表达档位（B6 / 表达档位设计 §9）══════════════
    //
    // 为什么放在人设页：档位改的是「她说话怎么放得开」，与人设 / 系统提示词是同一类东西 ——
    // 三处口径分在三个页面里，改了人设忘了改档位是最常见的漂移。

    /// <summary>三档的中文标签（顺序 = 由收 to 放）。</summary>
    public ObservableCollection<string> ExpressLevelOptions { get; } = ["日常", "亲近", "露骨"];

    /// <summary>标签 → 键值。</summary>
    /// <remarks>
    /// 客户端只在这一处做映射，且**保存后一律用服务端回显的标签纠正界面**：
    /// 哪天服务端改了档位名字，这里最多失效一次提示，不会出现"选了没生效还以为生效了"。
    /// </remarks>
    private static readonly Dictionary<string, string> ExpressLabelToKey = new(StringComparer.Ordinal)
    {
        ["日常"] = "daily",
        ["亲近"] = "close",
        ["露骨"] = "explicit",
    };

    [ObservableProperty]
    public partial string SelectedExpressLevel { get; set; } = "日常";

    [ObservableProperty]
    public partial string SelectedGroupCap { get; set; } = "露骨";

    [ObservableProperty]
    public partial string SelectedSessionLevel { get; set; } = "日常";

    /// <summary>当前选中会话是否已有自己的档位覆盖。</summary>
    [ObservableProperty]
    public partial bool HasSessionExpress { get; set; }

    [ObservableProperty]
    public partial string ExpressRules { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ExpressStatusLine { get; set; } = "尚未读取";

    [ObservableProperty]
    public partial string ExpressMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ExpressHygieneEnabled { get; set; } = true;

    [ObservableProperty]
    public partial bool ExpressHygieneRegenerate { get; set; } = true;

    public ObservableCollection<ExpressSnapshotDto> ExpressSnapshots { get; } = [];

    [ObservableProperty]
    public partial ExpressSnapshotDto? SelectedExpressSnapshot { get; set; }

    [RelayCommand]
    private async Task LoadExpressAsync()
    {
        try
        {
            await RefreshExpressAsync().ConfigureAwait(true);
            ExpressMessage = "已读取";
        }
        catch (Exception ex)
        {
            ExpressMessage = $"读取档位失败：{ex.Message}";
        }
    }

    private async Task RefreshExpressAsync()
    {
        var sessionId = CurrentSessionId();
        var status = await _api
            .CallAsync<ExpressStatusDto>("getExpressStatus", new { sessionId })
            .ConfigureAwait(true);

        if (status is null)
        {
            ExpressStatusLine = "服务端没有返回档位状态";
            return;
        }

        ApplyExpressStatus(status);
    }

    /// <summary>把服务端状态贴上界面（**不自己猜**，全部照抄回显）。</summary>
    private void ApplyExpressStatus(ExpressStatusDto status)
    {
        SelectedExpressLevel = string.IsNullOrWhiteSpace(status.LevelLabel) ? "日常" : status.LevelLabel;
        SelectedGroupCap = string.IsNullOrWhiteSpace(status.GroupCapLabel) ? "露骨" : status.GroupCapLabel;
        ExpressHygieneEnabled = status.Hygiene.Enabled;
        ExpressHygieneRegenerate = status.Hygiene.Regenerate;
        ExpressRules = status.PromptExpress.Text;

        var sessionId = CurrentSessionId();
        var sessionOverride = sessionId is null
            ? null
            : status.SessionOverrides.FirstOrDefault(o => o.SessionId == sessionId);

        HasSessionExpress = sessionOverride is not null;
        SelectedSessionLevel = sessionOverride?.LevelLabel ?? SelectedExpressLevel;

        var changedAt = status.LastChangedAt > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(status.LastChangedAt).ToLocalTime().ToString("MM-dd HH:mm")
            : "从未改动";

        ExpressStatusLine =
            $"当前档：{status.LevelLabel}（来源：{(status.Source == "session" ? "本会话" : "全局")}）" +
            $"｜群聊上限：{status.GroupCapLabel}" +
            $"｜卫生命中 {status.Hygiene.Hits} 次（重说 {status.Hygiene.Regenerated} 次）" +
            $"｜上次改动 {changedAt}";

        ExpressSnapshots.Clear();
        foreach (var snapshot in status.Snapshots)
        {
            ExpressSnapshots.Add(snapshot);
        }

        SelectedExpressSnapshot ??= ExpressSnapshots.FirstOrDefault();
    }

    /// <summary>当前选中会话的完整 ID；选中「全局」时为 <c>null</c>。</summary>
    private string? CurrentSessionId() =>
        SelectedSession == GlobalLabel ? null : _displayToSession.GetValueOrDefault(SelectedSession);

    [RelayCommand]
    private async Task SetExpressLevelAsync(string? label)
    {
        if (string.IsNullOrWhiteSpace(label) || !ExpressLabelToKey.TryGetValue(label, out var key))
        {
            ExpressMessage = $"认不出的档位：{label}";
            return;
        }

        await SaveExpressAsync(new { level = key }, $"全局档已改为{label}").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SetGroupCapAsync(string? label)
    {
        if (string.IsNullOrWhiteSpace(label) || !ExpressLabelToKey.TryGetValue(label, out var key))
        {
            ExpressMessage = $"认不出的档位：{label}";
            return;
        }

        await SaveExpressAsync(new { groupCap = key }, $"群聊上限已改为{label}").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SetSessionLevelAsync(string? label)
    {
        var sessionId = CurrentSessionId();
        if (sessionId is null)
        {
            ExpressMessage = "先在上面选一个具体会话，再设它的档位（「全局」没有覆盖一说）。";
            return;
        }

        if (string.IsNullOrWhiteSpace(label) || !ExpressLabelToKey.TryGetValue(label, out var key))
        {
            ExpressMessage = $"认不出的档位：{label}";
            return;
        }

        await SaveExpressAsync(
            new { sessionId, sessionLevel = key }, $"本会话档位已设为{label}").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ClearSessionLevelAsync()
    {
        var sessionId = CurrentSessionId();
        if (sessionId is null)
        {
            ExpressMessage = "「全局」没有会话覆盖可清除。";
            return;
        }

        // 只给 sessionId、不给 sessionLevel = 显式删除这条覆盖
        await SaveExpressAsync(new { sessionId }, "已清除本会话的档位覆盖").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveExpressRulesAsync()
    {
        await SaveExpressAsync(
            new { promptExpress = ExpressRules },
            string.IsNullOrWhiteSpace(ExpressRules) ? "规则段已恢复默认" : "规则段已保存").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task ResetExpressRulesAsync()
    {
        ExpressRules = string.Empty;
        await SaveExpressAsync(new { promptExpress = string.Empty }, "规则段已恢复默认").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task SaveExpressHygieneAsync()
    {
        await SaveExpressAsync(
            new { hygieneEnabled = ExpressHygieneEnabled, hygieneRegenerate = ExpressHygieneRegenerate },
            "免拒绝校准设置已保存").ConfigureAwait(true);
    }

    [RelayCommand]
    private async Task RollbackExpressAsync()
    {
        if (SelectedExpressSnapshot is null)
        {
            ExpressMessage = "先选一个回滚点。";
            return;
        }

        try
        {
            var result = await _api
                .CallAsync<ExpressStatusDto>("rollbackExpress", new { id = SelectedExpressSnapshot.Id })
                .ConfigureAwait(true);

            if (result is null)
            {
                ExpressMessage = "回滚没有返回结果";
                return;
            }

            ExpressMessage = $"已回滚到 #{SelectedExpressSnapshot.Id}（{result.LevelLabel}）";
            await RefreshExpressAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ExpressMessage = $"回滚失败：{ex.Message}";
        }
    }

    /// <summary>保存档位（统一入口）：**回显服务端返回值**，不在客户端猜结果。</summary>
    private async Task SaveExpressAsync(object payload, string successMessage)
    {
        try
        {
            var result = await _api.CallAsync<ExpressStatusDto>("setExpressLevel", payload).ConfigureAwait(true);

            if (result is null)
            {
                ExpressMessage = "保存没有返回结果（服务端可能未实现该接口）";
                return;
            }

            ExpressMessage = result.Skipped ? "无变化（本来就是这个值）" : successMessage;
            ApplyExpressStatus(result);

            // 改完刷新一次：会话覆盖列表与回滚点都要重新拉（set 的响应里没有 snapshots）
            await RefreshExpressAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            ExpressMessage = $"保存失败：{ex.Message}";
        }
    }
}
