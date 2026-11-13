using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
// ── App ──
// 应用入口、设置持久化与崩溃处理策略。

using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.UI.ViewManagement;

namespace ClassDock;

/// <summary>崩溃发生时的处理方式（设置 → 通用 → 基本）。</summary>
public enum CrashBehavior
{
    SilentExit,
    ShowError,
    SilentRestart,
    /// <summary>不提示也不退出，完全忽略错误（风险极高）。</summary>
    Ignore
}

/// <summary>界面动画级别。</summary>
public enum AnimationLevel
{
    Standard,
    None
}

/// <summary>批注交互模式。</summary>
public enum AnnotationInteractionMode
{
    /// <summary>桌面交互模式：鼠标/触控笔绘制，手指触控不落笔，避免误触。</summary>
    Desktop,
    /// <summary>屏幕触控模式：手指触控也可以直接绘制（适合触摸大屏）。</summary>
    Touch
}

/// <summary>批注采样率：决定笔迹采样点之间的最小间距。</summary>
public enum AnnotationSampleRate
{
    High,
    Default,
    Low
}

/// <summary>应用级设置的持久化，存于程序目录下的 data\Config.json（不可写时回退到用户数据目录）。</summary>
internal static class AppSettings
{
    /// <summary>配置文件路径（供备份/重置功能使用）。</summary>
    public static string SettingsFilePath => AppPaths.ConfigFile;

    /// <summary>默认同调试版本旧的弹窗行为。</summary>
    public static CrashBehavior CrashBehavior { get; set; } = CrashBehavior.ShowError;

    /// <summary>是否允许多开实例；开启后重复启动不再弹出提示。</summary>
    public static bool AllowMultiInstance { get; set; }

    /// <summary>应用明暗主题：跟随系统 / 明亮 / 黑暗。</summary>
    public static AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;

    /// <summary>强调色来源：系统 / 自定义。</summary>
    public static AccentSourceMode AccentSource { get; set; } = AccentSourceMode.System;

    /// <summary>自定义强调色（ARGB 整数）；仅在强调色来源为自定义时生效。</summary>
    public static int CustomAccentArgb { get; set; } = unchecked((int)0xFF0078D4);

    /// <summary>界面动画级别。</summary>
    public static AnimationLevel AnimationLevel { get; set; } = AnimationLevel.Standard;

    /// <summary>批注交互模式。</summary>
    public static AnnotationInteractionMode InteractionMode { get; set; } = AnnotationInteractionMode.Desktop;

    /// <summary>是否记住退出前的画笔颜色。</summary>
    public static bool RememberPenColor { get; set; } = true;

    /// <summary>上次使用的画笔颜色（ARGB 整数）。</summary>
    public static int LastPenColorArgb { get; set; } = unchecked((int)0xFFE81123);

    /// <summary>批注采样率。</summary>
    public static AnnotationSampleRate SampleRate { get; set; } = AnnotationSampleRate.Default;

    /// <summary>多指绘图（实验）：多根手指可同时各画一条笔迹。</summary>
    public static bool MultiTouchDrawing { get; set; }

    /// <summary>抬笔时笔锋修正：软笔收笔时做平滑与出锋。</summary>
    public static bool PenTaperCorrection { get; set; } = true;

    /// <summary>笔速橡皮（实验）：移动越快橡皮半径越大（有上下限）。</summary>
    public static bool SpeedEraser { get; set; }

    /// <summary>测试与调试页是否已解锁（版本号连点 7 次并确认风险后置为 true）。</summary>
    public static bool DebugUnlocked { get; set; }

    /// <summary>测试与调试页是否启用；关闭后侧边栏不再显示，需通过版本号连点重新启用。</summary>
    public static bool DebugEnabled { get; set; } = true;

    /// <summary>打开设置窗口时是否默认落在主页。</summary>
    public static bool OpenHomeByDefault { get; set; }

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "ClassDock";

    /// <summary>开机自启状态以注册表为准（不写入 Config.json）。</summary>
    public static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>写入/移除 HKCU Run 项实现开机自启。</summary>
    public static void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null) return;
            if (enabled)
            {
                // 优先指向根目录启动器：升级到新的 App-{版本号} 后自启路径依然有效
                var exe = File.Exists(AppPaths.LauncherPath) ? AppPaths.LauncherPath : Environment.ProcessPath;
                if (exe is null) return;
                key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // 无权限或被策略拦截时静默失败，开关状态以注册表实际值为准
        }
    }

    static AppSettings()
    {
        // 先解析目录并完成旧配置迁移，再读取
        AppPaths.Initialize();
        Load();
    }

    /// <summary>从磁盘重新读取配置文件，覆盖当前内存中的设置。仅供调试/测试触发使用。</summary>
    public static void Reload() => Load();

    private static void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.ConfigFile)) return;
            var model = JsonSerializer.Deserialize<Model>(File.ReadAllText(AppPaths.ConfigFile));
            if (model is not null)
            {
                CrashBehavior = model.CrashBehavior;
                AllowMultiInstance = model.AllowMultiInstance;
                ThemeMode = Enum.IsDefined(model.ThemeMode) ? model.ThemeMode : AppThemeMode.System;
                AccentSource = Enum.IsDefined(model.AccentSource) ? model.AccentSource : AccentSourceMode.System;
                // 强制不透明，避免历史/手改数据带来透明强调色
                CustomAccentArgb = unchecked(model.CustomAccentArgb | (int)0xFF000000);
                AnimationLevel = Enum.IsDefined(model.AnimationLevel) ? model.AnimationLevel : AnimationLevel.Standard;
                InteractionMode = Enum.IsDefined(model.InteractionMode) ? model.InteractionMode : AnnotationInteractionMode.Desktop;
                RememberPenColor = model.RememberPenColor;
                LastPenColorArgb = unchecked(model.LastPenColorArgb | (int)0xFF000000);
                SampleRate = Enum.IsDefined(model.SampleRate) ? model.SampleRate : AnnotationSampleRate.Default;
                MultiTouchDrawing = model.MultiTouchDrawing;
                PenTaperCorrection = model.PenTaperCorrection;
                SpeedEraser = model.SpeedEraser;
                DebugUnlocked = model.DebugUnlocked;
                DebugEnabled = model.DebugEnabled;
                OpenHomeByDefault = model.OpenHomeByDefault;
            }
        }
        catch
        {
            // 配置损坏时回落到默认值，不阻塞启动
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.ConfigFile)!);
            File.WriteAllText(
                AppPaths.ConfigFile,
                JsonSerializer.Serialize(new Model
                {
                    CrashBehavior = CrashBehavior,
                    AllowMultiInstance = AllowMultiInstance,
                    ThemeMode = ThemeMode,
                    AccentSource = AccentSource,
                    CustomAccentArgb = CustomAccentArgb,
                    AnimationLevel = AnimationLevel,
                    InteractionMode = InteractionMode,
                    RememberPenColor = RememberPenColor,
                    LastPenColorArgb = LastPenColorArgb,
                    SampleRate = SampleRate,
                    MultiTouchDrawing = MultiTouchDrawing,
                    PenTaperCorrection = PenTaperCorrection,
                    SpeedEraser = SpeedEraser,
                    DebugUnlocked = DebugUnlocked,
                    DebugEnabled = DebugEnabled,
                    OpenHomeByDefault = OpenHomeByDefault
                }));
        }
        catch
        {
            // 保存失败不影响当前会话
        }
    }

    private sealed class Model
    {
        public CrashBehavior CrashBehavior { get; set; } = CrashBehavior.ShowError;
        public bool AllowMultiInstance { get; set; }
        public AppThemeMode ThemeMode { get; set; } = AppThemeMode.System;
        public AccentSourceMode AccentSource { get; set; } = AccentSourceMode.System;
        public int CustomAccentArgb { get; set; } = unchecked((int)0xFF0078D4);
        public AnimationLevel AnimationLevel { get; set; } = AnimationLevel.Standard;
        public AnnotationInteractionMode InteractionMode { get; set; } = AnnotationInteractionMode.Desktop;
        public bool RememberPenColor { get; set; } = true;
        public int LastPenColorArgb { get; set; } = unchecked((int)0xFFE81123);
        public AnnotationSampleRate SampleRate { get; set; } = AnnotationSampleRate.Default;
        public bool MultiTouchDrawing { get; set; }
        public bool PenTaperCorrection { get; set; } = true;
        public bool SpeedEraser { get; set; }
        public bool DebugUnlocked { get; set; }
        public bool DebugEnabled { get; set; } = true;
        public bool OpenHomeByDefault { get; set; }
    }
}

public partial class App : Application
{
    /// <summary>静默重启时传给新进程的参数，用于识别“刚因崩溃重启过”。</summary>
    private const string RestartArgument = "--crash-restart";

    private Window? _barWindow;
    private bool _errorDialogOpen;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private readonly bool _cameFromCrashRestart;

    /// <summary>初始化应用，注册全局未处理异常回调。</summary>
    public App()
    {
        InitializeComponent();

        _cameFromCrashRestart = Environment.GetCommandLineArgs().Contains(RestartArgument);
        UnhandledException += OnUnhandledException;
    }

    /// <summary>全局未处理异常回调：落盘并根据设置决定退出、重启或忽略。</summary>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        LogError(e.Exception);

        if (AppSettings.CrashBehavior == CrashBehavior.Ignore)
        {
            // 不处理：不提示也不退出，仅静默落盘
            return;
        }

        if (AppSettings.CrashBehavior == CrashBehavior.SilentRestart)
        {
            Restart();
            return;
        }

        if (AppSettings.CrashBehavior == CrashBehavior.SilentExit)
        {
            Environment.Exit(1);
            return;
        }

        ShowErrorDialog(e.Exception);
    }

    /// <summary>异常落盘，便于事后与自动化复现时读取；同时写入统一分类日志供查看器展示。</summary>
    internal static void LogError(Exception? ex)
    {
        if (ex is null) return;
        AppLogger.Exception(ex);
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            File.AppendAllText(
                Path.Combine(AppPaths.LogsDir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch
        {
            // 记录失败不影响后续处理
        }
    }

    /// <summary>静默重启：启动阶段再次崩溃时直接退出，避免无限重启。</summary>
    private void Restart()
    {
        if (_cameFromCrashRestart && (DateTime.UtcNow - _startedAt).TotalSeconds < 15)
        {
            Environment.Exit(1);
            return;
        }

        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    ArgumentList = { RestartArgument }
                });
            }
        }
        catch
        {
            // 拉不起来就按静默退出处理
        }

        Environment.Exit(1);
    }

    /// <summary>由调试事件主动触发的重启：拉起新实例后退出当前进程（不经过崩溃循环保护）。</summary>
    internal static void RequestRestart()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is not null)
            {
                Process.Start(new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    ArgumentList = { RestartArgument }
                });
            }
        }
        catch
        {
            // 拉不起来就直接退出
        }

        Environment.Exit(0);
    }

    /// <summary>资源文件完整路径。</summary>
    private static string AssetPath(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", name);

    /// <summary>将应用图标设置到指定窗口（标题栏 / 任务栏 / Alt+Tab）。</summary>
    internal static void ApplyWindowIcon(Microsoft.UI.Windowing.AppWindow appWindow)
    {
        try
        {
            var path = AssetPath("AppIcon.ico");
            if (File.Exists(path)) appWindow.SetIcon(path);
        }
        catch
        {
            // 图标设置失败不影响窗口功能
        }
    }

    /// <summary>崩溃报告窗口：整体 WinUI 3 自绘，颜色跟随应用主题；
    /// 仅提供「退出应用」「重启应用」「复制」三个选项，关闭窗口即退出进程。</summary>
    private void ShowErrorDialog(Exception? ex)
    {
        if (_errorDialogOpen) return;
        _errorDialogOpen = true;

        try
        {
            var detail = ex?.ToString() ?? "未知错误";

            var window = new Window { Title = "ClassDock 崩溃报告" };
            ApplyWindowIcon(window.AppWindow);

            // —— 主题画刷：颜色可变，切换主题时只改 Color 即可整体同步 ——
            var background = new SolidColorBrush();
            var surface = new SolidColorBrush();
            var edge = new SolidColorBrush();
            var textBrush = new SolidColorBrush();
            var secondaryText = new SolidColorBrush();

            // 强调色按钮：用实例级资源覆盖模板配色，悬停/按下时保持强调色系
            static void UseAccentPalette(Button button, SolidColorBrush fill, SolidColorBrush hoverFill,
                SolidColorBrush pressedFill, SolidColorBrush foreground)
            {
                var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                button.Resources["ButtonBackground"] = fill;
                button.Resources["ButtonBackgroundPointerOver"] = hoverFill;
                button.Resources["ButtonBackgroundPressed"] = pressedFill;
                button.Resources["ButtonBackgroundDisabled"] = fill;
                button.Resources["ButtonForeground"] = foreground;
                button.Resources["ButtonForegroundPointerOver"] = foreground;
                button.Resources["ButtonForegroundPressed"] = foreground;
                button.Resources["ButtonForegroundDisabled"] = foreground;
                button.Resources["ButtonBorderBrush"] = transparent;
                button.Resources["ButtonBorderBrushPointerOver"] = transparent;
                button.Resources["ButtonBorderBrushPressed"] = transparent;
            }

            var appIcon = new BitmapImage(new Uri(AssetPath("AppIcon.png")));

            // —— 标题栏：应用图标 + 窗口标题，右侧系统按键由主题配色 ——
            var titleBar = new Grid
            {
                Height = 40,
                Background = background,
                Children =
                {
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Spacing = 8,
                        Margin = new Thickness(14, 0, 0, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Children =
                        {
                            new Image
                            {
                                Width = 16,
                                Height = 16,
                                Source = appIcon
                            },
                            new TextBlock
                            {
                                Text = "ClassDock 崩溃报告",
                                FontSize = 12,
                                Foreground = secondaryText,
                                VerticalAlignment = VerticalAlignment.Center
                            }
                        }
                    }
                }
            };

            // —— 头部：图标 + 大标题 ——
            var header = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 14,
                Children =
                {
                    new Image
                    {
                        Width = 46,
                        Height = 46,
                        Source = appIcon
                    },
                    new TextBlock
                    {
                        Text = "ClassDock 发生错误w(ﾟДﾟ)w",
                        FontSize = 26,
                        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                        Foreground = textBrush,
                        VerticalAlignment = VerticalAlignment.Center,
                        TextWrapping = TextWrapping.Wrap
                    }
                }
            };

            var message = new TextBlock
            {
                Margin = new Thickness(0, 16, 0, 0),
                FontSize = 14,
                LineHeight = 22,
                Foreground = textBrush,
                TextWrapping = TextWrapping.Wrap,
                Text = "ClassDock碰到了严重错误从而无法正常运行。您可以保存下方的错误信息并向他人寻求帮助或进一步在日志中排查。如果您认为这是由软件本身的错误所致，请携带日志文件前往本项目仓库中提交 Issues 。"
            };

            // —— 错误信息区：可滚动、可选中 ——
            var errorText = new TextBlock
            {
                Text = detail,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12.5,
                Foreground = textBrush,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true
            };
            var errorBox = new Border
            {
                Margin = new Thickness(0, 16, 0, 0),
                Background = surface,
                BorderBrush = edge,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Child = new ScrollViewer
                {
                    Padding = new Thickness(14, 12, 14, 12),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = errorText
                }
            };

            // —— 底部操作：退出 / 重启 / 复制 ——
            var exitButton = new Button
            {
                Content = "退出应用",
                Background = AppTheme.AccentBrush,
                Foreground = AppTheme.OnAccentBrush,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(22, 8, 22, 8),
                MinWidth = 104,
                CornerRadius = new CornerRadius(6)
            };
            var restartButton = new Button
            {
                Content = "重启应用",
                Margin = new Thickness(10, 0, 0, 0),
                Background = AppTheme.AccentSoftBrush,
                Foreground = AppTheme.OnAccentBrush,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(22, 8, 22, 8),
                MinWidth = 104,
                CornerRadius = new CornerRadius(6)
            };
            var copyButton = new Button
            {
                Background = surface,
                BorderBrush = edge,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(18, 8, 18, 8),
                MinWidth = 96,
                CornerRadius = new CornerRadius(6),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    Children =
                    {
                        new FontIcon
                        {
                            Glyph = "\uE8C8",
                            FontSize = 14,
                            Foreground = textBrush
                        },
                        new TextBlock
                        {
                            Text = "复制",
                            Foreground = textBrush
                        }
                    }
                }
            };

            UseAccentPalette(exitButton, AppTheme.AccentBrush, AppTheme.AccentHoverBrush,
                AppTheme.AccentPressedBrush, AppTheme.OnAccentBrush);
            UseAccentPalette(restartButton, AppTheme.AccentSoftBrush, AppTheme.AccentHoverBrush,
                AppTheme.AccentPressedBrush, AppTheme.OnAccentBrush);

            var buttonBar = new Grid
            {
                Margin = new Thickness(28, 18, 28, 20)
            };
            buttonBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            buttonBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            buttonBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var leftButtons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Children = { exitButton, restartButton }
            };
            buttonBar.Children.Add(leftButtons);
            Grid.SetColumn(copyButton, 2);
            buttonBar.Children.Add(copyButton);

            // —— 主体内容 ——
            var content = new Grid { Margin = new Thickness(28, 8, 28, 0) };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.Children.Add(header);
            Grid.SetRow(message, 1);
            content.Children.Add(message);
            Grid.SetRow(errorBox, 2);
            content.Children.Add(errorBox);

            var root = new Grid { Background = background };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(titleBar);
            Grid.SetRow(content, 1);
            root.Children.Add(content);
            Grid.SetRow(buttonBar, 2);
            root.Children.Add(buttonBar);

            // —— 主题应用：明暗取自统一主题服务，强调色按钮使用共享画刷 ——
            void ApplyTheme()
            {
                bool dark = AppTheme.IsDark;
                background.Color = dark
                    ? Microsoft.UI.ColorHelper.FromArgb(255, 32, 32, 32)
                    : Microsoft.UI.ColorHelper.FromArgb(255, 243, 243, 243);
                surface.Color = dark
                    ? Microsoft.UI.ColorHelper.FromArgb(255, 43, 43, 43)
                    : Microsoft.UI.ColorHelper.FromArgb(255, 255, 255, 255);
                edge.Color = dark
                    ? Microsoft.UI.ColorHelper.FromArgb(0x26, 255, 255, 255)
                    : Microsoft.UI.ColorHelper.FromArgb(0x1A, 0, 0, 0);
                textBrush.Color = dark
                    ? Microsoft.UI.ColorHelper.FromArgb(255, 255, 255, 255)
                    : Microsoft.UI.ColorHelper.FromArgb(0xE4, 0, 0, 0);
                secondaryText.Color = dark
                    ? Microsoft.UI.ColorHelper.FromArgb(0x99, 255, 255, 255)
                    : Microsoft.UI.ColorHelper.FromArgb(0x73, 0, 0, 0);

                // 系统标题栏按键：透明底，前景随主题
                var titleBarApi = window.AppWindow.TitleBar;
                titleBarApi.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBarApi.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
                titleBarApi.ButtonForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
                titleBarApi.ButtonInactiveForegroundColor = dark
                    ? Microsoft.UI.ColorHelper.FromArgb(0x99, 255, 255, 255)
                    : Microsoft.UI.ColorHelper.FromArgb(0x99, 0, 0, 0);
                titleBarApi.ButtonHoverBackgroundColor = dark
                    ? Microsoft.UI.ColorHelper.FromArgb(0x1A, 255, 255, 255)
                    : Microsoft.UI.ColorHelper.FromArgb(0x0F, 0, 0, 0);
                titleBarApi.ButtonHoverForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
                titleBarApi.ButtonPressedBackgroundColor = dark
                    ? Microsoft.UI.ColorHelper.FromArgb(0x33, 255, 255, 255)
                    : Microsoft.UI.ColorHelper.FromArgb(0x14, 0, 0, 0);
                titleBarApi.ButtonPressedForegroundColor = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
            }

            window.Content = root;
            AppTheme.Apply(root);
            ApplyTheme();
            AppTheme.Changed += (_, _) => ApplyTheme();
            window.ExtendsContentIntoTitleBar = true;
            window.SetTitleBar(titleBar);

            const int w = 880, h = 620;
            window.AppWindow.Resize(new SizeInt32(w, h));
            var workArea = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
            window.AppWindow.Move(new PointInt32(
                workArea.X + (workArea.Width - w) / 2,
                workArea.Y + (workArea.Height - h) / 2));

            exitButton.Click += (_, _) => Environment.Exit(1);
            restartButton.Click += (_, _) => Restart();
            copyButton.Click += (_, _) =>
            {
                try
                {
                    var package = new DataPackage();
                    package.SetText(detail);
                    Clipboard.SetContent(package);
                }
                catch
                {
                    // 复制失败不影响弹窗其余操作
                }
            };
            // 关闭窗口同样视为退出，避免进程残留在后台
            window.Closed += (_, _) => Environment.Exit(1);

            window.Activate();
        }
        catch
        {
            // 弹窗失败也要保证进程终止
            Environment.Exit(1);
        }
    }

    /// <summary>单实例互斥体名称（当前会话内）。</summary>
    private const string SingleInstanceMutexName = "ClassDock.SingleInstance";

    private Mutex? _instanceMutex;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 目录解析最靠前：建 data 目录、完成旧配置迁移，后续读设置/写日志都依赖它
        AppPaths.Initialize();

        // 主题服务先于任何窗口初始化，保证首个窗口即拿到正确的明暗与强调色
        AppTheme.Initialize();

        AppLogger.Info("应用启动");

        // 已存在实例且未允许多开时：弹提示并结束本实例；
        // 崩溃静默重启路径跳过提示（旧进程随即退出，互斥体自然释放）
        bool acquired = TryAcquireSingleInstance();
        if (!acquired && !_cameFromCrashRestart && !AppSettings.AllowMultiInstance)
        {
            ShowAlreadyRunningDialog();
            return;
        }

        // 入口窗口：右缘小白条（面板由它按需创建与显示）
        _barWindow = new BarWindow();
        _barWindow.Activate();
    }

    /// <summary>尝试持有单实例互斥体；已存在实例时返回 false。互斥体在进程退出时自动释放。</summary>
    private bool TryAcquireSingleInstance()
    {
        try
        {
            var mutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
            if (createdNew)
            {
                _instanceMutex = mutex;
                return true;
            }
            mutex.Dispose();
            return false;
        }
        catch
        {
            // 互斥体异常时不阻塞启动
            return true;
        }
    }

    /// <summary>已存在运行实例时的提示（整体 WinUI 3 窗口）：提示语 + 可点击的「重启软件」选项
    /// （圆形刷新图标 + 蓝色标题 + 灰色说明，无颜色按钮），右下角仅有「我知道了」。</summary>
    private static void ShowAlreadyRunningDialog()
    {
        static SolidColorBrush Brush(byte a, byte r, byte g, byte b) =>
            new(Microsoft.UI.ColorHelper.FromArgb(a, r, g, b));

        try
        {
            var window = new Window { Title = "ClassDock 已在运行" };
            ApplyWindowIcon(window.AppWindow);

            // 「重启软件」选项：圆形刷新图标 + 蓝色加粗标题 + 灰色说明，整行可点击
            var circle = new Border
            {
                Width = 40,
                Height = 40,
                CornerRadius = new CornerRadius(20),
                BorderBrush = Brush(0x33, 0, 0, 0),
                BorderThickness = new Thickness(1.5),
                Child = new FontIcon
                {
                    Glyph = "\uE72C",
                    FontSize = 18,
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    Foreground = Brush(0xCC, 0, 0, 0)
                }
            };
            var restartText = new StackPanel
            {
                Margin = new Thickness(14, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "重启软件",
                        FontSize = 14,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = new SolidColorBrush(AppTheme.AccentColor)
                    },
                    new TextBlock
                    {
                        Text = "结束正在运行的 ClassDock 实例，然后重新启动软件。",
                        FontSize = 12,
                        Foreground = Brush(0x66, 0, 0, 0),
                        Margin = new Thickness(0, 2, 0, 0)
                    }
                }
            };
            var optionGrid = new Grid();
            optionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            optionGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            optionGrid.Children.Add(circle);
            Grid.SetColumn(restartText, 1);
            optionGrid.Children.Add(restartText);

            var restartButton = new Button
            {
                Margin = new Thickness(0, 28, 0, 0),
                Padding = new Thickness(8),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = Brush(0x00, 0, 0, 0),
                BorderThickness = new Thickness(0),
                Content = optionGrid
            };

            var okButton = new Button
            {
                Content = "我知道了",
                MinWidth = 120,
                Padding = new Thickness(24, 6, 24, 6),
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 24, 20)
            };

            var body = new StackPanel
            {
                Margin = new Thickness(28, 24, 28, 0),
                Children =
                {
                    new TextBlock
                    {
                        Text = "ClassDock 已经开启，请不要重复打开。",
                        FontSize = 14,
                        TextWrapping = TextWrapping.Wrap
                    },
                    restartButton
                }
            };

            var root = new Grid { Background = Brush(0xFF, 0xF3, 0xF3, 0xF3) };
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.Children.Add(body);
            Grid.SetRow(okButton, 1);
            root.Children.Add(okButton);
            window.Content = root;

            const int w = 600, h = 300;
            window.AppWindow.Resize(new SizeInt32(w, h));
            var workArea = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
            window.AppWindow.Move(new PointInt32(
                workArea.X + (workArea.Width - w) / 2,
                workArea.Y + (workArea.Height - h) / 2));
            if (window.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
            }

            // 统一收口：标题栏 X、重启项、「我知道了」都走这里，保证进程必定退出
            bool done = false;
            void Finish(bool restart)
            {
                if (done) return;
                done = true;
                if (restart) RestartExistingInstance();
                window.Close();
                Environment.Exit(0);
            }
            window.Closed += (_, _) => Finish(false);
            restartButton.Click += (_, _) => Finish(true);
            okButton.Click += (_, _) => Finish(false);

            window.Activate();
        }
        catch
        {
            // 弹窗失败时静默退出本实例
            Environment.Exit(0);
        }
    }

    /// <summary>结束正在运行的其他实例并启动一个新实例（用户在重复启动提示中主动选择重启）。</summary>
    private static void RestartExistingInstance()
    {
        try
        {
            var exe = Environment.ProcessPath;
            var current = Process.GetCurrentProcess();
            foreach (var p in Process.GetProcessesByName(current.ProcessName))
            {
                if (p.Id == current.Id) continue;
                try
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
                catch
                {
                    // 单个进程结束失败不阻塞其余流程
                }
            }
            if (exe is not null)
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
            }
        }
        catch
        {
            // 重启失败则直接退出
        }
    }
}
