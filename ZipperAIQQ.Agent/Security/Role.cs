namespace ZipperAIQQ.Agent.Security;

/// <summary>
/// 权限主体（**消息发送者**，不是会话 —— FR-6.1）的角色。
/// </summary>
/// <remarks>
/// 数值大小有意义：比较用 <c>&gt;=</c>，所以顺序必须从低到高。
/// </remarks>
public enum Role
{
    /// <summary>普通成员：默认拒绝一切文件能力。</summary>
    Normal = 0,

    /// <summary>白名单：可读写**自己当前会话**的工作区。</summary>
    Whitelist = 1,

    /// <summary>管理员：全部会话目录、全部文件能力。</summary>
    Admin = 2,

    /// <summary>
    /// 主人（第 17 项）：在客户端指定的**唯一**一个人，权限高于管理员。
    /// </summary>
    /// <remarks>
    /// 为什么需要它：管理员可能有好几个（协作者），但「改主人 / 改全局配置」这种事
    /// 应该只归一个人 —— 否则谁都能把别人踢下台。
    /// </remarks>
    Owner = 3,
}

/// <summary>
/// 工具声明的**能力等级**（FR-6.3：门禁校验能力，不只校验工具名）。
/// </summary>
/// <remarks>
/// 同一个工具名将来若支持不同参数走不同能力，也必须分别声明 ——
/// 只按名字放行，等于把「删」伪装成「读」就能过。
/// </remarks>
public enum ToolCapability
{
    /// <summary>读文件 / 列目录。</summary>
    ReadFile,

    /// <summary>写文件 / 定向编辑 / 移动。</summary>
    WriteFile,

    /// <summary>删除 —— **没有 undo**，最危险。</summary>
    DeleteFile,

    /// <summary>
    /// 创建 / 取消定时任务（FR-14.6）。
    /// </summary>
    /// <remarks>
    /// 普通成员若能建任务，等于给了他「定时刷屏」的能力 —— 群里到点就被 @ 一次。
    /// </remarks>
    Schedule,
}
