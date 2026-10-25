using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Serilog;
using ZipperAIQQ.Channel.Apis;
using ZipperAIQQ.Channel.Audio;
using ZipperAIQQ.Channel.Filtering;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Transport.Client;

namespace ZipperAIQQ.Channel.NapCat;

/// <summary>
/// NapCat（OneBot 11）通道实现，走**正向 WebSocket**（我方主动连 NapCat）。
/// </summary>
/// <remarks>
/// <para>
/// 职责边界（接缝 B）：本类是**唯一**认识 OneBot 的地方。它把 OneBot 报文翻译成
/// <see cref="InboundEvent"/>，上层永远看不到 <c>post_type</c> / <c>echo</c> 这些概念。
/// </para>
/// <para>
/// **两条通道**：
/// <list type="bullet">
///   <item>事件：<c>post_type</c> 帧 → 映射 → 过滤 → 队列 → <see cref="ReceiveAsync"/></item>
///   <item>API 响应：带 <c>echo</c> 的帧 → 按 echo 定向唤醒等待者（**不进队列**）</item>
/// </list>
/// 两者**不能用同一条路**：事件是 1:N 广播，API 响应是 1:1 定向。
/// </para>
/// </remarks>
public sealed class NapCatChannel : IMessageChannel, IContactDirectory, ISpeechTranscriber
{
    private readonly IClientTransport _transport;
    private readonly IChatFilter _filter;
    private readonly NapCatOptions _options;
    private readonly ILogger _logger;
    private readonly SpeechOptions _speechOptions;
    private readonly NapCatPttTextTranscriber _speech;

    private readonly System.Threading.Channels.Channel<InboundEvent> _queue =
        System.Threading.Channels.Channel.CreateUnbounded<InboundEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _pending = new();
    private long _selfId;
    private long _blockedCount;

    public NapCatChannel(
        IClientTransport transport,
        IChatFilter filter,
        NapCatOptions options,
        ILogger logger,
        SpeechOptions? speechOptions = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _transport = transport;
        _filter = filter;
        _options = options;
        _logger = logger;
        _speechOptions = speechOptions ?? new SpeechOptions();

        // 转写器复用本通道的 API 管道（`fetch_ptt_text` 是平台扩展 action）
        _speech = new NapCatPttTextTranscriber(CallApiNodeAsync, _speechOptions, logger);
    }

    /// <summary>语音转写器（供 DI 暴露；通道内部也用它）。</summary>
    public ISpeechTranscriber SpeechTranscriber => _speech;

    bool ISpeechTranscriber.IsAvailable => _speech.IsAvailable;

    Task<string?> ISpeechTranscriber.TranscribeAsync(AudioClip clip, CancellationToken ct) =>
        _speech.TranscribeAsync(clip, ct);

    
    // ══════════════════════════════════════════════════════════════════
    // IContactDirectory：好友 / 群 / 群成员（第 17/18/19 项的基础）
    // ══════════════════════════════════════════════════════════════════

    /// <summary>
    /// 允许 AI 自主调用的 action 白名单。
    /// </summary>
    /// <remarks>
    /// 只放**只读查询**。发送消息、退群、禁言这类写操作**不在名单里** ——
    /// 那些必须走有权限门禁的工具，不能被"一个通用 API 调用"绕过去。
    /// 清单来自 <see cref="ChannelApiCatalog"/>（唯一来源）。
    /// </remarks>
    public static readonly IReadOnlySet<string> ActionAllowlist = ChannelApiCatalog.ReadActions;

    /// <summary>
    /// **写操作**白名单（比只读严格得多，且只能由有权限门禁的工具调到）。
    /// </summary>
    private static readonly IReadOnlySet<string> WriteActionAllowlist = ChannelApiCatalog.WriteActions;

    /// <summary>改自己的个性签名（NapCat <c>set_self_longnick</c>）。</summary>
    /// <remarks>
    /// 不走 <see cref="InvokeApiAsync"/> 的白名单 —— 那个是"模型可自主调用"的只读集合，
    /// 写操作必须被**权限门禁**挡着，而不是开放给通用调用。
    /// </remarks>
    public async Task SetSignatureAsync(string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);

        await CallWriteActionAsync("set_self_longnick", new JsonObject
        {
            ["longNick"] = text,
        }, ct).ConfigureAwait(false);
    }

    /// <summary>戳一戳（NapCat <c>send_poke</c>）。</summary>
    public async Task SendPokeAsync(string userId, string? groupId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);

        var args = new JsonObject
        {
            ["user_id"] = userId,
        };

        if (!string.IsNullOrWhiteSpace(groupId))
        {
            args["group_id"] = groupId;
        }

        await CallWriteActionAsync("send_poke", args, ct).ConfigureAwait(false);
    }

    /// <summary>调用**写**操作（不在只读白名单里，但仍在允许集合内）。</summary>
    private async Task CallWriteActionAsync(string action, JsonObject parameters, CancellationToken ct)
    {
        if (!WriteActionAllowlist.Contains(action))
        {
            throw new InvalidOperationException($"action「{action}」不在写操作白名单里。");
        }

        // 复用只读调用的底层管道（同样的 token + 超时策略），只是不检查只读白名单
        await CallApiNodeAsync(action, parameters, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ContactInfo>> GetFriendsAsync(CancellationToken ct = default)
    {
        var data = await CallApiNodeAsync("get_friend_list", null, ct).ConfigureAwait(false);

        // ⚠️ 失败**必须抛**，不能 return []：
        // 返回空列表会让上层和模型都以为「真的一个好友都没有」（实测用户报"好友列表查不到"）。
        // 好友列表不存在"合法的空"——人至少有自己。
        if (data is not JsonArray array)
        {
            throw new InvalidOperationException(
                "查询好友列表失败：NapCat 没有返回有效数据（可能超时或通道异常）。");
        }

        return array
            .OfType<JsonObject>()
            .Select(o => new ContactInfo(
                ReadString(o, "user_id"),
                ReadString(o, "nickname"),
                ReadOptional(o, "remark")))
            .Where(c => c.UserId.Length > 0)
            .ToList();
    }

    public async Task<GroupInfo?> GetGroupAsync(string groupId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        var data = await CallApiNodeAsync(
            "get_group_info",
            new JsonObject { ["group_id"] = groupId },
            ct).ConfigureAwait(false);

        if (data is not JsonObject obj)
        {
            return null;
        }

        return new GroupInfo(
            ReadString(obj, "group_id"),
            ReadString(obj, "group_name"),
            ReadInt(obj, "member_count"),
            ReadInt(obj, "max_member_count"));
    }

    public async Task<IReadOnlyList<GroupInfo>> GetGroupsAsync(CancellationToken ct = default)
    {
        var data = await CallApiNodeAsync("get_group_list", null, ct).ConfigureAwait(false);

        if (data is not JsonArray array)
        {
            throw new InvalidOperationException(
                "查询群列表失败：NapCat 没有返回有效数据（可能超时或通道异常）。");
        }

        return array
            .OfType<JsonObject>()
            .Select(o => new GroupInfo(
                ReadString(o, "group_id"),
                ReadString(o, "group_name"),
                ReadInt(o, "member_count"),
                ReadInt(o, "max_member_count")))
            .Where(g => g.GroupId.Length > 0)
            .ToList();
    }

    public async Task<IReadOnlyList<GroupMemberInfo>> GetGroupMembersAsync(
        string groupId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);

        var data = await CallApiNodeAsync(
            "get_group_member_list",
            new JsonObject { ["group_id"] = groupId },
            ct).ConfigureAwait(false);

        // 同好友列表：失败不能伪装成"群里没人"
        if (data is not JsonArray array)
        {
            throw new InvalidOperationException(
                $"查询群 {groupId} 的成员列表失败：NapCat 没有返回有效数据。");
        }

        return array
            .OfType<JsonObject>()
            .Select(o => new GroupMemberInfo(
                ReadString(o, "user_id"),
                ReadString(o, "nickname"),
                ReadOptional(o, "card"),
                ReadString(o, "role"),
                ReadOptional(o, "title")))
            .Where(m => m.UserId.Length > 0)
            .ToList();
    }

    public Task<JsonNode?> InvokeApiAsync(string action, JsonObject? parameters, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);

        if (!ActionAllowlist.Contains(action))
        {
            throw new InvalidOperationException(
                $"action「{action}」不在可自主调用的白名单里（只允许只读查询）。");
        }

        return CallApiNodeAsync(action, parameters, ct);
    }

    /// <summary>调一个 OneBot API 并返回 <c>data</c>（失败返回 null，不抛）。</summary>
    private async Task<JsonNode?> CallApiNodeAsync(string action, JsonObject? parameters, CancellationToken ct)
    {
        var echo = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pending.TryAdd(echo, tcs))
        {
            throw new InvalidOperationException("生成 echo 冲突（理论上不会发生）");
        }

        try
        {
            var payload = new JsonObject
            {
                ["action"] = action,
                ["params"] = parameters ?? new JsonObject(),
                ["echo"] = echo,
            };

            await _transport.SendAsync(payload).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.ApiTimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            JsonObject response;
            try
            {
                response = await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                _logger.Warning("API {Action} 超时（{Timeout} 秒）", action, _options.ApiTimeoutSeconds);
                return null;
            }

            var status = response["status"]?.GetValue<string>();
            if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warning("API {Action} 返回非 ok：{Status}", action, status);
                return null;
            }

            return response["data"]?.DeepClone();
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "API {Action} 调用失败", action);
            return null;
        }
        finally
        {
            _pending.TryRemove(echo, out _);
        }
    }

    private static string ReadString(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s
        : obj[name]?.ToString() ?? string.Empty;

    private static string? ReadOptional(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    private static int ReadInt(JsonObject obj, string name) =>
        obj[name] is JsonValue v && v.TryGetValue<int>(out var i) ? i : 0;

    public string Platform => "qq";

    public ChannelCapabilities Capabilities =>
        ChannelCapabilities.Text | ChannelCapabilities.Image | ChannelCapabilities.Mention | ChannelCapabilities.Reply;

    /// <summary>机器人自身账号；未知时为 null。</summary>
    public string? SelfId
    {
        get
        {
            var value = Interlocked.Read(ref _selfId);
            return value > 0 ? value.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
        }
    }

    /// <summary>首次发现 / 变更自身账号。</summary>
    public event Action<string>? SelfIdChanged;

    /// <summary>因不在名单内而被丢弃的事件数（FR-17.4，**只计数不记内容**）。</summary>
    public long BlockedCount => Interlocked.Read(ref _blockedCount);

    /// <summary>建立连接并开始接收。</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        _transport.EventHandler = OnTransportFrame;

        if (!Uri.TryCreate(_options.WebSocketUrl, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"NapCat WebSocketUrl 不是合法地址：{_options.WebSocketUrl}");
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.ConnectTimeoutSeconds));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        await _transport.ConnectAsync(uri, _options.AccessToken, linked.Token).ConfigureAwait(false);

        _logger.Information(
            "NapCat 通道已连接：{Url}（白名单 {White} 条 / 黑名单 {Black} 条 / 主人放行 {Allow} 条；语音转写 {Stt}）",
            uri, _filter.WhitelistCount, _filter.BlacklistCount, _filter.AlwaysAllow.Count,
            _speechOptions.Enabled ? "启用" : "关闭");
    }

    public async IAsyncEnumerable<InboundEvent> ReceiveAsync(
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var evt in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    public async Task SendAsync(OutgoingMessage message, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(message);

        var action = message.Chat.Type == ChatType.Group ? "send_group_msg" : "send_private_msg";
        var idKey = message.Chat.Type == ChatType.Group ? "group_id" : "user_id";

        var echo = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_pending.TryAdd(echo, tcs))
        {
            throw new InvalidOperationException("生成 echo 冲突（理论上不会发生）");
        }

        try
        {
            var payload = new JsonObject
            {
                ["action"] = action,
                ["params"] = new JsonObject
                {
                    [idKey] = message.Chat.Id,
                    ["message"] = BuildMessageArray(message),
                },
                ["echo"] = echo,
            };

            await _transport.SendAsync(payload).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_options.ApiTimeoutSeconds));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            JsonObject response;
            try
            {
                response = await tcs.Task.WaitAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
            {
                throw new TimeoutException($"发送消息超时（{_options.ApiTimeoutSeconds} 秒）");
            }

            var status = response["status"]?.GetValue<string>();
            if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
            {
                // 失败要抛出，由上层转成「回填给模型的失败原因」（FR-2.3）
                var detail = response["message"]?.ToString() ?? response.ToJsonString();
                throw new InvalidOperationException($"发送失败：{detail}");
            }
        }
        finally
        {
            _pending.TryRemove(echo, out _);
        }
    }

    private async ValueTask OnTransportFrame(JsonNode json)
    {
        if (json is not JsonObject obj)
        {
            return;
        }

        // ① API 响应对号入座（有 echo 且有人在等）
        if (obj["echo"] is JsonValue echoValue
            && echoValue.TryGetValue<string>(out var echo)
            && _pending.TryRemove(echo, out var tcs))
        {
            tcs.TrySetResult(obj);
            return;
        }

        // ② 其余按事件处理
        var evt = OneBotEventMapper.Map(obj, Platform);
        if (evt is null)
        {
            return;
        }

        // 记录机器人自身账号（心跳/生命周期事件里带 self_id），用于过滤自激
        if (obj["self_id"] is JsonValue selfValue && selfValue.TryGetValue<long>(out var selfId))
        {
            var previous = Interlocked.Exchange(ref _selfId, selfId);
            if (previous != selfId && selfId > 0)
            {
                var text = selfId.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _logger.Information("已识别机器人账号：{SelfId}", text);
                SelfIdChanged?.Invoke(text);
            }
        }

        // 自激过滤（消息）：自己发出的消息也会作为 message 事件回来
        var self = SelfId;
        if (evt is IncomingMessage msg && self is not null && msg.Sender.UserId == self)
        {
            return;
        }

        // 自激过滤（通知）：自己触发 / 自己是被操作者 → 丢弃（问题记录 #39）。
        // 只有通道层拿得到**动态** SelfId；路由层用配置/持久化自 ID 再兜一次。
        if (evt is IncomingNotice notice && NoticeSelfPolicy.IsAboutSelf(notice, self))
        {
            _logger.Information(
                "丢弃自身相关通知：{Type}（Actor={Actor}, Target={Target}, Self={Self}）",
                notice.Type,
                notice.Actor?.UserId,
                notice.Target?.UserId,
                self);
            return;
        }

        // 接收过滤（FR-17.3）：不在名单内 → 不落库、不进模型
        if (!_filter.IsAllowed(evt.Chat))
        {
            Interlocked.Increment(ref _blockedCount);
            return;
        }

        // 语音转写（FR-1.5，通道层完成）：放在过滤之后 —— 被过滤的聊天不必花一次转写
        if (evt is IncomingMessage message)
        {
            evt = await EnrichRecordsAsync(message).ConfigureAwait(false);
        }

        if (!_queue.Writer.TryWrite(evt))
        {
            _logger.Warning("事件队列写入失败，已丢弃一条事件");
        }
    }

    /// <summary>把语音段补齐成转写文本；转写不可用时原样返回。</summary>
    private async Task<IncomingMessage> EnrichRecordsAsync(IncomingMessage message)
    {
        if (!_speechOptions.Enabled || !_speech.IsAvailable)
        {
            return message;
        }

        List<MessageSegment>? replaced = null;
        for (var i = 0; i < message.Segments.Count; i++)
        {
            if (message.Segments[i] is not RecordSegment record ||
                string.IsNullOrWhiteSpace(record.FileId))
            {
                continue;
            }

            var text = await _speech
                .TranscribeAsync(new AudioClip(
                    record.FileId,
                    record.Url,
                    MessageId: message.EventId))
                .ConfigureAwait(false);

            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            replaced ??= message.Segments.ToList();
            replaced[i] = record with { Text = text, Transcriber = NapCatPttTextTranscriber.ActionName };

            _logger.Information("语音已转写：file={File} 长度 {Length}", record.FileId, text.Length);
        }

        return replaced is null ? message : message with { Segments = replaced };
    }

    private static JsonArray BuildMessageArray(OutgoingMessage message)
    {
        var array = new JsonArray();

        foreach (var segment in message.Segments)
        {
            switch (segment)
            {
                case TextSegment text:
                    array.Add(new JsonObject
                    {
                        ["type"] = "text",
                        ["data"] = new JsonObject { ["text"] = text.Text },
                    });
                    break;

                case MentionSegment mention:
                    array.Add(new JsonObject
                    {
                        ["type"] = "at",
                        ["data"] = new JsonObject { ["qq"] = mention.UserId },
                    });
                    break;

                case ReplySegment reply:
                    array.Add(new JsonObject
                    {
                        ["type"] = "reply",
                        ["data"] = new JsonObject { ["id"] = reply.MessageId },
                    });
                    break;

                case FaceSegment face:
                    array.Add(new JsonObject
                    {
                        ["type"] = "face",
                        ["data"] = new JsonObject { ["id"] = face.Id },
                    });
                    break;

                case MfaceSegment mface:
                    // 表情包：emoji_id 是必需字段；没有就退化成发图（有 url 时）
                    if (!string.IsNullOrEmpty(mface.EmojiId))
                    {
                        var mdata = new JsonObject
                        {
                            ["emoji_id"] = mface.EmojiId,
                            // NapCat 通常要求这两个字段，缺省用 "1" 能发出去
                            ["emoji_package_id"] = "1",
                            ["key"] = string.Empty,
                            ["summary"] = mface.Summary ?? string.Empty,
                        };

                        array.Add(new JsonObject
                        {
                            ["type"] = "mface",
                            ["data"] = mdata,
                        });
                    }
                    else if (!string.IsNullOrEmpty(mface.Url))
                    {
                        array.Add(new JsonObject
                        {
                            ["type"] = "image",
                            ["data"] = new JsonObject { ["file"] = mface.Url },
                        });
                    }

                    break;

                case ImageSegment image:
                    var file = image.FileId ?? image.Url;
                    if (!string.IsNullOrEmpty(file))
                    {
                        array.Add(new JsonObject
                        {
                            ["type"] = "image",
                            ["data"] = new JsonObject { ["file"] = file },
                        });
                    }
                    break;
            }
        }

        return array;
    }
}
