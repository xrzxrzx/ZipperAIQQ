using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using ZipperAIQQ.Client.Services;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client;

/// <summary>
/// 组合根（WinUI3 没有 Host，手工建 <see cref="ServiceCollection"/>）。
/// </summary>
public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();

        var services = new ServiceCollection();
        services.AddSingleton(AppSettings.Load());
        services.AddSingleton<ServerApiClient>();
        services.AddSingleton<StatsViewModel>();
        services.AddSingleton<MainViewModel>();

        Services = services.BuildServiceProvider();
    }

    /// <summary>全局容器（WinUI3 里没有 Host，只能这样拿）。</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow(Services.GetRequiredService<MainViewModel>());
        _window.Activate();
    }
}
