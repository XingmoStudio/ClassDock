using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
// ── PanelWindow ──
// 面板卡片窗口：由 BarWindow 控制显隐与位置的 Fluent 2 浅色圆角卡片。

using WinRT.Interop;

namespace ClassDock;

/// <summary>
/// 面板卡片窗口：Fluent 2 浅色圆角卡片。
/// 由 BarWindow 控制显隐与位置；失焦关闭由 BarWindow 轮询前台窗口实现。
/// </summary>
public sealed partial class PanelWindow : Window
{
    /// <summary>面板是否处于展开状态。</summary>
    public bool IsOpen { get; set; }

    /// <summary>窗口的原生句柄（HWND），用于前台窗口轮询判定焦点归属。</summary>
    public IntPtr Hwnd { get; }

    /// <summary>点击“屏幕批注”入口时触发（由 BarWindow 负责切换到批注界面）。</summary>
    public event Action? AnnotationRequested;

    public PanelWindow()
    {
        InitializeComponent();
        Hwnd = WindowNative.GetWindowHandle(this);
        DwmGlass.Enable(Hwnd);
        // 玻璃帧 + 卡片自带描边后，DWM 仍会在窗口外沿画一圈浅色系统边框，深色下就是那条白边
        DwmGlass.DisableBorder(Hwnd);

        // 面板跟随统一主题，功能图标使用应用强调色（设置按钮保持默认样式）
        AppTheme.Apply(Root);
        AnnotationIcon.Foreground = AppTheme.AccentBrush;
        Closed += (_, _) => AppTheme.Unapply(Root);

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
