using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace AgentBridge.App.Core;

// #3343: Velopack's apply renames current aside and stops every process under the installation's
// root, so the app hands its verified package to a copy of itself outside it (ApplyAndRestart), which
// waits for the app, runs Update.exe's apply, and only when that is refused over a held file installs in place.
public sealed record InPlaceHandoff(string Package, PackageDigest Digest, string Root, string Packages, int Pid, long Started, IReadOnlyList<string> Restart)
{
    public const string Flag = "--inplace-apply";
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public IReadOnlyList<string> Arguments() => [Flag, Package, Digest.Algorithm, Digest.Hex, Root, Packages, Pid.ToString(Inv), Started.ToString(Inv), .. Restart];
    public static InPlaceHandoff? Parse(IReadOnlyList<string> a) =>
        a.Count >= 8 && a[0] == Flag && int.TryParse(a[6], NumberStyles.None, Inv, out var pid) && long.TryParse(a[7], NumberStyles.None, Inv, out var started)
            ? new(a[1], new(a[2], a[3]), a[4], a[5], pid, started, a.Skip(8).ToList()) : null;

    // False while the app that handed over (that process, not a later one with its PID) still runs.
    public bool WaitForApp(TimeSpan most)
    {
        try { using var app = Process.GetProcessById(Pid); return app.StartTime.ToUniversalTime().Ticks != Started || app.WaitForExit(most); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return true; }
    }

    // The helper's whole run. Anyone can start the exe with these arguments, and Update.exe stops the
    // package's hosts, so it first asks what Velopack's hooks ask (LifecycleGuard, #67): a refused
    // caller installs and starts nothing. The app asked too before handing over, as its ancestry
    // leaves this one's once it exits. Then the install, and the app again either way; 1 when not all was installed.
    public int Run(Func<string?> refusal, string staged, Func<IReadOnlyList<string>, int> updateExe, Func<IReadOnlyList<string>> held, Action<IReadOnlyList<string>> restart, Action<string> log)
    {
        if (refusal() is { } why) { log($"update: nothing was installed or started: this is running {why}. Installing is for a person."); return 4; }
        var code = 0;
        try
        {
            if (!WaitForApp(TimeSpan.FromMinutes(1))) { throw new InvalidOperationException("the app that handed the update over did not exit; nothing was installed"); }
            log(Install(staged, updateExe, held, log) ? "update: installed in place; the held files were left as they were" : "update: installed");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException or Win32Exception)
        { log($"update: {e.Message}"); code = 1; }
        try { restart(Restart); }
        catch (Exception e) when (e is IOException or Win32Exception) { log($"update: the app could not be started again: {e.Message}"); code = 1; }
        return code;
    }

    // updateExe: Update.exe run with these arguments, its exit code. The package is held and checked against the
    // digest the app verified, and staged from that handle. True when installed in place, false when apply took.
    public bool Install(string staged, Func<IReadOnlyList<string>, int> updateExe, Func<IReadOnlyList<string>> held, Action<string>? log = null)
    {
        using var package = DownloadMark.Hold(Package, Digest)
            ?? throw new PackageMismatchException("the update handed over is no longer the bytes verified for it; nothing was installed");
        FileStream? locked = null;
        try
        {
            return InPlaceApply.Install(() => updateExe(PackageHandoff.Arguments(Package, Root, Packages, [], wait: false)) == 0,
                () => { locked = Lock(); return held() is { Count: > 0 } files ? Extract(package, staged, files) : []; },
                staged, Path.Combine(Root, "current"), Path.Combine(Root, "current-inplace"), log);
        }
        finally { locked?.Dispose(); try { if (Directory.Exists(staged)) { InPlaceApply.DeleteTree(staged); } } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
    }

    // Velopack's install lock (locator.rs try_get_exclusive_lock): another profile's Update.exe changes
    // current while it holds it. Taken once Update.exe has exited, and kept through any rollback.
    FileStream Lock()
    {
        try
        {
            Directory.CreateDirectory(Packages);
            return new FileStream(Path.Combine(Packages, ".velopack_lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new InPlaceApplyException($"another install holds {Path.Combine(Packages, ".velopack_lock")} ({e.Message}); nothing was installed", e); }
    }

    // As Velopack 1.2.161 extracts into current: the nuspec as sq.version, then lib/<framework>/'s
    // files but its launcher's two. A link, or a name outside, is refused.
    static IReadOnlyList<string> Extract(Stream package, string staged, IReadOnlyList<string> held)
    {
        if (Directory.Exists(staged)) { InPlaceApply.DeleteTree(staged); }
        var into = Directory.CreateDirectory(staged).FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if ((!name.Contains('/') && name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase) ? "sq.version"
                : name.Split('/') is ["lib", _, _, ..] parts && parts[^1] is { Length: > 0 } and not ("Squirrel.exe" or "_ExecutionStub.exe") ? string.Join('/', parts[2..]) : null) is not { } rel) { continue; }
            var to = Path.GetFullPath(Path.Combine(into, rel));
            if (rel.EndsWith(".__symlink", StringComparison.Ordinal) || !to.StartsWith(into, StringComparison.OrdinalIgnoreCase)) { throw new PackageMismatchException($"the update's {name} is a link or outside current, so it is not installed in place; nothing was installed"); }
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            entry.ExtractToFile(to, overwrite: true);
        }
        // The digest says which bytes, not that they are an app: one without its manifest (bundle.rs
        // read_manifest) or the app it names would leave current empty once mirrored.
        if (MainExe(staged) is not { } main || !File.Exists(Path.Combine(staged, main)))
        {
            throw new PackageMismatchException("the update has no manifest naming its app, or not that app, so it is not installed in place; nothing was installed");
        }
        return held;
    }

    static string? MainExe(string staged)
    {
        try
        {
            var doc = XDocument.Load(Path.Combine(staged, "sq.version"));
            string? Of(string name) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim() is { Length: > 0 } v ? v : null;
            return Of("id") is null || Of("version") is null || Of("mainExe") is not { } main || Path.GetFileName(main) != main ? null : main;
        }
        catch (Exception e) when (e is IOException or XmlException) { return null; }
    }

    // The app's whole folder, where neither Velopack's rename nor its stop reaches. The app is
    // self-contained, so its folder runs as it is (CI's self-test runs the published lib/app).
    public static string CopyHelper(string current, string to, string exe)
    {
        if (Directory.Exists(to)) { InPlaceApply.DeleteTree(new DirectoryInfo(to).Attributes.HasFlag(FileAttributes.ReparsePoint) ? throw new IOException($"{to} is a link; nothing was installed") : to); }
        foreach (var file in Directory.EnumerateFiles(current, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(to, Path.GetRelativePath(current, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }
        return Path.Combine(to, exe);
    }
}
