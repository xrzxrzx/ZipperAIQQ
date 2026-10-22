using Microsoft.UI.Xaml;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client;

/// <summary>主窗口：四个 Tab（状态 / 会话 / 配置 / 审计）。</summary>
public sealed partial class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);

        // x:Bind 读的就是这个属性，必须在 InitializeComponent 之前赋值
        Vm = vm;
        InitializeComponent();
    }

    public MainViewModel Vm { get; }

    /// <summary>
    /// <see cref="PasswordBox.Password"/> 不是依赖属性，没法 x:Bind TwoWay，
    /// 只能在这里手动同步到 VM。
    /// </summary>
    private void TokenBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        Vm.Token = TokenBox.Password;
    }

    private void ConfigKeyBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        Vm.ConfigKey = ConfigKeyBox.Password;
    }
}
