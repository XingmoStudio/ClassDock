using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
// ── App ──
// 应用入口、设置持久化与崩溃处理策略。

using Windows.Graphics;

namespace ClassDock;

/// <summary>崩溃发生时的处理方式（设置 → 调试）。</summary>
public enum CrashBehavior
{
    SilentExit,
    ShowError,
    SilentRestart,
    /// <summary>不提示也不退出，完全忽略错误（风险极高）。</summary>
    Ignore
}

/// <summary>应用级设置的持久化，存于 %LOCALAPPDATA%\ClassDock\settings.json。</summary>
internal static class AppSettings
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassDock");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    /// <summary>默认同调试版本旧的弹窗行为。</summary>
    public static CrashBehavior CrashBehavior { get; set; } = CrashBehavior.ShowError;

    static AppSettings() => Load();

    private static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var model = JsonSerializer.Deserialize<Model>(File.ReadAllText(FilePath));
            if (model is not null) CrashBehavior = model.CrashBehavior;
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
            Directory.CreateDirectory(Dir);
            File.WriteAllText(
                FilePath,
                JsonSerializer.Serialize(new Model { CrashBehavior = CrashBehavior }));
        }
        catch
        {
            // 保存失败不影响当前会话
        }
    }

    private sealed class Model
    {
        public CrashBehavior CrashBehavior { get; set; } = CrashBehavior.ShowError;
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

    /// <summary>异常落盘，便于事后与自动化复现时读取。</summary>
    internal static void LogError(Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassDock");
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, "error.log"),
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

    /// <summary>用 WinUI 3 的 ContentDialog 展示异常详情；由独立小窗口提供 XamlRoot。弹窗关闭后终止进程。</summary>
    private void ShowErrorDialog(Exception? ex)
    {
        if (_errorDialogOpen) return;
        _errorDialogOpen = true;

        try
        {
            var window = new Window { Title = "ClassDock 运行错误" };
            var root = new Grid();
            window.Content = root;
            window.AppWindow.Resize(new SizeInt32(660, 480));
            window.Activate();

            root.Loaded += async (_, _) =>
            {
                var dialog = new ContentDialog
                {
                    XamlRoot = root.XamlRoot,
                    Title = "ClassDock 运行错误",
                    CloseButtonText = "确定",
                    DefaultButton = ContentDialogButton.Close,
                    Content = new ScrollViewer
                    {
                        MaxHeight = 340,
                        Content = new TextBlock
                        {
                            Text = ex?.ToString() ?? "未知错误",
                            TextWrapping = TextWrapping.Wrap,
                            IsTextSelectionEnabled = true,
                            FontFamily = new FontFamily("Consolas"),
                            FontSize = 12
                        }
                    }
                };

                await dialog.ShowAsync();
                window.Close();
                Environment.Exit(1);
            };
        }
        catch
        {
            // 弹窗失败也要保证进程终止
            Environment.Exit(1);
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 入口窗口：右缘小白条（面板由它按需创建与显示）
        _barWindow = new BarWindow();
        _barWindow.Activate();
    }
}
