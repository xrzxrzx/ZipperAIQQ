using System.Collections.Specialized;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>审计页：配置变更动作记录（不含内容）。</summary>
/// <remarks>
/// 集合是「旧 → 新」顺序（#7），每次刷新后自动滚到最新一条 ——
/// 用户不用手动往下拖。
/// </remarks>
public sealed partial class AuditPage : Page
{
    public AuditPage()
    {
        Vm = App.Services.GetRequiredService<AuditViewModel>();
        InitializeComponent();

        // VM 是单例：只在页面存活期间订阅，否则每进出一次就多挂一个处理器（整页泄漏）
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public AuditViewModel Vm { get; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Vm.Audits.CollectionChanged -= OnAuditsChanged;
        Vm.Audits.CollectionChanged += OnAuditsChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) =>
        Vm.Audits.CollectionChanged -= OnAuditsChanged;

    private void OnAuditsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (Vm.Audits.Count == 0)
        {
            return;
        }

        var latest = Vm.Audits[^1];

        // 等这一轮布局完成再滚：直接 ScrollIntoView 时新项还没生成容器
        DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                AuditList.ScrollIntoView(latest);
            }
            catch
            {
                // 页面已卸载 / 容器未生成：滚动失败不影响数据
            }
        });
    }
}

