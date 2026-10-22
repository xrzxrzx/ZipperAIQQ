using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>审计页：配置变更动作记录（不含内容）。</summary>
public sealed partial class AuditPage : Page
{
    public AuditPage()
    {
        Vm = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
    }

    public MainViewModel Vm { get; }
}
