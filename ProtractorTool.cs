using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace ClassDock;

/// <summary>
/// 量角器：半透明半圆板体 + 双圈角度刻度，底边固定一条 0° 基准射线、另一条可绕圆心旋转；
/// 两条射线的夹角以两位小数实时显示。只有底边参与贴边画线。
/// </summary>
internal sealed class ProtractorTool : MeasureTool
{
    /// <summary>板体外留给按钮与描边的空白。</summary>
    private const double Pad = 34;

    /// <summary>射线末端的圆形抓手命中区边长（大于视觉圆点，方便触摸）。</summary>
    private const double HandleSize = 34;

    /// <summary>抓手圆点的视觉直径。</summary>
    private const double HandleDot = 14;

    private const double MinRadiusMm = 60;
    private const double MaxRadiusMm = 140;

    private readonly List<(Point A, Point B)> _edges = [];

    private double _radiusMm = 100;
    private double _radiusPx;
    private double _rayAngleRaw = 39;   // 局部角度（度，逆时针为正），旋转过程中不取整
    private double _rayGrabOffset;

    private Point _center;
    private Line _rayLine = null!;
    private Border _handle = null!;
    private TextBlock _reading = null!;

    public ProtractorTool(Canvas layer) : base(layer)
    {
        Rebuild();
    }

    public override MeasureKind Kind => MeasureKind.Protractor;

    public override string DisplayName => "量角器";

    public override IReadOnlyList<(Point A, Point B)> SnapEdges => _edges;

    protected override Point LocalCentroid =>
        new(_center.X, _center.Y - _radiusPx * 0.4);

    protected override double ResizeSize => _radiusMm;

    protected override bool SetResizeSize(double mm)
    {
        int rounded = (int)Math.Round(Math.Clamp(mm, MinRadiusMm, MaxRadiusMm));
        if (rounded == (int)_radiusMm) return false;
        _radiusMm = rounded;
        Rebuild();
        return true;
    }

    // ── 可旋转射线 ──

    /// <summary>按住抓手拖动：按指针方向更新射线，内部用 double 保存，不吸附、不取整。</summary>
    public override void DragRay(Point p) => ApplyRayAngle(LocalRayAngle(p) + _rayGrabOffset);

    /// <summary>当前折叠角：射线转到任意方向后取与基准射线之间的小于等于 180° 的那个角。</summary>
    private double Folded => Math.Abs(_rayAngleRaw);

    /// <summary>指针方向换算到量具局部角度（量具整体旋转时一起跟随）。</summary>
    private double LocalRayAngle(Point p)
    {
        double screen = Math.Atan2(p.Y - Anchor.Y, p.X - Anchor.X) * 180 / Math.PI;
        return Normalize(Angle - screen);
    }

    private void ApplyRayAngle(double degrees)
    {
        _rayAngleRaw = Normalize(degrees);
        UpdateRay();
    }

    // ── 构建 ──

    private void Rebuild()
    {
        Root.Children.Clear();
        _edges.Clear();

        _radiusPx = _radiusMm * MeasureScale.Mm;
        double r = _radiusPx;
        _center = new Point(Pad + r, Pad + r);
        Root.Width = 2 * r + Pad * 2;
        Root.Height = r + Pad * 2 + 40;
        Pivot = _center;

        // 半圆板体：从右端逆时针扫到左端，收口沿底边闭合
        var figure = new PathFigure
        {
            StartPoint = new Point(_center.X + r, _center.Y),
            IsClosed = true
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(_center.X - r, _center.Y),
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Counterclockwise,
            IsLargeArc = false
        });
        var bodyGeometry = new PathGeometry();
        bodyGeometry.Figures.Add(figure);
        var body = new Path
        {
            Data = bodyGeometry,
            Fill = MeasureToolVisuals.BodyFill,
            Stroke = MeasureToolVisuals.BodyBorder,
            StrokeThickness = 1
        };
        body.PointerPressed += (_, e) => HandleBodyPressed(e);
        Root.Children.Add(body);

        // 刻度带：外弧到内弧之间的环形，略亮于板体
        double bandInner = r - 24;
        var bandFigure = new PathFigure
        {
            StartPoint = new Point(_center.X + r, _center.Y),
            IsClosed = true
        };
        bandFigure.Segments.Add(new ArcSegment
        {
            Point = new Point(_center.X - r, _center.Y),
            Size = new Size(r, r),
            SweepDirection = SweepDirection.Counterclockwise,
            IsLargeArc = false
        });
        bandFigure.Segments.Add(new LineSegment { Point = new Point(_center.X - bandInner, _center.Y) });
        bandFigure.Segments.Add(new ArcSegment
        {
            Point = new Point(_center.X + bandInner, _center.Y),
            Size = new Size(bandInner, bandInner),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = false
        });
        var bandGeometry = new PathGeometry { FillRule = FillRule.EvenOdd };
        bandGeometry.Figures.Add(bandFigure);
        Root.Children.Add(new Path
        {
            Data = bandGeometry,
            Fill = MeasureToolVisuals.TickBandFill,
            IsHitTestVisible = false
        });

        MeasureScale.AddArcScale(Root, _center, r);

        // 底边刻度尺：吸附与读数都用这条边
        _edges.Add((new Point(_center.X - r, _center.Y), new Point(_center.X + r, _center.Y)));

        // 固定 0° 基准射线
        Root.Children.Add(new Line
        {
            X1 = _center.X,
            Y1 = _center.Y,
            X2 = _center.X + r * 1.04,
            Y2 = _center.Y,
            Stroke = MeasureToolVisuals.RayStroke,
            StrokeThickness = 1.6,
            IsHitTestVisible = false
        });

        // 圆心圆点
        var dot = new Ellipse
        {
            Width = 6,
            Height = 6,
            Fill = MeasureToolVisuals.RayStroke,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(dot, _center.X - 3);
        Canvas.SetTop(dot, _center.Y - 3);
        Root.Children.Add(dot);

        // 可旋转射线
        _rayLine = new Line
        {
            Stroke = MeasureToolVisuals.RayStroke,
            StrokeThickness = 1.6,
            IsHitTestVisible = false
        };
        Root.Children.Add(_rayLine);

        // 射线末端的白色圆形抓手（命中区比视觉圆点大，方便触摸）
        _handle = new Border
        {
            Width = HandleSize,
            Height = HandleSize,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = new Ellipse
            {
                Width = HandleDot,
                Height = HandleDot,
                Fill = new SolidColorBrush(Microsoft.UI.Colors.White),
                Stroke = MeasureToolVisuals.RayStroke,
                StrokeThickness = 1.5
            }
        };
        ToolTipService.SetToolTip(_handle, "按住拖动旋转射线");
        _handle.PointerPressed += OnHandlePressed;
        Root.Children.Add(_handle);

        // 夹角读数
        _reading = new TextBlock
        {
            Width = 110,
            TextAlignment = TextAlignment.Center,
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = MeasureToolVisuals.RayStroke,
            IsHitTestVisible = false
        };
        Root.Children.Add(_reading);

        AddButtons();
        UpdateRay();
        ApplyTransform();
    }

    private void AddButtons()
    {
        double r = _radiusPx;
        double bottom = _center.Y + 10;

        var close = MeasureToolVisuals.MakeButton(
            MeasureToolVisuals.BuildCloseIcon(), "收回到量角器", OnClosePressed);
        Canvas.SetLeft(close, Pad + 4);
        Canvas.SetTop(close, bottom);
        Root.Children.Add(close);

        var rotate = MeasureToolVisuals.MakeButton(
            MeasureToolVisuals.BuildGlyph("\uE72C", 13), "按住拖动旋转，Shift 以 1° 吸附", OnRotatePressed);
        Canvas.SetLeft(rotate, Pad + 2 * r - 4 - MeasureToolVisuals.ButtonSize);
        Canvas.SetTop(rotate, bottom);
        Root.Children.Add(rotate);

        var resize = MeasureToolVisuals.MakeButton(
            MeasureToolVisuals.BuildExpandIcon(), "按住左右滑动调整半径", OnResizePressed);
        Canvas.SetLeft(resize, Pad + 2 * r - 4 - MeasureToolVisuals.ButtonSize * 2 - 12);
        Canvas.SetTop(resize, bottom);
        Root.Children.Add(resize);
    }

    /// <summary>刷新可旋转射线、抓手与夹角读数（读数避让两条射线与圆心）。</summary>
    private void UpdateRay()
    {
        double r = _radiusPx;
        double rad = _rayAngleRaw * Math.PI / 180;
        double cos = Math.Cos(rad), sin = Math.Sin(rad);
        double tipX = _center.X + r * 1.04 * cos;
        double tipY = _center.Y - r * 1.04 * sin;

        _rayLine.X1 = _center.X;
        _rayLine.Y1 = _center.Y;
        _rayLine.X2 = tipX;
        _rayLine.Y2 = tipY;
        Canvas.SetLeft(_handle, tipX - HandleSize / 2);
        Canvas.SetTop(_handle, tipY - HandleSize / 2);

        double folded = Folded;
        _reading.Text = $"∠{folded:0.00}°";

        // 夹角很小时平分线贴着基准射线，改放到正上方；否则放在两射线之间的平分线上
        double placeAngle = folded < 14 ? 90 : _rayAngleRaw / 2;
        double placeRad = placeAngle * Math.PI / 180;
        double dist = (folded < 14 ? 0.40 : 0.58) * r;
        double lx = _center.X + dist * Math.Cos(placeRad);
        double ly = _center.Y - dist * Math.Sin(placeRad);
        Canvas.SetLeft(_reading, lx - 55);
        Canvas.SetTop(_reading, ly - 12);
    }

    private void OnHandlePressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        BringToFront();
        var p = e.GetCurrentPoint(Layer).Position;
        _rayGrabOffset = _rayAngleRaw - LocalRayAngle(p);
        RayDragging = true;
        Layer.CapturePointer(e.Pointer);
    }

    private static double Normalize(double angle)
    {
        angle %= 360;
        if (angle > 180) angle -= 360;
        else if (angle <= -180) angle += 360;
        return angle;
    }
}
