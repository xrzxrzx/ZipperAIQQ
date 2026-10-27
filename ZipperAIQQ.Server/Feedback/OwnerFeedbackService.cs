using Serilog;
using ZipperAIQQ.Agent.Tools;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Access;
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

    /// <summary>伪类型：外部接口发出的消息（不是 <see cref="NoticeType"/>）。</summary>
    public const string ExternalMessageType = "ExternalMessage";

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
        if (!types.Contains(notice.Type.ToString()))
        {
            return;
        }

        // 主人按平台取（S3-C）：抖音的通知要发给抖音的主人，不是 QQ 的主人
        var platform = AccessKeys.Normalize(notice.Chat.Platform);
        var ownerId = await AccessSettings.GetOwnerAsync(_settings, platform, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            _logger.Warning("主人反馈：{Platform} 平台还没有设置主人，跳过", platform);
            return;
        }

        // 防自激：主人自己撤回 / 自己触发的事件不转发
        if (string.Equals(notice.Actor?.UserId, ownerId, StringComparison.Ordinal))
        {
            return;
        }

        var chat = await ResolveTargetAsync(platform, ownerId, ct).ConfigureAwait(false);

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

    /// <summary>外部接口发消息后的反馈（伪类型 <see cref="ExternalMessageType"/>）。</summary>
    public async Task NotifyExternalAsync(ChatRef chat, string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(chat);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        if (!await IsEnabledAsync(ct).ConfigureAwait(false))
        {
            return;
        }

        var types = await GetTypesAsync(ct).ConfigureAwait(false);
        if (!types.Contains(ExternalMessageType))
        {
            return;
        }

        // 外部接口消息：主人取该会话所属平台（S3-C）
        var platform = AccessKeys.Normalize(chat.Platform);
        var ownerId = await AccessSettings.GetOwnerAsync(_settings, platform, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(ownerId))
        {
            return;
        }

        var target = await ResolveTargetAsync(platform, ownerId, ct).ConfigureAwait(false);
        try
        {
            await _channel.SendAsync(new OutgoingMessage
            {
                Chat = target,
                Segments = [new TextSegment($"[外部接口] {chat.SessionId}：{text}")],
            }, ct).ConfigureAwait(false);

            _logger.Information("主人反馈已发送：ExternalMessage → {Session}", target.SessionId);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "主人反馈发送失败：ExternalMessage");
        }
    }

    public async Task<bool> IsEnabledAsync(CancellationToken ct = default)
    {
        var value = await _settings.GetAsync(EnabledKey, ct).ConfigureAwait(false);
        return value is "1" or "true" or "True";
    }

    public async Task<IReadOnlySet<string>> GetTypesAsync(CancellationToken ct = default)
    {
        var raw = await _settings.GetAsync(TypesKey, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = DefaultTypes;
        }

        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            types.Add(item);
        }

        return types;
    }

    /// <summary>反馈发送目标：优先用配置的会话（须属于同一平台），否则该平台的主人私聊。</summary>
    private async Task<ChatRef> ResolveTargetAsync(string platform, string ownerId, CancellationToken ct)
    {
        var configured = await _settings.GetAsync(TargetKey, ct).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(configured) &&
            SessionRefParser.TryParse(configured, platform, out var parsed, out _) &&
            string.Equals(parsed.Platform, platform, StringComparison.OrdinalIgnoreCase))
        {
            return parsed;
        }

        return new ChatRef
        {
            Platform = platform,
            Type = ChatType.Private,
            Id = ownerId,
        };
    }
}
