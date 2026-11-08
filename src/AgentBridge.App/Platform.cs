using AgentBridge.App.Core;
using AgentBridge.Host;
using AgentBridge.Host.Core.Updates;
using Microsoft.Win32;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AgentBridge.App;

// The app's updates through Velopack. The host is in the same package, so this updates both.
// Releases come from the repository's GitHub releases; a feed.txt beside the app's data (a
// folder or URL holding releases.<channel>.json) overrides that, for a test VM or a private channel.
// profileFolder: this profile's own folder, where the installer's copy of the package is made
// (PackageHandoff); null for a copy that only reads its version. log: where which source answered
// a check (the CDN or GitHub, #927) is said.
sealed class VelopackUpdater(Func<bool> prereleases, IUiDispatcher ui, string profile, string? profileFolder = null, Action<string>? log = null) : IUpdater
{
    public const string Repository = "https://github.com/danswett/agent-bridge";
    // Shared with every profile's app and host through the app's folder (#530).
    readonly ReleaseDiscovery discovery = new(Paths.AppData, log: log);
    UpdateManager? manager;
    UpdateInfo? pending;
    string? source;
    readonly DownloadMark mark = new(Path.Combine(Paths.AppData, "update-downloaded.txt"));
    PackageHandoff? Handoff => profileFolder is null ? null : new(mark, Path.Combine(profileFolder, "app-update"));

    public static string FeedFile => Path.Combine(Paths.AppData, "feed.txt");

    // A feed.txt source is read whole at every check; only GitHub's list is shared and may be older.
    public DateTimeOffset? DiscoveredAt => source is { } key && key.StartsWith('|') ? discovery.FetchedAt : null;

    public string? InstalledVersion
    {
        get
        {
            try { var m = Manager(); return m.IsInstalled ? m.CurrentVersion?.ToString() : null; }
            catch (Exception e) when (Unreadable(e)) { return null; }
        }
    }

    // feed.txt, or the source it names, that can't be read just now: read by the banner and Settings
    // as they redraw (InstalledVersion, Ready), where an exception would end the app, so nothing
    // installed or ready rather than a throw.
    static bool Unreadable(Exception e) =>
        e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or UriFormatException or ArgumentException;

    // The version installed, for a check under way (a CDN's list may not be older, #927): the
    // manager as it is, never one Manager() would make afresh mid-check.
    string? Installed()
    {
        try { return manager is { IsInstalled: true } m ? m.CurrentVersion?.ToString() : null; }
        catch (Exception e) when (Unreadable(e)) { return null; }
    }

    UpdateManager Manager()
    {
        var feed = File.Exists(FeedFile) ? File.ReadAllText(FeedFile).Trim() : "";
        var key = $"{feed}|{prereleases()}";
        if (manager is not null && source == key) { return manager; }
        IUpdateSource from = feed.Length == 0
            ? new DiscoveredGithubSource(Repository, prereleases(), discovery, installed: Installed)
            : Uri.TryCreate(feed, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
                ? new SimpleWebSource(feed)
                : new SimpleFileSource(new DirectoryInfo(feed));
        source = key;
        pending = null;
        downloaded = null;
        // No channel named: the installed package's own decides, `win` for x64 and `win-arm64` for
        // Arm64 (build/app-channels.ps1), so a copy is only ever offered its own architecture.
        var first = manager is null;
        // A copy an earlier run handed the installer (installed since, or refused) is not kept.
        if (first) { Handoff?.Clear(); }
        manager = new UpdateManager(from, AppPackages.Options);
        // Only for the source this start began with: a change of feed or of "Include pre-releases"
        // drops what was downloaded, which must not come back as ready from the packages folder,
        // now or at a later start (the mark says which source the package came from, and the
        // checksum its bytes were verified against, which they must still match).
        local = first && Local(manager) is { } kept && PackageFile(kept) is { } file && mark.Vouches(file, kept.Version.ToString(), key) ? kept : null;
        return manager;
    }

    // Where Velopack keeps a release's package: its folder (the process's locator, which
    // UpdateManager uses too), under the release's file name. Null when that can't be told.
    static string? PackageFile(VelopackAsset release) =>
        VelopackLocator.IsCurrentSet && VelopackLocator.Current.PackagesDir is { Length: > 0 } folder && release.FileName is { Length: > 0 } name
            ? Path.Combine(folder, name)
            : null;

    // The installer's copy of the package (PackageHandoff), held open, read-only to everyone else,
    // until this process ends. It is in this profile's own folder, which no other profile's updater
    // writes, so the bytes checked are the ones Update.exe opens once the app has exited (#984).
    static FileStream? handedOver;

    // A release an earlier run downloaded and nobody has restarted into yet (#516): ready from this
    // start on, before any check, so the app offers Restart to update offline, or while the feed
    // refuses (#530), as it did when that run ended. Velopack keeps the package in its packages
    // folder; it no longer applies it by itself at startup (Program). The first check on this
    // source decides from then on.
    VelopackAsset? local;

    static VelopackAsset? Local(UpdateManager m)
    {
        try { return m.IsInstalled ? m.UpdatePendingRestart : null; }
        catch (Exception e) when (e is IOException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException) { return null; }
    }

    public async Task<string?> CheckAsync()
    {
        var from = Manager();
        // GitHub's limit (#800) comes as ReleaseDiscovery's Limited, with the reset GitHub sent:
        // AutoUpdater holds its checks off until then.
        var found = await from.CheckForUpdatesAsync();
        // The feed or the pre-release choice changed while this check ran (one by Settings, one
        // automatic, may overlap): its answer is the old source's, and must not undo the change by
        // becoming pending again. What the current source has found stands.
        if (!ReferenceEquals(from, Manager())) { return pending?.TargetFullRelease.Version.ToString(); }
        local = null;
        // The same release found again stays the one downloaded.
        if (found?.TargetFullRelease.Version.ToString() != pending?.TargetFullRelease.Version.ToString()) { pending = found; downloaded = null; }
        return pending?.TargetFullRelease.Version.ToString();
    }

    // The feed and Include pre-releases now: Manager reads both, and drops what the old ones found.
    public string Source
    {
        get
        {
            try { Manager(); }
            catch (Exception e) when (Unreadable(e)) { }
            return source ?? "";
        }
    }

    public string? PendingNotes => pending?.TargetFullRelease.NotesMarkdown is { Length: > 0 } notes ? notes : null;

    // Settings and the automatic updates share this updater, so a check or a change of feed by one
    // can replace the release the other downloaded: only the release downloaded is ready.
    UpdateInfo? downloaded;

    public string? Ready
    {
        get
        {
            // Read by the banner and Settings as they redraw, and on every change the updater says:
            // a feed.txt that cannot be read just now is nothing ready, never an exception there,
            // where it would end the app.
            try { Manager(); }
            catch (Exception e) when (Unreadable(e)) { return null; }
            if (pending is null) { return local?.Version.ToString(); }
            return ReferenceEquals(downloaded, pending) ? pending.TargetFullRelease.Version.ToString() : null;
        }
    }

    public async Task DownloadAsync(Action<int> progress)
    {
        var from = Manager();
        if (pending is not { } release) { return; }
        // Velopack takes a package already in its folder under this name for the release without
        // checking its bytes (one from another feed may be there): checked against the chosen
        // release's checksum from its feed before and after, and marked only when it matches
        // (Engineering's second-family review of #719, 6676612221).
        var target = release.TargetFullRelease;
        var expected = PackageDigest.Of(target.SHA256, target.SHA1)
            ?? throw new PackageMismatchException($"the release feed gives no checksum for {target.Version}, so its package cannot be verified");
        var file = PackageFile(target) ?? throw new PackageMismatchException("the folder Velopack keeps packages in can't be told, so the package can't be verified");
        DownloadMark.DropMismatch(file, expected);
        await from.DownloadUpdatesAsync(release, p => ui.Post(() => progress(p)));
        if (ReferenceEquals(from, Manager()) && ReferenceEquals(pending, release))
        {
            mark.Accept(file, target.Version.ToString(), source!, expected);
            downloaded = release;
        }
    }

    // Back in the background for this app's profile, as the host's own updates restart it
    // (HostUpdates.RestartArguments): without --profile it would come back as the default one.
    // Only the release named, downloaded here from the source selected now (Ready, which the mark
    // vouches for): anything else throws, before anything is replaced. A check or a download from
    // Settings may have replaced the one the person chose while the host stopped (r4487830884).
    public void ApplyAndRestart(string version)
    {
        if (Ready is not { } ready) { throw new InvalidOperationException("no downloaded update to install; check for updates again"); }
        if (ready != version) { throw new InvalidOperationException($"{version} is no longer the update downloaded ({ready} is); nothing was installed"); }
        var release = pending?.TargetFullRelease ?? local ?? throw new InvalidOperationException("no downloaded update to install; check for updates again");
        if (release.Version.ToString() != version) { throw new InvalidOperationException($"{version} is no longer the update downloaded; nothing was installed"); }
        // Not Velopack's ApplyUpdatesAndRestart, which names the shared packages folder's file to
        // Update.exe (#984): the bytes verified and marked, copied into this profile's own folder
        // and checked again from the handle that keeps them until this process ends, then
        // Update.exe started as ApplyUpdatesAndRestart starts it, with --package naming the copy
        // and the shared --packageDir, where its install lock is.
        Manager();
        var handoff = Handoff ?? throw new InvalidOperationException("this copy of the app has no profile folder to hand an update over from; nothing was installed");
        if (!VelopackLocator.IsCurrentSet) { throw new InvalidOperationException("Velopack's locator is not set, so the installer can't be found; nothing was installed"); }
        var locator = VelopackLocator.Current;
        if (locator.UpdateExePath is not { Length: > 0 } updateExe || !File.Exists(updateExe)) { throw new FileNotFoundException("Cannot find Update.exe to apply updates.", locator.UpdateExePath); }
        if (locator.RootAppDir is not { Length: > 0 } root || locator.PackagesDir is not { Length: > 0 } packages || PackageFile(release) is not { } file)
        {
            throw new InvalidOperationException("the installation's folders can't be told, so nothing was installed");
        }
        handedOver?.Dispose();
        handedOver = null;
        var copy = handoff.Prepare(file, version, source!);
        handedOver = handoff.Launch(copy, (uint)Environment.ProcessId, root, packages, ["--background", "--profile", profile],
            args => locator.Process.StartProcess(updateExe, args, Path.GetDirectoryName(updateExe)!, false));
        // As ApplyUpdatesAndRestart ends: Update.exe waits for this process to exit (--waitPid).
        locator.Process.Exit(0);
    }
}

// Another profile's app or host running from this installation, which an update would stop
// whatever it is doing (Update.exe stops every process in the package, and starts only the
// updating profile's app again). One that cannot be looked at counts: not knowing is not
// permission.
static class PackageProcesses
{
    public static string? Others(int? ownHost)
    {
        var root = Path.GetFullPath(AppContext.BaseDirectory);
        foreach (var name in new[] { "AgentBridge", "agent-bridge-host" })
        {
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(name))
            {
                using (p)
                {
                    if (p.Id == Environment.ProcessId || p.Id == ownHost) { continue; }
                    string? path;
                    try { path = p.MainModule?.FileName; }
                    catch (InvalidOperationException) { continue; }
                    catch (System.ComponentModel.Win32Exception) { return $"{name} (pid {p.Id}) runs and cannot be looked at"; }
                    if (path is not null && path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        return $"another profile's {(name == "AgentBridge" ? "app" : "host")} (pid {p.Id}) runs from this installation";
                    }
                }
            }
        }
        return null;
    }
}

// The app at sign-in, into the notification area, so toasts keep arriving after a restart: an
// HKCU Run entry (per user, no admin). The host has its own logon task; the app is only its
// window and toasts, so a plain Run entry is enough for it.
sealed class RunKey(string exe) : IAppAtSignIn
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string Name = "AgentBridgeApp";

    public bool Enabled
    {
        get
        {
            using var run = Registry.CurrentUser.OpenSubKey(Key);
            return run?.GetValue(Name) is string;
        }
    }

    public void Set(bool on)
    {
        using var run = Registry.CurrentUser.CreateSubKey(Key);
        if (on) { run.SetValue(Name, $"\"{exe}\" --background"); }
        else { run.DeleteValue(Name, throwOnMissingValue: false); }
    }
}

// Pictures too big to send are made smaller, as the PWA does: scaled down and re-encoded as
// JPEG until they fit, or given up on.
sealed class WinRtImageShrinker : IImageShrinker
{
    public async Task<byte[]?> ShrinkAsync(byte[] image, int maxBytes)
    {
        try
        {
            using var input = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(input.GetOutputStreamAt(0)))
            {
                writer.WriteBytes(image);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            var decoder = await BitmapDecoder.CreateAsync(input);
            var longest = Math.Max(decoder.PixelWidth, decoder.PixelHeight);
            foreach (var edge in new uint[] { 2560, 1920, 1600, 1280, 1024, 800 })
            {
                var scale = Math.Min(1.0, (double)edge / longest);
                using var output = new InMemoryRandomAccessStream();
                var jpeg = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, new BitmapPropertySet
                {
                    ["ImageQuality"] = new BitmapTypedValue(0.82, Windows.Foundation.PropertyType.Single),
                });
                var pixels = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
                jpeg.SetSoftwareBitmap(pixels);
                jpeg.BitmapTransform.ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
                jpeg.BitmapTransform.ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
                jpeg.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                await jpeg.FlushAsync();
                if (output.Size <= (ulong)maxBytes)
                {
                    var bytes = new byte[output.Size];
                    using var reader = new DataReader(output.GetInputStreamAt(0));
                    await reader.LoadAsync((uint)output.Size);
                    reader.ReadBytes(bytes);
                    return bytes;
                }
            }
            return null;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }
}
