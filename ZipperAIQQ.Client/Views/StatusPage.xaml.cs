using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>状态页：连接 + 运行信息。</summary>
public sealed partial class StatusPage : Page
{
    public StatusPage()
    {
        // x:Bind 需要，必须在 InitializeComponent 之前赋值
        Vm = App.Services.GetRequiredService<StatusViewModel>();
        InitializeComponent();
    }

    public StatusViewModel Vm { get; }

    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        Vm.Token = ((PasswordBox)sender).Password;
    }

    /// <summary>重启服务端（#36）：二次确认后发命令；真正重新拉起靠外部守护脚本。</summary>
    private async void Restart_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "重启服务端？",
            Content = "当前对话会中断约 2 秒（由守护脚本拉起）。没有运行守护脚本时需要手动启动。",
            PrimaryButtonText = "重启",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await Vm.RestartServerCommand.ExecuteAsync(null);
        }
    }
}

