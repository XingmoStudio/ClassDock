using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Windows.Storage.Streams;
using Windows.UI;
using Windows.Foundation;
using PathShape = Microsoft.UI.Xaml.Shapes.Path;

namespace ClassDock;

/// <summary>
/// 设置窗口：顶栏与三个系统窗口按键合并（内容延伸进标题栏），
/// 顶栏右端仅显示版本号；左侧导航（关于 / 主页 / 通用 / 小工具 / 调试 + 关闭软件）+ 右侧内容页。
/// 由 PanelWindow 齿轮按钮打开。
/// </summary>
public sealed partial class SettingsWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private static readonly SolidColorBrush SelectedBackground = new();
    private static readonly SolidColorBrush TransparentBrush = new(Colors.Transparent);

    private Button? _selectedNav;       // 当前选中的导航按钮（用于切换选中态底色）
    private Border? _selectedAccent;    // 当前选中按钮的强调色竖条
    private bool _loadingSettings;      // 加载设置中，屏蔽 Checked 回调以避免误触保存
    private bool _loadingAppearance;    // 加载外观设置中，屏蔽主题控件的 SelectionChanged

    private ColorPicker? _accentPicker;
    private Flyout? _accentFlyout;

    // 侧边栏收起状态与两个分组的展开状态
    private bool _collapsed;
    private bool _sidebarBusy;          // 动画进行中，阻止重复触发收起/展开
    private bool _generalExpanded = true;
    private bool _toolsExpanded = true;
    private bool _integrationsExpanded = true;

    // 版本号连点解锁调试页：7 次点击，间隔超过 1.5 秒重新计数
    private int _versionTapCount;
    private DateTime _lastVersionTap;

    // 最近一次统计到的占用数据（主题切换时用同样的数据重绘环状图）
    private List<(string Label, long Size)> _storageParts = [];

    // 主页欢迎语打字机：只用一个计时器，重复进入主页时先停再重启
    private readonly DispatcherTimer _greetingTimer = new();
    private string _homeGreeting = string.Empty;
    private int _greetingIndex;

    private static readonly Thickness IconMarginExpanded = new(7, 0, 0, 0);
    private static readonly Thickness IconMarginCollapsed = new(6.5, 0, 0, 0);
    private static readonly Thickness NavPadding = new(8, 0, 8, 0);
    private static readonly Thickness NavPaddingCollapsed = new(0);
    private static readonly Thickness HeaderMarginExpanded = new(10, 2, 0, 12);
    private static readonly Thickness HeaderMarginCollapsed = new(4, 2, 0, 12);

    public SettingsWindow()
    {
        InitializeComponent();
        App.ApplyWindowIcon(AppWindow);

        // 接入统一主题：明暗与强调色由 AppTheme 统一下发，窗口根元素跟随切换
        AppTheme.Apply(Root);
        AppTheme.Changed += OnAppThemeChanged;
        Closed += (_, _) =>
        {
            _greetingTimer.Stop();
            AppTheme.Changed -= OnAppThemeChanged;
            AppTheme.Unapply(Root);
        };
        _greetingTimer.Tick += OnGreetingTick;

        // 标题栏合并：AppTitleBar 作为拖拽区，右上角三个按键由系统绘制
        // 必须先开启内容延伸，之后才能调整标题栏按键配色与高度
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        OnAppThemeChanged(null, EventArgs.Empty);

        // 居中显示（保持默认可调/可最大化，确保“最大化/还原”可用）
        // 尺寸按 DPI 换算成物理像素，避免高缩放下窗口内容被挤小
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        int width = (int)(900 * scale), height = (int)(700 * scale);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        var workArea = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2));

        // 默认页：勾选“打开设置时默认打开此页”时落在主页，否则保持“关于 ClassDock”
        if (AppSettings.OpenHomeByDefault)
        {
            ShowPage("home");
            SelectNav(NavHome, FindAccentBorder(NavHome));
        }
        else
        {
            SelectNav(NavAbout, AboutAccent);
        }

        // 二级条目在 XAML 中按顺序排列，这里统一补齐 Tag（与导航顺序一一对应）
        AssignNavTags(GeneralChildren, "basic", "appearance", "storage", "security", "shortcuts");

        // 调试页默认隐藏，解锁后才出现在侧边栏
        NavDebug.Visibility = AppSettings.DebugUnlocked ? Visibility.Visible : Visibility.Collapsed;

        LoadCrashBehavior();
        LoadAppearanceSettings();
        LoadBasicSettings();
        LoadAnnotationSettings();
        LoadHomeSettings();
        AllowMultiInstanceSwitch.IsOn = AppSettings.AllowMultiInstance;
    }

    /// <summary>按顺序给分组内的二级导航按钮写 Tag（避免在 XAML 里逐个重复修改）。</summary>
    private static void AssignNavTags(StackPanel panel, params string[] tags)
    {
        for (int i = 0; i < tags.Length && i < panel.Children.Count; i++)
            if (panel.Children[i] is Button button) button.Tag = tags[i];
    }

    /// <summary>系统标题栏按键配色：透明底，前景随明暗主题；顶栏行高 48 DIP 同步加高。</summary>
    private void ApplyTitleBarColors()
    {
        var tb = AppWindow.TitleBar;
        bool dark = AppTheme.IsDark;
        tb.BackgroundColor = Colors.Transparent;
        tb.ForegroundColor = Colors.Transparent;
        tb.InactiveBackgroundColor = Colors.Transparent;
        tb.InactiveForegroundColor = Colors.Transparent;
        tb.ButtonBackgroundColor = Colors.Transparent;
        tb.ButtonInactiveBackgroundColor = Colors.Transparent;
        tb.ButtonForegroundColor = dark ? Colors.White : Color.FromArgb(230, 0, 0, 0);
        tb.ButtonInactiveForegroundColor = dark
            ? Color.FromArgb(128, 255, 255, 255)
            : Color.FromArgb(128, 0, 0, 0);
        tb.ButtonHoverBackgroundColor = dark
            ? Color.FromArgb(26, 255, 255, 255)
            : Color.FromArgb(26, 0, 0, 0);
        tb.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        tb.ButtonPressedBackgroundColor = dark
            ? Color.FromArgb(51, 255, 255, 255)
            : Color.FromArgb(13, 0, 0, 0);
        tb.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;
    }

    /// <summary>主题或强调色变化：同步选中底色、标题栏与强调色预览。</summary>
    private void OnAppThemeChanged(object? sender, EventArgs e)
    {
        SelectedBackground.Color = AppTheme.IsDark
            ? Color.FromArgb(0xFF, 0x38, 0x38, 0x38)
            : Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
        ApplyTitleBarColors();
        UpdateAccentVisuals();
        UpdateHomeBannerFade();
        if (_accentPicker is not null) _accentPicker.RequestedTheme = AppTheme.CurrentElementTheme;
        if (_storageParts.Count > 0)
        {
            BuildStorageRing();
            BuildStorageLegend();
        }
    }

    /// <summary>「允许多开实例」开关：即时生效并持久化；开启后重复启动不再提示。</summary>
    private void OnAllowMultiInstanceToggled(object sender, RoutedEventArgs e)
    {
        AppSettings.AllowMultiInstance = AllowMultiInstanceSwitch.IsOn;
        AppSettings.Save();
    }

    /// <summary>导航按钮点击：切换右侧内容页与选中态。</summary>
    private void OnNavClick(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        ShowPage(button.Tag as string ?? "blank");
        SelectNav(button, FindAccentBorder(button));
    }

    /// <summary>导航项内容栅格中 3px 宽的 Border 即选中态竖条。</summary>
    private static Border? FindAccentBorder(Button button)
    {
        if (button.Content is not Grid grid) return null;
        foreach (var child in grid.Children)
        {
            if (child is Border border && border.Width == 3) return border;
        }
        return null;
    }

    /// <summary>切换右侧内容页；未开放页面统一使用占位页。</summary>
    private void ShowPage(string tag)
    {
        AboutPage.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePage.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        BasicPage.Visibility = tag == "basic" ? Visibility.Visible : Visibility.Collapsed;
        StoragePage.Visibility = tag == "storage" ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPage.Visibility = tag == "shortcuts" ? Visibility.Visible : Visibility.Collapsed;
        AnnotationPage.Visibility = tag == "annotation" ? Visibility.Visible : Visibility.Collapsed;
        DebugPage.Visibility = tag == "debug" ? Visibility.Visible : Visibility.Collapsed;
        HomePage.Visibility = tag == "home" ? Visibility.Visible : Visibility.Collapsed;
        PlaceholderPage.Visibility = tag is "security" or "timer" or "quicklaunch" or "secrandom" or "classisland"
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (tag == "storage") _ = RefreshStorageUsageAsync();
        if (tag == "home")
        {
            EnsureHomeBannerLoaded();
            StartGreetingTyping();
        }
        else
        {
            StopGreetingTyping();
        }
    }

    /// <summary>选中态：卡片色底 + 左侧强调色竖条（两者颜色均由统一主题服务提供）。</summary>
    private void SelectNav(Button button, Border? accent)
    {
        if (_selectedNav is not null) _selectedNav.Background = TransparentBrush;
        if (_selectedAccent is not null) _selectedAccent.Background = TransparentBrush;

        _selectedNav = button;
        _selectedAccent = accent;
        button.Background = SelectedBackground;
        if (accent is not null) accent.Background = AppTheme.AccentBrush;
    }

    private void OnGeneralGroupClick(object sender, RoutedEventArgs e)
        => NavigateGroup(NavGeneral, GeneralAccent, GeneralChildren, () => _generalExpanded = true);

    private void OnToolsGroupClick(object sender, RoutedEventArgs e)
        => NavigateGroup(NavTools, ToolsAccent, ToolsChildren, () => _toolsExpanded = true);

    private void OnIntegrationsGroupClick(object sender, RoutedEventArgs e)
        => NavigateGroup(NavIntegrations, IntegrationsAccent, IntegrationsChildren, () => _integrationsExpanded = true);

    /// <summary>点击一级菜单：展开该组并跳到第一个二级页面。
    /// 侧边栏收起时二级项不可见，改为高亮一级项本身，保证缩放状态下点击同样能跳转。</summary>
    private void NavigateGroup(Button parent, Border accent, StackPanel children, Action expand)
    {
        expand();
        ApplyGroupState();
        if (children.Children.Count == 0 || children.Children[0] is not Button first) return;

        ShowPage(first.Tag as string ?? "blank");
        if (_collapsed) SelectNav(parent, accent);
        else SelectNav(first, FindAccentBorder(first));
    }

    /// <summary>分组右侧箭头：只负责展开/收起，不跳转（箭头与导航按钮是并列控件）。</summary>
    private void OnGeneralChevronClick(object sender, RoutedEventArgs e)
    {
        if (_collapsed || _sidebarBusy) return;
        _generalExpanded = !_generalExpanded;
        ApplyGroupState();
    }

    private void OnToolsChevronClick(object sender, RoutedEventArgs e)
    {
        if (_collapsed || _sidebarBusy) return;
        _toolsExpanded = !_toolsExpanded;
        ApplyGroupState();
    }

    private void OnIntegrationsChevronClick(object sender, RoutedEventArgs e)
    {
        if (_collapsed || _sidebarBusy) return;
        _integrationsExpanded = !_integrationsExpanded;
        ApplyGroupState();
    }

    /// <summary>子菜单可见性与分组箭头（无动画，供展开/收起分组时调用）。</summary>
    private void ApplyGroupState()
    {
        GeneralChildren.Visibility = !_collapsed && _generalExpanded ? Visibility.Visible : Visibility.Collapsed;
        ToolsChildren.Visibility = !_collapsed && _toolsExpanded ? Visibility.Visible : Visibility.Collapsed;
        IntegrationsChildren.Visibility = !_collapsed && _integrationsExpanded ? Visibility.Visible : Visibility.Collapsed;
        GeneralChevronIcon.Glyph = _generalExpanded ? "\uE70D" : "\uE70E";
        ToolsChevronIcon.Glyph = _toolsExpanded ? "\uE70D" : "\uE70E";
        IntegrationsChevronIcon.Glyph = _integrationsExpanded ? "\uE70D" : "\uE70E";
    }

    /// <summary>汉堡键：收起/展开侧边栏，附带宽度渐变与文字淡入淡出动画。</summary>
    private async void OnCollapseToggleClick(object sender, RoutedEventArgs e)
    {
        if (_sidebarBusy) return;
        _sidebarBusy = true;
        _collapsed = !_collapsed;

        // 动画级别为「无」：直接切换最终状态，不走渐变
        if (AppSettings.AnimationLevel == AnimationLevel.None)
        {
            SidebarPanel.Width = _collapsed ? 52 : 208;
            HeaderBar.Margin = _collapsed ? HeaderMarginCollapsed : HeaderMarginExpanded;
            ApplyIconLayout(collapsed: _collapsed);
            SetTextOpacity(_collapsed ? 0 : 1);
            SetTextVisibility(_collapsed ? Visibility.Collapsed : Visibility.Visible);
            ApplyGroupState();
            _sidebarBusy = false;
            return;
        }

        if (_collapsed)
        {
            // 先收起子菜单并淡出文字，再收缩宽度
            GeneralChildren.Visibility = Visibility.Collapsed;
            ToolsChildren.Visibility = Visibility.Collapsed;
            IntegrationsChildren.Visibility = Visibility.Collapsed;
            await FadeSidebarTextAsync(0, 90);
            SetTextVisibility(Visibility.Collapsed);
            HeaderBar.Margin = HeaderMarginCollapsed;
            ApplyIconLayout(collapsed: true);
            await AnimateSidebarWidthAsync(52, 170);
        }
        else
        {
            // 先展开宽度，再恢复图标布局与文字，避免内容在过窄时被挤压
            await AnimateSidebarWidthAsync(208, 170);
            HeaderBar.Margin = HeaderMarginExpanded;
            ApplyIconLayout(collapsed: false);
            SetTextOpacity(0);
            SetTextVisibility(Visibility.Visible);
            ApplyGroupState();
            await FadeSidebarTextAsync(1, 130);
        }

        _sidebarBusy = false;
    }

    /// <summary>图标边距与各导航项内边距（静态部分，不做动画）。</summary>
    private void ApplyIconLayout(bool collapsed)
    {
        var iconMargin = collapsed ? IconMarginCollapsed : IconMarginExpanded;
        AboutIcon.Margin = iconMargin;
        HomeIcon.Margin = iconMargin;
        GeneralIcon.Margin = iconMargin;
        ToolsIcon.Margin = iconMargin;
        IntegrationsIcon.Margin = iconMargin;
        DebugIcon.Margin = iconMargin;

        var padding = collapsed ? NavPaddingCollapsed : NavPadding;
        NavAbout.Padding = padding;
        NavHome.Padding = padding;
        NavGeneral.Padding = padding;
        NavTools.Padding = padding;
        NavIntegrations.Padding = padding;
        NavDebug.Padding = padding;
        ExitButton.Padding = padding;
        ExitIcon.Margin = collapsed ? new Thickness(11, 0, 0, 0) : new Thickness(0);
    }

    private void SetTextVisibility(Visibility visibility)
    {
        SettingsTitle.Visibility = visibility;
        AboutText.Visibility = visibility;
        HomeText.Visibility = visibility;
        GeneralText.Visibility = visibility;
        ToolsText.Visibility = visibility;
        IntegrationsText.Visibility = visibility;
        DebugText.Visibility = visibility;
        ExitText.Visibility = visibility;
        GeneralChevron.Visibility = visibility;
        ToolsChevron.Visibility = visibility;
        IntegrationsChevron.Visibility = visibility;
    }

    private void SetTextOpacity(double opacity)
    {
        SettingsTitle.Opacity = opacity;
        AboutText.Opacity = opacity;
        HomeText.Opacity = opacity;
        GeneralText.Opacity = opacity;
        ToolsText.Opacity = opacity;
        IntegrationsText.Opacity = opacity;
        DebugText.Opacity = opacity;
        ExitText.Opacity = opacity;
        GeneralChevron.Opacity = opacity;
        ToolsChevron.Opacity = opacity;
        IntegrationsChevron.Opacity = opacity;
    }

    /// <summary>侧边栏宽度渐变（Border 的 Width 可直接用 Storyboard 动画）。</summary>
    private Task AnimateSidebarWidthAsync(double to, double ms)
    {
        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = SidebarPanel.Width,
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            // Width 属于影响布局的依赖动画，不开启会被静默跳过
            EnableDependentAnimation = true,
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
            }
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, SidebarPanel);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Width");

        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        sb.Children.Add(anim);
        var tcs = new TaskCompletionSource();
        sb.Completed += (_, _) => tcs.SetResult();
        sb.Begin();
        return tcs.Task;
    }

    /// <summary>标题/导航文字/箭头的整体淡入淡出。</summary>
    private Task FadeSidebarTextAsync(double to, double ms)
    {
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        foreach (var element in new UIElement[]
        {
            SettingsTitle, AboutText, HomeText, GeneralText, ToolsText,
            IntegrationsText, DebugText, ExitText, GeneralChevron, ToolsChevron, IntegrationsChevron
        })
        {
            var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                To = to,
                Duration = TimeSpan.FromMilliseconds(ms)
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, element);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "Opacity");
            sb.Children.Add(anim);
        }
        var tcs = new TaskCompletionSource();
        sb.Completed += (_, _) => tcs.SetResult();
        sb.Begin();
        return tcs.Task;
    }

    /// <summary>从 AppSettings 读取崩溃处理策略并同步到下拉框。</summary>
    private void LoadCrashBehavior()
    {
        _loadingSettings = true;
        CrashBehaviorCombo.SelectedIndex = AppSettings.CrashBehavior switch
        {
            CrashBehavior.SilentExit => 0,
            CrashBehavior.SilentRestart => 2,
            CrashBehavior.Ignore => 3,
            _ => 1
        };
        _loadingSettings = false;
    }

    /// <summary>基本页：开机自启状态以注册表为准。</summary>
    private void LoadBasicSettings()
    {
        _loadingSettings = true;
        AutoStartSwitch.IsOn = AppSettings.IsAutoStartEnabled();
        _loadingSettings = false;
    }

    /// <summary>批注设置页：交互模式、笔色记忆、采样率与两个实验项。</summary>
    private void LoadAnnotationSettings()
    {
        _loadingSettings = true;
        InteractionModeCombo.SelectedIndex = AppSettings.InteractionMode == AnnotationInteractionMode.Touch ? 1 : 0;
        RememberPenColorSwitch.IsOn = AppSettings.RememberPenColor;
        SampleRateCombo.SelectedIndex = AppSettings.SampleRate switch
        {
            AnnotationSampleRate.High => 0,
            AnnotationSampleRate.Low => 2,
            _ => 1
        };
        // 多指绘图暂不可用：开关固定关闭并置灰
        MultiTouchSwitch.IsOn = false;
        PenTaperSwitch.IsOn = AppSettings.PenTaperCorrection;
        SpeedEraserSwitch.IsOn = AppSettings.SpeedEraser;

        _loadingAppearance = true;
        AnimationLevelCombo.SelectedIndex = AppSettings.AnimationLevel == AnimationLevel.None ? 1 : 0;
        _loadingAppearance = false;
        _loadingSettings = false;
    }

    /// <summary>「软件开机自启」：写入/移除 HKCU Run 项，失败时回读注册表实际状态。</summary>
    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        AppSettings.SetAutoStart(AutoStartSwitch.IsOn);
        bool actual = AppSettings.IsAutoStartEnabled();
        if (actual != AutoStartSwitch.IsOn)
        {
            _loadingSettings = true;
            AutoStartSwitch.IsOn = actual;
            _loadingSettings = false;
        }
    }

    /// <summary>动画级别下拉：写入设置后立即对后续动画生效。</summary>
    private void OnAnimationLevelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAppearance) return;
        AppSettings.AnimationLevel = (AnimationLevelCombo.SelectedItem as ComboBoxItem)?.Tag as string == "None"
            ? AnimationLevel.None
            : AnimationLevel.Standard;
        AppSettings.Save();
    }

    private void OnInteractionModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        AppSettings.InteractionMode = (InteractionModeCombo.SelectedItem as ComboBoxItem)?.Tag as string == "Touch"
            ? AnnotationInteractionMode.Touch
            : AnnotationInteractionMode.Desktop;
        AppSettings.Save();
    }

    private void OnRememberPenColorToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        AppSettings.RememberPenColor = RememberPenColorSwitch.IsOn;
        AppSettings.Save();
    }

    private void OnSampleRateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        AppSettings.SampleRate = ((SampleRateCombo.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "High" => AnnotationSampleRate.High,
            "Low" => AnnotationSampleRate.Low,
            _ => AnnotationSampleRate.Default
        };
        AppSettings.Save();
    }

    private void OnPenTaperToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        AppSettings.PenTaperCorrection = PenTaperSwitch.IsOn;
        AppSettings.Save();
    }

    private void OnSpeedEraserToggled(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        AppSettings.SpeedEraser = SpeedEraserSwitch.IsOn;
        AppSettings.Save();
    }

    /// <summary>按当前设置初始化外观与动画页的主题控件。</summary>
    private void LoadAppearanceSettings()
    {
        _loadingAppearance = true;
        ThemeModeCombo.SelectedIndex = AppSettings.ThemeMode switch
        {
            AppThemeMode.Light => 1,
            AppThemeMode.Dark => 2,
            _ => 0
        };
        AccentSourceCombo.SelectedIndex = AppSettings.AccentSource == AccentSourceMode.Custom ? 1 : 0;
        CustomAccentRow.Visibility = AppSettings.AccentSource == AccentSourceMode.Custom
            ? Visibility.Visible
            : Visibility.Collapsed;
        _loadingAppearance = false;
        UpdateAccentVisuals();
    }

    /// <summary>刷新强调色预览色块与十六进制文本。</summary>
    private void UpdateAccentVisuals()
    {
        if (AccentSwatch is null) return;
        var color = AppTheme.AccentColor;
        AccentSwatch.Background = new SolidColorBrush(color);
        AccentHexText.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    /// <summary>应用主题下拉：跟随系统 / 明亮 / 黑暗，立即生效并持久化。</summary>
    private void OnThemeModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAppearance) return;
        AppTheme.SetMode(ThemeModeCombo.SelectedIndex switch
        {
            1 => AppThemeMode.Light,
            2 => AppThemeMode.Dark,
            _ => AppThemeMode.System
        });
    }

    /// <summary>主题色来源下拉：系统 / 自定义；自定义时展开强调色参数。</summary>
    private void OnAccentSourceChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingAppearance) return;
        var source = AccentSourceCombo.SelectedIndex == 1
            ? AccentSourceMode.Custom
            : AccentSourceMode.System;
        CustomAccentRow.Visibility = source == AccentSourceMode.Custom
            ? Visibility.Visible
            : Visibility.Collapsed;
        AppTheme.SetAccentSource(source);
    }

    /// <summary>强调色按钮：弹出由代码创建的圆环选色器（XAML 内联 Flyout/ColorPicker 会让编译器静默崩溃）。</summary>
    private void OnAccentColorClick(object sender, RoutedEventArgs e)
    {
        EnsureAccentPicker();
        _accentPicker!.Color = AppTheme.CustomAccent;
        _accentFlyout!.ShowAt(AccentColorButton);
        // 模板首次应用后才吃 ColorSpectrumShape，打开后再赋一次确保是圆环
        DispatcherQueue.TryEnqueue(() => _accentPicker!.ColorSpectrumShape = ColorSpectrumShape.Ring);
    }

    /// <summary>按需创建强调色选色器与承载它的 Flyout。</summary>
    private void EnsureAccentPicker()
    {
        if (_accentFlyout is not null) return;

        _accentPicker = new ColorPicker
        {
            Width = 384,
            Height = 384,
            MinWidth = 0,
            MinHeight = 0,
            IsAlphaEnabled = false,
            IsColorChannelTextInputVisible = false,
            IsColorPreviewVisible = false,
            IsColorSliderVisible = false,
            IsHexInputVisible = false,
            IsMoreButtonVisible = false,
            ColorSpectrumShape = ColorSpectrumShape.Ring,
            RequestedTheme = AppTheme.CurrentElementTheme
        };
        _accentPicker.ColorChanged += OnAccentColorChanged;

        _accentFlyout = new Flyout
        {
            Placement = FlyoutPlacementMode.Bottom,
            Content = new Viewbox { Width = 220, Height = 220, Child = _accentPicker }
        };
    }

    /// <summary>选色器颜色变化：写入统一主题服务并持久化。</summary>
    private void OnAccentColorChanged(ColorPicker sender, ColorChangedEventArgs args)
    {
        if (_loadingAppearance) return;
        AppTheme.SetCustomAccent(args.NewColor);
        UpdateAccentVisuals();
    }

    private async void OnCrashBehaviorChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;

        var behavior = ((CrashBehaviorCombo.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "SilentExit" => CrashBehavior.SilentExit,
            "SilentRestart" => CrashBehavior.SilentRestart,
            "Ignore" => CrashBehavior.Ignore,
            _ => CrashBehavior.ShowError
        };

        if (behavior == CrashBehavior.Ignore)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                RequestedTheme = AppTheme.CurrentElementTheme,
                Title = "确认选择「不处理」？",
                PrimaryButtonText = "确认选择",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                Content = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Text = "选择后，应用发生任何错误时都不会提示、也不会退出，错误将被完全忽略。"
                        + "\n\n此选项风险性极高：应用可能在已损坏的状态下继续运行，导致功能异常、数据错误或其他无法预料的行为，且不会有任何反馈，问题将难以排查。"
                        + "\n\n确定要这样处理错误吗？"
                }
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                // 取消：还原为当前生效的处理方式
                LoadCrashBehavior();
                return;
            }
        }

        AppSettings.CrashBehavior = behavior;
        AppSettings.Save();
    }

    /// <summary>主动抛出未处理异常，验证崩溃处理方式是否按预期生效。</summary>
    private void OnThrowTestErrorClick(object sender, RoutedEventArgs e)
    {
        var message = CrashTestInput.Text.Trim();
        if (message.Length == 0) message = "这是一条主动抛出的测试错误。";
        throw new InvalidOperationException(message);
    }

    // ── 配置与存储 ──

    /// <summary>统计 data 目录与程序目录（不含 data）的占用，并重绘环状图与图例（后台线程计算）。</summary>
    private async Task RefreshStorageUsageAsync()
    {
        string dataDir = AppPaths.DataDir;
        string programDir = AppPaths.Root;

        var result = await Task.Run(() =>
        {
            long dataSize = DirectorySize(dataDir);
            // 程序目录包含 data 时要把 data 排除，避免同一份数据被算两遍
            long programSize = DirectorySize(programDir, dataDir);
            var files = new List<(string Name, long Size)>();
            try
            {
                foreach (var file in new DirectoryInfo(dataDir).EnumerateFiles())
                    files.Add((file.Name, file.Length));
            }
            catch
            {
                // data 目录不存在或不可读时忽略
            }
            return (dataSize, programSize, files);
        });

        StorageTotalText.Text = FormatSize(result.dataSize + result.programSize);
        _storageParts =
        [
            ("data 目录", result.dataSize),
            ("程序文件", result.programSize)
        ];
        BuildStorageRing();
        BuildStorageLegend();

        StorageFilesText.Text = result.files.Count == 0
            ? "data 目录为空。"
            : "data 目录文件：" + string.Join("，", result.files.Select(f => $"{f.Name}（{FormatSize(f.Size)}）"));
    }

    /// <summary>递归统计目录大小；excludeDir 用于跳过子目录（程序目录统计时排除 data）。</summary>
    private static long DirectorySize(string path, string? excludeDir = null)
    {
        if (!Directory.Exists(path)) return 0;
        long total = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // 单个文件读不到大小就跳过
                }
            }

            foreach (var dir in Directory.EnumerateDirectories(path))
            {
                if (excludeDir is not null && IsSamePath(dir, excludeDir)) continue;
                total += DirectorySize(dir);
            }
        }
        catch
        {
            // 目录遍历失败时返回已累计的部分
        }
        return total;
    }

    private static bool IsSamePath(string a, string b) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>把字节数格式化为易读单位。</summary>
    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{bytes} B" : $"{value:0.#} {units[unit]}";
    }

    /// <summary>环状图配色：首段用应用强调色，第二段向对比方向混合以便区分。</summary>
    private static Color RingColor(int index)
    {
        if (index == 0) return AppTheme.AccentColor;
        var toward = AppTheme.IsDark ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xFF, 0x00, 0x00, 0x00);
        return BlendColor(AppTheme.AccentColor, toward, 0.45);
    }

    private static Color BlendColor(Color from, Color to, double amount)
    {
        static byte Mix(byte a, byte b, double t) => (byte)Math.Round(a + (b - a) * t);
        return Color.FromArgb(0xFF, Mix(from.R, to.R, amount), Mix(from.G, to.G, amount), Mix(from.B, to.B, amount));
    }

    /// <summary>绘制占用环状图：每类数据一段圆弧，类与类之间留 2° 缝隙。</summary>
    private void BuildStorageRing()
    {
        StorageRingCanvas.Children.Clear();
        const double size = 180, thickness = 16;
        double radius = (size - thickness) / 2 - 4;
        var center = new Point(size / 2, size / 2);

        // 背景轨道
        var track = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = radius * 2,
            Height = radius * 2,
            StrokeThickness = thickness,
            Stroke = new SolidColorBrush(Color.FromArgb(
                AppTheme.IsDark ? (byte)0x1F : (byte)0x14, 0x00, 0x00, 0x00))
        };
        Canvas.SetLeft(track, center.X - radius);
        Canvas.SetTop(track, center.Y - radius);
        StorageRingCanvas.Children.Add(track);

        long total = _storageParts.Sum(p => p.Size);
        if (total <= 0) return;

        int visibleParts = _storageParts.Count(p => p.Size > 0);
        double start = 0;
        for (int i = 0; i < _storageParts.Count; i++)
        {
            var part = _storageParts[i];
            if (part.Size <= 0) continue;

            double sweep = part.Size / (double)total * 360.0;
            var color = RingColor(i);
            if (visibleParts == 1)
            {
                // 单类独占：整圈用 Ellipse，避免 360° 圆弧起点终点重合退化
                var ring = new Microsoft.UI.Xaml.Shapes.Ellipse
                {
                    Width = radius * 2,
                    Height = radius * 2,
                    StrokeThickness = thickness,
                    Stroke = new SolidColorBrush(color)
                };
                Canvas.SetLeft(ring, center.X - radius);
                Canvas.SetTop(ring, center.Y - radius);
                StorageRingCanvas.Children.Add(ring);
                break;
            }

            StorageRingCanvas.Children.Add(
                CreateArc(center, radius, start, Math.Max(sweep - 2, 0.5), color, thickness));
            start += sweep;
        }
    }

    /// <summary>生成一段圆弧（0° 在 12 点方向，顺时针）。</summary>
    private static PathShape CreateArc(
        Point center, double radius, double startDeg, double sweepDeg, Color color, double thickness)
    {
        double startRad = (startDeg - 90) * Math.PI / 180;
        double endRad = (startDeg + sweepDeg - 90) * Math.PI / 180;
        var figure = new PathFigure
        {
            StartPoint = new Point(
                center.X + radius * Math.Cos(startRad),
                center.Y + radius * Math.Sin(startRad))
        };
        figure.Segments.Add(new ArcSegment
        {
            Point = new Point(
                center.X + radius * Math.Cos(endRad),
                center.Y + radius * Math.Sin(endRad)),
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = sweepDeg > 180
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return new PathShape
        {
            Data = geometry,
            Stroke = new SolidColorBrush(color),
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Flat,
            StrokeEndLineCap = PenLineCap.Flat
        };
    }

    /// <summary>环状图右侧图例：色点 + 名称 + 占用大小。</summary>
    private void BuildStorageLegend()
    {
        StorageLegend.Children.Clear();
        for (int i = 0; i < _storageParts.Count; i++)
        {
            var part = _storageParts[i];
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var dot = new Border
            {
                Width = 10,
                Height = 10,
                CornerRadius = new CornerRadius(5),
                VerticalAlignment = VerticalAlignment.Center,
                Background = new SolidColorBrush(RingColor(i))
            };
            var label = new TextBlock
            {
                Text = part.Label,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center
            };
            var size = new TextBlock
            {
                Text = FormatSize(part.Size),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(AppTheme.IsDark
                    ? Color.FromArgb(0xFF, 0xB3, 0xB3, 0xB3)
                    : Color.FromArgb(0xFF, 0x60, 0x60, 0x60))
            };

            Grid.SetColumn(label, 1);
            Grid.SetColumn(size, 2);
            row.Children.Add(dot);
            row.Children.Add(label);
            row.Children.Add(size);
            StorageLegend.Children.Add(row);
        }
    }

    /// <summary>备份配置：把当前 Config.json 复制到 data\Backup 下，文件名带时间戳。</summary>
    private async void OnBackupConfigClick(object sender, RoutedEventArgs e)
    {
        try
        {
            AppSettings.Save();
            Directory.CreateDirectory(AppPaths.BackupDir);
            string target = Path.Combine(
                AppPaths.BackupDir, $"Config-{DateTime.Now:yyyyMMdd-HHmmss}.json");
            File.Copy(AppSettings.SettingsFilePath, target, overwrite: true);
            await ShowMessageAsync("备份完成", $"配置已备份到：\n{target}");
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("备份失败", ex.Message);
        }
    }

    /// <summary>重置配置：二次确认后删除配置文件并重启应用，使所有设置回到默认值。</summary>
    private async void OnResetConfigClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = AppTheme.CurrentElementTheme,
            Title = "重置配置？",
            PrimaryButtonText = "重置并重启",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = "将删除 data\\Config.json，并重新启动 ClassDock。"
                    + "\n\n主题、强调色、批注与实验项设置都会恢复到默认值，此操作无法撤销。"
            }
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            File.Delete(AppSettings.SettingsFilePath);
        }
        catch
        {
            // 文件不存在或占用时仍继续重启
        }

        try
        {
            if (Environment.ProcessPath is string exe)
            {
                // --crash-restart 让新实例跳过单实例检查，避免旧进程尚未完全退出时被拦下
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    ArgumentList = { "--crash-restart" }
                });
            }
        }
        catch
        {
            // 启动失败时至少完成退出
        }
        Environment.Exit(0);
    }

    /// <summary>通用消息对话框。</summary>
    private async Task ShowMessageAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = AppTheme.CurrentElementTheme,
            Title = title,
            CloseButtonText = "好",
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = content }
        };
        await dialog.ShowAsync();
    }

    /// <summary>连续点击关于页版本号 7 次后解锁「测试与调试」，需要输入风险确认文本。</summary>
    private async void OnAboutVersionTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if (AppSettings.DebugUnlocked) return;

        var now = DateTime.UtcNow;
        _versionTapCount = (now - _lastVersionTap).TotalSeconds <= 1.5 ? _versionTapCount + 1 : 1;
        _lastVersionTap = now;
        if (_versionTapCount < 7) return;
        _versionTapCount = 0;

        var input = new TextBox
        {
            Width = 320,
            PlaceholderText = "我已知晓其风险",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = "「测试与调试」包含会导致应用主动崩溃、允许重复启动等高风险操作，仅用于开发与排障。"
                + "\n\n如确认继续，请在下方输入「我已知晓其风险」。"
        });
        content.Children.Add(input);

        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = AppTheme.CurrentElementTheme,
            Title = "解锁测试与调试？",
            PrimaryButtonText = "解锁",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            Content = content
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        if (input.Text.Trim() != "我已知晓其风险")
        {
            await ShowMessageAsync("输入不正确", "需要完整输入「我已知晓其风险」才能解锁。");
            return;
        }

        AppSettings.DebugUnlocked = true;
        AppSettings.Save();
        NavDebug.Visibility = Visibility.Visible;
        await ShowMessageAsync("已解锁", "「测试与调试」已添加到侧边栏最下方。");
    }

    private void OnNavExitClick(object sender, RoutedEventArgs e)
    {
        // 结束整个 ClassDock 进程（含白条、面板等所有窗口）
        Environment.Exit(0);
    }

    // ── 主页 ──

    /// <summary>项目仓库地址（主页 STAR 按钮与关于页共用同一地址）。</summary>
    private const string RepositoryUrl = "https://github.com/XingmoStudio/ClassDock";

    /// <summary>主页 Banner 的在线图源（返回图片流，比例不固定）。</summary>
    private const string SpotlightUrl = "https://api.qzink.me/spotlight?orientation=landscape";

    /// <summary>Banner 诊断日志：追加写入 data\logs\homebanner.log。</summary>
    private static readonly string HomeBannerLogPath = Path.Combine(AppPaths.LogsDir, "homebanner.log");

    private bool _homeBannerRequested;   // Banner 只请求一次

    private static void LogHomeBanner(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HomeBannerLogPath)!);
            File.AppendAllText(HomeBannerLogPath,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志写入失败不影响界面
        }
    }

    /// <summary>填充问候语、版本行与复选框状态。</summary>
    private void LoadHomeSettings()
    {
        _homeGreeting = $"你好，{Environment.UserName}";
        // 占位块始终是完整文本，保证打字过程不改变布局尺寸
        HomeGreetingMeasure.Text = _homeGreeting;
        HomeGreetingText.Text = _homeGreeting;
        HomeVersionText.Text = $"欢迎使用 {AppInfo.Version} 版本";

        _loadingSettings = true;
        HomeDefaultPageCheck.IsChecked = AppSettings.OpenHomeByDefault;
        _loadingSettings = false;

        if (HomePage.Visibility == Visibility.Visible) StartGreetingTyping();
    }

    /// <summary>进入主页时播放打字机：先停掉上一次，避免重复进入叠加计时器。</summary>
    private void StartGreetingTyping()
    {
        _greetingTimer.Stop();
        if (string.IsNullOrEmpty(_homeGreeting)) return;

        if (AppSettings.AnimationLevel == AnimationLevel.None)
        {
            HomeGreetingText.Text = _homeGreeting;
            return;
        }

        _greetingIndex = 0;
        HomeGreetingText.Text = string.Empty;
        _greetingTimer.Interval = TimeSpan.FromMilliseconds(40);
        _greetingTimer.Start();
    }

    /// <summary>停止打字机；离开主页或窗口关闭时调用，避免对已隐藏的页面继续回调。</summary>
    private void StopGreetingTyping() => _greetingTimer.Stop();

    private void OnGreetingTick(object? sender, object e)
    {
        if (_greetingIndex >= _homeGreeting.Length)
        {
            _greetingTimer.Stop();
            return;
        }

        _greetingIndex++;
        HomeGreetingText.Text = _homeGreeting[.._greetingIndex];
        if (_greetingIndex >= _homeGreeting.Length) _greetingTimer.Stop();
    }

    /// <summary>Banner 底部渐变：从透明过渡到当前主题的页面底色，避免硬边。</summary>
    private void UpdateHomeBannerFade()
    {
        // 直接从根元素的主题底色取色，深浅主题切换后重新生成
        var pageColor = (Root.Background as SolidColorBrush)?.Color ?? Colors.Transparent;
        var gradient = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1)
        };
        gradient.GradientStops.Add(new GradientStop { Offset = 0, Color = Colors.Transparent });
        gradient.GradientStops.Add(new GradientStop { Offset = 1, Color = pageColor });
        HomeBannerFade.Fill = gradient;
    }

    /// <summary>首次进入主页时异步拉取 Banner；失败时保留下层本地 Assets/Banner.png。</summary>
    private void EnsureHomeBannerLoaded()
    {
        if (_homeBannerRequested)
        {
            LogHomeBanner("enter home: banner request already made, skip");
            return;
        }

        _homeBannerRequested = true;
        LogHomeBanner($"enter home: start banner request, url={SpotlightUrl}");
        _ = LoadHomeBannerAsync();
    }

    /// <summary>在线 Banner：先取响应头记录状态，正文给 60 秒预算（该 CDN 下完 2MB 实测 13~27 秒），失败保留本地回退图。</summary>
    private async Task LoadHomeBannerAsync()
    {
        int statusCode = 0;
        string? mediaType = null;
        Uri? finalUri = null;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            LogHomeBanner("request: sending GET");
            var started = Stopwatch.StartNew();
            using var response = await client.GetAsync(
                SpotlightUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token);

            // 记录取证信息：API 会 302 到 res.public.onecdn.static.microsoft，且图片常被标成 text/plain
            statusCode = (int)response.StatusCode;
            finalUri = response.RequestMessage?.RequestUri;
            mediaType = response.Content.Headers.ContentType?.MediaType;
            LogHomeBanner($"response: status={statusCode} mime={mediaType ?? "(none)"} " +
                          $"contentLength={response.Content.Headers.ContentLength} finalUri={finalUri} " +
                          $"headersIn={started.ElapsedMilliseconds}ms");
            response.EnsureSuccessStatusCode();

            var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
            LogHomeBanner($"body: {bytes.Length} bytes in {started.ElapsedMilliseconds}ms");
            using var memory = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(memory.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            memory.Seek(0);

            // 只有真正解码成功才替换；非图片数据会在这里抛异常并走回退
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(memory);
            LogHomeBanner($"decode: OK, size={bitmap.PixelWidth}x{bitmap.PixelHeight}, total={started.ElapsedMilliseconds}ms");
            HomeBannerRemote.Source = bitmap;
            LogHomeBanner($"assign: HomeBannerRemote.Source set, opacity={HomeBannerRemote.Opacity}, " +
                          $"actual={HomeBannerRemote.ActualWidth}x{HomeBannerRemote.ActualHeight}");

            // BitmapImage 已在本方法内解码完成，直接触发淡入；不依赖 ImageOpened（该事件可能不触发）
            BeginHomeBannerFade();
        }
        catch (Exception ex)
        {
            // 网络异常 / 超时 / 非图片：保留下层本地回退图，并把失败原因写进日志
            LogHomeBanner($"FAILED: status={statusCode} mime={mediaType ?? "(none)"} uri={finalUri}{Environment.NewLine}{ex}");
        }
    }

    /// <summary>本地回退图加载完成：确认它自己是否真的能显示。</summary>
    private void OnHomeBannerFallbackOpened(object sender, RoutedEventArgs e)
    {
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", "Banner.png");
        LogHomeBanner($"fallback: ImageOpened, actual={HomeBannerFallback.ActualWidth}x{HomeBannerFallback.ActualHeight}, " +
                      $"fileExists={File.Exists(file)}");
    }

    private void OnHomeBannerFallbackFailed(object sender, ExceptionRoutedEventArgs e)
    {
        LogHomeBanner($"fallback: ImageFailed, error={e.ErrorMessage}");
    }

    /// <summary>Banner 区域布局尺寸：用于判断“图没显示”是否属于布局/被遮挡问题。</summary>
    private void OnHomeBannerHostSizeChanged(object sender, SizeChangedEventArgs e)
    {
        LogHomeBanner($"layout: host={e.NewSize.Width}x{e.NewSize.Height}, " +
                      $"fallback={HomeBannerFallback.ActualWidth}x{HomeBannerFallback.ActualHeight}, " +
                      $"remote={HomeBannerRemote.ActualWidth}x{HomeBannerRemote.ActualHeight}, " +
                      $"homeVisible={HomePage.Visibility}");
    }

    /// <summary>在线 Banner 解码完成后淡入，覆盖本地回退图（以解码完成为准，ImageOpened 仅作补充）。</summary>
    private void OnHomeBannerImageOpened(object sender, RoutedEventArgs e)
    {
        LogHomeBanner("remote: ImageOpened");
        BeginHomeBannerFade();
    }

    private void OnHomeBannerRemoteFailed(object sender, ExceptionRoutedEventArgs e)
    {
        LogHomeBanner($"remote: ImageFailed, error={e.ErrorMessage}");
    }

    private bool _homeBannerFaded;

    /// <summary>淡入只执行一次；动画级别为「无」时直接显示。</summary>
    private void BeginHomeBannerFade()
    {
        if (_homeBannerFaded) return;

        var image = HomeBannerRemote;
        LogHomeBanner($"fade begin: opacityBefore={image.Opacity}, " +
                      $"actual={image.ActualWidth}x{image.ActualHeight}, animLevel={AppSettings.AnimationLevel}");

        if (AppSettings.AnimationLevel == AnimationLevel.None)
        {
            _homeBannerFaded = true;
            image.Opacity = 1;
            LogHomeBanner("fade: instant (animation disabled)");
            return;
        }

        var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = image.Opacity,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(450),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
            }
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, image);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "Opacity");

        var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        storyboard.Children.Add(animation);
        storyboard.Completed += (_, _) =>
        {
            image.Opacity = 1;
            LogHomeBanner("fade: completed, opacity=1");
        };

        try
        {
            _homeBannerFaded = true;
            storyboard.Begin();
            LogHomeBanner("fade: storyboard started");
        }
        catch (Exception ex)
        {
            image.Opacity = 1;
            LogHomeBanner($"fade: storyboard FAILED, forced opacity=1: {ex.Message}");
        }
    }

    /// <summary>STAR 按钮：交给系统默认浏览器打开仓库地址，不在应用内嵌网页。</summary>
    private async void OnHomeStarClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri(RepositoryUrl));
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("无法打开浏览器", ex.Message);
        }
    }

    /// <summary>功能卡：整卡点击跳到对应设置页，并同步侧边栏选中态。</summary>
    private void OnHomeFeatureCardClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        switch (button.Tag as string)
        {
            case "appearance":
                _generalExpanded = true;
                ApplyGroupState();
                ShowPage("appearance");
                SelectNav(_collapsed ? NavGeneral : NavAppearance,
                    _collapsed ? GeneralAccent : FindAccentBorder(NavAppearance));
                break;

            case "storage":
                _generalExpanded = true;
                ApplyGroupState();
                ShowPage("storage");
                SelectNav(_collapsed ? NavGeneral : NavStorage,
                    _collapsed ? GeneralAccent : FindAccentBorder(NavStorage));
                break;

            case "annotation":
                _toolsExpanded = true;
                ApplyGroupState();
                ShowPage("annotation");
                SelectNav(_collapsed ? NavTools : NavAnnotation,
                    _collapsed ? ToolsAccent : FindAccentBorder(NavAnnotation));
                break;
        }
    }

    /// <summary>“打开设置时默认打开此页”：持久化到 settings.json，下次打开设置窗口时生效。</summary>
    private void OnHomeDefaultPageChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;
        AppSettings.OpenHomeByDefault = HomeDefaultPageCheck.IsChecked == true;
        AppSettings.Save();
    }

    // ── 第三方库滑出面板 ──

    private bool _librariesOpen;        // 面板是否正在显示

    /// <summary>点击侧边栏“第三方库”：滑出面板。</summary>
    private void OnLibrariesClick(object sender, RoutedEventArgs e)
    {
        if (_librariesOpen) return;
        _librariesOpen = true;

        LibrariesPanel.Visibility = Visibility.Visible;
        LibrariesBackdrop.Visibility = Visibility.Visible;

        AnimatePanelX(from: 320, to: 0, ms: 220, completed: null);
    }

    /// <summary>点击面板右上角关闭或遮罩：收起面板。</summary>
    private void CloseLibrariesPanel()
    {
        if (!_librariesOpen) return;
        _librariesOpen = false;

        AnimatePanelX(from: 0, to: 320, ms: 200, completed: () =>
        {
            LibrariesPanel.Visibility = Visibility.Collapsed;
            LibrariesBackdrop.Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>第三方库面板水平位移动画（收起时 EaseIn、展开时 EaseOut）。</summary>
    private void AnimatePanelX(double from, double to, double ms, Action? completed)
    {
        if (AppSettings.AnimationLevel == AnimationLevel.None)
        {
            LibrariesPanelTransform.X = to;
            completed?.Invoke();
            return;
        }

        var anim = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(ms),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = to > from
                    ? Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseIn
                    : Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut
            }
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(anim, LibrariesPanelTransform);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(anim, "X");

        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        sb.Children.Add(anim);
        if (completed is not null)
        {
            sb.Completed += (_, _) => completed();
        }
        sb.Begin();
    }

    private void OnLibrariesCloseClick(object sender, RoutedEventArgs e) => CloseLibrariesPanel();

    private void OnLibrariesBackdropTapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        => CloseLibrariesPanel();
}
