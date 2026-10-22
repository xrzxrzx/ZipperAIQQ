using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>数据页：Token 用量 / 检索命中率 / JEV 判断情况。</summary>
public sealed partial class DataPage : Page
{
    public DataPage()
    {
        Vm = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();

        // 进页面先拉一次（之后由 0.5s 心跳维持）
        _ = Vm.LoadStatsCommand.ExecuteAsync(null);
    }

    public MainViewModel Vm { get; }
}
