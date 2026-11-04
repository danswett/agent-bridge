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
//
// Behind the release halt list (#580, ReleaseRollout), as the app's updater is (HaltListUpdater):
// a check that finds a halted release fails saying so, a list that cannot be read fails it too,
// and the list is read again once the release is downloaded, just before it is handed over, so a
// release halted meanwhile is never applied. Nothing here downgrades: a host on a halted release
// stays on it until a newer one is published.
sealed class VelopackHostUpdates(string dataRoot, string profile, Action stop, Action<string> log, IVelopackLocator? locator = null) : IHostUpdates
{
    public const string Repository = "https://github.com/danswett/agent-bridge";
    // Longer than handing over takes, short enough that the list read is the one that decided.
    static readonly TimeSpan ConfirmedFor = TimeSpan.FromMinutes(5);
    static readonly HttpClient Rollout = new() { Timeout = TimeSpan.FromSeconds(30) };
    readonly object gate = new();
    UpdateManager? manager;
    string? source;

    // A release as a check found it, carried in the UpdateRelease the check returned: the install
    // downloads and applies exactly that, from the manager (and so the feed) that found it, never a
    // same-numbered release another check found since. Confirmed: when the halt list, read after
    // the download, last did not halt it.
    sealed class Found(UpdateManager manager, UpdateInfo info, string feed)
    {
        public UpdateManager Manager { get; } = manager;
        public UpdateInfo Info { get; } = info;
        public string Feed { get; } = feed;
        public volatile bool Downloaded;
        public DateTimeOffset? Confirmed;
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

    // The manager for the feed configured now, and that feed ("" for GitHub), whose halt list goes
    // with what it finds.
    (UpdateManager Manager, string Feed) Manager()
    {
        lock (gate)
        {
            var feed = Feed();
            var prereleases = Prereleases();
            var key = $"{feed}|{prereleases}";
            if (manager is not null && source == key) { return (manager, feed); }
            IUpdateSource from = feed.Length == 0
                ? new GithubSource(Repository, null, prereleases, null)
                : Uri.TryCreate(feed, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
                    ? new SimpleWebSource(feed)
                    : new SimpleFileSource(new DirectoryInfo(feed));
            source = key;
            // The platform's own locator rather than VelopackLocator.Current, which only
            // VelopackApp.Build().Run() sets: that is the app's entry point, and the host has none.
            return (manager = new UpdateManager(from, null, Locator()), feed);
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
        var (m, feed) = Manager();
        var info = await m.CheckForUpdatesAsync().WaitAsync(cancellationToken);
        if (info is null) { return null; }
        var target = info.TargetFullRelease;
        var version = target.Version.ToString();
        // Throws when the list cannot be read: nothing is downloaded.
        if ((await ReadRolloutAsync(feed, cancellationToken)).Halts(version))
        {
            log($"update: {version} is on the release halt list ({ReleaseRollout.FileName}); it is not downloaded or installed");
            throw new ReleaseHaltedException(version);
        }
        return new UpdateRelease(version, target.NotesMarkdown, ReleasesUrl) { Handle = new Found(m, info, feed) };
    }

    // The halt list beside the feed the release came from: the `rollout` release's for GitHub.
    static Task<ReleaseRollout> ReadRolloutAsync(string feed, CancellationToken cancellationToken) =>
        ReleaseRollout.ReadAsync(ReleaseRollout.Location(feed, Repository), Rollout, cancellationToken);

    // The list read again once downloaded, which may have taken a while: a release halted since is
    // not handed over (ApplyAndRestart refuses one not confirmed here moments before).
    public async Task DownloadAsync(UpdateRelease release, Action<int> progress, CancellationToken cancellationToken)
    {
        var found = Of(release);
        found.Confirmed = null;
        await found.Manager.DownloadUpdatesAsync(found.Info, progress, cancellationToken);
        found.Downloaded = true;
        if ((await ReadRolloutAsync(found.Feed, cancellationToken)).Halts(release.Version))
        {
            log($"update: {release.Version}, downloaded, is now on the release halt list ({ReleaseRollout.FileName}); it is not installed");
            throw new ReleaseHaltedException(release.Version);
        }
        found.Confirmed = DateTimeOffset.UtcNow;
    }

    // The updater serve hands the connector: none on a read-only host, which may be driven from no
    // other device (an update would restart it, and every other profile's host in the package),
    // nor where nothing packages the host.
    internal static IHostUpdates? For(string? readOnlyReason, bool windows, Func<IHostUpdates> make) =>
        readOnlyReason is null && windows ? make() : null;

    // What the app is started with after the update: in the background, for this host's profile
    // (the app starts the host of the profile it opens, and defaults to "default").
    internal static string[] RestartArguments(string profile) => ["--background", "--profile", profile];

    public void ApplyAndRestart(UpdateRelease release)
    {
        var found = Of(release);
        if (!found.Downloaded) { throw new InvalidOperationException($"{release.Version} has not been downloaded here"); }
        if (found.Confirmed is not { } at || DateTimeOffset.UtcNow - at > ConfirmedFor)
        {
            throw new InvalidOperationException($"the release halt list was not read just before installing {release.Version}, so nothing was installed; try again");
        }
        found.Confirmed = null;
        found.Manager.WaitExitThenApplyUpdates(found.Info.TargetFullRelease, silent: true, restart: true, RestartArguments(profile));
        log($"updating to {release.Version}: Velopack applies it once this host has exited, and starts the app again, which starts the host");
        stop();
    }
}
