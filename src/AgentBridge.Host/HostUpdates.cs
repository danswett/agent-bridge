using System.Text.Json;
using AgentBridge.HomeAssistant;
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
sealed class VelopackHostUpdates(string dataRoot, string profile, Action stop, Action<string> log) : IHostUpdates
{
    public const string Repository = "https://github.com/danswett/agent-bridge";
    readonly object gate = new();
    UpdateManager? manager;
    string? source;
    // Each release a check has found, with the manager that found it, by version: a check made
    // while one is being installed (or a feed changed in between) must not take away the release
    // the install downloads and applies.
    readonly Dictionary<string, (UpdateManager Manager, UpdateInfo Info)> found = new(StringComparer.Ordinal);

    string AppFolder => Path.Combine(dataRoot, "app");

    // The app's feed.txt: a folder or URL holding releases.win.json, for a test VM or a private
    // channel.
    string Feed()
    {
        var file = Path.Combine(AppFolder, "feed.txt");
        try { return File.Exists(file) ? File.ReadAllText(file).Trim() : ""; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return ""; }
    }

    // The app's "Include pre-releases" for this profile (AppPreferences.Prereleases, on unless
    // turned off).
    bool Prereleases()
    {
        var file = Path.Combine(AppFolder, $"{profile}-settings.json");
        try
        {
            if (!File.Exists(file)) { return true; }
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return !(doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("prereleases", out var p) && p.ValueKind == JsonValueKind.False);
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
                ? new GithubSource(Repository, null, prereleases, null)
                : Uri.TryCreate(feed, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
                    ? new SimpleWebSource(feed)
                    : new SimpleFileSource(new DirectoryInfo(feed));
            source = key;
            // The platform's own locator rather than VelopackLocator.Current, which only
            // VelopackApp.Build().Run() sets: that is the app's entry point, and the host has none.
            return manager = new UpdateManager(from, null, VelopackLocator.CreateDefaultForPlatform());
        }
    }

    public string? Installed
    {
        get
        {
            try { var m = Manager(); return m.IsInstalled ? m.CurrentVersion?.ToString() : null; }
            catch (Exception e) when (e is not OutOfMemoryException) { return null; }
        }
    }

    public string? ReleasesUrl => Feed().Length == 0 ? $"{Repository}/releases" : null;

    public async Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken)
    {
        var m = Manager();
        var info = await m.CheckForUpdatesAsync().WaitAsync(cancellationToken);
        if (info is null) { return null; }
        var target = info.TargetFullRelease;
        var version = target.Version.ToString();
        lock (gate) { found[version] = (m, info); }
        return new UpdateRelease(version, target.NotesMarkdown, ReleasesUrl);
    }

    (UpdateManager Manager, UpdateInfo Info) Found(UpdateRelease release)
    {
        lock (gate)
        {
            return found.TryGetValue(release.Version, out var f) ? f : throw new InvalidOperationException($"{release.Version} was not found by a check here");
        }
    }

    public Task DownloadAsync(UpdateRelease release, Action<int> progress, CancellationToken cancellationToken)
    {
        var (m, info) = Found(release);
        return m.DownloadUpdatesAsync(info, progress, cancellationToken);
    }

    // What the app is started with after the update: in the background, for this host's profile
    // (the app starts the host of the profile it opens, and defaults to "default").
    internal static string[] RestartArguments(string profile) => ["--background", "--profile", profile];

    public void ApplyAndRestart(UpdateRelease release)
    {
        var (m, info) = Found(release);
        m.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: true, RestartArguments(profile));
        log($"updating to {release.Version}: Velopack applies it once this host has exited, and starts the app again, which starts the host");
        stop();
    }
}
