namespace ZipperAIQQ.Agent.Express;

/// <summary>
/// 取当前表达档位快照（Agent 侧的窄接口，Server 用 <c>ExpressOptionsService</c> 实现）。
/// </summary>
/// <remarks>
/// <para>
/// 放在 Agent 层而不是让 Agent 直接读 <c>RuntimeSettings</c>：Agent 不该知道键名与落库形态，
/// 与 <c>IWebSearchProvider</c> / <c>IMailReader</c> 同一条老路（实现放 Server）。
/// </para>
/// <para>
/// 实现方**必须返回不可变快照**：同一轮里会被读 3 次，中途换掉会让规则段与预算错配。
/// </para>
/// </remarks>
public interface IExpressSettingsSource
{
    /// <summary>当前档位快照（同步读内存，不打库）。</summary>
    ExpressSettings Current { get; }

    /// <summary>
    /// 记一次卫生命中（用于 <c>getExpressStatus.hygiene.hits</c>）。
    /// </summary>
    /// <param name="category">命中的类别。</param>
    void RecordHygieneHit(ExpressHygieneCategory category);

    /// <summary>
    /// 记一次「已请模型把同一件事重说一遍」（用于 <c>getExpressStatus.hygiene.regenerated</c>）。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="RecordHygieneHit"/> 分开计：命中多、重生成少说明**规则段本身**没压住自我审查，
    /// 两个数放一起才能看出是哪种情况（只记命中会让人以为一直在烧重生成的钱）。
    /// </remarks>
    void RecordHygieneRegeneration();
}
