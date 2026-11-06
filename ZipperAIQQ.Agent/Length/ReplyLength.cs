using ZipperAIQQ.Channel.Models;

namespace ZipperAIQQ.Agent.Length;

/// <summary>回复长度场景（R-11 / J-1）。</summary>
public enum ReplyLengthScene
{
    /// <summary>群里插话 / 闲聊：能短则短。</summary>
    Short = 0,

    /// <summary>正常对话：给到要点即可。</summary>
    Normal = 1,

    /// <summary>对方明确要「详细说说」：允许长。</summary>
    Long = 2,
}

/// <summary>单轮预算（R-11 / J-1：字数 + 条数双上限）。</summary>
public sealed record ReplyLengthBudget(int MaxChars, int MaxMessages);

/// <summary>预算配置（可整体关掉回退到现状：只留软提示）。</summary>
public sealed record ReplyLengthOptions
{
    public bool Enabled { get; init; } = true;

    public ReplyLengthBudget Short { get; init; } = new(120, 2);

    public ReplyLengthBudget Normal { get; init; } = new(600, 5);

    public ReplyLengthBudget Long { get; init; } = new(1500, 8);
}

/// <summary>
/// 场景判据 + 预算（R-11 / J-1）。
/// </summary>
/// <remarks>
/// <para>
/// 判据**复用 JEV**（2026-11-06 拍板）：不新增模型调用，只看
/// <see cref="IReplyModeDecider.GetModeAsync"/> 的模式 + 是否私聊/被 @。
/// 「jev 模式下群里没被 @ 却允许插话」= 闲聊插嘴 → 短预算；私聊/被 @ → 正常预算。
/// </para>
/// <para>
/// 用户显式要详细（「详细说说」「展开讲讲」）→ 长预算，优先级最高（拍板：关键词解除）。
/// </para>
/// </remarks>
public static class ReplyLengthPolicy
{
    private static readonly string[] DetailKeywords =
        ["详细", "展开", "具体说", "细细说", "多说点", "认真说", "完整说"];

    public static bool WantsDetail(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        DetailKeywords.Any(k => text.Contains(k, StringComparison.Ordinal));

    public static ReplyLengthScene Decide(
        ReplyMode mode, ChatType chatType, bool mentioned, string? text)
    {
        if (WantsDetail(text))
        {
            return ReplyLengthScene.Long;
        }

        // 群里 JEV 放行的「插话」场景：对方没点名，别写小作文
        if (mode == ReplyMode.Jev && chatType == ChatType.Group && !mentioned)
        {
            return ReplyLengthScene.Short;
        }

        return ReplyLengthScene.Normal;
    }

    public static ReplyLengthBudget BudgetFor(ReplyLengthScene scene, ReplyLengthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return scene switch
        {
            ReplyLengthScene.Short => options.Short,
            ReplyLengthScene.Long => options.Long,
            _ => options.Normal,
        };
    }

    /// <summary>
    /// 兜底裁剪：压到最后一个完整句（。！？；\n）以内；找不到句末就硬切。
    /// </summary>
    /// <remarks>
    /// 只在「模型改写后仍然超长」或改写失败时用 —— 拍板要求优先让模型重写，
    /// 这里保证绝不会出现「说到一半断了」的观感。
    /// </remarks>
    public static string TrimToBudget(string text, int maxChars)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxChars);

        if (text.Length <= maxChars)
        {
            return text;
        }

        var window = text[..maxChars];
        var lastStop = window.LastIndexOfAny(['。', '！', '？', '；', '\n', '.', '!', '?', ';']);

        return lastStop > maxChars / 3
            ? window[..(lastStop + 1)].TrimEnd()
            : window.TrimEnd();
    }
}
