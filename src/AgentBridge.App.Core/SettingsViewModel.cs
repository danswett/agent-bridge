using AgentBridge.Local;

namespace AgentBridge.App.Core;

// The app's own updates (Velopack in the app; a fake in tests). An app update is a host update:
// both ship in one package with one version.
public interface IUpdater
{
    // Null when this copy was not installed by Velopack (a build folder): no updates then.
    string? InstalledVersion { get; }
    Task<string?> CheckAsync();
    Task DownloadAsync(Action<int> progress);
    // Exits the app, replaces it, and starts the new version.
    void ApplyAndRestart();
}

// Whether the app itself starts at sign-in (into the notification area), so toasts keep coming
// after a restart. A Run entry in the app; a fake in tests.
public interface IAppAtSignIn
{
    bool Enabled { get; }
    void Set(bool on);
}

// The Settings page: this machine's host (running, version, start at sign-in, start, stop,
// restart, its log), its relay sign-in and name, what it lets other devices do, and the app's
// own preferences and updates.
public sealed class SettingsViewModel : Observable
{
    readonly HostConnection host;
    readonly HostManager manager;
    readonly IUpdater updater;
    readonly IAppAtSignIn appAtSignIn;
    readonly Func<AppPreferences> prefs;
    readonly Action<AppPreferences> savePrefs;
    string relayUrl;
    string machineName = "";
    bool nameEdited;
    string? error;
    bool busy;
    string updateStatus = "";
    string? updateVersion;
    string log = "";

    public SettingsViewModel(HostConnection host, HostManager manager, IUpdater updater, IAppAtSignIn appAtSignIn, Func<AppPreferences> prefs, Action<AppPreferences> savePrefs, string appVersion, string defaultRelay)
    {
        this.host = host;
        this.manager = manager;
        this.updater = updater;
        this.appAtSignIn = appAtSignIn;
        this.prefs = prefs;
        this.savePrefs = savePrefs;
        AppVersion = appVersion;
        relayUrl = prefs().Relay ?? defaultRelay;
        host.Changed += Refresh;
        Refresh();
    }

    public string AppVersion { get; }
    public string InstalledText => updater.InstalledVersion is { } v ? $"Installed version {v}" : "Not installed (a development copy): updates are off";
    HostStatus? Status => host.Status;

    public string HostState => host.State switch
    {
        LinkState.Connected => $"Running (pid {Status?.ProcessId}, since {Status?.StartedAt.ToLocalTime():g})",
        LinkState.Refused => $"The host refused this app: {host.StateDetail}",
        LinkState.NotRunning => manager.HostInstalled ? "Not running" : "Not installed beside the app",
        _ => host.StateDetail is { } d ? $"Connecting… ({d})" : "Connecting…",
    };
    public bool HostRunning => host.State == LinkState.Connected;
    public string HostVersion => Status is { } s ? $"{s.Version}{(s.Dev ? " (dev)" : "")}" : "";
    public bool VersionSkew => Status is { } s && !string.Equals(s.Version, AppVersion.Split('+')[0], StringComparison.Ordinal);
    public string Profile => manager.Layout.Profile;
    public string? AgentHaBridge => Status?.AgentHaBridge;
    public bool HasAgentHaBridge => !string.IsNullOrEmpty(Status?.AgentHaBridge);
    public bool RestartRequired => Status?.RestartRequired == true;
    public bool ElevatedWarning => Status?.Elevated == true;

    public string AccountText => Status?.Account is { } a ? $"Signed in as {a.Login}" : "Not signed in: this machine's sessions are only in this app";
    public string RelayText => Status?.Relay is not { } r ? "" : r.State switch
    {
        RelayState.Connected => $"Connected to {Host(r.Url)}",
        RelayState.Connecting => $"Connecting to {Host(r.Url)}{(r.Detail is { } d ? $": {d}" : "")}",
        RelayState.Refused => r.Detail ?? "The relay refused this device; sign in again",
        RelayState.Off => "Not using the relay",
        _ => "Not signed in",
    };
    public bool SignInPending => Status?.SignIn.State == SignInState.Pending;
    public string? SignInCode => Status?.SignIn.UserCode;
    public string? SignInUri => Status?.SignIn.VerificationUri;
    public string? SignInError => Status?.SignIn.State == SignInState.Failed ? Status.SignIn.Error : null;
    public bool CanSignIn => HostRunning && !SignInPending && Status?.Relay.State != RelayState.Off;

    public string RelayUrl { get => relayUrl; set => Set(ref relayUrl, value); }

    public string MachineName
    {
        get => machineName;
        set { if (Set(ref machineName, value)) { nameEdited = true; } }
    }

    public bool RemoteApproval
    {
        get => Status?.Settings.RemoteApproval ?? true;
        set { if (Status is { } s && s.Settings.RemoteApproval != value) { _ = RunAsync(() => host.CallAsync(HostOps.Settings, new SettingsRequest(RemoteApproval: value))); } }
    }

    // Each setter acts only on a change: a binding refreshing pushes the same value back.
    public bool StartAtSignIn
    {
        get { try { return manager.StartsAtSignIn; } catch (Exception) { return false; } }
        set { if (value != StartAtSignIn) { _ = RunAsync(() => { manager.SetStartAtSignIn(value); return Task.CompletedTask; }); } }
    }

    public bool AppStartsAtSignIn
    {
        get => appAtSignIn.Enabled;
        set { if (value != appAtSignIn.Enabled) { _ = RunAsync(() => { appAtSignIn.Set(value); return Task.CompletedTask; }); } }
    }

    public bool ShowDetail { get => prefs().ShowDetail; set { if (value != ShowDetail) { Save(prefs() with { ShowDetail = value }); } } }
    public bool Toasts { get => prefs().Toasts; set { if (value != Toasts) { Save(prefs() with { Toasts = value }); } } }
    public bool FinishedToasts { get => prefs().FinishedToasts; set { if (value != FinishedToasts) { Save(prefs() with { FinishedToasts = value }); } } }

    public string? Error { get => error; private set { if (Set(ref error, value)) { Raise(nameof(HasError)); } } }
    public bool HasError => error is not null;
    public bool Busy { get => busy; private set => Set(ref busy, value); }
    public string UpdateStatus { get => updateStatus; private set => Set(ref updateStatus, value); }
    public bool UpdateAvailable => updateVersion is not null;
    public string Log { get => log; private set => Set(ref log, value); }

    public event Action? PreferencesChanged;

    void Save(AppPreferences next)
    {
        savePrefs(next);
        RaiseAll();
        PreferencesChanged?.Invoke();
    }

    public void Refresh()
    {
        if (!nameEdited) { machineName = Status?.Settings.Name ?? Status?.Machine.Name ?? ""; }
        RaiseAll();
    }

    public Task StartHostAsync() => RunAsync(() => manager.StartAsync());
    public Task StopHostAsync() => RunAsync(() => manager.StopAsync());

    // Through the logon task or launcher when the host is installed beside the app, so the
    // new one runs as it does at sign-in (and the task's restart-on-failure watches it again);
    // a host the app cannot start (run from the command line, say) restarts itself.
    public Task RestartHostAsync() => RunAsync(async () =>
    {
        if (manager.HostInstalled) { await manager.RestartAsync(); }
        else if (host.State == LinkState.Connected) { await host.CallAsync(HostOps.Restart); }
        else { throw new InvalidOperationException($"the host is not beside the app ({manager.Layout.HostExe})"); }
    });

    public Task SignInAsync() => RunAsync(async () =>
    {
        var url = RelayUrl.Trim();
        savePrefs(prefs() with { Relay = url });
        await host.CallAsync<SignInStatus>(HostOps.SignIn, new SignInRequest(url.Length > 0 ? url : null), TimeSpan.FromSeconds(40));
    });

    public Task CancelSignInAsync() => RunAsync(() => host.CallAsync(HostOps.SignInCancel));

    public Task SaveNameAsync() => RunAsync(async () =>
    {
        await host.CallAsync(HostOps.Settings, new SettingsRequest(Name: MachineName));
        nameEdited = false;
    });

    public void RefreshLog() => Log = manager.LogTail();

    public Task CheckForUpdatesAsync() => RunAsync(async () =>
    {
        if (updater.InstalledVersion is null) { UpdateStatus = "Updates are off in a development copy."; return; }
        UpdateStatus = "Checking…";
        updateVersion = await updater.CheckAsync();
        UpdateStatus = updateVersion is { } v ? $"Version {v} is available." : $"Up to date ({updater.InstalledVersion}).";
        Raise(nameof(UpdateAvailable));
    });

    // The host is stopped first so nothing in the app's folder is in use while it is replaced;
    // the new version starts it again.
    public Task InstallUpdateAsync() => RunAsync(async () =>
    {
        if (updateVersion is null) { return; }
        UpdateStatus = $"Downloading {updateVersion}…";
        await updater.DownloadAsync(p => UpdateStatus = $"Downloading {updateVersion}… {p}%");
        UpdateStatus = "Stopping the host to update it…";
        await manager.StopAsync();
        UpdateStatus = "Restarting…";
        updater.ApplyAndRestart();
    });

    async Task RunAsync(Func<Task> act)
    {
        Error = null;
        Busy = true;
        try { await act(); }
        catch (Exception e) when (e is IOException or HostErrorException or TimeoutException or InvalidOperationException or UnauthorizedAccessException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            Error = e.Message;
        }
        finally
        {
            Busy = false;
            RaiseAll();
        }
    }

    static string Host(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url ?? "the relay";
}
