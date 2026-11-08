using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace ClassDock;

/// <summary>新增量具的种类。</summary>
internal enum MeasureKind
{
    Protractor,
    Triangle45,
    Triangle3060
}

/// <summary>量具上的手势状态。</summary>
internal enum MeasureGesture
{
    None,
    Drag,
    Rotate,
    Resize
}

/// <summary>贴边吸附结果（坐标均为尺规图层坐标系）。</summary>
internal readonly record struct MeasureSnap(
    Point Start,
    Point Dir,
    double Length,
    Point Outward,
    Point Projected);

/// <summary>
/// 量具基类：一份 Root 画布 + 旋转中心 + 拖动/旋转/缩放/调尺寸手势。
/// 直尺与圆规沿用窗口里已完成的实现，这个基类只服务于量角器与两块三角板。
/// </summary>
internal abstract class MeasureTool
{
    protected MeasureTool(Canvas layer)
    {
        Layer = layer;
        Root = new Canvas { UseLayoutRounding = true };
        // 让窗口能通过可视树反查指针落在哪只量具上（滚轮旋转归属）
        Root.Tag = this;
    }

    /// <summary>全局缩放档位，与直尺一致。</summary>
    public static readonly double[] ZoomSteps = [1.0, 1.25, 1.5];

    /// <summary>贴边吸附容差（逻辑像素）。</summary>
    public const double SnapTolerance = 14;

    /// <summary>两条边垂距差在这个范围内时，按声明顺序取先生效的那条。</summary>
    public const double SnapTieBand = 3;

    protected Canvas Layer { get; }

    /// <summary>量具的可视根：局部坐标，缩放与旋转都作用在它上面。</summary>
    public Canvas Root { get; }

    public abstract MeasureKind Kind { get; }

    public abstract string DisplayName { get; }

    /// <summary>旋转中心（Root 局部坐标）。</summary>
    public Point Pivot { get; protected set; }

    /// <summary>旋转中心落在尺规图层上的位置。</summary>
    public Point Anchor { get; private set; }

    public int ZoomIndex { get; private set; }

    public double Zoom => ZoomSteps[ZoomIndex];

    /// <summary>当前旋转角（度，顺时针为正，已含 Shift 吸附）。</summary>
    public double Angle { get; private set; }

    public MeasureGesture Gesture { get; private set; }

    /// <summary>是否正在拖动可旋转射线（量角器）。</summary>
    public bool RayDragging { get; protected set; }

    /// <summary>是否有手势正在进行（窗口据此决定是否接管指针事件）。</summary>
    public bool IsBusy => Gesture != MeasureGesture.None || RayDragging;

    /// <summary>关闭按钮：由窗口把这只量具从图层移除。</summary>
    public event Action<MeasureTool>? CloseRequested;

    /// <summary>窗口注入：画笔工具下按在可吸附边附近时返回 true，量具放行事件交给画布。</summary>
    public Func<Point, bool>? SnapPassThrough { get; set; }

    protected Point GestureStart { get; private set; }

    private Point _gestureAnchor;
    private double _gestureStartSize;
    private double _lastPointerAngle;
    private double _rawAngle;

    /// <summary>可吸附的边（Root 局部坐标），按优先级排列：直角边优先、斜边最后。</summary>
    public abstract IReadOnlyList<(Point A, Point B)> SnapEdges { get; }

    /// <summary>板体内部一点，用来确定刻度的悬挂方向与读数标签的外法线。</summary>
    protected abstract Point LocalCentroid { get; }

    /// <summary>长度调整按钮对应的当前尺寸（毫米）。</summary>
    protected abstract double ResizeSize { get; }

    /// <summary>设置尺寸（毫米）并按需重建；返回是否发生了变化。</summary>
    protected abstract bool SetResizeSize(double mm);

    // ── 位置 / 变换 ──

    /// <summary>把旋转中心放到尺规图层的指定位置。</summary>
    public void PlaceAt(Point anchor)
    {
        Anchor = anchor;
        ApplyTransform();
    }

    /// <summary>刷新缩放与旋转（均围绕 Pivot），并让 Pivot 落在 Anchor 上。</summary>
    public void ApplyTransform()
    {
        double scale = Zoom;
        Root.RenderTransformOrigin = new Point(0, 0);
        Root.RenderTransform = new TransformGroup
        {
            Children =
            {
                new ScaleTransform
                {
                    ScaleX = scale,
                    ScaleY = scale,
                    CenterX = Pivot.X,
                    CenterY = Pivot.Y
                },
                new RotateTransform
                {
                    Angle = Angle,
                    CenterX = Pivot.X,
                    CenterY = Pivot.Y
                }
            }
        };
        Canvas.SetLeft(Root, Anchor.X - Pivot.X);
        Canvas.SetTop(Root, Anchor.Y - Pivot.Y);
    }

    /// <summary>放大镜按钮：三档循环，不改变尺寸。</summary>
    public void ZoomNext()
    {
        ZoomIndex = (ZoomIndex + 1) % ZoomSteps.Length;
        ApplyTransform();
    }

    /// <summary>滚轮旋转：向上滚逆时针、向下滚顺时针（与直尺一致）。</summary>
    public void RotateByWheel(double degrees) => ApplyAngle(_rawAngle - degrees, snap: false);

    /// <summary>置于同类元素最上层。</summary>
    public void BringToFront()
    {
        if (!Layer.Children.Contains(Root)) return;
        Layer.Children.Remove(Root);
        Layer.Children.Add(Root);
    }

    // ── 手势 ──

    public void BeginDrag(Point p)
    {
        GestureStart = p;
        _gestureAnchor = Anchor;
        Gesture = MeasureGesture.Drag;
    }

    public void BeginRotate(Point p)
    {
        GestureStart = p;
        _lastPointerAngle = ScreenAngle(p);
        Gesture = MeasureGesture.Rotate;
    }

    public void BeginResize(Point p)
    {
        GestureStart = p;
        _gestureStartSize = ResizeSize;
        Gesture = MeasureGesture.Resize;
    }

    /// <summary>指针移动：按当前手势更新位置 / 角度 / 尺寸。Shift 期间角度吸附到整度。</summary>
    public void PointerMoved(Point p, bool snap)
    {
        switch (Gesture)
        {
            case MeasureGesture.Drag:
                PlaceAt(new Point(
                    _gestureAnchor.X + p.X - GestureStart.X,
                    _gestureAnchor.Y + p.Y - GestureStart.Y));
                break;

            case MeasureGesture.Rotate:
            {
                double angle = ScreenAngle(p);
                // 逐帧累加增量，指针跨越 ±180° 时不会跳变，可连续旋转任意角度
                double delta = angle - _lastPointerAngle;
                if (delta > 180) delta -= 360;
                else if (delta < -180) delta += 360;
                _lastPointerAngle = angle;
                ApplyAngle(_rawAngle + delta, snap);
                break;
            }

            case MeasureGesture.Resize:
            {
                // 把位移投影到按钮指示的方向（局部 +x，随量具一起旋转），再换算成毫米
                double rad = Angle * Math.PI / 180;
                double dx = (p.X - GestureStart.X) * Math.Cos(rad) + (p.Y - GestureStart.Y) * Math.Sin(rad);
                if (SetResizeSize(_gestureStartSize + dx / MeasureScale.Mm)) ApplyTransform();
                break;
            }
        }
    }

    public void EndGesture()
    {
        Gesture = MeasureGesture.None;
        RayDragging = false;
    }

    /// <summary>拖动可旋转射线（量角器覆盖，其余量具无此交互）。</summary>
    public virtual void DragRay(Point p)
    {
    }

    // ── 贴边吸附 ──

    /// <summary>
    /// 落笔点是否贴着某条可吸附边：返回该边几何与投影后的落点。
    /// 最近边优先；垂距差 ≤ 3 DIP 时按 SnapEdges 的声明顺序取先者。
    /// </summary>
    public MeasureSnap? TrySnap(Point p, FrameworkElement coordinateRoot)
    {
        if (SnapEdges.Count == 0) return null;
        var transform = Root.TransformToVisual(coordinateRoot);
        var centroid = transform.TransformPoint(LocalCentroid);

        MeasureSnap? best = null;
        double bestPerp = double.MaxValue;

        foreach (var (localA, localB) in SnapEdges)
        {
            var a = transform.TransformPoint(localA);
            var b = transform.TransformPoint(localB);
            double len = Distance(a, b);
            if (len < 1) continue;

            var dir = new Point((b.X - a.X) / len, (b.Y - a.Y) / len);
            double proj = (p.X - a.X) * dir.X + (p.Y - a.Y) * dir.Y;
            double perpX = p.X - (a.X + dir.X * proj);
            double perpY = p.Y - (a.Y + dir.Y * proj);
            double perp = Math.Sqrt(perpX * perpX + perpY * perpY);

            if (perp > SnapTolerance || proj < -8 || proj > len + 8) continue;
            // 已经有一条差距在窄带内的边时不换边：先声明的边胜出
            if (best is not null && perp >= bestPerp - SnapTieBand) continue;

            var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            double nx = mid.X - centroid.X, ny = mid.Y - centroid.Y;
            double nl = Math.Sqrt(nx * nx + ny * ny);
            var outward = nl > 0.01 ? new Point(nx / nl, ny / nl) : new Point(0, -1);

            double clamped = Math.Clamp(proj, 0, len);
            best = new MeasureSnap(
                a, dir, len, outward,
                new Point(a.X + dir.X * clamped, a.Y + dir.Y * clamped));
            bestPerp = perp;
        }
        return best;
    }

    // ── 子类共用的按钮处理 ──

    protected void HandleBodyPressed(PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(Layer).Position;
        // 画笔工具下按在刻度边附近：不吞事件、不进入拖动手势，放行冒泡去画贴边直线
        if (SnapPassThrough?.Invoke(p) == true) return;

        e.Handled = true;
        BringToFront();
        BeginDrag(p);
        Layer.CapturePointer(e.Pointer);
    }

    protected void OnClosePressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        CloseRequested?.Invoke(this);
    }

    protected void OnZoomPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        ZoomNext();
    }

    protected void OnRotatePressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        BringToFront();
        BeginRotate(e.GetCurrentPoint(Layer).Position);
        Layer.CapturePointer(e.Pointer);
    }

    protected void OnResizePressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        BringToFront();
        BeginResize(e.GetCurrentPoint(Layer).Position);
        Layer.CapturePointer(e.Pointer);
    }

    /// <summary>指针相对旋转中心的角度（度，顺时针为正）。</summary>
    private double ScreenAngle(Point p) =>
        Math.Atan2(p.Y - Anchor.Y, p.X - Anchor.X) * 180 / Math.PI;

    private void ApplyAngle(double raw, bool snap)
    {
        _rawAngle = NormalizeAngle(raw);
        Angle = snap ? Math.Round(_rawAngle) : _rawAngle;
        ApplyTransform();
    }

    private static double NormalizeAngle(double angle)
    {
        angle %= 360;
        if (angle > 180) angle -= 360;
        else if (angle <= -180) angle += 360;
        return angle;
    }

    protected static double Distance(Point a, Point b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
