using System.Text.Json;
using AgentBridge.HomeAssistant;
using AgentBridge.Host.Core.Updates;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace AgentBridge.Host;

// The host's own updates, for the Home Assistant connector's Install and Check buttons (stage 6,
// docs/ha-connector.md). The host is published inside the app's Velopack package
// (docs/app-windows.md), so updating it is updating the app: the same feed and the same rule for
// pre-releases as the app's Settings use (VelopackUpdater in AgentBridge.App), read from the app's
// files beside the profiles. A copy Velopack did not install (a build folder) has no Installed,
// and the connector serves no buttons for it.
//
// Applying: Velopack's Update.exe waits for this process to exit, stops whatever else runs from
// the package (the app, whose update hook stops the host it manages), replaces the version, and
// starts the app again in the background for this host's profile, which starts this host. So
// handing the release over is followed by this host stopping, as serve does on Ctrl+C.
sealed class VelopackHostUpdates(string dataRoot, string profile, Action stop, Action<string> log, IVelopackLocator? locator = null, ReleaseDiscovery? discovery = null,
    Func<CancellationToken, Task<string?>>? ready = null) : IHostUpdates
{
    public const string Repository = "https://github.com/danswett/agent-bridge";
    readonly object gate = new();
    // Shared with the app and every other profile's host through the app's folder (#530).
    readonly ReleaseDiscovery discovery = discovery ?? new ReleaseDiscovery(Path.Combine(dataRoot, "app"));
    UpdateManager? manager;
    string? source;

    // A release as a check found it, carried in the UpdateRelease the check returned: the install
    // downloads and applies exactly that, from the manager (and so the feed) that found it, never a
    // same-numbered release another check found since.
    sealed class Found(UpdateManager manager, UpdateInfo info)
    {
        public UpdateManager Manager { get; } = manager;
        public UpdateInfo Info { get; } = info;
        public volatile bool Downloaded;
    }

    static Found Of(UpdateRelease release) =>
        release.Handle as Found ?? throw new InvalidOperationException($"{release.Version} was not found by a check here");

    string AppFolder => Path.Combine(dataRoot, "app");

    // The installed package decides the channel, and so the feed (releases.<channel>.json) a check
    // reads: x64's `win` or Arm64's `win-arm64` (build/app-channels.ps1). Nothing here names one,
    // so a copy is only ever offered its own architecture. A test hands in the package's place.
    IVelopackLocator Locator() => locator ?? VelopackLocator.CreateDefaultForPlatform();

    // The app's feed.txt: a folder or URL holding releases.<channel>.json, for a test VM or a private
    // channel. Absent is GitHub; one that is there but cannot be read is an error, as in the app,
    // never GitHub: a press must not install from another channel than the one configured.
    string Feed()
    {
        var file = Path.Combine(AppFolder, "feed.txt");
        return File.Exists(file) ? File.ReadAllText(file).Trim() : "";
    }

    // The app's "Include pre-releases" for this profile, read as the app reads it
    // (AppPreferences.Load: web defaults, so any case; on unless turned off, and the defaults when
    // the file cannot be read).
    internal bool Prereleases()
    {
        var file = Path.Combine(AppFolder, $"{profile}-settings.json");
        try
        {
            if (!File.Exists(file)) { return true; }
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { return true; }
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(p.Name, "prereleases", StringComparison.OrdinalIgnoreCase)) { return p.Value.ValueKind != JsonValueKind.False; }
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return true; }
    }

    UpdateManager Manager()
    {
        lock (gate)
        {
            var feed = Feed();
            var prereleases = Prereleases();
            var key = $"{feed}|{prereleases}";
            if (manager is not null && source == key) { return manager; }
            IUpdateSource from = feed.Length == 0
                ? new DiscoveredGithubSource(Repository, prereleases, discovery)
                : Uri.TryCreate(feed, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
                    ? new SimpleWebSource(feed)
                    : new SimpleFileSource(new DirectoryInfo(feed));
            source = key;
            // The platform's own locator rather than VelopackLocator.Current, which only
            // VelopackApp.Build().Run() sets: that is the app's entry point, and the host has none.
            return manager = new UpdateManager(from, null, Locator());
        }
    }

    // From the package alone, whatever the feed: a feed that cannot be read now is a check's
    // problem, not a reason to stop serving the buttons.
    public string? Installed
    {
        get
        {
            try
            {
                var m = new UpdateManager(new SimpleFileSource(new DirectoryInfo(AppFolder)), null, Locator());
                return m.IsInstalled ? m.CurrentVersion?.ToString() : null;
            }
            catch (Exception e) when (e is not OutOfMemoryException) { return null; }
        }
    }

    public string? ReleasesUrl
    {
        get
        {
            try { return Feed().Length == 0 ? $"{Repository}/releases" : null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    public async Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        // Off the caller's thread before anything else: reading the feed and the preferences, and
        // whatever Velopack does before its first await, would otherwise run on the connector's
        // pass, where the heartbeat waits for it and no timeout reaches it.
        await Task.Yield();
        var m = Manager();
        var info = await m.CheckForUpdatesAsync().WaitAsync(cancellationToken);
        if (info is null) { return null; }
        var target = info.TargetFullRelease;
        return new UpdateRelease(target.Version.ToString(), target.NotesMarkdown, ReleasesUrl) { Handle = new Found(m, info) };
    }

    public async Task DownloadAsync(UpdateRelease release, Action<int> progress, CancellationToken cancellationToken)
    {
        var found = Of(release);
        await found.Manager.DownloadUpdatesAsync(found.Info, progress, cancellationToken);
        found.Downloaded = true;
    }

    // The updater serve hands the connector: none on a read-only host, which may be driven from no
    // other device (an update would restart it, and every other profile's host in the package),
    // nor where nothing packages the host.
    internal static IHostUpdates? For(string? readOnlyReason, bool windows, Func<IHostUpdates> make) =>
        readOnlyReason is null && windows ? make() : null;

    // What the app is started with after the update: in the background, for this host's profile
    // (the app starts the host of the profile it opens, and defaults to "default").
    internal static string[] RestartArguments(string profile) => ["--background", "--profile", profile];

    // Whatever the update would end is safe to end, or why not (HostUpdateGate).
    public async Task ReadyAsync(CancellationToken cancellationToken)
    {
        if (ready is not null && await ready(cancellationToken) is { } why) { throw new InvalidOperationException(why); }
    }

    public void ApplyAndRestart(UpdateRelease release)
    {
        var found = Of(release);
        if (!found.Downloaded) { throw new InvalidOperationException($"{release.Version} has not been downloaded here"); }
        found.Manager.WaitExitThenApplyUpdates(found.Info.TargetFullRelease, silent: true, restart: true, RestartArguments(profile));
        log($"updating to {release.Version}: Velopack applies it once this host has exited, and starts the app again, which starts the host");
        stop();
    }
}

// Velopack's GithubSource with its release list from the machine's shared ReleaseDiscovery
// (#530), rather than a request to the API of its own at every check. The rest is Velopack's:
// each release's releases.<channel>.json, and the packages, from their browser download URLs,
// which the API's limit does not count. The app has the same (AgentBridge.App's Platform.cs).
// downloader: Velopack's own unless a test hands in one that serves the releases' feeds.
sealed class DiscoveredGithubSource(string repository, bool prereleases, ReleaseDiscovery discovery, IFileDownloader? downloader = null)
    : GithubSource(repository, null, prereleases, downloader)
{
    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        // Read through discovery, so a list this can't read is its failure, backed off, never kept.
        var releases = await discovery.ReleasesAsync(ReleaseDiscovery.ReleasesUrl(RepoUri.ToString()),
            json => JsonSerializer.Deserialize<GithubRelease?[]>(json) ?? [], CancellationToken.None);
        // As GithubSource orders and filters its own.
        return [.. releases.OfType<GithubRelease>().OrderByDescending(r => r.PublishedAt).Where(r => includePrereleases || !r.Prerelease)];
    }
}

// Before the host hands an update to Velopack from Home Assistant's Install (#238). Velopack ends
// every process in the package, and a standalone Codex app-server whose input goes with a host must
// not be cut mid-refresh. This host's own probes are held and waited for. Another profile's host
// cannot be asked from here: its pipe takes management only from the app, never from this
// program, so one running from the package is a refusal, not a guess: update from the app there,
// which holds them all.
static class HostUpdateGate
{
    public static readonly TimeSpan Wait = TimeSpan.FromMinutes(11);

    // Cancelling (the host stopping) gives up the wait, never the probe.
    public static async Task<string?> ReadyAsync(Func<string?> otherHosts, Func<TimeSpan, CancellationToken, Task<bool>>? settle, CancellationToken cancellationToken)
    {
        if (Others(otherHosts) is { } before) { return before; }
        if (settle is not null && !await settle(Wait, cancellationToken))
        {
            return $"Codex's app-server, started to read its allowance, was still finishing after {Wait.TotalMinutes:0} minutes; try again later";
        }
        // Again just before the hand-over: one may have started while this host's probe finished.
        return Others(otherHosts);
    }

    static string? Others(Func<string?> otherHosts) => otherHosts() is { } others
        ? $"{others}, whose usage probes cannot be held from here; install from the Agent Bridge app on this machine, which waits for them"
        : null;

    // Another profile's host running from this installation, or one that cannot be looked at
    // (not knowing is not permission), as the app's PackageProcesses sees them.
    public static string? OtherHosts()
    {
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("agent-bridge-host"))
        {
            using (p)
            {
                if (p.Id == Environment.ProcessId) { continue; }
                string? path;
                try { path = p.MainModule?.FileName; }
                catch (InvalidOperationException) { continue; }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or NotSupportedException)
                {
                    return $"agent-bridge-host (pid {p.Id}) runs and cannot be looked at";
                }
                if (path is not null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return $"another profile's host (pid {p.Id}) runs from this installation";
                }
            }
        }
        return null;
    }
}
