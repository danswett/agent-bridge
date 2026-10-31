using AgentBridge.App.Core;
using Microsoft.Win32;
using Velopack;
using Velopack.Sources;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AgentBridge.App;

// The app's updates through Velopack. The host is in the same package, so this updates both.
// Releases come from the repository's GitHub releases; a feed.txt beside the app's data (a
// folder or URL holding releases.win.json) overrides that, for a test VM or a private channel.
sealed class VelopackUpdater(Func<bool> prereleases, IUiDispatcher ui, string profile) : IUpdater
{
    public const string Repository = "https://github.com/danswett/agent-bridge";
    UpdateManager? manager;
    UpdateInfo? pending;
    string? source;

    public static string FeedFile => Path.Combine(Paths.AppData, "feed.txt");

    public string? InstalledVersion
    {
        get
        {
            try { var m = Manager(); return m.IsInstalled ? m.CurrentVersion?.ToString() : null; }
            catch (Exception e) when (e is IOException or InvalidOperationException or NotSupportedException) { return null; }
        }
    }

    UpdateManager Manager()
    {
        var feed = File.Exists(FeedFile) ? File.ReadAllText(FeedFile).Trim() : "";
        var key = $"{feed}|{prereleases()}";
        if (manager is not null && source == key) { return manager; }
        IUpdateSource from = feed.Length == 0
            ? new GithubSource(Repository, null, prereleases(), null)
            : Uri.TryCreate(feed, UriKind.Absolute, out var u) && u.Scheme is "http" or "https"
                ? new SimpleWebSource(feed)
                : new SimpleFileSource(new DirectoryInfo(feed));
        source = key;
        pending = null;
        return manager = new UpdateManager(from);
    }

    public async Task<string?> CheckAsync()
    {
        pending = await Manager().CheckForUpdatesAsync();
        return pending?.TargetFullRelease.Version.ToString();
    }

    public string? PendingNotes => pending?.TargetFullRelease.NotesMarkdown is { Length: > 0 } notes ? notes : null;

    public Task DownloadAsync(Action<int> progress) =>
        pending is null ? Task.CompletedTask : Manager().DownloadUpdatesAsync(pending, p => ui.Post(() => progress(p)));

    // Back in the background for this app's profile, as the host's own updates restart it
    // (HostUpdates.RestartArguments): without --profile it would come back as the default one.
    public void ApplyAndRestart()
    {
        if (pending is not null) { Manager().ApplyUpdatesAndRestart(pending.TargetFullRelease, ["--background", "--profile", profile]); }
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
