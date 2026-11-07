using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using System.Runtime.InteropServices;
using Windows.UI;

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
            AppTheme.Changed -= OnAppThemeChanged;
            AppTheme.Unapply(Root);
        };

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

        // 默认选中“关于 ClassDock”
        SelectNav(NavAbout, AboutAccent);
        LoadCrashBehavior();
        AllowMultiInstanceSwitch.IsOn = AppSettings.AllowMultiInstance;
        LoadAppearanceSettings();
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
        if (_accentPicker is not null) _accentPicker.RequestedTheme = AppTheme.CurrentElementTheme;
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

    /// <summary>切换右侧内容页；未开放的页面保持空白。</summary>
    private void ShowPage(string tag)
    {
        AboutPage.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
        AppearancePage.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        DebugPage.Visibility = tag == "debug" ? Visibility.Visible : Visibility.Collapsed;
        BlankPage.Visibility = tag == "blank" ? Visibility.Visible : Visibility.Collapsed;
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
    {
        if (_collapsed || _sidebarBusy) return; // 收起态下不展开二级菜单
        _generalExpanded = !_generalExpanded;
        ApplyGroupState();
    }

    private void OnToolsGroupClick(object sender, RoutedEventArgs e)
    {
        if (_collapsed || _sidebarBusy) return;
        _toolsExpanded = !_toolsExpanded;
        ApplyGroupState();
    }

    /// <summary>子菜单可见性与分组箭头（无动画，供展开/收起分组时调用）。</summary>
    private void ApplyGroupState()
    {
        GeneralChildren.Visibility = !_collapsed && _generalExpanded ? Visibility.Visible : Visibility.Collapsed;
        ToolsChildren.Visibility = !_collapsed && _toolsExpanded ? Visibility.Visible : Visibility.Collapsed;
        GeneralChevron.Glyph = _generalExpanded ? "\uE70D" : "\uE70E";
        ToolsChevron.Glyph = _toolsExpanded ? "\uE70D" : "\uE70E";
    }

    /// <summary>汉堡键：收起/展开侧边栏，附带宽度渐变与文字淡入淡出动画。</summary>
    private async void OnCollapseToggleClick(object sender, RoutedEventArgs e)
    {
        if (_sidebarBusy) return;
        _sidebarBusy = true;
        _collapsed = !_collapsed;

        if (_collapsed)
        {
            // 先收起子菜单并淡出文字，再收缩宽度
            GeneralChildren.Visibility = Visibility.Collapsed;
            ToolsChildren.Visibility = Visibility.Collapsed;
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
        DebugIcon.Margin = iconMargin;

        var padding = collapsed ? NavPaddingCollapsed : NavPadding;
        NavAbout.Padding = padding;
        NavHome.Padding = padding;
        NavGeneral.Padding = padding;
        NavTools.Padding = padding;
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
        DebugText.Visibility = visibility;
        ExitText.Visibility = visibility;
        GeneralChevron.Visibility = visibility;
        ToolsChevron.Visibility = visibility;
    }

    private void SetTextOpacity(double opacity)
    {
        SettingsTitle.Opacity = opacity;
        AboutText.Opacity = opacity;
        HomeText.Opacity = opacity;
        GeneralText.Opacity = opacity;
        ToolsText.Opacity = opacity;
        DebugText.Opacity = opacity;
        ExitText.Opacity = opacity;
        GeneralChevron.Opacity = opacity;
        ToolsChevron.Opacity = opacity;
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
            DebugText, ExitText, GeneralChevron, ToolsChevron
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

    /// <summary>从 AppSettings 读取崩溃处理策略并同步到单选按钮。</summary>
    private void LoadCrashBehavior()
    {
        _loadingSettings = true;
        switch (AppSettings.CrashBehavior)
        {
            case CrashBehavior.SilentExit:
                CrashSilentExit.IsChecked = true;
                break;
            case CrashBehavior.SilentRestart:
                CrashRestart.IsChecked = true;
                break;
            case CrashBehavior.Ignore:
                CrashIgnore.IsChecked = true;
                break;
            default:
                CrashShowError.IsChecked = true;
                break;
        }
        _loadingSettings = false;
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

    private async void OnCrashBehaviorChanged(object sender, RoutedEventArgs e)
    {
        if (_loadingSettings) return;

        if (ReferenceEquals(sender, CrashIgnore))
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

            AppSettings.CrashBehavior = CrashBehavior.Ignore;
            AppSettings.Save();
            return;
        }

        AppSettings.CrashBehavior = ReferenceEquals(sender, CrashSilentExit) ? CrashBehavior.SilentExit
            : ReferenceEquals(sender, CrashRestart) ? CrashBehavior.SilentRestart
            : CrashBehavior.ShowError;
        AppSettings.Save();
    }

    /// <summary>主动抛出未处理异常，验证崩溃处理方式是否按预期生效。</summary>
    private void OnThrowTestErrorClick(object sender, RoutedEventArgs e)
    {
        var message = CrashTestInput.Text.Trim();
        if (message.Length == 0) message = "这是一条主动抛出的测试错误。";
        throw new InvalidOperationException(message);
    }

    private void OnNavExitClick(object sender, RoutedEventArgs e)
    {
        // 结束整个 ClassDock 进程（含白条、面板等所有窗口）
        Environment.Exit(0);
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
