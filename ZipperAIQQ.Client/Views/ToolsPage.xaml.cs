using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>工具管理页：进入即加载（#28）。</summary>
public sealed partial class ToolsPage : Page
{
    public ToolsPage()
    {
        Vm = App.Services.GetRequiredService<ToolsViewModel>();
        InitializeComponent();

        _ = Vm.LoadCommand.ExecuteAsync(null);
    }

    public ToolsViewModel Vm { get; }
}
