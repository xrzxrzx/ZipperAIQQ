using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Failures;
using ZipperAIQQ.Channel.Reliability;
using ZipperAIQQ.Channel.Filtering;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Server.Channels;

/// <summary>
/// 抖音通道：sidecar 通过 <c>/ws/channel</c> 连入，本类负责协议收发与入站分发。
/// </summary>
/// <remarks>
/// <para>
/// <b>职责边界</b>（设计 §2.3）：只做「帧 ↔ 平台无关模型」的翻译与连接管理。
/// **不落库、不去重** —— 去重靠 <c>Messages</c> 的唯一索引 + 路由层预检（设计 §3.4）：
/// 通道层做去重会多一份状态要维护（重启就丢），且和唯一索引的口径可能不一致。
/// </para>
/// <para>
/// <b>连接可替换</b>：sidecar 重连时新连接替换旧连接，<see cref="ReceiveAsync"/> 不中断 ——
/// 事件先进进程内缓冲通道，消费方（<see cref="CompositeMessageChannel"/> → <c>MessageRouter</c>）
/// 看不到连接抖动。
/// </para>
/// <para>
/// <b>失败要响</b>：<see cref="SendAsync"/> 超时 / 未连接 / sidecar 报错一律抛异常，
/// 由上层转成「回填给模型的失败原因」（FR-2.3）。静默吞掉会让模型以为发出去了。
/// </para>
/// </remarks>
public sealed class DouyinChannel : IMessageChannel, IContactDirectory, IChannelLogin, IAsyncDisposable, IRecoverableChannel
{
    /// <summary>平台标识，与 <see cref="ChatRef.Platform"/>、<c>session</c> 前缀一致。</summary>
    public const string PlatformName = "douyin";

    /// <summary>等旧接收循环收尾的上限：换连接/停服不能因为一条倔强的连接而挂住。</summary>
    private static readonly TimeSpan ReceiveLoopExitTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// 运行参数。**可热替换**（客户端在「第三方平台接入」页改白名单 / 超时后立即生效）。
    /// </summary>
    private volatile DouyinOptions _options;
    private readonly IChatFilter _filter;
    private readonly ILogger _logger;

    /// <summary>
    /// 入站事件缓冲（单读者 = <c>MessageRouter</c> 那一个循环）。
    /// </summary>
    /// <remarks>
    /// ⚠️ 必须写全 <c>System.Threading.Channels.Channel</c>：本文件命名空间在
    /// <c>ZipperAIQQ.Server.Channels</c> 下，而项目里还有个 <c>ZipperAIQQ.Channel</c> 命名空间，
    /// 裸写 <c>Channel</c> 会被解析到后者（CS0234）。
    /// </remarks>
    private readonly Channel<InboundEvent> _inbox =
        System.Threading.Channels.Channel.CreateUnbounded<InboundEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    /// <summary>出站 <c>ack</c> 配对表（requestId → 等待者）。超时 / 失败都会移除，不泄漏。</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DouyinAck>> _pendingAcks =
        new(StringComparer.Ordinal);

    /// <summary>查询 <c>result</c> 配对表（requestId → 等待者）。</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<DouyinQueryResult>> _pendingResults =
        new(StringComparer.Ordinal);

    /// <summary>换连接是「停旧 + 起新」，必须串行，否则两次重连会把接收循环叠起来。</summary>
    private readonly SemaphoreSlim _attachGate = new(1, 1);

    private IDouyinConnection? _connection;
    private CancellationTokenSource? _connectionCts;
    private Task? _receiveLoop;

    /// <summary>由 <c>hello</c> 帧写入；读方可能在别的线程，故用 <c>volatile</c> 保证可见性。</summary>
    private volatile string? _selfId;

    // ── 登录管理（2026-10-28）：客户端可直接发起登录 / 退出 / 看二维码 ──
    private volatile string _loginState = DouyinLoginState.Unknown;
    private volatile string? _loginMessage;
    private string? _loginQrBase64;
    private DateTimeOffset? _loginQrAt;

    // ── 浏览器底座（2026-11-20 B1）：驱动身份 / 登录挑战 / 页面体征 ──

    /// <summary>已接受 sidecar 自报的驱动（`im` / `web`）；未握手时为 `null`。</summary>
    private volatile string? _driver;

    /// <summary>最近一次被拒的 sidecar 说明（状态页与日志用）。</summary>
    private volatile string? _driverRejectNote;

    /// <summary>当前等待回填的登录挑战。</summary>
    private volatile DouyinLoginChallenge? _challenge;

    /// <summary>最近一次浏览器体征。</summary>
    private volatile DouyinBrowserState? _browserState;

    /// <summary>连接对端（日志用；不含凭据）。</summary>
    private volatile string? _transportPeer;

    private bool _disposed;

    /// <summary>构造通道（不连接，等 <see cref="AttachAsync"/>）。</summary>
    /// <param name="options">sidecar 配置（超时、分段阈值）。</param>
    /// <param name="filter">
    /// 会话过滤（与 QQ 同一套，按 <c>ChatRef.Platform</c> 取 <c>access.douyin.whitelist/blacklist</c>）。
    /// </param>
    /// <param name="logger">日志。</param>
    /// <remarks>
    /// 2026-10-28：**删掉抖音私有的 <c>allowedSessions</c>**，改用与 QQ 完全一致的
    /// <see cref="IChatFilter"/>（配置入口在「人员与名单」）。同一个概念两处配置必然漂移 ——
    /// 用户实测就问到了"这个不是能在人员与名单里配吗"。
    /// </remarks>
    public DouyinChannel(DouyinOptions options, IChatFilter filter, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options;
        _filter = filter;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Platform => PlatformName;

    /// <inheritdoc />
    /// <remarks>由 <c>hello</c> 帧驱动：sidecar 没连上（或未登录）时为 <c>null</c>。</remarks>
    public string? SelfId => _selfId;

    /// <inheritdoc />
    /// <remarks>只打日志/持久化用；订阅方不要在这里做重活（它在接收循环里触发）。</remarks>
    public event Action<string>? SelfIdChanged;

    /// <inheritdoc />
    /// <remarks>抖音第一版支持文本 / 图片 / @ / 引用（设计 §1.1）。</remarks>
    public ChannelCapabilities Capabilities =>
        ChannelCapabilities.Text
        | ChannelCapabilities.Image
        | ChannelCapabilities.Mention
        | ChannelCapabilities.Reply;

    /// <summary>当前是否有 sidecar 连接（健康检查 / 断线诊断用）。</summary>
    public bool IsConnected => _connection is not null;

    /// <summary>登录状态（见 <see cref="DouyinLoginState"/>）。</summary>
    public string LoginState => _loginState;

    /// <summary>登录相关的补充说明（出错原因 / 提示）。</summary>
    public string? LoginMessage => _loginMessage;

    /// <summary>最近一次二维码（PNG base64；没有则为 null）。</summary>
    public string? LoginQrBase64 => _loginQrBase64;

    /// <summary>最近一次二维码的生成时间。</summary>
    public DateTimeOffset? LoginQrAt => _loginQrAt;

    /// <summary>已接受 sidecar 的驱动（`im` / `web`）；没握手时为 `null`。</summary>
    public string? Driver => _driver;

    /// <summary>最近一次被拒连接的说明（驱动不匹配时非空）。</summary>
    public string? DriverRejectNote => _driverRejectNote;

    /// <summary>连接对端（`ip:port` / `unknown`）。</summary>
    public string? TransportPeer => _transportPeer;

    /// <summary>当前等待用户回填的登录挑战（短信 / 滑块）；没有则 `null`。</summary>
    public DouyinLoginChallenge? Challenge => _challenge;

    /// <summary>最近一次浏览器体征（`browser.state` 帧）。</summary>
    public DouyinBrowserState? BrowserState => _browserState;

    /// <summary>
    /// 让 sidecar 开始登录（客户端点「登录」时调）。
    /// </summary>
    /// <remarks>
    /// 走的是同一条 <c>/ws/channel</c>：sidecar 收到 <c>login.start</c> 后调 douyin-im 登录，
    /// 二维码通过 <c>login.qr</c> 回来 —— 这条链路不需要开新端口。
    /// </remarks>
    public async Task RequestLoginAsync(CancellationToken ct = default)
    {
        var connection = RequireConnection();
        _loginQrBase64 = null;
        _loginQrAt = null;
        _loginState = DouyinLoginState.WaitingQr;
        _loginMessage = null;
        _challenge = null;

        await connection
            .SendAsync(DouyinProtocol.BuildLoginCommand(DouyinFrameTypes.LoginStart), ct)
            .ConfigureAwait(false);

        _logger.Information("已请求 sidecar 发起抖音登录");
    }

    /// <summary>
    /// 退出抖音登录（**只清登录态，sidecar 进程继续跑** —— 用户 2026-10-28 选定的语义）。
    /// </summary>
    public async Task LogoutAsync(CancellationToken ct = default)
    {
        var connection = RequireConnection();

        await connection
            .SendAsync(DouyinProtocol.BuildLoginCommand(DouyinFrameTypes.LoginLogout), ct)
            .ConfigureAwait(false);

        _loginQrBase64 = null;
        _loginQrAt = null;
        _loginState = DouyinLoginState.LoggedOut;
        _loginMessage = null;
        _challenge = null;
        ClearSelfId();

        _logger.Information("已请求 sidecar 退出抖音登录");
    }

    /// <summary>
    /// 回填登录挑战答案（短信码 / 滑块偏移），下发给 sidecar。
    /// </summary>
    /// <remarks>
    /// 三条拒绝路径都要响（不静默丢）：没连上、没有待回填的挑战、类型不匹配 ——
    /// 用户点完「提交」却毫无反应，比报错更难受。
    /// </remarks>
    public async Task SubmitChallengeAsync(string kind, string value, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var connection = RequireConnection();
        var pending = _challenge;

        if (pending is null)
        {
            throw new InvalidOperationException(
                "当前没有等待回填的登录挑战（可能已自动通过或已超时）；刷新登录状态看看。");
        }

        if (!string.Equals(pending.Kind, kind.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"挑战类型不匹配：当前在等 {pending.Kind}，收到 {kind}。");
        }

        await connection
            .SendAsync(DouyinProtocol.BuildChallengeAnswer(pending.Kind, value.Trim()), ct)
            .ConfigureAwait(false);

        // 已经交出去了：先清掉本地挑战，避免用户连点两次把同一个答案发两遍
        _challenge = null;
        _logger.Information("已回填抖音登录挑战：kind={Kind}", pending.Kind);
    }

    /// <summary>
    /// 热替换运行参数（决策七：客户端在「第三方平台接入」页保存后立即生效）。
    /// </summary>
    /// <remarks>
    /// 只影响"下一次判断"：白名单立即生效；超时 / 文本上限对**已在等 ack** 的那一帧不追溯
    /// （它已经用旧值算出了超时），这是有意的 —— 不为了配置变更去打断进行中的发送。
    /// </remarks>
    public void UpdateOptions(DouyinOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _options = options;
        _logger.Information(
            "抖音通道参数已更新：驱动 {Driver} / 白名单 {White} 条 / 黑名单 {Black} 条 / 发送超时 {Send}ms / 文本上限 {Text} 字",
            options.Driver,
            _filter.WhitelistCount(PlatformName),
            _filter.BlacklistCount(PlatformName),
            options.SendTimeoutMs,
            options.MaxTextLength);
    }

    // IsSessionAllowed 已删除（2026-10-28）：改用 IChatFilter，与 QQ 同一套白/黑名单

    /// <summary>
    /// 空实现：抖音是**反向连接**，连接由 sidecar 发起（<see cref="AttachAsync"/>）。
    /// </summary>
    /// <remarks>保留给 <see cref="IMessageChannel"/> 契约（路由层启动时会调一次）。</remarks>
    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>正常取消时以 <see cref="OperationCanceledException"/> 结束（与接口约定一致）。</remarks>
    public async IAsyncEnumerable<InboundEvent> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var inboundEvent in _inbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return inboundEvent;
        }
    }

    /// <summary>
    /// 接入一条新的 sidecar 连接（重连时**替换**旧连接）。
    /// </summary>
    /// <param name="connection">新连接。</param>
    /// <param name="ct">取消令牌。</param>
    /// <remarks>
    /// 先切到新连接再收尾旧连接：这样收尾期间到达的新连接帧不会丢。
    /// 旧连接会被取消 + 关闭，接收循环退出；等它退出有 2s 上限，不会挂住 <c>/ws/channel</c> 的接入。
    /// </remarks>
    public async Task AttachAsync(IDouyinConnection connection, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _attachGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var previous = _connection;
            var previousCts = _connectionCts;
            var previousLoop = _receiveLoop;

            var cts = new CancellationTokenSource();
            _connection = connection;
            _connectionCts = cts;
            _receiveLoop = Task.Run(() => RunReceiveLoopAsync(connection, cts.Token), CancellationToken.None);

            if (connection is IDouyinTransport transport)
            {
                _transportPeer = $"{transport.Peer}（{transport.Kind}）";
            }

            if (previous is null)
            {
                _logger.Information("抖音 sidecar 已连接（peer={Peer}）", _transportPeer ?? "unknown");
                return;
            }

            _logger.Information("抖音 sidecar 重连：替换旧连接");

            previousCts?.Cancel();
            await CloseQuietlyAsync(previous, ct).ConfigureAwait(false);
            await WaitForExitAsync(previousLoop).ConfigureAwait(false);
            previousCts?.Dispose();
        }
        finally
        {
            _attachGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SendAsync(OutgoingMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // 运行时开关（S3-F 补）：关掉后即使老连接还在，也不许再从抖音发出去。
        // 抛异常而不是静默成功 —— 上层会把原因回填给模型（FR-2.3）。
        if (!_options.Enabled)
        {
            throw new InvalidOperationException(
                "抖音接入已停用（客户端「第三方平台接入」里可以打开）。");
        }

        // 路由层把 qq 会话发到抖音通道 = 路由 bug，宁可在通道层炸掉，也不要发到奇怪的地方。
        if (!string.Equals(message.Chat.Platform, PlatformName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"抖音通道只能发送 douyin 会话，收到 {message.Chat.Platform}。",
                nameof(message));
        }

        var connection = RequireConnection();

        var batches = BuildBatches(message.Segments, _options.MaxTextLength);
        if (batches.Count == 0)
        {
            throw new ContentInvalidException(
                "douyin",
                "消息没有可发送的内容片段（引用由 replyTo 承载，不单独成帧）。");
        }

        var replyTo = string.IsNullOrEmpty(message.ReplyToMessageId)
            ? message.Segments.OfType<ReplySegment>().Select(segment => segment.MessageId).FirstOrDefault()
            : message.ReplyToMessageId;

        var session = message.Chat.SessionId;

        for (var index = 0; index < batches.Count; index++)
        {
            var requestId = NewRequestId();

            // 引用只跟首帧：分段消息的第二段再带一次引用，对方会看到「回复了两次」。
            var json = DouyinProtocol.BuildOutbound(
                session,
                batches[index],
                index == 0 ? replyTo : null,
                requestId);

            await SendAndAwaitAckAsync(connection, requestId, json, index + 1, batches.Count, ct)
                .ConfigureAwait(false);
        }

        _logger.Information(
            "抖音消息已发出：会话 {Session}，共 {Count} 帧",
            session,
            batches.Count);
    }

    // ─────────────────────────── IContactDirectory ───────────────────────────

    /// <inheritdoc />
    /// <remarks>走 <c>query.contacts</c> + <c>result</c> 配对；**取不到抛异常**，不返回空列表冒充"没有好友"。</remarks>
    public async Task<IReadOnlyList<ContactInfo>> GetFriendsAsync(CancellationToken ct = default)
    {
        var result = await QueryAsync(DouyinFrameTypes.QueryContacts, "好友", ct).ConfigureAwait(false);
        return result.Contacts;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken ct = default)
    {
        var result = await QueryAsync(DouyinFrameTypes.QueryGroups, "群", ct).ConfigureAwait(false);
        return result.Groups;
    }

    /// <inheritdoc />
    /// <remarks>抖音 sidecar 只有群列表、没有单个群详情查询（设计 §3.5）。</remarks>
    public Task<GroupInfo?> GetGroupAsync(string groupId, CancellationToken ct = default) =>
        throw NotSupported("查询单个群详情");

    /// <inheritdoc />
    public Task<IReadOnlyList<GroupMemberInfo>> GetGroupMembersAsync(
        string groupId,
        CancellationToken ct = default) =>
        throw NotSupported("查询群成员");

    /// <inheritdoc />
    public Task SetSignatureAsync(string text, CancellationToken ct = default) =>
        throw NotSupported("修改个性签名");

    /// <inheritdoc />
    public Task SendPokeAsync(string userId, string? groupId, CancellationToken ct = default) =>
        throw NotSupported("戳一戳");

    /// <inheritdoc />
    public Task<System.Text.Json.Nodes.JsonNode?> InvokeApiAsync(
        string action,
        System.Text.Json.Nodes.JsonObject? parameters,
        CancellationToken ct = default) =>
        throw NotSupported($"直接调用平台 API（{action}）");

    // ─────────────────────────── 生命周期 ───────────────────────────

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await _attachGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var connection = _connection;
            var cts = _connectionCts;
            var loop = _receiveLoop;

            _connection = null;
            _connectionCts = null;
            _receiveLoop = null;

            cts?.Cancel();

            if (connection is not null)
            {
                await CloseQuietlyAsync(connection, CancellationToken.None).ConfigureAwait(false);
            }

            await WaitForExitAsync(loop).ConfigureAwait(false);
            cts?.Dispose();
        }
        finally
        {
            _attachGate.Release();
            _attachGate.Dispose();
        }

        // 让 ReceiveAsync 正常结束，而不是永远挂着
        _inbox.Writer.TryComplete();
    }

    // ─────────────────────────── 接收 ───────────────────────────

    /// <summary>一条连接的接收循环：解析帧 → 分发（hello / inbound / ack / result）。</summary>
    private async Task RunReceiveLoopAsync(IDouyinConnection connection, CancellationToken ct)
    {
        try
        {
            await foreach (var json in connection.ReceiveAsync(ct).ConfigureAwait(false))
            {
                await HandleFrameAsync(json, connection, ct).ConfigureAwait(false);
            }

            _logger.Information("抖音连接已关闭（sidecar 断开），等待重连");
        }
        catch (OperationCanceledException)
        {
            // 换连接 / 停服：正常路径
        }
        catch (Exception ex)
        {
            // 一条连接坏掉不影响别的平台（验收 7），也不影响本通道等待重连
            _logger.Warning(ex, "抖音连接接收失败，等待 sidecar 重连");
        }
        finally
        {
            // 只有「当前连接」才有资格把自己清掉：重连时旧循环晚退出不能把新连接抹了
            if (ReferenceEquals(Volatile.Read(ref _connection), connection))
            {
                _connection = null;
            }
        }
    }

    /// <summary>处理一帧；**未知类型 / 畸形帧只记日志，绝不抛出**。</summary>
    private async Task HandleFrameAsync(string json, IDouyinConnection connection, CancellationToken ct)
    {
        if (!DouyinProtocol.TryParseFrame(json, out var frame, out var error) || frame is null)
        {
            _logger.Warning("抖音帧解析失败：{Reason}", error);
            return;
        }

        switch (frame.Type)
        {
            case DouyinFrameTypes.LoginQr:
            {
                var qr = DouyinProtocol.ParseLoginQr(frame.Data);
                if (qr is null)
                {
                    _logger.Debug("抖音 login.qr 帧体为空，忽略");
                    break;
                }

                _loginQrBase64 = qr.QrCodeBase64;
                _loginQrAt = DateTimeOffset.UtcNow;
                _loginState = DouyinLoginState.WaitingQr;
                _logger.Information("收到抖音登录二维码（长度 {Length}），等待扫码", qr.QrCodeBase64.Length);
                break;
            }

            case DouyinFrameTypes.LoginState:
            {
                var state = DouyinProtocol.ParseLoginState(frame.Data);
                if (state is null)
                {
                    _logger.Debug("抖音 login.state 帧体为空，忽略");
                    break;
                }

                _loginState = state.State;
                _loginMessage = state.Message;

                if (state.State == DouyinLoginState.Online)
                {
                    // 登录成功：二维码与挑战都失效，清掉避免界面还挂着旧码 / 旧弹窗
                    _loginQrBase64 = null;
                    _loginQrAt = null;
                    _challenge = null;
                    UpdateSelfId(state.SelfId);
                    _logger.Information("抖音登录成功：{Nickname}（selfId={SelfId}）", state.Nickname, state.SelfId);
                }
                else if (state.State == DouyinLoginState.LoggedOut)
                {
                    _loginQrBase64 = null;
                    _loginQrAt = null;
                    _challenge = null;
                    ClearSelfId();
                    _logger.Information("抖音已退出登录（sidecar 仍在运行）");
                }
                else if (state.State == DouyinLoginState.Error)
                {
                    _logger.Warning("抖音登录出错：{Message}", state.Message);
                }

                break;
            }

            case DouyinFrameTypes.LoginChallenge:
            {
                var challenge = DouyinProtocol.ParseChallenge(frame.Data);
                if (challenge is null)
                {
                    _logger.Warning("抖音 login.challenge 帧体缺 kind 或类型不认识，已忽略（交回人工）");
                    break;
                }

                _challenge = challenge;
                _logger.Information(
                    "抖音登录需要人工介入：kind={Kind} 提示={Hint}",
                    challenge.Kind,
                    challenge.Hint ?? "-");
                break;
            }

            case DouyinFrameTypes.BrowserState:
            {
                var state = DouyinProtocol.ParseBrowserState(frame.Data);
                if (state is null)
                {
                    _logger.Debug("抖音 browser.state 帧体缺 page，已忽略");
                    break;
                }

                var previous = _browserState;
                _browserState = state;

                // 只在"状态变了"时打 Info，其余时候 Debug（这个帧每 15 分钟就会来一次）
                if (!string.Equals(previous?.Page, state.Page, StringComparison.Ordinal))
                {
                    _logger.Information(
                        "抖音浏览器状态：{Page}（内存 {MemoryMb}MB，上次动作：{Action}）{Note}",
                        state.Page,
                        state.MemoryMb,
                        state.LastAction ?? "-",
                        string.IsNullOrWhiteSpace(state.Note) ? string.Empty : $"；{state.Note}");
                }
                else
                {
                    _logger.Debug("抖音浏览器体征：{Page} / {MemoryMb}MB", state.Page, state.MemoryMb);
                }

                break;
            }

            case DouyinFrameTypes.Hello:
            {
                var hello = DouyinProtocol.ParseHello(frame.Data);
                if (hello is null)
                {
                    _logger.Warning("抖音 hello 帧体为空，忽略");
                    break;
                }

                // 驱动闸门（实施方案 B1）：两个 sidecar 共用同一套帧，靠自报 driver 区分。
                // 不匹配就**拒掉这条连接**（不是只记日志）：否则旧 sidecar 连上来会把新 sidecar 顶掉，
                // 表现成"刚切过去就又开始 10509"。老 sidecar 不带 driver → 按 im 处理。
                var announced = DouyinDriver.Normalize(hello.Driver) ?? DouyinDriver.Im;
                var expected = DouyinDriver.Normalize(_options.Driver) ?? DouyinDriver.Im;

                if (!string.Equals(announced, expected, StringComparison.Ordinal))
                {
                    _driverRejectNote =
                        $"已拒绝 {announced} 底座的 sidecar（自报 driver={announced}，当前 douyin.driver={expected}）";
                    _logger.Warning(
                        "拒绝抖音 sidecar：自报驱动 {Announced}，当前配置 {Expected}（peer={Peer}）；" +
                        "要切换底座请改 douyin.driver",
                        announced,
                        expected,
                        _transportPeer ?? "unknown");

                    await RejectConnectionAsync(connection).ConfigureAwait(false);
                    return;
                }

                _driver = announced;
                _driverRejectNote = null;
                _logger.Information("抖音 sidecar 驱动已确认：{Driver}（peer={Peer}）", announced, _transportPeer ?? "unknown");

                if (hello.Version is { Length: > 0 } version
                    && !string.Equals(version, DouyinProtocol.Version, StringComparison.Ordinal))
                {
                    _logger.Warning(
                        "抖音 sidecar 协议版本为 {Version}，Server 期望 {Expected}，仍按兼容模式继续",
                        version,
                        DouyinProtocol.Version);
                }

                UpdateSelfId(hello.SelfId);
                break;
            }

            case DouyinFrameTypes.Inbound:
            {
                // 运行时开关（S3-F 补）：停用时直接丢，不进库、不进模型。
                // 为什么在通道层：与接收过滤同口径（FR-17.3），且关掉之后连"占存储"都不该发生。
                if (!_options.Enabled)
                {
                    _logger.Debug("抖音接入已停用，丢弃入站帧");
                    break;
                }

                var message = DouyinProtocol.ParseInbound(frame.Data, PlatformName);
                if (message is null)
                {
                    // stranger（设计 §4.1 明确丢弃）与畸形帧都走这里：Debug 级别，不刷屏
                    _logger.Debug("抖音入站帧未映射（陌生人盒 / 缺字段 / 未知形状），已丢弃");
                    break;
                }

                // H-5：名单判定上移到路由层；通道一律投递，路由负责留档与"是否处理"。
                await _inbox.Writer.WriteAsync(message, ct).ConfigureAwait(false);
                break;
            }

            case DouyinFrameTypes.Ack:
            {
                var ack = DouyinProtocol.ParseAck(frame.Data);
                if (ack?.RequestId is not { } requestId)
                {
                    _logger.Warning("抖音 ack 帧缺少 requestId，无法配对");
                    break;
                }

                if (_pendingAcks.TryRemove(requestId, out var completion))
                {
                    completion.TrySetResult(ack);
                }
                else
                {
                    // 超时后迟到的 ack：属于正常竞态，Debug 就够
                    _logger.Debug("收到无人等待的抖音 ack（{RequestId}），已忽略", requestId);
                }

                break;
            }

            case DouyinFrameTypes.Result:
            {
                var result = DouyinProtocol.ParseResult(frame.Data);
                if (result?.RequestId is not { } requestId)
                {
                    _logger.Warning("抖音 result 帧缺少 requestId，无法配对");
                    break;
                }

                if (_pendingResults.TryRemove(requestId, out var completion))
                {
                    completion.TrySetResult(result);
                }
                else
                {
                    _logger.Debug("收到无人等待的抖音 result（{RequestId}），已忽略", requestId);
                }

                break;
            }

            case DouyinFrameTypes.Pong:
                break;

            default:
                _logger.Debug("未知抖音帧类型 {Type}，已忽略", frame.Type);
                break;
        }
    }

    /// <summary>hello 驱动 SelfId：值变了才触发事件（避免每次重连都刷一遍持久化）。</summary>
    private void UpdateSelfId(string? selfId)
    {
        if (string.IsNullOrWhiteSpace(selfId)
            || string.Equals(_selfId, selfId, StringComparison.Ordinal))
        {
            return;
        }

        _selfId = selfId;
        _logger.Information("抖音自身账号已确认：{SelfId}", selfId);
        SelfIdChanged?.Invoke(selfId);
    }

    /// <summary>
    /// 清掉 selfId（退出登录时用）。
    /// </summary>
    /// <remarks>
    /// 不能复用 <see cref="UpdateSelfId"/>：它对 <c>null</c> 是"忽略"（防止被空帧冲掉），
    /// 结果退出登录后 selfId 一直留着，界面看起来像"还登录着"（2026-10-28 实测）。
    /// </remarks>
    private void ClearSelfId()
    {
        if (string.IsNullOrEmpty(_selfId))
        {
            return;
        }

        _selfId = null;
        _logger.Information("抖音 selfId 已清空（退出登录）");
    }

    // ─────────────────────────── 发送 / 查询 ───────────────────────────

    /// <summary>发一帧并等 ack；超时/失败抛异常（不吞）。</summary>
    private async Task SendAndAwaitAckAsync(
        IDouyinConnection connection,
        string requestId,
        string json,
        int sequence,
        int total,
        CancellationToken ct)
    {
        var completion = new TaskCompletionSource<DouyinAck>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // 先登记再发：ack 可能比 SendAsync 的返回还快
        _pendingAcks[requestId] = completion;
        try
        {
            try
            {
                await connection.SendAsync(json, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException
                                          or ObjectDisposedException
                                          or System.Net.Sockets.SocketException
                                          or IOException)
            {
                throw new ChannelUnavailableException("douyin", $"抖音连接不可用：{ex.Message}", ex);
            }

            var timeout = TimeSpan.FromMilliseconds(Math.Max(1, _options.SendTimeoutMs));
            DouyinAck ack;
            try
            {
                ack = await completion.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new ChannelSendFailedException(
                    "douyin",
                    $"抖音发送超时：{_options.SendTimeoutMs}ms 内没收到 ack（第 {sequence}/{total} 帧）。");
            }

            if (!ack.Ok)
            {
                throw new ChannelSendFailedException(
                    "douyin",
                    $"抖音发送失败：{ack.Error ?? "sidecar 未给出原因"}");
            }
        }
        finally
        {
            // 超时 / 成功 / 异常都清掉：pending 表不允许长垃圾
            _pendingAcks.TryRemove(requestId, out _);
        }
    }

    /// <summary>查询候选（<c>query.*</c> + <c>result</c> 配对）。</summary>
    private async Task<DouyinQueryResult> QueryAsync(
        string type,
        string label,
        CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var connection = RequireConnection();
        var requestId = NewRequestId();
        var completion = new TaskCompletionSource<DouyinQueryResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        _pendingResults[requestId] = completion;
        try
        {
            await connection
                .SendAsync(DouyinProtocol.BuildQuery(type, requestId), ct)
                .ConfigureAwait(false);

            var timeout = TimeSpan.FromMilliseconds(Math.Max(1, _options.QueryTimeoutMs));
            DouyinQueryResult result;
            try
            {
                result = await completion.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new TimeoutException(
                    $"抖音{label}查询超时：{_options.QueryTimeoutMs}ms 内没收到 result。");
            }

            if (!result.Ok)
            {
                throw new InvalidOperationException(
                    $"抖音{label}查询失败：{result.Error ?? "sidecar 未给出原因"}");
            }

            return result;
        }
        finally
        {
            _pendingResults.TryRemove(requestId, out _);
        }
    }

    // ─────────────────────────── 内部工具 ───────────────────────────

    /// <summary>
    /// 把一条消息切成若干批（每批 = 一帧 <c>outbound</c>）。
    /// </summary>
    /// <remarks>
    /// 平台单条文本上限 <see cref="DouyinOptions.MaxTextLength"/>：超了不是"截断"，而是**分帧发**，
    /// 否则用户只会看到前一半。非文本段按原顺序留在各自批次里。
    /// </remarks>
    private static List<IReadOnlyList<MessageSegment>> BuildBatches(
        IReadOnlyList<MessageSegment> segments,
        int maxTextLength)
    {
        var limit = maxTextLength > 0 ? maxTextLength : int.MaxValue;
        var batches = new List<IReadOnlyList<MessageSegment>>();
        var current = new List<MessageSegment>();
        var currentLength = 0;

        void Flush()
        {
            if (current.Count == 0)
            {
                return;
            }

            batches.Add(current.ToArray());
            current.Clear();
            currentLength = 0;
        }

        foreach (var segment in segments)
        {
            if (segment is ReplySegment)
            {
                // 引用走帧级 replyTo，不进 segments
                continue;
            }

            if (segment is TextSegment text)
            {
                foreach (var chunk in DouyinProtocol.SplitText(text.Text, limit))
                {
                    if (currentLength > 0 && currentLength + chunk.Length > limit)
                    {
                        Flush();
                    }

                    current.Add(new TextSegment(chunk));
                    currentLength += chunk.Length;
                }

                continue;
            }

            current.Add(segment);
        }

        Flush();
        return batches;
    }

    /// <summary>当前是否已连上 sidecar（供恢复循环判断）。</summary>
    bool IRecoverableChannel.IsConnected => IsConnected;

    /// <summary>
    /// 抖音的「恢复」是**空操作**：服务端没有出站连接，连接由 sidecar 反向连入。
    /// 这里只把当前状态如实报回去，让共用的恢复循环继续按退避等待。
    /// </summary>
    Task<bool> IRecoverableChannel.TryRecoverAsync(CancellationToken ct) => Task.FromResult(IsConnected);

    private IDouyinConnection RequireConnection()
    {
        // 运行时开关先判：关掉时即使连接还在，也按"未启用"回答 —— 否则会误报"未连接"，
        // 排查时让人以为 sidecar 挂了。
        if (!_options.Enabled)
        {
            throw new InvalidOperationException(
                "抖音接入已停用（客户端「第三方平台接入」里可以打开）。");
        }

        return _connection
            ?? throw new ChannelUnavailableException(
                "douyin",
                "抖音未连接：sidecar 尚未连入 /ws/channel（或已断开），无法发送。");
    }

    /// <summary>
    /// 拒掉一条连接：关掉它，并把当前连接指针清掉（如果指向它）。
    /// </summary>
    /// <remarks>
    /// 不清指针的话，`SendAsync` 会继续往这条"已被拒绝"的连接上发，
    /// 用户看到的是"发送超时"而不是"底座配错了"，排查方向直接跑偏。
    /// </remarks>
    private async Task RejectConnectionAsync(IDouyinConnection connection)
    {
        if (ReferenceEquals(Volatile.Read(ref _connection), connection))
        {
            _connection = null;
        }

        await CloseQuietlyAsync(connection, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task CloseQuietlyAsync(IDouyinConnection connection, CancellationToken ct)
    {
        try
        {
            await connection.CloseAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "关闭抖音连接时出错（忽略）");
        }
    }

    /// <summary>等接收循环退出，有上限；超时只记日志，不能挂住调用方。</summary>
    private async Task WaitForExitAsync(Task? loop)
    {
        if (loop is null)
        {
            return;
        }

        try
        {
            await loop.WaitAsync(ReceiveLoopExitTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.Debug("抖音旧接收循环未在 {Seconds}s 内退出（忽略）", ReceiveLoopExitTimeout.TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            // 被取消 = 正常退出路径
        }
    }

    private static string NewRequestId() => Guid.NewGuid().ToString("N");

    private static NotSupportedException NotSupported(string action) =>
        new($"抖音通道不支持{action}：第一版只做文本 / 图片 / @ / 引用（设计 §1.2）。");
}
