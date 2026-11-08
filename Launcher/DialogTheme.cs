using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClassDock.Launcher;

/// <summary>对话框语义类型：错误 / 警告 / 提示。</summary>
internal enum DialogKind
{
    Error,
    Warning,
    Info
}

/// <summary>强调色来源；数值与主程序 AppTheme.AccentSourceMode 保持一致。</summary>
internal enum AccentSourceMode
{
    System = 0,
    Custom = 1
}

/// <summary>一次对话框绘制所需的调色板（0xAARRGGBB）。</summary>
internal readonly record struct DialogPalette(
    bool IsDark,
    uint WindowBackground,
    uint TextSecondary,
    uint Semantic,
    uint ButtonFill,
    uint ButtonHover,
    uint ButtonPressed,
    uint ButtonText);

/// <summary>
/// 对话框主题：系统深浅色 + 强调色三级回退。
/// 三级回退：ClassDock 自定义强调色（data\Config.json）→ 系统强调色（DwmGetColorizationColor）
/// → 系统默认蓝 #0078D4，任何一级失败都不会导致画不出来。
/// 启动器不引用 WinUI / WindowsAppSDK，这里全部用注册表与 dwmapi 完成。
/// </summary>
internal static class DialogTheme
{
    private const uint DefaultAccent = 0xFF0078D4;

    /// <summary>读取系统浅色/深色偏好；读不到按浅色处理。</summary>
    public static bool IsDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>解析强调色，并通过 <paramref name="source"/> 返回实际命中的一级（便于验证与排障）。</summary>
    public static uint ResolveAccent(string root, out string source)
    {
        if (TryReadClassDockAccent(root, out uint custom, out string configPath))
        {
            source = $"{configPath}（ClassDock 自定义强调色）";
            return custom;
        }

        if (TryGetSystemAccent(out uint system))
        {
            source = "系统强调色（DwmGetColorizationColor）";
            return system;
        }

        source = $"系统默认蓝 #{DefaultAccent & 0x00FFFFFF:X6}";
        return DefaultAccent;
    }

    /// <summary>按语义类型与当前明暗生成整份调色板。</summary>
    public static DialogPalette Build(DialogKind kind, bool dark, uint accent)
    {
        uint background = dark ? 0xFF202020 : 0xFFF3F3F3;
        uint secondary = dark ? 0xFFC5C5C5 : 0xFF5D5D5D;

        // 标题与徽章共用语义色：错误红、警告琥珀、提示用强调色
        uint semantic = kind switch
        {
            DialogKind.Error => dark ? 0xFFFF99A4 : 0xFFC42B1C,
            DialogKind.Warning => dark ? 0xFFFCE100 : 0xFF9D5D00,
            _ => EnsureReadable(dark ? Lighten(accent, 0.35) : Darken(accent, 0.10), background, !dark)
        };

        // 主按钮：浅色下加深、深色下提亮，与 WinUI 强调色按钮一致
        uint fill = dark ? Lighten(accent, 0.22) : Darken(accent, 0.12);
        uint hover = dark ? Lighten(fill, 0.12) : Darken(fill, 0.10);
        uint pressed = dark ? Darken(fill, 0.12) : Darken(fill, 0.20);

        return new DialogPalette(
            dark,
            background,
            secondary,
            semantic,
            fill,
            hover,
            pressed,
            OnColor(fill));
    }

    /// <summary>按亮度选择徽章字形/按钮文字颜色（白或黑），保证对比度。</summary>
    public static uint OnColor(uint background) =>
        Luminance(background) > 0.55 ? 0xFF000000 : 0xFFFFFFFF;

    /// <summary>读取系统强调色（窗口框颜色）；失败返回 false。</summary>
    public static bool TryGetSystemAccent(out uint accent)
    {
        accent = 0;
        try
        {
            if (DwmGetColorizationColor(out uint colorization, out _) == 0)
            {
                uint rgb = colorization & 0x00FFFFFF;
                if (rgb != 0 && rgb != 0x00FFFFFF)
                {
                    accent = 0xFF000000 | rgb;
                    return true;
                }
            }
        }
        catch
        {
            // 组件缺失时交给下一级回退
        }
        return false;
    }

    /// <summary>
    /// 读取 ClassDock 自己的强调色。只有 AccentSource=Custom(1) 且 CustomAccentArgb 有效才算命中；
    /// AccentSource=System 时主程序本来就用系统色，直接交给第二级。
    /// </summary>
    private static bool TryReadClassDockAccent(string root, out uint accent, out string path)
    {
        accent = 0;
        foreach (string candidate in ConfigCandidates(root))
        {
            if (!File.Exists(candidate)) continue;
            path = candidate;

            string json;
            try
            {
                json = File.ReadAllText(candidate);
            }
            catch
            {
                return false;
            }

            if (!TryReadInt(json, "AccentSource", out int mode)
                || mode != (int)AccentSourceMode.Custom
                || !TryReadInt(json, "CustomAccentArgb", out int raw))
            {
                return false;
            }

            uint rgb = unchecked((uint)raw) & 0x00FFFFFF;
            if (rgb == 0) return false; // 没有 alpha 的历史空值，视为非法
            accent = 0xFF000000 | rgb;
            return true;
        }

        path = string.Empty;
        return false;
    }

    /// <summary>主程序的 data 可能在根目录，也可能因只读安装位置回退到 %LOCALAPPDATA%。</summary>
    private static IEnumerable<string> ConfigCandidates(string root)
    {
        yield return Path.Combine(root, "data", "Config.json");

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length > 0) yield return Path.Combine(local, "ClassDock", "Config.json");
    }

    /// <summary>在扁平 JSON 中取出指定整数字段，避免为启动器引入 JSON 序列化器。</summary>
    private static bool TryReadInt(string json, string name, out int value)
    {
        value = 0;
        int index = json.IndexOf("\"" + name + "\"", StringComparison.Ordinal);
        if (index < 0) return false;

        index += name.Length + 2;
        while (index < json.Length && char.IsWhiteSpace(json[index])) index++;
        if (index >= json.Length || json[index] != ':') return false;
        index++;
        while (index < json.Length && char.IsWhiteSpace(json[index])) index++;

        int start = index;
        if (index < json.Length && (json[index] == '-' || json[index] == '+')) index++;
        int digits = index;
        while (index < json.Length && char.IsAsciiDigit(json[index])) index++;
        if (index == digits) return false;

        return int.TryParse(json.AsSpan(start, index - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>相对亮度（sRGB 线性化），用于选择文字色与校验对比度。</summary>
    public static double Luminance(uint color)
    {
        double r = Channel((color >> 16) & 0xFF);
        double g = Channel((color >> 8) & 0xFF);
        double b = Channel(color & 0xFF);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;

        static double Channel(uint value)
        {
            double c = value / 255.0;
            return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
    }

    private static double Contrast(uint a, uint b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        double high = Math.Max(la, lb);
        double low = Math.Min(la, lb);
        return (high + 0.05) / (low + 0.05);
    }

    /// <summary>标题文字对比度不足时，朝黑（浅色主题）或白（深色主题）方向逐级调整。</summary>
    private static uint EnsureReadable(uint color, uint background, bool darker)
    {
        for (int i = 0; i < 10 && Contrast(color, background) < 4.5; i++)
        {
            color = darker ? Darken(color, 0.10) : Lighten(color, 0.10);
        }
        return color;
    }

    private static uint Darken(uint color, double amount)
    {
        double keep = 1 - Math.Clamp(amount, 0, 1);
        byte r = (byte)Math.Round(((color >> 16) & 0xFF) * keep);
        byte g = (byte)Math.Round(((color >> 8) & 0xFF) * keep);
        byte b = (byte)Math.Round((color & 0xFF) * keep);
        return 0xFF000000 | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    private static uint Lighten(uint color, double amount)
    {
        double ratio = Math.Clamp(amount, 0, 1);
        byte r = (byte)Math.Round(((color >> 16) & 0xFF) + (255 - ((color >> 16) & 0xFF)) * ratio);
        byte g = (byte)Math.Round(((color >> 8) & 0xFF) + (255 - ((color >> 8) & 0xFF)) * ratio);
        byte b = (byte)Math.Round((color & 0xFF) + (255 - (color & 0xFF)) * ratio);
        return 0xFF000000 | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetColorizationColor(out uint colorization, out int opaqueBlend);
}
