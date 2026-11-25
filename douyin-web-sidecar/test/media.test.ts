/**
 * `media.ts` 的纯函数部分（B4）：详情解析 / 播放地址排序 / MP4 头判定 / 超时 / 内存缓存。
 *
 * 不联网、不开浏览器：抽帧与抽音要真页面，那是真机验收的事（`docs/08-上手/抖音web底座部署与登录.md`）。
 */

import { test } from "node:test";
import assert from "node:assert/strict";

// 走 `dist/`：`src/media.ts` 有相对导入，Node 的类型剥离解析不了 `./protocol.js` 这种写法；
// 而 `dist/` 是 `npm test` 先编译出来的产物（与 `send-api.test.ts` 同一条路）。
import {
  framesWireChars,
  looksLikeMp4,
  MediaError,
  parseVideoDetail,
  sortPlayUrls,
  VideoMediaCache,
  withTimeout,
} from "../dist/media.js";

/** 实测形状（`docs/06-调研/抖音网页版底座实测.md` §7.2）：`aweme_details[0].video.play_addr.url_list`。 */
const DETAIL_SAMPLE = {
  status_code: 0,
  aweme_details: [
    {
      aweme_id: "7399000000000000001",
      desc: "戒网瘾 测试",
      duration: undefined,
      video: {
        duration: 28_666,
        play_addr: {
          url_list: [
            "https://v3-web.douyinvod.com/aaa/video/tos/cn/tos-cn-ve-15/play.m3u8",
            "https://v26-web.douyinvod.com/bbb/video/tos/cn/tos-cn-ve-15/play.mp4",
          ],
        },
      },
    },
  ],
};

test("parseVideoDetail：实测形状 → id / 文案 / 时长 / 地址（mp4 排前）", () => {
  const detail = parseVideoDetail(DETAIL_SAMPLE, "7399000000000000001");
  assert.ok(detail);
  assert.equal(detail.itemId, "7399000000000000001");
  assert.equal(detail.title, "戒网瘾 测试");
  assert.equal(detail.durationMs, 28_666);
  assert.deepEqual(detail.playUrls, [
    "https://v26-web.douyinvod.com/bbb/video/tos/cn/tos-cn-ve-15/play.mp4",
    "https://v3-web.douyinvod.com/aaa/video/tos/cn/tos-cn-ve-15/play.m3u8",
  ]);
});

test("parseVideoDetail：没有播放地址 → null（不许回空列表冒充成功）", () => {
  assert.equal(parseVideoDetail({ aweme_details: [{ aweme_id: "1", video: {} }] }, "1"), null);
  assert.equal(
    parseVideoDetail({ aweme_details: [{ aweme_id: "1", video: { play_addr: { url_list: [] } } }] }, "1"),
    null,
  );
  assert.equal(parseVideoDetail({ aweme_details: [] }, "1"), null);
  assert.equal(parseVideoDetail({}, "1"), null);
  assert.equal(parseVideoDetail(null, "1"), null);
});

test("parseVideoDetail：url_list 里的非字符串被丢掉；全丢掉就等于没有地址", () => {
  const detail = parseVideoDetail(
    { aweme_details: [{ aweme_id: "1", video: { play_addr: { url_list: [null, 42, "  "] } } }] },
    "1",
  );
  assert.equal(detail, null);

  const partial = parseVideoDetail(
    { aweme_details: [{ aweme_id: "1", video: { play_addr: { url_list: [null, "https://x/a.mp4"] } } }] },
    "1",
  );
  assert.deepEqual(partial?.playUrls, ["https://x/a.mp4"]);
});

test("parseVideoDetail：时长按量级兼容秒 / 毫秒；缺了就是 0", () => {
  const seconds = parseVideoDetail(
    { aweme_details: [{ aweme_id: "1", video: { duration: 28, play_addr: { url_list: ["https://x/a.mp4"] } } }] },
    "1",
  );
  assert.equal(seconds?.durationMs, 28_000);

  const missing = parseVideoDetail(
    { aweme_details: [{ aweme_id: "1", video: { play_addr: { url_list: ["https://x/a.mp4"] } } }] },
    "1",
  );
  assert.equal(missing?.durationMs, 0);
});

test("parseVideoDetail：接口把结果放在 aweme_list 时也能取（两种包裹都见过）", () => {
  const detail = parseVideoDetail(
    { aweme_list: [{ aweme_id: "777", video: { play_addr: { url_list: ["https://x/a.mp4"] } } }] },
    "777",
  );
  assert.equal(detail?.itemId, "777");
});

test("sortPlayUrls：mp4 在前，其它在后，组内保持原序", () => {
  assert.deepEqual(
    sortPlayUrls(["https://x/1.m3u8", "https://x/2.mp4", "https://x/3.flv", "https://x/4.mp4"]),
    ["https://x/2.mp4", "https://x/4.mp4", "https://x/1.m3u8", "https://x/3.flv"],
  );
});

test("looksLikeMp4：认 `ftyp`，不认 HTML 错误页 / 太短的 body", () => {
  const mp4 = Buffer.concat([
    Buffer.from([0x00, 0x00, 0x00, 0x20]),
    Buffer.from("ftypisom", "latin1"),
    Buffer.alloc(8),
  ]);
  assert.equal(looksLikeMp4(mp4), true);
  assert.equal(looksLikeMp4(Buffer.from("<html>blocked</html>", "latin1")), false);
  assert.equal(looksLikeMp4(Buffer.alloc(4)), false);
});

test("framesWireChars：按 base64 字符数（= 上线字节）算，不是 JPEG 原始字节", () => {
  // 独立审查 P0：按 raw 字节算会小看 4/3，1 MiB 单帧上限就被踩穿、服务端直接断连。
  // 1000 个 base64 字符 = 1000 字节线上流量（base64 全 ASCII）
  assert.equal(framesWireChars([{ jpegBase64: "A".repeat(1000) }]), 1000);
  assert.equal(
    framesWireChars([{ jpegBase64: "A".repeat(600) }, { jpegBase64: "B".repeat(400) }]),
    1000,
  );
  assert.equal(framesWireChars([]), 0);

  // 6 张、每张 150 KiB base64 ⇒ 900 KiB 线上流量，恰好顶到默认预算（900 KiB）——
  // 而它们的 JPEG 原始字节只有 675 KiB，按 raw 算会误判成"还剩很多余量"
  const six = Array.from({ length: 6 }, () => ({ jpegBase64: "A".repeat(150 * 1024) }));
  assert.equal(framesWireChars(six), 900 * 1024);
});

test("withTimeout：超时抛 MediaError(page)，不超时就正常返回", async () => {
  await assert.rejects(
    () => withTimeout(new Promise<never>(() => undefined), 5, "太慢了"),
    (error: unknown) => error instanceof MediaError && error.code === "page" && error.message === "太慢了",
  );

  const value = await withTimeout(Promise.resolve(42), 1_000, "不该超时");
  assert.equal(value, 42);
});

test("VideoMediaCache：LRU 只留 N 条，过期就取不到", () => {
  let now = 1_000;
  const cache = new VideoMediaCache(2, 100, () => now);

  cache.set("a", Buffer.from("aa"));
  cache.set("b", Buffer.from("bb"));
  assert.equal(cache.get("a")?.toString(), "aa");

  // 再塞一条：最久没用的那条被挤掉（刚读过的 a 不算最久没用）
  cache.set("c", Buffer.from("cc"));
  assert.equal(cache.get("b"), null);
  assert.equal(cache.get("a")?.toString(), "aa");
  assert.equal(cache.get("c")?.toString(), "cc");

  now += 101;
  assert.equal(cache.get("a"), null);

  cache.set("d", Buffer.from("dd"));
  cache.clear();
  assert.equal(cache.get("d"), null);
});
