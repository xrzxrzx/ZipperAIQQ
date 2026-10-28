using System.Collections.Specialized;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;

namespace ZipperAIQQ.Client.Views;

/// <summary>会话页：左侧会话列表 / 右侧消息 + 搜索 + 区间导出。</summary>
public sealed partial class SessionsPage : Page
{
    public SessionsPage()
    {
        Vm = App.Services.GetRequiredService<SessionsViewModel>();
        InitializeComponent();

        // 消息更新后自动滚到底部（0.5s 增量追加时尤其需要，否则新消息在视野外）
        Vm.Messages.CollectionChanged += OnMessagesChanged;

        // 从别的页面切回来时，Messages 里已有数据但**不会再触发 Add 事件**
        // → 必须在页面 Loaded 时主动滚一次，否则永远停在顶部
        Loaded += (_, _) => ScrollToBottom();
        Unloaded += (_, _) => Vm.Messages.CollectionChanged -= OnMessagesChanged;
    }

    public SessionsViewModel Vm { get; }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _ = Vm.LoadSessionsCommand.ExecuteAsync(null);
    }

    /// <summary>按时间区间导出（#37）：起止都可空，空 = 不限那一端。</summary>
    private async void ExportRange_Click(object sender, RoutedEventArgs e)
    {
        var today = DateTimeOffset.Now.Date;

        var fromPicker = new DatePicker { Header = "开始日期", Date = today.AddDays(-7) };
        var toPicker = new DatePicker { Header = "结束日期", Date = today };
        var unlimited = new CheckBox { Content = "不限时间（导出当前已加载）" };

        var panel = DialogTheme.Panel(minWidth: 300);
        panel.Children.Add(fromPicker);
        panel.Children.Add(toPicker);
        panel.Children.Add(unlimited);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "按区间导出",
            Content = panel,
            PrimaryButtonText = "导出",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };

        DialogTheme.Apply(dialog);

        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"导出对话框失败：{ex}");
            return;
        }

        try
        {
            var file = unlimited.IsChecked == true
                ? await Vm.ExportAsync(null, null)
                : await Vm.ExportAsync(
                    SessionsViewModel.ExportStart(fromPicker.Date),
                    SessionsViewModel.ExportEnd(toPicker.Date));

            var done = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "已导出",
                Content = file,
                CloseButtonText = "好",
            };

            DialogTheme.Apply(done);
            await done.ShowAsync();
        }
        catch (Exception ex)
        {
            var failed = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "导出失败",
                Content = ex.Message,
                CloseButtonText = "好",
            };

            DialogTheme.Apply(failed);
            await failed.ShowAsync();
        }
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add || Vm.Messages.Count == 0)
        {
            return;
        }

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
