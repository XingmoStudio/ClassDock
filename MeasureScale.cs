using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace ClassDock;

/// <summary>
/// 量具刻度绘制：把毫米刻度、厘米数字、半圆角度刻度画进量具的局部坐标系。
/// 直尺的刻度带是横平竖直的老布局，这里给三角板的斜边和量角器的圆弧提供通用版本。
/// </summary>
internal static class MeasureScale
{
    /// <summary>1 毫米对应的逻辑像素（1 逻辑英寸恒为 96 逻辑像素，与 DPI 无关）。</summary>
    public const double Mm = 96.0 / 25.4;

    /// <summary>1 厘米对应的逻辑像素。</summary>
    public const double Cm = 10 * Mm;

    /// <summary>厘米、5 毫米、1 毫米三档刻度长度。</summary>
    private const double CmTick = 15;
    private const double HalfCmTick = 10;
    private const double MmTick = 6.5;

    /// <summary>
    /// 沿一条直边画毫米刻度：刻度由边向板体内部悬挂，每整厘米在刻度末端旁标注数字。
    /// <paramref name="interior"/> 是板体内的任意一点，只用它确定“向内”的方向。
    /// </summary>
    public static void AddLinearEdge(Canvas host, Point a, Point b, Point interior)
    {
        double len = Distance(a, b);
        if (len < Mm) return;

        var dir = new Point((b.X - a.X) / len, (b.Y - a.Y) / len);
        double nx = interior.X - (a.X + b.X) / 2;
        double ny = interior.Y - (a.Y + b.Y) / 2;
        double nl = Math.Sqrt(nx * nx + ny * ny);
        if (nl < 0.01) return;
        nx /= nl;
        ny /= nl;

        int count = (int)Math.Floor(len / Mm);
        for (int mm = 0; mm <= count; mm++)
        {
            double d = mm * Mm;
            // 两端各留一点白，避免刻度线顶到板体边框
            if (d < 4 || d > len - 4) continue;

            bool isCm = mm % 10 == 0;
            double tick = isCm ? CmTick : mm % 5 == 0 ? HalfCmTick : MmTick;
            var p = new Point(a.X + dir.X * d, a.Y + dir.Y * d);
            host.Children.Add(new Line
            {
                X1 = p.X,
                Y1 = p.Y,
                X2 = p.X + nx * tick,
                Y2 = p.Y + ny * tick,
                Stroke = MeasureToolVisuals.TickStroke,
                StrokeThickness = isCm ? 1.1 : 0.7,
                IsHitTestVisible = false
            });

            // 数字标注在刻度末端内侧，最后一段空间不足时省略
            if (!isCm || mm == 0 || d > len - 26) continue;
            AddEdgeLabel(host, p, nx, ny, dir, (mm / 10).ToString());
        }
    }

    /// <summary>
    /// 半圆角度刻度：外圈 0° 在右、180° 在左，内圈反向；每 1° 一根短刻度、每 10° 标数字。
    /// </summary>
    public static void AddArcScale(Canvas host, Point center, double radius)
    {
        for (int deg = 0; deg <= 180; deg++)
        {
            double rad = deg * Math.PI / 180;
            double cos = Math.Cos(rad), sin = Math.Sin(rad);
            bool isTen = deg % 10 == 0;
            bool isFive = deg % 5 == 0;
            double tick = isTen ? CmTick : isFive ? HalfCmTick : MmTick;

            host.Children.Add(new Line
            {
                X1 = center.X + radius * cos,
                Y1 = center.Y - radius * sin,
                X2 = center.X + (radius - tick) * cos,
                Y2 = center.Y - (radius - tick) * sin,
                Stroke = MeasureToolVisuals.TickStroke,
                StrokeThickness = isTen ? 1.1 : 0.7,
                IsHitTestVisible = false
            });

            if (!isTen) continue;
            AddArcLabel(host, center, radius - 28, deg, deg.ToString(), secondary: false);
            AddArcLabel(host, center, radius - 50, deg, (180 - deg).ToString(), secondary: true);
        }
    }

    /// <summary>直边上的厘米数字：跟随边的方向旋转，但始终保持正立可读。</summary>
    private static void AddEdgeLabel(Canvas host, Point p, double nx, double ny, Point dir, string text)
    {
        double angle = Math.Atan2(dir.Y, dir.X) * 180 / Math.PI;
        if (angle > 90) angle -= 180;
        else if (angle < -90) angle += 180;

        var label = new TextBlock
        {
            Text = text,
            FontSize = 10,
            Foreground = MeasureToolVisuals.NumberForeground,
            Width = 28,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform { Angle = angle }
        };
        Canvas.SetLeft(label, p.X + nx * 26 - 14);
        Canvas.SetTop(label, p.Y + ny * 26 - 7);
        host.Children.Add(label);
    }

    /// <summary>圆弧上的角度数字：按半径方向摆位，文字保持正立。</summary>
    private static void AddArcLabel(Canvas host, Point center, double r, int deg, string text, bool secondary)
    {
        double rad = deg * Math.PI / 180;
        var p = new Point(center.X + r * Math.Cos(rad), center.Y - r * Math.Sin(rad));
        var label = new TextBlock
        {
            Text = text,
            FontSize = 9,
            Foreground = secondary
                ? MeasureToolVisuals.SecondaryNumberForeground
                : MeasureToolVisuals.NumberForeground,
            Width = 26,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false
        };
        Canvas.SetLeft(label, p.X - 13);
        Canvas.SetTop(label, p.Y - 6);
        host.Children.Add(label);
    }

    private static double Distance(Point a, Point b) =>
        Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
