using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ZipperAIQQ.Client.Services;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>
/// 第三方平台接入配置页（S3-F）：Pivot 两页（邮件 / 抖音）+ 顶部平台总览。
/// </summary>
/// <remarks>
/// 交互照 <see cref="ExternalPage"/>：卡片网格 + 独立编辑对话框（对话框里校验、就地保存）。
/// 邮件账号的增删改**各自立即落库**（它们本来就是一条条独立记录）；
/// 抖音是一整块配置，所以只有那块有「保存」按钮。
/// </remarks>
public sealed partial class PlatformsPage : Page
{
    public PlatformsPage()
    {
        // 与其它页一致：VM 走 App 的单例注册（App.xaml.cs），页面只管取
        Vm = App.Services.GetRequiredService<PlatformsViewModel>();
        InitializeComponent();

        _ = Vm.LoadCommand.ExecuteAsync(null);
    }

    public PlatformsViewModel Vm { get; }

    // ── 邮件：新建 / 编辑 / 删除 / 立即同步 ─────────────────────────

    private void AddMail_Click(object sender, RoutedEventArgs e) => _ = ShowMailDialogAsync(null);

    private void EditMail_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is MailAccountItem item)
        {
            _ = ShowMailDialogAsync(item);
        }
    }

    private async void RemoveMail_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.DataContext is not MailAccountItem item)
            {
                return;
            }

            var confirm = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "删除这个账号？",
                Content = $"会从服务端移除 {item.Address} 的配置与授权码，之后不再收信。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            StyleDialog(confirm);

            if (await confirm.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await Vm.DeleteMailAccountAsync(item);
        }
        catch (Exception ex)
        {
            Vm.MailStatus = $"删除失败：{ex.Message}";
        }
    }

    private async void SyncMail_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if ((sender as FrameworkElement)?.DataContext is MailAccountItem item)
            {
                await Vm.SyncMailAsync(item);
            }
        }
        catch (Exception ex)
        {
            Vm.MailStatus = $"同步失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 卡片上的启用开关。**IsOn 只做 OneWay**：由用户拨动时 IsOn 与数据不一致才发请求，
    /// 这样"绑定回填"和"失败后还原"都不会被当成用户操作再打一次接口。
    /// </summary>
    private void MailEnabled_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || toggle.DataContext is not MailAccountItem item)
        {
            return;
        }

        if (toggle.IsOn == item.Enabled)
        {
            return;
        }

        _ = Vm.SetMailEnabledAsync(item, toggle.IsOn);
    }

    /// <summary>
    /// 邮件通道总开关（<c>saveMail</c>）。IsOn 同样只做 OneWay：
    /// 只有用户拨到与真值不同才发请求 —— 绑定回填和失败还原都不会再打一次接口。
    /// </summary>
    private void MailChannel_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || !Vm.MailSwitchAvailable)
        {
            return;
        }

        if (toggle.IsOn == Vm.MailEnabled)
        {
            return;
        }

        _ = Vm.SetMailChannelEnabledAsync(toggle.IsOn);
    }

    /// <summary>新建 / 编辑对话框：校验在按钮回调里，保存也在这里（失败就把对话框留着，输入不丢）。</summary>
    private async Task ShowMailDialogAsync(MailAccountItem? existing)
    {
        var address = new TextBox
        {
            Header = "邮箱地址",
            Text = existing?.Address ?? string.Empty,
            PlaceholderText = "someone@qq.com",
            MinWidth = 360,
        };

        var imapHost = new TextBox
        {
            Header = "IMAP 主机",
            Text = existing?.ImapHost ?? string.Empty,
            PlaceholderText = "imap.qq.com",
        };

        var imapPort = new NumberBox
        {
            Header = "端口",
            Value = existing?.ImapPort ?? 993,
            Minimum = 1,
            Maximum = 65535,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };

        var useSsl = new ToggleSwitch { Header = "使用 SSL", IsOn = existing?.UseSsl ?? true };

        var username = new TextBox
        {
            Header = "用户名（留空 = 同邮箱地址）",
            Text = existing?.Username ?? string.Empty,
        };

        var secret = new PasswordBox
        {
            Header = "IMAP 授权码",
            PlaceholderText = existing is null
                ? "QQ / 163 要用授权码（不是登录密码）"
                : "留空 = 不改（当前密文保留）",
            PasswordRevealMode = PasswordRevealMode.Hidden,
        };

        // 凭据永远不回显：这里只说"有没有"
        var secretState = new TextBlock
        {
            Text = existing is null
                ? "还没有授权码"
                : existing.HasSecret ? "授权码：已配置（不回显；要换就重填）" : "授权码：未配置",
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        };

        var enabled = new ToggleSwitch { Header = "启用", IsOn = existing?.Enabled ?? true };

        var error = new TextBlock
        {
            Foreground = new SolidColorBrush(Colors.OrangeRed),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var hostRow = new Grid { ColumnSpacing = 10 };
        hostRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        hostRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hostRow.Children.Add(imapHost);
        Grid.SetColumn(imapHost, 0);
        hostRow.Children.Add(imapPort);
        Grid.SetColumn(imapPort, 1);

        var panel = new StackPanel { Spacing = 10, MinWidth = 380 };
        panel.Children.Add(address);
        panel.Children.Add(hostRow);
        panel.Children.Add(useSsl);
        panel.Children.Add(username);
        panel.Children.Add(secret);
        panel.Children.Add(secretState);
        panel.Children.Add(enabled);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = existing is null ? "新建收信账号" : $"编辑收信账号 · {existing.Address}",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
        };

        StyleDialog(dialog);

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            try
            {
                var addressText = address.Text.Trim();
                if (addressText.Length == 0 || !addressText.Contains('@') || addressText.Any(char.IsWhiteSpace))
                {
                    ShowDialogError(error, "邮箱地址不合法：要像 someone@qq.com，且不能有空格。");
                    args.Cancel = true;
                    return;
                }

                var hostText = imapHost.Text.Trim();
                if (hostText.Length == 0 || hostText.Any(char.IsWhiteSpace))
                {
                    ShowDialogError(error, "IMAP 主机不能为空，也不能有空格（如 imap.qq.com）。");
                    args.Cancel = true;
                    return;
                }

                if (double.IsNaN(imapPort.Value) || imapPort.Value is < 1 or > 65535)
                {
                    ShowDialogError(error, "端口必须在 1~65535（IMAP 通常是 993）。");
                    args.Cancel = true;
                    return;
                }

                // 网络请求要 await，用 deferral 把对话框留到结果回来再决定关不关
                var deferral = args.GetDeferral();
                try
                {
                    var (ok, message) = await Vm.SaveMailAccountAsync(
                        existing?.Id,
                        addressText,
                        existing?.DisplayName ?? string.Empty,
                        hostText,
                        (int)imapPort.Value,
                        useSsl.IsOn,
                        username.Text.Trim(),
                        secret.Password,
                        enabled.IsOn);

                    if (ok)
                    {
                        return;
                    }

                    ShowDialogError(error, message.Length == 0 ? "保存失败。" : $"保存失败：{message}");
                    args.Cancel = true;
                }
                catch (Exception ex)
                {
                    ShowDialogError(error, $"保存失败：{ex.Message}");
                    args.Cancel = true;
                }
                finally
                {
                    deferral.Complete();
                }
            }
            catch (Exception ex)
            {
                // 上面那层兜的是同步校验段；万一还有漏网的，也别让对话框静默吞掉
                ShowDialogError(error, $"保存失败：{ex.Message}");
                args.Cancel = true;
            }
        };

        await dialog.ShowAsync();
    }

    private static void ShowDialogError(TextBlock error, string message)
    {
        error.Text = message;
        error.Visibility = Visibility.Visible;
    }

    /// <summary>对话框跟页面同一套日系主题与按钮样式（外部接口页也是这么做的）。</summary>
    private static void StyleDialog(ContentDialog dialog)
    {
        dialog.RequestedTheme = ElementTheme.Dark;

        if (Application.Current.Resources.TryGetValue("JpAccentButtonStyle", out var accent) &&
            accent is Style accentStyle)
        {
            dialog.PrimaryButtonStyle = accentStyle;
        }

        if (Application.Current.Resources.TryGetValue("JpButtonStyle", out var normal) &&
            normal is Style normalStyle)
        {
            dialog.CloseButtonStyle = normalStyle;
        }
    }
}
