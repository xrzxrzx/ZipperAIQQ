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
        Vm = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
    }

    public MainViewModel Vm { get; }

    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        Vm.Token = ((PasswordBox)sender).Password;
    }
}
