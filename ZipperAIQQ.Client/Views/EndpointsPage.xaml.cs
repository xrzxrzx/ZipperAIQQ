using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>模型配置页：普通 LLM 与 JEV 两个固定块。</summary>
public sealed partial class EndpointsPage : Page
{
    public EndpointsPage()
    {
        Vm = App.Services.GetRequiredService<EndpointsViewModel>();
        InitializeComponent();

        _ = Vm.LoadCommand.ExecuteAsync(null);
    }

    public EndpointsViewModel Vm { get; }

    private void LlmKey_PasswordChanged(object sender, RoutedEventArgs e)
        => Vm.LlmKey = ((PasswordBox)sender).Password;

    private void JudgeKey_PasswordChanged(object sender, RoutedEventArgs e)
        => Vm.JudgeKey = ((PasswordBox)sender).Password;

    private void SearchKey_PasswordChanged(object sender, RoutedEventArgs e)
        => Vm.SearchApiKey = ((PasswordBox)sender).Password;
}
