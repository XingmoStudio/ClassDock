using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Windows.Graphics;
using Windows.UI.Core;
using WinRT.Interop;

namespace ClassDock;

/// <summary>
/// 全屏批注画布窗口：置顶、无边框、无标题栏，承载 InkCanvas。
/// 与工具栏窗口分离——后续“穿透模式”只穿透本窗口，工具栏保持可交互。
/// </summary>
public sealed partial class AnnotationCanvasWindow : Window
{
    public IntPtr Hwnd { get; }

    public AnnotationCanvasWindow()
    {
        InitializeComponent();
        Hwnd = WindowNative.GetWindowHandle(this);

        // 注意：不能用 DwmGlass.Enable（会把 DWM 背景策略强制为 NONE，
        // 与 DesktopAcrylicController 冲突），由 SystemBackdrop 提供材质。
        SystemBackdrop = new AnnotationBackdrop();

        var presenter = AppWindow.Presenter as OverlappedPresenter;
        presenter?.SetBorderAndTitleBar(false, false);
        if (presenter is not null)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsMinimizable = false;
            presenter.IsMaximizable = false;
            presenter.IsResizable = false;
        }
        AppWindow.IsShownInSwitchers = false;

        // 鼠标 / 触摸 / 手写笔全部作为墨迹输入（压感由 InkPresenter 保留）
        InkLayer.InkPresenter.InputDeviceTypes =
            CoreInputDeviceTypes.Mouse | CoreInputDeviceTypes.Touch | CoreInputDeviceTypes.Pen;

        Root.Loaded += (_, _) => PlayEnter();
    }

    /// <summary>铺满主屏（物理像素）并显示；不激活，避免打断前台内容。</summary>
    public void ShowFullscreen(int screenWpx, int screenHpx)
    {
        AppWindow.MoveAndResize(new RectInt32(0, 0, screenWpx, screenHpx));
        AppWindow.Show(activateWindow: false);
    }

    // ── 进出场：整体淡入淡出 ──

    private void PlayEnter()
    {
        var visual = ElementCompositionPreview.GetElementVisual(Root);
        var comp = visual.Compositor;
        visual.Opacity = 0;

        var anim = comp.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, 0f);
        anim.InsertKeyFrame(1f, 1f);
        anim.Duration = TimeSpan.FromMilliseconds(220);
        visual.StartAnimation(nameof(Visual.Opacity), anim);
    }

    /// <summary>淡出后执行 onDone（由调用方关闭窗口）。</summary>
    public void PlayExit(Action onDone)
    {
        var visual = ElementCompositionPreview.GetElementVisual(Root);
        var comp = visual.Compositor;

        var anim = comp.CreateScalarKeyFrameAnimation();
        anim.InsertKeyFrame(0f, visual.Opacity);
        anim.InsertKeyFrame(1f, 0f);
        anim.Duration = TimeSpan.FromMilliseconds(140);
        anim.StopBehavior = AnimationStopBehavior.SetToFinalValue;

        var completed = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        completed.Tick += (_, _) =>
        {
            completed.Stop();
            onDone();
        };
        completed.Start();

        visual.StartAnimation(nameof(Visual.Opacity), anim);
    }
}
