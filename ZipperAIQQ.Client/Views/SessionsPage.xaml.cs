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

        // 从别的页面切回来时，Messages 里已有数据但**不会再触发 Add 事件**
        // → 必须在页面 Loaded 时主动滚一次，否则永远停在顶部
        Loaded += (_, _) => ScrollToBottom();
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
        ScrollToBottom();
    }

    /// <summary>滚到最后一条（下一帧执行：容器还没生成时立刻滚会滚不到位）。</summary>
    private void ScrollToBottom()
    {
        if (Vm.Messages.Count == 0)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            MessageList.ScrollIntoView(Vm.Messages[^1]);
        });
    }
}
