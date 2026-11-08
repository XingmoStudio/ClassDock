namespace ClassDock;

/// <summary>
/// 统一的路径解析入口。目录布局以程序主目录（App-{版本号} 的父级）为根：
///   &lt;根&gt;\ClassDock.exe          启动器
///   &lt;根&gt;\data\logs\            日志
///   &lt;根&gt;\data\Backup\          备份
///   &lt;根&gt;\data\Config.json       配置
///   &lt;根&gt;\data\                  其他附加资源
///   &lt;根&gt;\App-{版本号}\          主程序、运行库与布局资源
/// 程序安装位置不可写时，data 整体回退到 %LOCALAPPDATA%\ClassDock，保证启动不失败。
/// </summary>
internal static class AppPaths
{
    private static readonly string LocalDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClassDock");

    static AppPaths() => InitializeCore();

    /// <summary>程序主目录：主程序运行在 App-{版本号} 内时取其父级，否则为主程序所在目录。</summary>
    public static string Root { get; } = ResolveRoot(AppContext.BaseDirectory);

    /// <summary>根目录启动器路径；存在时开机自启等外部入口应指向它。</summary>
    public static string LauncherPath => Path.Combine(Root, "ClassDock.exe");

    /// <summary>data 目录；可写时位于程序目录下，否则回退到本机用户数据目录。</summary>
    public static string DataDir { get; private set; } = LocalDataDir;

    /// <summary>data 是否成功落在程序目录（false 表示因只读安装位置而回退）。</summary>
    public static bool UsesProgramDirectory { get; private set; }

    public static string LogsDir => Path.Combine(DataDir, "logs");

    public static string BackupDir => Path.Combine(DataDir, "Backup");

    public static string ConfigFile => Path.Combine(DataDir, "Config.json");

    /// <summary>旧版配置位置：%LOCALAPPDATA%\ClassDock\settings.json。</summary>
    public static string LegacyConfigFile { get; } = Path.Combine(LocalDataDir, "settings.json");

    /// <summary>程序版本号：与界面显示同源，见 <see cref="AppInfo.Version"/>。</summary>
    public static string Version => AppInfo.Version;

    /// <summary>版本目录：预留存放布局、主题等非运行库资源。</summary>
    public static string AppVersionDir => Path.Combine(Root, $"App-{Version}");

    /// <summary>重复调用无副作用；AppSettings 与 App 启动阶段都会调用。</summary>
    public static void Initialize() => InitializeCore();

    private static void InitializeCore()
    {
        var portable = Path.Combine(Root, "data");
        if (TryPrepare(portable))
        {
            DataDir = portable;
            UsesProgramDirectory = true;
        }
        else
        {
            // 程序装在只读位置：数据回退到 %LOCALAPPDATA%，避免写配置直接崩溃
            DataDir = LocalDataDir;
            UsesProgramDirectory = false;
            _ = TryPrepare(DataDir);
        }

        MigrateLegacyConfig();
    }

    /// <summary>建目录并做一次真实写入探测；任一步失败即视为该位置不可用。</summary>
    private static bool TryPrepare(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            Directory.CreateDirectory(Path.Combine(dir, "logs"));
            Directory.CreateDirectory(Path.Combine(dir, "Backup"));

            var probe = Path.Combine(dir, $".write-probe-{Environment.ProcessId}.tmp");
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>一次性迁移：新配置不存在且旧配置存在时，把 settings.json 搬到 Config.json。</summary>
    private static void MigrateLegacyConfig()
    {
        try
        {
            if (File.Exists(ConfigFile) || !File.Exists(LegacyConfigFile)) return;

            try
            {
                File.Move(LegacyConfigFile, ConfigFile);
            }
            catch
            {
                // 跨卷或目标被占用时改为复制后删除
                File.Copy(LegacyConfigFile, ConfigFile);
                try
                {
                    File.Delete(LegacyConfigFile);
                }
                catch
                {
                    // 旧文件删不掉就留着，不影响新配置生效
                }
            }
        }
        catch
        {
            // 迁移失败时按默认配置启动，不阻塞程序
        }
    }

    /// <summary>主程序位于 App-{版本号} 内时，根目录取其父级；否则就是主程序所在目录。</summary>
    private static string ResolveRoot(string baseDirectory)
    {
        var dir = Path.TrimEndingDirectorySeparator(baseDirectory);
        var name = Path.GetFileName(dir);
        if (name.StartsWith("App-", StringComparison.OrdinalIgnoreCase)
            && Path.GetDirectoryName(dir) is { Length: > 0 } parent)
        {
            return parent;
        }
        return dir;
    }
}
