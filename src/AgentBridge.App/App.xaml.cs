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

        var updater = new VelopackUpdater(() => prefs.Prereleases, ui);
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
        Machine Build(string key, HostConnection conn, FoldStore folds, Func<string?> readOnlyWhy)
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

        // The account's other machines, through the relay as a device of the app's own (the
        // demo's is a second pretend host). Remote folds are not kept across restarts.
        IFleetLink? link = null;
        Machine Remote(FleetHost h) =>
            Build(h.Id, new HostConnection(ct => link!.OpenAsync(h.Id, ct), ui, AppLog.Write) { RetryDelay = TimeSpan.FromSeconds(options.Demo ? 0.5 : 5) }, new FoldStore(null), () => null);
        if (options.Demo)
        {
            remoteDemo = DemoHost.Start(version, remote: true);
            link = new DemoFleetLink(remoteDemo);
            fleet = new Fleet(link, main, ui, Remote);
            fleet.Start();
        }
        else { _ = StartFleetAsync(connection, ui, Remote, l => link = l); }
        window = new MainWindow(main, options.SelfTest is not null);
        window.CloseRequested += OnWindowClosing;
        connection.Start();

        if (options.SelfTest is not null)
        {
            window.Activate();
            _ = SelfTest.RunAsync(window, main, options.SelfTest, demo!, remoteDemo!, code => { ExitCode = code; Quit(); });
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
        P256KeyPair key;
        try { key = await Task.Run(() => Identity.Load($"device-{options.Profile}", root: Paths.AppData).Key); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            AppLog.Write($"other machines are off: this app's device key could not be loaded: {e.Message}");
            return;
        }
        var link = new RelayFleetLink(async ct =>
        {
            await local.WhenConnectedAsync(ct);
            if (!local.Can(HostOps.Capability)) { return null; }
            try { return await local.CallAsync<RelayAccount>(HostOps.RelayAccount, ct: ct); }
            catch (HostErrorException) { return null; }
        }, key, $"{Environment.MachineName} · Agent Bridge app", AppLog.Write);
        ui.Post(() =>
        {
            if (quitting || main is null) { _ = link.DisposeAsync().AsTask(); return; }
            made(link);
            // A sign-in (or a different account) on this machine's host: the app follows it now.
            var account = local.Status?.Account?.AccountId;
            local.Changed += () =>
            {
                var now = local.Status?.Account?.AccountId;
                if (now == account) { return; }
                account = now;
                link.Poke();
            };
            fleet = new Fleet(link, main, ui, remote);
            fleet.Start();
        });
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
