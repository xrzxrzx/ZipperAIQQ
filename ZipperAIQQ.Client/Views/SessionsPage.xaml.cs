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

        // 消息更新后自动滚到底部（0.5s 增量追加时尤其需要，否则新消息在视野外）
        Vm.Messages.CollectionChanged += OnMessagesChanged;
        Unloaded += (_, _) => Vm.Messages.CollectionChanged -= OnMessagesChanged;
    }

    public MainViewModel Vm { get; }

    private void OnMessagesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add ||
            Vm.Messages.Count == 0)
        {
            return;
        }

        // 放到下一帧：集合刚变更时容器还没生成，立刻 ScrollIntoView 会滚不到位
        DispatcherQueue.TryEnqueue(() =>
        {
            var last = Vm.Messages[^1];
            MessageList.ScrollIntoView(last);
        });
    }
}
