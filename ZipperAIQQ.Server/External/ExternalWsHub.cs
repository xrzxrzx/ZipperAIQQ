using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Serilog;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Server.External;

/// <summary>一个外部 WS 连接（反向连入或正向连出都用它）。</summary>
public sealed class ExternalSession
{
    /// <summary>每连接出站缓冲上限：慢客户端只会丢旧事件，不会把服务端内存拖爆。</summary>
    private const int MaxOutbox = 512;

    private readonly Channel<string> _outbox;
    private volatile bool _messages = true;
    private volatile bool _notices = true;

    public ExternalSession(Guid id, string name)
    {
        Id = id;
        Name = name;
        _outbox = System.Threading.Channels.Channel.CreateBounded<string>(new BoundedChannelOptions(MaxOutbox)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    }

    public Guid Id { get; }

    public string Name { get; }

    public ChannelReader<string> Reader => _outbox.Reader;

    public void SetSubscriptions(IEnumerable<string> events)
    {
        var set = new HashSet<string>(events, StringComparer.OrdinalIgnoreCase);
        _messages = set.Contains("message");
        _notices = set.Contains("notice");
    }

    public bool Wants(string kind) => kind switch
    {
        "message" => _messages,
        "notice" => _notices,
        _ => false,
    };

    /// <summary>入队；返回 <c>false</c> 表示队列已满（调用方记一次丢弃）。</summary>
    public bool Enqueue(string frame) => _outbox.Writer.TryWrite(frame);

    internal void Close() => _outbox.Writer.TryComplete();
}

/// <summary>
/// 外部应用事件广播（问题记录 03 #5）：消息 / 通知按订阅推给外部连接。
/// </summary>
public sealed class ExternalWsHub
{
    private readonly ConcurrentDictionary<Guid, ExternalSession> _sessions = new();
    private readonly ILogger _logger;
    private readonly IMessageChannel _channel;
    private readonly Configuration.ServerOptions _options;
    private int _dropped;

    public ExternalWsHub(ILogger logger, IMessageChannel channel, Configuration.ServerOptions options)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(options);

        _logger = logger;
        _channel = channel;
        _options = options;
    }

    public int Count => _sessions.Count;

    public ExternalSession Add(string name)
    {
        var session = new ExternalSession(Guid.NewGuid(), name);
        _sessions[session.Id] = session;
        _logger.Information("外部 WS 已连接：{Name}（当前 {Count} 个）", name, _sessions.Count);
        return session;
    }

    public void Remove(ExternalSession session)
    {
        if (_sessions.TryRemove(session.Id, out _))
        {
            session.Close();
            _logger.Information("外部 WS 已断开：{Name}（剩余 {Count} 个）", session.Name, _sessions.Count);
        }
    }

    public void PublishHello(ExternalSession session)
    {
        // 现读：外部连接可能早于 QQ 握手，缓存值会让它永远拿到 null
        var selfId = _channel.SelfIdFor(_channel.Platform) ?? _channel.SelfId ?? _options.SelfId;

        session.Enqueue(ExternalProtocol.Frame("hello", new
        {
            version = ExternalProtocol.Version,
            selfId,
            capabilities = _channel.Capabilities.ToString(),
        }));
    }

    public void PublishMessage(IncomingMessage message)    {
        var payload = new
        {
            messageId = message.EventId,
            session = message.Chat.SessionId,
            chatType = message.Chat.Type.ToString(),
            sender = new
            {
                userId = message.Sender.UserId,
                nickname = message.Sender.Nickname,
                displayName = message.Sender.DisplayName,
            },
            text = message.PlainText,
            time = message.OccurredAt.ToUnixTimeMilliseconds(),
            capabilities = _channel.Capabilities.ToString(),
        };

        Broadcast("message", message.Chat.SessionId, payload);
    }

    public void PublishNotice(IncomingNotice notice)
    {
        var payload = new
        {
            type = notice.Type.ToString(),
            rawType = notice.RawType,
            session = notice.Chat.SessionId,
            actor = notice.Actor?.UserId,
            target = notice.Target?.UserId,
            relatedMessageId = notice.RelatedMessageId,
            time = notice.OccurredAt.ToUnixTimeMilliseconds(),
        };

        Broadcast("notice", notice.Chat.SessionId, payload);
    }

    private void Broadcast(string kind, string session, object payload)
    {
        if (_sessions.IsEmpty)
        {
            return;
        }

        var frame = ExternalProtocol.Event(kind, session, payload);
        foreach (var client in _sessions.Values)
        {
            if (!client.Wants(kind))
            {
                continue;
            }

            if (!client.Enqueue(frame))
            {
                var dropped = Interlocked.Increment(ref _dropped);
                if (dropped % 100 == 1)
                {
                    _logger.Warning("外部 WS 出站队列已满，丢弃事件（累计 {Dropped} 次）", dropped);
                }
            }
        }
    }
}
