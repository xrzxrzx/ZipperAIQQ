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
}
