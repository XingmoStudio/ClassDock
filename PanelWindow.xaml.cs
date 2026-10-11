using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassDock;

/// <summary>
/// 面板卡片窗口：Fluent 2 浅色圆角卡片。
/// 由 BarWindow 控制显隐与位置；失焦关闭由 BarWindow 轮询前台窗口实现。
/// </summary>
public sealed partial class PanelWindow : Window
{
    public bool IsOpen { get; set; }

    public IntPtr Hwnd { get; }

    /// <summary>点击“屏幕批注”入口时触发（由 BarWindow 负责切换到批注界面）。</summary>
    public event Action? AnnotationRequested;

    public PanelWindow()
    {
        InitializeComponent();
        Hwnd = WindowNative.GetWindowHandle(this);
        DwmGlass.Enable(Hwnd);

        var presenter = AppWindow.Presenter as OverlappedPresenter;
        presenter?.SetBorderAndTitleBar(false, false);
        if (presenter is not null)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
        }
        AppWindow.IsShownInSwitchers = false;
    }

    /// <summary>由 BarWindow 在度量完成后调用（隐藏状态下预设尺寸与位置）。</summary>
    public void Configure(int widthPx, int heightPx, int topPx, int closedXpx)
    {
        AppWindow.MoveAndResize(new RectInt32(closedXpx, topPx, widthPx, heightPx));
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var win = new SettingsWindow();
        win.Activate();
    }

    private void OnAnnotationClick(object sender, RoutedEventArgs e)
        => AnnotationRequested?.Invoke();
}
