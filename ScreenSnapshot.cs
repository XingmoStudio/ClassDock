using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;

namespace ClassDock;

/// <summary>
/// 屏幕快照：用 GDI BitBlt 抓一份全屏位图，供批注画布做背景。
///
/// 为什么不做“真透明覆盖层”：WinUI 3 窗口的系统背景只有 DWM 材质（Acrylic/Mica），
/// 两者都自带模糊；关掉材质后 XAML 岛会画上自己的白底，桌面完全不可见。
/// 因此批注层采用截图定格的经典做法——画面清晰、笔迹锐利。
/// </summary>
internal static class ScreenSnapshot
{
    private const uint SrcCopy = 0x00CC0020;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr dst, int x, int y, int width, int height,
        IntPtr src, int srcX, int srcY, uint rop);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(
        IntPtr hdc, ref BitmapInfo info, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Colors;
    }

    /// <summary>抓取主屏并转为可直接显示的位图；失败返回 null。</summary>
    public static WriteableBitmap? Capture()
    {
        int width = GetSystemMetrics(SmCxScreen);
        int height = GetSystemMetrics(SmCyScreen);
        if (width <= 0 || height <= 0) return null;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memoryDc = CreateCompatibleDC(screenDc);

        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height, // 负高度 = 自上而下，与 BGRA 像素顺序一致
                Planes = 1,
                BitCount = 32,
                Compression = 0 // BI_RGB
            }
        };

        IntPtr dib = CreateDIBSection(memoryDc, ref info, 0, out IntPtr bits, IntPtr.Zero, 0);
        IntPtr previous = SelectObject(memoryDc, dib);

        BitBlt(memoryDc, 0, 0, width, height, screenDc, 0, 0, SrcCopy);

        int length = width * height * 4;
        var pixels = new byte[length];
        Marshal.Copy(bits, pixels, 0, length);
        // BitBlt 不写 alpha 字节，补成不透明（否则显示时整张图是透明的）
        for (int i = 3; i < length; i += 4) pixels[i] = 0xFF;

        SelectObject(memoryDc, previous);
        DeleteObject(dib);
        DeleteDC(memoryDc);
        ReleaseDC(IntPtr.Zero, screenDc);

        var bitmap = new WriteableBitmap(width, height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, length);
        }
        bitmap.Invalidate();
        return bitmap;
    }
}
