namespace ZipperAIQQ.Agent.Express;

/// <summary>
/// 免拒绝校准的命中类别（表达档位设计 §5.1）。
/// </summary>
/// <remarks>
/// 分类只用于**日志与可观测**（哪一类在漏），判定逻辑本身只看「有没有命中」——
/// 三类都要纠，纠法完全一样。
/// </remarks>
public enum ExpressHygieneCategory
{
    /// <summary>「作为一个 AI，我不能…」这类自陈机器身份。</summary>
    AiSelfReference = 0,

    /// <summary>「我只是个程序」这类自我贬低式挡牌。</summary>
    SelfDenial = 1,

    /// <summary>「不能讨论」「这类话题不太合适」「建议你咨询…」这类拒绝。</summary>
    Refusal = 2,

    /// <summary>「不过我得提醒你一下」这类垫话。</summary>
    Hedging = 3,
}
