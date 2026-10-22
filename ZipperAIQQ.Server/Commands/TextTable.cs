using System.Text;

namespace ZipperAIQQ.Server.Commands;

/// <summary>
/// 把数据渲染成**等宽文本表格**（框线 + 宽度对齐）。
/// </summary>
/// <remarks>
/// <para>
/// 为什么不用 Markdown：QQ 的消息是**纯文本**，不渲染 Markdown ——
/// 发出去的 <c>| 指标 | 值 |</c> 只会原样显示成一串竖线（实测被用户指出）。
/// </para>
/// <para>
/// 宽度按**显示宽度**算：CJK / 全角字符占 2 格，其余占 1 格，
/// 这样中文列和英文列才能在同一列宽里对齐。
/// </para>
/// </remarks>
public static class TextTable
{
    public static string Render(
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);

        if (headers.Count == 0)
        {
            return string.Empty;
        }

        var widths = new int[headers.Count];
        for (var i = 0; i < headers.Count; i++)
        {
            widths[i] = DisplayWidth(headers[i]);
        }

        foreach (var row in rows)
        {
            for (var i = 0; i < headers.Count && i < row.Count; i++)
            {
                widths[i] = Math.Max(widths[i], DisplayWidth(row[i]));
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine(Border(widths, '┌', '┬', '┐'));
        sb.AppendLine(Row(headers, widths));
        sb.AppendLine(Border(widths, '├', '┼', '┤'));

        foreach (var row in rows)
        {
            sb.AppendLine(Row(row, widths));
        }

        sb.Append(Border(widths, '└', '┴', '┘'));
        return sb.ToString();
    }

    /// <summary>显示宽度：CJK 与全角符号按 2 格算。</summary>
    private static int DisplayWidth(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var width = 0;
        foreach (var ch in text)
        {
            width += IsWide(ch) ? 2 : 1;
        }

        return width;
    }

    private static bool IsWide(char ch) =>
        ch is >= '\u1100' and <= '\u115F'      // 谚文字母
        or >= '\u2E80' and <= '\uA4CF'          // CJK 部首、汉字、假名
        or >= '\uAC00' and <= '\uD7A3'          // 谚文音节
        or >= '\uF900' and <= '\uFAFF'          // CJK 兼容汉字
        or >= '\uFE30' and <= '\uFE6F'          // CJK 兼容形式
        or >= '\uFF00' and <= '\uFF60'          // 全角 ASCII
        or >= '\uFFE0' and <= '\uFFE6';         // 全角符号

    private static string Row(IReadOnlyList<string> cells, int[] widths)
    {
        var sb = new StringBuilder("│");
        for (var i = 0; i < widths.Length; i++)
        {
            var text = i < cells.Count ? cells[i] : string.Empty;
            var padding = widths[i] - DisplayWidth(text);
            sb.Append(' ').Append(text).Append(' ', padding + 1).Append('│');
        }

        return sb.ToString();
    }

    private static string Border(int[] widths, char left, char middle, char right)
    {
        var sb = new StringBuilder();
        sb.Append(left);
        for (var i = 0; i < widths.Length; i++)
        {
            sb.Append(new string('─', widths[i] + 2));
            sb.Append(i == widths.Length - 1 ? right : middle);
        }

        return sb.ToString();
    }
}
