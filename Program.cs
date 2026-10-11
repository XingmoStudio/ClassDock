using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClassDock;

public static class Program
{
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
