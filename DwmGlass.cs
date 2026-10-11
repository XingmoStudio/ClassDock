using System.Runtime.InteropServices;

namespace ClassDock;

/// <summary>
/// Win11/Win10 窗口真透明：
/// 1. DWMWA_SYSTEMBACKDROP_TYPE 设为 DWMSBT_NONE —— 关闭 Win11 默认系统背景策略，
///    否则它会覆盖传统玻璃扩展（这是 DwmExtendFrameIntoClientArea 在 Win11 失效的原因）；
/// 2. DwmExtendFrameIntoClientArea(-1) —— 整个客户区成为透明合成面。
/// 配合 XAML 根元素 Background="Transparent" 与不设置任何 SystemBackdrop 使用。
/// </summary>
internal static class DwmGlass
{
    private const int DwmwaSystemBackdropType = 38;
    private const int DwmsbtNone = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpDoNotRound = 1;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseW, int ellipseH);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw);

    /// <summary>
    /// 把窗口区域直接设为胶囊形（两端半圆）。
    /// 比 DWM 圆角偏好可靠：有自定义 region 的窗口 DWM 不再叠加自动圆角，
    /// 且 Hide→Show 后形状不会丢失。命中测试也随之变成胶囊形。
    /// 注意：SetWindowRgn 成功后 region 归系统所有，不可再 DeleteObject。
    /// </summary>
    public static void SetCapsuleRegion(IntPtr hwnd, int widthPx, int heightPx)
    {
        // right/bottom 为排他边界需 +1；椭圆宽 = 窗口宽 → 端部恰为半圆
        IntPtr rgn = CreateRoundRectRgn(0, 0, widthPx + 1, heightPx + 1, widthPx, widthPx);
        SetWindowRgn(hwnd, rgn, true);
    }

    public static void Enable(IntPtr hwnd)
    {
        int backdropType = DwmsbtNone;
        DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdropType, sizeof(int));

        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    /// <summary>
    /// 关闭 Win11 DWM 对窗口的自动圆角裁剪。
    /// 窄窗口（如 12 DIP 白条）上自动圆角半径会超过半宽，把端部裁成尖形；
    /// 禁用后由 XAML 自己的 CornerRadius 渲染完美胶囊半圆端。
    /// </summary>
    public static void DisableCornerRounding(IntPtr hwnd)
    {
        int pref = DwmwcpDoNotRound;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int));
    }
}
