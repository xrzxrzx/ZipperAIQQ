using Serilog;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Storage.Messages;
using ZipperAIQQ.Storage.Settings;

namespace ZipperAIQQ.Server.Feedback;

/// <summary>
/// 主人反馈（问题记录 03 #4）：指定类型的通知 → 主动发到主人私聊 / 指定会话。
/// </summary>
/// <remarks>
/// <para>
/// 与 <c>notice.reply:*</c> 解耦：那个开关决定"要不要在通知发生的会话里说话"，
/// 这个决定"要不要把事件抄送给主人"。
/// </para>
/// <para>
/// 撤回会按 <c>RelatedMessageId</c> 去库里取原消息，把内容一起带上。
/// </para>
/// </remarks>
public sealed class OwnerFeedbackService
{
    public const string EnabledKey = "feedback.enabled";
    public const string TypesKey = "feedback.types";
    public const string TargetKey = "feedback.target";
    public const string DefaultTypes = "MessageRecalled";

    private readonly IRuntimeSettingsStore _settings;
    private readonly IMessageStore _messages;
    private readonly IMessageChannel _channel;
    private readonly ILogger _logger;

    public OwnerFeedbackService(
        IRuntimeSettingsStore settings,
        IMessageStore messages,
        IMessageChannel channel,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(messages);
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(logger);

        _settings = settings;
        _messages = messages;
        _channel = channel;
        _logger = logger;
    }

    public async Task HandleAsync(IncomingNotice notice, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notice);

        if (!await IsEnabledAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var types = await GetTypesAsync(ct).ConfigureAwait(false);
        if (!types.Contains(notice.Type))
        {
            return;
        }

        var ownerId = await _settings.GetAsync("owner.userId", ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            _logger.Warning("主人反馈：还没有设置主人，跳过");
            return;
        }

        // 防自激：主人自己撤回 / 自己触发的事件不转发
        if (string.Equals(notice.Actor?.UserId, ownerId, StringComparison.Ordinal))
        {
            return;
        }

        var chat = await ResolveTargetAsync(ownerId, ct).ConfigureAwait(false);

        StoredMessage? original = null;
        if (notice.Type == NoticeType.MessageRecalled &&
            !string.IsNullOrWhiteSpace(notice.RelatedMessageId))
        {
            try
            {
                original = await _messages
                    .FindByMessageIdAsync(notice.Chat.SessionId, notice.RelatedMessageId, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "主人反馈：查撤回原消息失败（{MessageId}）", notice.RelatedMessageId);
            }
        }

        var text = FeedbackTextBuilder.Build(notice, original, _channel.SelfId);

        try
        {
            await _channel.SendAsync(new OutgoingMessage
            {
                Chat = chat,
                Segments = [new TextSegment(text)],
            }, ct).ConfigureAwait(false);

            _logger.Information(
                "主人反馈已发送：{Type} → {Session}（含原文={HasOriginal}）",
                notice.Type, chat.SessionId, original is not null);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "主人反馈发送失败：{Type} → {Session}", notice.Type, chat.SessionId);
        }
    }

    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        var value = await _settings.GetAsync(EnabledKey, ct).ConfigureAwait(false);
        return value is "1" or "true" or "True";
    }

    public async Task<IReadOnlySet<NoticeType>> GetTypesAsync(CancellationToken ct = default)
    {
        var raw = await _settings.GetAsync(TypesKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = DefaultTypes;
        }

        var types = new HashSet<NoticeType>();
        foreach (var item in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Enum.TryParse<NoticeType>(item, ignoreCase: true, out var type) && Enum.IsDefined(type))
            {
                types.Add(type);
            }
        }

        return types;
    }

    private async Task<ChatRef> ResolveTargetAsync(string ownerId, CancellationToken ct)
    {
        var configured = await _settings.GetAsync(TargetKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(configured) &&
            SessionRefParser.TryParse(configured, _channel.Platform, out var parsed, out _))
        {
            return parsed;
        }

        return new ChatRef
        {
            Platform = _channel.Platform,
            Type = ChatType.Private,
            Id = ownerId,
        };
    }
}
