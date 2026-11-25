/**
 * 抖音视频媒体层：**详情 → mp4 → 抽帧 / 抽音**（B4）。
 *
 * 分工：
 *   - 本文件：站点接口（详情）、Playwright 请求上下文（下载）、页面内 `canvas` / WebAudio；
 *   - `protocol.ts`：帧编解码（纯函数，可 `node --test` 直跑）；
 *   - `main.ts`：帧分发与编排。
 *
 * 为什么不用 ffmpeg：部署机上**装不上**（gyan.dev / GitHub / npm 包 / pip 四条路全堵，
 * 见 `docs/06-调研/抖音网页版底座实测.md` §7 / §11 坑 7）。实测证明也不需要：
 *   抽帧 = `<video>` 精确 seek → `canvas.drawImage` → `toDataURL('image/jpeg')`（§7.3）
 *   抽音 = `AudioContext.decodeAudioData` → 混单声道 → 线性重采样 16 kHz → 自封 WAV（§7.4）
 *
 * 为什么抽音要「按窗口」返回：`/ws/channel` 单帧上限 **1 MiB**
 * （`WsChannelEndpoint.MaxFrameBytes`），而 3 分钟 16 kHz 单声道 WAV 就有 5.7 MiB ——
 * 一次塞不进去。所以由服务端按 ≤20 秒的窗口来要，服务端自己拼回整段再交给 ASR。
 * 页面里同时缓存解码结果（`__zipperVideoAudio`），所以第 2 个窗口起不必重新下载/解码。
 */

import type { Page } from "playwright";

import { asRecord, num, str } from "./protocol.js";

/** 抽帧的画质（0–1）。0.8 在 720 宽的图上肉眼无损，体积只有 0.92 的一半。 */
const DEFAULT_JPEG_QUALITY = 0.8;

/**
 * 超预算时的降级画质。
 *
 * 只降这一档：真实视频（720 宽、细节多）实测 200–310 KiB/张，6 张就顶穿 1 MiB 单帧上限；
 * 0.6 后通常回落到 120 KiB 级。再往下糊成色块，模型也认不出画面了 —— 那种时候该少给几张。
 */
const FALLBACK_JPEG_QUALITY = 0.6;

/** 回包 JSON 里除 base64 之外的开销（requestId / atMs / 字段名 / 括号）预留字符数。 */
const JSON_OVERHEAD_CHARS = 512;

/** 解码/找帧/画图的单步超时（毫秒）。 */
const PAGE_ACTION_TIMEOUT_MS = 30_000;

/** 详情接口路径（实测：`report-v23.json`）。 */
const DETAIL_PATH = "/aweme/v1/web/multi/aweme/detail/";

/** 媒体抓取失败：带可机器识别的码 + 一句人话（码给程序看，文本给人看）。 */
export class MediaError extends Error {
  public readonly code: "resolve" | "download" | "decode" | "page" | "too_large" | "args";

  public constructor(code: MediaError["code"], message: string) {
    super(message);
    this.name = "MediaError";
    this.code = code;
  }
}

/** 视频详情（够抽样与转写用的最小集合）。 */
export interface VideoDetail {
  itemId: string;
  /** 作品文案（可能为空串：平台允许没文案）。 */
  title: string;
  /** 时长（毫秒）；取不到时为 0。 */
  durationMs: number;
  /** 候选播放地址（已按「先 mp4」排过序）。 */
  playUrls: string[];
}

/** 一张抽帧。 */
export interface FrameShot {
  /** 真实时间点（毫秒，来自 `<video>.currentTime` —— 不是"我要的那个点"）。 */
  atMs: number;
  /** JPEG 的 base64（不含 `data:` 前缀）。 */
  jpegBase64: string;
  bytes: number;
}

/** 抽帧结果。 */
export interface FrameCapture {
  shots: FrameShot[];
  /** 画面尺寸（已按 `maxWidth` 缩过）。 */
  width: number;
  height: number;
  /** 视频总时长（毫秒）。 */
  durationMs: number;
}

/** 一段音频窗口（16 kHz 单声道 PCM16 WAV）。 */
export interface AudioWindow {
  wavBase64: string;
  bytes: number;
  sampleRate: number;
  startMs: number;
  endMs: number;
  /** 整条音频的总时长（毫秒）——服务端据此判「要不要继续要下一窗」。 */
  durationMs: number;
}

// ═══════════════════════════ ① 详情（纯函数 + 抓取） ═══════════════════════════

/**
 * 把详情接口的原始 JSON 收窄成 {@link VideoDetail}。
 *
 * @param raw 接口响应（**外部数据，先当 `unknown`**）
 * @param itemId 我们请求的那条 id（用于日志/兜底，不做一致性强制：平台偶尔回别的 id）
 * @returns 拿不到播放地址时返回 `null`（调用方据此说人话，而不是回一个空列表冒充成功）
 */
export function parseVideoDetail(raw: unknown, itemId: string): VideoDetail | null {
  const root = asRecord(raw);
  if (!root) {
    return null;
  }

  const details = root.aweme_details ?? root.aweme_list;
  const first = Array.isArray(details) ? asRecord(details[0]) : null;
  if (!first) {
    return null;
  }

  const video = asRecord(first.video);
  const playAddr = asRecord(video?.play_addr);
  const urlList = playAddr?.url_list;
  const urls = Array.isArray(urlList)
    ? urlList
        .map((item) => str(item))
        // 只留绝对地址：`url_list` 里混进空串 / 数字/ `"  "` 这类东西时，
        // 交给下载只会得到一个看不懂的失败（审查：外部数据一律先收窄）
        .filter((item): item is string => item !== undefined && item.startsWith("http"))
    : [];

  // 播放地址是**唯一硬要求**：没有它，后面三件事全能做（下载/抽帧/抽音）都会失败
  if (urls.length === 0) {
    return null;
  }

  const realId = str(first.aweme_id) ?? str(first.awemeId) ?? str(first.item_id) ?? itemId;

  // 时长：`video.duration` 是毫秒（实测），但也见过秒级字段 —— 小于 1000 当秒
  const rawDuration = num(video?.duration) ?? num(first.duration) ?? 0;
  const durationMs = rawDuration > 0 && rawDuration < 1000 ? Math.round(rawDuration * 1000) : Math.round(rawDuration);

  return {
    itemId: realId,
    title: str(first.desc) ?? "",
    durationMs,
    playUrls: sortPlayUrls(urls),
  };
}

/** 播放地址排序：先 mp4（`<video>` 放得动），m3u8 排后面（浏览器 `<video>` 认不了裸 m3u8）。 */
export function sortPlayUrls(urls: readonly string[]): string[] {
  const mp4 = urls.filter((url) => url.includes(".mp4") || url.includes("video/mp4"));
  const others = urls.filter((url) => !mp4.includes(url));
  return [...mp4, ...others];
}

/**
 * 取视频详情。两条路都试：
 *   ① 页面上下文 `fetch`（带页面自己的 cookie，站点接口最认这条路）
 *   ② `context.request`（绕开页面 CORS / secsdk；带 `referer`）
 *
 * 两条都失败时抛 {@link MediaError}，并把两条路各一句话塞进消息 ——
 * 只说"取不到"没法排查，得知道是 403、还是回了个 HTML 登录页。
 */
export async function resolveVideo(page: Page, itemId: string, timeoutMs: number): Promise<VideoDetail> {
  const id = itemId.trim();
  if (!/^[0-9]{5,32}$/.test(id)) {
    throw new MediaError("args", `视频 id 不合法：「${itemId}」（应是纯数字的作品 id）`);
  }

  const query = `aweme_ids=[${id}]&origin=0&device_platform=webapp&aid=6383`;
  const failures: string[] = [];

  // ① 页面内 fetch：cookie / secsdk 都由页面自己带
  try {
    const viaPage = await page.evaluate(
      async (arg: { path: string; timeoutMs: number }) => {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), arg.timeoutMs);
        try {
          const response = await fetch(arg.path, {
            headers: { accept: "application/json, text/plain, */*" },
            credentials: "include",
            signal: controller.signal,
          });
          const text = await response.text();
          return { status: response.status, text };
        } finally {
          clearTimeout(timer);
        }
      },
      { path: `${DETAIL_PATH}?${query}`, timeoutMs },
    );

    const parsed = tryParseJson(viaPage.text);
    const detail = parseVideoDetail(parsed, id);
    if (detail) {
      return detail;
    }

    failures.push(`页面 fetch → ${viaPage.status} ${snippet(viaPage.text)}`);
  } catch (error) {
    failures.push(`页面 fetch 抛出：${describe(error)}`);
  }

  // ② 请求上下文：页面被 secsdk 拦时这条还能走
  try {
    const response = await page.context().request.get(`https://www.douyin.com${DETAIL_PATH}?${query}`, {
      headers: { referer: "https://www.douyin.com/", accept: "application/json, text/plain, */*" },
      timeout: timeoutMs,
    });
    const text = await response.text();
    const detail = parseVideoDetail(tryParseJson(text), id);
    if (detail) {
      return detail;
    }

    failures.push(`请求上下文 → ${response.status()} ${snippet(text)}`);
  } catch (error) {
    failures.push(`请求上下文抛出：${describe(error)}`);
  }

  throw new MediaError(
    "resolve",
    `取不到视频详情（id=${id}）：${failures.join("；")}。` +
      "常见原因：视频已删/私密、登录态失效、或平台改了接口。",
  );
}

// ═══════════════════════════ ② 下载 ═══════════════════════════

/**
 * 下载视频本体（按候选地址依次试）。
 *
 * @throws MediaError `too_large`（超过上限）/ `download`（全部失败）
 */
export async function downloadVideo(
  page: Page,
  detail: VideoDetail,
  maxBytes: number,
  timeoutMs: number,
): Promise<{ bytes: Buffer; url: string }> {
  const failures: string[] = [];

  for (const url of detail.playUrls) {
    try {
      const response = await page.context().request.get(url, {
        // 抖音 CDN 校验 referer；不带给 403
        headers: { referer: "https://www.douyin.com/", "user-agent": "Mozilla/5.0" },
        timeout: timeoutMs,
      });

      if (!response.ok()) {
        failures.push(`${hostOf(url)} → HTTP ${response.status()}`);
        continue;
      }

      // 先看 Content-Length：一条 300 MB 的长视频不能等整段读进内存再拒
      // （64 MiB 的上限如果只在读完 body 后判，等于形同虚设，还可能把 2 GB 的小机顶到 OOM）
      const declared = Number(response.headers()["content-length"] ?? "");
      if (Number.isFinite(declared) && declared > maxBytes) {
        failures.push(`${hostOf(url)} → 声明 ${formatSize(declared)} 超过上限`);
        continue;
      }

      const body = await response.body();
      if (!looksLikeMp4(body)) {
        // 空 body / 错误页：继续试下一个 CDN，而不是把 HTML 当视频往下传
        failures.push(`${hostOf(url)} → 不是 MP4（${body.length} 字节，头 ${snippet(body.toString("latin1"), 24)}）`);
        continue;
      }

      if (body.length > maxBytes) {
        throw new MediaError(
          "too_large",
          `视频本体 ${formatSize(body.length)} 超过上限 ${formatSize(maxBytes)}，` +
            "这类视频请让对方发短一点的片段，或只做首帧。",
        );
      }

      return { bytes: body, url };
    } catch (error) {
      if (error instanceof MediaError) {
        throw error;
      }

      failures.push(`${hostOf(url)} → ${describe(error)}`);
    }
  }

  throw new MediaError(
    "download",
    `视频下载失败（${detail.playUrls.length} 个 CDN 都没成）：${failures.join("；")}`,
  );
}

/**
 * 一批帧在回包里的**字符数**（= 上线字节数，base64 全是 ASCII）。
 *
 * 为什么要单独一个函数：这是「会不会踩穿 `/ws/channel` 1 MiB 单帧上限」的唯一判据，
 * 必须能被单测直接喂数字验证 —— 按 JPEG **原始字节**算会小看 4/3（独立审查 P0）。
 */
export function framesWireChars(shots: readonly { jpegBase64: string }[]): number {
  return shots.reduce((sum, shot) => sum + shot.jpegBase64.length, 0);
}

/** MP4 的头部判定：`....ftyp`（第 4–7 字节）。 */
export function looksLikeMp4(bytes: Buffer): boolean {
  return bytes.length > 12 && bytes.toString("latin1", 4, 8) === "ftyp";
}

// ═══════════════════════════ ③ 抽帧 ═══════════════════════════

/**
 * 在页面里抽帧：把 mp4 喂成 `blob:` 给一个离屏 `<video>`，按时间点 seek 后画到 canvas。
 *
 * @param timesMs 时间点（毫秒）。**服务端算**：首帧 / 等分 / 区间都是业务语义，不该埋在浏览器里。
 * @throws MediaError `decode`（浏览器解不了这个编码）/ `page`（超时）/ `too_large`（图太大）
 */
export async function captureFrames(
  page: Page,
  mp4: Buffer,
  timesMs: readonly number[],
  options: { maxWidth: number; maxPayloadChars: number; timeoutMs: number },
): Promise<FrameCapture> {
  if (timesMs.length === 0) {
    throw new MediaError("args", "抽帧时间点是空的");
  }

  const data = mp4.toString("base64");

  const raw = await withTimeout(
    page.evaluate(
      async (arg: { data: string; times: number[]; maxWidth: number; quality: number; qualityLow: number; budgetChars: number }) => {
        const binary = atob(arg.data);
        const bytes = new Uint8Array(binary.length);
        for (let i = 0; i < binary.length; i += 1) {
          bytes[i] = binary.charCodeAt(i);
        }

        const url = URL.createObjectURL(new Blob([bytes], { type: "video/mp4" }));
        const video = document.createElement("video");
        video.muted = true;
        video.playsInline = true;
        video.preload = "auto";
        video.src = url;
        video.style.position = "fixed";
        video.style.left = "-10000px";
        video.style.top = "0";
        video.style.width = "2px";
        video.style.height = "2px";
        document.body.appendChild(video);

        try {
          await new Promise<void>((resolve, reject) => {
            const timer = setTimeout(
              () => reject(new Error("视频元数据 20 秒内没就绪（多半是浏览器解不了这个编码）")),
              20_000,
            );

            video.addEventListener(
              "loadeddata",
              () => {
                clearTimeout(timer);
                resolve();
              },
              { once: true },
            );

            video.addEventListener(
              "error",
              () => {
                clearTimeout(timer);
                reject(new Error(`视频解码失败（MediaError code=${video.error?.code ?? "-"}）`));
              },
              { once: true },
            );
          });

          const width = video.videoWidth;
          const height = video.videoHeight;
          if (width <= 0 || height <= 0) {
            throw new Error("取不到画面尺寸（这条视频可能只有音轨）");
          }

          const scale = arg.maxWidth > 0 && width > arg.maxWidth ? arg.maxWidth / width : 1;
          const canvas = document.createElement("canvas");
          canvas.width = Math.max(1, Math.round(width * scale));
          canvas.height = Math.max(1, Math.round(height * scale));

          const context = canvas.getContext("2d");
          if (!context) {
            throw new Error("canvas 2d 上下文创建失败");
          }

          const duration = Number.isFinite(video.duration) ? video.duration : 0;

          // 按给定质量把全部时间点拍一遍；超预算时**在页面内**用低质量重拍一次
          // （只重拍一次：反复降质会把画面糊成色块，而"少给几张"由上层按 count 决定）
          const shoot = async (quality: number) => {
            const shots: { atMs: number; jpegBase64: string; bytes: number }[] = [];

            for (const wanted of arg.times) {
              // 末尾留 50 ms 余量：正好 seek 到 duration 时有些解码器永远不派 seeked
              const limit = duration > 0 ? Math.max(0, duration - 0.05) : wanted / 1000;
              const seconds = Math.min(Math.max(0, wanted / 1000), limit);

              if (Math.abs(video.currentTime - seconds) > 0.005) {
                await new Promise<void>((resolve, reject) => {
                  const timer = setTimeout(
                    () => reject(new Error(`定位到 ${seconds.toFixed(2)} 秒超时`)),
                    15_000,
                  );

                  video.addEventListener(
                    "seeked",
                    () => {
                      clearTimeout(timer);
                      resolve();
                    },
                    { once: true },
                  );

                  video.currentTime = seconds;
                });
              }

              context.drawImage(video, 0, 0, canvas.width, canvas.height);
              const dataUrl = canvas.toDataURL("image/jpeg", quality);
              const comma = dataUrl.indexOf(",");
              const jpegBase64 = comma >= 0 ? dataUrl.slice(comma + 1) : "";
              if (jpegBase64.length === 0) {
                throw new Error(`第 ${shots.length + 1} 张图编码失败`);
              }

              shots.push({
                atMs: Math.round(seconds * 1000),
                jpegBase64,
                bytes: Math.round((jpegBase64.length * 3) / 4),
              });
            }

            return shots;
          };

          // 预算按 **base64 字符数**算：base64 全是 ASCII ⇒ 字符数 = 上线字节数。
          // 按 JPEG 原始字节算会小看 4/3，1 MiB 单帧上限就这么被踩穿的（独立审查 P0）。
          const wireSize = (shots: { jpegBase64: string }[]): number =>
            shots.reduce((sum, shot) => sum + shot.jpegBase64.length, 0);

          let usedQuality = arg.quality;
          let shots = await shoot(arg.quality);

          if (wireSize(shots) > arg.budgetChars) {
            usedQuality = arg.qualityLow;
            shots = await shoot(arg.qualityLow);
          }

          return {
            shots,
            width: canvas.width,
            height: canvas.height,
            durationMs: Math.round(duration * 1000),
            quality: usedQuality,
            wireChars: wireSize(shots),
          };
        } finally {
          video.removeAttribute("src");
          video.load();
          video.remove();
          URL.revokeObjectURL(url);
        }
      },
      {
        data,
        times: [...timesMs],
        maxWidth: options.maxWidth,
        quality: DEFAULT_JPEG_QUALITY,
        qualityLow: FALLBACK_JPEG_QUALITY,
        budgetChars: Math.max(1024, options.maxPayloadChars - JSON_OVERHEAD_CHARS),
      },
    ),
    options.timeoutMs,
    `抽帧超时（${options.timeoutMs} 毫秒）`,
  );

  // 兜底再判一次（含 JSON 余量）：页面内用的是同一套字符预算，这里补上 requestId / 字段名的开销
  const wireChars = framesWireChars(raw.shots) + JSON_OVERHEAD_CHARS;
  if (wireChars > options.maxPayloadChars) {
    throw new MediaError(
      "too_large",
      `${raw.shots.length} 张截图编码后合计 ${wireChars} 字符，超过单帧回传上限 ` +
        `${options.maxPayloadChars}；请减少张数（count）再试。`,
    );
  }

  return {
    shots: raw.shots,
    width: raw.width,
    height: raw.height,
    durationMs: raw.durationMs,
  };
}

// ═══════════════════════════ ④ 抽音（按窗口） ═══════════════════════════

/** 目标采样率：ASR 接口只认 16 kHz（实测口径）。 */
const TARGET_SAMPLE_RATE = 16_000;

/** 页面内解码缓存最多留几条（见 `cache.set` 处说明：无界会把标签页顶到 OOM）。 */
const MAX_PAGE_AUDIO_CACHE = 1;

/**
 * 取一段音频窗口（16 kHz 单声道 PCM16 WAV）。
 *
 * @param key 缓存键（用视频 id 就够了：同一视频反复要窗口时不重复下载/解码）
 * @param mp4 视频字节；为 `null` 表示「上次已经给过，页面里应该有缓存」（缓存没了会回 `needData`）
 * @param startMs 窗口起点（毫秒）
 * @param endMs 窗口终点（毫秒，**不含**）
 *
 * @throws MediaError `decode` / `page` / `too_large`
 */
export async function extractAudioWindow(
  page: Page,
  key: string,
  mp4: Buffer | null,
  startMs: number,
  endMs: number,
  timeoutMs: number,
): Promise<AudioWindow> {
  if (endMs <= startMs) {
    throw new MediaError("args", `音频窗口不合法：${startMs}–${endMs} 毫秒`);
  }

  const call = async (data: string | null) =>
    withTimeout(
      page.evaluate(
        async (arg: { key: string; data: string | null; startMs: number; endMs: number; rate: number }) => {
          interface AudioCacheHolder {
            __zipperVideoAudio?: Map<string, AudioBuffer>;
          }

          const holder = window as unknown as AudioCacheHolder;
          const cache = holder.__zipperVideoAudio ?? new Map<string, AudioBuffer>();
          holder.__zipperVideoAudio = cache;

          let buffer = cache.get(arg.key);
          if (!buffer) {
            if (!arg.data) {
              return { needData: true as const };
            }

            const binary = atob(arg.data);
            const bytes = new Uint8Array(binary.length);
            for (let i = 0; i < binary.length; i += 1) {
              bytes[i] = binary.charCodeAt(i);
            }

            const context = new AudioContext();
            try {
              buffer = await context.decodeAudioData(bytes.buffer as ArrayBuffer);
            } finally {
              // AudioBuffer 解出来就独立于 ctx 了；不关会把音频设备句柄一直占着
              await context.close().catch(() => undefined);
            }

            cache.set(arg.key, buffer);

            // 页面内的解码缓存**必须有界**：decodeAudioData 解出的是整条音轨的 Float32
            // （5 分钟 48 kHz 立体声 ≈115 MB），只增不删会把这个标签页一路涨到 OOM，
            // 然后浏览器被重建 —— 连入站 WS 一起断（Node 那边的 LRU 管不到页面内存）。
            // 留 1 条就够：同一条视频的十几个窗口是连着来的。
            while (cache.size > MAX_PAGE_AUDIO_CACHE) {
              const oldest = cache.keys().next();
              if (oldest.done) {
                break;
              }

              cache.delete(oldest.value);
            }
          }

          const source = buffer;
          const sourceRate = source.sampleRate;
          const durationMs = Math.round(source.duration * 1000);
          const fromMs = Math.max(0, Math.min(arg.startMs, durationMs));
          const toMs = Math.max(fromMs, Math.min(arg.endMs, durationMs));
          const fromSample = Math.floor((fromMs * sourceRate) / 1000);
          const toSample = Math.min(source.length, Math.ceil((toMs * sourceRate) / 1000));
          const count = Math.max(0, Math.floor(((toSample - fromSample) * arg.rate) / sourceRate));

          const channels: Float32Array[] = [];
          for (let c = 0; c < source.numberOfChannels; c += 1) {
            channels.push(source.getChannelData(c));
          }

          const dataBytes = count * 2;
          const wav = new Uint8Array(44 + dataBytes);
          const view = new DataView(wav.buffer);

          const writeAscii = (offset: number, text: string): void => {
            for (let i = 0; i < text.length; i += 1) {
              wav[offset + i] = text.charCodeAt(i);
            }
          };

          writeAscii(0, "RIFF");
          view.setUint32(4, 36 + dataBytes, true);
          writeAscii(8, "WAVE");
          writeAscii(12, "fmt ");
          view.setUint32(16, 16, true);
          view.setUint16(20, 1, true);
          view.setUint16(22, 1, true);
          view.setUint32(24, arg.rate, true);
          view.setUint32(28, arg.rate * 2, true);
          view.setUint16(32, 2, true);
          view.setUint16(34, 16, true);
          writeAscii(36, "data");
          view.setUint32(40, dataBytes, true);

          for (let i = 0; i < count; i += 1) {
            const position = fromSample + (i * sourceRate) / arg.rate;
            const low = Math.floor(position);
            const high = Math.min(toSample - 1, low + 1);
            const fraction = position - low;

            let sum = 0;
            for (const channel of channels) {
              const a = channel[low] ?? 0;
              const b = channel[high] ?? 0;
              sum += a + (b - a) * fraction;
            }

            const mixed = channels.length > 0 ? sum / channels.length : 0;
            view.setInt16(44 + i * 2, Math.round(Math.max(-1, Math.min(1, mixed)) * 32767), true);
          }

          // 分段 btoa：一次性 String.fromCharCode(...大数组) 会爆调用栈
          let body = "";
          const chunk = 0x8000;
          for (let i = 0; i < wav.length; i += chunk) {
            body += String.fromCharCode(...wav.subarray(i, i + chunk));
          }

          return {
            needData: false as const,
            wavBase64: btoa(body),
            bytes: wav.length,
            sampleRate: arg.rate,
            startMs: fromMs,
            endMs: toMs,
            durationMs,
          };
        },
        { key, data, startMs, endMs, rate: TARGET_SAMPLE_RATE },
      ),
      timeoutMs,
      `抽音超时（${timeoutMs} 毫秒）`,
    );

  // 先**不带**视频本体：页面里通常已经有解码缓存（同一条视频的十几个窗口是连着要的）。
  // 把 64 MiB 视频每窗都 base64 一遍是 85 MB/次 × 十几窗 —— 纯浪费 CDP 带宽与内存，
  // 还会把整轮转写顶超时。只有缓存真的没了（reload / 页面重建）才补一次数据。
  let result = await call(null);

  // 页面缓存没了（reload / 重建 page）：这次把 mp4 带过去重新解码
  if (result.needData) {
    if (!mp4) {
      throw new MediaError("page", "页面里没有这个视频的解码缓存，且这次没带视频本体，请重试一次整段转写。");
    }

    result = await call(mp4.toString("base64"));
    if (result.needData) {
      throw new MediaError("decode", "页面拒绝解码这个视频的音频（可能只有画面没有音轨）。");
    }
  }

  return {
    wavBase64: result.wavBase64,
    bytes: result.bytes,
    sampleRate: result.sampleRate,
    startMs: result.startMs,
    endMs: result.endMs,
    durationMs: result.durationMs,
  };
}

// ═══════════════════════════ ⑤ 小工具 ═══════════════════════════

/** 给 Promise 套一个上界：页面卡死时不能让请求一直挂着。 */
export async function withTimeout<T>(promise: Promise<T>, timeoutMs: number, message: string): Promise<T> {
  let timer: NodeJS.Timeout | undefined;
  try {
    return await Promise.race([
      promise,
      new Promise<never>((_, reject) => {
        timer = setTimeout(() => reject(new MediaError("page", message)), Math.max(1, timeoutMs));
      }),
    ]);
  } finally {
    if (timer) {
      clearTimeout(timer);
    }
  }
}

function tryParseJson(text: string): unknown {
  try {
    return JSON.parse(text);
  } catch {
    return null;
  }
}

function snippet(text: string, max = 120): string {
  const flat = text.replace(/\s+/g, " ").trim();
  return flat.length <= max ? flat : `${flat.slice(0, max)}…`;
}

function describe(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function hostOf(url: string): string {
  try {
    return new URL(url).host;
  } catch {
    return "无法解析的地址";
  }
}

function formatSize(bytes: number): string {
  if (bytes >= 1024 * 1024) {
    return `${(bytes / 1024 / 1024).toFixed(1)} MiB`;
  }

  return `${Math.round(bytes / 1024)} KiB`;
}

/**
 * 视频本体的内存缓存（按 `itemId`）。
 *
 * 为什么要有：一条 5 分钟的视频抽音要 15 个窗口，每个窗口都重新下载 2.5 MB 是浪费；
 * 而缓存又不能无限（一条 60 MB 的视频留着不放会把内存吃光）——所以 LRU 2 条 + 10 分钟过期。
 */
export class VideoMediaCache {
  private readonly entries = new Map<string, { mp4: Buffer; atMs: number }>();

  public constructor(
    private readonly maxEntries = 2,
    private readonly ttlMs = 10 * 60 * 1000,
    private readonly now: () => number = Date.now,
  ) {}

  public get(itemId: string): Buffer | null {
    const entry = this.entries.get(itemId);
    if (!entry) {
      return null;
    }

    if (this.now() - entry.atMs > this.ttlMs) {
      this.entries.delete(itemId);
      return null;
    }

    // 读一次就算"最近用过"：抽音要连着要十几个窗口，不刷新的话第一个窗口之后就把它挤掉了。
    // 注意**不刷新 atMs** —— 过期时间从第一次缓存那刻算，否则一直被用的视频永远不清。
    this.entries.delete(itemId);
    this.entries.set(itemId, entry);
    return entry.mp4;
  }

  public set(itemId: string, mp4: Buffer): void {
    this.entries.delete(itemId);
    this.entries.set(itemId, { mp4, atMs: this.now() });

    while (this.entries.size > Math.max(1, this.maxEntries)) {
      const oldest = this.entries.keys().next();
      if (oldest.done) {
        break;
      }

      this.entries.delete(oldest.value);
    }
  }

  public clear(): void {
    this.entries.clear();
  }
}
