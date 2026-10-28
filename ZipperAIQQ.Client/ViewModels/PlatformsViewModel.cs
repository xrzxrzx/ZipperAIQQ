using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZipperAIQQ.Client.Dto;
using ZipperAIQQ.Client.Services;

namespace ZipperAIQQ.Client.ViewModels;

/// <summary>
/// 顶部「接入状态」里的一个点（**只有邮件 / 抖音**，QQ 不在这排里——
/// QQ 的连通状态归状态页，这页只管第三方平台）。
/// </summary>
/// <remarks>
/// 文案与红字都在构造时算好：模板只负责显示，不在 XAML 里做判断。
/// </remarks>
public sealed class OverviewChipItem
{
    public OverviewChipItem(string label, bool ok, string statusText, string errorText)
    {
        Label = label;
        Connected = ok;
        StatusText = statusText;
        ErrorText = errorText;
    }

    public string Label { get; }

    /// <summary>绿点：邮件看"是否启用"，抖音看"是否已连入"。</summary>
    public bool Connected { get; }

    /// <summary>x:Load 的第二根笔：不健康时显示灰点。</summary>
    public bool Offline => !Connected;

    public string StatusText { get; }

    public string ErrorText { get; }

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);
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

    /// <summary>卡片副标题：**没有显示名就不显示**（不再用「未命名账号」占位）。</summary>
    public bool HasDisplayName => !string.IsNullOrWhiteSpace(DisplayName);

    public string SubtitleText => DisplayName;

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

    /// <summary>上一次拿到的 mail / douyin 块：开关与 chips 都要用它算文案（null = 旧服务端没给）。</summary>
    private MailPlatformDto? _mailBlock;
    private DouyinPlatformDto? _douyinBlock;

    public PlatformsViewModel(ServerApiClient api)
    {
        ArgumentNullException.ThrowIfNull(api);
        _api = api;
    }

    /// <summary>顶部接入状态（邮件 / 抖音两块，不用 getPlatforms.platforms）。</summary>
    public ObservableCollection<OverviewChipItem> OverviewChips { get; } = [];

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

    /// <summary>汇总是否再交人设 agent 润色（<c>getPlatforms.mail.personaDigest</c>）。</summary>
    [ObservableProperty]
    public partial bool MailPersonaDigest { get; set; }

    /// <summary>人设润色开关的状态位。</summary>
    [ObservableProperty]
    public partial string MailPersonaDigestStatus { get; set; } = string.Empty;

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

    /// <summary>抖音自己的账号 ID（顶部 chip 用；没连上时为空）。</summary>
    [ObservableProperty]
    public partial string DouyinSelfId { get; set; } = string.Empty;

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

            // 账号数 / lastError 会影响顶部「邮件」那块，顺手对齐
            RebuildOverviewChips();
        }
        catch (Exception ex)
        {
            MailStatus = $"加载账号失败：{ex.Message}";
            MailEmptyHint = MailAccounts.Count == 0 ? "账号列表加载失败（原因见上一行）" : string.Empty;
        }
    }

    /// <summary>拉平台块（mail / douyin）；**失败保留旧值**，只更新状态文案。</summary>
    private async Task LoadPlatformsAsync()
    {
        PlatformsDto? data;
        try
        {
            data = await _api.CallAsync<PlatformsDto>("getPlatforms").ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            OverviewStatus = $"接入状态加载失败：{ex.Message}（邮件账号不受影响；抖音配置需要新服务端）";

            // 别让抖音页停在"还没加载"这种含糊状态上 —— 说清是"没拿到"而不是"没连上"
            if (OverviewChips.Count == 0)
            {
                DouyinStateText = "抖音状态未知（getPlatforms 失败，见上方提示）";
                MailChannelText = "邮件通道：状态未知（getPlatforms 失败，不影响账号增删改）";
            }

            return;
        }

        if (data is null)
        {
            OverviewStatus = "接入状态：服务端没有返回数据。";
            return;
        }

        // 顶部两块只认 mail / douyin；getPlatforms.platforms（含 QQ）留给别的页用，这里不摆
        OverviewStatus = data.Mail is null && data.Douyin is null
            ? "服务端没返回邮件 / 抖音配置（旧版本？）—— 下面两个 Tab 仍可各自操作"
            : string.Empty;

        ApplyMailBlock(data.Mail);
        ApplyDouyinBlock(data.Douyin);
        RebuildOverviewChips();
    }

    /// <summary>邮件块：通道开关 + 人设润色的真值 + 账号兜底（账号仍以 listMailAccounts 为准）。</summary>
    private void ApplyMailBlock(MailPlatformDto? mail)
    {
        _mailBlock = mail;

        if (mail is null)
        {
            // 旧服务端：连 mail 块都没有 → 不知道真值，也不许假装知道
            MailSwitchAvailable = false;
            MailChannelText = "邮件通道：服务端未返回运行状态（旧版本）—— 开关不可用，只能改 server.yaml 后重启";
            return;
        }

        MailSwitchAvailable = true;
        MailEnabled = mail.Enabled;
        MailPersonaDigest = mail.PersonaDigest;
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

    /// <summary>
    /// 顶部两块（邮件 / 抖音）的文案与状态点。**不使用 <c>getPlatforms.platforms</c>** ——
    /// 那里面是 QQ 之类的通道，本页只关心邮件与抖音。
    /// </summary>
    private void RebuildOverviewChips()
    {
        if (_mailBlock is null && _douyinBlock is null)
        {
            return;   // 一次都没拿到平台块：保持空，别编状态
        }

        OverviewChips.Clear();
        OverviewChips.Add(BuildMailChip());
        OverviewChips.Add(BuildDouyinChip());
    }

    private OverviewChipItem BuildMailChip()
    {
        if (_mailBlock is null)
        {
            return new OverviewChipItem("邮件", false, "状态未知（旧服务端）", string.Empty);
        }

        var count = MailAccounts.Count > 0 ? MailAccounts.Count : _mailBlock.Accounts.Count;
        var status = MailEnabled ? $"已启用（{count} 个账号）" : "已停用";

        // 任一账号有 lastError → 红灯提示（只取第一条，别把顶栏堆成日志）
        var firstError = MailAccounts.FirstOrDefault(a => a.HasError)?.LastError
            ?? _mailBlock.Accounts.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a.LastError))?.LastError
            ?? string.Empty;

        return new OverviewChipItem(
            "邮件",
            MailEnabled,
            status,
            string.IsNullOrWhiteSpace(firstError) ? string.Empty : $"同步失败：{firstError}");
    }

    private OverviewChipItem BuildDouyinChip()
    {
        if (_douyinBlock is null)
        {
            return new OverviewChipItem("抖音", false, "状态未知（旧服务端）", string.Empty);
        }

        if (!DouyinEnabled)
        {
            return new OverviewChipItem("抖音", false, "未启用", string.Empty);
        }

        return DouyinConnected
            ? new OverviewChipItem(
                "抖音",
                true,
                string.IsNullOrWhiteSpace(DouyinSelfId) ? "已连入" : $"已连入（selfId={DouyinSelfId}）",
                string.Empty)
            : new OverviewChipItem("抖音", false, "未连入（等待 sidecar）", string.Empty);
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

            // 以服务端回的真值为准（skipped = 本来就是这值，不算错）。
            // 同一次响应也会带 personaDigest —— 一起对齐，别让它与真实配置脱节。
            var actual = result?.Enabled ?? enabled;
            MailEnabled = actual;
            MailPersonaDigest = result?.PersonaDigest ?? MailPersonaDigest;
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

    /// <summary>
    /// 「汇总再经人格润色」开关（<c>saveMail { personaDigest }</c>）。
    /// 与启用开关同一套处理：失败拨回 + 可见错误；旧服务端（404）直接说明"不支持"。
    /// </summary>
    public async Task SetMailPersonaDigestAsync(bool enabled)
    {
        if (enabled == MailPersonaDigest)
        {
            return;   // 没变就别打扰服务端
        }

        var previous = MailPersonaDigest;
        MailPersonaDigest = enabled;
        MailPersonaDigestStatus = "保存中…";

        try
        {
            var result = await _api.CallAsync<SaveMailResultDto>("saveMail", new { personaDigest = enabled })
                .ConfigureAwait(true);

            MailPersonaDigest = result?.PersonaDigest ?? enabled;
            MailEnabled = result?.Enabled ?? MailEnabled;
            ShowMailChannelState(MailEnabled);
            MailPersonaDigestStatus = result?.Skipped == true
                ? $"没有变化（当前：{(MailPersonaDigest ? "已开启" : "已关闭")}）"
                : (MailPersonaDigest
                    ? "已开启：汇总先交人设 agent 润色后再发"
                    : "已关闭：直接发汇总文本");

            await LoadPlatformsAsync().ConfigureAwait(true);
        }
        catch (ApiCallException ex) when (ex.Code == 404 && ex.Message.Contains("未知 action", StringComparison.Ordinal))
        {
            MailPersonaDigest = previous;
            MailSwitchAvailable = false;
            MailChannelText = "邮件通道：服务端不支持运行时开关（旧版本）—— 只能改 server.yaml 后重启";
            MailPersonaDigestStatus = $"未保存：服务端没有 saveMail（{ex.Message}）";
        }
        catch (Exception ex)
        {
            MailPersonaDigest = previous;
            MailPersonaDigestStatus = $"保存失败（已还原开关）：{ex.Message}";
        }
    }

    /// <summary>抖音块：配置 + 连通状态。</summary>
    private void ApplyDouyinBlock(DouyinPlatformDto? douyin)
    {
        _douyinBlock = douyin;

        if (douyin is null)
        {
            DouyinConnected = false;
            DouyinOffline = true;
            DouyinSelfId = string.Empty;
            DouyinStateText = "服务端未返回抖音配置（旧版本？抖音页需要新服务端）";
            DouyinEndpointText = "/ws/channel";
            return;
        }

        DouyinEnabled = douyin.Enabled;
        DouyinSelfId = douyin.SelfId ?? string.Empty;
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
