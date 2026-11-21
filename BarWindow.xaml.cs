using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
// ── BarWindow ──
// 屏幕右缘交互白条：透明热区 + 白色胶囊握把，负责面板的开关与拖拽跟手。

using WinRT.Interop;

namespace ClassDock;

/// <summary>
/// 屏幕右缘的侧边栏：
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
    private bool _annotationActive;

    // 屏幕度量（物理像素 / DIP）
    private int _screenWpx, _screenHpx;
    private double _scale = 1;
    private int _barWpx, _barHpx, _barLeftPx, _barTopPx;
    private int _panelWpx, _panelHpx, _panelTopPx, _spanPx;
    private int _openXpx, _closedXpx;

    private enum DragAxis { None, Horizontal, Vertical }

    // 拖拽状态
    private bool _pressed, _moved, _wasOpen, _verticalDragArmed;
    private double _startXdip, _lastInwardDip;
    private DragAxis _dragAxis;
    private int _startCursorXpx, _startCursorYpx, _startBarTopPx;

    // 动画 & 失焦检测
    private DispatcherTimer? _animTimer;
    private DispatcherTimer? _focusWatchTimer;

    public BarWindow()
    {
        InitializeComponent();
        App.ApplyWindowIcon(AppWindow);
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

    /// <summary>配置窗口样式：无边框、置顶，并使用 DWM 小圆角平滑裁剪。</summary>
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

        // 小圆角由 DWM 平滑裁剪；不再使用会产生硬锯齿的 SetWindowRgn。
        // 默认圆角半径过大，会把窄白条端部切成尖形，因此使用小圆角。
        DwmGlass.EnableSmallCornerRounding(_hwnd);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    /// <summary>计算屏幕/面板度量，摆放自身并创建面板窗口。</summary>
    private void Layout()
    {
        _screenWpx = GetSystemMetrics(SmCxScreen);
        _screenHpx = GetSystemMetrics(SmCyScreen);
        _scale = Grip.XamlRoot?.RasterizationScale ?? 1;
        if (_scale <= 0) _scale = 1;

        // 自身：右缘短小胶囊，按持久化比例恢复垂直位置。
        _barWpx = Px(BarWidthDip);
        _barHpx = Px(BarHeightDip);
        int verticalRange = Math.Max(0, _screenHpx - _barHpx);
        _barTopPx = Math.Clamp((int)Math.Round(verticalRange * AppSettings.BarVerticalRatio), 0, verticalRange);
        _barLeftPx = _screenWpx - BarInsetPx - _barWpx;
        AppWindow.MoveAndResize(new RectInt32(_barLeftPx, _barTopPx, _barWpx, _barHpx));
        Grip.CornerRadius = new CornerRadius(BarWidthDip / 2);

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

    /// <summary>DIP 转物理像素。</summary>
    private int Px(double dip) => (int)Math.Round(dip * _scale);

    // ── hover 握把 ──

    /// <summary>指针进入握把：完全不透明。</summary>
    private void OnRootEntered(object sender, PointerRoutedEventArgs e)
    {
        Grip.Opacity = 1;
    }

    /// <summary>指针离开握把：恢复半透明（拖拽中不改变）。</summary>
    private void OnRootExited(object sender, PointerRoutedEventArgs e)
    {
        if (_pressed) return;
        Grip.Opacity = 0.85;
    }

    // ── 点击 / 拖拽 ──

    /// <summary>指针按下：记录起始状态，捕获指针。</summary>
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!e.GetCurrentPoint(Grip).Properties.IsLeftButtonPressed) return;
        StopAnim();
        _pressed = true;
        _moved = false;
        _wasOpen = _panel is { IsOpen: true };
        _verticalDragArmed = AppSettings.BarVerticalDragEnabled;
        _dragAxis = DragAxis.None;
        _startXdip = e.GetCurrentPoint(Grip).Position.X;
        _lastInwardDip = 0;
        _startBarTopPx = _barTopPx;
        GetCursorPos(out var cursor);
        _startCursorXpx = cursor.X;
        _startCursorYpx = cursor.Y;
        Grip.CapturePointer(e.Pointer);
    }

    /// <summary>指针移动：按主导方向调整白条位置或跟手拖拽面板。</summary>
    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;

        // 开关开启时，第一次越过死区即锁定主轴，避免垂直调整误触面板。
        if (_verticalDragArmed && _dragAxis == DragAxis.None)
        {
            GetCursorPos(out var cursor);
            int dx = cursor.X - _startCursorXpx;
            int dy = cursor.Y - _startCursorYpx;
            int deadzonePx = Math.Max(1, Px(DeadzoneDip));
            if (Math.Abs(dx) <= deadzonePx && Math.Abs(dy) <= deadzonePx) return;
            _dragAxis = Math.Abs(dy) > Math.Abs(dx) ? DragAxis.Vertical : DragAxis.Horizontal;
        }

        if (_dragAxis == DragAxis.Vertical)
        {
            GetCursorPos(out var cursor);
            _moved = true;
            MoveBarVertical(_startBarTopPx + cursor.Y - _startCursorYpx);
            return;
        }

        if (_panel is null) return;
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

    /// <summary>指针抬起：保存垂直位置，或按水平拖拽距离和方向决定面板吸附状态。</summary>
    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        bool moved = _moved;
        var dragAxis = _dragAxis;
        _pressed = false;
        _moved = false;
        _dragAxis = DragAxis.None;
        _verticalDragArmed = false;
        Grip.ReleasePointerCapture(e.Pointer);

        if (dragAxis == DragAxis.Vertical)
        {
            SaveBarVerticalPosition();
            return;
        }

        if (!moved)
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

    /// <summary>指针捕获丢失：取消未完成的垂直拖动并复位。</summary>
    private void OnCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (!_pressed) return;
        if (_dragAxis == DragAxis.Vertical) MoveBarVertical(_startBarTopPx);
        _pressed = false;
        _moved = false;
        _dragAxis = DragAxis.None;
        _verticalDragArmed = false;
    }

    /// <summary>移动可见侧边栏并夹紧到当前屏幕的垂直可容纳范围。</summary>
    private void MoveBarVertical(int topPx)
    {
        int verticalRange = Math.Max(0, _screenHpx - _barHpx);
        _barTopPx = Math.Clamp(topPx, 0, verticalRange);
        AppWindow.Move(new PointInt32(_barLeftPx, _barTopPx));
    }

    /// <summary>将当前位置保存为可移动行程比例，适配重启后的分辨率与 DPI 变化。</summary>
    private void SaveBarVerticalPosition()
    {
        int verticalRange = Math.Max(0, _screenHpx - _barHpx);
        if (verticalRange <= 0) return;
        AppSettings.BarVerticalRatio = Math.Clamp((double)_barTopPx / verticalRange, 0, 1);
        AppSettings.Save();
    }

    // ── 面板开关（带缓动动画） ──

    /// <summary>切换面板开关状态。</summary>
    private void TogglePanel()
    {
        if (_panel is { IsOpen: true }) ClosePanelAnimated();
        else OpenPanelAnimated();
    }

    /// <summary>展开面板：从屏幕外滑入，隐藏白条并启动失焦检测。</summary>
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

    /// <summary>收起面板：滑出屏幕后隐藏，恢复白条显示。</summary>
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

    // ── 屏幕批注会话（画布 + 同窗口内的竖排工具栏） ──

    /// <summary>进入批注模式：收起面板，延迟后截屏并显示画布。</summary>
    private void EnterAnnotation()
    {
        if (_panel is null || _annotationActive) return;
        _annotationActive = true;

        StopFocusWatch();
        StopAnim();

        // 收起主面板与白条；等 DWM 重绘完再截屏，避免把面板截进背景
        _panel.IsOpen = false;
        _panel.AppWindow.Hide();

        var delay = DispatcherQueue.CreateTimer();
        delay.Interval = TimeSpan.FromMilliseconds(160);
        delay.IsRepeating = false;
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            if (!_annotationActive) return;
            StartAnnotationWindow();
        };
        delay.Start();
    }

    /// <summary>截取屏幕并启动全屏批注画布。</summary>
    private void StartAnnotationWindow()
    {
        // 先截屏，再让全屏画布以快照为背景出现
        var snapshot = ScreenSnapshot.Capture();

        _annoCanvas = new AnnotationCanvasWindow();
        if (snapshot is not null) _annoCanvas.SetSnapshot(snapshot);
        _annoCanvas.ExitRequested += ExitAnnotation;
        _annoCanvas.ShowFullscreen(_screenWpx, _screenHpx);
    }

    /// <summary>退出批注模式：关闭画布并恢复面板。</summary>
    private void ExitAnnotation()
    {
        if (!_annotationActive || _annoCanvas is null) return;
        _annotationActive = false;

        var canvas = _annoCanvas;
        _annoCanvas = null;
        canvas.ExitRequested -= ExitAnnotation;

        canvas.PlayExit(() =>
        {
            canvas.AppWindow.Hide();
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

    /// <summary>停止失焦检测计时器。</summary>
    private void StopFocusWatch()
    {
        _focusWatchTimer?.Stop();
        _focusWatchTimer = null;
    }

    /// <summary>启动面板滑入/滑出动画（ExponentialEaseOut）。</summary>
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

    /// <summary>停止面板滑动动画。</summary>
    private void StopAnim()
    {
        _animTimer?.Stop();
        _animTimer = null;
    }
}
