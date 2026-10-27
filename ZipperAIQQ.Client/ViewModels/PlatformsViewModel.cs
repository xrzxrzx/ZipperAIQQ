using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>
/// 顶部平台总览里的一个点（QQ / 抖音…），数据来自 <c>getPlatforms.platforms</c>。
/// </summary>
public sealed class PlatformChipItem
{
    public PlatformChipItem(PlatformInfoDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        Id = dto.Id;
        Label = string.IsNullOrWhiteSpace(dto.Label) ? dto.Id : dto.Label;
        Connected = dto.Connected;
        Enabled = dto.Enabled;
        SelfId = dto.SelfId ?? string.Empty;
        Features = dto.Features;
    }

    public string Id { get; }

    public string Label { get; }

    public bool Connected { get; }

    /// <summary>x:Load 的第二根笔：没连上 / 没启用时显示灰点。</summary>
    public bool Offline => !Connected;

    public bool Enabled { get; }

    public string SelfId { get; }

    public IReadOnlyList<string> Features { get; }

    public string StatusText => Connected
        ? (string.IsNullOrWhiteSpace(SelfId) ? "已连接" : $"已连接 · {SelfId}")
        : (Enabled ? "未连接" : "未启用");

    public string FeatureText => Features.Count == 0 ? string.Empty : string.Join("、", Features);
}

/// <summary>
/// 邮件页的一张账号卡（<see cref="MailAccountDto"/> 的界面投影）。
/// </summary>
/// <remarks>
/// 服务端只给 <c>hasSecret</c>，这里也只有它 —— 凭据永远不进客户端内存。
/// <see cref="Enabled"/> 可被用户拨动，拨完立刻按"合并语义"保存（只带 id + enabled）。
/// </remarks>
public sealed partial class MailAccountItem : ObservableObject
{
    public MailAccountItem(MailAccountDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        Id = dto.Id;
        Address = dto.Address;
        DisplayName = dto.DisplayName ?? string.Empty;
        ImapHost = dto.ImapHost;
        ImapPort = dto.ImapPort;
        UseSsl = dto.UseSsl;
        Username = dto.Username ?? string.Empty;
        HasSecret = dto.HasSecret;
        LastSyncAt = dto.LastSyncAt;
        LastError = dto.LastError ?? string.Empty;
        Enabled = dto.Enabled;
    }

    public string Id { get; }

    public string Address { get; }

    public string DisplayName { get; }

    public string ImapHost { get; }

    public int ImapPort { get; }

    public bool UseSsl { get; }

    public string Username { get; }

    public bool HasSecret { get; }

    public long? LastSyncAt { get; }

    public string LastError { get; }

    [ObservableProperty]
    public partial bool Enabled { get; set; }

    public string SubtitleText => string.IsNullOrWhiteSpace(DisplayName) ? "未命名账号" : DisplayName;

    public string ImapText => $"{ImapHost}:{ImapPort} · {(UseSsl ? "SSL" : "明文")} · 用户 {Username}";

    public string SecretText => HasSecret ? "授权码：已配置（不回显）" : "授权码：未配置（收信需要它）";

    public string SyncText => LastSyncAt is { } ms && ms > 0
        ? $"最近同步：{DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime:MM-dd HH:mm:ss}"
        : "还没同步过";

    public bool HasError => !string.IsNullOrWhiteSpace(LastError);

    public string ErrorText => $"上次同步失败：{LastError}";
}

/// <summary>
/// 第三方平台接入配置页（S3-F）：邮件收信账号 + 抖音 sidecar 配置。
/// </summary>
/// <remarks>
/// <para>
/// 两条加载线**互不拖累**：邮件走已有的 <c>listMailAccounts</c>，平台总览 / 抖音走新的 <c>getPlatforms</c>。
/// 旧服务端没有 <c>getPlatforms</c> 时，邮件页照样能用，只是总览与抖音页给"需要新服务端"的提示。
/// </para>
/// <para>
/// 加载 / 保存失败一律**不清旧数据**，只把原因写到对应的状态位（页面看得见），符合"失败要响"。
/// </para>
/// </remarks>
public sealed partial class PlatformsViewModel : ObservableObject
{
    private const int DefaultSendTimeoutMs = 15_000;
    private const int DefaultMaxTextLength = 16_000;
    private const int DefaultQueryTimeoutMs = 10_000;

    private readonly ServerApiClient _api;

    public PlatformsViewModel(ServerApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    /// <summary>平台总览（QQ / 抖音…）。</summary>
    public ObservableCollection<PlatformChipItem> PlatformChips { get; } = [];

    /// <summary>邮件账号卡（每张卡一个账号）。</summary>
    public ObservableCollection<MailAccountItem> MailAccounts { get; } = [];

    [ObservableProperty]
    public partial string OverviewStatus { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string MailStatus { get; set; } = string.Empty;

    /// <summary>账号为空时的引导文案（有卡就清空）。</summary>
    [ObservableProperty]
    public partial string MailEmptyHint { get; set; } = string.Empty;

    /// <summary>邮件通道当前状态文案（已启用 / 已停用 / 未知）。</summary>
    [ObservableProperty]
    public partial string MailChannelText { get; set; } = "邮件通道：状态未知（还没加载）";

    /// <summary>邮件总开关的运行时真值（<c>getPlatforms.mail.enabled</c>，可热改，不必重启）。</summary>
    [ObservableProperty]
    public partial bool MailEnabled { get; set; }

    /// <summary>
    /// 开关能不能动：**服务端没返回 mail 块（旧版本）→ false**。
    /// 宁可禁用并说明，也不摆一个假状态。
    /// </summary>
    [ObservableProperty]
    public partial bool MailSwitchAvailable { get; set; }

    /// <summary>开关自己的状态位（成功「已启用 / 已停用」、失败原因）。</summary>
    [ObservableProperty]
    public partial string MailSwitchStatus { get; set; } = string.Empty;

    // ── 抖音配置卡 ────────────────────────────────────────────────
    [ObservableProperty]
    public partial bool DouyinEnabled { get; set; }

    /// <summary>用户这次输入的 token（**保存成功后立刻清空**，不在内存里留明文）。</summary>
    [ObservableProperty]
    public partial string DouyinTokenInput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DouyinTokenStatus { get; set; } = "未配置";

    [ObservableProperty]
    public partial string DouyinAllowedSessionsInput { get; set; } = string.Empty;

    // NumberBox 的 Value 是 double，所以这三个也保持 double，发请求时再取整
    [ObservableProperty]
    public partial double DouyinSendTimeoutMs { get; set; } = DefaultSendTimeoutMs;

    [ObservableProperty]
    public partial double DouyinMaxTextLength { get; set; } = DefaultMaxTextLength;

    [ObservableProperty]
    public partial double DouyinQueryTimeoutMs { get; set; } = DefaultQueryTimeoutMs;

    [ObservableProperty]
    public partial string DouyinStatus { get; set; } = string.Empty;

    // ── 抖音状态卡 ────────────────────────────────────────────────
    [ObservableProperty]
    public partial bool DouyinConnected { get; set; }

    [ObservableProperty]
    public partial bool DouyinOffline { get; set; } = true;

    [ObservableProperty]
    public partial string DouyinStateText { get; set; } = "还没加载";

    [ObservableProperty]
    public partial string DouyinEndpointText { get; set; } = "/ws/channel";

    /// <summary>刷新页面（两个 Tab 一起刷）。</summary>
    [RelayCommand]
    private async Task LoadAsync()
    {
        await LoadMailAccountsAsync().ConfigureAwait(true);
        await LoadPlatformsAsync().ConfigureAwait(true);
    }

    /// <summary>拉邮件账号；**失败保留旧卡片**，只更新状态文案。</summary>
    private async Task LoadMailAccountsAsync()
    {
        try
        {
            List<MailAccountDto> accounts =
                await _api.CallAsync<List<MailAccountDto>>("listMailAccounts")
                    .ConfigureAwait(true) ?? [];

            MailAccounts.Clear();
            foreach (var dto in accounts)
            {
                MailAccounts.Add(new MailAccountItem(dto));
            }

            MailEmptyHint = MailAccounts.Count == 0
                ? "还没有收信账号 —— 点右上角「新建账号」开始。"
                : string.Empty;
            MailStatus = "已加载";
        }
        catch (Exception ex)
        {
            MailStatus = $"加载账号失败：{ex.Message}";
            MailEmptyHint = MailAccounts.Count == 0 ? "账号列表加载失败（原因见上一行）" : string.Empty;
        }
    }

    /// <summary>拉平台总览 + 抖音块；**失败保留旧值**，只更新状态文案。</summary>
    private async Task LoadPlatformsAsync()
    {
        PlatformsDto? data;
        try
        {
            data = await _api.CallAsync<PlatformsDto>("getPlatforms").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            OverviewStatus = $"平台总览加载失败：{ex.Message}（邮件账号不受影响；抖音配置需要新服务端）";

            // 别让抖音页停在"还没加载"这种含糊状态上 —— 说清是"没拿到"而不是"没连上"
            if (PlatformChips.Count == 0)
            {
                DouyinStateText = "抖音状态未知（getPlatforms 失败，见上方提示）";
                MailChannelText = "邮件通道：状态未知（getPlatforms 失败，不影响账号增删改）";
            }

            return;
        }

        if (data is null)
        {
            OverviewStatus = "平台总览：服务端没有返回数据。";
            return;
        }

        PlatformChips.Clear();
        foreach (var info in data.Platforms)
        {
            PlatformChips.Add(new PlatformChipItem(info));
        }

        OverviewStatus = PlatformChips.Count == 0
            ? "服务端没返回平台清单（旧版本？）—— 不影响下面两个 Tab 的配置"
            : string.Empty;

        ApplyMailBlock(data.Mail);
        ApplyDouyinBlock(data.Douyin);
    }

    /// <summary>邮件块：通道开关的真值 + 账号兜底（账号仍以 listMailAccounts 为准）。</summary>
    private void ApplyMailBlock(MailPlatformDto? mail)
    {
        if (mail is null)
        {
            // 旧服务端：连 mail 块都没有 → 不知道真值，也不许假装知道
            MailSwitchAvailable = false;
            MailChannelText = "邮件通道：服务端未返回运行状态（旧版本）—— 开关不可用，只能改 server.yaml 后重启";
            return;
        }

        MailSwitchAvailable = true;
        MailEnabled = mail.Enabled;
        ShowMailChannelState(mail.Enabled);

        // 正常情况下 listMailAccounts 已经填过；只有它失败（列表还空着）时才用这份兜底
        if (MailAccounts.Count == 0 && mail.Accounts.Count > 0)
        {
            foreach (var dto in mail.Accounts)
            {
                MailAccounts.Add(new MailAccountItem(dto));
            }

            MailEmptyHint = string.Empty;
        }
    }

    /// <summary>把「已启用 / 已停用」翻译成给人看的一句话（关闭的后果也写在这儿）。</summary>
    private void ShowMailChannelState(bool enabled) =>
        MailChannelText = enabled
            ? "邮件通道：已启用（运行时可热切，改完不必重启服务）"
            : "邮件通道：已停用 —— 关闭后不再连 IMAP 收信；已收的邮件与账号仍保留";

    /// <summary>
    /// 邮件总开关（<c>saveMail</c>，运行时热切）。失败**把开关拨回去**并把原因写出来；
    /// 旧服务端（未知 action = 404）额外说明"不支持运行时开关"，同时禁用开关。
    /// </summary>
    public async Task SetMailChannelEnabledAsync(bool enabled)
    {
        if (enabled == MailEnabled)
        {
            return;   // 没变就别打扰服务端
        }

        var previous = MailEnabled;
        MailEnabled = enabled;
        MailSwitchStatus = enabled ? "正在启用…" : "正在停用…";

        try
        {
            var result = await _api.CallAsync<SaveMailResultDto>("saveMail", new { enabled })
                .ConfigureAwait(true);

            // 以服务端回的真值为准（skipped = 本来就是这值，不算错）
            var actual = result?.Enabled ?? enabled;
            MailEnabled = actual;
            ShowMailChannelState(actual);
            MailSwitchStatus = result?.Skipped == true
                ? $"没有变化（当前：{(actual ? "已启用" : "已停用")}）"
                : (actual ? "已启用邮件接入" : "已停用邮件接入");

            // 再拉一次平台总览回显（不自己猜服务端状态）
            await LoadPlatformsAsync().ConfigureAwait(true);
        }
        catch (ApiCallException ex) when (ex.Code == 404 && ex.Message.Contains("未知 action", StringComparison.Ordinal))
        {
            MailEnabled = previous;
            ShowMailChannelState(previous);
            MailSwitchAvailable = false;   // 服务端没有这个能力了，别让用户再拨
            MailChannelText = "邮件通道：服务端不支持运行时开关（旧版本）—— 只能改 server.yaml 后重启";
            MailSwitchStatus = $"未保存：服务端没有 saveMail（{ex.Message}）";
        }
        catch (Exception ex)
        {
            MailEnabled = previous;
            ShowMailChannelState(previous);
            MailSwitchStatus = $"保存失败（已还原开关）：{ex.Message}";
        }
    }

    /// <summary>抖音块：配置 + 连通状态。</summary>
    private void ApplyDouyinBlock(DouyinPlatformDto? douyin)
    {
        if (douyin is null)
        {
            DouyinConnected = false;
            DouyinOffline = true;
            DouyinStateText = "服务端未返回抖音配置（旧版本？抖音页需要新服务端）";
            DouyinEndpointText = "/ws/channel";
            return;
        }

        DouyinEnabled = douyin.Enabled;
        DouyinTokenStatus = douyin.TokenConfigured
            ? "已配置（不回显；要换就重填，留空 = 不改）"
            : "未配置（sidecar 与 Server 都必须配上同一个 token）";
        DouyinAllowedSessionsInput = string.Join(", ", douyin.AllowedSessions);

        DouyinSendTimeoutMs = douyin.SendTimeoutMs > 0 ? douyin.SendTimeoutMs : DefaultSendTimeoutMs;
        DouyinMaxTextLength = douyin.MaxTextLength > 0 ? douyin.MaxTextLength : DefaultMaxTextLength;
        DouyinQueryTimeoutMs = douyin.QueryTimeoutMs > 0 ? douyin.QueryTimeoutMs : DefaultQueryTimeoutMs;

        DouyinConnected = douyin.Connected;
        DouyinOffline = !douyin.Connected;
        DouyinStateText = douyin.Connected
            ? (string.IsNullOrWhiteSpace(douyin.SelfId) ? "sidecar 已连入" : $"sidecar 已连入 · selfId {douyin.SelfId}")
            : (douyin.Enabled ? "未连入（sidecar 还没连上来 / 登录态可能过期）" : "未启用（启用后 /ws/channel 才会监听）");
        DouyinEndpointText = string.IsNullOrWhiteSpace(douyin.Endpoint)
            ? "/ws/channel"
            : douyin.Endpoint;
    }

    /// <summary>保存抖音配置：`token` 留空 = 不改；保存成功后清掉输入框里的明文。</summary>
    [RelayCommand]
    private async Task SaveDouyinAsync()
    {
        try
        {
            DouyinStatus = "保存中…";

            var payload = new Dictionary<string, object?>
            {
                ["enabled"] = DouyinEnabled,
                ["allowedSessions"] = SplitSessions(DouyinAllowedSessionsInput),
                ["sendTimeoutMs"] = ToPositiveInt(DouyinSendTimeoutMs, DefaultSendTimeoutMs),
                ["maxTextLength"] = ToPositiveInt(DouyinMaxTextLength, DefaultMaxTextLength),
                ["queryTimeoutMs"] = ToPositiveInt(DouyinQueryTimeoutMs, DefaultQueryTimeoutMs),
            };

            var token = DouyinTokenInput.Trim();
            if (token.Length > 0)
            {
                // 空串 / 不传 = 不改（契约），所以只有真有输入才放进 payload
                payload["token"] = token;
            }

            var result = await _api.CallAsync<SaveDouyinResultDto>("saveDouyin", payload)
                .ConfigureAwait(true);

            DouyinTokenInput = string.Empty;   // 成功地落库了，明文不留在界面里
            DouyinConnected = result?.Connected == true;
            DouyinOffline = !DouyinConnected;
            DouyinStatus = result?.Changed is { Count: > 0 } changed
                ? $"已保存：{string.Join("、", changed)}"
                : "已保存（没有变化）";

            // 回显 enabled / tokenConfigured / connected（别自己猜服务端到底存了什么）
            await LoadPlatformsAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            // 失败：输入框里的 token 保留，用户不用重打
            DouyinStatus = $"保存失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 新建 / 编辑一个邮件账号。**<paramref name="secret"/> 为空 = 保留旧授权码**（合并语义）。
    /// </summary>
    /// <returns>成功与否 + 失败原因（对话框要用它决定是关掉还是留着继续改）。</returns>
    public async Task<(bool Ok, string Error)> SaveMailAccountAsync(
        string? id,
        string address,
        string displayName,
        string imapHost,
        int imapPort,
        bool useSsl,
        string username,
        string? secret,
        bool enabled)
    {
        try
        {
            var payload = new Dictionary<string, object?>
            {
                ["address"] = address,
                ["displayName"] = displayName,
                ["imapHost"] = imapHost,
                ["imapPort"] = imapPort,
                ["useSsl"] = useSsl,
                ["username"] = username,
                ["enabled"] = enabled,
            };

            if (!string.IsNullOrWhiteSpace(id))
            {
                payload["id"] = id;
            }

            if (!string.IsNullOrWhiteSpace(secret))
            {
                payload["secret"] = secret;   // 只有重填了才带（空 = 不改密文）
            }

            await _api.CallAsync<MailAccountDto>("saveMailAccount", payload).ConfigureAwait(true);
            await LoadMailAccountsAsync().ConfigureAwait(true);

            MailStatus = string.IsNullOrWhiteSpace(id) ? $"已新增 {address}" : $"已保存 {address}";
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            MailStatus = $"保存失败：{ex.Message}";
            return (false, ex.Message);
        }
    }

    /// <summary>删除一个账号（配置与密文一起从服务端移除）。</summary>
    public async Task<bool> DeleteMailAccountAsync(MailAccountItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        try
        {
            await _api.CallAsync<object>("deleteMailAccount", new { id = item.Id }).ConfigureAwait(true);
            await LoadMailAccountsAsync().ConfigureAwait(true);

            MailStatus = $"已删除 {item.Address}";
            return true;
        }
        catch (Exception ex)
        {
            MailStatus = $"删除失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 立即收信。<c>syncMail</c> 是"全停再全起"的热重建：只校验这个账号存在，
    /// 实际**所有已启用账号都会重连** —— 文案要说清，免得以为只同步了一个。
    /// </summary>
    public async Task SyncMailAsync(MailAccountItem item)
    {
        ArgumentNullException.ThrowIfNull(item);

        try
        {
            MailStatus = $"正在触发同步（{item.Address}）…";
            await _api.CallAsync<object>("syncMail", new { accountId = item.Id }).ConfigureAwait(true);

            // 先刷新再写文案：LoadMailAccountsAsync 会把 MailStatus 重置成"已加载"
            await LoadMailAccountsAsync().ConfigureAwait(true);
            MailStatus = $"同步已触发（{item.Address}）：服务端会整体重连全部已启用账号，新邮件稍后出现在库里";
        }
        catch (Exception ex)
        {
            MailStatus = $"同步失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 卡片上的启用开关：只带 <c>id</c> + <c>enabled</c>（服务端没给的字段沿用库里的值）。
    /// 失败要把开关拨回去，不然界面在骗人。
    /// </summary>
    public async Task SetMailEnabledAsync(MailAccountItem item, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(item);

        var previous = item.Enabled;
        item.Enabled = enabled;

        try
        {
            await _api.CallAsync<MailAccountDto>("saveMailAccount", new { id = item.Id, enabled })
                .ConfigureAwait(true);

            MailStatus = enabled ? $"已启用 {item.Address}" : $"已停用 {item.Address}";
        }
        catch (Exception ex)
        {
            item.Enabled = previous;
            MailStatus = $"保存失败（已还原开关）：{ex.Message}";
        }
    }

    /// <summary>逗号分隔（半角 / 全角都认）→ 列表。</summary>
    private static List<string> SplitSessions(string text) =>
        text.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    /// <summary>NumberBox 的 double → 正整数字段（0 / NaN 用默认值兜底，别把 0 发下去）。</summary>
    private static int ToPositiveInt(double value, int fallback)
    {
        if (double.IsNaN(value) || value < 1)
        {
            return fallback;
        }

        return value > int.MaxValue ? int.MaxValue : (int)Math.Round(value);
    }
}
