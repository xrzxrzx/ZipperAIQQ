using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Agent.Tools;

/// <summary>
/// 会话引用解析（<c>qq:p&lt;QQ号&gt;</c> / <c>qq:g&lt;群号&gt;</c>）。
/// </summary>
/// <remarks>
/// 与 <c>listSessions</c> / <c>getMessages</c> 的 sessionId 完全一致，
/// 定时任务、跨会话发送都用这一套，避免出现"另一套 ID 格式"。
/// </remarks>
public static class SessionRefParser
{
    public static bool TryParse(
        string? raw,
        string defaultPlatform,
        out ChatRef chat,
        out string? error)
    {
        chat = null!;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "会话 ID 不能为空。格式：`qq:p<QQ号>` 或 `qq:g<群号>`。";
            return false;
        }

        var text = raw.Trim();
        var colon = text.IndexOf(':');
        if (colon <= 0 || colon == text.Length - 1)
        {
            error = $"看不懂会话 ID「{raw}」。格式：`qq:p<QQ号>` 或 `qq:g<群号>`。";
            return false;
        }

        var platform = text[..colon].Trim();
        var rest = text[(colon + 1)..].Trim();

        if (string.Equals(platform, "qq", StringComparison.OrdinalIgnoreCase))
        {
            platform = "qq";
        }
        else if (!string.Equals(platform, defaultPlatform, StringComparison.OrdinalIgnoreCase))
        {
            error = $"平台「{platform}」与当前通道（{defaultPlatform}）不一致。";
            return false;
        }

        if (rest.Length < 2 || char.ToLowerInvariant(rest[0]) is not ('p' or 'g'))
        {
            error = $"会话 ID「{raw}」缺少类型前缀（p=私聊，g=群）。";
            return false;
        }

        var kind = char.ToLowerInvariant(rest[0]);
        var id = rest[1..];
        if (id.Length == 0 || !id.All(char.IsAsciiDigit))
        {
            error = $"会话 ID「{raw}」里的号码不是纯数字。";
            return false;
        }

        chat = new ChatRef
        {
            Platform = platform,
            Type = kind == 'p' ? ChatType.Private : ChatType.Group,
            Id = id,
        };

        return true;
    }
}
