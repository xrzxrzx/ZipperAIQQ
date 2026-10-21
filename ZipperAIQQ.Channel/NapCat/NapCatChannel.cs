using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Serilog;
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
public sealed class NapCatChannel : IMessageChannel
{
    private readonly IClientTransport _transport;
    private readonly IChatFilter _filter;
    private readonly NapCatOptions _options;
    private readonly ILogger _logger;

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
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _transport = transport;
        _filter = filter;
        _options = options;
        _logger = logger;
    }

    public string Platform => "qq";

    public ChannelCapabilities Capabilities =>
        ChannelCapabilities.Text | ChannelCapabilities.Image | ChannelCapabilities.Mention | ChannelCapabilities.Reply;

    /// <summary>机器人自身账号（用于过滤自己发出的消息，避免自激循环）。</summary>
    public long SelfId => Interlocked.Read(ref _selfId);

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

        _logger.Information("NapCat 通道已连接：{Url}（名单模式 {Mode}，条目 {Count} 条）",
            uri, _filter.Mode, _filter.Count);
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

    private ValueTask OnTransportFrame(JsonNode json)
    {
        if (json is not JsonObject obj)
        {
            return ValueTask.CompletedTask;
        }

        // ① API 响应对号入座（有 echo 且有人在等）
        if (obj["echo"] is JsonValue echoValue
            && echoValue.TryGetValue<string>(out var echo)
            && _pending.TryRemove(echo, out var tcs))
        {
            tcs.TrySetResult(obj);
            return ValueTask.CompletedTask;
        }

        // ② 其余按事件处理
        var evt = OneBotEventMapper.Map(obj, Platform);
        if (evt is null)
        {
            return ValueTask.CompletedTask;
        }

        // 记录机器人自身账号（心跳/生命周期事件里带 self_id），用于过滤自激
        if (obj["self_id"] is JsonValue selfValue && selfValue.TryGetValue<long>(out var selfId))
        {
            Interlocked.Exchange(ref _selfId, selfId);
        }

        // 自激过滤：自己发出的消息也会作为 message 事件回来
        if (evt is IncomingMessage msg && msg.Sender.UserId == SelfId.ToString())
        {
            return ValueTask.CompletedTask;
        }

        // 接收过滤（FR-17.3）：不在名单内 → 不落库、不进模型
        if (!_filter.IsAllowed(evt.Chat))
        {
            Interlocked.Increment(ref _blockedCount);
            return ValueTask.CompletedTask;
        }

        if (!_queue.Writer.TryWrite(evt))
        {
            _logger.Warning("事件队列写入失败，已丢弃一条事件");
        }

        return ValueTask.CompletedTask;
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
