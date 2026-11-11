using System.Text;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ClassDock;

/// <summary>日志级别：信息 / 警告 / 错误 / 其他。</summary>
public enum LogLevel
{
    Info,
    Warning,
    Error,
    Other
}

/// <summary>
/// 应用统一日志：按级别写入 data\logs\app.log，供日志查看器解析并分类着色展示。
/// 行格式：[yyyy-MM-dd HH:mm:ss.fff] [LEVEL] message
/// </summary>
public static class AppLogger
{
    private static readonly object SyncRoot = new();
    private static string? _logPath;

    /// <summary>日志文件路径（data\logs\app.log）。</summary>
    public static string LogPath =>
        _logPath ??= Path.Combine(AppPaths.LogsDir, "app.log");

    public static void Info(string message) => Write(LogLevel.Info, message);

    public static void Warn(string message) => Write(LogLevel.Warning, message);

    public static void Error(string message) => Write(LogLevel.Error, message);

    public static void Other(string message) => Write(LogLevel.Other, message);

    /// <summary>写入一条异常日志（级别为 Error，附带完整堆栈）。</summary>
    public static void Exception(Exception? ex)
    {
        if (ex is null) return;
        Write(LogLevel.Error, ex.ToString());
    }

    /// <summary>原样写入一行指定级别的日志。</summary>
    public static void Write(LogLevel level, string message)
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level.ToString().ToUpperInvariant()}] {message}";
            lock (SyncRoot)
            {
                Directory.CreateDirectory(AppPaths.LogsDir);
                File.AppendAllText(LogPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志写入失败不影响主流程
        }
    }

    /// <summary>按级别读取全部日志条目（供日志查看器使用）。</summary>
    public static IReadOnlyList<LogEntry> ReadAll()
    {
        var entries = new List<LogEntry>();
        try
        {
            if (!File.Exists(LogPath)) return entries;
            foreach (var raw in File.ReadAllLines(LogPath, Encoding.UTF8))
            {
                if (LogEntry.TryParse(raw, out var entry)) entries.Add(entry);
                else if (!string.IsNullOrWhiteSpace(raw)) entries.Add(new LogEntry(LogLevel.Other, raw));
            }
        }
        catch
        {
            // 读取失败时返回已读到的内容
        }
        return entries;
    }
}

/// <summary>一条解析后的日志条目。</summary>
public sealed class LogEntry
{
    private static readonly SolidColorBrush WarningBrush = new(Color.FromArgb(0xFF, 0xE6, 0xA2, 0x3C));
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromArgb(0xFF, 0xE8, 0x11, 0x23));
    private static readonly SolidColorBrush OtherBrush = new(Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E));

    public LogLevel Level { get; }
    public string Timestamp { get; }
    public string Message { get; }

    /// <summary>该级别对应的颜色画刷（用于日志查看器着色）。</summary>
    public Brush LevelBrush => Level switch
    {
        LogLevel.Info => AppTheme.AccentBrush,
        LogLevel.Warning => WarningBrush,
        LogLevel.Error => ErrorBrush,
        _ => OtherBrush
    };

    public LogEntry(LogLevel level, string timestamp, string message)
    {
        Level = level;
        Timestamp = timestamp;
        Message = message;
    }

    /// <summary>整行无法解析时使用：整条消息归为「其他」。</summary>
    public LogEntry(LogLevel level, string message)
        : this(level, string.Empty, message)
    {
    }

    /// <summary>解析 [时间] [级别] 消息；不匹配时返回 false。</summary>
    public static bool TryParse(string line, out LogEntry entry)
    {
        entry = null!;
        if (string.IsNullOrWhiteSpace(line)) return false;

        // [yyyy-MM-dd HH:mm:ss.fff] [LEVEL] message
        var tsEnd = line.IndexOf(']');
        if (tsEnd < 2 || line[0] != '[') return false;
        var timestamp = line.Substring(1, tsEnd - 1).Trim();

        var rest = line.Substring(tsEnd + 1).TrimStart();
        if (rest.Length == 0 || rest[0] != '[') return false;
        var levelEnd = rest.IndexOf(']');
        if (levelEnd < 1) return false;
        var levelText = rest.Substring(1, levelEnd - 1).Trim();

        var message = rest.Substring(levelEnd + 1).TrimStart();
        var level = levelText.ToUpperInvariant() switch
        {
            "INFO" => LogLevel.Info,
            "WARNING" => LogLevel.Warning,
            "ERROR" => LogLevel.Error,
            _ => LogLevel.Other
        };

        entry = new LogEntry(level, timestamp, message);
        return true;
    }
}
