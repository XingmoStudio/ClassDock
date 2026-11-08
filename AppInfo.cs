using System.Reflection;

namespace ClassDock;

/// <summary>
/// 应用身份信息。版本号的唯一源头是 Directory.Build.props 的 &lt;Version&gt;，
/// 这里只从程序集元数据读取，界面、目录名与发布包名都由它派生，不再写死版本字符串。
/// </summary>
public static class AppInfo
{
    /// <summary>版本代号，与版本号一起显示在标题栏和关于页。</summary>
    public const string Codename = "PaintingTheWorld";

    /// <summary>
    /// 显示用版本号：取 AssemblyInformationalVersion，与 &lt;Version&gt; 完全一致，
    /// 3 段（0.1.0）/ 4 段（0.0.7.9）原样保留，不补也不裁尾部的 .0。
    /// SourceLink 可能追加的 +提交号会被去掉，避免目录名与界面不一致。
    /// </summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>标题栏 / 关于页的完整文案，如 0.0.7.9 (Codename PaintingTheWorld)。</summary>
    public static string VersionWithCodename => $"{Version} (Codename {Codename})";

    private static string ResolveVersion()
    {
        var assembly = typeof(AppInfo).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            int cut = informational.IndexOf('+');
            return cut > 0 ? informational[..cut] : informational;
        }

        // 兜底：没有 InformationalVersion 时用程序集版本；Windows 版本号固定 4 段，
        // 修订号为 0 时还原成 3 段，避免显示 0.1.0.0。
        var version = assembly.GetName().Version;
        if (version is null) return "未知";
        return version.Revision > 0 ? version.ToString() : version.ToString(3);
    }
}
