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

        // 切页 / 页面卸载就把登录轮询停掉，别在后台一直打 getDouyinLogin
        Unloaded += (_, _) => Vm.StopDouyinLoginWatch();
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

            DialogTheme.Apply(confirm);

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

    /// <summary>
    /// 「汇总再经人格润色」开关（<c>saveMail { personaDigest }</c>）。
    /// 与「启用邮件接入」同一套：OneWay + 事件，绑定回填 / 失败还原都不会误触发。
    /// </summary>
    private void MailPersonaDigest_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle || !Vm.MailSwitchAvailable)
        {
            return;
        }

        if (toggle.IsOn == Vm.MailPersonaDigest)
        {
            return;
        }

        _ = Vm.SetMailPersonaDigestAsync(toggle.IsOn);
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

        // 显示名（可选）：以前漏了这个字段，用户看到卡片副标题是「未命名账号」以为是 bug
        var displayName = new TextBox
        {
            Header = "显示名（可选）",
            Text = existing?.DisplayName ?? string.Empty,
            PlaceholderText = "给人看的名字，比如「工作邮箱」；留空 = 不显示这一行",
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

        // 内容根的主题由 DialogTheme.Panel() 带上（只设在 dialog 上时输入框会白底）
        var panel = DialogTheme.Panel();
        panel.Children.Add(address);
        panel.Children.Add(displayName);
        panel.Children.Add(hostRow);
        panel.Children.Add(useSsl);
        panel.Children.Add(username);
        panel.Children.Add(secret);

        // 凭据永远不回显。**新建时不显示这行**：原来写的是"还没有授权码"，
        // 看起来像可点的链接、点了却没有任何反应 —— 没实现的东西就不该写（用户反馈）。
        if (existing is not null)
        {
            panel.Children.Add(new TextBlock
            {
                Text = existing.HasSecret ? "授权码：已配置（不回显；要换就重填）" : "授权码：未配置",
                FontSize = 12,
                Opacity = 0.75,
                TextWrapping = TextWrapping.Wrap,
            });
        }

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

        DialogTheme.Apply(dialog);

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
                        displayName.Text.Trim(),   // 空串照发：允许把显示名清空
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
}
