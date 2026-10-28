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
    /// <summary>App.xaml 里的对话框样式键（主/次/关闭按钮 + 不透明底 + 圆角 12 都在里面）。</summary>
    public const string StyleKey = "JpDialogStyle";

    /// <summary>
    /// 给对话框套上日系主题：Dark + <see cref="StyleKey"/>，
    /// 顺手把内容根也设成 Dark（内容还没设的话，稍后自己调 <see cref="ApplyToContent"/>）。
    /// </summary>
    public static void Apply(ContentDialog dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        // 先定主题再套样式：ThemeResource 才会按 Dark 那套颜色解析
        dialog.RequestedTheme = ElementTheme.Dark;

        if (Application.Current.Resources.TryGetValue(StyleKey, out var style) && style is Style dialogStyle)
        {
            dialog.Style = dialogStyle;
        }

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
