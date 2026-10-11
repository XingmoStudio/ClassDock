using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.UI;

namespace ClassDock;

/// <summary>
/// 设置窗口：顶栏与三个系统窗口按键合并（内容延伸进标题栏），
/// 顶栏右端仅显示版本号；左侧导航（关于 / 关闭）+ 右侧内容页。
/// 由 PanelWindow 齿轮按钮打开。
/// </summary>
public sealed partial class SettingsWindow : Window
{
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

        // 居中显示（保持默认可调/可最大化，确保"最大化/还原"可用）
        const int width = 920, height = 640;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        var workArea = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2));
    }

    private void OnNavAboutClick(object sender, RoutedEventArgs e)
    {
        // v0：关于页是唯一内容页，无需切换
    }

    private void OnNavExitClick(object sender, RoutedEventArgs e)
    {
        // 结束整个 ClassDock 进程（含白条、面板等所有窗口）
        Environment.Exit(0);
    }
}
