# DeepSeek 网页搜索 / 抓取 · 实测记录

> 日期：2026-10-25 · 用途：问题记录 02 #1（网页搜索 404）
> 凭证：复用 `ZIPPER_LLM_APIKEY`（**不记录明文**）

## 1. Search

```http
POST https://api.deepseek.com/v1/tools/search
Authorization: Bearer <key>
Content-Type: application/json

{"query":"DeepSeek 最新消息","count":2,"need_content":false}
```

**响应：HTTP 200**（`len≈2740`）

```json
{
  "results": [
    {
      "title": "周鸿祎谈DeepSeek开源：AI时代需要“主权AI”",
      "site_name": "新浪财经",
      "site_icon": "https://cdn.deepseek.com/site-icons/sina.com.cn",
      "authority": 4,
      "rerank_score": 2.5887180634490083,
      "date": "2026-10-22T16:00:00+00:00",
      "url": "https://finance.sina.com.cn/...",
      "summary": "# 周鸿祎谈...（Markdown 摘要）",
      "snippet": "周鸿祎谈DeepSeek开源...",
      "content": ""
    }
  ]
}
```

结论：

- 顶层是 `results` 数组（不是 `data.results`）。
- 每条可用字段：`title` / `url` / `snippet` / `summary`（Markdown）/ `content`（`need_content=false` 时为空）。
- 参数名确实是 **`count`**，不是 `top_k`。
- 旧实现请求 `/search` → 404，与线上日志一致。

## 2. Fetch

```http
POST https://api.deepseek.com/v1/tools/fetch
Authorization: Bearer <key>
Content-Type: application/json

{"url":"https://example.com"}
```

**响应：HTTP 200**（`len≈996`）

```json
{"content":"该域名仅用于文档示例...（Markdown 正文）"}
```

结论：

- 返回 `content`（Markdown），可能没有 `title`。
- 官方负责反爬与正文提取；失败时再回落自建 GET + 正则剥标签。

## 3. 计费口径

官方：每 **1000 次成功请求 5 元**；仅 HTTP 200 且产生结果才计费。
→ `fetch_url` 采用「官方优先 + 自建回落」，避免全量走付费接口。

## 4. 实现映射

| 项 | 值 |
|---|---|
| Search 路径 | `/v1/tools/search` |
| Search body | `{query, count, need_content:false}` |
| Fetch 路径 | `/v1/tools/fetch` |
| Fetch body | `{url}` |
| 结果解析 | `results[]` → `title`/`url`/`snippet`（缺失回退 `summary`） |
| 失败 | 非 2xx → `WebSearchException(status)`；空 200 才是「没搜到」 |
