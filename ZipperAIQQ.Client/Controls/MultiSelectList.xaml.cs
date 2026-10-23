using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ZipperAIQQ.Client.Controls;

/// <summary>
/// 带搜索的多选列表（问题记录 #26）。
/// </summary>
/// <remarks>
/// 管理员 / 白名单 / 黑名单原来都是「逗号分隔的文本框」——要手打账号，
/// 而且完全看不出候选是谁。这里用勾选式列表 + 搜索。
/// </remarks>
public sealed partial class MultiSelectList : UserControl
{
    private List<string> _all = [];
    private bool _syncing;

    public MultiSelectList()
    {
        InitializeComponent();
        Loaded += (_, _) => SyncSelectionFromValues();
    }

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IList<string>), typeof(MultiSelectList),
        new PropertyMetadata(null, OnItemsChanged));

    public static readonly DependencyProperty SelectedValuesProperty = DependencyProperty.Register(
        nameof(SelectedValues), typeof(IList<string>), typeof(MultiSelectList),
        new PropertyMetadata(null, OnValuesChanged));

    public IList<string>? ItemsSource
    {
        get => (IList<string>?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public IList<string>? SelectedValues
    {
        get => (IList<string>?)GetValue(SelectedValuesProperty);
        set => SetValue(SelectedValuesProperty, value);
    }

    private static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (MultiSelectList)d;

        if (e.OldValue is INotifyCollectionChanged oldNotify)
        {
            oldNotify.CollectionChanged -= control.OnCollectionChanged;
        }

        control._all = e.NewValue is IList<string> list ? list.ToList() : [];
        if (e.NewValue is INotifyCollectionChanged notify)
        {
            notify.CollectionChanged += control.OnCollectionChanged;
        }

        control.RefreshItems();
        control.SyncSelectionFromValues();
    }

    private static void OnValuesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (MultiSelectList)d;

        if (e.OldValue is INotifyCollectionChanged oldNotify)
        {
            oldNotify.CollectionChanged -= control.OnValuesCollectionChanged;
        }

        if (e.NewValue is INotifyCollectionChanged notify)
        {
            notify.CollectionChanged += control.OnValuesCollectionChanged;
        }

        control.SyncSelectionFromValues();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        _all = ItemsSource?.ToList() ?? [];
        RefreshItems();
        SyncSelectionFromValues();
    }

    private void OnValuesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => SyncSelectionFromValues();

    private void RefreshItems()
    {
        var keyword = SearchBox.Text?.Trim() ?? string.Empty;
        var view = string.IsNullOrEmpty(keyword)
            ? _all
            : _all.Where(x => x.Contains(keyword, StringComparison.OrdinalIgnoreCase)).ToList();

        _syncing = true;
        try
        {
            List.ItemsSource = view;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SyncSelectionFromValues()
    {
        var values = SelectedValues;
        if (values is null || List.ItemsSource is not IEnumerable<string> view)
        {
            return;
        }

        _syncing = true;
        try
        {
            List.SelectedItems.Clear();
            foreach (var item in view)
            {
                if (values.Contains(item))
                {
                    List.SelectedItems.Add(item);
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshItems();
        SyncSelectionFromValues();
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SelectedValues is not { } values)
        {
            return;
        }

        _syncing = true;
        try
        {
            var selected = List.SelectedItems.OfType<string>().ToList();

            // 只改「当前可见项」的选择：搜索过滤不能让看不见的已选项消失
            foreach (var item in values.ToList())
            {
                if (List.ItemsSource is IEnumerable<string> view && view.Contains(item) && !selected.Contains(item))
                {
                    values.Remove(item);
                }
            }

            foreach (var item in selected)
            {
                if (!values.Contains(item))
                {
                    values.Add(item);
                }
            }
        }
        finally
        {
            _syncing = false;
        }
    }
}
