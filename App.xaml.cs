using Microsoft.UI.Xaml;

namespace ClassDock;

public partial class App : Application
{
    private Window? _barWindow;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // 入口窗口：右缘小白条（面板由它按需创建与显示）
        _barWindow = new BarWindow();
        _barWindow.Activate();
    }
}
