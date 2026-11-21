// ── DwmGlass ──
// DWM 窗口透明与形状控制：玻璃扩展、自动圆角禁用、胶囊区域设定。

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
    private const int DwmwcpRoundSmall = 3;

    // ── 玻璃扩展 ──

    /// <summary>
    /// 关闭系统背景策略并将玻璃帧扩展至整个客户区，实现窗口真透明。
    /// 需配合 XAML 根元素 Background="Transparent" 且不设置 SystemBackdrop。
    /// </summary>
    public static void Enable(IntPtr hwnd)
    {
        int backdropType = DwmsbtNone;
        DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref backdropType, sizeof(int));

        var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref margins);
    }

    // ── 圆角控制 ──

    /// <summary>
    /// 使用 DWM 小圆角平滑裁剪窗口。
    /// 侧边栏宽度较窄，小圆角可避免默认圆角把端部切成尖形；
    /// 相比 SetWindowRgn 的二值 region，DWM 会保留边缘的抗锯齿过渡像素。
    /// </summary>
    public static void EnableSmallCornerRounding(IntPtr hwnd)
    {
        int pref = DwmwcpRoundSmall;
        DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref pref, sizeof(int));
    }

    // ── 窗口边框 ──

    private const int DwmwaBorderColor = 34;
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);

    /// <summary>
    /// 关闭 DWM 沿窗口外形绘制的那一圈 1px 系统边框。
    /// 玻璃帧铺满客户区后窗口内部已由 XAML 卡片自己描边，DWM 边框是多余的；
    /// 且它取窗口自身的明暗设定（本应用窗口始终按浅色注册），深色面板下会露出一圈浅色描边。
    /// </summary>
    public static void DisableBorder(IntPtr hwnd)
    {
        int none = DwmwaColorNone;
        DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref none, sizeof(int));
    }
}
