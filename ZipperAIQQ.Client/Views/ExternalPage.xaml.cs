using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>外部接口（WS）配置页。</summary>
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
        Vm.AddReverse();
    }

    private void RemoveReverse_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ReverseServerItem item)
        {
            Vm.RemoveReverse(item);
        }
    }
}
