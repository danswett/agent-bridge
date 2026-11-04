using AgentBridge.App.Core;
using AgentBridge.Host.Core.Usage;
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
        // uninstall takes away everything the app put outside it. Never a release downloaded
        // earlier applied at start (Velopack's default): it would skip the release halt list read
        // just before installing (#580, HaltListUpdater) and the idle rules; AutoUpdater installs it.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .OnBeforeUpdateFastCallback(_ => Lifecycle.BeforeUpdate())
            .OnAfterUpdateFastCallback(_ => Lifecycle.AfterUpdate())
            .OnBeforeUninstallFastCallback(_ => Lifecycle.BeforeUninstall())
            .Run();

        var options = AppOptions.Parse(args);
        if (options.SelfTest is not null) { SelfTestTrace.Start(); }

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
        // Said in the self-test's log: on CI, a run whose result showed no failure once ended with
        // exit code 1, and this tells the app's own code from what happened after it.
        if (options.SelfTest is not null) { AppLog.Write($"self-test: the app ends with exit code {App.ExitCode}"); }
        return App.ExitCode;
    }
}

// For the self-test only, in the app's log: every exception thrown, caught or not, and the code
// the process ends with. On CI a run once ended with exit code 1 a moment after high contrast was
// turned off, with no crash log and before Main returned; this says what was thrown before it.
static class SelfTestTrace
{
    const int Most = 400;
    static int written;
    [ThreadStatic] static bool writing;

    public static void Start()
    {
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (writing || Interlocked.Increment(ref written) > Most) { return; }
            writing = true;
            try { AppLog.Write($"self-test: thrown on thread {Environment.CurrentManagedThreadId}: {e.Exception.GetType().FullName}: {e.Exception.Message}"); }
            finally { writing = false; }
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => AppLog.Write($"self-test: the process exits with code {Environment.ExitCode}");
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

    // Everything the app put outside its folder: owned hooks, the host stopped, its logon task,
    // the app's Run entry and toast registration. The host's profile (key, account, config) stays,
    // like any app's data, so reinstalling picks up where it was; docs/app-windows.md.
    public static void BeforeUninstall()
    {
        if (Refused("uninstall")) { return; }
        var manager = Manager();
        // Velopack terminates this callback after 30 seconds; reserve time for registration cleanup.
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var stopped = false;
        try { manager.StopAsync(stop.Token).GetAwaiter().GetResult(); stopped = true; }
        catch (Exception e) { AppLog.Write($"uninstall: the host did not stop; hooks were left alone: {e.Message}"); }
        if (stopped)
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { AppLog.Write($"uninstall: {manager.RemoveOwnedHooksAsync(new CommandRunner(), cleanup.Token).GetAwaiter().GetResult()}"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or TimeoutException or OperationCanceledException)
            {
                AppLog.Write($"uninstall: hooks were not fully disconnected: {e.Message}");
            }
        }
        try { manager.SetStartAtSignIn(false); } catch (Exception e) { AppLog.Write($"uninstall: the logon task stays: {e.Message}"); }
        try { new RunKey(Paths.Exe).Set(false); } catch (Exception e) { AppLog.Write($"uninstall: the Run entry stays: {e.Message}"); }
        try { ToastActivator.Unregister(); } catch (Exception e) { AppLog.Write($"uninstall: the toast registration stays: {e.Message}"); }
    }
}
