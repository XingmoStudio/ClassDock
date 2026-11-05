// ── Program ──
// 非打包应用的自定义入口；替代 XAML 自动生成的 Main，手动初始化 WinRT 并启动 App。

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClassDock;

/// <summary>应用入口。非打包（unpackaged）WinUI 3 应用必须自行提供 Main。</summary>
public static class Program
{
    /// <summary>初始化 COM Wrappers、设置同步上下文并启动 XAML Application。</summary>
    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        Application.Start((ApplicationInitializationCallbackParams _) =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });

        return 0;
    }
}
