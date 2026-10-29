using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
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

        var deliveries = new Deliveries(connection, ui);
        MainViewModel? self = null;
        var conn = connection;
        var cards = new CardContext
        {
            Deliveries = deliveries,
            Folds = new FoldStore(Path.Combine(appData, $"{options.Profile}-folds.json")),
            Capabilities = () => conn.Capabilities,
            MachineName = () => conn.Hello?.Machine.Name,
            ReadOnlyWhy = () => self?.ReadOnlyWhy,
            ShowDetail = () => prefs.ShowDetail,
            Host = conn,
            Shrinker = new WinRtImageShrinker(),
            FetchDetail = async id =>
            {
                try { return await conn.CallAsync<Session>(Ops.SessionGet, new { id }, TimeSpan.FromSeconds(15)); }
                catch (Exception e) when (e is IOException or HostErrorException or TimeoutException or JsonException) { return null; }
            },
        };

        IToastSink sink = installed ? new WindowsToastSink() : new NoToasts();
        var planner = new ToastPlanner(sink)
        {
            Enabled = () => prefs.Toasts && installed,
            FinishedEnabled = () => prefs.FinishedToasts,
            WindowActive = () => window?.IsInFront == true,
            Quiet = FocusAssist.Quiet,
            Capabilities = () => conn.Capabilities,
            MachineName = () => conn.Hello?.Machine.Name,
        };
        main = self = new MainViewModel(connection, cards, new UsageViewModel(), settings, planner);
        settings.PreferencesChanged += () => { foreach (var c in main.Sessions) { c.CapabilitiesChanged(); } };
        responder = new ToastResponder(deliveries, sink, id => ShowWindow(id));

        window = new MainWindow(main, options.SelfTest is not null);
        window.CloseRequested += OnWindowClosing;
        connection.Start();

        if (options.SelfTest is not null)
        {
            window.Activate();
            _ = SelfTest.RunAsync(window, main, options.SelfTest, demo!, code => { ExitCode = code; Quit(); });
            return;
        }

        tray = new TrayIcon(window.Handle, Path.Combine(AppContext.BaseDirectory, "Assets", "AgentBridge.ico"), () => ShowWindow(null), Quit);
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            main.Tick();
            tray?.SetTip(main.Waiting > 0 ? $"Agent Bridge: {main.Waiting} waiting on you" : "Agent Bridge");
        };
        timer.Start();

        // A toast click: open its session, or answer it without opening anything.
        ToastActivator.Attach((arguments, inputs) => queue.TryEnqueue(() => _ = responder.HandleAsync(arguments, inputs)));
        showWait = ThreadPool.RegisterWaitForSingleObject(single.Show, (_, _) => queue.TryEnqueue(() => ShowWindow(null)), null, Timeout.Infinite, executeOnlyOnce: false);

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

    void ShowWindow(string? sessionId)
    {
        if (window is null) { return; }
        window.ShowInFront();
        if (sessionId is not null) { main?.Open(sessionId); }
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
        _ = Task.Run(async () =>
        {
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
    public const string Relay = "https://agent-bridge-relay.agreeablepebble-329880d4.westus2.azurecontainerapps.io";
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
