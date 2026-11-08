using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace ClassDock;

/// <summary>批注工具。尺规选中即弹出工具列表面板，绘制行为本阶段仍为占位。</summary>
public enum AnnotationTool
{
    Pen,
    Shapes,
    Eraser,
    Ruler
}

/// <summary>笔类型。激光笔暂未开放。</summary>
public enum PenKind
{
    Hard,
    Soft,
    Highlighter,
    Laser
}

/// <summary>几何图形类型。None 表示这是一条普通笔迹而非图形。</summary>
public enum ShapeKind
{
    None,
    Line,
    Arrow,
    DashedArrow,
    DashedLine,
    Rectangle,
    RoundedRectangle,
    Triangle,
    RightTriangle,
    Circle,
    Ellipse
}

/// <summary>
/// 全屏批注画布：置顶、无边框，底层铺一张进入批注时的屏幕快照，
/// 笔迹用 Pointer 事件 + Polyline 自绘（WinUI 3 无 InkCanvas）；
/// 撤销/重做按编辑动作入栈；橡皮按面积擦除，被擦到的笔迹截断成多段保留。
/// </summary>
public sealed partial class AnnotationCanvasWindow : Window
{
    private const double PenThickness = 3;

    /// <summary>橡皮擦除半径，由橡皮面板的三档“大小”调整。</summary>
    private double _eraserRadius = 12;

    /// <summary>默认画笔颜色（红色）。</summary>
    private static readonly Color PenColor = Color.FromArgb(0xFF, 0xE8, 0x11, 0x23);

    /// <summary>强调色画刷均为共享实例：RefreshAccent 只改 Color 即全局生效，无需重建引用。</summary>
    private static readonly SolidColorBrush AccentBrush
        = new(PenColor);
    private static readonly SolidColorBrush NormalBrush     // 未选中图标/文字的默认色
        = new(Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B));
    private static readonly SolidColorBrush SwatchBorderBrush // 色块默认边框（半透明黑）
        = new(Color.FromArgb(0x1F, 0x00, 0x00, 0x00));

    /// <summary>批注窗的强调色固定跟随当前画笔颜色，不取全局应用强调色。
    /// 非画笔工具（橡皮、图形等）不改变笔色，因此沿用最近一次笔色，不会突然跳回全局强调色或变成黑色。</summary>
    private Color AccentColor => ReadableAccent(_penColor);

    // 工具按钮选中态的底色画刷（取自 XAML 资源，共享实例）
    private readonly SolidColorBrush _toolCheckedBrush;
    private readonly SolidColorBrush _toolCheckedHoverBrush;
    private readonly SolidColorBrush _toolCheckedPressedBrush;
    // 工具按钮悬停/按下蒙层（取自 XAML 资源，随明暗切换改色）
    private readonly SolidColorBrush _toolHoverBrush;
    private readonly SolidColorBrush _toolPressedBrush;

    // 笔迹集合与撤销/重做栈
    private readonly List<InkStroke> _strokes = [];
    private readonly Stack<EditAction> _undo = [];
    private readonly Stack<EditAction> _redo = [];

    /// <summary>本次橡皮拖动中被整体移除的原笔迹（撤销时还原）。</summary>
    private readonly List<InkStroke> _eraseRemoved = [];

    /// <summary>本次橡皮拖动中切割出的最终片段（撤销时移除）。</summary>
    private readonly List<InkStroke> _eraseFragments = [];
    // 被擦笔迹/切出片段各自在 _strokes 中的原始下标，撤销重做时按原位恢复层级
    private readonly Dictionary<InkStroke, int> _eraseRemovedIndex = [];
    private readonly Dictionary<InkStroke, int> _eraseFragmentIndex = [];

    // 当前绘制状态
    private InkStroke? _current;        // 正在绘制的笔迹
    private InkStroke? _shapeDraft;     // 几何图形预览草稿（松手时提交或丢弃）
    private Point _shapeStart;          // 图形起始点
    private AnnotationTool _tool = AnnotationTool.Pen; // 当前选中工具
    private bool _erasing;              // 橡皮拖动中
    private uint _activePointerId;      // 活动指针 ID，过滤其它指针的事件

    // 笔速橡皮（实验）：擦除半径随指针移动速度动态变化（有上下限）
    private double _eraseRadiusEffective = 12;
    private Point _eraseLastPoint;
    private long _eraseLastTimestamp;

    // 画笔参数
    private Color _penColor = PenColor;
    private double _penThickness = PenThickness;
    private PenKind _penKind = PenKind.Hard;

    // 图形参数
    private ShapeKind _shapeKind = ShapeKind.Line;
    private double _shapeThickness = 3;

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
        public required Color Color { get; init; }

        /// <summary>图形类型；普通笔迹为 None。虚线类图形擦断后的片段靠它保持虚线样式。</summary>
        public ShapeKind Shape { get; init; } = ShapeKind.None;
    }

    /// <summary>一次可撤销的编辑，撤销与重做各自维护画布状态。</summary>
    private sealed class EditAction(Action undo, Action redo)
    {
        public void Undo() => undo();
        public void Redo() => redo();
    }

    /// <summary>笔类型对应的基准线宽：荧光笔最宽，软笔略宽于硬笔。</summary>
    private double NominalThickness => _penKind switch
    {
        PenKind.Highlighter => _penThickness * 2.8,
        PenKind.Soft => _penThickness * 1.8,
        _ => _penThickness
    };

    /// <summary>笔迹透明度：荧光笔半透明，其余笔类型不透明。</summary>
    private double StrokeOpacity => _penKind == PenKind.Highlighter ? 0.35 : 1;

    /// <summary>窗口的原生句柄（HWND）。</summary>
    public IntPtr Hwnd { get; }

    /// <summary>按 Esc 请求退出批注（由 BarWindow 负责收尾）。</summary>
    public event Action? ExitRequested;

    /// <summary>初始化批注画布窗口：配置窗口样式、加载画刷资源、刷新 UI 状态并注册快捷键。</summary>
    public AnnotationCanvasWindow()
    {
        InitializeComponent();
        App.ApplyWindowIcon(AppWindow);
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
        _toolCheckedBrush = (SolidColorBrush)Root.Resources["ToolCheckedBrush"];
        _toolCheckedHoverBrush = (SolidColorBrush)Root.Resources["ToolCheckedHoverBrush"];
        _toolCheckedPressedBrush = (SolidColorBrush)Root.Resources["ToolCheckedPressedBrush"];
        _toolHoverBrush = (SolidColorBrush)Root.Resources["ToolHoverBrush"];
        _toolPressedBrush = (SolidColorBrush)Root.Resources["ToolPressedBrush"];
        // 记忆退出前的颜色：开启时沿用上次笔色，否则每次从默认红开始
        if (AppSettings.RememberPenColor)
            _penColor = FromArgbInt(AppSettings.LastPenColorArgb);
        // 批注窗面板跟随应用明暗；强调色跟画笔颜色，不跟随全局应用强调色
        AppTheme.Apply(Root);
        ApplyAnnotTheme();
        RefreshAccent();
        IconOf(PenButton).Fill = AccentBrush;
        AppTheme.Changed += OnAppThemeChanged;
        Closed += (_, _) =>
        {
            AppTheme.Changed -= OnAppThemeChanged;
            AppTheme.Unapply(Root);
            if (AppSettings.RememberPenColor) AppSettings.Save();
        };
        UpdateThicknessVisuals();
        UpdateSwatchSelection();
        UpdateEraserSizeVisuals();
        UpdateShapeOptionVisuals();
        UpdateShapeThicknessVisuals();
        AddAccelerators();

        // 尺规手势统一在尺规图层上处理：图层常驻，重建尺身时指针捕获不会丢失
        RulerLayer.PointerMoved += OnRulerPointerMoved;
        RulerLayer.PointerReleased += OnRulerPointerReleased;
        RulerLayer.PointerCaptureLost += OnRulerPointerCaptureLost;
        RulerLayer.PointerWheelChanged += OnRulerPointerWheelChanged;
    }

    /// <summary>按钮内容 = Viewbox + Path，取出其中的 Path 以便切换选中态颜色。
    /// （Path 上不能写 x:Name——XamlCompiler 会静默崩溃；也不支持 AncestorType 绑定。）</summary>
    private static Path IconOf(ButtonBase button) => (Path)((Viewbox)button.Content).Child;

    // ── 橡皮贴图：跟随指针移动，压在最上层，不参与命中测试；只在绘制区域显示 ──

    /// <summary>橡皮贴图边长随擦除半径缩放，让擦除范围可见（笔速橡皮时跟随实时半径）。</summary>
    private double EraserVisualSize => Math.Max(24, (_erasing ? _eraseRadiusEffective : _eraserRadius) * 2 + 8);

    /// <summary>橡皮工具下让贴图跟随指针（贴图中心对齐指针，避免偏移造成擦除手感偏差）。
    /// 指针移到笔设置栏或工具栏时收起，绘制区域之外不显示橡皮。</summary>
    private void UpdateEraserVisual(Point position, object? source)
    {
        if (_tool != AnnotationTool.Eraser || IsOverUiPanel(source))
        {
            HideEraserVisual();
            return;
        }

        double size = EraserVisualSize;
        EraserImage.Width = size;
        EraserImage.Height = size;
        Canvas.SetLeft(EraserImage, position.X - size / 2);
        Canvas.SetTop(EraserImage, position.Y - size / 2);
        EraserImage.Visibility = Visibility.Visible;
    }

    /// <summary>隐藏橡皮擦贴图。</summary>
    private void HideEraserVisual() => EraserImage.Visibility = Visibility.Collapsed;

    /// <summary>沿可视树向上找命名容器，判断指针是否落在笔设置栏 / 工具栏上。</summary>
    private static bool IsOverUiPanel(object? source)
    {
        var element = source as DependencyObject;
        while (element is not null)
        {
            if (element is FrameworkElement { Name: "PenPanel" or "EraserPanel" or "ShapesPanel" or "ToolPanel" or "Card" }) return true;
            element = VisualTreeHelper.GetParent(element);
        }
        return false;
    }

    /// <summary>铺满主屏（物理像素）并显示；不激活，避免打断前台内容。</summary>
    public void ShowFullscreen(int screenWpx, int screenHpx)
    {
        AppWindow.MoveAndResize(new RectInt32(0, 0, screenWpx, screenHpx));
        AppWindow.Show(activateWindow: false);
    }

    /// <summary>设置背景快照（铺满整个窗口）。</summary>
    public void SetSnapshot(ImageSource snapshot) => Snapshot.Source = snapshot;

    // ── 工具切换：单选，选中态用主题蓝 ──

    /// <summary>工具栏按钮点击：切换工具或弹出/收起已选工具的设置面板。</summary>
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
            // 该工具已选中时再点一次 → 弹出/收起对应的设置面板
            if (tool == AnnotationTool.Pen) TogglePenPanel();
            else if (tool == AnnotationTool.Eraser) ToggleEraserPanel();
            else if (tool == AnnotationTool.Shapes) ToggleShapesPanel();
            else if (tool == AnnotationTool.Ruler) ToggleToolPanel();
            return;
        }
        FinishStroke();
        _tool = tool;
        HideEraserVisual();
        // 切走对应工具时收起它的设置面板
        if (tool != AnnotationTool.Pen) HidePenPanel();
        if (tool != AnnotationTool.Eraser) HideEraserPanel();
        if (tool != AnnotationTool.Shapes) HideShapesPanel();
        if (tool != AnnotationTool.Ruler) HideToolPanel();
        // 工具：选中即弹出面板，无需再点一次
        if (tool == AnnotationTool.Ruler) ShowSlidePanel(ToolPanel);
    }

    // ── 设置面板（画笔 / 橡皮 / 几何图形）：都从右侧工具栏向左滑入，再向右收起 ──

    private const double PanelSlideDistance = 48;
    private readonly HashSet<Border> _closingPanels = [];
    private bool _sliderPinned;
    private bool _sliderHovered;

    /// <summary>切换画笔设置面板的显示/隐藏。</summary>
    private void TogglePenPanel()
    {
        if (PenPanel.Visibility == Visibility.Visible && !_closingPanels.Contains(PenPanel)) HidePenPanel();
        else ShowSlidePanel(PenPanel);
    }

    /// <summary>隐藏画笔设置面板。</summary>
    private void HidePenPanel() => HideSlidePanel(PenPanel);

    /// <summary>切换橡皮设置面板的显示/隐藏。</summary>
    private void ToggleEraserPanel()
    {
        if (EraserPanel.Visibility == Visibility.Visible && !_closingPanels.Contains(EraserPanel)) HideEraserPanel();
        else ShowSlidePanel(EraserPanel);
    }

    /// <summary>隐藏橡皮设置面板。</summary>
    private void HideEraserPanel() => HideSlidePanel(EraserPanel);

    /// <summary>切换几何图形设置面板的显示/隐藏。</summary>
    private void ToggleShapesPanel()
    {
        if (ShapesPanel.Visibility == Visibility.Visible && !_closingPanels.Contains(ShapesPanel)) HideShapesPanel();
        else ShowSlidePanel(ShapesPanel);
    }

    /// <summary>隐藏几何图形设置面板。</summary>
    private void HideShapesPanel() => HideSlidePanel(ShapesPanel);

    /// <summary>切换工具面板的显示/隐藏。</summary>
    private void ToggleToolPanel()
    {
        if (ToolPanel.Visibility == Visibility.Visible && !_closingPanels.Contains(ToolPanel)) HideToolPanel();
        else ShowSlidePanel(ToolPanel);
    }

    /// <summary>隐藏工具面板。</summary>
    private void HideToolPanel() => HideSlidePanel(ToolPanel);

    // ── 工具：保存至本地 ──

    private bool _savingToLocal;

    /// <summary>「保存至本地」：把进入批注时的快照底图与画布笔迹合成后写入用户选择的文件。</summary>
    private async void OnSaveToLocalPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_savingToLocal) return;
        _savingToLocal = true;
        try
        {
            e.Handled = true;
            await SaveCanvasToLocalAsync();
        }
        catch (Exception ex)
        {
            await new ContentDialog
            {
                Title = "保存失败",
                Content = ex.Message,
                CloseButtonText = "确定",
                XamlRoot = Root.XamlRoot
            }.ShowAsync();
        }
        finally
        {
            _savingToLocal = false;
        }
    }

    /// <summary>选择保存位置（WinUI 3 的现代保存对话框），默认 JPG，合成快照与笔迹后编码写出。</summary>
    private async Task SaveCanvasToLocalAsync()
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = "ClassDock批注_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"),
            SuggestedStartLocation = PickerLocationId.PicturesLibrary,
            DefaultFileExtension = ".jpg"
        };
        picker.FileTypeChoices.Add("JPEG 图片", [".jpg", ".jpeg"]);
        picker.FileTypeChoices.Add("PNG 图片", [".png"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null) return;

        // 底图：优先复用进入批注时的快照，失败则现截一张，再失败退化为纯黑底
        var snapshot = Snapshot.Source as WriteableBitmap ?? ScreenSnapshot.Capture();
        int width, height;
        byte[] basePixels;
        if (snapshot is not null)
        {
            width = snapshot.PixelWidth;
            height = snapshot.PixelHeight;
            basePixels = new byte[width * height * 4];
            using var pixelStream = snapshot.PixelBuffer.AsStream();
            pixelStream.ReadExactly(basePixels);
        }
        else
        {
            width = AppWindow.Size.Width;
            height = AppWindow.Size.Height;
            basePixels = new byte[width * height * 4];
            for (int i = 3; i < basePixels.Length; i += 4) basePixels[i] = 0xFF;
        }

        // 笔迹：把 InkLayer 直接渲染到与底图相同的像素尺寸（自动处理 DPI 缩放）
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(InkLayer, width, height);
        var inkPixels = (await rtb.GetPixelsAsync()).ToArray(); // BGRA 预乘 alpha

        // 合成：预乘 alpha 的笔迹叠在不透明底图上，out = src + dst * (1 - srcA)
        if (inkPixels.Length == basePixels.Length)
        {
            for (int i = 0; i < basePixels.Length; i += 4)
            {
                int inv = 255 - inkPixels[i + 3];
                if (inv == 255) continue;
                basePixels[i] = (byte)(inkPixels[i] + (basePixels[i] * inv + 127) / 255);
                basePixels[i + 1] = (byte)(inkPixels[i + 1] + (basePixels[i + 1] * inv + 127) / 255);
                basePixels[i + 2] = (byte)(inkPixels[i + 2] + (basePixels[i + 2] * inv + 127) / 255);
            }
        }

        bool isPng = file.FileType.Equals(".png", StringComparison.OrdinalIgnoreCase);
        using var fileStream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(
            isPng ? BitmapEncoder.PngEncoderId : BitmapEncoder.JpegEncoderId, fileStream);
        if (!isPng)
        {
            var quality = new BitmapPropertySet
            {
                { "ImageQuality", new BitmapTypedValue(0.9f, PropertyType.Single) }
            };
            await encoder.BitmapProperties.SetPropertiesAsync(quality);
        }
        double dpi = 96.0 * (Root.XamlRoot?.RasterizationScale ?? 1.0);
        encoder.SetPixelData(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            (uint)width, (uint)height, dpi, dpi, basePixels);
        await encoder.FlushAsync();
    }

    // ── 工具：屏幕刻度尺 ──

    /// <summary>1 毫米对应的逻辑像素数（1 逻辑英寸恒为 96 逻辑像素，与 DPI 缩放无关）。</summary>
    private const double MmToLogicalPx = 96.0 / 25.4;

    private const double RulerCmPx = 10 * MmToLogicalPx;   // 1 厘米对应的逻辑像素
    private const double RulerEndMargin = 10;               // 刻度起点距尺身两端的留白
    private const double RulerBaseHeight = 100;             // 尺身原始高度（缩放前）
    private const double RulerTickBandHeight = 32;          // 顶部刻度带高度
    private const int RulerMinCm = 15;                      // 最短刻度长度（含两端留白约 15.5cm）
    private const double RulerWheelAngleStep = 5;           // 滚轮一格旋转角度

    private static readonly double[] RulerZoomSteps = [1.0, 1.25, 1.5];

    /// <summary>尺规图层上的手势：直尺的拖动/旋转/调长度，圆规的拖动/调开口/画弧。</summary>
    private enum RulerGesture { None, Drag, Rotate, Resize, CompassDrag, CompassResize, CompassDraw }

    private Border? _screenRuler;
    private int _rulerCmCount = RulerMinCm;
    private int _rulerZoomIndex;
    private double _rulerAngle;       // 当前旋转角度（度，顺时针为正）

    private RulerGesture _rulerGesture = RulerGesture.None;
    private Point _gestureStart;      // 手势起点（尺规图层坐标）
    private double _gestureStartLeft;
    private double _gestureStartTop;
    private int _gestureStartCm;
    private double _gestureStartHalfAngle; // 圆规调开口手势起始的半角（度）

    // ---- 尺边吸附画直线 ----
    private const double RulerSnapTolerance = 14; // 距刻度边多少逻辑像素内触发吸附
    private bool _rulerSnapActive;      // 本次笔画是否为贴尺边画直线
    private Point _snapStart;           // 吸附起点（画布坐标，已投影到尺边上）
    private Point _snapEdgeStart;       // 尺边左端（画布坐标）
    private Point _snapEdgeDir;         // 尺边方向单位向量
    private double _snapEdgeLen;        // 尺边长度
    private Point _snapOutward;         // 尺边外法线方向（读数标签侧）
    private TextBlock? _snapLabel;      // 实时长度读数（厘米，两位小数）
    private double _lastPointerAngle; // 旋转手势中上一次指针相对尺心的角度
    private bool _rotateMoved;        // 旋转手势是否已真正移动（用于区分单击）

    // ---- 量角器 / 三角板：可同时存在多只的独立量具 ----
    private const int MeasureToolMaxPerKind = 3;
    private readonly List<MeasureTool> _measureTools = [];

    // ---- 圆规 ----
    private const double CompassLegLength = 380;        // 铰链到尖端的长度（逻辑像素）
    private const double CompassPivotTop = 118;         // 铰链中心距容器顶部（上方容纳关闭按钮与捏柄）
    private const double CompassMinHalfAngle = 6;       // 最小开口半角（度）
    private const double CompassMaxHalfAngle = 30;      // 最大开口半角（度）
    private const double CompassDefaultHalfAngle = 15;
    private const double CompassDrawStepDegrees = 3;    // 画弧采样步进（度），快速旋转时补点保证弧线平滑

    private Canvas? _compass;
    private Polyline? _compassRadiusLine;   // 针尖与笔尖之间的虚连线
    private TextBlock? _compassRadiusLabel; // 半径读数
    private double _compassHalfAngle = CompassDefaultHalfAngle;
    private double _compassAngle;           // 圆规整体绕针尖的旋转角（度，顺时针为正）
    private Point _compassNeedle;           // 针尖在画布坐标中的位置（圆心）
    private InkStroke? _compassStroke;      // 画弧期间的活动笔迹
    private double _compassLastPointerAngle;

    /// <summary>「屏幕刻度尺」：在画布中央召唤/收回一把直尺。</summary>
    private void OnScreenRulerPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (_screenRuler is not null)
        {
            HideScreenRuler();
            return;
        }
        _screenRuler = BuildScreenRuler();
        RulerLayer.Children.Add(_screenRuler);
        ApplyRulerTransform();
        CenterRuler();
        ScreenRulerOption.Background = ShapeActiveBrush;
    }

    private void HideScreenRuler()
    {
        if (_screenRuler is null) return;
        RulerLayer.Children.Remove(_screenRuler);
        _screenRuler = null;
        // 收起时复位形态，下次召唤回到默认的 15cm 横放 100%
        _rulerZoomIndex = 0;
        _rulerAngle = 0;
        _rulerCmCount = RulerMinCm;
        _rulerGesture = RulerGesture.None;
        ScreenRulerOption.Background = TransparentBrush;
    }

    /// <summary>关闭按钮：收起直尺。</summary>
    private void OnRulerClosePressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        HideScreenRuler();
    }

    /// <summary>缩放按钮：在 100% → 125% → 150% 之间循环，围绕尺心缩放不改变位置。</summary>
    private void OnRulerZoomPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        _rulerZoomIndex = (_rulerZoomIndex + 1) % RulerZoomSteps.Length;
        ApplyRulerTransform();
    }

    /// <summary>旋转按钮：按住拖动可自由旋转，单击则顺时针转 90°。</summary>
    private void OnRulerRotatePressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (_screenRuler is null) return;
        _gestureStart = e.GetCurrentPoint(RulerLayer).Position;
        _lastPointerAngle = PointerAngleToRulerCenter(_gestureStart);
        _rotateMoved = false;
        _rulerGesture = RulerGesture.Rotate;
        RulerLayer.CapturePointer(e.Pointer);
    }

    /// <summary>长度调整按钮：按住后左右滑动改变刻度长度（最短 15.5cm 上下）。</summary>
    private void OnRulerResizePressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (_screenRuler is null) return;
        _gestureStart = e.GetCurrentPoint(RulerLayer).Position;
        _gestureStartCm = _rulerCmCount;
        _rulerGesture = RulerGesture.Resize;
        RulerLayer.CapturePointer(e.Pointer);
    }

    /// <summary>尺身按下：拖动整把尺子。</summary>
    private void OnRulerBodyPressed(object sender, PointerRoutedEventArgs e)
    {
        // 画笔工具下按在刻度边附近：不吞事件、不进入拖尺手势，
        // 放行冒泡到画布，由画布走贴边画直线
        if (_tool == AnnotationTool.Pen
            && e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed
            && TrySnapToAnyEdge(e.GetCurrentPoint(Root).Position) is not null)
        {
            return;
        }

        e.Handled = true;
        if (_screenRuler is null) return;
        _gestureStart = e.GetCurrentPoint(RulerLayer).Position;
        _gestureStartLeft = Canvas.GetLeft(_screenRuler);
        _gestureStartTop = Canvas.GetTop(_screenRuler);
        _rulerGesture = RulerGesture.Drag;
        RulerLayer.CapturePointer(e.Pointer);
    }

    private void OnRulerPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        // 量角器 / 三角板的手势优先：同一次拖动只会落到一只量具上
        if (DispatchMeasurePointerMoved(e))
        {
            e.Handled = true;
            return;
        }
        if (_rulerGesture == RulerGesture.None) return;
        // 直尺手势只在尺子存在时处理；圆规手势不依赖尺子
        if (_screenRuler is null && _rulerGesture is RulerGesture.Drag or RulerGesture.Rotate or RulerGesture.Resize) return;
        e.Handled = true;
        var pt = e.GetCurrentPoint(RulerLayer).Position;
        switch (_rulerGesture)
        {
            case RulerGesture.Drag:
                Canvas.SetLeft(_screenRuler!, _gestureStartLeft + pt.X - _gestureStart.X);
                Canvas.SetTop(_screenRuler!, _gestureStartTop + pt.Y - _gestureStart.Y);
                break;

            case RulerGesture.Rotate:
            {
                double angle = PointerAngleToRulerCenter(pt);
                // 逐帧累加增量，指针跨越 ±180° 时不会跳变，可连续旋转任意角度
                double delta = angle - _lastPointerAngle;
                if (delta > 180) delta -= 360;
                else if (delta < -180) delta += 360;
                _lastPointerAngle = angle;
                if (Math.Abs(pt.X - _gestureStart.X) + Math.Abs(pt.Y - _gestureStart.Y) > 4) _rotateMoved = true;
                _rulerAngle = NormalizeAngle(_rulerAngle + delta);
                ApplyRulerTransform();
                break;
            }

            case RulerGesture.Resize:
            {
                double perCm = RulerCmPx * RulerZoomSteps[_rulerZoomIndex];
                int target = Math.Clamp(
                    _gestureStartCm + (int)Math.Round((pt.X - _gestureStart.X) / perCm),
                    RulerMinCm, MaxRulerCm());
                if (target != _rulerCmCount) RebuildRulerKeepingPlacement(target);
                break;
            }

            case RulerGesture.CompassDrag:
                _compassNeedle = new Point(
                    _gestureStartLeft + pt.X - _gestureStart.X,
                    _gestureStartTop + pt.Y - _gestureStart.Y);
                ApplyCompassTransform();
                break;

            case RulerGesture.CompassResize:
            {
                double theta0 = _gestureStartHalfAngle * Math.PI / 180;
                // 让半径变化跟手（Δr ≈ Δx），再换算回半角增量
                double dDeg = (pt.X - _gestureStart.X) * 180 / (Math.PI * 2 * CompassLegLength * Math.Cos(theta0));
                double target = Math.Clamp(_gestureStartHalfAngle + dDeg, CompassMinHalfAngle, CompassMaxHalfAngle);
                if (Math.Abs(target - _compassHalfAngle) >= 0.2)
                {
                    _compassHalfAngle = target;
                    RebuildCompass();
                }
                break;
            }

            case RulerGesture.CompassDraw:
            {
                double angle = PointerAngleToCompassCenter(pt);
                // 逐帧累加增量，指针跨越 ±180° 时不会跳变，可连续旋转画弧
                double delta = angle - _compassLastPointerAngle;
                if (delta > 180) delta -= 360;
                else if (delta < -180) delta += 360;
                _compassLastPointerAngle = angle;
                // 快速旋转时按 ≤3° 步进补采样点，保证弧线平滑
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / CompassDrawStepDegrees));
                for (int i = 1; i <= steps; i++)
                {
                    _compassAngle += delta / steps;
                    if (_compassStroke is not null) AppendStrokePoint(_compassStroke, CompassPencilCanvasPos());
                }
                ApplyCompassTransform();
                break;
            }
        }
    }

    private void OnRulerPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_measureTools.FirstOrDefault(t => t.IsBusy) is { } measureTool)
        {
            e.Handled = true;
            // 先结束手势再释放捕获：ReleasePointerCapture 会同步触发 CaptureLost
            measureTool.EndGesture();
            RulerLayer.ReleasePointerCapture(e.Pointer);
            return;
        }
        if (_rulerGesture == RulerGesture.None) return;
        e.Handled = true;
        var gesture = _rulerGesture;
        // 先结束手势再释放捕获：ReleasePointerCapture 会同步触发 CaptureLost
        _rulerGesture = RulerGesture.None;
        RulerLayer.ReleasePointerCapture(e.Pointer);
        if (gesture == RulerGesture.Rotate && !_rotateMoved)
        {
            // 单击旋转按钮：顺时针吸附到下一个 90°
            _rulerAngle = NormalizeAngle(Math.Round(_rulerAngle / 90) * 90 + 90);
            ApplyRulerTransform();
        }
        if (gesture == RulerGesture.CompassDraw)
        {
            // 收笔：落笔即点、拖动成弧在此定稿
            FinishStroke();
            _compassStroke = null;
        }
    }

    private void OnRulerPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        foreach (var measureTool in _measureTools) measureTool.EndGesture();
        if (_rulerGesture == RulerGesture.CompassDraw)
        {
            FinishStroke();
            _compassStroke = null;
        }
        _rulerGesture = RulerGesture.None;
    }

    /// <summary>滚轮：向上滚逆时针、向下滚顺时针旋转。</summary>
    private void OnRulerPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        int wheelDelta = e.GetCurrentPoint(RulerLayer).Properties.MouseWheelDelta;
        if (wheelDelta == 0) return;

        // 滚轮落在哪只量具上就只转那一只
        if (ToolFromSource(e.OriginalSource) is { } measureTool)
        {
            e.Handled = true;
            measureTool.RotateByWheel(wheelDelta / 120.0 * RulerWheelAngleStep);
            return;
        }
        if (_screenRuler is null) return;
        e.Handled = true;
        _rulerAngle = NormalizeAngle(_rulerAngle - wheelDelta / 120.0 * RulerWheelAngleStep);
        ApplyRulerTransform();
    }

    /// <summary>指针相对尺心（旋转中心）的角度。</summary>
    private double PointerAngleToRulerCenter(Point pt)
    {
        double cx = Canvas.GetLeft(_screenRuler!) + _screenRuler!.Width / 2;
        double cy = Canvas.GetTop(_screenRuler) + _screenRuler.Height / 2;
        return Math.Atan2(pt.Y - cy, pt.X - cx) * 180 / Math.PI;
    }

    /// <summary>把角度折算到 (-180, 180]，避免累加后数值无限增长。</summary>
    private static double NormalizeAngle(double angle)
    {
        angle %= 360;
        if (angle > 180) angle -= 360;
        else if (angle <= -180) angle += 360;
        return angle;
    }

    /// <summary>最短长度之外的可用上限：按当前缩放不超出窗口宽度。</summary>
    private int MaxRulerCm()
    {
        double available = Root.ActualWidth - 48 - RulerEndMargin * 2;
        return Math.Max(RulerMinCm, (int)Math.Floor(available / (RulerCmPx * RulerZoomSteps[_rulerZoomIndex])));
    }

    /// <summary>改变刻度长度后重建尺身：左端保持原位，长度向右延伸或向左收缩。</summary>
    private void RebuildRulerKeepingPlacement(int cmCount)
    {
        if (_screenRuler is null) return;
        double left = Canvas.GetLeft(_screenRuler);
        double top = Canvas.GetTop(_screenRuler);
        RulerLayer.Children.Remove(_screenRuler);
        _rulerCmCount = cmCount;
        _screenRuler = BuildScreenRuler();
        RulerLayer.Children.Add(_screenRuler);
        ApplyRulerTransform();
        Canvas.SetLeft(_screenRuler, left);
        Canvas.SetTop(_screenRuler, top);
    }

    /// <summary>刷新缩放与旋转（均围绕尺身中心，不改变位置）。</summary>
    private void ApplyRulerTransform()
    {
        if (_screenRuler is null) return;
        double scale = RulerZoomSteps[_rulerZoomIndex];
        _screenRuler.RenderTransformOrigin = new Point(0.5, 0.5);
        _screenRuler.RenderTransform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform { ScaleX = scale, ScaleY = scale },
                new RotateTransform { Angle = _rulerAngle }
            }
        };
    }

    /// <summary>把尺身中心对准画布中心（仅召唤时使用）。</summary>
    private void CenterRuler()
    {
        if (_screenRuler is null) return;
        Canvas.SetLeft(_screenRuler, (Root.ActualWidth - _screenRuler.Width) / 2);
        Canvas.SetTop(_screenRuler, (Root.ActualHeight - _screenRuler.Height) / 2);
    }

    /// <summary>
    /// 生成一把直尺：半透明浅色尺身，顶部为略亮的刻度带（刻度自顶边向下悬挂，厘米刻度右侧标注数字），
    /// 底部左侧为关闭按钮、右侧为缩放/旋转/长度调整按钮。长度由 _rulerCmCount 决定。
    /// </summary>
    private Border BuildScreenRuler()
    {
        double rulerWidth = _rulerCmCount * RulerCmPx + RulerEndMargin * 2;

        var tickBrush = MeasureToolVisuals.TickStroke;
        var numberBrush = MeasureToolVisuals.NumberForeground;

        var tickCanvas = new Canvas { Width = rulerWidth - 2, Height = RulerTickBandHeight };
        for (int mm = 0; mm <= _rulerCmCount * 10; mm++)
        {
            double x = RulerEndMargin + mm * MmToLogicalPx;
            bool isCm = mm % 10 == 0;
            tickCanvas.Children.Add(new Line
            {
                X1 = x, Y1 = 0, X2 = x,
                Y2 = isCm ? 15 : mm % 5 == 0 ? 10 : 6.5,
                Stroke = tickBrush,
                StrokeThickness = isCm ? 1.1 : 0.7
            });
            if (!isCm) continue;
            var label = new TextBlock
            {
                Text = (mm / 10).ToString(),
                FontSize = 10,
                Foreground = numberBrush
            };
            Canvas.SetLeft(label, x + 2);
            Canvas.SetTop(label, 16);
            tickCanvas.Children.Add(label);
        }

        var tickBand = new Border
        {
            Height = RulerTickBandHeight,
            Background = MeasureToolVisuals.TickBandFill,
            BorderBrush = MeasureToolVisuals.TickBandBorder,
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(5, 5, 0, 0),
            Child = tickCanvas
        };

        var closeButton = MeasureToolVisuals.MakeButton(MeasureToolVisuals.BuildCloseIcon(), "关闭直尺", OnRulerClosePressed);
        closeButton.HorizontalAlignment = HorizontalAlignment.Left;
        closeButton.VerticalAlignment = VerticalAlignment.Bottom;
        closeButton.Margin = new Thickness(12, 0, 0, 11);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 14,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 12, 11)
        };
        actions.Children.Add(MeasureToolVisuals.MakeButton(MeasureToolVisuals.BuildGlyph("\uE8A3", 13), "缩放（100% / 125% / 150%）", OnRulerZoomPressed));
        actions.Children.Add(MeasureToolVisuals.MakeButton(MeasureToolVisuals.BuildGlyph("\uE72C", 13), "按住拖动旋转，滚轮也可旋转", OnRulerRotatePressed));
        actions.Children.Add(MeasureToolVisuals.MakeButton(MeasureToolVisuals.BuildExpandIcon(), "按住左右滑动调整长度", OnRulerResizePressed));

        var body = new Grid();
        body.Children.Add(closeButton);
        body.Children.Add(actions);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(tickBand);
        Grid.SetRow(body, 1);
        layout.Children.Add(body);

        var ruler = new Border
        {
            Width = rulerWidth,
            Height = RulerBaseHeight,
            CornerRadius = new CornerRadius(6),
            Background = MeasureToolVisuals.BodyFill,
            BorderBrush = MeasureToolVisuals.BodyBorder,
            BorderThickness = new Thickness(1),
            Child = layout
        };
        // 尺身拦截指针：按下即开始拖动整把尺子，且不会在尺子上画出笔迹；按钮自身先处理并阻断冒泡
        ruler.PointerPressed += OnRulerBodyPressed;
        return ruler;
    }

    // ---- 尺边吸附画直线 ----

    /// <summary>落笔点是否贴着尺子的刻度边（顶边）：是则记录尺边几何并返回投影后的吸附起点，否则返回 null。</summary>
    private Point? TrySnapToRulerEdge(Point p)
    {
        if (_screenRuler is null) return null;
        var transform = _screenRuler.TransformToVisual(Root);
        var a = transform.TransformPoint(new Point(0, 0));
        var b = transform.TransformPoint(new Point(_screenRuler.Width, 0));
        double len = Distance(a, b);
        if (len < 1) return null;

        var dir = new Point((b.X - a.X) / len, (b.Y - a.Y) / len);
        double proj = (p.X - a.X) * dir.X + (p.Y - a.Y) * dir.Y;
        double perpX = p.X - (a.X + dir.X * proj);
        double perpY = p.Y - (a.Y + dir.Y * proj);
        if (Math.Sqrt(perpX * perpX + perpY * perpY) > RulerSnapTolerance) return null;
        if (proj < -8 || proj > len + 8) return null;

        _snapEdgeStart = a;
        _snapEdgeDir = dir;
        _snapEdgeLen = len;

        // 外法线：背离尺身中心的一侧，用于放置长度读数
        var center = transform.TransformPoint(new Point(_screenRuler.Width / 2, _screenRuler.Height / 2));
        double midX = (a.X + b.X) / 2, midY = (a.Y + b.Y) / 2;
        double nx = midX - center.X, ny = midY - center.Y;
        double nl = Math.Sqrt(nx * nx + ny * ny);
        _snapOutward = nl > 0.01 ? new Point(nx / nl, ny / nl) : new Point(0, -1);

        proj = Math.Clamp(proj, 0, len);
        return new Point(a.X + dir.X * proj, a.Y + dir.Y * proj);
    }

    /// <summary>落笔点贴着任一量具的可吸附边：把该边几何写进共享字段，返回投影后的吸附起点。
    /// 三角板三条边里取最近的一条，量角器只认底边；都不命中时退回直尺的刻度边。</summary>
    private Point? TrySnapToAnyEdge(Point p)
    {
        for (int i = _measureTools.Count - 1; i >= 0; i--)
        {
            if (_measureTools[i].TrySnap(p, Root) is not { } snap) continue;
            _snapEdgeStart = snap.Start;
            _snapEdgeDir = snap.Dir;
            _snapEdgeLen = snap.Length;
            _snapOutward = snap.Outward;
            return snap.Projected;
        }
        return TrySnapToRulerEdge(p);
    }

    // ---- 量角器 / 三角板 ----

    /// <summary>面板上的量角器：每次点击新增一把，用板身上的关闭按钮收回。</summary>
    private void OnProtractorOptionPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        AddMeasureTool(new ProtractorTool(RulerLayer));
    }

    /// <summary>面板上的 45° 三角板：每次点击新增一块。</summary>
    private void OnTriangle45OptionPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        AddMeasureTool(new TriangleTool(RulerLayer, TriangleKind.FortyFive));
    }

    /// <summary>面板上的 30°-60° 三角板：每次点击新增一块。</summary>
    private void OnTriangle3060OptionPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        AddMeasureTool(new TriangleTool(RulerLayer, TriangleKind.ThirtySixty));
    }

    /// <summary>新增一只量具：同类最多三只，超出时先收回最早的一只；落点按同类数量错开。</summary>
    private void AddMeasureTool(MeasureTool tool)
    {
        int sameKind = _measureTools.Count(t => t.Kind == tool.Kind);
        if (sameKind >= MeasureToolMaxPerKind)
        {
            RemoveMeasureTool(_measureTools.First(t => t.Kind == tool.Kind));
            sameKind = MeasureToolMaxPerKind - 1;
        }

        tool.CloseRequested += RemoveMeasureTool;
        // 画笔工具下按在可吸附边附近时放行给画布，走既有的贴边画直线流程
        tool.SnapPassThrough = p => _tool == AnnotationTool.Pen && TrySnapToAnyEdge(p) is not null;
        _measureTools.Add(tool);
        RulerLayer.Children.Add(tool.Root);
        tool.PlaceAt(new Point(
            Root.ActualWidth / 2 + sameKind * 32,
            Root.ActualHeight / 2 + sameKind * 32));
        tool.BringToFront();
        UpdateMeasureOptionStates();
    }

    /// <summary>收回一只量具。</summary>
    private void RemoveMeasureTool(MeasureTool tool)
    {
        tool.EndGesture();
        RulerLayer.Children.Remove(tool.Root);
        _measureTools.Remove(tool);
        UpdateMeasureOptionStates();
    }

    /// <summary>面板项底色：该类量具只要还在场就保持高亮。</summary>
    private void UpdateMeasureOptionStates()
    {
        ProtractorOption.Background = _measureTools.Any(t => t.Kind == MeasureKind.Protractor)
            ? ShapeActiveBrush : TransparentBrush;
        Triangle45Option.Background = _measureTools.Any(t => t.Kind == MeasureKind.Triangle45)
            ? ShapeActiveBrush : TransparentBrush;
        Triangle3060Option.Background = _measureTools.Any(t => t.Kind == MeasureKind.Triangle3060)
            ? ShapeActiveBrush : TransparentBrush;
    }

    /// <summary>指针落在哪只量具上（滚轮旋转按此归属，避免一次滚轮转全部量具）。</summary>
    private MeasureTool? ToolFromSource(object? source)
    {
        var element = source as DependencyObject;
        while (element is not null)
        {
            if (element is FrameworkElement { Tag: MeasureTool tool }) return tool;
            if (ReferenceEquals(element, RulerLayer)) return null;
            element = VisualTreeHelper.GetParent(element);
        }
        return null;
    }

    /// <summary>量具手势的移动分发：射线拖动优先，其次拖动/旋转/调尺寸。返回是否已消费。</summary>
    private bool DispatchMeasurePointerMoved(PointerRoutedEventArgs e)
    {
        if (_measureTools.Count == 0) return false;
        var pt = e.GetCurrentPoint(RulerLayer).Position;

        if (_measureTools.FirstOrDefault(t => t.RayDragging) is { } ray)
        {
            ray.DragRay(pt);
            return true;
        }
        if (_measureTools.FirstOrDefault(t => t.Gesture != MeasureGesture.None) is { } tool)
        {
            tool.PointerMoved(pt, e.KeyModifiers.HasFlag(VirtualKeyModifiers.Shift));
            return true;
        }
        return false;
    }

    /// <summary>新建贴边直线草稿：视觉样式跟随当前画笔（颜色/粗细/荧光笔半透明），松手时按图形草稿提交。</summary>
    private InkStroke BeginSnapDraft(Point p)
    {
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(_penColor),
            StrokeThickness = NominalThickness,
            Opacity = StrokeOpacity,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = _penKind == PenKind.Highlighter ? PenLineCap.Flat : PenLineCap.Round,
            StrokeEndLineCap = _penKind == PenKind.Highlighter ? PenLineCap.Flat : PenLineCap.Round,
            IsHitTestVisible = false
        };
        line.Points.Add(p);

        var draft = new InkStroke
        {
            Visual = line,
            BaseThickness = NominalThickness,
            Kind = _penKind == PenKind.Highlighter ? PenKind.Highlighter : PenKind.Hard,
            Color = _penColor
        };
        draft.Points.Add(p);

        InkLayer.Children.Add(line);
        return draft;
    }

    /// <summary>贴边画线中：指针投影到尺边上（钳在尺边范围内）作为终点，实时刷新直线与长度读数。</summary>
    private void UpdateRulerSnap(Point p)
    {
        double proj = (p.X - _snapEdgeStart.X) * _snapEdgeDir.X + (p.Y - _snapEdgeStart.Y) * _snapEdgeDir.Y;
        proj = Math.Clamp(proj, 0, _snapEdgeLen);
        var end = new Point(_snapEdgeStart.X + _snapEdgeDir.X * proj, _snapEdgeStart.Y + _snapEdgeDir.Y * proj);

        // 密化轮廓点，橡皮的逐点命中判定与切割直接复用
        var points = DensifySegment(_snapStart, end);
        var draft = _shapeDraft!;
        draft.Points.Clear();
        draft.Points.AddRange(points);
        var line = (Polyline)draft.Visual;
        line.Points.Clear();
        foreach (var q in points) line.Points.Add(q);

        UpdateSnapLabel(end, Distance(_snapStart, end));
    }

    /// <summary>显示/刷新长度读数：厘米为单位，精确到小数点后两位，颜色跟随当前画笔。</summary>
    private void UpdateSnapLabel(Point end, double lengthPx)
    {
        if (_snapLabel is null)
        {
            _snapLabel = new TextBlock
            {
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(_penColor),
                IsHitTestVisible = false
            };
            RulerLayer.Children.Add(_snapLabel);
        }
        _snapLabel.Text = $"{lengthPx / RulerCmPx:0.00}";
        Canvas.SetLeft(_snapLabel, end.X + _snapOutward.X * 30 - 14);
        Canvas.SetTop(_snapLabel, end.Y + _snapOutward.Y * 30 - 9);
    }

    private void HideSnapLabel()
    {
        if (_snapLabel is null) return;
        RulerLayer.Children.Remove(_snapLabel);
        _snapLabel = null;
    }

    // ---- 圆规 ----

    /// <summary>「圆规」：在画布中央召唤/收回一只圆规。</summary>
    private void OnCompassOptionPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (_compass is not null)
        {
            HideCompass();
            return;
        }
        _compassHalfAngle = CompassDefaultHalfAngle;
        _compassAngle = 0;
        // 让针尖与笔尖的连线中点大致落在画布中心
        _compassNeedle = new Point(
            Root.ActualWidth / 2 - CompassRadiusPx,
            Root.ActualHeight / 2 + 40);
        _compass = BuildCompass();
        RulerLayer.Children.Add(_compass);
        ApplyCompassTransform();
        CompassOption.Background = ShapeActiveBrush;
    }

    private void HideCompass()
    {
        if (_compass is null) return;
        if (_rulerGesture == RulerGesture.CompassDraw)
        {
            FinishStroke();
            _compassStroke = null;
        }
        _rulerGesture = RulerGesture.None;
        RulerLayer.Children.Remove(_compass);
        _compass = null;
        _compassRadiusLine = null;
        _compassRadiusLabel = null;
        _compassHalfAngle = CompassDefaultHalfAngle;
        _compassAngle = 0;
        CompassOption.Background = TransparentBrush;
    }

    /// <summary>开口半径：针尖到笔尖的距离（逻辑像素）。</summary>
    private double CompassRadiusPx => 2 * CompassLegLength * Math.Sin(_compassHalfAngle * Math.PI / 180);

    /// <summary>针尖在圆规容器内的局部坐标。</summary>
    private Point CompassNeedleLocal()
    {
        double theta = _compassHalfAngle * Math.PI / 180;
        return new Point(
            _compass!.Width / 2 - CompassLegLength * Math.Sin(theta),
            CompassPivotTop + CompassLegLength * Math.Cos(theta));
    }

    /// <summary>笔尖在画布坐标中的位置（随圆规整体旋转变化）。</summary>
    private Point CompassPencilCanvasPos()
    {
        double rad = _compassAngle * Math.PI / 180;
        double r = CompassRadiusPx;
        return new Point(
            _compassNeedle.X + r * Math.Cos(rad),
            _compassNeedle.Y + r * Math.Sin(rad));
    }

    /// <summary>指针相对针尖（圆心）的角度。</summary>
    private double PointerAngleToCompassCenter(Point pt) =>
        Math.Atan2(pt.Y - _compassNeedle.Y, pt.X - _compassNeedle.X) * 180 / Math.PI;

    /// <summary>刷新圆规的旋转（绕针尖）与位置（针尖对准 _compassNeedle）。</summary>
    private void ApplyCompassTransform()
    {
        if (_compass is null) return;
        var n = CompassNeedleLocal();
        _compass.RenderTransform = new RotateTransform { Angle = _compassAngle, CenterX = n.X, CenterY = n.Y };
        Canvas.SetLeft(_compass, _compassNeedle.X - n.X);
        Canvas.SetTop(_compass, _compassNeedle.Y - n.Y);
    }

    /// <summary>开口变化后重建圆规：针尖位置与旋转角保持不变（捕获在尺规图层上，重建不丢手势）。</summary>
    private void RebuildCompass()
    {
        if (_compass is null) return;
        RulerLayer.Children.Remove(_compass);
        _compass = BuildCompass();
        RulerLayer.Children.Add(_compass);
        ApplyCompassTransform();
    }

    /// <summary>虚连线与半径读数跟随当前画笔颜色（换色时同步刷新）。</summary>
    private void UpdateCompassInkColor()
    {
        if (_compassRadiusLine is null || _compassRadiusLabel is null) return;
        _compassRadiusLine.Stroke = new SolidColorBrush(_penColor);
        _compassRadiusLabel.Foreground = new SolidColorBrush(_penColor);
    }

    /// <summary>把柄处的移动按钮：按住直接拖动整只圆规。</summary>
    private void OnCompassMovePressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (_compass is null) return;
        _gestureStart = e.GetCurrentPoint(RulerLayer).Position;
        _gestureStartLeft = _compassNeedle.X;
        _gestureStartTop = _compassNeedle.Y;
        _rulerGesture = RulerGesture.CompassDrag;
        RulerLayer.CapturePointer(e.Pointer);
    }

    /// <summary>笔尖侧边的调整按钮：按住左右滑动调整圆规开口大小。</summary>
    private void OnCompassResizePressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (_compass is null) return;
        _gestureStart = e.GetCurrentPoint(RulerLayer).Position;
        _gestureStartHalfAngle = _compassHalfAngle;
        _rulerGesture = RulerGesture.CompassResize;
        RulerLayer.CapturePointer(e.Pointer);
    }

    /// <summary>笔尖（含黑色保护壳）按下：以针尖为圆心旋转画弧，
    /// 落笔时先在圆心（针尖）与笔尖各点一个笔迹点。</summary>
    private void OnCompassDrawPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        if (_compass is null) return;
        _compassLastPointerAngle = PointerAngleToCompassCenter(e.GetCurrentPoint(RulerLayer).Position);
        // 圆心落点：单点笔迹在 FinishStroke 中补成圆点
        _current = BeginStroke(_compassNeedle);
        FinishStroke();
        // 笔尖落点：画弧的起笔点
        _current = BeginStroke(CompassPencilCanvasPos());
        _compassStroke = _current;
        _rulerGesture = RulerGesture.CompassDraw;
        RulerLayer.CapturePointer(e.Pointer);
    }

    /// <summary>关闭按钮：收回圆规。</summary>
    private void OnCompassClosePressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        HideCompass();
    }

    /// <summary>圆规主体（尺腿/铰链/捏柄）按下：只吞掉事件，不在圆规上画出笔迹。</summary>
    private static void OnCompassBodyPressed(object sender, PointerRoutedEventArgs e) => e.Handled = true;

    /// <summary>生成一只圆规：顶部为关闭按钮与白色捏柄，深色铰链内嵌移动按钮；
    /// 两条尺腿下端分别为金属针尖与带黑色保护壳的铅笔笔尖，笔尖旁为开口调整按钮；
    /// 针尖与笔尖之间用虚线连接并标注半径读数（颜色跟随画笔）。</summary>
    private Canvas BuildCompass()
    {
        double theta = _compassHalfAngle * Math.PI / 180;
        double sin = Math.Sin(theta), cos = Math.Cos(theta);
        double width = 2 * CompassLegLength * sin + 170;
        double height = CompassPivotTop + CompassLegLength * cos + 46;
        double halfW = width / 2;
        double tipsY = CompassPivotTop + CompassLegLength * cos;
        double needleX = halfW - CompassLegLength * sin;
        double pencilX = halfW + CompassLegLength * sin;

        var root = new Canvas { Width = width, Height = height };

        // 针尖—笔尖虚连线（压在尺腿下面）
        _compassRadiusLine = new Polyline
        {
            StrokeThickness = 2.5,
            StrokeDashArray = [4, 3],
            IsHitTestVisible = false
        };
        _compassRadiusLine.Points.Add(new Point(needleX, tipsY));
        _compassRadiusLine.Points.Add(new Point(pencilX, tipsY));
        root.Children.Add(_compassRadiusLine);

        // 两条尺腿（绕铰链各向左右张开半角）
        root.Children.Add(BuildCompassLeg(-_compassHalfAngle, halfW, true));
        root.Children.Add(BuildCompassLeg(_compassHalfAngle, halfW, false));

        // 深色铰链
        var hinge = new Border
        {
            Width = 76,
            Height = 88,
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x3C, 0x40, 0x48))
        };
        Canvas.SetLeft(hinge, halfW - 38);
        Canvas.SetTop(hinge, CompassPivotTop - 44);
        hinge.PointerPressed += OnCompassBodyPressed;
        root.Children.Add(hinge);

        // 把柄处的移动按钮（按住拖动整只圆规）
        var moveIcon = BuildCompassMoveIcon();
        moveIcon.HorizontalAlignment = HorizontalAlignment.Center;
        moveIcon.VerticalAlignment = VerticalAlignment.Center;
        var move = new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(23),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x56, 0x5C, 0x66)),
            Child = moveIcon
        };
        ToolTipService.SetToolTip(move, "按住拖动圆规");
        Canvas.SetLeft(move, halfW - 23);
        Canvas.SetTop(move, CompassPivotTop - 23);
        move.PointerPressed += OnCompassMovePressed;
        root.Children.Add(move);

        // 白色捏柄（纯装饰）
        var knob = new Border
        {
            Width = 18,
            Height = 26,
            CornerRadius = new CornerRadius(9),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xF2, 0xF3, 0xF5)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xD7, 0xDA, 0xDE)),
            BorderThickness = new Thickness(1)
        };
        Canvas.SetLeft(knob, halfW - 9);
        Canvas.SetTop(knob, CompassPivotTop - 76);
        knob.PointerPressed += OnCompassBodyPressed;
        root.Children.Add(knob);

        // 关闭按钮
        var close = MeasureToolVisuals.MakeButton(MeasureToolVisuals.BuildCloseIcon(), "收回圆规", OnCompassClosePressed);
        Canvas.SetLeft(close, halfW - 13);
        Canvas.SetTop(close, 8);
        root.Children.Add(close);

        // 开口调整按钮（笔尖侧边）
        var resize = MeasureToolVisuals.MakeButton(MeasureToolVisuals.BuildExpandIcon(), "按住左右滑动调整开口", OnCompassResizePressed);
        Canvas.SetLeft(resize, halfW + 0.58 * CompassLegLength * sin + 34);
        Canvas.SetTop(resize, CompassPivotTop + 0.58 * CompassLegLength * cos - 13);
        root.Children.Add(resize);

        // 半径读数
        _compassRadiusLabel = new TextBlock
        {
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Text = $"r={CompassRadiusPx / RulerCmPx:0.0}",
            IsHitTestVisible = false
        };
        Canvas.SetLeft(_compassRadiusLabel, halfW - 20);
        Canvas.SetTop(_compassRadiusLabel, tipsY + 10);
        root.Children.Add(_compassRadiusLabel);

        // 笔尖命中区（含黑色保护壳）：按住旋转画弧
        var drawZone = new Border
        {
            Width = 46,
            Height = 46,
            CornerRadius = new CornerRadius(23),
            Background = TransparentBrush
        };
        ToolTipService.SetToolTip(drawZone, "按住旋转画圆");
        Canvas.SetLeft(drawZone, pencilX - 23);
        Canvas.SetTop(drawZone, tipsY - 49);
        drawZone.PointerPressed += OnCompassDrawPressed;
        root.Children.Add(drawZone);

        UpdateCompassInkColor();
        return root;
    }

    /// <summary>生成一条尺腿：左针腿末端为金属针尖，右笔腿末端为带黑色保护壳的铅笔。整体绕铰链旋转 angle。</summary>
    private Canvas BuildCompassLeg(double angle, double pivotX, bool needle)
    {
        var leg = new Canvas { Width = 30, Height = 24 + CompassLegLength };

        var bar = new Border
        {
            Width = 24,
            Height = CompassLegLength - 6,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xF2, 0xF3, 0xF5)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xD7, 0xDA, 0xDE)),
            BorderThickness = new Thickness(1)
        };
        Canvas.SetLeft(bar, 3);
        Canvas.SetTop(bar, 24);
        leg.Children.Add(bar);

        if (needle)
        {
            var collar = new Border
            {
                Width = 18,
                Height = 12,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xC9, 0xCC, 0xD2))
            };
            Canvas.SetLeft(collar, 6);
            Canvas.SetTop(collar, 24 + CompassLegLength - 40);
            leg.Children.Add(collar);

            // 金属针尖：细长三角刺出尺腿末端
            leg.Children.Add(new Polygon
            {
                Points = new PointCollection
                {
                    new Point(15, 24 + CompassLegLength),
                    new Point(11, 24 + CompassLegLength - 30),
                    new Point(19, 24 + CompassLegLength - 30)
                },
                Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0x87, 0x8C, 0x95))
            });
        }
        else
        {
            var pencil = new Border
            {
                Width = 30,
                Height = 110,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0xED, 0xEF, 0xF2)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0xD0, 0xD3, 0xD8)),
                BorderThickness = new Thickness(1)
            };
            Canvas.SetLeft(pencil, 0);
            Canvas.SetTop(pencil, 24 + CompassLegLength - 162);
            leg.Children.Add(pencil);

            // 黑色保护壳
            var sheath = new Border
            {
                Width = 30,
                Height = 40,
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x2E, 0x31, 0x38))
            };
            Canvas.SetLeft(sheath, 0);
            Canvas.SetTop(sheath, 24 + CompassLegLength - 54);
            leg.Children.Add(sheath);

            // 铅笔笔尖
            leg.Children.Add(new Polygon
            {
                Points = new PointCollection
                {
                    new Point(15, 24 + CompassLegLength),
                    new Point(6, 24 + CompassLegLength - 18),
                    new Point(24, 24 + CompassLegLength - 18)
                },
                Fill = new SolidColorBrush(Color.FromArgb(0xFF, 0x4A, 0x4E, 0x56))
            });
        }

        // 绕铰链点旋转：局部坐标 (15,24) 对准容器中的铰链位置
        leg.RenderTransform = new RotateTransform { Angle = angle, CenterX = 15, CenterY = 24 };
        Canvas.SetLeft(leg, pivotX - 15);
        Canvas.SetTop(leg, CompassPivotTop - 24);
        leg.PointerPressed += OnCompassBodyPressed;
        return leg;
    }

    /// <summary>移动按钮图标：白色四向箭头。</summary>
    private static Viewbox BuildCompassMoveIcon()
    {
        var stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
        var canvas = new Canvas { Width = 24, Height = 24 };
        void Add(double x1, double y1, double x2, double y2) => canvas.Children.Add(
            new Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = stroke,
                StrokeThickness = 2.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            });
        Add(12, 3.5, 12, 20.5);   // 竖杆
        Add(3.5, 12, 20.5, 12);   // 横杆
        Add(12, 3.5, 9, 6.5);     // 上箭头
        Add(12, 3.5, 15, 6.5);
        Add(12, 20.5, 9, 17.5);   // 下箭头
        Add(12, 20.5, 15, 17.5);
        Add(3.5, 12, 6.5, 9);     // 左箭头
        Add(3.5, 12, 6.5, 15);
        Add(20.5, 12, 17.5, 9);   // 右箭头
        Add(20.5, 12, 17.5, 15);
        return new Viewbox { Width = 20, Height = 20, Child = canvas };
    }

    /// <summary>从右侧滑出：面板贴右侧工具栏，向左位移归位并淡入。</summary>
    private void ShowSlidePanel(Border panel)
    {
        _closingPanels.Remove(panel);
        panel.Visibility = Visibility.Visible;
        var slide = new TranslateTransform { X = PanelSlideDistance };
        panel.RenderTransform = slide;
        panel.Opacity = 0;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var storyboard = new Storyboard();
        storyboard.Children.Add(BuildAnimation(slide, "X", PanelSlideDistance, 0, 220, ease));
        storyboard.Children.Add(BuildAnimation(panel, "Opacity", 0, 1, 180, ease));
        storyboard.Begin();
    }

    /// <summary>向右收回后再隐藏，避免面板突然消失。</summary>
    private void HideSlidePanel(Border panel)
    {
        if (panel.Visibility != Visibility.Visible || _closingPanels.Contains(panel)) return;

        _closingPanels.Add(panel);
        var slide = panel.RenderTransform as TranslateTransform ?? new TranslateTransform();
        panel.RenderTransform = slide;

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var storyboard = new Storyboard();
        storyboard.Children.Add(BuildAnimation(slide, "X", slide.X, PanelSlideDistance, 150, ease));
        storyboard.Children.Add(BuildAnimation(panel, "Opacity", panel.Opacity, 0, 130, ease));
        storyboard.Completed += (_, _) =>
        {
            // 收回过程中又被点开：放弃这次隐藏
            if (!_closingPanels.Contains(panel)) return;
            _closingPanels.Remove(panel);
            storyboard.Stop();
            slide.X = 0;
            panel.Opacity = 1;
            panel.Visibility = Visibility.Collapsed;
        };
        storyboard.Begin();
    }

    // ── 橡皮设置面板：清空 / 三档大小 ──

    /// <summary>橡皮面板"清空"按钮：清除所有笔迹。</summary>
    private void OnEraserClearClick(object sender, RoutedEventArgs e) => ClearAll();

    /// <summary>橡皮大小档位按钮：按 Tag 设置擦除半径。</summary>
    private void OnEraserSizePresetPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }
            || !double.TryParse(tag, out double radius)) return;

        _eraserRadius = radius;
        _eraseRadiusEffective = radius;
        UpdateEraserSizeVisuals();
    }

    /// <summary>刷新橡皮大小标签文字与贴图尺寸。</summary>
    private void UpdateEraserSizeVisuals()
    {
        if (EraserSizeLabel is null) return;
        string size = _eraserRadius < 9 ? "小" : _eraserRadius < 16 ? "中" : "大";
        EraserSizeLabel.Text = $"大小 {size}";

        EraserImage.Width = EraserVisualSize;
        EraserImage.Height = EraserVisualSize;
    }

    // ── 几何图形设置面板：图形选择 + 三档粗细 ──

    private static readonly SolidColorBrush ShapeActiveBrush = new(Color.FromArgb(0x16, 0x00, 0x78, 0xD4));
    private static readonly SolidColorBrush TransparentBrush = new(Color.FromArgb(0, 0, 0, 0));

    /// <summary>图形选择按钮：按 Tag 切换当前图形类型。</summary>
    private void OnShapeOptionPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }
            || !Enum.TryParse(tag, out ShapeKind kind)) return;
        _shapeKind = kind;
        UpdateShapeOptionVisuals();
    }

    /// <summary>图形粗细档位按钮：按 Tag 设置线条粗细。</summary>
    private void OnShapeThicknessPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string tag }
            || !double.TryParse(tag, out double thickness)) return;
        _shapeThickness = thickness;
        UpdateShapeThicknessVisuals();
    }

    /// <summary>选中的图形图标着主题蓝、底色淡蓝，其余恢复常态。</summary>
    private void UpdateShapeOptionVisuals()
    {
        if (ShapeRow1 is null) return;
        foreach (var row in new[] { ShapeRow1, ShapeRow2 })
        {
            foreach (var child in row.Children)
            {
                if (child is not Border { Tag: string tag } option) continue;
                bool active = Enum.TryParse(tag, out ShapeKind kind) && kind == _shapeKind;
                option.Background = active ? ShapeActiveBrush : TransparentBrush;
                if (option.Child is Path icon) icon.Stroke = active ? AccentBrush : NormalBrush;
            }
        }
    }

    /// <summary>刷新图形粗细档位的选中态底色。</summary>
    private void UpdateShapeThicknessVisuals()
    {
        if (ShapeThicknessRow is null) return;
        foreach (var child in ShapeThicknessRow.Children)
        {
            if (child is not Border { Tag: string tag } preset) continue;
            bool active = double.TryParse(tag, out double t) && Math.Abs(t - _shapeThickness) < 0.01;
            preset.Background = active ? ShapeActiveBrush : TransparentBrush;
        }
    }

    /// <summary>调色盘颜色变更：同步画笔颜色并刷新强调色与色块选中态。</summary>
    private void OnPenColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        _penColor = args.NewColor;
        SavePenColor(_penColor);
        RefreshAccent();
        UpdateSwatchSelection();
        UpdateCompassInkColor();
    }

    /// <summary>强调色（跟随当前画笔颜色）刷新所有共用画刷；画刷是共享实例，改 Color 即全局生效。</summary>
    private void RefreshAccent()
    {
        Color accent = AccentColor;
        AccentBrush.Color = accent;
        ShapeActiveBrush.Color = WithAlpha(accent, 0x16);
        _toolCheckedBrush.Color = WithAlpha(accent, 0x16);
        _toolCheckedHoverBrush.Color = WithAlpha(accent, 0x24);
        _toolCheckedPressedBrush.Color = WithAlpha(accent, 0x32);
    }

    /// <summary>批注窗跟随应用明暗：面板底色/描边/文字由 ThemeResource 自动换档，
    /// 这里只同步代码里直接改色值的共享画刷（图标默认色、色块边框、悬停/按下蒙层）。</summary>
    private void ApplyAnnotTheme()
    {
        bool dark = AppTheme.IsDark;
        NormalBrush.Color = dark
            ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B);
        SwatchBorderBrush.Color = dark
            ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x1F, 0x00, 0x00, 0x00);
        _toolHoverBrush.Color = dark
            ? Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x0C, 0x00, 0x00, 0x00);
        _toolPressedBrush.Color = dark
            ? Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x16, 0x00, 0x00, 0x00);
    }

    /// <summary>应用主题变化：批注窗面板同步明暗；笔色强调色按新主题的底色重算对比度。</summary>
    private void OnAppThemeChanged(object? sender, EventArgs e)
    {
        ApplyAnnotTheme();
        RefreshAccent();
    }

    /// <summary>保留透明度、替换 RGB，用于由强调色派生各档选中态底色。</summary>
    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>批注窗面板底色（与 App.xaml 主题字典的卡片色一致），用于计算可读强调色。</summary>
    private static Color PanelBackground => AppTheme.IsDark
        ? Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B)
        : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);

    /// <summary>把画笔颜色转成面板上的可读强调色：浅黄、荧光绿这类低对比色
    /// 按当前主题向黑（浅色主题）或白（深色主题）混合，直到与面板底色对比度达标。
    /// 只影响 UI 强调色，不改画笔本身，笔迹仍用原始颜色。</summary>
    private static Color ReadableAccent(Color penColor)
    {
        Color background = PanelBackground;
        Color toward = AppTheme.IsDark
            ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
        Color result = penColor;
        for (int i = 0; i < 12 && ContrastRatio(result, background) < 3.0; i++)
            result = Blend(result, toward, 0.18);
        return result;
    }

    /// <summary>WCAG 相对亮度（0~1）。</summary>
    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            double s = value / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    /// <summary>两色对比度（1~21，WCAG 公式）。</summary>
    private static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a);
        double lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>按比例把 from 混向 to，结果保持不透明。</summary>
    private static Color Blend(Color from, Color to, double amount)
    {
        static byte Mix(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
        return Color.FromArgb(0xFF, Mix(from.R, to.R, amount), Mix(from.G, to.G, amount), Mix(from.B, to.B, amount));
    }

    /// <summary>粗细滑块值变更：同步画笔粗细并刷新演示曲线。</summary>
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

    /// <summary>离开粗细演示区域：若未固定则隐藏滑块、恢复曲线。</summary>
    private void OnPreviewAreaExited(object sender, PointerRoutedEventArgs e)
    {
        _sliderHovered = false;
        if (!_sliderPinned) SetSliderVisible(false);
    }

    /// <summary>切换粗细滑块与演示曲线的显隐互斥状态。</summary>
    private void SetSliderVisible(bool visible)
    {
        ThicknessSlider.Opacity = visible ? 1 : 0;
        ThicknessSlider.IsHitTestVisible = visible;
        if (ThicknessPreviewArea.Children[0] is Path curve)
        {
            curve.Opacity = visible ? 0 : 1;
        }
    }

    /// <summary>固定/取消固定粗细滑块，使其常驻显示或随悬停显隐。</summary>
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

    /// <summary>调色盘入口点击：弹出 Flyout 承载的圆环调色盘。</summary>
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

    /// <summary>预设色块点击：按 Tag 中的十六进制色值切换画笔颜色。</summary>
    private void OnSwatchPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border { Tag: string hex })
        {
            _penColor = ParseHex(hex);
            SavePenColor(_penColor);
            if (_colorPicker is not null) _colorPicker.Color = _penColor;
            RefreshAccent();
            UpdateSwatchSelection();
            UpdateCompassInkColor();
            e.Handled = true;
        }
    }

    /// <summary>将 #RRGGBB 或 #AARRGGBB 格式的字符串解析为 Color。</summary>
    private static Color ParseHex(string hex)
    {
        string digits = hex.TrimStart('#');
        if (digits.Length == 6) digits = "FF" + digits;
        uint value = Convert.ToUInt32(digits, 16);
        return Color.FromArgb(
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    /// <summary>ARGB 整数转 Color（用于读取设置里保存的笔色）。</summary>
    private static Color FromArgbInt(int argb)
    {
        uint value = unchecked((uint)argb);
        return Color.FromArgb(
            (byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    /// <summary>「记忆退出前的颜色」开启时，把当前笔色记到设置里；实际落盘在窗口关闭时统一执行。</summary>
    private static void SavePenColor(Color color)
    {
        if (!AppSettings.RememberPenColor) return;
        int argb = unchecked((int)((uint)0xFF000000
            | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B));
        AppSettings.LastPenColorArgb = argb;
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

    /// <summary>"返回主面板"按钮：触发退出批注请求。</summary>
    private void OnBackClick(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    // ── 绘制 ──

    /// <summary>指针按下：右键弹出操作菜单，左键按当前工具开始绘制（笔/橡皮/图形草稿）。</summary>
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        var position = point.Position;
        UpdateEraserVisual(position, e.OriginalSource);

        // 桌面交互模式：手指触控不落笔（触控笔与鼠标不受影响）
        if (AppSettings.InteractionMode == AnnotationInteractionMode.Desktop
            && e.Pointer.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Touch)
            return;

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
                _eraseRadiusEffective = _eraserRadius;
                _eraseLastPoint = position;
                _eraseLastTimestamp = Stopwatch.GetTimestamp();
                _eraseRemoved.Clear();
                _eraseFragments.Clear();
                EraseAt(position);
                break;

            case AnnotationTool.Pen:
                // 落笔在直尺刻度边或量具可吸附边附近：贴边画直线；否则正常自由笔迹
                if (TrySnapToAnyEdge(position) is { } snapStart)
                {
                    _rulerSnapActive = true;
                    _snapStart = snapStart;
                    _shapeDraft = BeginSnapDraft(snapStart);
                    UpdateSnapLabel(snapStart, 0);
                }
                else
                {
                    _current = BeginStroke(position);
                }
                break;

            case AnnotationTool.Shapes:
                _shapeStart = position;
                _shapeDraft = BeginShapeDraft(position);
                break;

            // 尺规：本阶段占位，不响应绘制
        }
    }

    /// <summary>新建一条几何图形草稿：先挂预览，松手时才入栈提交。
    /// 所有图形统一渲染为“密化轮廓点”的折线，橡皮的逐点命中判定与切割直接复用。</summary>
    private InkStroke BeginShapeDraft(Point p)
    {
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(_penColor),
            StrokeThickness = _shapeThickness,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false
        };
        if (_shapeKind is ShapeKind.DashedArrow or ShapeKind.DashedLine)
        {
            line.StrokeDashArray = [3, 2];
        }
        line.Points.Add(p);

        var draft = new InkStroke
        {
            Visual = line,
            BaseThickness = _shapeThickness,
            Kind = PenKind.Hard,
            Color = _penColor,
            Shape = _shapeKind
        };
        draft.Points.Add(p);

        InkLayer.Children.Add(line);
        return draft;
    }

    /// <summary>拖动时按当前图形类型重建轮廓点并刷新预览。</summary>
    private void UpdateShapeDraft(Point end)
    {
        var draft = _shapeDraft!;
        var line = (Polyline)draft.Visual;

        var points = BuildShapePoints(_shapeKind, _shapeStart, end);
        draft.Points.Clear();
        draft.Points.AddRange(points);
        line.Points.Clear();
        foreach (var p in points) line.Points.Add(p);
    }

    // ── 图形轮廓点生成：相邻点间距 ≤ ShapeSampleStep，保证橡皮命中判定准确 ──

    private const double ShapeSampleStep = 6;

    /// <summary>按图形类型生成密化轮廓点（间距 ≤ ShapeSampleStep），供渲染与橡皮命中判定复用。</summary>
    private static List<Point> BuildShapePoints(ShapeKind kind, Point a, Point b)
    {
        switch (kind)
        {
            case ShapeKind.Arrow:
            case ShapeKind.DashedArrow:
                return AppendHead(DensifySegment(a, b), a, b);

            case ShapeKind.Rectangle:
            {
                ShapeBounds(a, b, out double x1, out double y1, out double x2, out double y2);
                return DensifyPath(
                    [new Point(x1, y1), new Point(x2, y1), new Point(x2, y2), new Point(x1, y2)], closed: true);
            }

            case ShapeKind.RoundedRectangle:
            {
                ShapeBounds(a, b, out double x1, out double y1, out double x2, out double y2);
                return RoundedRectPoints(x1, y1, x2, y2);
            }

            case ShapeKind.Triangle:
            {
                ShapeBounds(a, b, out double x1, out double y1, out double x2, out double y2);
                return DensifyPath(
                    [new Point((x1 + x2) / 2, y1), new Point(x2, y2), new Point(x1, y2)], closed: true);
            }

            case ShapeKind.RightTriangle:
            {
                ShapeBounds(a, b, out double x1, out double y1, out double x2, out double y2);
                return DensifyPath(
                    [new Point(x1, y1), new Point(x1, y2), new Point(x2, y2)], closed: true);
            }

            case ShapeKind.Circle:
            {
                double r = Distance(a, b);
                return EllipsePoints(a, r, r);
            }

            case ShapeKind.Ellipse:
            {
                var center = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
                return EllipsePoints(center, Math.Abs(b.X - a.X) / 2, Math.Abs(b.Y - a.Y) / 2);
            }

            default: // Line
                return DensifySegment(a, b);
        }
    }

    /// <summary>计算两点确定的矩形包围盒。</summary>
    private static void ShapeBounds(Point a, Point b, out double x1, out double y1, out double x2, out double y2)
    {
        x1 = Math.Min(a.X, b.X); y1 = Math.Min(a.Y, b.Y);
        x2 = Math.Max(a.X, b.X); y2 = Math.Max(a.Y, b.Y);
    }

    /// <summary>把一段直线密化成间距 ≤ 6px 的点列（含两端点）。</summary>
    private static List<Point> DensifySegment(Point a, Point b)
    {
        List<Point> points = [a];
        double length = Distance(a, b);
        int steps = (int)(length / ShapeSampleStep);
        for (int i = 1; i <= steps; i++)
        {
            double t = (double)i / (steps + 1);
            points.Add(new Point(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
        }
        points.Add(b);
        return points;
    }

    /// <summary>把顶点序列的每条边密化；closed 时末尾回到起点闭合。</summary>
    private static List<Point> DensifyPath(IReadOnlyList<Point> vertices, bool closed)
    {
        var points = new List<Point>();
        for (int i = 0; i < vertices.Count - 1; i++)
        {
            var segment = DensifySegment(vertices[i], vertices[i + 1]);
            segment.RemoveAt(segment.Count - 1);
            points.AddRange(segment);
        }
        if (closed)
        {
            points.AddRange(DensifySegment(vertices[^1], vertices[0]));
        }
        else
        {
            points.Add(vertices[^1]);
        }
        return points;
    }

    /// <summary>给线段末尾补上箭头两翼（翼长随杆身长度在 8~16 间伸缩）。</summary>
    private static List<Point> AppendHead(List<Point> shaft, Point a, Point b)
    {
        shaft.Add(WingPoint(b, a, 25));
        shaft.Add(b);
        shaft.Add(WingPoint(b, a, -25));
        return shaft;
    }

    /// <summary>箭头一翼的端点：tip 为箭尖，from 决定朝向，angle 为相对箭杆的偏角。</summary>
    private static Point WingPoint(Point tip, Point from, double angleDeg)
    {
        var dir = Unit(from, tip);
        double length = Math.Clamp(Distance(from, tip) * 0.3, 8, 16);
        double angle = angleDeg * Math.PI / 180;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);
        double bx = -dir.X, by = -dir.Y;
        return new Point(
            tip.X + (bx * cos - by * sin) * length,
            tip.Y + (bx * sin + by * cos) * length);
    }

    /// <summary>圆角矩形轮廓：四个 90° 圆角圆弧 + 直边密化，半径随尺寸自适应（≤10）。</summary>
    private static List<Point> RoundedRectPoints(double x1, double y1, double x2, double y2)
    {
        double r = Math.Min(10, Math.Min(x2 - x1, y2 - y1) / 2);
        if (r < 0.5)
        {
            return DensifyPath(
                [new Point(x1, y1), new Point(x2, y1), new Point(x2, y2), new Point(x1, y2)], closed: true);
        }

        var corners = new List<Point>();
        AddArc(corners, new Point(x2 - r, y1 + r), r, -90, 0);
        AddArc(corners, new Point(x2 - r, y2 - r), r, 0, 90);
        AddArc(corners, new Point(x1 + r, y2 - r), r, 90, 180);
        AddArc(corners, new Point(x1 + r, y1 + r), r, 180, 270);
        corners.Add(corners[0]);
        return EnsureDensity(corners);
    }

    /// <summary>向点列追加一段圆弧采样点（7 点近似 90° 弧）。</summary>
    private static void AddArc(List<Point> points, Point center, double r, double fromDeg, double toDeg)
    {
        for (int i = 0; i <= 6; i++)
        {
            double t = (fromDeg + (toDeg - fromDeg) * i / 6) * Math.PI / 180;
            points.Add(new Point(center.X + r * Math.Cos(t), center.Y + r * Math.Sin(t)));
        }
    }

    /// <summary>椭圆轮廓：按拉马努金近似周长决定采样数，首尾重合闭合。</summary>
    private static List<Point> EllipsePoints(Point center, double rx, double ry)
    {
        rx = Math.Max(rx, 0.5);
        ry = Math.Max(ry, 0.5);
        double circumference = Math.PI * (3 * (rx + ry) - Math.Sqrt((3 * rx + ry) * (rx + 3 * ry)));
        int n = Math.Max(24, (int)(circumference / ShapeSampleStep));
        var points = new List<Point>(n + 1);
        for (int i = 0; i <= n; i++)
        {
            double t = 2 * Math.PI * i / n;
            points.Add(new Point(center.X + rx * Math.Cos(t), center.Y + ry * Math.Sin(t)));
        }
        return points;
    }

    /// <summary>对已闭合的轮廓点列，把间距超标的相邻点之间补密化（圆角弧之间的直边）。</summary>
    private static List<Point> EnsureDensity(List<Point> points)
    {
        var result = new List<Point>();
        for (int i = 0; i < points.Count - 1; i++)
        {
            var segment = DensifySegment(points[i], points[i + 1]);
            segment.RemoveAt(segment.Count - 1);
            result.AddRange(segment);
        }
        result.Add(points[^1]);
        return result;
    }

    /// <summary>图形草稿的包围盒最长边，用于判断“太短视为误触”（闭合图形首尾重合不能用端点距离）。</summary>
    private static double ShapeExtent(InkStroke draft)
    {
        double x1 = double.MaxValue, y1 = double.MaxValue;
        double x2 = double.MinValue, y2 = double.MinValue;
        foreach (var p in draft.Points)
        {
            x1 = Math.Min(x1, p.X); y1 = Math.Min(y1, p.Y);
            x2 = Math.Max(x2, p.X); y2 = Math.Max(y2, p.Y);
        }
        return Math.Max(x2 - x1, y2 - y1);
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
            Kind = _penKind,
            Color = _penColor
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
        _undo.Push(new EditAction(
            () => DetachStroke(stroke),
            () => AttachStroke(stroke)));
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

    /// <summary>将闭合轮廓点列转为 Nonzero 填充的 PathGeometry，避免自交处出现空洞。</summary>
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

    /// <summary>返回 from→to 方向的单位向量；零向量时回退为 (1,0)。</summary>
    private static Point Unit(Point from, Point to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        return length < 0.0001 ? new Point(1, 0) : new Point(dx / length, dy / length);
    }

    /// <summary>返回 a→b 方向的左旋法线单位向量。</summary>
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

    /// <summary>指针移动：橡皮持续擦除、图形草稿实时预览、画笔追加采样点（过滤微抖动）。</summary>
    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        var p = point.Position;
        UpdateEraserVisual(p, e.OriginalSource);

        if (e.Pointer.PointerId != _activePointerId) return;

        if (_erasing)
        {
            if (!point.Properties.IsLeftButtonPressed) return;

            // 笔速橡皮（实验）：移动越快半径越大，慢速缩小；平滑过渡并限制在 0.6~2.2 倍之间
            if (AppSettings.SpeedEraser)
            {
                long now = Stopwatch.GetTimestamp();
                double dtMs = (now - _eraseLastTimestamp) * 1000.0 / Stopwatch.Frequency;
                double segment = Distance(p, _eraseLastPoint);
                _eraseLastPoint = p;
                _eraseLastTimestamp = now;
                if (dtMs > 0.5)
                {
                    double speed = segment / dtMs;   // px/ms
                    double target = Math.Clamp(
                        _eraserRadius * (0.65 + speed * 0.5),
                        _eraserRadius * 0.6, _eraserRadius * 2.2);
                    _eraseRadiusEffective += (target - _eraseRadiusEffective) * 0.35;
                }
            }
            else
            {
                _eraseRadiusEffective = _eraserRadius;
            }

            UpdateEraserVisual(p, e.OriginalSource);
            EraseAt(p);
            return;
        }

        if (_rulerSnapActive)
        {
            UpdateRulerSnap(p);
            return;
        }

        if (_shapeDraft is not null)
        {
            UpdateShapeDraft(p);
            return;
        }

        if (_current is null) return;
        var last = _current.Points[^1];
        // 过滤抖动：采样点间距过小会让法线方向乱跳，轮廓更容易自交；间距档位由设置决定
        double minDistance = MinSampleDistance;
        if (Math.Abs(p.X - last.X) < minDistance && Math.Abs(p.Y - last.Y) < minDistance) return;
        AppendStrokePoint(_current, p);
    }

    /// <summary>采样点最小间距：高频更密（笔迹更细腻），低频更疏（更省性能）。</summary>
    private static double MinSampleDistance => AppSettings.SampleRate switch
    {
        AnnotationSampleRate.High => 0.6,
        AnnotationSampleRate.Low => 2.4,
        _ => 1.2
    };

    /// <summary>指针离开画布：隐藏橡皮贴图。</summary>
    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => HideEraserVisual();

    /// <summary>指针抬起：释放捕获并结束当前笔迹/草稿。</summary>
    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _activePointerId) return;

        _activePointerId = 0;
        Root.ReleasePointerCapture(e.Pointer);
        FinishStroke();
    }

    /// <summary>指针捕获丢失：重置活动指针并结束绘制。</summary>
    private void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (e.Pointer.PointerId != _activePointerId) return;
        _activePointerId = 0;
        FinishStroke();
    }

    /// <summary>结束当前笔迹；点一下不拖则补一个极短线段，让它显示为圆点。软笔此时收笔出锋。
    /// 几何图形草稿在此提交：太短视为误触直接丢弃，否则作为一条可撤销的笔迹入栈。</summary>
    private void FinishStroke()
    {
        // 贴边画线结束：复位吸附状态并隐藏长度读数（草稿按下方图形流程提交）
        if (_rulerSnapActive)
        {
            _rulerSnapActive = false;
            HideSnapLabel();
        }

        if (_shapeDraft is not null)
        {
            var draft = _shapeDraft;
            _shapeDraft = null;

            if (ShapeExtent(draft) < 3)
            {
                InkLayer.Children.Remove(draft.Visual);
            }
            else
            {
                _strokes.Add(draft);
                _redo.Clear();
                _undo.Push(new EditAction(
                    () => DetachStroke(draft),
                    () => AttachStroke(draft)));
            }
        }

        if (_current is not null)
        {
            FinishPenStroke(_current);
        }
        _current = null;
        CommitErase();
        _erasing = false;
    }

    /// <summary>结束一条笔迹：点一下不拖则补一个极短线段，让它显示为圆点。
    /// 软笔按「抬笔时笔锋修正」设置决定是否做收笔平滑与出锋。</summary>
    private static void FinishPenStroke(InkStroke stroke)
    {
        if (stroke.Points.Count == 1)
        {
            var p = stroke.Points[0];
            var p2 = new Point(p.X + 0.5, p.Y);
            stroke.Points.Add(p2);
            stroke.HalfWidths.Add(stroke.HalfWidths.Count > 0 ? stroke.HalfWidths[^1] : 1);
            if (stroke.Visual is Polyline line) line.Points.Add(p2);
        }

        if (stroke.Kind == PenKind.Soft)
        {
            // 书写期间只画平滑的等宽变化；收笔后再整体平滑并生成笔锋
            if (AppSettings.PenTaperCorrection)
            {
                SmoothHalfWidths(stroke);
                TaperSoftInkEnd(stroke);
            }
            RebuildSoftInk(stroke);
        }
    }

    // ── 橡皮：面积擦 —— 擦除范围内的采样点被剔除，笔迹剩余部分拆成多段保留 ──

    /// <summary>以 position 为圆心、按擦除半径逐条切割笔迹，剩余片段原地替换。</summary>
    private void EraseAt(Point position)
    {
        for (int i = _strokes.Count - 1; i >= 0; i--)
        {
            var stroke = _strokes[i];
            var fragments = FragmentStroke(stroke, position);
            if (fragments is null) continue;      // 没擦到，保持原样

            // 记录原位：片段视觉插回被擦笔迹原来的图层位置，保证批注层级固定、后画恒覆盖先画
            int visualIndex = InkLayer.Children.IndexOf(stroke.Visual);
            _strokes.RemoveAt(i);
            InkLayer.Children.Remove(stroke.Visual);

            // 拖动中切出来的片段再被切时，只需从片段集合里摘掉，撤销时才能还原到拖动前的状态
            if (!_eraseFragments.Remove(stroke))
            {
                _eraseRemoved.Add(stroke);
                _eraseRemovedIndex[stroke] = i;
            }
            _eraseFragmentIndex.Remove(stroke);

            for (int f = 0; f < fragments.Count; f++)
            {
                _strokes.Insert(i + f, fragments[f]);
                if (visualIndex >= 0) InkLayer.Children.Insert(visualIndex + f, fragments[f].Visual);
                else InkLayer.Children.Add(fragments[f].Visual);
                _eraseFragments.Add(fragments[f]);
                _eraseFragmentIndex[fragments[f]] = i + f;
            }
            _redo.Clear();
        }
    }

    /// <summary>按擦除圆命中判断：返回 null 表示完全没擦到；返回空列表表示整条被擦掉；
    /// 否则返回剩余连续段的片段笔迹。</summary>
    private List<InkStroke>? FragmentStroke(InkStroke stroke, Point eraser)
    {
        var points = stroke.Points;
        int count = points.Count;
        var erased = new bool[count];
        bool hit = false;

        for (int i = 0; i < count; i++)
        {
            double half = HalfWidthAt(stroke, i);
            if (Distance(eraser, points[i]) <= _eraseRadiusEffective + half)
            {
                erased[i] = true;
                hit = true;
            }
        }
        if (!hit) return null;

        var fragments = new List<InkStroke>();
        int start = -1;
        for (int i = 0; i <= count; i++)
        {
            bool keep = i < count && !erased[i];
            if (keep)
            {
                if (start < 0) start = i;
            }
            else if (start >= 0)
            {
                fragments.Add(CreateFragment(stroke, start, i - 1));
                start = -1;
            }
        }
        return fragments;
    }

    /// <summary>取笔迹指定索引处的半宽：软笔读 HalfWidths，其余取基准线宽的一半。</summary>
    private static double HalfWidthAt(InkStroke stroke, int index)
        => stroke.Kind == PenKind.Soft && index < stroke.HalfWidths.Count
            ? stroke.HalfWidths[index]
            : stroke.BaseThickness / 2;

    /// <summary>用原笔迹的一段采样点造出片段，保持颜色、笔类型与线宽。</summary>
    private static InkStroke CreateFragment(InkStroke source, int from, int to)
    {
        var points = new List<Point>();
        var halfWidths = new List<double>();
        for (int i = from; i <= to; i++)
        {
            points.Add(source.Points[i]);
            halfWidths.Add(HalfWidthAt(source, i));
        }

        // 单点片段补一个极短线段，让它显示为圆点
        if (points.Count == 1)
        {
            points.Add(new Point(points[0].X + 0.5, points[0].Y));
            halfWidths.Add(halfWidths[0]);
        }

        var brush = new SolidColorBrush(source.Color);
        Shape visual;
        if (source.Kind == PenKind.Soft)
        {
            visual = new Path { Fill = brush, IsHitTestVisible = false };
        }
        else
        {
            visual = new Polyline
            {
                Stroke = brush,
                StrokeThickness = source.BaseThickness,
                Opacity = source.Kind == PenKind.Highlighter ? 0.35 : 1,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = source.Kind == PenKind.Highlighter ? PenLineCap.Flat : PenLineCap.Round,
                StrokeEndLineCap = source.Kind == PenKind.Highlighter ? PenLineCap.Flat : PenLineCap.Round,
                IsHitTestVisible = false
            };
        }

        var fragment = new InkStroke
        {
            Visual = visual,
            BaseThickness = source.BaseThickness,
            Kind = source.Kind,
            Color = source.Color,
            Shape = source.Shape
        };
        fragment.Points.AddRange(points);
        fragment.HalfWidths.AddRange(halfWidths);

        if (visual is Polyline line)
        {
            // 虚线类图形的片段保持虚线样式
            if (source.Shape is ShapeKind.DashedArrow or ShapeKind.DashedLine)
            {
                line.StrokeDashArray = [3, 2];
            }
            foreach (var p in points) line.Points.Add(p);
        }
        else
        {
            RebuildSoftInk(fragment);
        }
        return fragment;
    }

    /// <summary>一次橡皮拖动结束后合并成一条撤销记录；撤销/重做都按原始下标恢复，层级保持稳定。</summary>
    private void CommitErase()
    {
        if (_eraseRemoved.Count == 0) return;

        var removed = _eraseRemoved
            .OrderBy(s => _eraseRemovedIndex.TryGetValue(s, out var idx) ? idx : int.MaxValue)
            .Select(s => (stroke: s, index: _eraseRemovedIndex.TryGetValue(s, out var idx) ? idx : int.MaxValue))
            .ToList();
        var fragments = _eraseFragments
            .OrderBy(s => _eraseFragmentIndex.TryGetValue(s, out var idx) ? idx : int.MaxValue)
            .Select(s => (stroke: s, index: _eraseFragmentIndex.TryGetValue(s, out var idx) ? idx : int.MaxValue))
            .ToList();
        _eraseRemoved.Clear();
        _eraseFragments.Clear();
        _eraseRemovedIndex.Clear();
        _eraseFragmentIndex.Clear();

        _undo.Push(new EditAction(
            () =>
            {
                foreach (var f in fragments) DetachStroke(f.stroke);
                foreach (var s in removed) AttachStrokeAt(s.stroke, s.index);
            },
            () =>
            {
                foreach (var s in removed) DetachStroke(s.stroke);
                foreach (var f in fragments) AttachStrokeAt(f.stroke, f.index);
            }));
    }

    /// <summary>将笔迹挂回集合与画布（撤销/重做共用）：追加到顶层，用于新笔迹。</summary>
    private void AttachStroke(InkStroke stroke)
    {
        _strokes.Add(stroke);
        InkLayer.Children.Add(stroke.Visual);
    }

    /// <summary>将笔迹按原始下标挂回集合与画布（橡皮撤销/重做共用）：层级与擦除前一致。</summary>
    private void AttachStrokeAt(InkStroke stroke, int index)
    {
        int at = Math.Min(index, _strokes.Count);
        _strokes.Insert(at, stroke);
        InkLayer.Children.Insert(Math.Min(at, InkLayer.Children.Count), stroke.Visual);
    }

    /// <summary>从集合与画布移除笔迹（撤销/重做共用）。</summary>
    private void DetachStroke(InkStroke stroke)
    {
        _strokes.Remove(stroke);
        InkLayer.Children.Remove(stroke.Visual);
    }

    /// <summary>计算两点间的欧氏距离。</summary>
    private static double Distance(Point a, Point b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    // ── 撤销 / 重做 / 清空（键盘与右键菜单共用） ──

    /// <summary>撤销上一步编辑操作。</summary>
    public void Undo()
    {
        FinishStroke();
        if (_undo.Count == 0) return;

        var action = _undo.Pop();
        action.Undo();
        _redo.Push(action);
    }

    /// <summary>重做上一步撤销的操作。</summary>
    public void Redo()
    {
        FinishStroke();
        if (_redo.Count == 0) return;

        var action = _redo.Pop();
        action.Redo();
        _undo.Push(action);
    }

    /// <summary>清空所有笔迹与撤销/重做栈（视为开启新的一页）。</summary>
    public void ClearAll()
    {
        FinishStroke();
        // 清空视为开启新的一页：不做撤销
        _strokes.Clear();
        _undo.Clear();
        _redo.Clear();
        InkLayer.Children.Clear();
    }

    // ── 快捷键（需窗口为前台，点击画布即可） ──

    /// <summary>注册键盘快捷键：Ctrl+Z 撤销、Ctrl+Y/Ctrl+Shift+Z 重做、Ctrl+Delete 清空、Esc 退出。</summary>
    private void AddAccelerators()
    {
        AddAccelerator(VirtualKey.Z, VirtualKeyModifiers.Control, Undo);
        AddAccelerator(VirtualKey.Y, VirtualKeyModifiers.Control, Redo);
        AddAccelerator(VirtualKey.Z, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift, Redo);
        AddAccelerator(VirtualKey.Delete, VirtualKeyModifiers.Control, ClearAll);
        AddAccelerator(VirtualKey.Escape, VirtualKeyModifiers.None,
            () => ExitRequested?.Invoke());
    }

    /// <summary>注册单条键盘快捷键到根元素。</summary>
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

    /// <summary>在指定位置弹出右键菜单（撤销 / 重做 / 清空批注）。</summary>
    private void ShowActionMenu(Point position)
    {
        var menu = new MenuFlyout();

        var undo = new MenuFlyoutItem { Text = "撤销", IsEnabled = _undo.Count > 0 };
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

    /// <summary>进场动画：整体淡入 + 工具栏从上方向下滑入。「动画级别=无」时直接呈现。</summary>
    private void PlayEnter()
    {
        if (AppSettings.AnimationLevel == AnimationLevel.None) return;

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

    /// <summary>构造一条启用依赖动画的 DoubleAnimation（供 Storyboard 使用）。</summary>
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

    /// <summary>淡出后执行 onDone（由调用方关闭窗口）。「动画级别=无」时立即回调。</summary>
    public void PlayExit(Action onDone)
    {
        if (AppSettings.AnimationLevel == AnimationLevel.None)
        {
            onDone();
            return;
        }

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
