using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>人员与名单页：进入即自动加载（#10）。</summary>
public sealed partial class AccessPage : Page
{
    public AccessPage()
    {
        Vm = App.Services.GetRequiredService<AccessViewModel>();
        InitializeComponent();

        _ = Vm.LoadCommand.ExecuteAsync(null);
    }

    public AccessViewModel Vm { get; }

    /// <summary>保存名单；若服务端要求确认「拉黑管理员」，弹窗确认后带 confirm 重发（#23）。</summary>
    private async void SaveLists_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var ok = await Vm.SaveListsAsync();
            if (ok || !Vm.NeedsAdminConfirm)
            {
                return;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "确认拉黑管理员？",
                Content = Vm.ConfirmMessage,
                PrimaryButtonText = "确认（剥夺管理员并拉黑）",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
            };

            DialogTheme.Apply(dialog);

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await Vm.SaveListsAsync(confirmDemote: true);
            }
        }
        catch (Exception ex)
        {
            // async void 里漏出去 = 进程崩；这里兜住并留痕
            System.Diagnostics.Debug.WriteLine($"保存人员与名单失败：{ex}");
        }
    }
}
