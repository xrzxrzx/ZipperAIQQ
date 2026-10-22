using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ZipperAIQQ.Client.ViewModels;
using ZipperAIQQ.Client.Views;
using Windows.Graphics;

namespace ZipperAIQQ.Client;

/// <summary>主窗口：Mica 背景 + 自定义标题栏 + NavigationView 导航。</summary>
public sealed partial class MainWindow : Window
{
    private readonly AppWindow _appWindow;

    public MainWindow(MainViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);

        // x:Bind 读的就是这个属性，必须在 InitializeComponent 之前赋值
        Vm = vm;
        InitializeComponent();

        _appWindow = AppWindow;
        InitializeTitleBar();

        // 任务栏/Alt+Tab 图标也用头像（unpackaged 下用输出目录里的文件）
        var avatar = Path.Combine(AppContext.BaseDirectory, "Assets", "avatar.png");
        if (File.Exists(avatar))
        {
            _appWindow.SetIcon(avatar);
        }

        // 默认给一个舒服的窗口大小，并选中第一项
        _appWindow.Resize(new SizeInt32(1200, 820));
        NavView.SelectedItem = NavView.MenuItems[0];
        ContentFrame.Navigate(typeof(StatusPage));
    }

    public MainViewModel Vm { get; }

    /// <summary>隐藏系统标题栏，内容扩展到标题栏区域，系统按钮透明化。</summary>
    private void InitializeTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        _appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        _appWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;

        _appWindow.TitleBar.ButtonBackgroundColor = Colors.Transparent;
        _appWindow.TitleBar.ButtonInactiveBackgroundColor = Colors.Transparent;
        _appWindow.TitleBar.ButtonForegroundColor = Colors.Gray;
        _appWindow.TitleBar.ButtonInactiveForegroundColor = Colors.Gray;
    }

    private void AppTitleBar_Loaded(object sender, RoutedEventArgs e) => UpdateTitleBarLayout();

    private void AppTitleBar_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTitleBarLayout();

    /// <summary>为系统按钮预留左右空间，并把标题栏设为拖动区域。</summary>
    private void UpdateTitleBarLayout()
    {
        if (AppTitleBar.XamlRoot is null)
        {
            return;
        }

        var scale = AppTitleBar.XamlRoot.RasterizationScale > 0
            ? AppTitleBar.XamlRoot.RasterizationScale
            : 1.0;

        LeftPaddingColumn.Width = new GridLength(_appWindow.TitleBar.LeftInset / scale);
        RightPaddingColumn.Width = new GridLength(_appWindow.TitleBar.RightInset / scale);

        var totalWidth = AppTitleBar.ActualWidth * scale;
        if (totalWidth <= 0)
        {
            return;
        }

        var leftInset = _appWindow.TitleBar.LeftInset;
        var rightInset = _appWindow.TitleBar.RightInset;
        var dragWidth = Math.Max(0, totalWidth - leftInset - rightInset);

        _appWindow.TitleBar.SetDragRectangles(
        [
            new RectInt32((int)leftInset, 0, (int)dragWidth, (int)(AppTitleBar.ActualHeight * scale)),
        ]);
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag?.ToString() is not { } tag)
        {
            return;
        }

        var page = tag switch
        {
            "sessions" => typeof(SessionsPage),
            "config" => typeof(ConfigPage),
            "audit" => typeof(AuditPage),
            _ => typeof(StatusPage),
        };

        ContentFrame.Navigate(page);
    }
}
