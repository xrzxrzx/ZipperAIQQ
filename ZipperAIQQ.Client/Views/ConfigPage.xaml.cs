using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>配置页：查看 + 编辑模型端点（key 只写不回显）。</summary>
public sealed partial class ConfigPage : Page
{
    public ConfigPage()
    {
        Vm = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
    }

    public MainViewModel Vm { get; }

    private void KeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        Vm.ConfigKey = ((PasswordBox)sender).Password;
    }
}
