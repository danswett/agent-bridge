using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AgentBridge.App;

public partial class App : Application
{
    public static int ExitCode { get; private set; }

    readonly AppOptions options;
    readonly SingleInstance single;
    readonly bool installed;
    MainWindow? window;
    TrayIcon? tray;
    DispatcherTimer? timer;
    DispatcherTimer? updateTimer;
    AutoUpdater? autoUpdater;
    UpdateReporter? updateReporter;
    HostConnection? connection;
    DemoHost? demo;
    DemoHost? remoteDemo;
    Fleet? fleet;
    HostManager? manager;
    MainViewModel? main;
    ToastResponder? responder;
    RegisteredWaitHandle? showWait;
    bool quitting;

    internal App(AppOptions options, SingleInstance single, bool installed)
    {
        this.options = options;
        this.single = single;
        this.installed = installed;
        UnhandledException += (_, e) =>
        {
            CrashLog.Write(e.Exception);
            if (options.SelfTest is not null) { ExitCode = 2; }
        };
        InitializeComponent();
    }

    internal static HostManager ManagerFor(string profile, string? dataRoot = null, ILogonTasks? tasks = null, IProcesses? processes = null) =>
        new(new InstallLayout(AppContext.BaseDirectory, dataRoot ?? Paths.DataRoot, profile), tasks ?? new WindowsLogonTasks(), processes ?? new SystemProcesses());

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var queue = DispatcherQueue.GetForCurrentThread();
        var ui = new QueueDispatcher(queue);
        // As the host reports its own: the package version, without the SDK's "+<commit>".
        var version = (typeof(App).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false).FirstOrDefault() as System.Reflection.AssemblyInformationalVersionAttribute)?.InformationalVersion.Split('+')[0] ?? "0.0.0";

        // The demo is a pretend host on a pipe of its own, with its files in a scratch folder:
        // it never reaches the real host, its profile or the machine's registrations.
        string appData;
        if (options.Demo)
        {
            demo = DemoHost.Start(version);
            appData = demo.Folder;
            manager = ManagerFor("demo", demo.Folder, new NoLogonTasks(), new NoProcesses());
            var endpoint = demo.Endpoint;
            connection = new HostConnection(ct => LocalClient.ConnectAsync(endpoint, TimeSpan.FromSeconds(2), ct), ui, AppLog.Write) { RetryDelay = TimeSpan.FromMilliseconds(500) };
        }
        else
        {
            appData = Paths.AppData;
            manager = ManagerFor(options.Profile);
            connection = HostConnection.ForProfile(manager.Layout.ProfileFolder, ui, AppLog.Write);
        }

        var prefsFile = Path.Combine(appData, $"{options.Profile}-settings.json");
        var prefs = AppPreferences.Load(prefsFile);
        AppPreferences Prefs() => prefs;
        void SavePrefs(AppPreferences next)
        {
            prefs = next;
            try { next.Save(prefsFile); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { AppLog.Write($"settings not saved: {e.Message}"); }
        }

        // One updater for Settings and the automatic updates alike, observed so that what either
        // does is what the account's other devices are told (#51).
        var updater = new ObservedUpdater(new VelopackUpdater(() => prefs.Prereleases, ui, options.Profile));
        IAppAtSignIn atSignIn = installed ? new RunKey(Paths.Exe) : new NoRunKey();
        var settings = new SettingsViewModel(connection, manager, updater, atSignIn, Prefs, SavePrefs, version, HostDefaults.Relay);

        if (installed)
        {
            FirstRun(Prefs, SavePrefs, atSignIn);
            try { ToastActivator.Register(Paths.Exe, Path.Combine(AppContext.BaseDirectory, "Assets", "AgentBridge.png")); }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException) { AppLog.Write($"toasts not registered: {e.Message}"); }
        }

        MainViewModel? self = null;
        var shrinker = new WinRtImageShrinker();
        IToastSink sink = installed ? new WindowsToastSink() : new NoToasts();

        // A machine's cards and toasts, whichever machine it is: everything they do goes to that
        // machine's own host.
        Machine Build(string key, HostConnection conn, FoldStore folds, Func<string?> readOnlyWhy, IBlobStore? blobs = null)
        {
            Machine? m = null;
            var cards = new CardContext
            {
                Deliveries = new Deliveries(conn, ui),
                Folds = folds,
                Capabilities = () => conn.Capabilities,
                MachineName = () => m?.Name,
                ShowMachine = () => self?.ShowMachines == true,
                ReadOnlyWhy = readOnlyWhy,
                ShowDetail = () => prefs.ShowDetail,
                Host = conn,
                Shrinker = shrinker,
                Blobs = blobs,
                BlobsFor = blobs is null ? null : key,
                FetchDetail = async id =>
                {
                    try { return await conn.CallAsync<Session>(Ops.SessionGet, new { id }, TimeSpan.FromSeconds(15)); }
                    catch (Exception e) when (e is IOException or HostErrorException or TimeoutException or JsonException) { return null; }
                },
            };
            var planner = new ToastPlanner(sink)
            {
                Enabled = () => prefs.Toasts && installed,
                FinishedEnabled = () => prefs.FinishedToasts,
                WindowActive = () => window?.IsInFront == true,
                Quiet = FocusAssist.Quiet,
                Capabilities = () => conn.Capabilities,
                MachineName = () => m?.Name,
                MachineKey = key == Machine.LocalKey ? null : key,
            };
            return m = new Machine(key, conn, cards, planner);
        }

        var localMachine = Build(Machine.LocalKey, connection, new FoldStore(Path.Combine(appData, $"{options.Profile}-folds.json")), () => self?.ReadOnlyWhy);
        main = self = new MainViewModel(localMachine, new UsageViewModel(), settings);
        settings.PreferencesChanged += () => { foreach (var c in main.AllCards) { c.CapabilitiesChanged(); } };
        responder = new ToastResponder(key => main.MachineOf(key)?.Deliveries, sink, (machine, id) => ShowWindow(machine, id));
        main.MachineRemoved += responder.MachineGone;

        // The account's other machines, through the relay as a device of the app's own (the
        // demo's is a second pretend host). Remote folds are not kept across restarts.
        IFleetLink? link = null;
        Machine Remote(FleetHost h) =>
            Build(h.Id, new HostConnection(ct => link!.OpenAsync(h.Id, ct), ui, AppLog.Write) { RetryDelay = TimeSpan.FromSeconds(options.Demo ? 0.5 : 5) }, new FoldStore(null), () => null, link!.Blobs);
        if (options.Demo)
        {
            remoteDemo = DemoHost.Start(version, remote: true);
            link = new DemoFleetLink(remoteDemo);
            fleet = new Fleet(link, main, ui, Remote);
            fleet.Start();
        }
        else { _ = StartFleetAsync(connection, ui, Remote, l => link = l); }
        var launch = new LaunchViewModel(main, ui);
        window = new MainWindow(main, launch, options.SelfTest is not null);
        window.CloseRequested += OnWindowClosing;
        connection.Start();

        if (options.SelfTest is not null)
        {
            window.Activate();
            _ = SelfTest.RunAsync(window, main, launch, options.SelfTest, demo!, remoteDemo!, code => { ExitCode = code; Quit(); });
            return;
        }

        tray = new TrayIcon(window.Handle, Path.Combine(AppContext.BaseDirectory, "Assets", "AgentBridge.ico"), () => ShowWindow(null, null), Quit);
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            main.Tick();
            tray?.SetTip(main.Waiting > 0 ? $"Agent Bridge: {main.Waiting} waiting on you" : "Agent Bridge");
        };
        timer.Start();

        // A toast click: open its session, or answer it without opening anything.
        ToastActivator.Attach((arguments, inputs) => queue.TryEnqueue(() => _ = responder.HandleAsync(arguments, inputs)));
        showWait = ThreadPool.RegisterWaitForSingleObject(single.Show, (_, _) => queue.TryEnqueue(() => ShowWindow(null, null)), null, Timeout.Infinite, executeOnlyOnce: false);

        if (!options.Background && !options.ToastActivated) { window.Activate(); }
        if (!options.Demo) { _ = StartHostAsync(); }

        // Updates without being asked (AutoUpdater): any installed copy of the real app, whatever
        // its profile, never the demo or a development copy. The first start after one says what
        // changed.
        if (!options.Demo && updater.InstalledVersion is not null)
        {
            AutoUpdater.Announce(Prefs, SavePrefs, updater, sink, AppLog.Write);
            autoUpdater = new AutoUpdater(updater, connection, manager, Prefs, SavePrefs, AppLog.Write, PackageProcesses.Others);
            updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            updateTimer.Tick += (_, _) => _ = autoUpdater.TickAsync();
            updateTimer.Start();
            _ = autoUpdater.TickAsync();
        }
        // What this machine's updater is doing, for the account's other devices to see, and their
        // presses of Check and Install (#51). A copy the updater cannot update says so.
        if (!options.Demo) { updateReporter = new UpdateReporter(connection, autoUpdater, AppLog.Write); }
        if (autoUpdater is { } auto) { main.CheckLocal = () => _ = auto.Ask(UpdateAskKind.Check, Guid.NewGuid().ToString()); }
    }

    // The app starts the host when it is not running: at first run, after an update, or after
    // someone stopped it and opened the app again. Also while agent-ha-bridge runs: the host
    // then only watches, and says why.
    async Task StartHostAsync()
    {
        if (manager is null || !manager.HostInstalled) { return; }
        try { await manager.StartAsync(); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            AppLog.Write($"the host did not start: {e.Message}");
        }
    }

    // The installed app's first run: start at sign-in, both the host and the app, which is what
    // makes it a background service at all. Once; turning either off afterwards sticks.
    void FirstRun(Func<AppPreferences> prefs, Action<AppPreferences> save, IAppAtSignIn atSignIn)
    {
        if (prefs().SetUp) { return; }
        try
        {
            if (manager!.HostInstalled && !manager.StartsAtSignIn) { manager.SetStartAtSignIn(true); }
            atSignIn.Set(true);
            save(prefs() with { SetUp = true });
        }
        // Any failure: COM maps HRESULTs onto many exception types, and this runs during launch,
        // where an escape ends the app before its window opens.
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            AppLog.Write($"first run: start at sign-in not set up: {e.Message}");
        }
    }

    void ShowWindow(string? machine, string? sessionId)
    {
        if (window is null) { return; }
        window.ShowInFront();
        if (sessionId is not null) { main?.Open(machine, sessionId); }
    }

    // The account's other machines through the relay: the app's own device key (kept like the
    // host's, in the TPM or with DPAPI, in the app's folder), and the account this machine's host
    // signed in to, which the host hands to the app alone (host.relay.account). Loading a TPM
    // key can take a moment, so it is not done on the UI thread.
    async Task StartFleetAsync(HostConnection local, IUiDispatcher ui, Func<FleetHost, Machine> remote, Action<IFleetLink> made)
    {
        Identity identity;
        try
        {
            // The same person starting the app for the first time in two Windows sessions at once
            // would create two keys, and the reference file would name only one of them. The
            // first copy creates it; the other waits, then loads the same one. Off the UI thread: a
            // TPM can take a moment, and the window and this machine's connection need not wait.
            var root = Path.Combine(Paths.AppData, "device");
            identity = await Task.Run(() =>
            {
                var folder = Directory.CreateDirectory(Path.Combine(root, options.Profile)).FullName;
                return UserLock.RunAsync(Path.Combine(folder, "init.lock"), () => Identity.Load(options.Profile, root: root), TimeSpan.FromSeconds(60), AppLog.Write);
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            AppLog.Write($"other machines are off: this app's device key could not be loaded: {e.Message}");
            return;
        }
        // The account last read, for reconnecting to the relay while this machine's host is away:
        // the other machines need not wait for it. Dropped when the relay refuses it, and read
        // again whenever the host is back.
        RelayAccount? last = null;
        var link = new RelayFleetLink(async ct =>
        {
            if (local.State != LinkState.Connected && Volatile.Read(ref last) is { } kept) { return kept; }
            await local.WhenConnectedAsync(ct);
            RelayAccount? now = null;
            if (local.Can(HostOps.Capability))
            {
                try { now = await local.CallAsync<RelayAccount>(HostOps.RelayAccount, ct: ct); }
                catch (HostErrorException) { }
            }
            Volatile.Write(ref last, now);
            return now;
        }, identity.Key, $"{Environment.MachineName} · Agent Bridge app", AppLog.Write, () => ClaimRelay(identity.Folder));
        link.Changed += () => { if (link.State == FleetState.Refused) { Volatile.Write(ref last, null); } };
        ui.Post(() =>
        {
            if (quitting || main is null) { _ = link.DisposeAsync().AsTask(); return; }
            made(link);
            // A sign-in (or a different account) on this machine's host: the app follows it now.
            // The relay too: the same account signed in through another relay moves the app with it.
            // And the host coming back: what it says now is checked against the account in use.
            // And a sign-in that finished, even to the same account: see LocalAccountWatch.
            var watch = new LocalAccountWatch(local.Status, local.State == LinkState.Connected);
            local.Changed += () => { if (watch.Next(local.Status, local.State == LinkState.Connected)) { link.Poke(); } };
            fleet = new Fleet(link, main, ui, remote);
            fleet.Start();
        });
    }

    // One relay link per person, not per Windows session: the same person signed in twice runs the
    // app twice, both with this key, and the relay keeps one connection per device, so the two
    // would take it from each other every second. Held while the app runs (UserLock).
    FileStream? relayLock;

    bool ClaimRelay(string folder)
    {
        if (relayLock is not null) { return true; }
        try
        {
            relayLock = UserLock.TryTake(Path.Combine(folder, "relay.lock"));
            return relayLock is not null;
        }
        // Not one that another copy holds: the lock cannot be had at all, so go without it.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"the relay lock could not be opened, so it is not kept: {e.Message}");
            return true;
        }
    }

    // Closing the window keeps the app in the notification area, where its toasts come from.
    void OnWindowClosing(Microsoft.UI.Windowing.AppWindowClosingEventArgs e)
    {
        if (quitting || tray is null) { return; }
        e.Cancel = true;
        window?.HideToTray();
    }

    void Quit()
    {
        if (quitting) { return; }
        quitting = true;
        timer?.Stop();
        updateTimer?.Stop();
        // Quit while an update stops the host, or before it is applied: the host comes back now, not
        // at the next sign-in, and no update stops it from here on. (Killed instead, the app's next
        // start does it.)
        manager?.Quit(TimeSpan.FromSeconds(25), AppLog.Write);
        showWait?.Unregister(null);
        tray?.Dispose();
        tray = null;
        var c = connection;
        var d = demo;
        var f = fleet;
        _ = Task.Run(async () =>
        {
            if (f is not null) { await f.DisposeAsync(); }
            if (c is not null) { await c.DisposeAsync(); }
            if (d is not null) { await d.DisposeAsync(); }
        });
        window?.Close();
        Exit();
    }
}

// The relay the app offers to sign in to, until the person names another.
static class HostDefaults
{
    public const string Relay = "https://relay.bad-duck.com";
}

// Stand-ins where the real thing would touch the machine: a development copy and the demo
// register nothing, start nothing and show no toasts.
sealed class NoLogonTasks : ILogonTasks
{
    public bool Exists(string name) => false;
    public void Register(string name, string xml) => throw new InvalidOperationException("the demo does not install anything");
    public void Delete(string name) { }
    public void Run(string name) { }
}

sealed class NoProcesses : IProcesses
{
    public int Start(string path, IReadOnlyList<string> arguments) => throw new InvalidOperationException("the demo does not start a host");
    public bool IsRunning(int pid) => false;
    public void Kill(int pid) { }
}

sealed class NoRunKey : IAppAtSignIn
{
    public bool Enabled => false;
    public void Set(bool on)
    {
        if (on) { throw new InvalidOperationException("only the installed app starts at sign-in"); }
    }
}

sealed class NoToasts : IToastSink
{
    public void Show(ToastNotice notice) { }
    public void Withdraw(string tag) { }
}
