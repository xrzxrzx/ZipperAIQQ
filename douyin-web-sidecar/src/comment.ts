/**
 * 抖音**评论区**的纯逻辑（B5）：把页面快照解析成结构化评论、挑回复目标、做文案校验。
 *
 * 分工（与 `media.ts` 同一条纪律）：
 *   - **本文件**：只吃普通对象、只吐普通对象，不碰网络 / 文件 / 浏览器 ⇒ 能用 `node --test` 喂假快照
 *   - `site.ts`：选择器、坐标、点击、输入（页面动作全在那里）
 *   - `main.ts`：帧分发与编排
 *
 * 为什么解析**不放在页面里**：`page.evaluate` 运行在浏览器，没有模块作用域，
 * 引用外部常量会直接 `ReferenceError`（B4 真机踩过，编译与单测都看不见）。
 * 所以页面只量「原始文本 + 坐标 + 属性里的数字」，解析回到 Node 侧做。
 *
 * 评论区的 DOM 是**推测出来的启发式**（`docs/06-调研/抖音网页版底座实测.md` §6.2 只记了坐标级做法），
 * 因此解析必须「宁缺勿错」：拿不准的字段给 `null`，不编造作者、不编造点赞数。
 */

/** 视口里的一个矩形（复用于回复按钮定位）。 */
export interface Box {
  x: number;
  y: number;
  width: number;
  height: number;
}

/**
 * 一条评论块的**原始**快照（`site.ts` 在页面里量出来的）。
 *
 * 刻意保持"未解析"：作者名与内容混在 `text` 的多行里，怎么拆是 `parseCommentBlock` 的事。
 */
export interface CommentBlockSnapshot {
  /** 块内 `data-*` / `id` / `href` 里出现的长数字串（评论 id 与作者 uid 混在一起）。 */
  ids: string[];
  /** 作者主页链接里的 uid（`/user/<uid>`），拿不到就是 `null`。 */
  authorId: string | null;
  /** 整块 `innerText`（保留换行 —— 拆行是解析的活）。 */
  text: string;
  x: number;
  y: number;
  width: number;
  height: number;
  /** 块内「回复」按钮的坐标；找不到就是 `null`（外层容器常没有它）。 */
  replyBox: Box | null;
  /** 块内是否带「置顶」标记。 */
  pinned: boolean;
}

/** 解析后的评论（回给服务端的形状）。 */
export interface ParsedComment {
  /** 评论 id（`null` = DOM 里没有，只能用 作者 + 内容 定位）。 */
  id: string | null;
  author: string;
  authorId: string | null;
  /** 评论正文（已去掉作者 / 时间 / 点赞 / 按钮文案）。 */
  text: string;
  /** 相对时间原文（如「2小时前」），拿不到就是 `null`。 */
  time: string | null;
  /** 点赞数（「1.2万」已折算成 12000）；拿不到就是 `null`。 */
  likes: number | null;
  /** 「展开 N 条回复」里的 N；没有就是 `null`。 */
  replyCount: number | null;
  pinned: boolean;
  /** 正文里出现的 `@某某`（已去重、去尾部标点）。 */
  mentions: string[];
  /** 稳定去重键：有 id 用 id，否则用 作者 + 内容。 */
  key: string;
}

/** 回复目标的定位条件（三个都给是**与**关系，都空则报错）。 */
export interface CommentTarget {
  commentId?: string | null;
  author?: string | null;
  /** 评论内容片段（前 10 个字就够，页面里做包含匹配）。 */
  text?: string | null;
}

/** 定位结果：`index >= 0` 为命中，否则 `problem` 是能直接回给模型的话。 */
export interface CommentTargetLookup {
  /** 命中时是快照下标；未命中是 `-1`。 */
  index: number;
  /** 靠什么命中的（未命中为 `null`）。 */
  matchedBy: "id" | "text" | "author+text" | null;
  /** 未命中的原因（命中为 `null`）。 */
  problem: string | null;
}

/** 相对时间的形态（抖音是「2小时前」「3天前」「刚刚」「2024-05-06」这几种）。 */
const TIME_PATTERN =
  /(刚刚|(\d+)\s*(秒|分钟|小时|天|周|月|年)前|\d{4}-\d{1,2}-\d{1,2}|今天|昨天)/;

/** 「展开 3 条回复」「共 12 条回复」。 */
const REPLY_COUNT_PATTERN = /(?:展开|查看|共)\s*(\d+)\s*条回复/;

/** 纯数字（点赞数）；允许 `1.2万` / `3,412`。 */
const LIKES_PATTERN = /^([\d,]+(?:\.\d+)?)(万|w)?$/i;

/** 这些行是按钮 / 标签，不是正文。 */
const NOISE_LINES = new Set([
  "回复",
  "展开",
  "收起",
  "作者",
  "置顶",
  "点赞",
  "分享",
  "收藏",
  "举报",
  "翻译",
  "展开更多",
  "展开更多回复",
]);

/**
 * 把一条评论块快照解析成结构化评论。
 *
 * @param snapshot 页面量出来的原始快照。
 * @returns 解析结果；`text` 为空（只有按钮没有正文）时返回 `null`。
 */
export function parseCommentBlock(snapshot: CommentBlockSnapshot): ParsedComment | null {
  const lines = snapshot.text
    .split("\n")
    .map((line) => line.replace(/\s+/g, " ").trim())
    .filter((line) => line.length > 0);

  if (lines.length === 0) {
    return null;
  }

  const time = lines.map((line) => TIME_PATTERN.exec(line)?.[0]).find((hit) => hit) ?? null;

  // 作者：第一个既不是时间、也不是纯数字、也不是按钮文案的行
  const author =
    lines.find(
      (line) =>
        !TIME_PATTERN.test(line) && !LIKES_PATTERN.test(line) && !NOISE_LINES.has(line),
    ) ?? "";

  // 点赞：最后一个纯数字行（结构是「作者 → 正文 → 时间 → 点赞 → 回复」）
  let likes: number | null = null;
  for (const line of [...lines].reverse()) {
    const hit = LIKES_PATTERN.exec(line);
    if (!hit) {
      continue;
    }

    const value = Number.parseFloat((hit[1] ?? "").replace(/,/g, ""));
    if (Number.isFinite(value)) {
      likes = Math.round(hit[2] ? value * 10_000 : value);
      break;
    }
  }

  const replyCountHit = REPLY_COUNT_PATTERN.exec(snapshot.text);
  const replyCount = replyCountHit ? Number.parseInt(replyCountHit[1] ?? "", 10) : null;

  const body = lines
    .filter((line) => line !== author)
    .filter((line) => !TIME_PATTERN.test(line))
    .filter((line) => !LIKES_PATTERN.test(line))
    .filter((line) => !NOISE_LINES.has(line))
    .filter((line) => !REPLY_COUNT_PATTERN.test(line))
    .join("\n")
    .trim();

  // 纯数字评论（「666」这种很常见）会被上面的点赞过滤吃掉：正文空了就把**第一个**数字行还回来当正文，
  // 只有「整块只有一个数字」时才把它同时当点赞 —— 否则一条「666」会显示成 666 个赞
  const numericLines = lines.filter((line) => LIKES_PATTERN.test(line));
  const numericBody = body.length === 0 ? numericLines[0] ?? null : null;
  const finalBody = body.length > 0 ? body : numericBody ?? "";

  if (!finalBody) {
    return null;
  }

  if (numericBody !== null && numericLines.length === 1) {
    likes = null;
  }

  const id = pickCommentId(snapshot.ids, snapshot.authorId);
  const authorId = snapshot.authorId;

  return {
    id,
    author: author || "(未知作者)",
    authorId,
    text: finalBody,
    time,
    likes,
    replyCount: Number.isFinite(replyCount) ? replyCount : null,
    pinned: snapshot.pinned || lines.includes("置顶"),
    mentions: extractMentions(finalBody),
    // 去重键**不能带序号**：滚动分页时同一条评论会在两次快照里排到不同位置，
    // 带序号等于「每次读都是新评论」，分页会越滚越多（`index` 只在兜底字段里用得到）
    key: id ?? `a:${author || "?"}|t:${collapse(finalBody).slice(0, 40)}`,
  };
}

/**
 * 批量解析 + 去重（嵌套容器会让同一条评论出现两次）。
 *
 * @param blocks 页面快照（顺序即视觉顺序）。
 * @returns 按视觉顺序排好的评论；正文为空的块被丢弃。
 */
export function parseCommentBlocks(blocks: readonly CommentBlockSnapshot[]): ParsedComment[] {
  const parsed: ParsedComment[] = [];
  const seen = new Set<string>();

  for (const block of blocks) {
    const comment = parseCommentBlock(block);
    if (!comment) {
      continue;
    }

    // 去重键不用 index：嵌套快照的 id 一样（没有 id 时用作者+内容前缀）
    const key = comment.id ?? `a:${comment.author}|t:${collapse(comment.text).slice(0, 40)}`;
    if (seen.has(key)) {
      continue;
    }

    seen.add(key);
    parsed.push(comment);
  }

  // 置顶的排最前（与页面视觉一致）；其余保持原顺序（Array.prototype.sort 是稳定的）
  return parsed.sort((a, b) => Number(b.pinned) - Number(a.pinned));
}

/**
 * 按 id / 作者 + 内容 定位一条评论（**宁缺勿错**：条件不足或有歧义就报问题）。
 *
 * @param blocks 页面快照（新读一次，别用旧坐标）。
 * @param target 定位条件。
 */
export function pickCommentTarget(
  blocks: readonly CommentBlockSnapshot[],
  target: CommentTarget,
): CommentTargetLookup {
  const commentId = normalizeOptional(target.commentId);
  const author = normalizeOptional(target.author);
  const textProbe = collapse(normalizeOptional(target.text) ?? "").slice(0, 10);

  if (!commentId && !author && !textProbe) {
    return miss("要指定回复哪一条：给评论 id（读评论时那串数字），或者给 作者 + 内容片段。");
  }

  const indexById = commentId
    ? blocks.findIndex((block) => block.ids.includes(commentId))
    : -1;
  if (indexById >= 0) {
    return { index: indexById, matchedBy: "id", problem: null };
  }

  // 只给了 id 而没命中 ⇒ **不许**退化成"随便挑一条"（会回错人，是这类写操作最贵的错）
  if (!author && !textProbe) {
    return miss(
      `当前页面里找不到这条评论（id=${commentId}）：可能被折叠 / 还没加载，先用 douyin_comments 再读一次。`,
    );
  }

  const candidates = blocks
    .map((block, index) => ({ block, index }))
    .filter(({ block }) => !author || collapse(block.text).includes(collapse(author)))
    .filter(({ block }) => textProbe.length === 0 || collapse(block.text).includes(textProbe));

  if (candidates.length === 1) {
    return {
      index: candidates[0]!.index,
      matchedBy: author && textProbe ? "author+text" : "text",
      problem: null,
    };
  }

  if (candidates.length > 1) {
    // 「作者 + 内容」都对上的才允许挑；只凭作者命中多条时**不许猜**
    const exact = candidates.filter(
      ({ block }) => textProbe.length > 0 && collapse(block.text).includes(textProbe),
    );
    if (exact.length === 1 && author) {
      return { index: exact[0]!.index, matchedBy: "author+text", problem: null };
    }

    return miss(
      `对上了 ${candidates.length} 条评论，没法确定是哪一条：请给评论 id，或再补几个字的内容片段。`,
    );
  }

  const hint = commentId ? `（id=${commentId}）` : "";
  return miss(`当前页面里找不到这条评论${hint}：可能被折叠 / 还没加载，先用 douyin_comments 再读一次。`);
}

/** 输入框里是不是还留着「@某人」前缀（丢了就说明会变成顶层评论 —— 实测踩过的坑）。 */
export function hasAtPrefix(value: string): boolean {
  return /^\s*@/.test(value);
}

/** 把多行文本折成一行（比较 / 去重用）。 */
export function collapse(text: string): string {
  return text.replace(/\s+/g, " ").trim();
}

/**
 * 从正文里抽 `@某某`。
 *
 * 只做「@ 后面跟一段非空白」的粗抽：抖音正文里的提及是纯文本（不是结构化的 mention 节点），
 * 抽出来给服务端与主人昵称比对即可 —— 这里不判"是不是主人"（那要知道主人昵称，属于服务端）。
 */
export function extractMentions(text: string): string[] {
  const found = new Set<string>();

  for (const hit of text.matchAll(/@([^\s@:：,，。!！?？、]+)/g)) {
    const name = (hit[1] ?? "").trim();
    if (name.length > 0 && name.length <= 40) {
      found.add(name);
    }
  }

  return [...found];
}

/**
 * 一条评论 id：优先 15 位以上的长数字，且**不能等于作者 uid**（同一块里两者都会出现）。
 */
function pickCommentId(ids: readonly string[], authorId: string | null): string | null {
  const candidates = ids
    .filter((id) => /^\d{15,}$/.test(id))
    .filter((id) => id !== authorId)
    .sort((a, b) => b.length - a.length);

  return candidates[0] ?? null;
}

function normalizeOptional(value: string | null | undefined): string | null {
  const trimmed = (value ?? "").trim();
  return trimmed.length > 0 ? trimmed : null;
}

function miss(problem: string): CommentTargetLookup {
  return { index: -1, matchedBy: null, problem };
}
