using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>端点与参数页：进入即自动加载（#10），保存结果就地显示（#27）。</summary>
public sealed partial class EndpointsPage : Page
{
    public EndpointsPage()
    {
        Vm = App.Services.GetRequiredService<EndpointsViewModel>();
        InitializeComponent();

        _ = Vm.LoadCommand.ExecuteAsync(null);
    }

    public EndpointsViewModel Vm { get; }

    private void EndpointKey_PasswordChanged(object sender, RoutedEventArgs e)
        => Vm.EndpointKey = ((PasswordBox)sender).Password;

    private void JudgeKey_PasswordChanged(object sender, RoutedEventArgs e)
        => Vm.JudgeKey = ((PasswordBox)sender).Password;
}
