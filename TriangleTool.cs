using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace ClassDock;

/// <summary>三角板规格：等腰直角 / 30°-60° 直角。</summary>
internal enum TriangleKind
{
    FortyFive,
    ThirtySixty
}

/// <summary>
/// 一块三角板：半透明板体 + 三条边的毫米刻度 + 直角标记与角度文字，
/// 直角顶点同时是拖动/旋转/缩放的中心。三边都可贴边画线。
/// </summary>
internal sealed class TriangleTool : MeasureTool
{
    /// <summary>板体外留给按钮与描边的空白。</summary>
    private const double Pad = 26;

    private const double MinSizeMm = 40;
    private const double MaxSizeMm45 = 120;
    private const double MaxSizeMm3060 = 90;

    private readonly TriangleKind _kind;
    private readonly List<(Point A, Point B)> _edges = [];

    private double _sizeMm;          // 45°：两条直角边；30-60：短直角边
    private Point _vertexA;          // 直角顶点（左下）
    private Point _vertexB;          // 右下
    private Point _vertexC;          // 上
    private Point _centroid;

    public TriangleTool(Canvas layer, TriangleKind kind) : base(layer)
    {
        _kind = kind;
        _sizeMm = kind == TriangleKind.FortyFive ? 80 : 60;
        Rebuild();
    }

    public override MeasureKind Kind => _kind == TriangleKind.FortyFive
        ? MeasureKind.Triangle45
        : MeasureKind.Triangle3060;

    public override string DisplayName => _kind == TriangleKind.FortyFive
        ? "45° 三角板"
        : "30°-60° 三角板";

    public override IReadOnlyList<(Point A, Point B)> SnapEdges => _edges;

    protected override Point LocalCentroid => _centroid;

    protected override double ResizeSize => _sizeMm;

    /// <summary>短边（45° 时为直角边，30-60 时为竖直边）的像素长度。</summary>
    private double LegPx => _sizeMm * MeasureScale.Mm;

    /// <summary>水平直角边的像素长度（30-60 为短边的 √3 倍）。</summary>
    private double LongPx => _kind == TriangleKind.FortyFive ? LegPx : LegPx * Math.Sqrt(3);

    protected override bool SetResizeSize(double mm)
    {
        double max = _kind == TriangleKind.FortyFive ? MaxSizeMm45 : MaxSizeMm3060;
        int rounded = (int)Math.Round(Math.Clamp(mm, MinSizeMm, max));
        if (rounded == _sizeMm) return false;
        _sizeMm = rounded;
        Rebuild();
        return true;
    }

    /// <summary>按当前尺寸重建整块三角板（直角顶点保持不动）。</summary>
    private void Rebuild()
    {
        Root.Children.Clear();
        _edges.Clear();

        double w = LongPx;
        double h = LegPx;
        _vertexA = new Point(Pad, Pad + h);
        _vertexB = new Point(Pad + w, Pad + h);
        _vertexC = new Point(Pad, Pad);
        Root.Width = w + Pad * 2;
        Root.Height = h + Pad * 2;
        _centroid = new Point(
            (_vertexA.X + _vertexB.X + _vertexC.X) / 3,
            (_vertexA.Y + _vertexB.Y + _vertexC.Y) / 3);
        Pivot = _vertexA;

        // 板体
        var figure = new PathFigure { StartPoint = _vertexA, IsClosed = true };
        figure.Segments.Add(new LineSegment { Point = _vertexB });
        figure.Segments.Add(new LineSegment { Point = _vertexC });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        var body = new Path
        {
            Data = geometry,
            Fill = MeasureToolVisuals.BodyFill,
            Stroke = MeasureToolVisuals.BodyBorder,
            StrokeThickness = 1
        };
        body.PointerPressed += (_, e) => HandleBodyPressed(e);
        Root.Children.Add(body);

        // 三边刻度：短直角边 → 长直角边 → 斜边（与吸附优先级一致）
        _edges.Add((_vertexA, _vertexC));
        _edges.Add((_vertexA, _vertexB));
        _edges.Add((_vertexB, _vertexC));
        MeasureScale.AddLinearEdge(Root, _vertexA, _vertexC, _centroid);
        MeasureScale.AddLinearEdge(Root, _vertexA, _vertexB, _centroid);
        MeasureScale.AddLinearEdge(Root, _vertexB, _vertexC, _centroid);

        AddRightAngleMark();

        if (_kind == TriangleKind.FortyFive)
        {
            AddAngleLabel("90°", _vertexA);
            AddAngleLabel("45°", _vertexB);
            AddAngleLabel("45°", _vertexC);
        }
        else
        {
            AddAngleLabel("90°", _vertexA);
            AddAngleLabel("30°", _vertexB);
            AddAngleLabel("60°", _vertexC);
        }

        AddButtons();
        ApplyTransform();
    }

    /// <summary>直角处的小方框标记。</summary>
    private void AddRightAngleMark()
    {
        double abx = _vertexB.X - _vertexA.X, aby = _vertexB.Y - _vertexA.Y;
        double abLen = Math.Sqrt(abx * abx + aby * aby);
        double acx = _vertexC.X - _vertexA.X, acy = _vertexC.Y - _vertexA.Y;
        double acLen = Math.Sqrt(acx * acx + acy * acy);
        if (abLen < 1 || acLen < 1) return;

        const double side = 13;
        var p1 = new Point(_vertexA.X + abx / abLen * side, _vertexA.Y + aby / abLen * side);
        var p3 = new Point(_vertexA.X + acx / acLen * side, _vertexA.Y + acy / acLen * side);
        var polyline = new Polyline
        {
            Stroke = MeasureToolVisuals.TickStroke,
            StrokeThickness = 0.9,
            IsHitTestVisible = false
        };
        polyline.Points.Add(p1);
        polyline.Points.Add(new Point(p1.X + p3.X - _vertexA.X, p1.Y + p3.Y - _vertexA.Y));
        polyline.Points.Add(p3);
        Root.Children.Add(polyline);
    }

    /// <summary>角度文字：从顶点朝板体内部偏一点，避免压住刻度线。</summary>
    private void AddAngleLabel(string text, Point vertex)
    {
        double dx = _centroid.X - vertex.X, dy = _centroid.Y - vertex.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 0.01) return;

        var label = new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = MeasureToolVisuals.NumberForeground,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, vertex.X + dx / len * 26 - 10);
        Canvas.SetTop(label, vertex.Y + dy / len * 26 - 7);
        Root.Children.Add(label);
    }

    /// <summary>四个动作按钮沿直角平分线竖排，等距落在斜边之前。</summary>
    private void AddButtons()
    {
        // 直角顶点沿平分线到斜边的距离：√2·p·q/(p+q)
        double p = LongPx, q = LegPx;
        double bisector = Math.Sqrt(2) * p * q / (p + q);

        void Place(FrameworkElement button, double fraction)
        {
            double d = bisector * fraction / Math.Sqrt(2);
            Canvas.SetLeft(button, _vertexA.X + d - MeasureToolVisuals.ButtonSize / 2);
            Canvas.SetTop(button, _vertexA.Y - d - MeasureToolVisuals.ButtonSize / 2);
            Root.Children.Add(button);
        }

        Place(MeasureToolVisuals.MakeButton(
            MeasureToolVisuals.BuildCloseIcon(), $"收回{DisplayName}", OnClosePressed), 0.12);
        Place(MeasureToolVisuals.MakeButton(
            MeasureToolVisuals.BuildGlyph("\uE8A3", 13), "缩放（100% / 125% / 150%）", OnZoomPressed), 0.35);
        Place(MeasureToolVisuals.MakeButton(
            MeasureToolVisuals.BuildExpandIcon(), "按住左右滑动调整尺寸", OnResizePressed), 0.58);
        Place(MeasureToolVisuals.MakeButton(
            MeasureToolVisuals.BuildGlyph("\uE72C", 13), "按住拖动旋转，Shift 以 1° 吸附", OnRotatePressed), 0.81);
    }
}
