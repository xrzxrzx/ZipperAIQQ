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

    /// <summary>
    /// 跨会话读别人的历史（<c>search_other_sessions</c>）。
    /// </summary>
    /// <remarks>
    /// 这是「读别人的私聊」—— 比读文件更该收紧：文件按角色分目录，
    /// 而跨会话读是**穿透会话隔离**看所有聊天的内容。硬底线管理员，默认也只给管理员。
    /// </remarks>
    CrossSessionHistory,

    /// <summary>
    /// 跨平台查通讯录（好友 / 群，<c>query_contacts</c>）。
    /// </summary>
    /// <remarks>
    /// 好友列表能反推一个人的社交关系，且会一次铺开所有平台的数据 —— 默认只给管理员。
    /// </remarks>
    QueryContacts,

    /// <summary>改自己的资料（个性签名等）。</summary>
    /// <remarks>写的是**自己的账号**，对外可见，但影响面限于"我这个人看起来什么样"。</remarks>
    WriteProfile,

    /// <summary>戳一戳别人。</summary>
    /// <remarks>
    /// 影响**别人**：会在他那边弹提示。滥用就是骚扰，所以比改签名更严（同样硬底线管理员）。
    /// </remarks>
    SendPoke,

    /// <summary>
    /// 主动给**别的会话**发消息（不只是当前会话）。
    /// </summary>
    /// <remarks>
    /// 这是**代账号对外发话**里最重的一个：可以在没人要求的情况下主动发。
    /// 硬底线管理员，且实现里还有两道附加限制（必须有历史、每次留审计）。
    /// </remarks>
    SendToOtherSession,

    /// <summary>
    /// 读图里文字（OCR，<c>ocr_image</c>）。
    /// </summary>
    /// <remarks>
    /// 默认给普通成员：它只**读**用户自己发来的图，不产生对外动作。
    /// 代价是平台按页计费（1.5 元/千页），所以要能在工具页单独关掉。
    /// </remarks>
    MediaRead,

    /// <summary>
    /// 文本转语音（<c>tts_speak</c>）。
    /// </summary>
    /// <remarks>
    /// 只落文件、不自动发送，但仍按**字符计费**（3 元/万字符）并会写磁盘；
    /// 默认给普通成员（需求 G6「默认 Normal」），需要收紧时在工具页调。
    /// </remarks>
    MediaSpeak,

    /// <summary>
    /// 看邮件画面（<c>mail_render</c>）：把 HTML 邮件渲染成截图递给模型。
    /// </summary>
    /// <remarks>
    /// 邮件是**私信级别的隐私**：群里任何一个白名单成员让它渲染，模型的回答就会把
    /// 邮件版面讲给群里听。所以默认只给管理员（不是 Normal），硬底线也是管理员。
    /// 它只读、没有对外动作，但读的是别人的私人内容 —— 这一条比"能不能发"更该收紧。
    /// </remarks>
    MailRead,

    /// <summary>
    /// 读邮件原文（<c>mail_raw</c>）：完整头 + 正文源文本。
    /// </summary>
    /// <remarks>
    /// 比画面更敏感：Received 链、验证码、账单号、退订链接都在这里，且**逐字可信**。
    /// 默认只给主人，硬底线管理员；每次调用留审计（<c>mail_raw</c> 的工具实现里写）。
    /// </remarks>
    MailRaw,

    /// <summary>
    /// 看抖音视频 / 听它的人声（<c>douyin_video</c> / <c>douyin_transcript</c>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 与 OCR 不同：它要**下载整条视频**（几十 MB 流量）、在浏览器里解码抽帧，
    /// 转写还要再调一次 ASR（按时长计费）。群里任何一条视频都能触发这一串动作，
    /// 所以默认只给管理员（与 <c>MailRead</c> 同档），硬底线也是管理员 ——
    /// 要放开就在工具页单独调，别让"默认值"替人决定烧钱。
    /// </para>
    /// <para>
    /// 它不产生对外动作（不发、不评论），所以比写类能力弱一档；收紧的理由是**成本**与**内容面**，
    /// 不是危险动作。
    /// </para>
    /// </remarks>
    MediaVideo,
}
