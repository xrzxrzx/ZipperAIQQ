using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ZipperAIQQ.Client.Views;

/// <summary>
/// 对话框主题的**唯一入口**：所有 <see cref="ContentDialog"/> 建完就调 <see cref="Apply"/>。
/// </summary>
/// <remarks>
/// <para>
/// 以前每个页各写一段 <c>Application.Current.Resources.TryGetValue("JpAccentButtonStyle"…)</c>，
/// 结果"有的页设了、有的页没设"—— 外部接口页的编辑框就退回系统默认深灰（用户实测反馈）。
/// 现在样式全部由 <c>JpDialogStyle</c> 提供（含三个按钮样式），页面只需要调一次。
/// </para>
/// <para>
/// 主题要设两处：<see cref="ContentDialog.RequestedTheme"/> 管对话框外壳，
/// 内容根还要再来一次 —— 只设 dialog 的话，输入框在焦点 / 悬停时仍去取应用默认主题，
/// 表现就是「深色弹窗里输入框变白底」。
/// </para>
/// </remarks>
public static class DialogTheme
{
    /// <summary>App.xaml 里的对话框样式键（不透明底 + 圆角 12 都在里面）。</summary>
    public const string StyleKey = "JpDialogStyle";

    /// <summary>主按钮样式键（粉色实心）。</summary>
    public const string AccentButtonStyleKey = "JpAccentButtonStyle";

    /// <summary>次按钮 / 关闭按钮样式键（近黑底 + 粉字）。</summary>
    public const string NormalButtonStyleKey = "JpButtonStyle";

    /// <summary>对话框按钮统一的圆角（主/次/关闭三者一致，用户反复要求过）。</summary>
    public const double ButtonCornerRadius = 10;

    /// <summary>
    /// 给对话框套上日系主题：Dark + <see cref="StyleKey"/>，
    /// 顺手把内容根也设成 Dark（内容还没设的话，稍后自己调 <see cref="ApplyToContent"/>）。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>三个按钮样式必须在这里再显式赋一遍</b>（2026-10-28 实测结论）：只写在
    /// <c>JpDialogStyle</c> 的 Setter 里时，主按钮实测仍按系统强调色渲染
    /// （截图逐像素采样得到 <c>#F98FB2</c> = <c>SystemAccentColorLight2</c>），
    /// 说明 ContentDialog 应用模板时没有把 Style 里的 <c>PrimaryButtonStyle</c> 带下去。
    /// 直接赋属性是确定的 —— 属性值优先于样式 Setter。
    /// </para>
    /// <para>
    /// 另外把 <c>ControlCornerRadius</c> 限定在对话框子树内为 10：万一某个按钮最终还是落到
    /// 系统默认模板（圆角取 <c>ControlCornerRadius</c>），圆角也和我们的模板一致，不会再出现
    /// 「保存 10 / 关闭 4」这种不一致。
    /// </para>
    /// </remarks>
    public static void Apply(ContentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        // 先定主题再套样式：ThemeResource 才会按 Dark 那套颜色解析
        dialog.RequestedTheme = ElementTheme.Dark;

        if (Application.Current.Resources.TryGetValue(StyleKey, out var style) && style is Style dialogStyle)
        {
            dialog.Style = dialogStyle;
        }

        // 直接赋值（覆盖 Style Setter 与模板默认值），三个按钮一起对齐
        if (Application.Current.Resources.TryGetValue(AccentButtonStyleKey, out var accent) && accent is Style accentStyle)
        {
            dialog.PrimaryButtonStyle = accentStyle;
        }

        if (Application.Current.Resources.TryGetValue(NormalButtonStyleKey, out var normal) && normal is Style normalStyle)
        {
            dialog.SecondaryButtonStyle = normalStyle;
            dialog.CloseButtonStyle = normalStyle;
        }

        // 兜底：任何落在系统默认模板上的按钮，圆角也按我们的口径
        dialog.Resources["ControlCornerRadius"] = new CornerRadius(ButtonCornerRadius);

        if (dialog.Content is FrameworkElement root)
        {
            ApplyToContent(root);
        }
    }

    /// <summary>内容根也要 Dark（修「输入框白底」）；Content 是字符串时不需要。</summary>
    public static void ApplyToContent(FrameworkElement root)
    {
        ArgumentNullException.ThrowIfNull(root);
        root.RequestedTheme = ElementTheme.Dark;
    }

    /// <summary>
    /// 建一个已经套好主题、内容也上好 Dark 的 <see cref="StackPanel"/>（对话框里那种表单容器）。
    /// 用它拼内容就不用每页重复写 <c>RequestedTheme = ElementTheme.Dark</c>。
    /// </summary>
    public static StackPanel Panel(double minWidth = 380, double spacing = 10)
    {
        var panel = new StackPanel
        {
            Spacing = spacing,
            MinWidth = minWidth,
            RequestedTheme = ElementTheme.Dark,
        };

        return panel;
    }
}
