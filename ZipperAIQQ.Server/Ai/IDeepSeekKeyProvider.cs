namespace ZipperAIQQ.Server.Ai;

/// <summary>
/// DeepSeek 账号级 key 的唯一来源（问题记录 06 §5 / 方案 G7）。
/// </summary>
/// <remarks>
/// <para>
/// 对外只有**一把** key：对话 / 搜索 / 抓取 / 余额采样全部从这里取，
/// 改一处全链路生效（配合各消费端的 reload）。全家桶的 OCR / ASR / TTS（B2-b）也会从这里取。
/// </para>
/// <para>
/// 内部**保留多密钥写法**：槽位表的 <c>Slot = 0</c> 是生效的那把，其余元素原样留着不删。
/// 这样「以后想恢复多 key」不需要改数据结构，只需要在解析时换一条规则。
/// </para>
/// <para>
/// 实现放 Server、接口也在 Server 是本项目的既定形态（与 <c>IWebSearchProvider</c> 不同：
/// 那个接口的消费者在 Agent），因为 key 的消费者全在 Server 层（客户端建连、搜索、媒体接口）。
/// </para>
/// </remarks>
public interface IDeepSeekKeyProvider
{
    /// <summary>当前是否有一把可用的 key（**不触发解密**，读缓存）。</summary>
    bool HasKey { get; }

    /// <summary>
    /// 启动时调用：**先做 legacy 迁移，再加载缓存**（幂等；失败不阻断启动）。
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// 生效的 key：**按槽位升序第一个能解开的**那把（常态就是 <c>Slot 0</c>）；没有则 <c>null</c>。
    /// </summary>
    Task<string?> GetAsync(CancellationToken ct = default);

    /// <summary>
    /// 对外状态（是否配置 / 留档把数 / 掩码）。
    /// </summary>
    /// <remarks>
    /// **刻意不提供「取出全部明文」的接口**：唯一的消费者（设置页视图）只需要掩码与计数，
    /// 多说一句「全部明文」就多一次哪天被人顺手打进日志的机会。要轮换多把就分次 Set。
    /// </remarks>
    DeepSeekKeyStatus GetStatus();

    /// <summary>
    /// 写入生效 key（**只动槽位 0**，其余留档槽位原样保留）。
    /// </summary>
    /// <returns>是否真的发生了变更（同一把 key 重复保存不算）。</returns>
    Task<bool> SetKeyAsync(string plainKey, string actor, CancellationToken ct = default);

    /// <summary>重新读库刷新缓存（改完 key、或外部直接改库后调用）。</summary>
    Task ReloadAsync(CancellationToken ct = default);
}
