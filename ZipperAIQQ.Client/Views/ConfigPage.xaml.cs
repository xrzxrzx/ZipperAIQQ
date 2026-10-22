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

        // 进页面就把「运行参数」和「行为设置」拉一遍，别让用户对着空白点读取
        _ = Vm.LoadSettingsCommand.ExecuteAsync(null);
        _ = Vm.LoadBehaviorCommand.ExecuteAsync(null);
    }

    public MainViewModel Vm { get; }

    private void JudgeKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        Vm.JudgeEndpointKey = ((PasswordBox)sender).Password;
    }

    private void KeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        Vm.ConfigKey = ((PasswordBox)sender).Password;
    }
}
