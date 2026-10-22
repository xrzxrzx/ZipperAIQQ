using System.Text;

namespace ZipperAIQQ.Storage.Personas;

/// <summary>一份人设。</summary>
/// <param name="Name">人设名（如「傲娇助手」），用于展示与切换。</param>
/// <param name="Content">人设正文，直接作为 system 消息内容。</param>
public sealed record Persona(string Name, string Content)
{
    /// <summary>形象小节的标头（外观、身份、说话风格）。</summary>
    public const string ImageHeader = "【形象】";

    /// <summary>性格小节的标头（性格、习惯、情绪反应）。</summary>
    public const string PersonalityHeader = "【性格】";

    /// <summary>
    /// 由「形象 + 性格」拼出一份完整人设。
    /// </summary>
    /// <remarks>
    /// 为什么用固定小节标头而不是给数据库加两列：
    /// ① 存储层（会话表 + 全局文件）都要改，改动面大；
    /// ② 标头方式对老数据**向后兼容** —— 没有标头的旧人设仍然整体生效。
    /// </remarks>
    public static Persona FromParts(string name, string? image, string? personality)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(image))
        {
            sb.AppendLine(ImageHeader);
            sb.AppendLine(image.Trim());
        }

        if (!string.IsNullOrWhiteSpace(personality))
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            sb.AppendLine(PersonalityHeader);
            sb.AppendLine(personality.Trim());
        }

        return new Persona(name, sb.ToString().Trim());
    }

    /// <summary>形象小节内容（旧人设没有标头时返回 <c>null</c>）。</summary>
    public string? Image => ExtractSection(ImageHeader);

    /// <summary>性格小节内容（旧人设没有标头时返回 <c>null</c>）。</summary>
    public string? Personality => ExtractSection(PersonalityHeader);

    private string? ExtractSection(string header)
    {
        if (string.IsNullOrEmpty(Content))
        {
            return null;
        }

        var start = Content.IndexOf(header, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += header.Length;
        var end = Content.Length;

        foreach (var other in new[] { ImageHeader, PersonalityHeader })
        {
            if (other == header)
            {
                continue;
            }

            var next = Content.IndexOf(other, start, StringComparison.Ordinal);
            if (next >= 0 && next < end)
            {
                end = next;
            }
        }

        return Content[start..end].Trim();
    }
}

/// <summary>
/// 人设存储：**全局默认 + 每会话覆盖**。
/// </summary>
/// <remarks>
/// <para>
/// 需求 D12：每聊天的人设**完全覆盖**全局，不是叠加。
/// 叠加会让人设变成"两个角色的缝合怪"，比没有更糟。
/// </para>
/// <para>
/// 全局人设来自配置文件（部署时给），会话级覆盖来自数据库（运行时切）。
/// </para>
/// </remarks>
public interface IPersonaStore
{
    /// <summary>取该会话生效的人设：会话级优先，没有则回落全局。</summary>
    Task<Persona> GetAsync(string platform, string chatType, string chatId, CancellationToken ct = default);

    /// <summary>为某个会话设定人设（覆盖全局）。</summary>
    Task SetAsync(string sessionId, Persona persona, CancellationToken ct = default);

    /// <summary>清除会话级人设，回落到全局。</summary>
    Task ClearAsync(string sessionId, CancellationToken ct = default);
}
