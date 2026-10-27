using Microsoft.UI.Xaml;

namespace AgentBridgeS3;

public partial class App : Application
{
    MainWindow? window;

    public App()
    {
        // A XAML failure otherwise surfaces only as 0xC000027B in Microsoft.UI.Xaml.dll,
        // with the real exception lost; record it where the spike's other logs go.
        UnhandledException += (_, e) => CrashLog.Write(e.Exception);
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        window.SetLaunch(Toasts.LaunchedByToast ? "from a toast (cold start)" : "normally");
        var w = window;
        Toasts.Attach((how, a, input) => w.DispatcherQueue.TryEnqueue(() => w.ShowAnswer(how, a, input)));
        window.Activate();
    }
}
