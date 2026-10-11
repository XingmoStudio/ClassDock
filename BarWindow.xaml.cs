using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using WinRT.Interop;

namespace ClassDock;

/// <summary>
/// 屏幕右缘的小白条：
/// - 12 DIP 宽、全屏高的透明热区窗口，置顶、无边框、不激活（WS_EX_NOACTIVATE）
/// - 白色胶囊握把居中，hover 时 3x32 → 4x40、透明度 0.85 → 1
/// - 点击切换面板；按住向内拖拽跟手露出面板，松手按阈值吸附
/// </summary>
public sealed partial class BarWindow : Window
{
    private const double BarWidthDip = 8;       // 白条宽度
    private const double BarHeightDip = 80;     // 白条高度（短小胶囊）
    private const int BarInsetPx = 9;           // 距屏幕右缘的内缩（物理像素）
    private const double RightMarginDip = 12;   // 面板展开后距右缘的距离
    private const double DeadzoneDip = 4;       // 点击 vs 拖拽死区
    private const double OpenThresholdDip = 40; // 拖拽判定阈值
    private const int AnimMs = 280;             // 滑入滑出时长

    private PanelWindow? _panel;
    private IntPtr _hwnd;

    // 屏幕批注会话
    private AnnotationCanvasWindow? _annoCanvas;
    private AnnotationToolbarWindow? _annoToolbar;
    private bool _annotationActive;

    // 屏幕度量（物理像素 / DIP）
    private int _screenWpx, _screenHpx;
    private double _scale = 1;
    private int _panelWpx, _panelHpx, _panelTopPx, _spanPx;
    private int _openXpx, _closedXpx;

    // 拖拽状态
    private bool _pressed, _moved, _wasOpen;
    private double _startXdip, _lastInwardDip;

    // 动画 & 失焦检测
    private DispatcherTimer? _animTimer;
    private DispatcherTimer? _focusWatchTimer;

    public BarWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        SetupWindow();

        Grip.Loaded += (_, _) => Layout();
        Grip.PointerEntered += OnRootEntered;
        Grip.PointerExited += OnRootExited;
        Grip.PointerPressed += OnPointerPressed;
        Grip.PointerMoved += OnPointerMoved;
        Grip.PointerReleased += OnPointerReleased;
        Grip.PointerCaptureLost += OnCaptureLost;
    }

    private void SetupWindow()
    {
        WindowMinSize.RemoveLimits(_hwnd);
        // 移除对话框边框残留——否则 XAML 客户区被挤小，白条圆角端被截成平顶
        WindowMinSize.StripDialogFrame(_hwnd);

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

        // 白条形状 = 窗口形状：禁用 DWM 自动圆角（窄窗口会被裁成尖端），
        // 窗口区域由 Layout 中的 SetCapsuleRegion 设为胶囊（两端半圆）。
        // 注意：SystemBackdrop 透明画刷与 DwmGlass 玻璃帧都会与 XAML 岛冲突
        // （崩溃或吞掉内容），已弃用；窗口白底 + 胶囊区域 = 白色胶囊白条。
        DwmGlass.DisableCornerRounding(_hwnd);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    /// <summary>计算屏幕/面板度量，摆放自身并创建面板窗口。</summary>
    private void Layout()
    {
        _screenWpx = GetSystemMetrics(SmCxScreen);
        _screenHpx = GetSystemMetrics(SmCyScreen);
        _scale = Grip.XamlRoot?.RasterizationScale ?? 1;
        if (_scale <= 0) _scale = 1;

        // 自身：右缘短小胶囊，垂直居中
        int barWpx = Px(BarWidthDip);
        int barHpx = Px(BarHeightDip);
        int barTopPx = (_screenHpx - barHpx) / 2;
        AppWindow.MoveAndResize(new RectInt32(_screenWpx - BarInsetPx - barWpx, barTopPx, barWpx, barHpx));
        // 圆角 = 半宽 → 两端半圆胶囊
        Grip.CornerRadius = new CornerRadius(BarWidthDip / 2);
        // 窗口区域设为胶囊（两端半圆），DWM 不再叠加自动圆角
        DwmGlass.SetCapsuleRegion(_hwnd, barWpx, barHpx);

        // 面板：高 = 屏幕高 25%，宽 = 高 x √2
        double panelHdip = _screenHpx / _scale * 0.25;
        double panelWdip = panelHdip * Math.Sqrt(2);
        _panelWpx = Px(panelWdip);
        _panelHpx = Px(panelHdip);
        _panelTopPx = (_screenHpx - _panelHpx) / 2;
        _spanPx = _panelWpx + Px(RightMarginDip);
        _openXpx = _screenWpx - _spanPx;
        _closedXpx = _screenWpx;

        if (_panel is null)
        {
            _panel = new PanelWindow();
            _panel.AnnotationRequested += EnterAnnotation;
        }
        _panel.Configure(_panelWpx, _panelHpx, _panelTopPx, _closedXpx);
    }

    private int Px(double dip) => (int)Math.Round(dip * _scale);

    // ── hover 握把 ──

    private void OnRootEntered(object sender, PointerRoutedEventArgs e)
    {
        Grip.Opacity = 1;
    }

    private void OnRootExited(object sender, PointerRoutedEventArgs e)
    {
        if (_pressed) return;
        Grip.Opacity = 0.85;
    }

    // ── 点击 / 拖拽 ──

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Grip).Properties.IsLeftButtonPressed) return;
        StopAnim();
        _pressed = true;
        _moved = false;
        _wasOpen = _panel is { IsOpen: true };
        _startXdip = e.GetCurrentPoint(Grip).Position.X;
        _lastInwardDip = 0;
        Grip.CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed || _panel is null) return;
        double inward = _startXdip - e.GetCurrentPoint(Grip).Position.X; // 向左(内)为正
        if (!_moved && Math.Abs(inward) <= DeadzoneDip) return;

        if (!_moved)
        {
            _moved = true;
            if (!_wasOpen)
            {
                _panel.AppWindow.Move(new PointInt32(_closedXpx, _panelTopPx));
                _panel.AppWindow.Show(activateWindow: false);
            }
        }
        _lastInwardDip = inward;

        double offsetPx = _wasOpen
            ? _spanPx + Math.Clamp(-inward, 0, _spanPx / _scale) * _scale
            : Math.Clamp(inward, 0, _spanPx / _scale) * _scale;
        _panel.AppWindow.Move(
            new PointInt32(_screenWpx - (int)Math.Round(offsetPx), _panelTopPx));
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        Grip.ReleasePointerCapture(e.Pointer);

        if (!_moved)
        {
            TogglePanel();
            return;
        }

        if (_wasOpen)
        {
            if (-_lastInwardDip > OpenThresholdDip) ClosePanelAnimated();
            else OpenPanelAnimated();
        }
        else
        {
            if (_lastInwardDip > OpenThresholdDip) OpenPanelAnimated();
            else ClosePanelAnimated();
        }
    }

    private void OnCaptureLost(object sender, PointerRoutedEventArgs e) => _pressed = false;

    // ── 面板开关（带缓动动画） ──

    private void TogglePanel()
    {
        if (_panel is { IsOpen: true }) ClosePanelAnimated();
        else OpenPanelAnimated();
    }

    private void OpenPanelAnimated()
    {
        if (_panel is null) return;
        StopAnim();
        var panel = _panel;
        panel.AppWindow.Move(new PointInt32(_closedXpx, _panelTopPx));
        if (!panel.AppWindow.IsVisible) panel.AppWindow.Show(activateWindow: false);
        panel.IsOpen = true;
        panel.Activate();
        AppWindow.Hide(); // 面板打开期间隐藏白条
        StartFocusWatch();
        StartAnim(fromX: _closedXpx, toX: _openXpx);
    }

    private void ClosePanelAnimated()
    {
        if (_panel is null) return;
        StopFocusWatch();
        StopAnim();
        var panel = _panel;
        panel.IsOpen = false;
        if (!AppWindow.IsVisible) AppWindow.Show(activateWindow: false); // 恢复白条
        if (!panel.AppWindow.IsVisible) return;
        StartAnim(fromX: _openXpx, toX: _closedXpx,
            onDone: () => panel.AppWindow.Hide());
    }

    // ── 屏幕批注会话（界面阶段：画布 + 竖排工具栏） ──

    private void EnterAnnotation()
    {
        if (_panel is null || _annotationActive) return;
        _annotationActive = true;

        StopFocusWatch();
        StopAnim();

        // 收起主面板；白条保持隐藏
        _panel.IsOpen = false;
        if (_panel.AppWindow.IsVisible) _panel.AppWindow.Hide();

        // 画布不激活（不打断前台内容），工具栏激活以接收点击
        _annoCanvas = new AnnotationCanvasWindow();
        _annoToolbar = new AnnotationToolbarWindow();
        _annoToolbar.BackRequested += ExitAnnotation;

        _annoCanvas.ShowFullscreen(_screenWpx, _screenHpx);
        _annoToolbar.ShowAtRight(_screenWpx, _screenHpx, _scale);
    }

    private void ExitAnnotation()
    {
        if (!_annotationActive || _annoCanvas is null || _annoToolbar is null) return;
        _annotationActive = false;

        var canvas = _annoCanvas;
        var toolbar = _annoToolbar;
        _annoCanvas = null;
        _annoToolbar = null;
        toolbar.BackRequested -= ExitAnnotation;

        canvas.PlayExit(() => { });
        toolbar.PlayExit(() =>
        {
            canvas.AppWindow.Hide();
            toolbar.AppWindow.Hide();
            // 返回主面板（横向滑入恢复，白条仍隐藏）
            OpenPanelAnimated();
        });
    }

    /// <summary>
    /// 面板打开期间轮询前台窗口：用户点到面板/白条以外的地方（桌面、其它应用）
    /// 即视为“点击外部关闭”。
    /// </summary>
    private void StartFocusWatch()
    {
        StopFocusWatch();
        _focusWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _focusWatchTimer.Tick += (_, _) =>
        {
            if (_panel is not { IsOpen: true } || _pressed) return;
            var fg = GetForegroundWindow();
            if (fg != _panel.Hwnd && fg != _hwnd) ClosePanelAnimated();
        };
        _focusWatchTimer.Start();
    }

    private void StopFocusWatch()
    {
        _focusWatchTimer?.Stop();
        _focusWatchTimer = null;
    }

    private void StartAnim(int fromX, int toX, Action? onDone = null)
    {
        var panel = _panel!;
        var sw = Stopwatch.StartNew();
        _animTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _animTimer.Tick += (_, _) =>
        {
            double t = sw.ElapsedMilliseconds / (double)AnimMs;
            if (t >= 1)
            {
                StopAnim();
                panel.AppWindow.Move(new PointInt32(toX, _panelTopPx));
                onDone?.Invoke();
                return;
            }
            double ease = 1 - Math.Pow(2, -10 * t); // ExponentialEaseOut
            int x = fromX + (int)Math.Round((toX - fromX) * ease);
            panel.AppWindow.Move(new PointInt32(x, _panelTopPx));
        };
        _animTimer.Start();
    }

    private void StopAnim()
    {
        _animTimer?.Stop();
        _animTimer = null;
    }
}
