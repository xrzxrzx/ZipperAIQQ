using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>服务端日志页（#31）。</summary>
public sealed partial class LogsPage : Page
{
    public LogsPage()
    {
        Vm = App.Services.GetRequiredService<LogsViewModel>();
        InitializeComponent();

        _ = Vm.QueryCommand.ExecuteAsync(null);
    }

    public LogsViewModel Vm { get; }
}
