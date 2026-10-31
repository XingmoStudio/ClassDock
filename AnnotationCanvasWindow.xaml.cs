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
using System.Diagnostics;
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

/// <summary>笔类型。荧光笔与激光笔暂未开放。</summary>
public enum PenKind
{
    Hard,
    Soft,
    Highlighter,
    Laser
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
    private static readonly SolidColorBrush SwatchBorderBrush
        = new(Color.FromArgb(0x1F, 0x00, 0x00, 0x00));

    private readonly List<InkStroke> _strokes = [];
    private readonly Stack<InkStroke> _redo = [];
    private InkStroke? _current;
    private AnnotationTool _tool = AnnotationTool.Pen;
    private bool _erasing;
    private uint _activePointerId;
    private Color _penColor = PenColor;
    private double _penThickness = PenThickness;
    private PenKind _penKind = PenKind.Hard;

    /// <summary>一条笔迹：渲染元素 + 采样点与每点半宽（橡皮命中、撤销重做、软笔笔锋重建都用它）。</summary>
    private sealed class InkStroke
    {
        public required Shape Visual { get; init; }
        public List<Point> Points { get; } = [];
        public List<double> HalfWidths { get; } = [];
        public List<double> Speeds { get; } = [];   // px/ms，用于判断收笔是否该出锋
        public long LastTimestamp { get; set; }
        public required double BaseThickness { get; init; }
        public required PenKind Kind { get; init; }
    }

    /// <summary>笔类型对应的基准线宽：荧光笔最宽，软笔略宽于硬笔。</summary>
    private double NominalThickness => _penKind switch
    {
        PenKind.Highlighter => _penThickness * 2.8,
        PenKind.Soft => _penThickness * 1.8,
        _ => _penThickness
    };

    private double StrokeOpacity => _penKind == PenKind.Highlighter ? 0.35 : 1;

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
        UpdateThicknessVisuals();
        UpdateSwatchSelection();
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

        if (_tool == tool)
        {
            // 画笔已选中时再点一次 → 弹出/收起画笔设置面板
            if (tool == AnnotationTool.Pen) TogglePenPanel();
            return;
        }
        FinishStroke();
        _tool = tool;
        // 切走画笔时收起设置面板
        if (tool != AnnotationTool.Pen) HidePenPanel();
    }

    // ── 画笔设置面板：颜色 / 粗细 ──

    private const double PanelSlideDistance = 48;
    private bool _penPanelClosing;
    private bool _sliderPinned;
    private bool _sliderHovered;

    private void TogglePenPanel()
    {
        if (PenPanel.Visibility == Visibility.Visible && !_penPanelClosing) HidePenPanel();
        else ShowPenPanel();
    }

    /// <summary>从右侧滑出：面板贴右侧工具栏，向左位移归位并淡入。</summary>
    private void ShowPenPanel()
    {
        _penPanelClosing = false;
        PenPanel.Visibility = Visibility.Visible;
        var slide = new TranslateTransform { X = PanelSlideDistance };
        PenPanel.RenderTransform = slide;
        PenPanel.Opacity = 0;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();
        storyboard.Children.Add(BuildAnimation(slide, "X", PanelSlideDistance, 0, 220, ease));
        storyboard.Children.Add(BuildAnimation(PenPanel, "Opacity", 0, 1, 180, ease));
        storyboard.Begin();
    }

    /// <summary>向右收回后再隐藏，避免面板突然消失。</summary>
    private void HidePenPanel()
    {
        if (PenPanel.Visibility != Visibility.Visible || _penPanelClosing) return;

        _penPanelClosing = true;
        var slide = PenPanel.RenderTransform as TranslateTransform ?? new TranslateTransform();
        PenPanel.RenderTransform = slide;

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var storyboard = new Storyboard();
        storyboard.Children.Add(BuildAnimation(slide, "X", slide.X, PanelSlideDistance, 150, ease));
        storyboard.Children.Add(BuildAnimation(PenPanel, "Opacity", PenPanel.Opacity, 0, 130, ease));
        storyboard.Completed += (_, _) =>
        {
            // 收回过程中又被点开：放弃这次隐藏
            if (!_penPanelClosing) return;
            _penPanelClosing = false;
            storyboard.Stop();
            slide.X = 0;
            PenPanel.Opacity = 1;
            PenPanel.Visibility = Visibility.Collapsed;
        };
        storyboard.Begin();
    }

    private void OnPenColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        _penColor = args.NewColor;
        UpdateSwatchSelection();
    }

    private void OnThicknessChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        _penThickness = e.NewValue;
        UpdateThicknessVisuals();
    }

    /// <summary>粗细演示曲线与“粗细 N”标签跟随当前粗细/笔类型。</summary>
    private void UpdateThicknessVisuals()
    {
        if (ThicknessLabel is null) return;

        ThicknessLabel.Text = $"粗细 {_penThickness:0.#}";

        if (ThicknessPreviewArea.Children.Count > 0
            && ThicknessPreviewArea.Children[0] is Path curve)
        {
            curve.StrokeThickness = Math.Max(1.5, NominalThickness);
        }

        if (ThicknessSlider is not null
            && Math.Abs(ThicknessSlider.Value - _penThickness) > 0.01)
        {
            ThicknessSlider.Value = _penThickness;
        }
    }

    /// <summary>悬停粗细演示曲线时，曲线淡出、自由滑块浮现。</summary>
    private void OnPreviewAreaEntered(object sender, PointerRoutedEventArgs e)
    {
        _sliderHovered = true;
        SetSliderVisible(true);
    }

    private void OnPreviewAreaExited(object sender, PointerRoutedEventArgs e)
    {
        _sliderHovered = false;
        if (!_sliderPinned) SetSliderVisible(false);
    }

    private void SetSliderVisible(bool visible)
    {
        ThicknessSlider.Opacity = visible ? 1 : 0;
        ThicknessSlider.IsHitTestVisible = visible;
        if (ThicknessPreviewArea.Children[0] is Path curve)
        {
            curve.Opacity = visible ? 0 : 1;
        }
    }

    private void OnPinSliderClick(object sender, RoutedEventArgs e)
    {
        _sliderPinned = !_sliderPinned;
        SetSliderVisible(_sliderPinned || _sliderHovered);
    }

    /// <summary>粗细档位圆点：点击即切到对应粗细。</summary>
    private void OnThicknessPresetPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border { Tag: string tag } && double.TryParse(tag, out double value))
        {
            ThicknessSlider.Value = value;
            e.Handled = true;
        }
    }

    private ColorPicker? _colorPicker;
    private Flyout? _colorFlyout;

    private void OnColorWheelPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_colorFlyout is null)
        {
            var host = new Viewbox { Width = 220, Height = 220, Child = EnsureColorPicker() };
            _colorFlyout = new Flyout
            {
                Placement = FlyoutPlacementMode.Top,
                Content = host
            };
        }
        _colorFlyout.ShowAt(ColorWheelSwatch);
        // 模板首次应用后才吃 ColorSpectrumShape，打开后再赋一次确保是圆环
        var picker = _colorPicker!;
        DispatcherQueue.TryEnqueue(() => picker.ColorSpectrumShape = ColorSpectrumShape.Ring);
        e.Handled = true;
    }

    /// <summary>按需创建调色盘。XAML 内联 Flyout/ColorPicker 会让 XamlCompiler 静默崩溃，
    /// 且 ColorSpectrumShape 必须在代码里赋值才生效。</summary>
    private ColorPicker EnsureColorPicker()
    {
        if (_colorPicker is not null) return _colorPicker;

        var picker = new ColorPicker
        {
            Width = 384,
            Height = 384,
            MinWidth = 0,
            MinHeight = 0,
            IsAlphaEnabled = false,
            IsColorChannelTextInputVisible = false,
            IsColorPreviewVisible = false,
            IsColorSliderVisible = false,
            IsHexInputVisible = false,
            ColorSpectrumShape = ColorSpectrumShape.Ring,
            Color = _penColor
        };
        picker.ColorChanged += OnPenColorChanged;
        _colorPicker = picker;
        return picker;
    }

    private void OnSwatchPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border { Tag: string hex })
        {
            _penColor = ParseHex(hex);
            if (_colorPicker is not null) _colorPicker.Color = _penColor;
            UpdateSwatchSelection();
            e.Handled = true;
        }
    }

    private static Color ParseHex(string hex)
    {
        string digits = hex.TrimStart('#');
        if (digits.Length == 6) digits = "FF" + digits;
        uint value = Convert.ToUInt32(digits, 16);
        return Color.FromArgb(
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    /// <summary>色块选中态：与当前画笔颜色相同的格子显示强调边框。</summary>
    private void UpdateSwatchSelection()
    {
        foreach (var child in SwatchGrid.Children)
        {
            if (child is not Border { Tag: string hex } border) continue;
            bool selected = ParseHex(hex).Equals(_penColor);
            border.BorderThickness = new Thickness(selected ? 2 : 1);
            border.BorderBrush = selected ? AccentBrush : SwatchBorderBrush;
        }
    }

    /// <summary>笔类型单选：硬笔 / 软笔 / 荧光笔可用，激光笔暂未开放。</summary>
    private void OnPenKindClick(object sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender;
        HardPenButton.IsChecked = ReferenceEquals(clicked, HardPenButton);
        SoftPenButton.IsChecked = ReferenceEquals(clicked, SoftPenButton);
        HighlighterButton.IsChecked = ReferenceEquals(clicked, HighlighterButton);

        _penKind = ReferenceEquals(clicked, SoftPenButton) ? PenKind.Soft
            : ReferenceEquals(clicked, HighlighterButton) ? PenKind.Highlighter
            : PenKind.Hard;
        UpdateThicknessVisuals();
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
                _current = BeginStroke(position);
                break;

            // 几何图形 / 尺规：本阶段占位，不响应绘制
        }
    }

    /// <summary>新建一条笔迹。软笔用填充多边形（宽度逐点变化形成笔锋），
    /// 硬笔/荧光笔用等宽折线。</summary>
    private InkStroke BeginStroke(Point p)
    {
        var brush = new SolidColorBrush(_penColor);
        Shape visual;

        if (_penKind == PenKind.Soft)
        {
            visual = new Path { Fill = brush, IsHitTestVisible = false };
        }
        else
        {
            visual = new Polyline
            {
                Stroke = brush,
                StrokeThickness = NominalThickness,
                Opacity = StrokeOpacity,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = _penKind == PenKind.Highlighter ? PenLineCap.Flat : PenLineCap.Round,
                StrokeEndLineCap = _penKind == PenKind.Highlighter ? PenLineCap.Flat : PenLineCap.Round,
                IsHitTestVisible = false
            };
        }

        var stroke = new InkStroke
        {
            Visual = visual,
            BaseThickness = NominalThickness,
            Kind = _penKind
        };
        stroke.Points.Add(p);
        stroke.HalfWidths.Add(stroke.BaseThickness / 2 * (stroke.Kind == PenKind.Soft ? 0.6 : 1));
        stroke.Speeds.Add(0.2);
        stroke.LastTimestamp = Stopwatch.GetTimestamp();

        if (visual is Polyline line)
        {
            line.Points.Add(p);
        }
        else
        {
            RebuildSoftInk(stroke);
        }

        InkLayer.Children.Add(visual);
        _strokes.Add(stroke);
        _redo.Clear();
        return stroke;
    }

    /// <summary>追加一个采样点。软笔按“点间距≈笔速”估算笔压：行笔快则细、慢则粗。</summary>
    private static void AppendStrokePoint(InkStroke stroke, Point p)
    {
        if (stroke.Kind == PenKind.Soft)
        {
            double segment = Distance(p, stroke.Points[^1]);

            // 用采样间隔估算速度（px/ms），比只看点间距更可靠
            long now = Stopwatch.GetTimestamp();
            double dtMs = (now - stroke.LastTimestamp) * 1000.0 / Stopwatch.Frequency;
            stroke.LastTimestamp = now;
            double speed = dtMs > 0.2 ? segment / dtMs : 0.2;
            stroke.Speeds.Add(speed);

            double baseHalf = stroke.BaseThickness / 2;
            // 慢写 ≈ 1.05（粗），快写 ≈ 0.3（细）
            double targetHalf = baseHalf * Math.Clamp(1.05 - speed * 0.5, 0.3, 1.05);

            // 起笔前两点从细到粗，模拟落笔
            int index = stroke.Points.Count;
            if (index < 3) targetHalf *= 0.6 + index * 0.15;

            // 指数平滑 + 限制相邻点宽度突变，避免轮廓自交导致笔迹破碎
            double previousHalf = stroke.HalfWidths[^1];
            double maxStep = baseHalf * 0.15;
            double half = previousHalf * 0.6 + targetHalf * 0.4;
            half = Math.Clamp(half, previousHalf - maxStep, previousHalf + maxStep);
            half = Math.Clamp(half, baseHalf * 0.2, baseHalf * 1.1);

            stroke.HalfWidths.Add(half);
        }

        stroke.Points.Add(p);

        if (stroke.Visual is Polyline line)
        {
            line.Points.Add(p);
        }
        else
        {
            RebuildSoftInk(stroke);
        }
    }

    /// <summary>按各点半宽生成左右边界，拼成闭合轮廓填充（笔锋即来自宽度差异）。
    /// 用 PathGeometry + Nonzero 填充规则：轮廓自交处仍保持实心，
    /// 避免多边形默认偶数填充在急转角处出现“破碎”空洞。</summary>
    private static void RebuildSoftInk(InkStroke stroke)
    {
        var path = (Path)stroke.Visual;
        var points = stroke.Points;
        var outline = new List<Point>();

        if (points.Count == 1)
        {
            double r = Math.Max(1.2, stroke.HalfWidths[0]);
            var p = points[0];
            outline.Add(new Point(p.X - r, p.Y));
            outline.Add(new Point(p.X, p.Y - r));
            outline.Add(new Point(p.X + r, p.Y));
            outline.Add(new Point(p.X, p.Y + r));
            path.Data = BuildOutlineGeometry(outline);
            return;
        }

        int n = points.Count;
        var left = new List<Point>(n);
        var right = new List<Point>(n);

        for (int i = 0; i < n; i++)
        {
            var prev = points[i > 0 ? i - 1 : 0];
            var next = points[i < n - 1 ? i + 1 : n - 1];
            var normal = Normal(prev, next);
            double half = stroke.HalfWidths[i];
            left.Add(new Point(points[i].X + normal.X * half, points[i].Y + normal.Y * half));
            right.Add(new Point(points[i].X - normal.X * half, points[i].Y - normal.Y * half));
        }

        double startHalf = stroke.HalfWidths[0];
        double endHalf = stroke.HalfWidths[n - 1];

        // 起点圆帽：从左侧边界绕后方到右侧边界，避免平头被看成“截断”
        if (startHalf > 0.6)
        {
            var p0 = points[0];
            var dir = Unit(points[1], points[0]);       // 指向笔画后方
            var normal = new Point(-dir.Y, dir.X);
            for (int k = 0; k < 6; k++)
            {
                double angle = Math.PI * k / 6;
                double cx = Math.Cos(angle), sx = Math.Sin(angle);
                outline.Add(new Point(
                    p0.X + normal.X * startHalf * cx + dir.X * startHalf * sx,
                    p0.Y + normal.Y * startHalf * cx + dir.Y * startHalf * sx));
            }
        }

        for (int i = 0; i < n; i++) outline.Add(right[i]);

        // 终点圆帽：从右侧边界经前方绕到左侧边界
        if (endHalf > 0.6)
        {
            var pe = points[^1];
            var dir = Unit(points[^2], points[^1]);     // 指向笔画前方
            var normal = new Point(-dir.Y, dir.X);
            for (int k = 0; k < 6; k++)
            {
                double angle = Math.PI * k / 6;
                double cx = Math.Cos(angle), sx = Math.Sin(angle);
                outline.Add(new Point(
                    pe.X - normal.X * endHalf * cx + dir.X * endHalf * sx,
                    pe.Y - normal.Y * endHalf * cx + dir.Y * endHalf * sx));
            }
        }

        for (int i = n - 1; i >= 0; i--) outline.Add(left[i]);
        path.Data = BuildOutlineGeometry(outline);
    }

    private static PathGeometry BuildOutlineGeometry(List<Point> outline)
    {
        var figure = new PathFigure
        {
            StartPoint = outline[0],
            IsClosed = true,
            IsFilled = true
        };
        var segment = new PolyLineSegment();
        for (int i = 1; i < outline.Count; i++) segment.Points.Add(outline[i]);
        figure.Segments.Add(segment);

        var geometry = new PathGeometry { FillRule = FillRule.Nonzero };
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Point Unit(Point from, Point to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        return length < 0.0001 ? new Point(1, 0) : new Point(dx / length, dy / length);
    }

    private static Point Normal(Point a, Point b)
    {
        var dir = Unit(a, b);
        return new Point(-dir.Y, dir.X);
    }

    /// <summary>收笔后对整条笔迹的宽度做两轮平滑，抹掉采样噪声造成的粗细抖动。</summary>
    private static void SmoothHalfWidths(InkStroke stroke)
    {
        var widths = stroke.HalfWidths;
        if (widths.Count < 3) return;

        for (int pass = 0; pass < 2; pass++)
        {
            var snapshot = new List<double>(widths);
            for (int i = 1; i < widths.Count - 1; i++)
            {
                widths[i] = (snapshot[i - 1] + snapshot[i] * 2 + snapshot[i + 1]) / 4;
            }
        }
    }

    /// <summary>收笔出锋：只有末段书写速度够快才出尖（类似提笔撇出），
    /// 慢速收笔保持圆润收尾；收窄沿弧长平滑过渡，避免突兀截断。</summary>
    private static void TaperSoftInkEnd(InkStroke stroke)
    {
        var points = stroke.Points;
        var halfWidths = stroke.HalfWidths;
        int n = Math.Min(points.Count, halfWidths.Count);
        if (n < 3) return;

        double speed = EndSpeed(stroke.Speeds);
        if (speed < 0.9) return;                       // 慢速提笔：不出锋

        double strength = Math.Clamp((speed - 0.9) / 2.5, 0.0, 1.0);
        double taperLength = 10 + 14 * strength;       // 出锋长度随速度增加
        double baseHalf = stroke.BaseThickness / 2;
        double floor = baseHalf * 0.06;

        double accumulated = 0;
        for (int i = n - 1; i >= 1; i--)
        {
            accumulated += Distance(points[i], points[i - 1]);
            double factor = Math.Clamp(accumulated / taperLength, 0.0, 1.0);
            // 平方曲线让末端收得更快，但整体仍连续
            double scaled = halfWidths[i] * factor * factor;
            halfWidths[i] = Math.Max(floor, Math.Min(halfWidths[i], scaled));
            if (accumulated > taperLength) break;
        }

        // 顺行笔方向补一个极细笔尖
        var last = points[^1];
        var prev = points[^2];
        double dx = last.X - prev.X, dy = last.Y - prev.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.0001) return;

        double extension = 1.5 + 4 * strength;
        points.Add(new Point(last.X + dx / length * extension, last.Y + dy / length * extension));
        halfWidths.Add(floor);
    }

    /// <summary>取末段平均速度（px/ms）。</summary>
    private static double EndSpeed(List<double> speeds)
    {
        if (speeds.Count == 0) return 0;
        int take = Math.Min(3, speeds.Count);
        double sum = 0;
        for (int i = speeds.Count - take; i < speeds.Count; i++) sum += speeds[i];
        return sum / take;
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
        // 过滤抖动：采样点间距过小会让法线方向乱跳，轮廓更容易自交
        if (Math.Abs(p.X - last.X) < 1.2 && Math.Abs(p.Y - last.Y) < 1.2) return;
        AppendStrokePoint(_current, p);
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

    /// <summary>结束当前笔迹；点一下不拖则补一个极短线段，让它显示为圆点。软笔此时收笔出锋。</summary>
    private void FinishStroke()
    {
        if (_current is not null)
        {
            if (_current.Points.Count == 1)
            {
                var p = _current.Points[0];
                var p2 = new Point(p.X + 0.5, p.Y);
                _current.Points.Add(p2);
                _current.HalfWidths.Add(_current.HalfWidths.Count > 0 ? _current.HalfWidths[^1] : 1);
                if (_current.Visual is Polyline line) line.Points.Add(p2);
            }

            if (_current.Kind == PenKind.Soft)
            {
                // 书写期间只画平滑的等宽变化；收笔后再整体平滑并生成笔锋
                SmoothHalfWidths(_current);
                TaperSoftInkEnd(_current);
                RebuildSoftInk(_current);
            }
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
            InkLayer.Children.Remove(stroke.Visual);
            _redo.Push(stroke);
        }
    }

    private static bool HitStroke(InkStroke stroke, Point p)
    {
        double radius = EraserRadius + stroke.BaseThickness / 2;
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
        InkLayer.Children.Remove(stroke.Visual);
        _redo.Push(stroke);
    }

    public void Redo()
    {
        FinishStroke();
        if (_redo.Count == 0) return;

        var stroke = _redo.Pop();
        _strokes.Add(stroke);
        InkLayer.Children.Add(stroke.Visual);
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
