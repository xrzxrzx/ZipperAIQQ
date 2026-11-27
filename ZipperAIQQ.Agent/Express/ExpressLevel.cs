namespace ZipperAIQQ.Agent.Express;

/// <summary>
/// 表达档位（问题记录 06 §6 / 表达档位设计 §2）。
/// </summary>
/// <remarks>
/// <para>
/// 档位管的是「放开到什么程度」，**不管「能不能说实话」** ——
/// 事实准确性、工具权限、隐私边界三件事与档位无关（规则段第 6 条写死）。
/// </para>
/// <para>
/// 枚举值必须**有序**：群聊上限靠 <c>min</c> 收紧，只有能比较大小才可能「只收紧不放宽」。
/// </para>
/// </remarks>
public enum ExpressLevel
{
    /// <summary>日常：与现状一致，不主动往亲密方向带。</summary>
    Daily = 0,

    /// <summary>亲近：撒娇 / 吃醋 / 直白，允许暧昧，但不写性行为细节。</summary>
    Close = 1,

    /// <summary>露骨：用户 2026-11-20 选定的天花板。</summary>
    Explicit = 2,
}
