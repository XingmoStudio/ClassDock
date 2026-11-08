namespace ClassDock.Launcher;

/// <summary>
/// 启动画面预留层：后续要加启动动画/进度时，只需替换这里的实现，
/// Program 中的 Show/Close 调用点保持不变。
/// </summary>
internal static class StartupSplash
{
    public static void Show()
    {
        // 当前不显示任何界面；未来的动画窗口在此创建
    }

    public static void Close()
    {
        // 主程序退出后关闭动画窗口
    }
}
