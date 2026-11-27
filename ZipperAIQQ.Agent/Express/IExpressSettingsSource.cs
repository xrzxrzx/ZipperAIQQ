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
}
