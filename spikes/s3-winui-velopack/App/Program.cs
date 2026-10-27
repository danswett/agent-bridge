using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace AgentBridgeS3;

public static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);

        // Must run first: Velopack invokes the installed exe with hook arguments on
        // install, update and uninstall, and Run() handles those and exits.
        VelopackApp.Build()
            .OnBeforeUninstallFastCallback(_ => Toasts.Uninstall())
            .Run();

        Toasts.Start();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App();
        });
    }
}
