using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Storage.Messages;

/// <summary>
/// 一条已入库的消息：数据库主键 + 原始消息 + 接收时间。
/// </summary>
/// <remarks>
/// <see cref="Id"/> 是数据库自增主键，用于「命中后往前/往后扩窗」（FR-4.2）。
/// <see cref="ReceivedAt"/> 与 <see cref="Message"/>.SentAt 不同：
/// 前者是本机收到的时间，后者是平台给出的发送时间，补发消息时两者可能差很远。
/// </remarks>
public sealed record StoredMessage(
    long Id,
    IncomingMessage Message,
    DateTimeOffset ReceivedAt);
