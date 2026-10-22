using SkiaSharp;

namespace ZipperAIQQ.Server.Commands;

/// <summary>
/// 把统计表渲染成 PNG（/status 用）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么发图片：QQ 是**纯文本 + 比例字体** —— Markdown 不渲染、框线字符对不齐，
/// 手机和 PC 上都会歪。图是唯一能在两端都完美呈现表格的形态。
/// </para>
/// <para>
/// 配色沿用客户端的日系暗色（夜樱紫底 + 樱粉强调），和界面一致。
/// </para>
/// </remarks>
public static class StatusImageRenderer
{
    private const int Padding = 24;
    private const int LineHeight = 30;
    private const int HeaderHeight = 44;

    /// <summary>
    /// 渲染倍率（高 DPI）。
    /// </summary>
    /// <remarks>
    /// QQ 会把图片缩放到气泡宽度显示，1x 渲染（宽 500 多）放大后发糊（实测反馈）。
    /// 画布按 2.5 倍像素创建、坐标仍用逻辑值，等于出一张更清晰的图。
    /// </remarks>
    private const float Scale = 2.5f;

    private static readonly SKColor Background = new(0x2A, 0x16, 0x22);
    private static readonly SKColor CardColor = new(0x33, 0x1E, 0x2E);
    private static readonly SKColor Accent = new(0xFF, 0x8F, 0xBE);
    private static readonly SKColor Text = new(0xF8, 0xEA, 0xEF);
    private static readonly SKColor Subtle = new(0xC4, 0xA6, 0xB8);

    /// <summary>一张表：标题 + 表头 + 数据行。</summary>
    public sealed record Table(string Title, string[] Headers, string[][] Rows);

    public static byte[] Render(string title, string subtitle, IReadOnlyList<Table> tables)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(tables);

        var typeface = ResolveTypeface();
        using var titleFont = new SKFont(typeface, 22, 1);
        using var subFont = new SKFont(typeface, 13, 1);
        using var cellFont = new SKFont(typeface, 14, 1);
        using var headFont = new SKFont(typeface, 14, 1);

        // ── 量尺寸 ──
        var colWidths = tables
            .Select(t => MeasureColumns(t, headFont, cellFont))
            .ToList();

        var tableWidths = colWidths
            .Select(w => w.Sum() + (w.Length - 1) * 16)
            .ToList();

        var contentWidth = Math.Max(
            Measure(title, titleFont) + Padding * 2,
            tableWidths.Max() + Padding * 2 + 20);

        // 最小宽度：QQ 的图片气泡是固定宽的，图太窄会在右边留一大片底色（实测）
        contentWidth = Math.Max(contentWidth, 560);

        // 表格按图片宽度**拉伸铺满**：否则表格只占左边，右边仍是空的
        var targetTableWidth = (int)contentWidth - (Padding * 2) - 20;
        for (var t = 0; t < colWidths.Count; t++)
        {
            var widths = colWidths[t];
            var natural = widths.Sum() + (widths.Length - 1) * 16;
            var extra = targetTableWidth - natural;
            if (extra <= 0)
            {
                continue;
            }

            // 平均分配多余宽度（最后一列吃余数，保证总宽精确）
            var per = extra / widths.Length;
            for (var c = 0; c < widths.Length; c++)
            {
                widths[c] += per;
            }

            widths[^1] += extra - (per * widths.Length);
            tableWidths[t] = widths.Sum() + (widths.Length - 1) * 16;
        }

        var height = Padding + 40 + 26;

        // 每张表：标题行 + 表头 + 行 + 间距
        var tableHeights = tables
            .Select(t => 28 + HeaderHeight + t.Rows.Length * LineHeight + 22)
            .ToList();

        height += tableHeights.Sum() + Padding;

        var width = (int)contentWidth;

        using var surface = SKSurface.Create(
            new SKImageInfo((int)(width * Scale), (int)(height * Scale)));

        var canvas = surface.Canvas;

        // 后续全部按逻辑坐标绘制，实际输出为 Scale 倍像素 → 高 DPI 清晰图
        canvas.Scale(Scale);

        canvas.Clear(Background);

        // 顶部渐变条
        using (var bar = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, 0),
                new SKPoint(width, 0),
                [Accent, new SKColor(0xC9, 0xA0, 0xDC)],
                null,
                SKShaderTileMode.Clamp),
        })
        {
            canvas.DrawRect(0, 0, width, 5, bar);
        }

        var y = Padding + 20f;

        DrawText(canvas, title, Padding, y, titleFont, Accent);
        y += 28;

        DrawText(canvas, subtitle, Padding, y, subFont, Subtle);
        y += 30;

        for (var t = 0; t < tables.Count; t++)
        {
            var table = tables[t];
            var widths = colWidths[t];
            var tableWidth = tableWidths[t];

            DrawText(canvas, table.Title, Padding, y, headFont, Subtle);
            y += 26;

            // 表头
            using (var headBg = new SKPaint { Color = CardColor })
            {
                canvas.DrawRoundRect(new SKRect(Padding, y - 16, Padding + tableWidth, y + 14), 8, 8, headBg);
            }

            var x = Padding + 8f;
            for (var c = 0; c < table.Headers.Length; c++)
            {
                DrawText(canvas, table.Headers[c], x, y, headFont, Accent);
                x += widths[c] + 16;
            }

            y += 26;

            // 分隔线
            using (var line = new SKPaint { Color = new SKColor(0x50, 0xFF, 0x8F, 0xBE), StrokeWidth = 1 })
            {
                canvas.DrawLine(Padding, y - 10, Padding + tableWidth, y - 10, line);
            }

            foreach (var row in table.Rows)
            {
                x = Padding + 8f;
                for (var c = 0; c < row.Length && c < widths.Length; c++)
                {
                    DrawText(canvas, row[c], x, y, cellFont, c == 0 ? Text : Subtle);
                    x += widths[c] + 16;
                }

                y += LineHeight;
            }

            y += 22;
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static int[] MeasureColumns(Table table, SKFont headFont, SKFont cellFont)
    {
        var widths = table.Headers.Select(h => (int)Math.Ceiling(headFont.MeasureText(h)) + 8).ToArray();

        foreach (var row in table.Rows)
        {
            for (var i = 0; i < row.Length && i < widths.Length; i++)
            {
                widths[i] = Math.Max(widths[i], (int)Math.Ceiling(cellFont.MeasureText(row[i])) + 8);
            }
        }

        return widths;
    }

    private static float Measure(string text, SKFont font) => font.MeasureText(text);

    private static void DrawText(SKCanvas canvas, string text, float x, float y, SKFont font, SKColor color)
    {
        using var paint = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);
    }

    /// <summary>挑一个能显示中文的字体；找不到就退回默认（宁可难看，不能崩）。</summary>
    private static SKTypeface ResolveTypeface()
    {
        foreach (var name in new[] { "Microsoft YaHei", "微软雅黑", "Segoe UI", "Noto Sans CJK SC", "DejaVu Sans" })
        {
            var tf = SKTypeface.FromFamilyName(name);
            if (tf is not null && tf.FamilyName is { Length: > 0 } family && family != "DirectWrite")
            {
                return tf;
            }
        }

        return SKTypeface.Default;
    }
}
