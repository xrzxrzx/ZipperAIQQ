/**
 * `protocol.ts` 的单测（`node --test`，不联网、不开浏览器）。
 *
 * 覆盖「最容易静默出错」的四块：
 *   ① 帧解析（畸形帧不许抛）
 *   ② 文本分段（代理对不许被切断）
 *   ③ 出站计划（降级必须被记下来，空消息必须失败）
 *   ④ 入站归一化（真实抓包的 content JSON；命令型事件必须丢）
 *
 * 夹具里的 `content` 字符串来自 2026-11-18 的真实抓包（`ws-016` 表情 / `ws-018` 文本）。
 */

import assert from "node:assert/strict";
import { test } from "node:test";

import {
  buildOutboundPlan,
  CAPABILITIES,
  chunkText,
  classifyFailure,
  IdLru,
  MESSAGE_TYPE,
  normalizePrivateMessage,
  OutboundError,
  parseFrame,
  parseSessionId,
  resolveThreadId,
  sessionIdFor,
  toInboundFrame,
} from "../src/protocol.ts";

// ─────────────────────────── ① 帧解析 ───────────────────────────

test("parseFrame：正常帧往返", () => {
  const frame = parseFrame('{"type":"outbound","time":123,"data":{"session":"douyin:p1"}}');
  assert.ok(frame);
  assert.equal(frame.type, "outbound");
  assert.equal(frame.time, 123);
  assert.equal(frame.data?.session, "douyin:p1");
});

test("parseFrame：畸形帧一律返回 null，不抛", () => {
  for (const raw of ["", "not json", "[]", "{}", '{"data":{}}', "null"]) {
    assert.equal(parseFrame(raw), null, `应判为畸形：${raw}`);
  }
});

test("parseFrame：data 缺省时给空对象（省掉调用方判空）", () => {
  const frame = parseFrame('{"type":"ping"}');
  assert.ok(frame);
  assert.deepEqual(frame.data, {});
});

// ─────────────────────────── ② 会话与分段 ───────────────────────────

test("会话 ID 往返：私聊 / 群聊", () => {
  assert.equal(sessionIdFor("private", "888"), "douyin:p888");
  assert.equal(sessionIdFor("group", "99"), "douyin:g99");
  assert.deepEqual(parseSessionId("douyin:p888"), { chatType: "private", threadId: "888" });
  assert.deepEqual(parseSessionId("douyin:g99"), { chatType: "group", threadId: "99" });
  assert.equal(parseSessionId("qq:p1"), null);
});

test("chunkText：不切断代理对", () => {
  const emoji = "😀"; // 两个 code unit
  const text = emoji.repeat(4);
  const chunks = chunkText(text, 3);

  for (const chunk of chunks) {
    assert.ok(
      !/[\uD800-\uDBFF]$/.test(chunk) || /[\uDC00-\uDFFF]/.test(chunk),
      `切出了孤立的高代理：${chunk}`,
    );
  }

  assert.equal(chunks.join(""), text);
});

test("chunkText：上限大于长度时原样返回；空串返回空数组", () => {
  assert.deepEqual(chunkText("abc", 10), ["abc"]);
  assert.deepEqual(chunkText("", 10), []);
});

// ─────────────────────────── ③ 出站计划 ───────────────────────────

test("buildOutboundPlan：文本 + 图片 URL + 引用降级", () => {
  const plan = buildOutboundPlan({
    type: "outbound",
    data: {
      session: "douyin:p1234567890",
      requestId: "r1",
      replyTo: "7709523357389440570",
      segments: [
        { type: "text", text: "收到～" },
        { type: "image", url: "https://p3.douyinpic.com/a.jpg" },
      ],
    },
  });

  assert.equal(plan.chatType, "private");
  assert.equal(plan.threadId, "1234567890");
  assert.deepEqual(plan.chunks, ["收到～"]);
  assert.deepEqual(plan.imageUrls, ["https://p3.douyinpic.com/a.jpg"]);
  assert.equal(plan.replyTo, "7709523357389440570");
  assert.deepEqual(plan.degraded, ["reply_to"]);
});

test("buildOutboundPlan：内联 base64 图走 imageData（服务端自己生成的图）", () => {
  const plan = buildOutboundPlan({
    type: "outbound",
    data: {
      session: "douyin:p1",
      segments: [{ type: "image", data: "iVBORw0KGgo=" }],
    },
  });

  assert.deepEqual(plan.imageData, ["iVBORw0KGgo="]);
  assert.deepEqual(plan.imageUrls, []);
  assert.deepEqual(plan.degraded, []);
});

test("buildOutboundPlan：空内容 / 只有引用 → bad_request", () => {
  for (const data of [
    { session: "douyin:p1", segments: [{ type: "reply", messageId: "1" }] },
    { session: "douyin:p1", segments: [] },
  ]) {
    assert.throws(
      () => buildOutboundPlan({ type: "outbound", data }),
      (error: unknown) => error instanceof OutboundError && error.code === "bad_request",
    );
  }
});

test("buildOutboundPlan：缺 session / 非抖音会话 → bad_request", () => {
  assert.throws(
    () => buildOutboundPlan({ type: "outbound", data: { segments: [] } }),
    (error: unknown) => error instanceof OutboundError && error.code === "bad_request",
  );

  assert.throws(
    () => buildOutboundPlan({ type: "outbound", data: { session: "qq:p1", segments: [] } }),
    (error: unknown) => error instanceof OutboundError && error.code === "bad_request",
  );
});

test("buildOutboundPlan：超长文本按上限切多块", () => {
  const plan = buildOutboundPlan(
    { type: "outbound", data: { session: "douyin:p1", segments: [{ type: "text", text: "a".repeat(25) }] } },
    { maxTextLength: 10 },
  );

  assert.deepEqual(plan.chunks.map((chunk) => chunk.length), [10, 10, 5]);
});

// ─────────────────────────── ④ 入站归一化 ───────────────────────────

const TEXT_CONTENT =
  '{"type":0,"instruction_type":0,"item_type_local":-1,"text":"舅舅家","createdAt":0,"is_card":false,"msgHint":"","aweType":700}';

const EMOJI_CONTENT =
  '{"aweType":507,"display_name":"比心","emoji_type":"lite_emoji",' +
  '"url":{"uri":"https://p26-sign.douyinpic.com/obj/im-resource/x.png",' +
  '"url_list":["https://p11-sign.douyinpic.com/obj/im-resource/x.png"]},"width":100,"height":100}';

const SELF = "1000000000";
const PEER = "1234567890";
const CONVERSATION = `0:1:${SELF}:${PEER}`;

test("normalizePrivateMessage：文本消息（真实抓包形状）", () => {
  const event = normalizePrivateMessage(
    {
      conversationId: CONVERSATION,
      conversationType: 1,
      serverMessageId: "7709523357389440570",
      messageType: MESSAGE_TYPE.legacyText,
      sender: SELF,
      content: TEXT_CONTENT,
      createTime: "1795013288293",
    },
    SELF,
  );

  assert.ok(event);
  assert.equal(event.chatType, "private");
  assert.equal(event.threadId, PEER);
  assert.equal(event.text, "舅舅家");
  assert.equal(event.messageId, "7709523357389440570");
  assert.equal(event.timeFromPlatform, true);
  assert.equal(event.occurredAtMs, 1795013288293);
});

test("normalizePrivateMessage：表情包降级成占位（不丢事件）", () => {
  const event = normalizePrivateMessage(
    {
      conversationId: CONVERSATION,
      conversationType: 1,
      serverMessageId: "7709523343171569189",
      messageType: MESSAGE_TYPE.legacyEmoji,
      sender: PEER,
      content: EMOJI_CONTENT,
    },
    SELF,
  );

  assert.ok(event);
  assert.equal(event.threadId, PEER);
  assert.deepEqual(event.placeholders, ["[表情:比心]"]);
  assert.equal(event.text, undefined);
});

test("normalizePrivateMessage：命令型事件必须丢弃", () => {
  const event = normalizePrivateMessage(
    {
      conversationId: CONVERSATION,
      conversationType: 1,
      messageType: MESSAGE_TYPE.command,
      sender: PEER,
      content: "{}",
    },
    SELF,
  );

  assert.equal(event, null);
});

test("normalizePrivateMessage：图片消息取出 CDN 直链", () => {
  const event = normalizePrivateMessage(
    {
      conversationId: CONVERSATION,
      conversationType: 1,
      messageType: MESSAGE_TYPE.image,
      sender: PEER,
      content: JSON.stringify({ url_list: ["https://p3.douyinpic.com/a.webp"], width: 10 }),
    },
    SELF,
  );

  assert.ok(event);
  assert.deepEqual(event.imageUrls, ["https://p3.douyinpic.com/a.webp"]);
  assert.deepEqual(event.placeholders, []);
});

test("normalizePrivateMessage：视频 / 分享卡片降级成占位（B4：带 itemId 时把 id 一起递出去）", () => {
  const video = normalizePrivateMessage(
    { conversationId: CONVERSATION, conversationType: 1, messageType: MESSAGE_TYPE.video, sender: PEER, content: "{}" },
    SELF,
  );
  assert.deepEqual(video?.placeholders, ["[视频]"]);

  // 有 id 就给模型 id：否则 `douyin_video` 没有任何抓手去取这一条
  const videoWithId = normalizePrivateMessage(
    {
      conversationId: CONVERSATION,
      conversationType: 1,
      messageType: MESSAGE_TYPE.video,
      sender: PEER,
      content: JSON.stringify({ videoId: "7706296015465071715" }),
    },
    SELF,
  );
  assert.deepEqual(videoWithId?.placeholders, ["[视频 id=7706296015465071715]"]);

  const share = normalizePrivateMessage(
    {
      conversationId: CONVERSATION,
      conversationType: 1,
      messageType: MESSAGE_TYPE.shareCard,
      sender: PEER,
      content: JSON.stringify({ itemId: "7706296015465071715", title: "戒网瘾" }),
    },
    SELF,
  );

  assert.deepEqual(share?.placeholders, ["[分享:戒网瘾（id=7706296015465071715）]"]);

  // 2026-11-26 真机：私信里的分享卡片实际是 messageType=8（不是文档里的 105），
  // 认不出来就只剩 [消息类型:8] —— 模型既拿不到标题也拿不到 id
  const shareByType8 = normalizePrivateMessage(
    {
      conversationId: CONVERSATION,
      conversationType: 1,
      messageType: MESSAGE_TYPE.shareCardAlt,
      sender: PEER,
      content: JSON.stringify({ itemId: "7709503373430574362" }),
    },
    SELF,
  );

  assert.deepEqual(shareByType8?.placeholders, ["[分享卡片（id=7709503373430574362）]"]);
  assert.equal(shareByType8?.sharedItemId, "7709503373430574362");
  assert.equal(share?.sharedItemId, "7706296015465071715");
});

test("resolveThreadId：私聊取「不是自己」的那个 uid；群聊取最后一段", () => {
  assert.equal(resolveThreadId(CONVERSATION, SELF, SELF, "private")?.peer, PEER);
  assert.equal(resolveThreadId(CONVERSATION, PEER, SELF, "private")?.peer, PEER);
  assert.equal(resolveThreadId("0:2:555:777", PEER, SELF, "group")?.peer, "777");
  assert.equal(resolveThreadId("", PEER, SELF, "private"), null);
});

test("toInboundFrame：吐出的帧符合服务端解析口径", () => {
  const event = normalizePrivateMessage(
    {
      conversationId: CONVERSATION,
      conversationType: 1,
      serverMessageId: "7709523357389440570",
      messageType: MESSAGE_TYPE.legacyText,
      sender: PEER,
      content: TEXT_CONTENT,
      createTime: "1795013288293",
      referenceInfo: { referencedMessageId: "7709523343171569189" },
    },
    SELF,
  );

  assert.ok(event);
  const frame = toInboundFrame(event);

  assert.equal(frame.type, "inbound");
  assert.equal(frame.data?.type, "private");
  assert.equal(frame.data?.threadId, PEER);
  assert.equal(frame.data?.session, `douyin:p${PEER}`);
  assert.equal(frame.data?.serverMessageId, "7709523357389440570");
  assert.deepEqual(frame.data?.sender, { userId: PEER, nickname: undefined });
  assert.deepEqual(frame.data?.segments, [
    { type: "reply", messageId: "7709523343171569189" },
    { type: "text", text: "舅舅家" },
  ]);
  assert.equal(frame.data?.time, 1795013288293);
});

// ─────────────────────────── ⑤ 幂等 LRU ───────────────────────────

test("IdLru：重复 id 第二次返回 false，超出容量淘汰最旧", () => {
  const lru = new IdLru(2);

  assert.equal(lru.admit("a"), true);
  assert.equal(lru.admit("a"), false);
  assert.equal(lru.admit("b"), true);
  assert.equal(lru.admit("c"), true); // 挤掉 a
  assert.equal(lru.size, 2);
  assert.equal(lru.admit("a"), true); // a 已被淘汰，可以再进来
  assert.equal(lru.admit(""), true); // 空 id 不去重
});

test("IdLru：容量必须为正", () => {
  assert.throws(() => new IdLru(0), /容量必须为正数/);
});

// ─────────────────────────── ⑥ 失败分类 ───────────────────────────

test("CAPABILITIES：只声明真的能原样还原的能力（mention / reply 不声明）", () => {
  assert.deepEqual([...CAPABILITIES], ["text", "image"]);
});

test("buildOutboundPlan：@ 段一律降级留痕（网页版按 uid 精确 @ 做不到）", () => {
  const plan = buildOutboundPlan({
    type: "outbound",
    data: {
      session: "douyin:g777",
      segments: [
        { type: "text", text: "在吗" },
        { type: "mention", userId: "1234567890" },
      ],
    },
  });

  assert.deepEqual(plan.chunks, ["在吗"]);
  assert.equal(plan.mentions.length, 1);
  assert.ok(plan.degraded.includes("mention"), "少了一个 @ 必须留痕，不能假装成功");
});

test("classifyFailure：把常见抛出物映射成可重试性判据", () => {
  assert.equal(classifyFailure(new Error("Target page, context or browser has been closed")).code, "page");
  // 自家 browser.page 抛的是中文：漏了它 → 判成 unknown → 自愈不触发（审查 P1-8）
  assert.equal(classifyFailure(new Error("浏览器页面不可用（尚未启动或已崩溃）")).code, "page");
  assert.equal(classifyFailure(new Error("Timeout 15000ms exceeded")).code, "timeout");
  assert.equal(classifyFailure(new Error("出现验证码，请完成校验")).code, "risk");
  assert.equal(classifyFailure(new Error("未能进入私信页：可能未登录")).code, "auth");
  assert.equal(classifyFailure(new Error("找不到会话（threadId=1）")).code, "not_found");
  assert.equal(classifyFailure(new Error("图片上传入口没命中 upload failed")).code, "upload");
  assert.equal(
    classifyFailure(new OutboundError("readback", "回读不一致")).code,
    "readback",
  );
});
