using System.Text;
using ZipperAIQQ.Channel.Models;
using ZipperAIQQ.Server.Notices;
using ZipperAIQQ.Storage.Messages;

namespace ZipperAIQQ.Server.Feedback;

/// <summary>把通知整理成发给主人的一段话（含撤回原文）。</summary>
public static class FeedbackTextBuilder
{
    public static string Build(IncomingNotice notice, StoredMessage? original, string? selfId)
    {
        ArgumentNullException.ThrowIfNull(notice);

        if (notice.Type == NoticeType.MessageRecalled)
        {
            var who = notice.Actor is null
                ? "某人"
                : $"{notice.Actor.DisplayName}（{notice.Actor.UserId}）";
            var content = original is null ? "（原文不可得）" : Render(original.Message);
            return $"[撤回] {who} 撤回了：{content}";
        }

        return $"[反馈] {NoticeResponder.Describe(notice, selfId)}";
    }

    /// <summary>取消息的可读文本；纯图片/表情给出占位，避免只发一个空串。</summary>
    public static string Render(IncomingMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (!string.IsNullOrWhiteSpace(message.PlainText))
        {
            return message.PlainText.Trim();
        }

        var sb = new StringBuilder();
        foreach (var segment in message.Segments)
        {
            sb.Append(segment switch
            {
                TextSegment text => text.Text,
                ImageSegment => "[图片]",
                MfaceSegment => "[表情包]",
                FaceSegment => "[表情]",
                RecordSegment record => string.IsNullOrWhiteSpace(record.Text) ? "[语音]" : $"[语音:{record.Text}]",
                MentionSegment mention => $"@{mention.UserId}",
                _ => string.Empty,
            });
        }

        var rendered = sb.ToString().Trim();
        return rendered.Length == 0 ? "（空消息）" : rendered;
    }
}
