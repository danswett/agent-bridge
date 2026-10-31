using AgentBridge.App.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Velopack;

namespace AgentBridge.App;

public static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashLog.Write(e.ExceptionObject as Exception);

        // First: Velopack starts the installed exe with hook arguments on install, update and
        // uninstall, and Run() handles those and exits. The host lives in the app's folder, so
        // it is stopped before an update replaces the folder and started by the new version;
        // uninstall takes away everything the app put outside it.
        VelopackApp.Build()
            .OnBeforeUpdateFastCallback(_ => Lifecycle.BeforeUpdate())
            .OnAfterUpdateFastCallback(_ => Lifecycle.AfterUpdate())
            .OnBeforeUninstallFastCallback(_ => Lifecycle.BeforeUninstall())
            .Run();

        var options = AppOptions.Parse(args);

        // One window per profile: a second start shows the first one's window and leaves.
        using var single = SingleInstance.TryClaim(options);
        if (single is null) { return 0; }

        // Only an installed app owns the AUMID and the toast activator; a development copy or
        // the demo registers nothing on the machine.
        var installed = !options.Demo && options.IsDefaultProfile && Lifecycle.Installed;
        if (installed)
        {
            _ = ToastActivator.SetCurrentProcessExplicitAppUserModelID(ToastActivator.Aumid);
            ToastActivator.Start();
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(_ =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            new App(options, single, installed);
        });
        ToastActivator.Stop();
        return App.ExitCode;
    }
}

// The app's single-instance lock, and the signal a second start sends to show the window.
sealed class SingleInstance : IDisposable
{
    readonly Mutex mutex;
    public EventWaitHandle Show { get; }

    SingleInstance(Mutex mutex, EventWaitHandle show)
    {
        this.mutex = mutex;
        Show = show;
    }

    public static SingleInstance? TryClaim(AppOptions options)
    {
        // The self-test runs beside anything, and claims nothing.
        var name = options.SelfTest is not null ? $"AgentBridgeApp.selftest.{Environment.ProcessId}" : options.Demo ? "AgentBridgeApp.demo" : $"AgentBridgeApp.{options.Profile}";
        var mutex = new Mutex(true, $@"Local\{name}", out var mine);
        var show = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{name}.show");
        if (mine) { return new SingleInstance(mutex, show); }
        if (!options.Background && !options.ToastActivated) { show.Set(); }
        show.Dispose();
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        try { mutex.ReleaseMutex(); } catch (ApplicationException) { }
        mutex.Dispose();
        Show.Dispose();
    }
}

// What installing, updating and uninstalling do beyond Velopack's own copying.
static class Lifecycle
{
    public static bool Installed => new VelopackUpdater(() => false, InlineDispatcher.Instance, "default").InstalledVersion is not null;

    static HostManager Manager() => App.ManagerFor("default");

    // Inside an agent session the hooks do nothing to the host (LifecycleGuard), and say so in
    // the app's log.
    static bool Refused(string what)
    {
        if (LifecycleGuard.Refusal() is not { } why) { return false; }
        AppLog.Write($"{what}: left the host alone: this is running {why}. Installing, updating and uninstalling are for a person.");
        return true;
    }

    // In the old version, before its folder is replaced: the host runs from that folder.
    public static void BeforeUpdate()
    {
        if (Refused("update")) { return; }
        try { Manager().StopAsync().Wait(TimeSpan.FromSeconds(25)); }
        catch (Exception e) { AppLog.Write($"update: the host did not stop: {e.Message}"); }
    }

    // In the new version. The app restarts after an update and starts the host itself; this is
    // for an update applied without a restart, so the host is not left stopped.
    public static void AfterUpdate()
    {
        if (Refused("update")) { return; }
        try
        {
            var manager = Manager();
            if (manager.StartsAtSignIn) { manager.StartAsync().Wait(TimeSpan.FromSeconds(25)); }
        }
        catch (Exception e) { AppLog.Write($"update: the host did not start: {e.Message}"); }
    }

    // Everything the app put outside its folder: the host stopped, its logon task, the app's
    // Run entry and the toast registration. The host's profile (key, account, config) stays,
    // like any app's data, so reinstalling picks up where it was; docs/app-windows.md.
    public static void BeforeUninstall()
    {
        if (Refused("uninstall")) { return; }
        var manager = Manager();
        try { manager.StopAsync().Wait(TimeSpan.FromSeconds(25)); } catch (Exception e) { AppLog.Write($"uninstall: the host did not stop: {e.Message}"); }
        try { manager.SetStartAtSignIn(false); } catch (Exception e) { AppLog.Write($"uninstall: the logon task stays: {e.Message}"); }
        try { new RunKey(Paths.Exe).Set(false); } catch (Exception e) { AppLog.Write($"uninstall: the Run entry stays: {e.Message}"); }
        try { ToastActivator.Unregister(); } catch (Exception e) { AppLog.Write($"uninstall: the toast registration stays: {e.Message}"); }
    }
}
