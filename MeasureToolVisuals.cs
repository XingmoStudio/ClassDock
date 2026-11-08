using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ClassDock;

/// <summary>
/// 尺规工具共用的视觉规格：半透明板体材质、刻度颜色、26×26 圆形动作按钮与图标。
/// 直尺、圆规、量角器、三角板都取这一份，保证质感和按钮手感一致。
/// </summary>
internal static class MeasureToolVisuals
{
    /// <summary>板体填充：与直尺一致的半透明浅灰。</summary>
    public static readonly SolidColorBrush BodyFill = new(Color.FromArgb(0xB4, 0xF6, 0xF9, 0xF9));

    /// <summary>板体描边。</summary>
    public static readonly SolidColorBrush BodyBorder = new(Color.FromArgb(0x1A, 0x00, 0x00, 0x00));

    /// <summary>刻度带底色（略亮于板体）。</summary>
    public static readonly SolidColorBrush TickBandFill = new(Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF));

    /// <summary>刻度带与板体之间的分隔线。</summary>
    public static readonly SolidColorBrush TickBandBorder = new(Color.FromArgb(0x14, 0x00, 0x00, 0x00));

    /// <summary>刻度线颜色。</summary>
    public static readonly SolidColorBrush TickStroke = new(Color.FromArgb(0xFF, 0x4A, 0x4A, 0x4A));

    /// <summary>刻度数字颜色。</summary>
    public static readonly SolidColorBrush NumberForeground = new(Color.FromArgb(0xFF, 0x6E, 0x6E, 0x6E));

    /// <summary>次级刻度数字颜色（量角器内圈）。</summary>
    public static readonly SolidColorBrush SecondaryNumberForeground = new(Color.FromArgb(0xB4, 0x6E, 0x6E, 0x6E));

    /// <summary>射线与角度读数颜色。</summary>
    public static readonly SolidColorBrush RayStroke = new(Color.FromArgb(0xFF, 0x3C, 0x40, 0x48));

    /// <summary>圆形按钮的底色。</summary>
    public static readonly SolidColorBrush ButtonFill = new(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    /// <summary>圆形按钮的描边。</summary>
    public static readonly SolidColorBrush ButtonBorder = new(Color.FromArgb(0x24, 0x00, 0x00, 0x00));

    /// <summary>按钮图标颜色。</summary>
    public static readonly SolidColorBrush IconForeground = new(Color.FromArgb(0xFF, 0x4A, 0x4A, 0x4A));

    /// <summary>圆形按钮直径。</summary>
    public const double ButtonSize = 26;

    /// <summary>尺子上的圆形工具按钮：半透明白底、细描边，内容居中。</summary>
    public static Border MakeButton(FrameworkElement icon, string tooltip, PointerEventHandler onPressed)
    {
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        var button = new Border
        {
            Width = ButtonSize,
            Height = ButtonSize,
            CornerRadius = new CornerRadius(ButtonSize / 2),
            Background = ButtonFill,
            BorderBrush = ButtonBorder,
            BorderThickness = new Thickness(1),
            Child = icon
        };
        ToolTipService.SetToolTip(button, tooltip);
        button.PointerPressed += onPressed;
        return button;
    }

    /// <summary>Segoe MDL2 Assets 字形图标（细线条风格，与尺子按钮一致）。</summary>
    public static TextBlock BuildGlyph(string glyph, double size) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = size,
        Foreground = IconForeground
    };

    /// <summary>关闭图标：细线叉号。</summary>
    public static Viewbox BuildCloseIcon()
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
        {
            X1 = 6, Y1 = 6, X2 = 18, Y2 = 18,
            Stroke = IconForeground,
            StrokeThickness = 2
        });
        canvas.Children.Add(new Microsoft.UI.Xaml.Shapes.Line
        {
            X1 = 18, Y1 = 6, X2 = 6, Y2 = 18,
            Stroke = IconForeground,
            StrokeThickness = 2
        });
        return new Viewbox { Width = 13, Height = 13, Child = canvas };
    }

    /// <summary>横向展开图标：双向水平箭头。</summary>
    public static Viewbox BuildExpandIcon()
    {
        var canvas = new Canvas { Width = 24, Height = 24 };
        void Add(double x1, double y1, double x2, double y2) => canvas.Children.Add(
            new Microsoft.UI.Xaml.Shapes.Line
            {
                X1 = x1, Y1 = y1, X2 = x2, Y2 = y2,
                Stroke = IconForeground,
                StrokeThickness = 2.2
            });
        Add(3, 12, 21, 12);    // 杆身
        Add(21, 12, 17, 8);    // 右箭头
        Add(21, 12, 17, 16);
        Add(3, 12, 7, 8);      // 左箭头
        Add(3, 12, 7, 16);
        return new Viewbox { Width = 17, Height = 17, Child = canvas };
    }
}
