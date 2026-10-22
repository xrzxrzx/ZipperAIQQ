using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>数据页：Token 用量 / 检索命中率 / JEV 判断情况。</summary>
/// <remarks>
/// 统计相关的绑定全部走 <see cref="StatsViewModel"/>（从 MainViewModel 抽出来的），
/// 这里只用 <see cref="MainViewModel"/> 取全局状态条。
/// </remarks>
public sealed partial class DataPage : Page
{
    public DataPage()
    {
        Vm = App.Services.GetRequiredService<MainViewModel>();
        Stats = App.Services.GetRequiredService<StatsViewModel>();
        InitializeComponent();

        // 进页面先拉一次（之后由 0.5s 心跳维持）
        _ = Stats.LoadCommand.ExecuteAsync(null);
    }

    public MainViewModel Vm { get; }

    public StatsViewModel Stats { get; }
}
