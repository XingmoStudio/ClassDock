using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace ClassDock;

/// <summary>
/// 批注画布的背景：Thin Acrylic（Win11 任务栏式的极淡云母材质）。
/// 界面阶段使用——既能看到桌面内容、又与普通透明窗口区分；
/// 功能阶段将替换为可穿透的真透明层。
/// 官方 DesktopAcrylicController 实现，自带调度器握手，不会出现
/// 游离 Compositor 导致的崩溃。
/// </summary>
internal sealed class AnnotationBackdrop : SystemBackdrop
{
    private DesktopAcrylicController? _controller;

    protected override void OnTargetConnected(
        ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);

        DispatcherQueueHelper.EnsureOnCurrentThread();

        _controller = new DesktopAcrylicController();
        if (DesktopAcrylicController.IsKindSupported(DesktopAcrylicKind.Thin))
        {
            _controller.Kind = DesktopAcrylicKind.Thin;
        }
        _controller.AddSystemBackdropTarget(connectedTarget);
    }

    protected override void OnTargetDisconnected(
        ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);

        if (_controller is not null)
        {
            _controller.RemoveSystemBackdropTarget(disconnectedTarget);
            _controller.Dispose();
            _controller = null;
        }
    }

    /// <summary>非打包模式下 DesktopAcrylicController 需要当前线程存在 DispatcherQueue。</summary>
    private static class DispatcherQueueHelper
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct DispatcherQueueOptions
        {
            public int DwSize;
            public int ThreadType;
            public int ApartmentType;
        }

        [DllImport("CoreMessaging.dll")]
        private static extern int CreateDispatcherQueueController(
            in DispatcherQueueOptions options, out IntPtr dispatcherQueueController);

        private static bool _initialized;

        public static void EnsureOnCurrentThread()
        {
            if (_initialized) return;
            _initialized = true;

            var options = new DispatcherQueueOptions
            {
                DwSize = Marshal.SizeOf<DispatcherQueueOptions>(),
                ThreadType = 2,    // DQTYPE_THREAD_CURRENT
                ApartmentType = 2  // DQTAT_COM_STA
            };
            CreateDispatcherQueueController(in options, out _);
        }
    }
}
