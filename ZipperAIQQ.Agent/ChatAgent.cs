using System.Diagnostics;
using Microsoft.Extensions.AI;
using Serilog;
using ZipperAIQQ.Agent.Events;
using ZipperAIQQ.Agent.Faces;
using ZipperAIQQ.Agent.Llm;
using ZipperAIQQ.Agent.Memory;
using ZipperAIQQ.Agent.Search;
using ZipperAIQQ.Agent.Security;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using System.Text;
using ZipperAIQQ.Core.Events;
using ZipperAIQQ.Core.Time;
using ZipperAIQQ.Storage.Messages;
using ZipperAIQQ.Storage.Metrics;
using ZipperAIQQ.Storage.Personas;
using ZipperAIQQ.Storage.Schedules;
using ZipperAIQQ.Storage.Summaries;

namespace ZipperAIQQ.Agent;

/// <summary>
/// 一个聊天的执行体（需求 D9：一聊天一实例）。
/// </summary>
/// <remarks>
/// <para>同一实例内部**串行**（实例内一把锁）；不同会话并行。</para>
/// <para>
/// M1-B 的上下文是**最小版**：system + 最近 N 条历史 + 当前消息。
/// 三层压缩、检索、人设重注入分别在 M1-C / M1-D 做。
/// </para>
/// </remarks>
public sealed class ChatAgent : IChatAgent
{
    private readonly IChatClientProvider _clients;
    private readonly IMessageChannel _channel;
    private readonly IMessageStore _store;
    private readonly FaceCatalog _faces;

    /// <summary>会话级发送节流：保证两次真正发出去之间至少隔 MinSendIntervalMs。</summary>
    private readonly SendThrottle _sendThrottle = new();
    private readonly MemoryRetriever _retriever;
    private readonly SummaryService _summaries;
    private readonly ISummaryStore _summaryStore;
    private readonly IPersonaStore _personas;
    private readonly IMetricsStore _metrics;
    private readonly IEventBus _events;
    private readonly IPermissionGate _permissionGate;
    private readonly IExecutionPolicy _policy;
    private readonly Search.IWebSearchProvider? _webSearch;
    private readonly Media.IImageFetcher? _imageFetcher;
    private readonly Media.IImageConverter? _imageConverter;
    private readonly Storage.Config.IConfigStore _configs;
    private readonly Storage.Settings.IRuntimeSettingsStore _settings;
    private readonly IRoleResolver _roles;
    private readonly WorkspacePathResolver _workspace;
    private readonly int _fileMaxBytes;
    private readonly IScheduleStore _schedules;
    private readonly ChatAgentOptions _options;
    private readonly ILogger _logger;

    private readonly List<ChatMessage> _history = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// 人设锚点（D4 的第 1 条 system）：实例存活期间**永不改变**。
    /// </summary>
    /// <remarks>
    /// 它保证「最初设定」始终在上下文最前方；后面那条会随轮次刷新的重注入
    /// 负责「让模型最近又看到一次」。两条各司其职，缺一个都会慢慢漂。
    /// </remarks>
    private string? _anchorPersona;

    /// <summary>上次锁定锚点用的人设内容（用于检测"人设被改过"）。</summary>
    private string? _anchorPersonaSource;

    public ChatAgent(string sessionId, ChatAgentDependencies deps,
        Storage.Config.IConfigStore configs,
        Storage.Settings.IRuntimeSettingsStore settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(deps);

        SessionId = sessionId;
        _clients = deps.Client;
        _channel = deps.Channel;
        _store = deps.Store;
        _faces = deps.Faces;
        _retriever = deps.Retriever;
        _summaries = deps.Summaries;
        _summaryStore = deps.SummaryStore;
        _personas = deps.Personas;
        _metrics = deps.Metrics;
        _events = deps.Events;
        _permissionGate = deps.PermissionGate;
        _policy = deps.Policy;
        _webSearch = deps.WebSearch;
        _imageFetcher = deps.ImageFetcher;
        _imageConverter = deps.ImageConverter;
        _configs = configs;
        _settings = settings;
        _roles = deps.Roles;
        _workspace = deps.Workspace;
        _fileMaxBytes = deps.FileMaxBytes;
        _schedules = deps.Schedules;
        _options = deps.Options;
        _logger = deps.Logger;
    }

    /// <summary>机器人自身标识（M1-B 用固定值；接真 self_id 后替换）。</summary>
    public const string BotUserId = "bot";

    public string SessionId { get; }

    /// <summary>
    /// 清空内存历史（<c>/reset</c>）：日志仍在库里，但本轮之后不再自动恢复。
    /// </summary>
    public void ResetHistory()
    {
        // 与 RunAsync 用同一把锁：避免清到一半又被追加
        if (!_lock.Wait(TimeSpan.FromSeconds(5)))
        {
            _logger.Warning("重置历史超时（会话 {SessionId}），跳过", SessionId);
            return;
        }

        try
        {
            _history.Clear();
            // 关键：标记为"已加载"，否则下次对话会从库里再捞回来
            _historyLoadTask = Task.CompletedTask;
        }
        finally
        {
            _lock.Release();
        }

        _logger.Information("会话 {SessionId} 上下文已重置", SessionId);
    }

    /// <summary>
    /// 把一次命令交互补进上下文：命令原话（user）+ 命令结果（user 侧，标 <c>[命令结果]</c>）。
    /// </summary>
    /// <remarks>
    /// 不这么做的话，命令交互只在**重启后从库恢复**时才可见；
    /// 当前会话里模型对"刚问过 /status"一无所知，下一轮容易接歪。
    /// </remarks>
    public void RememberCommand(string userMessage, string commandResponse)
    {
        if (string.IsNullOrWhiteSpace(commandResponse))
        {
            return;
        }

        if (!_lock.Wait(TimeSpan.FromSeconds(5)))
        {
            _logger.Warning("记录命令交互超时（会话 {SessionId}），跳过", SessionId);
            return;
        }

        try
        {
            _history.Add(new ChatMessage(ChatRole.User, $"[{BeijingTime.Format(BeijingTime.Now)}] {userMessage}"));
            _history.Add(new ChatMessage(ChatRole.User, $"[命令结果] {commandResponse}"));
            TrimHistory();
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<AgentRunResult> RunAsync(AgentRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await RunCoreAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
            TriggerCompressionInBackground();
        }
    }

    private int _compressing;

    /// <summary>
    /// 后台尝试压缩摘要。
    /// </summary>
    /// <remarks>
    /// **不 await**：压缩要调一次模型（1~2 秒），放在回复路径上会让人等两倍时间。
    /// 没到阈值时它自己会立刻返回，几乎零成本。
    /// <para>
    /// **用独立 CancellationToken**：不能用请求的 ct，
    /// 否则请求一结束压缩就被取消，摘要永远压不出来。
    /// </para>
    /// </remarks>
    private void TriggerCompressionInBackground()
    {
        // 同一实例同时只跑一个压缩任务
        if (Interlocked.CompareExchange(ref _compressing, 1, 0) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _summaries
                    .TryCompressAsync(SessionId, _options.HotWindowMessages, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "摘要压缩失败（会话 {SessionId}）", SessionId);
            }
            finally
            {
                Interlocked.Exchange(ref _compressing, 0);
            }
        }, CancellationToken.None);
    }

    private async Task<AgentRunResult> RunCoreAsync(AgentRunRequest request, CancellationToken ct)
    {
        using var activity = AgentTelemetry.Source.StartActivity("agent.run");
        activity?.SetTag("session.id", SessionId);
        activity?.SetTag("chat.type", request.Chat.Type.ToString());

        // 会话从库恢复（M1-C 遗留的补做）：实例存活期内只做一次。
        // 放在打 hot.window 标签之前，否则标签永远显示 0。
        await EnsureHistoryLoadedAsync(request, ct).ConfigureAwait(false);

        activity?.SetTag("hot.window", _history.Count);

        // 长期记忆检索：热窗只覆盖最近几条，很久前提过的事要靠这个找回来。
        // 检索失败不影响本轮回复 —— 少点记忆总比不回复强。
        IReadOnlyList<Storage.Messages.StoredMessage> related = [];
        try
        {
            related = await _retriever
                .RetrieveAsync(SessionId, request.UserText, topK: 5, excludeRecentCount: _options.HotWindowMessages, ct: ct)
                .ConfigureAwait(false);
            activity?.SetTag("memory.retrieved", related.Count);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "检索失败（会话 {SessionId}），本轮不带长期记忆", SessionId);
        }
        // ── 人设（D4/D12）：会话级覆盖全局，完全替换而非叠加 ──
        var persona = await _personas
            .GetAsync(request.Chat.Platform, request.Chat.Type.ToString(), request.Chat.Id, ct)
            .ConfigureAwait(false);

        // 人设正文 + 名字前缀：只给【形象】/【性格】时模型不知道自己叫什么，
        // 会影响「我是谁」的回答（实测它自己编了个名字）。
        var personaText = BuildPersonaText(persona);

        // 锚点：首次锁定后**永不改动**，保证「最初设定」一直在最前方
        // 锚点 = **首次的人设**，之后固定在最前方，防止长对话把人设冲淡。

        // 但人设**被改过**时必须跟着更新 —— 否则锚点还是旧的，

        // 重注入里是新的人设，两条 system 互相打架，模型通常听第一条（实测：改了人设像没生效）。

        if (_anchorPersona is null || !string.Equals(_anchorPersonaSource, personaText, StringComparison.Ordinal))

        {

            _anchorPersona = personaText;

            _anchorPersonaSource = personaText;

            _logger.Information("人设已刷新（会话 {SessionId}）：{Name}", SessionId, persona.Name);

        }

        // 重注入内容 = 人设 + 摘要。
        // 摘要并进这里而不是单独占一条 system —— D4 要求人设 system 恰好两条，
        // 多一条会让人分不清哪个才是"角色设定"。
        var summary = await _summaryStore.GetAsync(SessionId, ct).ConfigureAwait(false);

        // 系统提示词（与人设分开）：客户端可改；没配就用内置默认
        var configuredPrompt = await _settings.GetAsync(SystemPrompts.StorageKey, ct).ConfigureAwait(false);
        var systemPrompt = string.IsNullOrWhiteSpace(configuredPrompt)
            ? SystemPrompts.Default
            : configuredPrompt.Trim();

        var reinject = new StringBuilder(personaText);
        if (summary is not null && !string.IsNullOrWhiteSpace(summary.Summary))
        {
            reinject.AppendLine().AppendLine()
                    .AppendLine("【之前对话的摘要（供理解上下文，不必逐条回应）】")
                    .Append(summary.Summary);
            activity?.SetTag("memory.summary", "yes");
        }

        // 能力声明：工具按角色隐藏时，模型会**幻觉**「我做了」——
        // 实测：普通成员被要求写文件，它回了「test2.txt 建好了」，实际什么都没建。
        // 所以必须**告诉它没有这个能力**，而不是让它自己猜（FR-6.4 的另一半）。
        var capabilityNote = BuildCapabilityNote(_roles.Resolve(request.Sender.UserId));
        if (capabilityNote.Length > 0)
        {
            reinject.AppendLine().AppendLine(capabilityNote);
        }

        // 基础行为规则（发消息 / 表情 / 记忆 / 工具 / 主动行为 / 语气）：
        // 统一由「系统提示词」提供，客户端可改写，默认值见 SystemPrompts.Default
        reinject.AppendLine().AppendLine(systemPrompt);

        // 关系段：光有"主人是 10001"这个号码，模型认不出这个人是谁
        // （实测它只会照着念号码，不会联想到"这是拉链"）—— 所以把**昵称 + 当前身份**都给出来
        reinject.AppendLine();
        reinject.Append(await BuildRelationshipNoteAsync(request.Sender.UserId, ct).ConfigureAwait(false));

        // 自我身份（问题记录 02 #3/#4）：模型不知道自己的 QQ 号，
        // 就会把「A 戳了 B」当成「有人在戳我」。
        // ⚠️ 按会话所在平台取（S3-A）：多平台下 QQ 号不能当成抖音的"我自己"。
        var selfId = _channel.SelfIdFor(request.Chat.Platform) ?? _options.SelfId;
        if (!string.IsNullOrWhiteSpace(selfId))
        {
            reinject.AppendLine().AppendLine()
                    .AppendLine($"【你自己是谁】你的 QQ 号是 {selfId}；群里/消息里出现这个号，指的就是你本人。");
        }

        // 人设守卫（第 20 项）：每次重注入都带，位置紧贴当前消息 ——
        // 长对话里人设会被上下文稀释，靠这一条把它拉回来。
        // 主人有例外（问题记录 02 #2）：主人要求改设定/称呼/扮演时照做。
        var ownerId = await _settings.GetAsync("owner.userId", ct).ConfigureAwait(false);
        var isOwner = !string.IsNullOrWhiteSpace(ownerId) &&
                      string.Equals(request.Sender.UserId, ownerId, StringComparison.Ordinal);
        reinject.AppendLine().AppendLine(PromptRules.PersonaGuard(isOwner));

        var messages = new List<ChatMessage>
        {
            // ① 锚点 system：永远第一条
            new(ChatRole.System, _anchorPersona),
        };

        messages.AddRange(_history);

        // ② 重注入 system：紧邻当前消息之前。
        //    位置很关键 —— 放在历史前面就退化成"和锚点差不多远"，
        //    起不到「最近又提醒一次」的作用。
        messages.Add(new ChatMessage(ChatRole.System, reinject.ToString()));

        // 时间注入（FR-15）：每条消息带时间，且每次都给发送者双名
        var memoryBlock = BuildMemoryBlock(related);
        var userLine = $"[{BeijingTime.Format(request.Now)}] {request.Sender.DisplayName}：{memoryBlock}{RenderContent(request)}";
        messages.Add(await BuildUserMessageAsync(request, userLine, ct).ConfigureAwait(false));

        // send_message 绑定当前会话（模型无法指定发给谁）
        var sent = 0;
        var tool = SendMessageTool.Create(
            _channel,
            _store,
            selfId ?? BotUserId,
            request.Chat,
            _options.MaxMessagesPerCall,
            ct => _sendThrottle.WaitAsync(_options.MinSendIntervalMs, ct),
            _faces,
            _metrics,
            n => sent += n);

        var chatOptions = new ChatOptions
        {
            Tools = BuildTools(request, tool),
            // 温度之类先不调，保持端点默认
        };

        ChatResponse response;
        try
        {
            response = await CallLlmAsync(messages, chatOptions, "llm.call", ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "调用模型失败（会话 {SessionId}）", SessionId);
            return new AgentRunResult(null, 0);
        }

        var finalText = response.Text;

        // ══════ §8.3 指令遵循漂移：该调工具却没调 ══════
        // 检测信号是"可判定的"：这一轮一条都没发出去（sent == 0），却产出了文本。
        //
        // 硬规矩：**只在没有副作用时才重试**。sent == 0 说明本轮什么都没发出去，
        // 重跑整条链是安全的；一旦本轮已经发过消息，重跑就可能把同样的话再发一遍 ——
        // 宁可这一轮答得不好，也不能让消息重复。
        if (sent == 0 && !string.IsNullOrWhiteSpace(finalText))
        {
            activity?.SetTag("drift.detected", "no_tool_call");
            PublishEvent(new DriftDetectedEvent(SessionId, BeijingTime.Now, "instruction", "no_tool_call"));

            messages.Add(new ChatMessage(ChatRole.User, DriftCorrectionPrompt));
            try
            {
                var retry = await CallLlmAsync(messages, chatOptions, "llm.retry", ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(retry.Text))
                {
                    finalText = retry.Text;
                }

                activity?.SetTag("drift.retry.sent", sent);
                _logger.Information(
                    "指令遵循漂移：已回填具体原因并重试 1 次（会话 {SessionId}），重试后发出 {Sent} 条",
                    SessionId, sent);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "漂移纠正重试失败（会话 {SessionId}），转入降级发送", SessionId);
            }
        }

        // ══════ §8.3 降级：连续 2 次都没调工具 → 由我们代发 ══════
        // 兜底不是"随便发发"：表情照样解析、按空行分条，与 send_message 工具**同一套解析**。
        // （上一版这里直接拼 TextSegment，结果表情变字面量、几句话挤成一条 —— 实测抓到的 bug。）
        if (sent == 0 && !string.IsNullOrWhiteSpace(finalText))
        {
            var parts = MessageContentParser.SplitByBlankLine(finalText, _options.MaxMessagesPerCall);

            foreach (var part in parts)
            {
                // 会话级最小发送间隔（防风控）：不是"第几条之后才等"，而是每次真正发送前都排一次队
                await _sendThrottle.WaitAsync(_options.MinSendIntervalMs, ct).ConfigureAwait(false);

                if (await TrySendAsync(request.Chat, MessageContentParser.Parse(part, _faces), ct).ConfigureAwait(false))
                {
                    sent++;
                }
            }

            activity?.SetTag("drift.degraded", "yes");
            PublishEvent(new SendDegradedEvent(SessionId, BeijingTime.Now, sent));
            _logger.Warning(
                "降级发送（会话 {SessionId}）：连续 2 次未调用工具，已代发 {Sent} 条",
                SessionId, sent);
        }

        // 更新历史
        _history.Add(new ChatMessage(ChatRole.User, userLine));
        if (!string.IsNullOrWhiteSpace(finalText))
        {
            _history.Add(new ChatMessage(ChatRole.Assistant, finalText));
        }

        TrimHistory();

        activity?.SetTag("messages.sent", sent);
        activity?.SetStatus(ActivityStatusCode.Ok);

        return new AgentRunResult(finalText, sent);
    }

    /// <summary>历史是否已经从库里加载过（实例存活期内只加载一次）。</summary>
    private Task? _historyLoadTask;

    /// <summary>
    /// 从库里恢复热窗历史（**实例存活期内只做一次**）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 重启后 <c>_history</c> 是空的，只靠摘要 + 检索，模型会把「最开始说过什么」
    /// 答成旧摘要。这就是「会话从库恢复」：照着最近 N 条把对话接上。
    /// </para>
    /// <para>
    /// ⚠️ 本轮这批消息**已经落库**（路由是先存再调），必须按 <c>EventId</c> 排除，
    /// 否则同一条消息会在「恢复的历史」和「本轮 userLine」里各出现一次。
    /// </para>
    /// </remarks>
    private Task EnsureHistoryLoadedAsync(AgentRunRequest request, CancellationToken ct)
    {
        return _historyLoadTask ??= LoadHistoryAsync(request, ct);
    }

    private async Task LoadHistoryAsync(AgentRunRequest request, CancellationToken ct)
    {
        try
        {
            var currentIds = request.Batch
                .Select(m => m.EventId)
                .Where(id => !string.IsNullOrEmpty(id))
                .ToHashSet(StringComparer.Ordinal);

            // 多取批内条数，保证排除掉本批之后仍能凑满热窗
            var recent = await _store
                .GetRecentAsync(SessionId, _options.HotWindowMessages + Math.Max(1, request.Batch.Count), ct)
                .ConfigureAwait(false);

            var added = 0;
            foreach (var stored in recent)
            {
                if (stored.Message.EventId is { Length: > 0 } id && currentIds.Contains(id))
                {
                    continue;
                }

                _history.Add(ToHistoryMessage(stored));
                added++;
            }

            TrimHistory();

            if (added > 0)
            {
                _logger.Information("会话 {SessionId} 从库恢复历史 {Count} 条", SessionId, _history.Count);
            }
        }
        catch (Exception ex)
        {
            // 恢复失败就当没有历史：不能因为读库失败把对话卡死
            _logger.Warning(ex, "会话 {SessionId} 历史恢复失败，本轮从空历史开始", SessionId);
        }
    }

    /// <summary>把入库消息还原成给模型看的历史条目（格式与实时消息保持一致）。</summary>
    private static ChatMessage ToHistoryMessage(StoredMessage stored)
    {
        // 命令响应单独标注：它**不是 AI 说的话**（FR-9.4），
        // 不标注的话模型会把「/status 的输出」当成自己之前的发言。
        if (stored.IsCommand)
        {
            return new ChatMessage(ChatRole.User, $"[命令结果] {stored.Message.PlainText}");
        }

        var text =
            $"[{BeijingTime.Format(stored.Message.OccurredAt)}] {stored.Message.Sender.DisplayName}：{stored.Message.PlainText}";

        return new ChatMessage(stored.IsOutgoing ? ChatRole.Assistant : ChatRole.User, text);
    }

    /// <summary>
    /// 组装本次调用可用的工具：基础工具 + **按角色**可用的文件工具。
    /// </summary>
    /// <remarks>
    /// 不可用的工具**连 schema 都不给模型** —— 比「看得见但调不动」更省 token，也少试探。
    /// 但可见性**不是**唯一防线：每个文件工具执行前还会再过一次门禁。
    /// </remarks>
    private List<AITool> BuildTools(AgentRunRequest request, AIFunction sendMessage)
    {
        var context = new ToolCallContext
        {
            Chat = request.Chat,
            Sender = request.Sender,
            Role = _roles.Resolve(request.Sender.UserId),
        };

        var tools = new List<AITool>
        {
            sendMessage,
            ListFacesTool.Create(_faces),
        };

        tools.AddRange(FileTools.Create(context, _permissionGate, _workspace, _fileMaxBytes));
        tools.AddRange(ScheduleTools.Create(context, _permissionGate, _schedules));

        // 查历史：模型只看得到热窗那十几条，没有这个工具时它会瞎编"我们之前聊过…"
        // 跨会话搜索按权限给：这是读**别人的对话**，只给管理员/主人
        var canCrossSession = _permissionGate.IsAllowed(ToolCapability.CrossSessionHistory, context.Role);
        tools.AddRange(MessageTools.Create(_store, request.Chat, maxResults: 30, allowCrossSession: canCrossSession));

        // 主动给别人发消息（管理员）：让 AI 能跨会话传话，而不只是被动回当前会话
        tools.AddRange(SendToOtherSessionTool.Create(
            _channel, _store, context, _permissionGate, _configs, maxResults: 10));

        // 平台信息查询（第 19 项）：好友 / 群 / 群成员 / 群头衔
        // 通道实现了 IContactDirectory 才给工具，否则模型会去调一个不存在的能力
        if (_channel is Channel.IContactDirectory contacts)
        {
            tools.AddRange(ContactTools.Create(contacts, context, _policy));

            // 写操作（改自己签名 / 戳别人）：同样是"通道支持才给"，
            // 内部再按权限门禁逐个判断 —— 没权限就连工具都不给模型看
            tools.AddRange(ProfileTools.Create(contacts, context, _permissionGate, _configs));
        }

        // 策略过滤（#28）：关掉的工具不给 schema；角色不够的也不给。
        // 可见性不是唯一防线 —— 每个工具执行前仍会再过一次门禁。
        if (_webSearch is { IsAvailable: true })
        {
            tools.AddRange(WebSearchTool.Create(_webSearch));
        }

        var allowed = new List<AITool>(tools.Count);
        foreach (var tool in tools)
        {
            var capability = ToolCatalog.CapabilityOf(tool.Name);
            var fallback = capability is null ? Role.Normal : PermissionGate.DefaultRoleOf(capability.Value);

            if (!_policy.IsEnabled(PolicyCategory.Tool, tool.Name))
            {
                continue;
            }

            if (context.Role < _policy.MinRole(PolicyCategory.Tool, tool.Name, fallback))
            {
                continue;
            }

            allowed.Add(tool);
        }

        return allowed;
    }

    /// <summary>
    /// 工具使用指引。
    /// </summary>
    /// <remarks>
    /// 实测问题：被问「我们之前聊过什么」时，模型宁可编也不调 <c>search_history</c> ——
    /// 工具在那儿但没被用起来，所以要把「什么时候必须用」写清楚。
    /// </remarks>
    /// <summary>
    /// 基础运行说明（发消息 / 表情 / 记忆）。
    /// </summary>
    /// <remarks>
    /// 这些内容**原本写在 <c>config/persona.md</c> 里**，导致"人设文件"里一半是操作说明、
    /// 另一半才是角色设定 —— 用户填了角色也感觉不到效果（模型看到的主要是工具说明书）。
    /// 现在操作说明固定在代码里，persona.md 只负责角色本身。
    /// </remarks>
    /// <summary>
    /// 人设正文 + 名字前缀。
    /// </summary>
    /// <remarks>
    /// 只给【形象】/【性格】时，模型不知道自己叫什么 —— 实测它自己编了一个。
    /// 名字是角色的最低限度信息，必须显式告诉它。
    /// </remarks>
    /// <summary>
    /// 「你和当前说话的人是什么关系」—— 主人是谁、现在这个人是不是主人。
    /// </summary>
    /// <remarks>
    /// 只给号码不够：模型知道「主人是 10001」，但不会把它和好友列表里的
    /// 「拉链」对应起来，于是对话里完全不认人（实测）。
    /// 所以这里同时给**昵称**和**当前身份判断**。
    /// </remarks>
    private async Task<string> BuildRelationshipNoteAsync(string senderId, CancellationToken ct)
    {
        string? ownerId = null;
        string? ownerName = null;

        try
        {
            ownerId = await _settings.GetAsync("owner.userId", ct).ConfigureAwait(false);
            ownerName = await _settings.GetAsync("owner.name", ct).ConfigureAwait(false);
        }
        catch
        {
            // 读不到就当没设主人
        }

        var hasOwner = !string.IsNullOrWhiteSpace(ownerId);
        var isOwner = hasOwner && string.Equals(senderId, ownerId, StringComparison.Ordinal);
        var ownerLabel = string.IsNullOrWhiteSpace(ownerName) ? "（未命名）" : ownerName;

        var sb = new StringBuilder();
        sb.AppendLine("【你和当前说话的人的关系】");
        sb.Append(PromptRules.Relationship(hasOwner, isOwner, ownerLabel, ownerId));

        return sb.ToString().TrimEnd();
    }

    private static string BuildPersonaText(Persona persona)
    {
        var name = persona.Name?.Trim();

        // "persona" 是全局文件的默认名，不是真名，别写进 system
        var isDefaultName = string.IsNullOrWhiteSpace(name) ||
                            name.Equals("persona", StringComparison.OrdinalIgnoreCase);

        var sb = new StringBuilder();

        // ⚠️ 关键：人设是**行为指令**，不是背景资料。
        // 只贴一段「蓝发猫耳少女、说话简短」时，模型会把它当设定集存档，
        // 回复照样是标准问答腔（实测：问名字能答「小衣」，但语气完全没变）。
        sb.AppendLine("下面是你这个角色的设定。**它不是背景资料，是你说话的方式本身** ——");
        sb.AppendLine("语气、称呼、句子长短、用不用颜文字、有没有口头禅，都必须照着来。");
        sb.AppendLine("只描述外貌的条目（发色、衣服、身高）你自己心里知道就行，不必刻意说出来，"
                    + "但它暗示的气质要体现在语气里。");
        sb.AppendLine();

        if (!isDefaultName)
        {
            sb.AppendLine($"你的名字是「{name}」。别人问你是谁，就回答这个，不要编别的名字。");
            sb.AppendLine();
        }

        sb.Append(persona.Content);

        return sb.ToString();
    }

    private const string RuntimeGuideNote = """
        【怎么把话说出去】
        - **必须调用 `send_message` 工具**，直接输出文本对方看不到
        - 可以一次发多条，像真人把一句话分几条发；回复简短自然，别写小作文
        - 不能也无需指定发给谁，会话是固定的

        【怎么发表情】
        1. 需要时先调 `list_faces`（不传关键词看常用，传关键词按名称/标签搜）
        2. 把 `[表情:名字]` 写进消息里，例如：`哈哈哈[表情:笑哭]`
        3. **单独一条只写 `[表情:名字]`** 会作为大表情发送（QQ 会自动播放特效）

        【关于记忆】
        - 上下文里可能出现「【之前对话的摘要】」「【可能相关的早期对话】」
        - 它们是参考材料，不必逐条回应，但代表「你们之前确实聊过」
        - 不要假装不记得，也不要生硬复述 —— 像真人那样自然接上
        """;

    /// <summary>
    /// 工具使用指引（**强制语气**）。
    /// </summary>
    /// <remarks>
    /// 实测问题：被问「好友列表有多少人」时，模型明明手上有 query_napcat_api，
    /// 却回「说不出来，不瞎编」—— 宁可不答也不用工具。
    /// 根因是提示词只写了「该用的时候用」，模型把「我不确定」当成了不用的理由。
    /// 所以这里明确：**不确定正是必须去查的信号**。
    /// </remarks>
    private const string ToolUsageNote = """
        【工具使用 · 强制】
        你手上的工具：`search_history` / `get_recent_history`（本会话历史）、
        `query_napcat_api`（好友/群/群成员/群头衔/陌生人资料）、`file_*`（读写文件）、`schedule_*`（定时任务）。

        规则：
        1. 被问到**你不确定的事实**（有多少人、某人是谁 / 什么头衔、之前聊过的原话、群里几个人）
           → **必须先调工具查，再回答**
        2. **不许**在没查的情况下回「我不知道 / 说不出来」—— 那等于没试就放弃
        3. 查了确实没有，才说「查不到」，并说清查过什么
        4. 工具报错就如实说报错，不要假装成功

        【可以主动做的事】
        - `poke_someone`（戳一戳）和 `set_my_signature`（改个性签名）是**你自己可以决定**的行为：
          心情好、想引起注意、想表达态度时可以主动做，不必等对方要求
        - 但要克制：同一轮里不要反复戳同一个人；签名不要频繁改
        - 做完自然带过即可，不要解释成"我调用了工具"
        """;

    /// <summary>
    /// 按角色生成「能力限制」说明，塞进重注入 system。
    /// </summary>
    /// <remarks>
    /// 工具不可见时**必须显式告知**，否则模型会编造"已完成" ——
    /// 这是实测抓到的：普通成员要求写文件，它回「test2.txt 建好了」，工作区空空如也。
    /// </remarks>
    private string BuildCapabilityNote(Role role)
    {
        var missing = new List<string>();

        if (!_permissionGate.IsAllowed(ToolCapability.ReadFile, role))
        {
            missing.Add("读文件");
        }

        if (!_permissionGate.IsAllowed(ToolCapability.WriteFile, role))
        {
            missing.Add("写文件");
        }

        if (!_permissionGate.IsAllowed(ToolCapability.DeleteFile, role))
        {
            missing.Add("删除文件");
        }

        return missing.Count == 0
            ? string.Empty
            : $"【能力限制】你当前**没有**{string.Join("、", missing)}的能力（不是权限被拒，是这些工具根本没提供给你）。" +
              "被要求做这些事时，**明确说做不到**，不要声称已经完成。";
    }

    /// <summary>发 Agent 事件；订阅方抛异常不能反过来影响对话。</summary>
    private void PublishEvent<T>(T evt) where T : AgentEvent
    {
        try
        {
            _events.Publish(evt);
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Agent 事件发布失败：{Event}", typeof(T).Name);
        }
    }

    /// <summary>
    /// §8.3 的纠正提示：必须说清**哪里不对** —— 只说「重新来」等于没说。
    /// </summary>
    private const string DriftCorrectionPrompt =
        "（系统提示）你上一条只输出了文本，**没有调用 send_message 工具**，所以对方一个字都没收到。\n" +
        "请调用 send_message 把你要说的话真正发出去；需要分几条说就分几条。";

    /// <summary>
    /// 一次模型调用 + 指标落库（耗时 / token）。
    /// </summary>
    /// <param name="metricKind">
    /// 指标分类：正常调用记 <c>llm.call</c>，漂移纠正的重试记 <c>llm.retry</c> ——
    /// 分开记才能看出「因为漂移多烧了多少次调用」。
    /// </param>
    private async Task<ChatResponse> CallLlmAsync(
        List<ChatMessage> messages,
        ChatOptions options,
        string metricKind,
        CancellationToken ct)
    {
        using var llmActivity = AgentTelemetry.Source.StartActivity("llm.call");
        llmActivity?.SetTag("llm.message.count", messages.Count);
        llmActivity?.SetTag("llm.kind", metricKind);

        var sw = Stopwatch.StartNew();
        var response = await _clients.Current.GetResponseAsync(messages, options, ct).ConfigureAwait(false);
        sw.Stop();

        llmActivity?.SetTag("llm.elapsed.ms", sw.ElapsedMilliseconds);
        llmActivity?.SetStatus(ActivityStatusCode.Ok);
        llmActivity?.SetTag("llm.input.tokens", response.Usage?.InputTokenCount);
        llmActivity?.SetTag("llm.output.tokens", response.Usage?.OutputTokenCount);

        // token 用量与耗时落库 —— 客户端要能看「哪类调用最慢 / token 烧多快 / 缓存命中多少」
        await _metrics.RecordAsync(new MetricEntry(
            SessionId,
            metricKind,
            sw.Elapsed.TotalMilliseconds,
            response.Usage?.InputTokenCount,
            response.Usage?.OutputTokenCount,
            response.Usage?.CachedInputTokenCount), ct).ConfigureAwait(false);

        return response;
    }

    /// <summary>
    /// 发送一条消息并落库（降级路径用）。
    /// </summary>
    /// <returns>是否成功发出；落库失败只记日志，不改变「已经发出去」这个事实。</returns>
    private async Task<bool> TrySendAsync(
        ChatRef chat,
        IReadOnlyList<MessageSegment> segments,
        CancellationToken ct)
    {
        var outgoing = new OutgoingMessage
        {
            Chat = chat,
            Segments = segments,
        };

        try
        {
            await _channel.SendAsync(outgoing, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "发送失败（会话 {SessionId}）", SessionId);
            return false;
        }

        try
        {
            await _store.AppendOutgoingAsync(
                outgoing,
                _channel.SelfIdFor(outgoing.Chat.Platform) ?? _options.SelfId ?? BotUserId,
                BeijingTime.Now,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 已发出，落库失败不改变事实
            _logger.Warning(ex, "落库失败（会话 {SessionId}）", SessionId);
        }

        return true;
    }

    /// <summary>热窗裁剪（M1-C 会换成真正的三层策略）。</summary>
    private void TrimHistory()
    {
        var max = Math.Max(2, _options.HotWindowMessages);
        if (_history.Count > max)
        {
            _history.RemoveRange(0, _history.Count - max);
        }
        }

    /// <summary>
    /// 把检索到的早期对话渲染成一段前置说明。
    /// </summary>
    /// <remarks>
    /// 明确标成「可能相关」而不是直接混进对话：模型需要知道这些是**参考材料**，
    /// 不一定都要回应，否则会把几天前的话当成本轮刚说的内容接上来。
    /// </remarks>
    private static string BuildMemoryBlock(IReadOnlyList<Storage.Messages.StoredMessage> related)
    {
        if (related.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        sb.AppendLine("【可能相关的早期对话】（供参考，不一定要回应）");
        foreach (var item in related)
        {
            sb.Append('[').Append(BeijingTime.Format(item.Message.OccurredAt)).Append("] ")
              .Append(item.Message.Sender.DisplayName).Append('：')
              .AppendLine(item.Message.PlainText);
        }
        sb.AppendLine();
        sb.Append("【当前消息】");
        return sb.ToString();
    }

    /// <summary>
    /// 构建发给模型的当前消息。**有图片时走多模态**（deepseek-flash 支持视觉输入）。
    /// </summary>
    /// <remarks>
    /// 只对**当前消息**这样做：历史里的图片仍是 <c>[图片]</c> 占位。
    /// 否则每轮都要把所有历史图片重传一遍，token 会迅速烧光，
    /// 而且旧图对当前这轮几乎没有价值。
    /// </remarks>
    private async Task<ChatMessage> BuildUserMessageAsync(
        AgentRunRequest request, string textVersion, CancellationToken ct)
    {
        var hasImage = request.Segments.Any(s => s is ImageSegment { Url: not null });
        if (!hasImage)
        {
            return new ChatMessage(ChatRole.User, textVersion);
        }

        var contents = new List<AIContent>
        {
            new TextContent($"[{BeijingTime.Format(request.Now)}] {request.Sender.DisplayName}："),
        };

        foreach (var segment in request.Segments)
        {
            switch (segment)
            {
                case TextSegment text:
                    contents.Add(new TextContent(text.Text));
                    break;

                case FaceSegment face:
                    contents.Add(new TextContent(RenderFace(face)));
                    break;

                case RecordSegment record:
                    contents.Add(new TextContent(RenderRecord(record)));
                    break;

                case ImageSegment image when Uri.TryCreate(image.Url, UriKind.Absolute, out _):
                    contents.Add(await Media.ImageContentBuilder
                        .BuildAsync(image, _imageFetcher, _imageConverter, ct)
                        .ConfigureAwait(false));
                    break;

                case ImageSegment:
                    contents.Add(new TextContent("[图片]"));
                    break;

                case MentionSegment mention:
                    contents.Add(new TextContent("@" + mention.UserId + " "));
                    break;
            }
        }

        return new ChatMessage(ChatRole.User, contents);
    }

    /// <summary>
    /// 把消息片段渲染成 AI 能读的文本（表情会变成 [微笑] 这类名字）。
    /// </summary>
    /// <remarks>
    /// 不能直接用 PlainText —— 它只含文本段，表情会被静默丢掉，
    /// 表现出来就是「AI 读不了表情」。
    /// </remarks>
    private string RenderContent(AgentRunRequest request)
    {
        // 积压合并：把这一批里每条都渲染成一行，让模型看到完整上下文
        if (request.Batch.Count > 1)
        {
            var lines = request.Batch.Select(m =>
                "[" + BeijingTime.Format(m.OccurredAt) + "] " + m.Sender.DisplayName + "：" +
                RenderSegments(m.Segments, m.PlainText));
            return string.Join("\n", lines);
        }

        return RenderSegments(request.Segments, request.UserText);
    }

    private string RenderSegments(IReadOnlyList<MessageSegment> segments, string fallback)
    {
        if (segments.Count == 0)
        {
            return fallback;
        }

        var sb = new StringBuilder();
        foreach (var segment in segments)
        {
            sb.Append(segment switch
            {
                TextSegment text => text.Text,
                FaceSegment face => RenderFace(face),
                // 语音：**转写已在通道层完成**（FR-1.5），这里只负责呈现结果
                RecordSegment record => RenderRecord(record),
                // 历史里的图片：**当前消息**会把图作为 UriContent 传给模型（多模态），
                // 但历史回放只能给占位文本 —— 所以这里标"历史"，不要让它以为现在也看不见。
                ImageSegment => "[图片]",
                // 表情包：带上 emoji id，模型**原样复制**这个标记就能发回同一张
                MfaceSegment mface => string.IsNullOrEmpty(mface.EmojiId)
                    ? "[表情包:" + (mface.Summary ?? "动画表情") + "]"
                    : "[表情包:" + (mface.Summary ?? "动画表情") + "|emoji=" + mface.EmojiId + "]",
                MentionSegment mention => "@" + mention.UserId + " ",
                ReplySegment => string.Empty,
                _ => string.Empty,
            });
        }

        var rendered = sb.ToString().Trim();
        return rendered.Length > 0 ? rendered : fallback;
    }

    /// <summary>
    /// 把入站表情渲染成**模型能认出来的形式**。
    /// </summary>
    /// <remarks>
    /// ⚠️ 以前输出的是 <c>[笑哭]</c> —— 模型会把它当成普通方括号文字，
    /// 根本不知道「对方发了个表情」，因此既不会复读、也不会学着用（实测踩到）。
    /// 现在和出站格式统一成 <c>[表情:名字]</c>：模型看到就知道这是表情，
    /// 而且**可以直接照抄这个标记发回去**。
    /// </remarks>
    private string RenderFace(FaceSegment face)
    {
        var entry = _faces.GetById(face.Id);

        // 查不到名字时也要说清"这是个表情"，否则模型会以为消息里有段空白。
        // 带上 id：模型至少能原样复读同一个表情，排查时也能看出是哪一个（问题记录 #25）。
        return entry is null ? "[表情:" + face.Id + "]" : "[表情:" + entry.Name + "]";
    }

    /// <summary>
    /// 把通道层转写好的语音渲染成模型能读的文本。
    /// </summary>
    /// <remarks>
    /// 未转写时保留 <c>[语音]</c>：模型需要知道"对方发了语音但内容不可见"，
    /// 而不是以为消息里什么都没有。
    /// </remarks>
    private static string RenderRecord(RecordSegment record) =>
        string.IsNullOrWhiteSpace(record.Text) ? "[语音]" : "[语音转写:" + record.Text + "]";
}