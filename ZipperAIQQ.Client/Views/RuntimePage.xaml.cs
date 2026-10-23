using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>运行与行为页：进入即自动加载（#10）。</summary>
public sealed partial class RuntimePage : Page
{
    public RuntimePage()
    {
        Vm = App.Services.GetRequiredService<RuntimeViewModel>();
        InitializeComponent();

        _ = Vm.LoadCommand.ExecuteAsync(null);
    }

    public RuntimeViewModel Vm { get; }
}
