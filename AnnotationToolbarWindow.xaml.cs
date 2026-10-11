using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace ClassDock;

/// <summary>
/// 屏幕批注竖排工具栏：置顶、无边框白色卡片，靠屏幕右侧垂直居中。
/// 与全屏批注画布分离，便于后续让画布点击穿透而工具栏保持可交互。
/// 本阶段只负责界面与选中态，不接实际批注能力。
/// </summary>
public sealed partial class AnnotationToolbarWindow : Window
{
    private const double WidthDip = 56;
    private const double HeightDip = 247;
    private const double RightMarginDip = 12;

    private static readonly SolidColorBrush AccentBrush
        = new(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4));
    private static readonly SolidColorBrush NormalBrush
        = new(Color.FromArgb(0xFF, 0x2B, 0x2B, 0x2B));

    public IntPtr Hwnd { get; }

    /// <summary>点击“返回主面板”时触发。</summary>
    public event Action? BackRequested;

    public AnnotationToolbarWindow()
    {
        InitializeComponent();
        Hwnd = WindowNative.GetWindowHandle(this);
        DwmGlass.Enable(Hwnd);

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

        // 默认选中画笔
        PenButton.Foreground = AccentBrush;

        Card.Loaded += (_, _) => PlayEnter();
    }

    /// <summary>移动到屏幕右侧垂直居中并显示（物理像素 + DPI 缩放）。</summary>
    public void ShowAtRight(int screenWpx, int screenHpx, double scale)
    {
        int wpx = (int)Math.Round(WidthDip * scale);
        int hpx = (int)Math.Round(HeightDip * scale);
        int marginPx = (int)Math.Round(RightMarginDip * scale);
        int x = screenWpx - marginPx - wpx;
        int y = (screenHpx - hpx) / 2;

        AppWindow.MoveAndResize(new RectInt32(x, y, wpx, hpx));
        Activate();
    }

    // ── 进出场：自上而下的渐变 ──

    private void PlayEnter()
    {
        ElementCompositionPreview.SetIsTranslationAvailable(Card, true);
        var visual = ElementCompositionPreview.GetElementVisual(Card);
        var comp = visual.Compositor;
        var ease = comp.CreateCubicBezierEasingFunction(
            new Vector2(0.16f, 1f), new Vector2(0.3f, 1f));

        visual.Opacity = 0f;
        visual.Translation = new Vector3(0f, -14f, 0f);

        var fade = comp.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(220);
        visual.StartAnimation(nameof(Visual.Opacity), fade);

        var slide = comp.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, new Vector3(0f, -14f, 0f));
        slide.InsertKeyFrame(1f, new Vector3(0f, 0f, 0f), ease);
        slide.Duration = TimeSpan.FromMilliseconds(220);
        visual.StartAnimation(nameof(Visual.Translation), slide);
    }

    /// <summary>向上淡出，完成后执行 onDone（由调用方关闭窗口）。</summary>
    public void PlayExit(Action onDone)
    {
        var visual = ElementCompositionPreview.GetElementVisual(Card);
        var comp = visual.Compositor;

        var fade = comp.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, visual.Opacity);
        fade.InsertKeyFrame(1f, 0f);
        fade.Duration = TimeSpan.FromMilliseconds(130);
        fade.StopBehavior = AnimationStopBehavior.SetToFinalValue;
        visual.StartAnimation(nameof(Visual.Opacity), fade);

        var slide = comp.CreateVector3KeyFrameAnimation();
        slide.InsertKeyFrame(0f, visual.Translation);
        slide.InsertKeyFrame(1f, new Vector3(0f, -8f, 0f));
        slide.Duration = TimeSpan.FromMilliseconds(130);
        slide.StopBehavior = AnimationStopBehavior.SetToFinalValue;
        visual.StartAnimation(nameof(Visual.Translation), slide);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(140) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            onDone();
        };
        timer.Start();
    }

    // ── 工具按钮：单选，选中态用主题蓝 ──

    private void OnToolClick(object sender, RoutedEventArgs e)
    {
        var clicked = (ToggleButton)sender;
        var tools = new[] { PenButton, ShapesButton, EraserButton, RulerButton };

        foreach (var btn in tools)
        {
            bool active = ReferenceEquals(btn, clicked);
            btn.IsChecked = active;
            btn.Foreground = active ? AccentBrush : NormalBrush;
        }
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
        => BackRequested?.Invoke();
}
