using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace ClassDock;

/// <summary>批注工具。几何图形与尺规本阶段仅占位。</summary>
public enum AnnotationTool
{
    Pen,
    Shapes,
    Eraser,
    Ruler
}

/// <summary>
/// 全屏批注画布：置顶、无边框，底层铺一张进入批注时的屏幕快照，
/// 笔迹用 Pointer 事件 + Polyline 自绘（WinUI 3 无 InkCanvas）；
/// 撤销/重做以“整条笔迹”为单位，橡皮整条删除。
/// </summary>
public sealed partial class AnnotationCanvasWindow : Window
{
    private const double PenThickness = 3;
    private const double EraserRadius = 10;

    private static readonly Color PenColor = Color.FromArgb(0xFF, 0xE8, 0x11, 0x23);

    private static readonly SolidColorBrush AccentBrush
        = new(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
    private static readonly SolidColorBrush NormalBrush
        = new(Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B));

    private readonly List<Polyline> _strokes = new();
    private readonly Stack<Polyline> _redo = new();
    private Polyline? _current;
    private AnnotationTool _tool = AnnotationTool.Pen;
    private bool _erasing;
    private uint _activePointerId;

    public IntPtr Hwnd { get; }

    /// <summary>按 Esc 请求退出批注（由 BarWindow 负责收尾）。</summary>
    public event Action? ExitRequested;

    public AnnotationCanvasWindow()
    {
        InitializeComponent();
        Hwnd = WindowNative.GetWindowHandle(this);

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

        Root.Loaded += (_, _) => PlayEnter();
        IconOf(PenButton).Fill = AccentBrush;
        AddAccelerators();
    }

    /// <summary>按钮内容 = Viewbox + Path，取出其中的 Path 以便切换选中态颜色。
    /// （Path 上不能写 x:Name——XamlCompiler 会静默崩溃；也不支持 AncestorType 绑定。）</summary>
    private static Path IconOf(ButtonBase button) => (Path)((Viewbox)button.Content).Child;

    /// <summary>铺满主屏（物理像素）并显示；不激活，避免打断前台内容。</summary>
    public void ShowFullscreen(int screenWpx, int screenHpx)
    {
        AppWindow.MoveAndResize(new RectInt32(0, 0, screenWpx, screenHpx));
        AppWindow.Show(activateWindow: false);
    }

    /// <summary>设置背景快照（铺满整个窗口）。</summary>
    public void SetSnapshot(ImageSource snapshot) => Snapshot.Source = snapshot;

    // ── 工具切换：单选，选中态用主题蓝 ──

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender;
        var tools = new (ToggleButton Button, Path Icon)[]
        {
            (PenButton, IconOf(PenButton)), (ShapesButton, IconOf(ShapesButton)),
            (EraserButton, IconOf(EraserButton)), (RulerButton, IconOf(RulerButton))
        };

        foreach (var (button, icon) in tools)
        {
            bool active = ReferenceEquals(button, clicked);
            button.IsChecked = active;
            icon.Fill = active ? AccentBrush : NormalBrush;
        }
        AnnotationTool tool = AnnotationTool.Pen;
        if (ReferenceEquals(clicked, ShapesButton)) tool = AnnotationTool.Shapes;
        else if (ReferenceEquals(clicked, EraserButton)) tool = AnnotationTool.Eraser;
        else if (ReferenceEquals(clicked, RulerButton)) tool = AnnotationTool.Ruler;

        if (_tool == tool) return;
        FinishStroke();
        _tool = tool;
    }

    /// <summary>工具栏上的按下不要落到画布上变成笔迹。</summary>
    private void OnCardPressed(object sender, PointerRoutedEventArgs e) => e.Handled = true;

    private void OnBackClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    // ── 绘制 ──

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        var position = point.Position;

        if (point.Properties.IsRightButtonPressed)
        {
            ShowActionMenu(position);
            e.Handled = true;
            return;
        }
        if (!point.Properties.IsLeftButtonPressed) return;

        // 记录本次按下的指针：移动/抬起只处理它，避免混入其它指针的事件
        _activePointerId = e.Pointer.PointerId;
        Root.CapturePointer(e.Pointer);
        e.Handled = true;

        switch (_tool)
        {
            case AnnotationTool.Eraser:
                _erasing = true;
                EraseAt(position);
                break;

            case AnnotationTool.Pen:
                _current = new Polyline
                {
                    Stroke = new SolidColorBrush(PenColor),
                    StrokeThickness = PenThickness,
                    StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    IsHitTestVisible = false
                };
                _current.Points.Add(position);
                InkLayer.Children.Add(_current);
                _strokes.Add(_current);
                _redo.Clear();
                break;

            // 几何图形 / 尺规：本阶段占位，不响应绘制
        }
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _activePointerId) return;

        var point = e.GetCurrentPoint(Root);
        var p = point.Position;

        if (_erasing)
        {
            if (point.Properties.IsLeftButtonPressed) EraseAt(p);
            return;
        }

        if (_current is null) return;
        var last = _current.Points[^1];
        // 过滤亚像素抖动，笔迹点更少、渲染更稳
        if (Math.Abs(p.X - last.X) < 0.8 && Math.Abs(p.Y - last.Y) < 0.8) return;
        _current.Points.Add(p);
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _activePointerId) return;

        _activePointerId = 0;
        Root.ReleasePointerCapture(e.Pointer);
        FinishStroke();
    }

    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        _activePointerId = 0;
        FinishStroke();
    }

    /// <summary>结束当前笔迹；点一下不拖则补一个极短线段，让它显示为圆点。</summary>
    private void FinishStroke()
    {
        if (_current is not null && _current.Points.Count == 1)
        {
            var p = _current.Points[0];
            _current.Points.Add(new Point(p.X + 0.5, p.Y));
        }
        _current = null;
        _erasing = false;
    }

    // ── 橡皮：整条笔迹删除 ──

    private void EraseAt(Point position)
    {
        for (int i = _strokes.Count - 1; i >= 0; i--)
        {
            var stroke = _strokes[i];
            if (!HitStroke(stroke, position)) continue;

            _strokes.RemoveAt(i);
            InkLayer.Children.Remove(stroke);
            _redo.Push(stroke);
        }
    }

    private static bool HitStroke(Polyline stroke, Point p)
    {
        double radius = EraserRadius + stroke.StrokeThickness / 2;
        var points = stroke.Points;

        if (points.Count == 1) return Distance(p, points[0]) <= radius;

        for (int i = 1; i < points.Count; i++)
        {
            if (DistanceToSegment(p, points[i - 1], points[i]) <= radius) return true;
        }
        return false;
    }

    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y;
        double lengthSquared = abx * abx + aby * aby;

        if (lengthSquared <= double.Epsilon) return Distance(p, a);

        double t = ((p.X - a.X) * abx + (p.Y - a.Y) * aby) / lengthSquared;
        t = Math.Clamp(t, 0, 1);

        return Distance(p, new Point(a.X + t * abx, a.Y + t * aby));
    }

    // ── 撤销 / 重做 / 清空（键盘与右键菜单共用） ──

    public void Undo()
    {
        FinishStroke();
        if (_strokes.Count == 0) return;

        var stroke = _strokes[^1];
        _strokes.RemoveAt(_strokes.Count - 1);
        InkLayer.Children.Remove(stroke);
        _redo.Push(stroke);
    }

    public void Redo()
    {
        FinishStroke();
        if (_redo.Count == 0) return;

        var stroke = _redo.Pop();
        _strokes.Add(stroke);
        InkLayer.Children.Add(stroke);
    }

    public void ClearAll()
    {
        FinishStroke();
        // 清空视为开启新的一页：不做撤销
        _strokes.Clear();
        _redo.Clear();
        InkLayer.Children.Clear();
    }

    // ── 快捷键（需窗口为前台，点击画布即可） ──

    private void AddAccelerators()
    {
        AddAccelerator(VirtualKey.Z, VirtualKeyModifiers.Control, Undo);
        AddAccelerator(VirtualKey.Y, VirtualKeyModifiers.Control, Redo);
        AddAccelerator(VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, Redo);
        AddAccelerator(VirtualKey.Delete, VirtualKeyModifiers.Control, ClearAll);
        AddAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None,
            () => ExitRequested?.Invoke());
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            args.Handled = true;
            action();
        };
        Root.KeyboardAccelerators.Add(accelerator);
    }

    // ── 右键菜单：撤销 / 重做 / 清空 ──

    private void ShowActionMenu(Point position)
    {
        var menu = new MenuFlyout();

        var undo = new MenuFlyoutItem { Text = "撤销", IsEnabled = _strokes.Count > 0 };
        undo.Click += (_, _) => Undo();
        menu.Items.Add(undo);

        var redo = new MenuFlyoutItem { Text = "重做", IsEnabled = _redo.Count > 0 };
        redo.Click += (_, _) => Redo();
        menu.Items.Add(redo);

        menu.Items.Add(new MenuFlyoutSeparator());

        var clear = new MenuFlyoutItem { Text = "清空批注", IsEnabled = _strokes.Count > 0 };
        clear.Click += (_, _) => ClearAll();
        menu.Items.Add(clear);

        menu.ShowAt(Root, new FlyoutShowOptions { Position = position });
    }

    // ── 进出场：整体淡入 + 工具栏自上而下渐现 ──

    private void PlayEnter()
    {
        var visual = ElementCompositionPreview.GetElementVisual(Root);
        var comp = visual.Compositor;

        visual.Opacity = 0;
        var fade = comp.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f);
        fade.Duration = TimeSpan.FromMilliseconds(220);
        visual.StartAnimation(nameof(Visual.Opacity), fade);

        // 工具栏：从上方 14px 处滑入，透明度 0 → 1
        // 用 UI 线程 Storyboard（不走合成动画）：窗口未激活时合成动画可能被丢弃，
        // 会把 Card 永久定格在首帧的透明态。Storyboard 只在渲染时推进，更可靠。
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var slide = new TranslateTransform { Y = -14 };
        Card.RenderTransform = slide;

        var storyboard = new Storyboard();
        storyboard.Children.Add(BuildAnimation(Card, "Opacity", 0, 1, 240, ease));
        storyboard.Children.Add(BuildAnimation(slide, "Y", -14, 0, 240, ease));
        storyboard.Begin();
    }

    private static DoubleAnimation BuildAnimation(
        DependencyObject target, string path, double from, double to, double ms,
        EasingFunctionBase ease)
    {
        var animation = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = ease,
            EnableDependentAnimation = true
        };
        Storyboard.SetTarget(animation, target);
        Storyboard.SetTargetProperty(animation, path);
        return animation;
    }

    /// <summary>淡出后执行 onDone（由调用方关闭窗口）。</summary>
    public void PlayExit(Action onDone)
    {
        var visual = ElementCompositionPreview.GetElementVisual(Root);
        var comp = visual.Compositor;

        var anim = comp.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, visual.Opacity);
        anim.InsertKeyFrame(1f, 0f);
        anim.Duration = TimeSpan.FromMilliseconds(140);
        anim.StopBehavior = AnimationStopBehavior.SetToFinalValue;

        var completed = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        completed.Tick += (_, _) =>
        {
            completed.Stop();
            onDone();
        };
        completed.Start();

        visual.StartAnimation(nameof(Visual.Opacity), anim);
    }
}
