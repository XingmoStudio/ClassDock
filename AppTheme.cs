using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.Collections.Generic;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace ClassDock;

/// <summary>应用明暗主题。</summary>
public enum AppThemeMode
{
    System,
    Light,
    Dark
}

/// <summary>强调色来源。</summary>
public enum AccentSourceMode
{
    System,
    Custom
}

/// <summary>主题包接口（预留）：后续主题包系统实现该接口即可接管应用强调色。</summary>
public interface IThemePack
{
    /// <summary>主题包标识。</summary>
    string Id { get; }

    /// <summary>主题包名称。</summary>
    string DisplayName { get; }

    /// <summary>返回当前应使用的强调色；返回 null 表示该主题包不接管强调色。</summary>
    Color? GetAccentColor(ElementTheme theme);
}

/// <summary>
/// 全应用统一的主题与强调色入口。
/// 所有需要强调色的控件（按钮、单选项、输入框激活边框、开关、导航激活态、功能图标）都从这里取色，
/// 并可通过 <see cref="Apply"/> 让 WinUI 默认控件资源也使用同一强调色。
/// </summary>
internal static class AppTheme
{
    // —— 强调色画刷：实例固定、颜色可变，改 Color 即全局同步 ——
    private static readonly SolidColorBrush AccentFillBrush = new();
    private static readonly SolidColorBrush AccentSoftFillBrush = new();
    private static readonly SolidColorBrush AccentHoverFillBrush = new();
    private static readonly SolidColorBrush AccentPressedFillBrush = new();
    private static readonly SolidColorBrush OnAccentFillBrush = new();
    private static readonly SolidColorBrush AccentTintFillBrush = new();
    private static readonly SolidColorBrush AccentTintHoverFillBrush = new();
    private static readonly SolidColorBrush AccentTintPressedFillBrush = new();

    private static readonly List<FrameworkElement> Roots = [];
    private static readonly Dictionary<FrameworkElement, ResourceDictionary> RootDictionaries = [];
    private static readonly Dictionary<FrameworkElement, ElementTheme> RootThemes = [];

    private static DispatcherQueue? _dispatcher;
    private static UISettings? _uiSettings;
    private static IThemePack? _themePack;
    private static bool _initialized;

    /// <summary>主题或强调色发生变化；已取过画刷的界面无需订阅（画刷颜色会自动更新），
    /// 需要按颜色重新计算的场景（如生成半透明变体）订阅此事件。</summary>
    public static event EventHandler? Changed;

    /// <summary>主填充强调色画刷（按钮、开关、选中态等）。</summary>
    public static SolidColorBrush AccentBrush => AccentFillBrush;

    /// <summary>次一档填充强调色画刷（次级按钮）。</summary>
    public static SolidColorBrush AccentSoftBrush => AccentSoftFillBrush;

    /// <summary>强调色悬停填充。</summary>
    public static SolidColorBrush AccentHoverBrush => AccentHoverFillBrush;

    /// <summary>强调色按下填充。</summary>
    public static SolidColorBrush AccentPressedBrush => AccentPressedFillBrush;

    /// <summary>强调色填充上的文字颜色。</summary>
    public static SolidColorBrush OnAccentBrush => OnAccentFillBrush;

    /// <summary>强调色淡底色（选中项背景）。</summary>
    public static SolidColorBrush AccentTintBrush => AccentTintFillBrush;

    /// <summary>强调色淡底色悬停。</summary>
    public static SolidColorBrush AccentTintHoverBrush => AccentTintHoverFillBrush;

    /// <summary>强调色淡底色按下。</summary>
    public static SolidColorBrush AccentTintPressedBrush => AccentTintPressedFillBrush;

    /// <summary>当前设置的主题模式。</summary>
    public static AppThemeMode Mode => AppSettings.ThemeMode;

    /// <summary>当前设置的强调色来源。</summary>
    public static AccentSourceMode AccentSource => AppSettings.AccentSource;

    /// <summary>自定义强调色（仅当来源为自定义时生效）。</summary>
    public static Color CustomAccent => ColorFromArgb(255,
        (byte)((AppSettings.CustomAccentArgb >> 16) & 0xFF),
        (byte)((AppSettings.CustomAccentArgb >> 8) & 0xFF),
        (byte)(AppSettings.CustomAccentArgb & 0xFF));

    /// <summary>当前实际生效的明暗主题（"跟随系统"时读取系统背景色判断）。</summary>
    public static ElementTheme CurrentElementTheme => Mode switch
    {
        AppThemeMode.Light => ElementTheme.Light,
        AppThemeMode.Dark => ElementTheme.Dark,
        _ => SystemPrefersDark() ? ElementTheme.Dark : ElementTheme.Light
    };

    /// <summary>当前是否使用深色主题。</summary>
    public static bool IsDark => CurrentElementTheme == ElementTheme.Dark;

    /// <summary>当前生效的强调色（未经明暗派生）。</summary>
    public static Color AccentColor { get; private set; } = ColorFromArgb(255, 0, 120, 212);

    /// <summary>初始化：读取设置、开始监听系统颜色变化并刷新一次。</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        _dispatcher = DispatcherQueue.GetForCurrentThread();
        try
        {
            _uiSettings = new UISettings();
            _uiSettings.ColorValuesChanged += (_, _) => _dispatcher?.TryEnqueue(OnSystemColorsChanged);
        }
        catch
        {
            // 拿不到系统颜色设置时退化为固定值
        }

        Refresh();
    }

    /// <summary>注册主题包（预留接口，后续主题包系统调用）。</summary>
    public static void RegisterThemePack(IThemePack pack)
    {
        _themePack = pack;
        Refresh();
    }

    /// <summary>把某一窗口接入统一主题：设置根元素明暗并注入强调色资源，
    /// 使 RadioButton / ToggleSwitch / TextBox 等默认控件也跟随应用强调色。</summary>
    public static void Apply(FrameworkElement root)
    {
        if (!Roots.Contains(root)) Roots.Add(root);
        RootThemes[root] = CurrentElementTheme;
        root.RequestedTheme = CurrentElementTheme;

        var wrapper = CreateDictionaryForCurrentTheme();
        if (RootDictionaries.TryGetValue(root, out var old))
        {
            int index = root.Resources.MergedDictionaries.IndexOf(old);
            if (index >= 0) root.Resources.MergedDictionaries[index] = wrapper;
            else root.Resources.MergedDictionaries.Insert(0, wrapper);
        }
        else
        {
            root.Resources.MergedDictionaries.Insert(0, wrapper);
        }
        RootDictionaries[root] = wrapper;
    }

    /// <summary>把已关闭窗口的根元素从主题服务中移除，避免长驻引用。</summary>
    public static void Unapply(FrameworkElement root)
    {
        Roots.Remove(root);
        RootDictionaries.Remove(root);
        RootThemes.Remove(root);
    }

    /// <summary>重新应用当前主题到所有已接入窗口（调试事件触发用）。</summary>
    public static void Reapply() => Refresh();

    /// <summary>设置明暗主题并持久化。</summary>
    public static void SetMode(AppThemeMode mode)
    {
        if (AppSettings.ThemeMode == mode) return;
        AppSettings.ThemeMode = mode;
        AppSettings.Save();
        Refresh();
    }

    /// <summary>设置强调色来源并持久化。</summary>
    public static void SetAccentSource(AccentSourceMode source)
    {
        if (AppSettings.AccentSource == source) return;
        AppSettings.AccentSource = source;
        AppSettings.Save();
        Refresh();
    }

    /// <summary>设置自定义强调色并持久化。</summary>
    public static void SetCustomAccent(Color color)
    {
        int argb = unchecked((int)((uint)0xFF000000
            | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B));
        if (AppSettings.CustomAccentArgb == argb) return;
        AppSettings.CustomAccentArgb = argb;
        AppSettings.Save();
        Refresh();
    }

    private static void OnSystemColorsChanged()
    {
        // 仅当系统颜色实际影响当前配置时才刷新
        if (Mode == AppThemeMode.System || AccentSource == AccentSourceMode.System) Refresh();
    }

    /// <summary>重新计算主题与强调色，并同步所有已接入的窗口。</summary>
    private static void Refresh()
    {
        bool dark = CurrentElementTheme == ElementTheme.Dark;
        Color baseColor = ResolveAccentColor(CurrentElementTheme);
        AccentColor = baseColor;

        // 主填充按明暗取一档：浅色下加深、深色下提亮，与 WinUI 强调色填充观感一致
        Color fill = dark ? Lighten(baseColor, 0.22) : Darken(baseColor, 0.16);
        Color soft = Lighten(fill, 0.18);
        Color hover = Lighten(fill, 0.12);
        Color pressed = Darken(fill, 0.14);

        AccentFillBrush.Color = fill;
        AccentSoftFillBrush.Color = soft;
        AccentHoverFillBrush.Color = hover;
        AccentPressedFillBrush.Color = pressed;
        OnAccentFillBrush.Color = Luminance(fill) > 0.55 ? Colors.Black : Colors.White;
        AccentTintFillBrush.Color = WithAlpha(fill, 0x16);
        AccentTintHoverFillBrush.Color = WithAlpha(fill, 0x24);
        AccentTintPressedFillBrush.Color = WithAlpha(fill, 0x32);

        // 应用级资源：让 ContentDialog / Flyout 等弹出层也使用统一强调色
        ApplyGlobalEntries();

        // 已接入窗口：同步明暗并替换强调色资源字典
        foreach (var root in Roots)
        {
            RootThemes[root] = CurrentElementTheme;
            root.RequestedTheme = CurrentElementTheme;

            var wrapper = CreateDictionaryForCurrentTheme();
            if (RootDictionaries.TryGetValue(root, out var old))
            {
                int index = root.Resources.MergedDictionaries.IndexOf(old);
                if (index >= 0) root.Resources.MergedDictionaries[index] = wrapper;
                else root.Resources.MergedDictionaries.Insert(0, wrapper);
            }
            else
            {
                root.Resources.MergedDictionaries.Insert(0, wrapper);
            }
            RootDictionaries[root] = wrapper;
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>解析当前应使用的强调色：主题包优先，其次自定义，最后系统强调色。</summary>
    private static Color ResolveAccentColor(ElementTheme theme)
    {
        if (_themePack is not null && _themePack.GetAccentColor(theme) is { } packColor) return packColor;
        if (AccentSource == AccentSourceMode.Custom) return CustomAccent;

        try
        {
            var settings = _uiSettings ?? new UISettings();
            return settings.GetColorValue(UIColorType.Accent);
        }
        catch
        {
            return ColorFromArgb(255, 0, 120, 212);
        }
    }

    private static bool SystemPrefersDark()
    {
        try
        {
            var settings = _uiSettings ?? new UISettings();
            return settings.GetColorValue(UIColorType.Background).R < 128;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>构造一份把 WinUI 默认强调色资源指向应用强调色的资源字典。</summary>
    private static ResourceDictionary CreateDictionaryForCurrentTheme()
    {
        var wrapper = new ResourceDictionary();
        wrapper.ThemeDictionaries["Light"] = CreateAccentEntries();
        wrapper.ThemeDictionaries["Dark"] = CreateAccentEntries();
        wrapper.ThemeDictionaries["Default"] = CreateAccentEntries();
        return wrapper;
    }

    /// <summary>把强调色直接写入应用级资源，使 ContentDialog / Flyout 等弹出层同样跟随。</summary>
    private static void ApplyGlobalEntries()
    {
        var app = Application.Current?.Resources;
        if (app is null) return;
        foreach (var (key, value) in BuildAccentEntries()) app[key] = value;
    }

    private static ResourceDictionary CreateAccentEntries()
    {
        var d = new ResourceDictionary();
        foreach (var (key, value) in BuildAccentEntries()) d[key] = value;
        return d;
    }

    private static Dictionary<object, object> BuildAccentEntries()
    {
        var d = new Dictionary<object, object>();

        // 系统强调色色阶（部分控件模板直接使用 Color）
        d["SystemAccentColor"] = AccentColor;
        d["SystemAccentColorLight1"] = Lighten(AccentColor, 0.16);
        d["SystemAccentColorLight2"] = Lighten(AccentColor, 0.30);
        d["SystemAccentColorLight3"] = Lighten(AccentColor, 0.45);
        d["SystemAccentColorDark1"] = Darken(AccentColor, 0.16);
        d["SystemAccentColorDark2"] = Darken(AccentColor, 0.30);
        d["SystemAccentColorDark3"] = Darken(AccentColor, 0.45);

        // 通用强调色填充资源
        d["AppAccentBrush"] = AccentFillBrush;
        d["AppAccentSoftBrush"] = AccentSoftFillBrush;
        d["AccentFillColorDefaultBrush"] = AccentFillBrush;
        d["AccentFillColorSecondaryBrush"] = AccentHoverBrush;
        d["AccentFillColorTertiaryBrush"] = AccentPressedBrush;
        d["AccentFillColorSelectedTextBackgroundBrush"] = AccentFillBrush;
        d["TextOnAccentFillColorPrimaryBrush"] = OnAccentBrush;
        d["TextOnAccentFillColorSecondaryBrush"] = OnAccentBrush;
        d["TextOnAccentFillColorDisabledBrush"] = OnAccentBrush;

        // 强调色文字（超链接、文本按钮等）
        d["AccentTextFillColorPrimaryBrush"] = AccentFillBrush;
        d["AccentTextFillColorSecondaryBrush"] = AccentHoverBrush;
        d["AccentTextFillColorTertiaryBrush"] = AccentPressedBrush;

        // 常用控件：开关、单选项、复选项、输入框激活边框
        d["ToggleSwitchFillOn"] = AccentFillBrush;
        d["ToggleSwitchFillOnPointerOver"] = AccentHoverBrush;
        d["ToggleSwitchFillOnPressed"] = AccentPressedBrush;
        d["ToggleSwitchStrokeOn"] = AccentFillBrush;
        d["ToggleSwitchStrokeOnPointerOver"] = AccentHoverBrush;
        d["ToggleSwitchStrokeOnPressed"] = AccentPressedBrush;

        d["RadioButtonOuterEllipseCheckedFill"] = AccentFillBrush;
        d["RadioButtonOuterEllipseCheckedFillPointerOver"] = AccentHoverBrush;
        d["RadioButtonOuterEllipseCheckedFillPressed"] = AccentPressedBrush;
        d["RadioButtonOuterEllipseCheckedStroke"] = AccentFillBrush;
        d["RadioButtonCheckGlyphFill"] = OnAccentBrush;

        d["CheckBoxCheckBackgroundFillChecked"] = AccentFillBrush;
        d["CheckBoxCheckBackgroundFillCheckedPointerOver"] = AccentHoverBrush;
        d["CheckBoxCheckBackgroundFillCheckedPressed"] = AccentPressedBrush;
        d["CheckBoxCheckGlyphForegroundChecked"] = OnAccentBrush;

        d["TextControlBorderBrushFocused"] = AccentFillBrush;
        d["TextControlElevationBorderFocusedBrush"] = AccentFillBrush;

        return d;
    }

    private static Color ColorFromArgb(byte a, byte r, byte g, byte b) =>
        Microsoft.UI.ColorHelper.FromArgb(a, r, g, b);

    /// <summary>向白色混合。</summary>
    private static Color Lighten(Color color, double amount) =>
        ColorFromArgb(color.A,
            (byte)(color.R + (255 - color.R) * amount),
            (byte)(color.G + (255 - color.G) * amount),
            (byte)(color.B + (255 - color.B) * amount));

    /// <summary>向黑色混合。</summary>
    private static Color Darken(Color color, double amount) =>
        ColorFromArgb(color.A,
            (byte)(color.R * (1 - amount)),
            (byte)(color.G * (1 - amount)),
            (byte)(color.B * (1 - amount)));

    private static Color WithAlpha(Color color, byte alpha) =>
        ColorFromArgb(alpha, color.R, color.G, color.B);

    private static double Luminance(Color color) =>
        (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255;
}
