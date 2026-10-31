using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.UI;

namespace ClassDock;

/// <summary>
/// 设置窗口：顶栏与三个系统窗口按键合并（内容延伸进标题栏），
/// 顶栏右端仅显示版本号；左侧导航（关于 / 主页 / 通用 / 小工具 / 调试 + 关闭软件）+ 右侧内容页。
/// 由 PanelWindow 齿轮按钮打开。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static readonly SolidColorBrush AccentBrush
        = new(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
    private static readonly SolidColorBrush SelectedBackground
        = new(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    private Button? _selectedNav;
    private Border? _selectedAccent;
    private bool _loadingSettings;

    public SettingsWindow()
    {
        InitializeComponent();

        // 标题栏合并：AppTitleBar 作为拖拽区，右上角三个按键由系统绘制
        ExtendsContentIntoTitleBar = true;
        // 浅色主题下的按键配色（透明底 + 深色图标，与浅色顶栏融合）
        var tb = AppWindow.TitleBar;
        tb.BackgroundColor = Colors.Transparent;
        tb.ForegroundColor = Colors.Transparent;
        tb.InactiveBackgroundColor = Colors.Transparent;
        tb.InactiveForegroundColor = Colors.Transparent;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = Color.FromArgb(230, 0, 0, 0);
        tb.ButtonHoverBackgroundColor = Color.FromArgb(26, 0, 0, 0);
        tb.ButtonHoverForegroundColor = Colors.Black;
        tb.ButtonPressedBackgroundColor = Color.FromArgb(13, 0, 0, 0);
        tb.ButtonInactiveForegroundColor = Color.FromArgb(128, 0, 0, 0);

        // 顶栏行高 48 DIP，系统按键同步加高，使版本号文字与三键垂直居中对齐
        tb.PreferredHeightOption = TitleBarHeightOption.Tall;

        // 居中显示（保持默认可调/可最大化，确保“最大化/还原”可用）
        // 尺寸按 DPI 换算成物理像素，避免高缩放下窗口内容被挤小
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        int width = (int)(900 * scale), height = (int)(700 * scale);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        var workArea = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2));

        // 默认选中“关于 ClassDock”
        SelectNav(NavAbout, AboutAccent);
        LoadCrashBehavior();
    }

    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        var tag = button.Tag as string ?? "blank";

        ShowPage(tag);
        SelectNav(button, tag switch
        {
            "about" => AboutAccent,
            "debug" => DebugAccent,
            _ => null
        });
    }

    /// <summary>切换右侧内容页；未开放的页面保持空白。</summary>
    private void ShowPage(string tag)
    {
        AboutPage.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        DebugPage.Visibility = tag == "debug" ? Visibility.Visible : Visibility.Collapsed;
        BlankPage.Visibility = tag == "blank" ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>选中态：白色底 + 左侧强调色竖条。</summary>
    private void SelectNav(Button button, Border? accent)
    {
        if (_selectedNav is not null) _selectedNav.Background = TransparentBrush;
        if (_selectedAccent is not null) _selectedAccent.Background = TransparentBrush;

        _selectedNav = button;
        _selectedAccent = accent;
        button.Background = SelectedBackground;
        if (accent is not null) accent.Background = AccentBrush;
    }

    private void OnGeneralGroupClick(object sender, RoutedEventArgs e)
        => ToggleGroup(GeneralChildren, GeneralChevron);

    private void OnToolsGroupClick(object sender, RoutedEventArgs e)
        => ToggleGroup(ToolsChildren, ToolsChevron);

    private static void ToggleGroup(StackPanel children, FontIcon chevron)
    {
        var expand = children.Visibility != Visibility.Visible;
        children.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        chevron.Glyph = expand ? "\uE70D" : "\uE70E";
    }

    private void LoadCrashBehavior()
    {
        _loadingSettings = true;
        switch (AppSettings.CrashBehavior)
        {
            case CrashBehavior.SilentExit:
                CrashSilentExit.IsChecked = true;
                break;
            case CrashBehavior.SilentRestart:
                CrashRestart.IsChecked = true;
                break;
            default:
                CrashShowError.IsChecked = true;
                break;
        }
        _loadingSettings = false;
    }

    private void OnCrashBehaviorChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;

        AppSettings.CrashBehavior = ReferenceEquals(sender, CrashSilentExit) ? CrashBehavior.SilentExit
            : ReferenceEquals(sender, CrashRestart) ? CrashBehavior.SilentRestart
            : CrashBehavior.ShowError;
        AppSettings.Save();
    }

    private void OnNavExitClick(object sender, RoutedEventArgs e)
    {
        // 结束整个 ClassDock 进程（含白条、面板等所有窗口）
        Environment.Exit(0);
    }
}
