using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;

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

    // updateExe: Update.exe run with these arguments, its exit code. The package is held and checked against the
    // digest the app verified, and staged from that handle. True when installed in place, false when apply took.
    public bool Install(string staged, Func<IReadOnlyList<string>, int> updateExe, Func<IReadOnlyList<string>> held, Action<string>? log = null)
    {
        using var package = DownloadMark.Hold(Package, Digest)
            ?? throw new PackageMismatchException("the update handed over is no longer the bytes verified for it; nothing was installed");
        try
        {
            return InPlaceApply.Install(() => updateExe(PackageHandoff.Arguments(Package, Root, Packages, [], wait: false)) == 0,
                () => held() is { Count: > 0 } files ? Extract(package, staged, files) : [],
                staged, Path.Combine(Root, "current"), Path.Combine(Root, "current-inplace"), log);
        }
        finally { try { if (Directory.Exists(staged)) { InPlaceApply.DeleteTree(staged); } } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
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
        return held;
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
