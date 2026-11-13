using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ClassDock;

/// <summary>调试事件描述信息。</summary>
public sealed class DebugEventInfo
{
    /// <summary>事件唯一名称（触发时输入）。</summary>
    public string Name { get; }

    /// <summary>分类标签（如 app / log / config / theme）。</summary>
    public string Category { get; }

    /// <summary>人类可读的说明。</summary>
    public string Description { get; }

    /// <summary>是否为高风险操作（退出、重启、重置配置等）。</summary>
    public bool IsDangerous { get; }

    /// <summary>事件处理函数；返回的消息会展示给用户。</summary>
    public Func<CancellationToken, Task<string>> Handler { get; }

    public DebugEventInfo(
        string name,
        string category,
        string description,
        Func<CancellationToken, Task<string>> handler,
        bool isDangerous = false)
    {
        Name = name;
        Category = category;
        Description = description;
        Handler = handler;
        IsDangerous = isDangerous;
    }
}

/// <summary>
/// 调试事件注册表：把"事件名称"映射到可执行函数。
/// 调试页允许用户按名称触发这些函数，用于复现与排障。
/// </summary>
public static class DebugEvents
{
    private static readonly Dictionary<string, DebugEventInfo> _registry = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>已注册事件的只读快照。</summary>
    public static IReadOnlyList<DebugEventInfo> All { get; private set; } = Array.Empty<DebugEventInfo>();

    static DebugEvents()
    {
        RegisterBuiltins();
        RebuildSnapshot();
    }

    /// <summary>注册一个调试事件。名称大小写不敏感。</summary>
    public static void Register(DebugEventInfo info)
    {
        if (info is null) throw new ArgumentNullException(nameof(info));
        _registry[info.Name] = info;
        RebuildSnapshot();
    }

    /// <summary>按名称查找事件（大小写不敏感）。</summary>
    public static bool TryGet(string name, out DebugEventInfo info)
        => _registry.TryGetValue(name?.Trim() ?? string.Empty, out info!);

    /// <summary>触发指定事件，返回处理结果文本。</summary>
    public static async Task<string> InvokeAsync(string name, CancellationToken cancellationToken = default)
    {
        if (!TryGet(name, out var info))
            throw new KeyNotFoundException($"未找到事件：{name}");
        return await info.Handler(cancellationToken).ConfigureAwait(false);
    }

    private static void RebuildSnapshot()
    {
        All = new ReadOnlyCollection<DebugEventInfo>(
            _registry.Values
                .OrderBy(e => e.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    // ── 内置事件 ──────────────────────────────────────────────

    private static void RegisterBuiltins()
    {
        // —— 应用 ——
        Register(new DebugEventInfo(
            "app.exit", "app", "退出 ClassDock。",
            _ => { Environment.Exit(0); return Task.FromResult("已退出应用。"); },
            isDangerous: true));

        Register(new DebugEventInfo(
            "app.restart", "app", "重启 ClassDock。",
            _ => { App.RequestRestart(); return Task.FromResult("正在重启应用…"); },
            isDangerous: true));

        Register(new DebugEventInfo(
            "app.throw", "app", "抛出一个未处理异常，用于验证崩溃处理逻辑。",
            _ => throw new InvalidOperationException("由调试事件 app.throw 主动抛出的测试异常。")));

        Register(new DebugEventInfo(
            "app.gc", "app", "强制执行一次垃圾回收。",
            _ =>
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                return Task.FromResult($"已执行 GC，当前托管堆大小约 {GC.GetTotalMemory(false) / 1024} KB。");
            }));

        // —— 日志 ——
        Register(new DebugEventInfo(
            "log.info", "log", "写入一条信息级别的测试日志。",
            _ => { AppLogger.Info("由调试事件 log.info 写入的测试日志。"); return Task.FromResult("已写入信息日志。"); }));

        Register(new DebugEventInfo(
            "log.warn", "log", "写入一条警告级别的测试日志。",
            _ => { AppLogger.Warn("由调试事件 log.warn 写入的测试日志。"); return Task.FromResult("已写入警告日志。"); }));

        Register(new DebugEventInfo(
            "log.error", "log", "写入一条错误级别的测试日志。",
            _ => { AppLogger.Error("由调试事件 log.error 写入的测试日志。"); return Task.FromResult("已写入错误日志。"); }));

        Register(new DebugEventInfo(
            "log.other", "log", "写入一条「其他」级别的测试日志。",
            _ => { AppLogger.Other("由调试事件 log.other 写入的测试日志。"); return Task.FromResult("已写入其他日志。"); }));

        // —— 配置 ——
        Register(new DebugEventInfo(
            "config.reload", "config", "从磁盘重新加载配置文件，覆盖当前内存设置。",
            _ => { AppSettings.Reload(); return Task.FromResult("已重新加载配置。"); }));

        Register(new DebugEventInfo(
            "config.save", "config", "立即把当前设置写入配置文件。",
            _ => { AppSettings.Save(); return Task.FromResult("已保存配置。"); }));

        Register(new DebugEventInfo(
            "config.reset", "config", "删除配置文件并重启，所有设置恢复默认值（不可撤销）。",
            _ =>
            {
                try { System.IO.File.Delete(AppSettings.SettingsFilePath); }
                catch { /* 文件不存在或占用时仍继续重启 */ }
                App.RequestRestart();
                return Task.FromResult("正在重置配置并重启…");
            },
            isDangerous: true));

        // —— 主题 ——
        Register(new DebugEventInfo(
            "theme.apply", "theme", "重新应用当前主题设置到所有窗口。",
            _ =>
            {
                AppTheme.Reapply();
                return Task.FromResult("已重新应用主题。");
            }));
    }
}
