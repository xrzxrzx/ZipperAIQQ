using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>会话页：左侧会话列表 / 右侧消息 + 导出。</summary>
public sealed partial class SessionsPage : Page
{
    public SessionsPage()
    {
        Vm = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
    }

    public MainViewModel Vm { get; }
}
