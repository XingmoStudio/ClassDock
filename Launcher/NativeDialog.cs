using System.Runtime.InteropServices;
using System.Text;

namespace ClassDock.Launcher;

/// <summary>
/// Win32 自绘对话框：DWM 圆角 + Segoe UI Variable 字体 + 圆角强调色按钮。
/// 不引用 WinUI / WindowsAppSDK，只依赖系统自带的 user32 / gdi32 / dwmapi，
/// 以保持启动器单文件、自带运行时、冷启动快。
/// </summary>
internal static class NativeDialog
{
    private const string WindowClassName = "ClassDock.NativeDialog";

    private const uint WindowStyle = 0x80000000 | 0x02000000; // WS_POPUP | WS_CLIPCHILDREN
    private const uint WindowExStyle = 0x00000080 | 0x00000008; // WS_EX_TOOLWINDOW | WS_EX_TOPMOST

    private static bool _classRegistered;
    private static WndProcDelegate? _wndProc;
    private static DialogWindow? _active;

    private static string _titleFamily = "Segoe UI";
    private static string _bodyFamily = "Segoe UI";
    private static string _iconFamily = "Segoe MDL2 Assets";

    /// <summary>错误对话框（启动失败、找不到 App 目录等）。</summary>
    public static void ShowError(string message, string? title = null) =>
        Show(DialogKind.Error, title ?? "ClassDock 启动失败", message);

    /// <summary>警告对话框（可继续运行但需要用户留意的情况）。</summary>
    public static void ShowWarning(string message, string? title = null) =>
        Show(DialogKind.Warning, title ?? "ClassDock 提示", message);

    /// <summary>提示对话框（普通信息）。</summary>
    public static void ShowInfo(string message, string? title = null) =>
        Show(DialogKind.Info, title ?? "ClassDock", message);

    public static void Show(DialogKind kind, string title, string message)
    {
        try
        {
            ShowCore(kind, title, message);
        }
        catch (Exception)
        {
            // 自绘链路意外失败时退回系统 MessageBox，保证错误提示永远出得来
            FallbackMessageBox(kind, title, message);
        }
    }

    private static void ShowCore(DialogKind kind, string title, string message)
    {
        EnablePerMonitorDpi();
        ResolveFontFamilies();

        bool dark = DialogTheme.IsDarkTheme();
        string root = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        uint accent = DialogTheme.ResolveAccent(root, out _);
        var palette = DialogTheme.Build(kind, dark, accent);

        GetCursorPos(out POINT cursor);
        uint dpi = GetMonitorDpi(cursor, out RECT work);

        var dialog = new DialogWindow(kind, title, message, palette);
        _active = dialog;
        try
        {
            dialog.Run(dpi, work);
        }
        finally
        {
            _active = null;
        }
    }

    private static void EnablePerMonitorDpi()
    {
        try
        {
            SetProcessDpiAwarenessContext(new IntPtr(-4)); // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
        }
        catch
        {
            // 老系统没有该 API 时交给窗口自身按 96 DPI 处理
        }
    }

    /// <summary>优先 Segoe UI Variable / Segoe Fluent Icons，缺失时退 Segoe UI / Segoe MDL2 Assets。</summary>
    private static void ResolveFontFamilies()
    {
        IntPtr hdc = GetDC(IntPtr.Zero);
        try
        {
            if (hdc != IntPtr.Zero)
            {
                _titleFamily = PickFamily(hdc, "Segoe UI Variable Display", "Segoe UI");
                _bodyFamily = PickFamily(hdc, "Segoe UI Variable Text", "Segoe UI");
                _iconFamily = PickFamily(hdc, "Segoe Fluent Icons", "Segoe MDL2 Assets");
            }
        }
        finally
        {
            if (hdc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, hdc);
        }
    }

    private static string PickFamily(IntPtr hdc, string preferred, string fallback)
    {
        IntPtr font = CreateFontW(-12, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, preferred);
        if (font == IntPtr.Zero) return fallback;

        IntPtr old = SelectObject(hdc, font);
        var buffer = new StringBuilder(64);
        int length = GetTextFaceW(hdc, buffer.Capacity, buffer);
        SelectObject(hdc, old);
        DeleteObject(font);

        return length > 0 && buffer.ToString().Contains(preferred, StringComparison.OrdinalIgnoreCase)
            ? preferred
            : fallback;
    }

    private static uint GetMonitorDpi(POINT cursor, out RECT work)
    {
        IntPtr monitor = MonitorFromPoint(cursor, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        work = GetMonitorInfoW(monitor, ref info)
            ? info.rcWork
            : new RECT { left = 0, top = 0, right = 1920, bottom = 1080 };

        try
        {
            if (GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out uint dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX;
            }
        }
        catch
        {
            // shcore 不可用时按 96 DPI 处理
        }
        return 96;
    }

    private static void EnsureWindowClass()
    {
        if (_classRegistered) return;

        _wndProc = WndProc;
        var wc = new WNDCLASSEXW
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
            style = 0x0001 | 0x0002, // CS_VREDRAW | CS_HREDRAW
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandleW(null),
            hCursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512) /* IDC_ARROW */),
            hbrBackground = IntPtr.Zero, // 背景全部在 WM_PAINT 里处理，Mica 才能透出来
            lpszClassName = WindowClassName
        };
        RegisterClassExW(ref wc);
        _classRegistered = true;
    }

    private static IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            DialogWindow? dialog = _active;
            return dialog is null
                ? DefWindowProcW(hwnd, msg, wParam, lParam)
                : dialog.HandleMessage(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            // 记下绘制/消息处理中的异常，消息循环结束后交给外层回退 MessageBox
            if (_active is not null) _active.Failure = ex;
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    private static void FallbackMessageBox(DialogKind kind, string title, string message)
    {
        uint icon = kind switch
        {
            DialogKind.Error => 0x00000010,   // MB_ICONERROR
            DialogKind.Warning => 0x00000030, // MB_ICONWARNING
            _ => 0x00000040                   // MB_ICONINFORMATION
        };
        MessageBoxW(IntPtr.Zero, message, title, icon);
    }

    private sealed class DialogWindow
    {
        private readonly DialogKind _kind;
        private readonly string _title;
        private readonly string _message;
        private readonly DialogPalette _palette;

        private IntPtr _hwnd;
        private int _width;
        private int _height;
        private double _scale = 1;
        private int _pad;
        private int _badge;
        private int _gap;
        private int _textLeft;
        private int _textWidth;
        private int _titleHeight;
        private int _bodyTop;
        private int _bodyHeight;
        private int _titleSize;
        private int _bodySize;
        private int _iconSize;
        private RECT _button;
        private bool _hover;
        private bool _pressed;
        private bool _trackingLeave;

        /// <summary>窗口过程里捕获到的异常，消息循环结束后抛出，交给外层回退 MessageBox。</summary>
        public Exception? Failure;

        public DialogWindow(DialogKind kind, string title, string message, DialogPalette palette)
        {
            _kind = kind;
            _title = title;
            _message = message;
            _palette = palette;
        }

        public void Run(uint dpi, RECT work)
        {
            Layout(dpi);

            int x = work.left + Math.Max(0, (work.right - work.left - _width) / 2);
            int y = work.top + Math.Max(0, (work.bottom - work.top - _height) / 2);

            EnsureWindowClass();
            IntPtr instance = GetModuleHandleW(null);
            _hwnd = CreateWindowExW(
                WindowExStyle, WindowClassName, _title, WindowStyle,
                x, y, _width, _height,
                IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero) throw new InvalidOperationException("创建对话框窗口失败。");

            ApplyDwm();

            ShowWindow(_hwnd, 1 /* SW_SHOWNORMAL */);
            SetForegroundWindow(_hwnd);
            UpdateWindow(_hwnd);

            while (GetMessageW(out MSG msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }

            _hwnd = IntPtr.Zero;
            if (Failure is not null) throw Failure;
        }

        public IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case 0x000F: // WM_PAINT
                    OnPaint(hwnd);
                    return IntPtr.Zero;

                case 0x0014: // WM_ERASEBKGND
                    return new IntPtr(1);

                case 0x0002: // WM_DESTROY
                    PostQuitMessage(0);
                    return IntPtr.Zero;

                case 0x0010: // WM_CLOSE
                    DestroyWindow(hwnd);
                    return IntPtr.Zero;

                case 0x0100: // WM_KEYDOWN：回车 / ESC / 空格关闭
                    int key = (int)wParam.ToInt64();
                    if (key is 0x0D or 0x1B or 0x20) DestroyWindow(hwnd);
                    return IntPtr.Zero;

                case 0x0201: // WM_LBUTTONDOWN
                    OnLeftButtonDown(hwnd, lParam);
                    return IntPtr.Zero;

                case 0x0202: // WM_LBUTTONUP
                    OnLeftButtonUp(hwnd, lParam);
                    return IntPtr.Zero;

                case 0x0200: // WM_MOUSEMOVE
                    OnMouseMove(hwnd, lParam);
                    return IntPtr.Zero;

                case 0x02A3: // WM_MOUSELEAVE
                    _trackingLeave = false;
                    if (_hover)
                    {
                        _hover = false;
                        InvalidateButton(hwnd);
                    }
                    return IntPtr.Zero;

                case 0x0020: // WM_SETCURSOR
                    SetCursor(LoadCursorW(IntPtr.Zero, new IntPtr(_hover ? 32649 /* IDC_HAND */ : 32512)));
                    return new IntPtr(1);
            }

            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }

        private void ApplyDwm()
        {
            // 深色标题栏/背景（20 是新属性号，老版本用 19）
            int dark = _palette.IsDark ? 1 : 0;
            if (DwmSetWindowAttribute(_hwnd, 20, ref dark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(_hwnd, 19, ref dark, sizeof(int));
            }

            // DWM 圆角；不支持时退回窗口区域裁剪
            int corner = 2; // DWMWCP_ROUND
            if (DwmSetWindowAttribute(_hwnd, 33, ref corner, sizeof(int)) != 0)
            {
                int radius = Px(8);
                IntPtr region = CreateRoundRectRgn(0, 0, _width + 1, _height + 1, radius, radius);
                if (region != IntPtr.Zero) SetWindowRgn(_hwnd, region, false);
            }
        }

        private void Layout(uint dpi)
        {
            _scale = dpi / 96.0;
            _pad = Px(24);
            _badge = Px(40);
            _gap = Px(16);
            _titleSize = Px(20);
            _bodySize = Px(14);
            _iconSize = Px(18);

            int minWidth = Px(360);
            int maxWidth = Px(520);
            int buttonHeight = Px(32);
            int buttonGap = Px(24);
            int minButtonWidth = Px(88);

            IntPtr hdc = GetDC(IntPtr.Zero);
            IntPtr titleFont = CreateFontW(-_titleSize, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, _titleFamily);
            IntPtr bodyFont = CreateFontW(-_bodySize, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, _bodyFamily);
            try
            {
                int maxTextWidth = maxWidth - _pad * 2 - _badge - _gap;
                int minTextWidth = minWidth - _pad * 2 - _badge - _gap;

                int titleNatural = MeasureSingleLine(hdc, titleFont, _title);
                int bodyNatural = MeasureSingleLine(hdc, bodyFont, _message);
                int textWidth = Math.Clamp(Math.Max(titleNatural, bodyNatural), minTextWidth, maxTextWidth);

                _textLeft = _pad + _badge + _gap;
                _textWidth = textWidth;
                _titleHeight = MeasureWrappedHeight(hdc, titleFont, _title, textWidth);
                _bodyTop = _pad + _titleHeight + Px(8);
                _bodyHeight = MeasureWrappedHeight(hdc, bodyFont, _message, textWidth);

                int contentBottom = Math.Max(_pad + _badge, _bodyTop + _bodyHeight);
                _width = _pad * 2 + _badge + _gap + textWidth;
                _height = contentBottom + buttonGap + buttonHeight + _pad;

                int labelWidth = MeasureSingleLine(hdc, bodyFont, ButtonLabel);
                int buttonWidth = Math.Max(minButtonWidth, labelWidth + Px(32));
                _button = new RECT
                {
                    left = _width - _pad - buttonWidth,
                    top = _height - _pad - buttonHeight,
                    right = _width - _pad,
                    bottom = _height - _pad
                };
            }
            finally
            {
                if (hdc != IntPtr.Zero)
                {
                    DeleteObject(titleFont);
                    DeleteObject(bodyFont);
                    ReleaseDC(IntPtr.Zero, hdc);
                }
            }
        }

        private void OnPaint(IntPtr hwnd)
        {
            IntPtr hdc = BeginPaint(hwnd, out PAINTSTRUCT ps);
            if (hdc == IntPtr.Zero) return;
            try
            {
                Paint(hdc);
            }
            finally
            {
                EndPaint(hwnd, ref ps);
            }
        }

        private void Paint(IntPtr hdc)
        {
            DialogPalette palette = _palette;

            // 纯色 Fluent 背景：DWM 圆角负责裁形，Mica 在纯 GDI 窗口上无法正确合成文字
            IntPtr backgroundBrush = CreateSolidBrush(ToColorRef(palette.WindowBackground));
            var full = new RECT { left = 0, top = 0, right = _width, bottom = _height };
            FillRect(hdc, ref full, backgroundBrush);
            DeleteObject(backgroundBrush);

            SetBkMode(hdc, 1 /* TRANSPARENT */);

            // 语义徽章：实心圆 + 统一字形
            IntPtr badgeBrush = CreateSolidBrush(ToColorRef(palette.Semantic));
            IntPtr badgePen = CreatePen(0, 1, ToColorRef(palette.Semantic));
            IntPtr previousBrush = SelectObject(hdc, badgeBrush);
            IntPtr previousPen = SelectObject(hdc, badgePen);
            Ellipse(hdc, _pad, _pad, _pad + _badge + 1, _pad + _badge + 1);
            SelectObject(hdc, previousBrush);
            SelectObject(hdc, previousPen);
            DeleteObject(badgeBrush);
            DeleteObject(badgePen);

            IntPtr iconFont = CreateFontW(-_iconSize, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, _iconFamily);
            IntPtr oldFont = SelectObject(hdc, iconFont);
            SetTextColor(hdc, ToColorRef(DialogTheme.OnColor(palette.Semantic)));
            var badgeRect = new RECT { left = _pad, top = _pad, right = _pad + _badge, bottom = _pad + _badge };
            DrawTextW(hdc, Glyph, -1, ref badgeRect, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            SelectObject(hdc, oldFont);
            DeleteObject(iconFont);

            // 标题：语义色
            IntPtr titleFont = CreateFontW(-_titleSize, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, _titleFamily);
            oldFont = SelectObject(hdc, titleFont);
            SetTextColor(hdc, ToColorRef(palette.Semantic));
            var titleRect = new RECT
            {
                left = _textLeft,
                top = _pad,
                right = _textLeft + _textWidth,
                bottom = _pad + _titleHeight + 4
            };
            DrawTextW(hdc, _title, -1, ref titleRect, DT_WORDBREAK | DT_NOPREFIX);
            SelectObject(hdc, oldFont);
            DeleteObject(titleFont);

            // 正文：中性色，自动换行
            IntPtr bodyFont = CreateFontW(-_bodySize, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 5, 0, _bodyFamily);
            oldFont = SelectObject(hdc, bodyFont);
            SetTextColor(hdc, ToColorRef(palette.TextSecondary));
            var bodyRect = new RECT
            {
                left = _textLeft,
                top = _bodyTop,
                right = _textLeft + _textWidth,
                bottom = _bodyTop + _bodyHeight + 4
            };
            DrawTextW(hdc, _message, -1, ref bodyRect, DT_WORDBREAK | DT_NOPREFIX);
            SelectObject(hdc, oldFont);
            DeleteObject(bodyFont);

            // 圆角强调色主按钮
            uint fill = _pressed ? palette.ButtonPressed : _hover ? palette.ButtonHover : palette.ButtonFill;
            IntPtr buttonBrush = CreateSolidBrush(ToColorRef(fill));
            IntPtr buttonPen = CreatePen(0, 1, ToColorRef(fill));
            previousBrush = SelectObject(hdc, buttonBrush);
            previousPen = SelectObject(hdc, buttonPen);
            int buttonRadius = Px(5);
            RoundRect(hdc, _button.left, _button.top, _button.right, _button.bottom,
                buttonRadius * 2, buttonRadius * 2);
            SelectObject(hdc, previousBrush);
            SelectObject(hdc, previousPen);
            DeleteObject(buttonBrush);
            DeleteObject(buttonPen);

            IntPtr buttonFont = CreateFontW(-_bodySize, 0, 0, 0, 600, 0, 0, 0, 1, 0, 0, 5, 0, _bodyFamily);
            oldFont = SelectObject(hdc, buttonFont);
            SetTextColor(hdc, ToColorRef(palette.ButtonText));
            var buttonText = _button;
            DrawTextW(hdc, ButtonLabel, -1, ref buttonText, DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);
            SelectObject(hdc, oldFont);
            DeleteObject(buttonFont);
        }

        private void OnLeftButtonDown(IntPtr hwnd, IntPtr lParam)
        {
            if (!PtInRect(ref _button, PointFromLParam(lParam))) return;
            _pressed = true;
            SetCapture(hwnd);
            InvalidateButton(hwnd);
        }

        private void OnLeftButtonUp(IntPtr hwnd, IntPtr lParam)
        {
            if (!_pressed) return;
            _pressed = false;
            ReleaseCapture();
            InvalidateButton(hwnd);
            if (PtInRect(ref _button, PointFromLParam(lParam))) DestroyWindow(hwnd);
        }

        private void OnMouseMove(IntPtr hwnd, IntPtr lParam)
        {
            if (!_trackingLeave)
            {
                var track = new TRACKMOUSEEVENT
                {
                    cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                    dwFlags = 0x00000002, // TME_LEAVE
                    hwndTrack = hwnd
                };
                TrackMouseEvent(ref track);
                _trackingLeave = true;
            }

            bool hover = PtInRect(ref _button, PointFromLParam(lParam));
            if (hover == _hover) return;
            _hover = hover;
            InvalidateButton(hwnd);
        }

        private void InvalidateButton(IntPtr hwnd) => InvalidateRect(hwnd, ref _button, false);

        private string ButtonLabel => _kind switch
        {
            DialogKind.Error => "关闭",
            DialogKind.Warning => "知道了",
            _ => "好的"
        };

        private string Glyph => _kind switch
        {
            DialogKind.Error => char.ConvertFromUtf32(0xE783),   // Error
            DialogKind.Warning => char.ConvertFromUtf32(0xE7BA), // Warning
            _ => char.ConvertFromUtf32(0xE946)                   // Info
        };

        private int Px(double dip) => (int)Math.Round(dip * _scale);

        private static POINT PointFromLParam(IntPtr lParam)
        {
            int value = lParam.ToInt32();
            return new POINT { x = (short)(value & 0xFFFF), y = (short)((value >> 16) & 0xFFFF) };
        }
    }

    private static int MeasureSingleLine(IntPtr hdc, IntPtr font, string text)
    {
        IntPtr old = SelectObject(hdc, font);
        try
        {
            var rect = new RECT { right = 100000 };
            DrawTextW(hdc, Flatten(text), -1, ref rect, DT_CALCRECT | DT_SINGLELINE | DT_NOPREFIX);
            return rect.right;
        }
        finally
        {
            SelectObject(hdc, old);
        }
    }

    private static int MeasureWrappedHeight(IntPtr hdc, IntPtr font, string text, int width)
    {
        IntPtr old = SelectObject(hdc, font);
        try
        {
            var rect = new RECT { right = width };
            DrawTextW(hdc, text, -1, ref rect, DT_CALCRECT | DT_WORDBREAK | DT_NOPREFIX);
            return rect.bottom;
        }
        finally
        {
            SelectObject(hdc, old);
        }
    }

    private static string Flatten(string text) => text.Replace("\r\n", " ").Replace('\n', ' ');

    private static uint ToColorRef(uint argb) =>
        ((argb & 0x000000FF) << 16) | (argb & 0x0000FF00) | ((argb >> 16) & 0x000000FF);

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    private const uint DT_CENTER = 0x00000001;
    private const uint DT_VCENTER = 0x00000004;
    private const uint DT_SINGLELINE = 0x00000020;
    private const uint DT_WORDBREAK = 0x00000010;
    private const uint DT_NOPREFIX = 0x00000800;
    private const uint DT_CALCRECT = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr hdc;
        public int fErase;
        public RECT rcPaint;
        public int fRestore;
        public int fIncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] rgbReserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT
    {
        public uint cbSize;
        public uint dwFlags;
        public IntPtr hwndTrack;
        public uint dwHoverTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEXW
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint exStyle, string className, string windowName, uint style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool UpdateWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    private static extern IntPtr BeginPaint(IntPtr hwnd, out PAINTSTRUCT paint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT paint);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hwnd, ref RECT rect, bool erase);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCapture(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT evt);

    [DllImport("user32.dll")]
    private static extern bool PtInRect(ref RECT rect, POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    [DllImport("user32.dll")]
    private static extern bool SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? moduleName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint color);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreatePen(int style, int width, uint color);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern int SetBkMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern uint SetTextColor(IntPtr hdc, uint color);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, string text, int length, ref RECT rect, uint format);

    [DllImport("user32.dll")]
    private static extern int FillRect(IntPtr hdc, ref RECT rect, IntPtr brush);

    [DllImport("gdi32.dll")]
    private static extern bool Ellipse(IntPtr hdc, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool RoundRect(IntPtr hdc, int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetTextFaceW(IntPtr hdc, int count, StringBuilder faceName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(
        int height, int width, int escapement, int orientation, int weight,
        uint italic, uint underline, uint strikeOut, uint charSet,
        uint outPrecision, uint clipPrecision, uint quality, uint pitchAndFamily, string faceName);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
