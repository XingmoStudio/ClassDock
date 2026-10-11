using System.Runtime.InteropServices;

namespace ClassDock;

/// <summary>
/// 解除 WinUI 3 桌面窗口的默认最小尺寸限制（136x39 DIP，Windows App SDK 硬编码）。
/// 通过 comctl32 安全子类化处理 WM_GETMINMAXINFO，将 ptMinTrackSize 清零。
/// </summary>
internal static class WindowMinSize
{
    private const int WmGetMinMaxInfo = 0x0024;

    private delegate IntPtr SubclassProc(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(
        IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    // 保持委托存活，防止 GC 回收原生回调
    private static readonly SubclassProc Proc = OnWndProc;

    public static void RemoveLimits(IntPtr hwnd) =>
        SetWindowSubclass(hwnd, Proc, UIntPtr.Zero, IntPtr.Zero);

    // ── 去除对话框边框残留 ──

    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint WsDlgFrame = 0x00400000;
    private const uint WsExWindowEdge = 0x00000100;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    /// <summary>
    /// 移除 WinUI 3 无边框窗口残留的 WS_DLGFRAME / WS_EX_WINDOWEDGE 样式。
    /// 这些样式会让系统保留非客户边框区，把 XAML 客户区挤小（白条因此比窗口窄、
    /// 圆角端被截成平顶）。必须在 MoveAndResize 之前调用。
    /// </summary>
    public static void StripDialogFrame(IntPtr hwnd)
    {
        long style = GetWindowLongPtr(hwnd, GwlStyle).ToInt64();
        style &= ~(long)WsDlgFrame;
        SetWindowLongPtr(hwnd, GwlStyle, new IntPtr(style));

        long exStyle = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64();
        exStyle &= ~(long)WsExWindowEdge;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(exStyle));

        // 通知系统按新样式重算非客户区
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpFrameChanged);
    }

    private static IntPtr OnWndProc(
        IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
        UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (msg == WmGetMinMaxInfo)
        {
            // 先让系统填充默认值（最大化尺寸等），再把 ptMinTrackSize 清零（x64 偏移 24）
            var result = DefSubclassProc(hWnd, msg, wParam, lParam);
            Marshal.WriteInt32(lParam, 24, 0);
            Marshal.WriteInt32(lParam, 28, 0);
            return result;
        }

        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }
}
