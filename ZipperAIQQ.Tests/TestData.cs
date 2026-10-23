using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Tests;

/// <summary>测试用的固定数据（避免每个测试自己拼 ChatRef / SenderInfo）。</summary>
internal static class TestData
{
    public static ChatRef PrivateChat(string id = "10001") => new()
    {
        Platform = "qq",
        Type = ChatType.Private,
        Id = id,
    };

    public static ChatRef GroupChat(string id = "20001") => new()
    {
        Platform = "qq",
        Type = ChatType.Group,
        Id = id,
    };

    public static SenderInfo Sender(string id = "10001", string nickname = "测试用户") => new()
    {
        UserId = id,
        Nickname = nickname,
    };
}
