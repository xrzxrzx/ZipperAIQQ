using System.Text.Json;
using System.Text.Json.Nodes;
using ZipperAIQQ.Channel;
using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Server.Channels;

/// <summary>
/// <c>/ws/channel</c> 的帧类型（设计 §3.2）。
/// </summary>
/// <remarks>
/// 方向是**不对称**的：<c>hello</c> / <c>inbound</c> / <c>ack</c> / <c>result</c> 由 sidecar 发来，
/// <c>outbound</c> / <c>query.*</c> / <c>ping</c> 由 Server 发出去。
/// </remarks>
public static class DouyinFrameTypes
{
    /// <summary>sidecar → Server：握手（版本、selfId、能力）。</summary>
    public const string Hello = "hello";

    /// <summary>sidecar → Server：一条入站消息。</summary>
    public const string Inbound = "inbound";

    /// <summary>Server → sidecar：一条出站消息（带 requestId）。</summary>
    public const string Outbound = "outbound";

    /// <summary>sidecar → Server：出站结果（requestId 配对）。</summary>
    public const string Ack = "ack";

    /// <summary>Server → sidecar：查询好友候选。</summary>
    public const string QueryContacts = "query.contacts";

    /// <summary>Server → sidecar：查询群候选。</summary>
    public const string QueryGroups = "query.groups";

    /// <summary>sidecar → Server：查询结果（requestId 配对）。</summary>
    public const string Result = "result";

    /// <summary>Server → sidecar：心跳（MVP 用 WS 层 ping，保留）。</summary>
    public const string Ping = "ping";

    /// <summary>sidecar → Server：心跳应答。</summary>
    public const string Pong = "pong";
}

/// <summary>一帧 <c>/ws/channel</c> 消息：<c>{type, time, data}</c>（设计 §3.1）。</summary>
/// <param name="Type">帧类型，见 <see cref="DouyinFrameTypes"/>。</param>
/// <param name="Data">帧体；缺 <c>data</c> 时是空对象（不是 null，省掉调用方判空）。</param>
public sealed record DouyinFrame(string Type, JsonObject Data);

/// <summary>握手帧（<c>hello</c>）。</summary>
/// <param name="Version">sidecar 声明的协议版本。</param>
/// <param name="Platform">sidecar 声明的平台标识（应为 <c>douyin</c>）。</param>
/// <param name="SelfId">当前登录的抖音账号 uid；未登录时为 <c>null</c>。</param>
/// <param name="Capabilities">sidecar 支持的能力（<c>text</c> / <c>image</c> / <c>mention</c> / <c>reply</c>）。</param>
public sealed record DouyinHello(
    string? Version,
    string? Platform,
    string? SelfId,
    IReadOnlyList<string> Capabilities);

/// <summary>出站结果帧（<c>ack</c>）。</summary>
/// <param name="RequestId">配对键；缺了就没法配对（会被丢弃）。</param>
/// <param name="Ok">是否成功。</param>
/// <param name="Error">失败原因（sidecar 给的原文，如 <c>conversation-risk</c> 拒绝）。</param>
public sealed record DouyinAck(string? RequestId, bool Ok, string? Error);

/// <summary>查询结果帧（<c>result</c>）。</summary>
/// <param name="RequestId">配对键。</param>
/// <param name="Ok">是否成功。</param>
/// <param name="Error">失败原因。</param>
/// <param name="Contacts">好友候选（<c>query.contacts</c> 的结果）。</param>
/// <param name="Groups">群候选（<c>query.groups</c> 的结果）。</param>
public sealed record DouyinQueryResult(
    string? RequestId,
    bool Ok,
    string? Error,
    IReadOnlyList<ContactInfo> Contacts,
    IReadOnlyList<GroupInfo> Groups);

/// <summary>
/// 抖音帧的解析与序列化（**纯函数、无副作用**）。
/// </summary>
/// <remarks>
/// <para>
/// 和 <c>OneBotEventMapper</c> 同一定位：只做协议翻译。区别是这里多一个方向（出站序列化），
/// 因为抖音的出站帧要拼 <c>segments</c> 并支持超长文本分段。
/// </para>
/// <para>
/// ⚠️ **不抛异常**：畸形帧 / 缺字段 / 未知类型一律返回 <c>null</c>，
/// 由 <see cref="DouyinChannel"/> 记 Warning —— 一条坏帧不能打断整条接收流（设计 §9 必测项）。
/// </para>
/// <para>
/// 入站形状**两种都收**：设计 §4.1 的 douyin-im 原生形状（<c>type</c> / <c>threadId</c> /
/// <c>content.kind</c> / <c>mentions</c> / <c>referenceInfo</c>）与 §3.3 示例的
/// 已翻译形状（<c>session</c> / <c>chatType</c> / <c>segments</c>）。
/// 取舍理由：§3.3 是契约样例、§4.1 是映射表，两边都能自圆其说；
/// 收两种形状的成本远低于上线时发现 sidecar 选了另一种。
/// </para>
/// </remarks>
public static class DouyinProtocol
{
    /// <summary>协议版本（与 <c>hello</c> 帧里 sidecar 声明的版本比对用）。</summary>
    public const string Version = "1";

    /// <summary>入站消息缺发送者 uid 时的占位（与 NapCat 的 <c>unknown</c> 口径一致）。</summary>
    private const string UnknownUser = "unknown";

    // ─────────────────────────── 解析：帧 ───────────────────────────

    /// <summary>把一帧文本解析成 <see cref="DouyinFrame"/>；失败返回 <c>false</c> + 中文原因。</summary>
    /// <param name="json">原始帧文本。</param>
    /// <param name="frame">解析结果（失败为 <c>null</c>）。</param>
    /// <param name="error">失败原因（成功为 <c>null</c>）。</param>
    public static bool TryParseFrame(string? json, out DouyinFrame? frame, out string? error)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            frame = null;
            error = "空帧";
            return false;
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            frame = null;
            error = $"不是合法 JSON：{ex.Message}";
            return false;
        }

        if (node is not JsonObject root)
        {
            frame = null;
            error = "帧不是 JSON 对象";
            return false;
        }

        var type = GetNonEmpty(root, "type");
        if (type is null)
        {
            frame = null;
            error = "帧缺少 type";
            return false;
        }

        frame = new DouyinFrame(type, root["data"] as JsonObject ?? new JsonObject());
        error = null;
        return true;
    }

    // ─────────────────────────── 解析：各类帧体 ───────────────────────────

    /// <summary>解析 <c>hello</c> 帧体。</summary>
    /// <param name="data">帧体。</param>
    /// <returns>握手信息；<paramref name="data"/> 为 null 时返回 <c>null</c>。</returns>
    public static DouyinHello? ParseHello(JsonObject? data)
    {
        if (data is null)
        {
            return null;
        }

        var capabilities = new List<string>();
        if (data["capabilities"] is JsonArray array)
        {
            foreach (var node in array)
            {
                if (node is JsonValue value && value.TryGetValue<string>(out var capability))
                {
                    capabilities.Add(capability);
                }
            }
        }

        return new DouyinHello(
            Version: GetNonEmpty(data, "version"),
            Platform: GetNonEmpty(data, "platform"),
            SelfId: GetNonEmpty(data, "selfId"),
            Capabilities: capabilities);
    }

    /// <summary>
    /// 解析 <c>inbound</c> 帧体 → 平台无关的 <see cref="IncomingMessage"/>。
    /// </summary>
    /// <param name="data">帧体。</param>
    /// <param name="platform">平台标识（如 <c>douyin</c>）。</param>
    /// <returns>
    /// 映射结果；**陌生人盒消息**（<c>stranger</c>）、缺会话 ID、缺会话类型等无法定位的情况返回 <c>null</c>。
    /// </returns>
    public static IncomingMessage? ParseInbound(JsonObject? data, string platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(platform);

        if (data is null)
        {
            return null;
        }

        var kind = StripMessagePrefix(GetString(data, "type"));

        // 陌生人盒第一版不响应（设计 §4.1）：不是映射不出来，是**明确不处理**。
        if (kind == "stranger")
        {
            return null;
        }

        var chatType = kind switch
        {
            "private" => ChatType.Private,
            "group" => ChatType.Group,
            _ => ParseChatType(GetString(data, "chatType")),
        };

        if (chatType is null)
        {
            return null;
        }

        // 会话 ID：原生形状用 threadId；已翻译形状只有 session（"douyin:g123"），从它反推。
        var chatId = GetNonEmpty(data, "threadId") ?? DeriveIdFromSession(GetString(data, "session"), platform);
        if (string.IsNullOrWhiteSpace(chatId))
        {
            return null;
        }

        var segments = new List<MessageSegment>();
        var replyId = GetNonEmpty(data, "replyTo") ?? ExtractReferenceId(data["referenceInfo"]);

        if (data["segments"] is JsonArray translated)
        {
            // §3.3 已翻译形状：segments 直接就是消息段，reply 段也在这里。
            ParseTranslatedSegments(translated, segments, ref replyId);
        }
        else
        {
            ParseNativeContent(data, segments);
            ParseMentions(data["mentions"], segments);
        }

        if (!string.IsNullOrEmpty(replyId))
        {
            // 引用放最前：与 NapCat 的 message 段顺序一致，渲染时「回复某人」在正文之前。
            segments.Insert(0, new ReplySegment(replyId));
        }

        return new IncomingMessage
        {
            Chat = new ChatRef
            {
                Platform = platform,
                Type = chatType.Value,
                Id = chatId,
            },
            Sender = ParseSender(data, chatType.Value),
            Segments = segments,
            OccurredAt = ParseTime(data),
            // RFC 口径：平台消息 ID 只走 EventId，不另设字段（IncomingMessage 的约定）。
            EventId = GetNonEmpty(data, "serverMessageId") ?? GetNonEmpty(data, "messageId"),
            ReplyToMessageId = string.IsNullOrEmpty(replyId) ? null : replyId,
        };
    }

    /// <summary>解析 <c>ack</c> 帧体；缺 <c>requestId</c> 无法配对，返回 <c>null</c>。</summary>
    /// <param name="data">帧体。</param>
    public static DouyinAck? ParseAck(JsonObject? data)
    {
        if (data is null)
        {
            return null;
        }

        var requestId = GetNonEmpty(data, "requestId");
        if (requestId is null)
        {
            return null;
        }

        // 缺 ok 视为成功：只在明确 false 时才算失败，别让老 sidecar 的不完整帧全变失败。
        var ok = GetBool(data, "ok") ?? true;

        return new DouyinAck(requestId, ok, GetNonEmpty(data, "error"));
    }

    /// <summary>解析 <c>result</c> 帧体；缺 <c>requestId</c> 无法配对，返回 <c>null</c>。</summary>
    /// <param name="data">帧体。</param>
    public static DouyinQueryResult? ParseResult(JsonObject? data)
    {
        if (data is null)
        {
            return null;
        }

        var requestId = GetNonEmpty(data, "requestId");
        if (requestId is null)
        {
            return null;
        }

        return new DouyinQueryResult(
            RequestId: requestId,
            Ok: GetBool(data, "ok") ?? true,
            Error: GetNonEmpty(data, "error"),
            Contacts: ParseContacts(data),
            Groups: ParseGroups(data));
    }

    // ─────────────────────────── 序列化：出站 / 查询 ───────────────────────────

    /// <summary>拼一帧 <c>outbound</c>。</summary>
    /// <param name="session">目标会话（<c>ChatRef.SessionId</c>，形如 <c>douyin:g123</c>）。</param>
    /// <param name="segments">消息段（已按 <see cref="SplitText"/> 分过段）。</param>
    /// <param name="replyTo">引用的消息 ID（只在首帧带，后续分段不带）。</param>
    /// <param name="requestId">配对键（每个分段一个）。</param>
    public static string BuildOutbound(
        string session,
        IReadOnlyList<MessageSegment> segments,
        string? replyTo,
        string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(session);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var array = new JsonArray();
        foreach (var segment in segments)
        {
            switch (segment)
            {
                case TextSegment text when !string.IsNullOrEmpty(text.Text):
                    array.Add(Segment("text", "text", text.Text));
                    break;

                case MentionSegment mention when IsNumericUserId(mention.UserId):
                    array.Add(new JsonObject
                    {
                        ["type"] = "mention",
                        ["userId"] = mention.UserId,
                    });
                    break;

                case MentionSegment mention:
                    // 群 @ 要求数字 uid（设计 §10 风险 7）：非法就降级成纯文本，
                    // 别把整条消息赔进去。
                    array.Add(Segment("text", "text", "@" + mention.UserId));
                    break;

                case ImageSegment image when !string.IsNullOrEmpty(image.Url):
                    array.Add(Segment("image", "url", image.Url));
                    break;

                case ImageSegment:
                    // 只有 FileId 没有 URL：sidecar 只能下载 URL，这里明确降级而不是静默丢。
                    array.Add(Segment("text", "text", "[图片]"));
                    break;

                case ReplySegment:
                    // 引用由帧级 replyTo 承载，不再单独进 segments（避免 sidecar 里重复表达）。
                    break;

                default:
                    array.Add(Segment("text", "text", $"[{GetSegmentLabel(segment)}]"));
                    break;
            }
        }

        var data = new JsonObject
        {
            ["session"] = session,
            ["segments"] = array,
            ["requestId"] = requestId,
        };

        if (!string.IsNullOrEmpty(replyTo))
        {
            data["replyTo"] = replyTo;
        }

        return Frame(DouyinFrameTypes.Outbound, data);
    }

    /// <summary>拼一帧 <c>query.contacts</c> / <c>query.groups</c>。</summary>
    /// <param name="type">帧类型（<see cref="DouyinFrameTypes.QueryContacts"/> 或 <see cref="DouyinFrameTypes.QueryGroups"/>）。</param>
    /// <param name="requestId">配对键。</param>
    public static string BuildQuery(string type, string requestId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        return Frame(type, new JsonObject { ["requestId"] = requestId });
    }

    /// <summary>拼一帧 <c>ping</c>（MVP 用 WS 层心跳，保留给需要应用层心跳时）。</summary>
    public static string BuildPing() => Frame(DouyinFrameTypes.Ping, new JsonObject());

    /// <summary>
    /// 把超长文本按 <paramref name="maxChars"/> 切成若干段，**不切断代理对**。
    /// </summary>
    /// <param name="text">原文。</param>
    /// <param name="maxChars">每段最大字符数（douyin-im 单条 ≤ 16000）。</param>
    /// <remarks>
    /// 切在代理对中间会留下半个字符：JSON 里是非法 <c>\ud83d</c>，抖音侧要么报错要么发出去是乱码。
    /// </remarks>
    public static IReadOnlyList<string> SplitText(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        if (maxChars <= 0 || text.Length <= maxChars)
        {
            return [text];
        }

        var chunks = new List<string>();
        var start = 0;

        while (start < text.Length)
        {
            var length = Math.Min(maxChars, text.Length - start);

            // 截断点落在高代理上 → 退一格；下一段从低代理开始，配对完整。
            if (start + length < text.Length
                && char.IsHighSurrogate(text[start + length - 1])
                && char.IsLowSurrogate(text[start + length]))
            {
                length--;
            }

            // maxChars == 1 且首字符就是高代理：躲不开，硬切（否则死循环）。
            if (length <= 0)
            {
                length = 1;
            }

            chunks.Add(text.Substring(start, length));
            start += length;
        }

        return chunks;
    }

    // ─────────────────────────── 内部：入站映射 ───────────────────────────

    /// <summary>原生形状：<c>content.kind</c> + 顶层 <c>text</c>。</summary>
    private static void ParseNativeContent(JsonObject data, List<MessageSegment> segments)
    {
        if (data["content"] is not JsonObject content)
        {
            // 没有 content 但有裸 text：当纯文本处理（兼容手写帧）
            var bare = GetNonEmpty(data, "text");
            if (bare is not null)
            {
                segments.Add(new TextSegment(bare));
            }

            return;
        }

        var kind = GetString(content, "kind") ?? GetString(data, "contentKind");

        switch (kind)
        {
            case "text":
                var text = GetNonEmpty(content, "text") ?? GetNonEmpty(data, "text");
                if (text is not null)
                {
                    segments.Add(new TextSegment(text));
                }

                break;

            case "image":
                var url = GetNonEmpty(content, "url")
                    ?? GetNonEmpty(content, "uri")
                    ?? GetNonEmpty(content, "imageUrl");
                segments.Add(new ImageSegment(Url: url, FileId: null));
                break;

            // 第一版不解析这几种（设计 §1.2 / §4.1）：降级**可读占位**，别让 AI 以为什么都没发。
            case "video":
                segments.Add(new TextSegment("[视频]"));
                break;

            case "emoji":
            case "sticker":
                segments.Add(new TextSegment("[表情]"));
                break;

            case "file":
                segments.Add(new TextSegment("[文件]"));
                break;

            default:
                segments.Add(new TextSegment($"[{kind ?? "未知内容"}]"));
                break;
        }
    }

    /// <summary>原生形状的 <c>mentions[]</c>：元素可能是对象或裸字符串。</summary>
    private static void ParseMentions(JsonNode? node, List<MessageSegment> segments)
    {
        if (node is not JsonArray array)
        {
            return;
        }

        foreach (var item in array)
        {
            var userId = item switch
            {
                JsonObject obj => GetNonEmpty(obj, "userId") ?? GetNonEmpty(obj, "uid"),
                JsonValue value when value.TryGetValue<string>(out var raw) => raw,
                _ => null,
            };

            if (string.IsNullOrWhiteSpace(userId))
            {
                continue;
            }

            if (IsNumericUserId(userId))
            {
                segments.Add(new MentionSegment(userId));
            }
            else
            {
                // uid 非数字：群 @ 在 douyin-im 里要求数字 uid（设计 §10 风险 7），入站降级纯文本。
                var nickname = (item as JsonObject) is { } obj
                    ? GetNonEmpty(obj, "nickname") ?? GetNonEmpty(obj, "displayName")
                    : null;
                segments.Add(new TextSegment($"@{nickname ?? userId}"));
            }
        }
    }

    /// <summary>已翻译形状的 <c>segments[]</c>；顺带把 <c>reply</c> 段并进引用 ID。</summary>
    private static void ParseTranslatedSegments(
        JsonArray array,
        List<MessageSegment> segments,
        ref string? replyId)
    {
        foreach (var item in array)
        {
            if (item is not JsonObject segment)
            {
                continue;
            }

            switch (GetString(segment, "type"))
            {
                case "text":
                    var text = GetString(segment, "text");
                    if (!string.IsNullOrEmpty(text))
                    {
                        segments.Add(new TextSegment(text));
                    }

                    break;

                case "mention":
                case "at":
                    var userId = GetNonEmpty(segment, "userId") ?? GetNonEmpty(segment, "uid");
                    if (string.IsNullOrWhiteSpace(userId))
                    {
                        break;
                    }

                    segments.Add(IsNumericUserId(userId)
                        ? new MentionSegment(userId)
                        : new TextSegment($"@{GetNonEmpty(segment, "nickname") ?? userId}"));
                    break;

                case "image":
                    segments.Add(new ImageSegment(
                        Url: GetNonEmpty(segment, "url"),
                        FileId: GetNonEmpty(segment, "fileId") ?? GetNonEmpty(segment, "file")));
                    break;

                case "reply":
                    // 引用已由帧级 replyTo 表达；这里只补齐缺失的 ID。
                    replyId ??= GetNonEmpty(segment, "messageId") ?? GetNonEmpty(segment, "id");
                    break;

                default:
                    segments.Add(new TextSegment($"[{GetString(segment, "type") ?? "未知"}]"));
                    break;
            }
        }
    }

    /// <summary>发送者：原生形状给 <c>sender</c> 对象或扁平的 <c>senderUid</c> 等字段。</summary>
    private static SenderInfo ParseSender(JsonObject data, ChatType chatType)
    {
        var sender = data["sender"] as JsonObject;
        var nickname = GetNonEmpty(sender, "nickname")
            ?? GetNonEmpty(data, "senderNickname")
            ?? GetNonEmpty(data, "senderName");
        var displayName = GetNonEmpty(sender, "displayName") ?? GetNonEmpty(data, "senderDisplayName");

        return new SenderInfo
        {
            UserId = GetNonEmpty(sender, "userId")
                ?? GetNonEmpty(sender, "uid")
                ?? GetNonEmpty(data, "senderUid")
                ?? UnknownUser,
            Nickname = nickname,
            // 群昵称：抖音事件里对应 displayName（与 QQ 的 card 不同名）。
            // ⚠️ 这条映射未真机验证：若 displayName 其实就是昵称，会显示成「名字（名字）」。
            GroupCard = chatType == ChatType.Group
                && displayName is not null
                && !string.Equals(displayName, nickname, StringComparison.Ordinal)
                    ? displayName
                    : null,
            // 私聊能进来 → 视为好友；群聊是不是好友事件里没有（与 NapCat 同口径）。
            IsFriend = chatType == ChatType.Private,
        };
    }

    /// <summary><c>referenceInfo</c> 可能是对象（<c>serverMessageId</c>）也可能是裸字符串。</summary>
    private static string? ExtractReferenceId(JsonNode? node) => node switch
    {
        JsonObject obj => GetNonEmpty(obj, "serverMessageId") ?? GetNonEmpty(obj, "messageId"),
        JsonValue value when value.TryGetValue<string>(out var raw) => raw,
        _ => null,
    };

    /// <summary>
    /// 从 <c>session</c>（<c>{platform}:{g|p}{id}</c>）反推会话 ID。
    /// </summary>
    /// <remarks>只在没给 <c>threadId</c> 时用：已翻译形状只带 session。</remarks>
    private static string? DeriveIdFromSession(string? session, string platform)
    {
        if (string.IsNullOrWhiteSpace(session))
        {
            return null;
        }

        var prefix = platform + ":";
        var rest = session.StartsWith(prefix, StringComparison.Ordinal)
            ? session[prefix.Length..]
            : session;

        if (rest.Length > 0 && (rest[0] == 'g' || rest[0] == 'p'))
        {
            rest = rest[1..];
        }

        return string.IsNullOrWhiteSpace(rest) ? null : rest;
    }

    // ─────────────────────────── 内部：查询结果 ───────────────────────────

    private static IReadOnlyList<ContactInfo> ParseContacts(JsonObject data)
    {
        if (FindResultArray(data, "contacts") is not { } array)
        {
            return [];
        }

        var contacts = new List<ContactInfo>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            var userId = GetNonEmpty(obj, "userId") ?? GetNonEmpty(obj, "uid");
            if (userId is null)
            {
                continue;
            }

            contacts.Add(new ContactInfo(
                UserId: userId,
                Nickname: GetNonEmpty(obj, "nickname") ?? userId,
                Remark: GetNonEmpty(obj, "remark")));
        }

        return contacts;
    }

    private static IReadOnlyList<GroupInfo> ParseGroups(JsonObject data)
    {
        if (FindResultArray(data, "groups") is not { } array)
        {
            return [];
        }

        var groups = new List<GroupInfo>(array.Count);
        foreach (var item in array)
        {
            if (item is not JsonObject obj)
            {
                continue;
            }

            var groupId = GetNonEmpty(obj, "groupId") ?? GetNonEmpty(obj, "id");
            if (groupId is null)
            {
                continue;
            }

            groups.Add(new GroupInfo(
                GroupId: groupId,
                Name: GetNonEmpty(obj, "name") ?? groupId,
                MemberCount: (int)(GetLong(obj, "memberCount") ?? 0),
                MaxMemberCount: (int)(GetLong(obj, "maxMemberCount") ?? 0)));
        }

        return groups;
    }

    /// <summary>
    /// 结果数组：优先取 <c>contacts</c> / <c>groups</c> 键，取不到再看裸 <c>data</c> 数组。
    /// </summary>
    /// <remarks>
    /// 裸 <c>data</c> 兜底是给「sidecar 直接回数组」的写法留的路；
    /// 两个解析器各自按字段过滤，所以同一个数组喂进来不会互相污染。
    /// </remarks>
    private static JsonArray? FindResultArray(JsonObject data, string key) =>
        data[key] as JsonArray ?? data["data"] as JsonArray;

    // ─────────────────────────── 内部：小工具 ───────────────────────────

    /// <summary>拼外层帧：<c>{type, time, data}</c>。</summary>
    private static string Frame(string type, JsonObject data) =>
        JsonSerializer.Serialize(new JsonObject
        {
            ["type"] = type,
            ["time"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["data"] = data,
        });

    private static JsonObject Segment(string type, string key, string value) => new()
    {
        ["type"] = type,
        [key] = value,
    };

    private static string? StripMessagePrefix(string? type) =>
        type is null ? null
        : type.StartsWith("message.", StringComparison.Ordinal) ? type["message.".Length..]
        : type;

    private static ChatType? ParseChatType(string? chatType) => chatType switch
    {
        "private" => ChatType.Private,
        "group" => ChatType.Group,
        _ => null,
    };

    private static string GetSegmentLabel(MessageSegment segment) => segment switch
    {
        FaceSegment => "表情",
        MfaceSegment => "表情包",
        RecordSegment => "语音",
        ReplySegment => "引用",
        _ => "不支持的消息",
    };

    /// <summary>群 @ 要求数字 uid（设计 §10 风险 7）。</summary>
    private static bool IsNumericUserId(string userId) =>
        userId.Length > 0 && userId.All(char.IsAsciiDigit);

    /// <summary>
    /// 平台时间戳 → <see cref="DateTimeOffset"/>。
    /// </summary>
    /// <remarks>
    /// douyin-im 用毫秒；顺手容忍秒（手写帧 / 老版本）。**只在完全缺失时**退回 <c>UtcNow</c>，
    /// 绝不拿本地时钟顶替已有时间戳（FR-15.1）。
    /// </remarks>
    private static DateTimeOffset ParseTime(JsonObject data)
    {
        var raw = GetLong(data, "time") ?? 0;
        if (raw <= 0)
        {
            return DateTimeOffset.UtcNow;
        }

        return raw >= 100_000_000_000
            ? DateTimeOffset.FromUnixTimeMilliseconds(raw)
            : DateTimeOffset.FromUnixTimeSeconds(raw);
    }

    private static string? GetString(JsonObject? obj, string key)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return null;
        }

        return node switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => text,
            _ => node.ToJsonString(),
        };
    }

    private static string? GetNonEmpty(JsonObject? obj, string key)
    {
        var value = GetString(obj, key);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static long? GetLong(JsonObject? obj, string key)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var asLong))
        {
            return asLong;
        }

        if (value.TryGetValue<double>(out var asDouble))
        {
            return (long)asDouble;
        }

        if (value.TryGetValue<string>(out var asText) && long.TryParse(asText, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static bool? GetBool(JsonObject? obj, string key)
    {
        if (obj is null || !obj.TryGetPropertyValue(key, out var node) || node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<bool>(out var asBool))
        {
            return asBool;
        }

        return value.TryGetValue<string>(out var asText) && bool.TryParse(asText, out var parsed)
            ? parsed
            : null;
    }
}
