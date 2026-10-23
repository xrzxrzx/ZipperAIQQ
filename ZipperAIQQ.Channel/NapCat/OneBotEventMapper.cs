using System.Text.Json.Nodes;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Channel.NapCat;

/// <summary>
/// OneBot 11 事件 → 平台无关模型。
/// </summary>
/// <remarks>
/// <para>
/// **纯函数、无副作用**：这是全项目最容易写错的一块（字段名、嵌套、类型），
/// 做成纯函数才能拿真实报文直接断言。
/// </para>
/// <para>
/// 只映射 <c>post_type</c> 为 <c>message</c> / <c>notice</c> 的事件；
/// <c>meta_event</c>（心跳等）与 <c>request</c>（加群/加好友申请）返回 <c>null</c>，
/// 由调用方丢弃 —— 前者是噪音，后者 M1 不处理。
/// </para>
/// </remarks>
public static class OneBotEventMapper
{
    /// <summary>
    /// 尝试把一帧 OneBot 事件映射成入站事件。
    /// </summary>
    /// <param name="json">原始事件。</param>
    /// <param name="platform">平台标识（如 <c>qq</c>）。</param>
    /// <returns>映射结果；非消息/通知事件返回 <c>null</c>。</returns>
    public static InboundEvent? Map(JsonNode? json, string platform)
    {
        if (json is not JsonObject obj)
        {
            return null;
        }

        return GetString(obj, "post_type") switch
        {
            "message" => MapMessage(obj, platform),
            "notice" => MapNotice(obj, platform),
            "request" => MapRequest(obj, platform),
            _ => null,
        };
    }

    /// <summary>
    /// 请求事件（加好友 / 加群）：**只映射用于记录**，上层默认不响应。
    /// </summary>
    private static IncomingNotice? MapRequest(JsonObject obj, string platform)
    {
        var requestType = GetString(obj, "request_type");
        if (requestType is not ("friend" or "group"))
        {
            return null;
        }

        var userId = GetString(obj, "user_id");
        var groupId = GetString(obj, "group_id");

        var chat = new ChatRef
        {
            Platform = platform,
            Type = requestType == "group" ? ChatType.Group : ChatType.Private,
            Id = requestType == "group" ? groupId ?? "unknown" : userId ?? "unknown",
        };

        return new IncomingNotice
        {
            Chat = chat,
            Type = requestType == "group" ? NoticeType.GroupRequest : NoticeType.FriendRequest,
            RawType = "request." + requestType,
            Actor = userId is null ? null : new SenderInfo { UserId = userId },
            Target = null,
            OccurredAt = ParseTime(obj),
            Extra = BuildNoticeExtra(obj),
        };
    }

    private static IncomingMessage? MapMessage(JsonObject obj, string platform)
    {
        var messageType = GetString(obj, "message_type");
        var isGroup = messageType == "group";

        var chatId = isGroup
            ? GetString(obj, "group_id")
            : GetString(obj, "user_id");

        if (string.IsNullOrEmpty(chatId))
        {
            return null;
        }

        var chat = new ChatRef
        {
            Platform = platform,
            Type = isGroup ? ChatType.Group : ChatType.Private,
            Id = chatId,
        };

        var senderObj = obj["sender"] as JsonObject;
        var sender = new SenderInfo
        {
            UserId = GetString(senderObj ?? obj, "user_id") ?? GetString(obj, "user_id") ?? "unknown",
            GroupCard = GetNonEmpty(senderObj, "card"),
            Nickname = GetNonEmpty(senderObj, "nickname"),
            // 群内身份（第 18 项）：OneBot 群消息的 sender 里带 role / title。
            // 有了它模型才能分清「群主 / 群管理 / 普通成员」，与客户端配的管理员是两回事。
            GroupRole = isGroup ? GetNonEmpty(senderObj, "role") : null,
            GroupTitle = isGroup ? GetNonEmpty(senderObj, "title") : null,
            // 私聊能发消息 → 对方是好友；群聊里是否为好友**事件里没有**，需另行查询。
            IsFriend = !isGroup,
        };

        return new IncomingMessage
        {
            Chat = chat,
            Sender = sender,
            Segments = ParseSegments(obj),
            OccurredAt = ParseTime(obj),
            EventId = GetString(obj, "message_id"),
            ReplyToMessageId = FindReplyId(obj),
        };
    }

    private static IncomingNotice? MapNotice(JsonObject obj, string platform)
    {
        var noticeType = GetString(obj, "notice_type");
        var subType = GetString(obj, "sub_type");

        var groupId = GetString(obj, "group_id");

        var chat = new ChatRef
        {
            Platform = platform,
            Type = string.IsNullOrEmpty(groupId) ? ChatType.Private : ChatType.Group,
            Id = string.IsNullOrEmpty(groupId)
                ? (GetString(obj, "user_id") ?? "unknown")
                : groupId,
        };

        var userId = GetString(obj, "user_id");
        var targetId = GetString(obj, "target_id");
        var operatorId = GetString(obj, "operator_id");

        // 群成员增减事件**没有 target_id**：被操作者就是 user_id。
        // 不补这一步 → Target 恒为 null → 文案退化成「某人」、自激过滤也没有靶子（问题记录 #39）。
        var isMemberChange = noticeType is "group_increase" or "group_decrease";
        var resolvedTargetId = targetId ?? (isMemberChange ? userId : null);

        // 操作者 / 被操作者分开记（FR-18.4）：戳一戳是「A 戳 B」，不是「发生了一次戳一戳」
        // 成员增减缺 operator_id 时不要拿 user_id 顶替 —— 那会把「新成员」同时记成操作者
        var actorId = !string.IsNullOrEmpty(operatorId) && operatorId != "0"
            ? operatorId
            : (isMemberChange ? null : userId);
        var actor = actorId is null ? null : new SenderInfo { UserId = actorId };
        var target = resolvedTargetId is null ? null : new SenderInfo { UserId = resolvedTargetId };

        var type = MapNoticeType(noticeType, subType);

        return new IncomingNotice
        {
            Chat = chat,
            Type = type,
            RawType = string.IsNullOrEmpty(subType) ? (noticeType ?? "unknown") : $"{noticeType}.{subType}",
            Actor = actor,
            Target = target,
            RelatedMessageId = GetString(obj, "message_id"),
            OccurredAt = ParseTime(obj),
            EventId = null,
            Extra = BuildNoticeExtra(obj),
        };
    }

    private static NoticeType MapNoticeType(string? noticeType, string? subType) => (noticeType, subType) switch
    {
        ("notify", "poke") => NoticeType.Poke,
        ("notify", "group_name") => NoticeType.GroupNameChanged,
        ("notify", "title") => NoticeType.GroupTitleChanged,
        ("notify", "gray_tip") => NoticeType.GroupGrayTip,
        ("notify", "profile_like") => NoticeType.ProfileLike,
        ("notify", "input_status") => NoticeType.InputStatus,
        ("notify", _) => NoticeType.Unknown,
        ("group_increase", _) => NoticeType.GroupIncrease,
        ("group_decrease", _) => NoticeType.GroupDecrease,
        ("group_admin", _) => NoticeType.GroupAdminChanged,
        ("group_ban", _) => NoticeType.GroupBan,
        ("group_recall", _) => NoticeType.MessageRecalled,
        ("friend_recall", _) => NoticeType.MessageRecalled,
        ("friend_add", _) => NoticeType.FriendAdd,
        ("group_upload", _) => NoticeType.GroupFileUpload,
        ("group_card", _) => NoticeType.GroupCardChanged,
        ("essence", _) => NoticeType.EssenceChanged,
        ("group_msg_emoji_like", _) => NoticeType.MessageEmojiLike,
        ("bot_offline", _) => NoticeType.BotOffline,
        _ => NoticeType.Unknown,
    };

    private static Dictionary<string, string>? BuildNoticeExtra(JsonObject obj)
    {
        // 只挑少数有语义的字段，避免把整帧塞进去（既占存储又可能带敏感内容）
        var extra = new Dictionary<string, string>();
        foreach (var key in new[] { "duration", "sub_type", "target_id" })
        {
            var value = GetString(obj, key);
            if (value is not null)
            {
                extra[key] = value;
            }
        }

        return extra.Count == 0 ? null : extra;
    }

    /// <summary>解析消息段数组。缺失时退化为 raw_message 纯文本。</summary>
    private static IReadOnlyList<MessageSegment> ParseSegments(JsonObject obj)
    {
        if (obj["message"] is not JsonArray array)
        {
            var raw = GetString(obj, "raw_message") ?? string.Empty;
            return raw.Length == 0
                ? Array.Empty<MessageSegment>()
                : new[] { new TextSegment(raw) };
        }

        var segments = new List<MessageSegment>(array.Count);

        foreach (var node in array)
        {
            if (node is not JsonObject seg)
            {
                continue;
            }

            var type = GetString(seg, "type");
            var data = seg["data"] as JsonObject;

            switch (type)
            {
                case "text":
                    var text = GetString(data ?? seg, "text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        segments.Add(new TextSegment(text));
                    }
                    break;

                case "image":
                    segments.Add(new ImageSegment(
                        Url: GetNonEmpty(data, "url"),
                        FileId: GetNonEmpty(data, "file")));
                    break;

                case "at":
                    var qq = GetString(data ?? seg, "qq");
                    if (!string.IsNullOrEmpty(qq))
                    {
                        segments.Add(new MentionSegment(qq));
                    }
                    break;

                case "reply":
                    var replyId = GetString(data ?? seg, "id");
                    if (!string.IsNullOrEmpty(replyId))
                    {
                        segments.Add(new ReplySegment(replyId));
                    }
                    break;

                case "mface":
                    // 表情包：保留 id/url/summary —— 出站复读要用
                    segments.Add(new MfaceSegment(
                        EmojiId: GetNonEmpty(data, "emoji_id"),
                        Url: GetNonEmpty(data, "url"),
                        Summary: GetNonEmpty(data, "summary")));
                    break;

                case "face":
                    // 系统表情：保留 id，名称由上层按平台表情表渲染
                    var faceId = GetString(data ?? seg, "id");
                    if (!string.IsNullOrEmpty(faceId))
                    {
                        segments.Add(new FaceSegment(faceId));
                    }
                    break;

                case "record":
                    // 语音：先保留文件标识，**转写由通道层补齐**（FR-1.5）。
                    // 以前这里落进 default 直接变成 "[语音]"，音频内容从未被获取（问题记录 #1）。
                    segments.Add(new RecordSegment(
                        FileId: GetNonEmpty(data, "file"),
                        Url: GetNonEmpty(data, "url"),
                        Text: null,
                        Transcriber: null));
                    break;

                default:
                    // 未专门处理的段 → **转成可读占位**，而不是丢弃。
                    // 丢弃会让 AI 完全不知道"这里有个表情/语音"，读起来就像对方什么都没发。
                    var label = type switch
                    {
                        // 表情包：带上 summary（"猫猫头"这类），但**标明它是表情包**
                        // 只给 [表情] 两个字时，模型读不出"对方发了个表情"，也不会跟着发表情
                        "mface" => "[表情包]",
                        "record" => "[语音]",
                        "video" => "[视频]",
                        "json" => "[卡片消息]",
                        "file" => "[文件]",
                        "forward" => "[合并转发]",
                        _ => "[" + (type ?? "未知") + "]",
                    };
                    segments.Add(new TextSegment(label));
                    break;
            }
        }

        return segments;
    }

    private static string? FindReplyId(JsonObject obj)
    {
        if (obj["message"] is not JsonArray array)
        {
            return null;
        }

        foreach (var node in array)
        {
            if (node is JsonObject seg && GetString(seg, "type") == "reply")
            {
                return GetString(seg["data"] as JsonObject ?? seg, "id");
            }
        }

        return null;
    }

    /// <summary>OneBot 的 <c>time</c> 是 **Unix 秒**，不是毫秒。</summary>
    private static DateTimeOffset ParseTime(JsonObject obj)
    {
        var seconds = obj["time"]?.GetValue<long>() ?? 0;
        return seconds <= 0
            ? DateTimeOffset.UtcNow
            : DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private static string? GetString(JsonObject? obj, string key)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        return node switch
        {
            JsonValue v when v.TryGetValue<string>(out var s) => s,
            _ => node.ToJsonString(),
        };
    }

    private static string? GetNonEmpty(JsonObject? obj, string key)
    {
        var value = GetString(obj, key);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
