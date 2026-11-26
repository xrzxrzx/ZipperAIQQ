/**
 * `comment.ts` 的纯函数部分（B5）：评论块解析 / 去重 / 回复目标定位 / `@` 前缀校验。
 *
 * 不联网、不开浏览器：评论区 DOM 是启发式的，真机验收在
 * `docs/08-上手/抖音web底座部署与登录.md`；这里锁的是「拿到快照之后不许解析错」。
 *
 * 快照形状来自 `site.ts` 的 `readCommentBlocks`（页面里只量不解析），
 * `text` 的样例行序按实测观察写：「作者 → 正文 → 时间·地区 → 点赞 → 回复」。
 */

import { test } from "node:test";
import assert from "node:assert/strict";

import {
  collapse,
  extractMentions,
  hasAtPrefix,
  parseCommentBlock,
  parseCommentBlocks,
  pickCommentTarget,
  type CommentBlockSnapshot,
} from "../dist/comment.js";

const COMMENT_ID = "7412345678901234567";
const AUTHOR_ID = "88892527967";

function block(overrides: Partial<CommentBlockSnapshot> = {}): CommentBlockSnapshot {
  return {
    ids: [],
    authorId: null,
    text: "",
    x: 900,
    y: 200,
    width: 480,
    height: 120,
    replyBox: { x: 1120, y: 260, width: 32, height: 20 },
    pinned: false,
    ...overrides,
  };
}

const TYPICAL = `拉链
@戒网瘾 这条视频到底在讲什么？
2小时前·北京
128
回复`;

test("parseCommentBlock：拆出作者 / 正文 / 时间 / 点赞", () => {
  const parsed = parseCommentBlock(block({ text: TYPICAL, ids: [COMMENT_ID], authorId: AUTHOR_ID }));

  assert.ok(parsed);
  assert.equal(parsed.author, "拉链");
  assert.equal(parsed.text, "@戒网瘾 这条视频到底在讲什么？");
  assert.equal(parsed.time, "2小时前");
  assert.equal(parsed.likes, 128);
  assert.equal(parsed.id, COMMENT_ID);
  assert.deepEqual(parsed.mentions, ["戒网瘾"]);
});

test("parseCommentBlock：点赞数带「万」要折算，不能记成 1.2", () => {
  const parsed = parseCommentBlock(
    block({ text: "小明\n这个我笑了半天\n3天前·上海\n1.2万\n回复" }),
  );

  assert.ok(parsed);
  assert.equal(parsed.likes, 12_000);
  assert.equal(parsed.time, "3天前");
});

test("parseCommentBlock：正文是纯数字（「666」）时既当正文、又不变点赞数", () => {
  const parsed = parseCommentBlock(block({ text: "路人\n666\n昨天\n42\n回复" }));

  assert.ok(parsed);
  // 42 是点赞，666 是正文 —— 两条都给对，不能把 666 吃掉
  assert.equal(parsed.text, "666");
  assert.equal(parsed.likes, 42);
});

test("parseCommentBlock：只有按钮没有正文的块返回 null（不编造空评论）", () => {
  assert.equal(parseCommentBlock(block({ text: "回复\n展开 2 条回复" })), null);
});

test("parseCommentBlock：展开回复的条数进 replyCount", () => {
  const parsed = parseCommentBlock(
    block({ text: "拉链\n说得好\n1小时前\n9\n展开 3 条回复\n回复" }),
  );

  assert.ok(parsed);
  assert.equal(parsed.replyCount, 3);
  assert.equal(parsed.text, "说得好");
});

test("parseCommentBlock：没有 data-* 时 id 为 null（靠 作者 + 内容 定位）", () => {
  const parsed = parseCommentBlock(block({ text: TYPICAL, ids: [AUTHOR_ID], authorId: AUTHOR_ID }));

  assert.ok(parsed);
  // 只有作者 uid、没有评论 id ⇒ 不许把它当评论 id
  assert.equal(parsed.id, null);
  assert.equal(parsed.key, "a:拉链|t:@戒网瘾 这条视频到底在讲什么？");
});

test("parseCommentBlocks：嵌套容器重复命中的同一条只留一次", () => {
  const outer = block({ text: TYPICAL, ids: [COMMENT_ID], authorId: AUTHOR_ID, height: 240 });
  const inner = block({ text: TYPICAL, ids: [COMMENT_ID], authorId: AUTHOR_ID });

  const parsed = parseCommentBlocks([outer, inner]);

  assert.equal(parsed.length, 1);
  assert.equal(parsed[0]?.id, COMMENT_ID);
});

test("parseCommentBlocks：置顶的排最前，其余保持视觉顺序", () => {
  const parsed = parseCommentBlocks([
    block({ text: "甲\n第一条\n1小时前\n1\n回复", ids: ["7410000000000000001"] }),
    block({
      text: "乙\n置顶的一条\n2小时前\n2\n回复",
      ids: ["7410000000000000002"],
      pinned: true,
    }),
    block({ text: "丙\n第三条\n3小时前\n3\n回复", ids: ["7410000000000000003"] }),
  ]);

  assert.deepEqual(
    parsed.map((comment) => comment.author),
    ["乙", "甲", "丙"],
  );
});

test("pickCommentTarget：评论 id 优先，且只认命中", () => {
  const blocks = [
    block({ text: "甲\n第一条\n1小时前\n1\n回复", ids: ["7410000000000000001"] }),
    block({ text: "乙\n第二条\n2小时前\n2\n回复", ids: ["7410000000000000002"] }),
  ];

  const hit = pickCommentTarget(blocks, { commentId: "7410000000000000002" });

  assert.equal(hit.index, 1);
  assert.equal(hit.matchedBy, "id");
});

test("pickCommentTarget：没有 id 时用 作者 + 内容片段", () => {
  const blocks = [
    block({ text: "甲\n第一条\n1小时前\n1\n回复" }),
    block({ text: "乙\n@机器人 帮我看看这个\n2小时前\n2\n回复" }),
  ];

  const hit = pickCommentTarget(blocks, { author: "乙", text: "@机器人 帮我看看" });

  assert.equal(hit.index, 1);
  assert.equal(hit.matchedBy, "author+text");
});

test("pickCommentTarget：只凭作者命中多条时不许猜", () => {
  const blocks = [
    block({ text: "甲\n第一条\n1小时前\n1\n回复" }),
    block({ text: "甲\n第二条\n2小时前\n2\n回复" }),
  ];

  const hit = pickCommentTarget(blocks, { author: "甲" });

  assert.equal(hit.index, -1);
  assert.match(hit.problem ?? "", /对上了 2 条/);
});

test("pickCommentTarget：什么都没给时说清要什么，而不是回第一条", () => {
  const hit = pickCommentTarget([block({ text: "甲\n第一条\n1小时前\n1\n回复" })], {});

  assert.equal(hit.index, -1);
  assert.match(hit.problem ?? "", /评论 id/);
});

test("pickCommentTarget：找不到时提示先重读（可能被折叠）", () => {
  const hit = pickCommentTarget([block({ text: "甲\n第一条\n1小时前\n1\n回复" })], {
    commentId: "7410000000000009999",
  });

  assert.equal(hit.index, -1);
  assert.match(hit.problem ?? "", /找不到这条评论/);
});

test("hasAtPrefix：只认开头的 @（前缀丢了就说明会变成顶层评论）", () => {
  assert.equal(hasAtPrefix("@拉链 你好"), true);
  assert.equal(hasAtPrefix("  @拉链 你好"), true);
  assert.equal(hasAtPrefix("你好 @拉链"), false);
  assert.equal(hasAtPrefix(""), false);
});

test("extractMentions：多个 @ 去重、尾部标点不吞进来", () => {
  assert.deepEqual(extractMentions("@拉链，@拉链 @ @小助手 在吗？"), ["拉链", "小助手"]);
});

test("collapse：折行折空白（比较用）", () => {
  assert.equal(collapse("  第一行\n第二行  "), "第一行 第二行");
});
