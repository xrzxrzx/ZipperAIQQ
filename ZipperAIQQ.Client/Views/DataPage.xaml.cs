using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>数据页：Token 用量 / 缓存 / 检索命中率 / JEV 判断 / 会话维度。</summary>
public sealed partial class DataPage : Page
{
    public DataPage()
    {
        Stats = App.Services.GetRequiredService<StatsViewModel>();
        Vm = Stats;
        InitializeComponent();

        // 进页面先拉一次（之后由 0.5s 心跳维持）
        _ = Stats.LoadCommand.ExecuteAsync(null);
    }

    /// <summary>页面状态条也走统计 VM（B7：不再依赖原 MainViewModel）。</summary>
    public StatsViewModel Vm { get; }

    public StatsViewModel Stats { get; }
}

