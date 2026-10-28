using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>外部接口（WS）配置页：Pivot 分页 + 自动换行卡片 + 独立编辑框。</summary>
public sealed partial class ExternalPage : Page
{
    public ExternalPage()
    {
        Vm = App.Services.GetRequiredService<ExternalViewModel>();
        InitializeComponent();
        _ = Vm.LoadCommand.ExecuteAsync(null);
    }

    public ExternalViewModel Vm { get; }

    private void AddReverse_Click(object sender, RoutedEventArgs e)
    {
        var item = Vm.AddReverse();
        _ = ShowEditDialogAsync(item);
    }

    private void AddForward_Click(object sender, RoutedEventArgs e)
    {
        var item = Vm.AddForward();
        _ = ShowEditDialogAsync(item);
    }

    private void EditServer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ExternalServerItem item)
        {
            _ = ShowEditDialogAsync(item);
        }
    }

    private void RemoveServer_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ExternalServerItem item)
        {
            Vm.Remove(item);
        }
    }

    private async Task ShowEditDialogAsync(ExternalServerItem item)
    {
        var name = new TextBox { Header = "名称", Text = item.Name, MinWidth = 360 };
        var enabled = new ToggleSwitch { Header = "启用", IsOn = item.Enabled };
        var host = new TextBox { Header = "Host", Text = item.Host };
        var port = new NumberBox
        {
            Header = "Port",
            Value = item.Port,
            Minimum = 1,
            Maximum = 65535,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
        };
        var url = new TextBox { Header = "WS 地址", Text = item.Url, PlaceholderText = "ws://host:port" };
        var token = new TextBox
        {
            Header = "Token",
            Text = string.Empty,
            PlaceholderText = item.HasToken ? "已配置 · 留空不修改" : "未配置 · 留空 = 不鉴权",
        };
        var clearToken = new CheckBox
        {
            Content = "清除已保存的 Token",
            IsChecked = false,
            Visibility = item.HasToken ? Visibility.Visible : Visibility.Collapsed,
        };
        var error = new TextBlock
        {
            Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.OrangeRed),
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
        };

        var panel = DialogTheme.Panel();
        panel.Children.Add(enabled);
        panel.Children.Add(name);

        if (item.IsReverse)
        {
            var hostRow = new Grid { ColumnSpacing = 10 };
            hostRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            hostRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            hostRow.Children.Add(host);
            Grid.SetColumn(host, 0);
            hostRow.Children.Add(port);
            Grid.SetColumn(port, 1);
            panel.Children.Add(hostRow);
        }
        else
        {
            panel.Children.Add(url);
        }

        panel.Children.Add(token);
        panel.Children.Add(clearToken);
        panel.Children.Add(error);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = item.IsReverse ? "Websocket Server" : "Websocket Client",
            Content = panel,
            PrimaryButtonText = "保存",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Primary,
        };

        // 主题统一走 DialogTheme：用户反馈这页的弹窗"又变回默认样式了"（以前这里只有按钮样式，没有对话框样式）
        DialogTheme.Apply(dialog);

        dialog.PrimaryButtonClick += (_, args) =>
        {
            var errorText = ExternalViewModel.ValidateServer(
                item.IsReverse, host.Text.Trim(), port.Value, url.Text.Trim());

            if (errorText is not null)
            {
                error.Text = errorText;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }

            item.Name = string.IsNullOrWhiteSpace(name.Text) ? item.TypeText : name.Text.Trim();
            item.Enabled = enabled.IsOn;
            item.Host = host.Text.Trim();
            item.Port = port.Value;
            item.Url = url.Text.Trim();

            // token 不回显：留空 = 不修改；勾选"清除" = 明确清除；输入 = 更新
            item.ClearToken = clearToken.IsChecked == true;
            item.TokenInput = item.ClearToken ? string.Empty : token.Text.Trim();
        };

        await dialog.ShowAsync();
    }
}
