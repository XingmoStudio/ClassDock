using System.Diagnostics;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("windows")]

namespace ClassDock.Launcher;

/// <summary>
/// 根目录启动器：只负责挑选最新的 App-{版本号} 目录并拉起 ClassDock.Desktop.exe，
/// 自身不加载 WinUI、不常驻（主程序退出后立即结束，并原样返回其退出码）。
/// </summary>
internal static class Program
{
    private const string DesktopExeName = "ClassDock.Desktop.exe";
    private const string VersionDirPrefix = "App-";

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            string root = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
            string? appDir = FindLatestAppDirectory(root);
            if (appDir is null)
            {
                NativeDialog.ShowError(
                    "未找到可用的程序目录。\r\n\r\n" +
                    $"请确认 ClassDock.exe 与 {VersionDirPrefix}<版本号> 目录位于同一文件夹，" +
                    $"且该目录内存在 {DesktopExeName}。");
                return 2;
            }

            var startInfo = new ProcessStartInfo(Path.Combine(appDir, DesktopExeName))
            {
                // 工作目录必须切到版本目录，主程序才能找到同级的运行库与资源
                WorkingDirectory = appDir,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args) startInfo.ArgumentList.Add(arg);

            StartupSplash.Show();
            try
            {
                using var process = Process.Start(startInfo);
                if (process is null)
                {
                    NativeDialog.ShowError($"无法启动主程序：\r\n{startInfo.FileName}");
                    return 3;
                }

                // 等待主程序结束，把它的退出码当作启动器的退出码
                process.WaitForExit();
                return process.ExitCode;
            }
            finally
            {
                StartupSplash.Close();
            }
        }
        catch (Exception ex)
        {
            NativeDialog.ShowError($"启动失败：\r\n{ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// 在根目录下查找 App-* 目录，按版本号数值比较（0.1.10 &gt; 0.1.9），
    /// 取最新且确实包含主程序的那一个。
    /// </summary>
    private static string? FindLatestAppDirectory(string root)
    {
        try
        {
            return Directory.EnumerateDirectories(root, VersionDirPrefix + "*", SearchOption.TopDirectoryOnly)
                .Select(dir => (Dir: dir, Version: ParseVersion(Path.GetFileName(dir))))
                .Where(item => item.Version is not null
                               && File.Exists(Path.Combine(item.Dir, DesktopExeName)))
                .OrderByDescending(item => item.Version)
                .Select(item => item.Dir)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从目录名解析版本号；无法解析（如带预览后缀）的目录直接忽略。</summary>
    private static Version? ParseVersion(string directoryName) =>
        directoryName.StartsWith(VersionDirPrefix, StringComparison.OrdinalIgnoreCase)
        && Version.TryParse(directoryName[VersionDirPrefix.Length..], out var version)
            ? version
            : null;
}
