using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using Windows.UI;
using Windows.Storage.Pickers;

namespace ClassDock;

/// <summary>日志查看器：按 信息 / 警告 / 错误 / 其他 分类着色展示 app.log。</summary>
public sealed partial class LogViewerWindow : Window
{
    private readonly ObservableCollection<LogEntry> _entries = new();
    private string _currentFilter = "All";

    public LogViewerWindow()
    {
        InitializeComponent();
        App.ApplyWindowIcon(AppWindow);

        AppTheme.Apply(Root);
        Closed += (_, _) => AppTheme.Unapply(Root);

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        ApplyTitleBarColors();

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0) scale = 1;
        int width = (int)(720 * scale), height = (int)(560 * scale);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));
        var workArea = DisplayArea.Primary.WorkArea;
        AppWindow.Move(new Windows.Graphics.PointInt32(
            workArea.X + (workArea.Width - width) / 2,
            workArea.Y + (workArea.Height - height) / 2));

        LogList.ItemsSource = _entries;
        Reload();
        UpdateFilterHighlight();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

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
        tb.ButtonInactiveForegroundColor = dark ? Color.FromArgb(128, 255, 255, 255) : Color.FromArgb(128, 0, 0, 0);
        tb.ButtonHoverBackgroundColor = dark ? Color.FromArgb(26, 255, 255, 255) : Color.FromArgb(26, 0, 0, 0);
        tb.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        tb.ButtonPressedBackgroundColor = dark ? Color.FromArgb(51, 255, 255, 255) : Color.FromArgb(13, 0, 0, 0);
        tb.ButtonPressedForegroundColor = dark ? Colors.White : Colors.Black;
    }

    /// <summary>重新读取日志文件并按当前筛选条件渲染。</summary>
    private void Reload()
    {
        var all = AppLogger.ReadAll();
        _entries.Clear();
        int filtered = 0;
        foreach (var e in all)
        {
            if (MatchesFilter(e)) { _entries.Add(e); filtered++; }
        }
        CountText.Text = $"共 {all.Count} 条，当前显示 {filtered} 条";
    }

    private bool MatchesFilter(LogEntry e) => _currentFilter switch
    {
        "Info" => e.Level == LogLevel.Info,
        "Warning" => e.Level == LogLevel.Warning,
        "Error" => e.Level == LogLevel.Error,
        "Other" => e.Level == LogLevel.Other,
        _ => true
    };

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _currentFilter = tag;
            UpdateFilterHighlight();
            Reload();
        }
    }

    /// <summary>高亮当前筛选按钮。</summary>
    private void UpdateFilterHighlight()
    {
        SetFilterStyle(FilterAll, _currentFilter == "All");
        SetFilterStyle(FilterInfo, _currentFilter == "Info");
        SetFilterStyle(FilterWarn, _currentFilter == "Warning");
        SetFilterStyle(FilterError, _currentFilter == "Error");
        SetFilterStyle(FilterOther, _currentFilter == "Other");
    }

    private static void SetFilterStyle(Button btn, bool active)
    {
        if (active)
        {
            btn.Background = AppTheme.AccentBrush;
            btn.Foreground = AppTheme.OnAccentBrush;
        }
        else
        {
            // 必须用 ClearValue 清除本地值，赋值 null 只会设置一个 null 画刷（文字透明）。
            btn.ClearValue(Control.BackgroundProperty);
            btn.ClearValue(Control.ForegroundProperty);
        }
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) => Reload();

    /// <summary>导出日志：把 app.log 另存到用户指定位置。</summary>
    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = $"ClassDock-{DateTime.Now:yyyyMMdd-HHmmss}.log"
            };
            picker.FileTypeChoices.Add("日志文件", new List<string> { ".log", ".txt" });
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            if (file is null) return;

            if (File.Exists(AppLogger.LogPath))
                await File.ReadAllBytesAsync(AppLogger.LogPath).ContinueWith(t =>
                    File.WriteAllBytes(file.Path, t.Result));
            else
                await File.WriteAllTextAsync(file.Path, string.Empty);

            await ShowMessageAsync("导出完成", $"日志已保存到：\n{file.Path}");
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"导出日志失败：{ex.Message}");
            await ShowMessageAsync("导出失败", ex.Message);
        }
    }

    /// <summary>清空日志：需要确认后截断 app.log。</summary>
    private async void OnClearClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = AppTheme.CurrentElementTheme,
            Title = "清空日志？",
            Content = "清空后无法恢复，确认继续？",
            PrimaryButtonText = "清空",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            if (File.Exists(AppLogger.LogPath)) File.WriteAllText(AppLogger.LogPath, string.Empty);
            Reload();
        }
        catch (Exception ex)
        {
            await ShowMessageAsync("清空失败", ex.Message);
        }
    }

    private async Task ShowMessageAsync(string title, string content)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            RequestedTheme = AppTheme.CurrentElementTheme,
            Title = title,
            Content = new TextBlock { Text = content, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "好"
        };
        await dialog.ShowAsync();
    }
}
