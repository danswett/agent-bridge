namespace AgentBridge.App.Core;

// How the package checked against its release reaches Velopack's installer (#984). Update.exe opens
// the package it installs by its path, once the app has exited (Velopack 1.2.161: UpdateExe.Apply
// names PackagesDir\<file name>, and apply.rs opens it after --waitPid). The packages folder is the
// installation's, shared by every profile's app and host, so another profile could write a valid
// package of the same version there, from its own feed, after this app checked it and before the
// installer opened it (#719, 6677353697). So the installer is handed a copy in this profile's own
// folder, which no other profile's updater writes: copied from the handle that checked the shared
// package, and checked again against the same digest from the handle that keeps it, read-only to
// everyone else, until this process ends. The shared packages folder is still the one named for
// --packageDir: Update.exe takes its install lock there (packages\.velopack_lock), which keeps the
// profiles from installing over each other.
public sealed class PackageHandoff(DownloadMark mark, string folder)
{
    // The copy for the installer, and the digest it was checked against.
    public sealed record Copy(string Package, PackageDigest Digest);

    // The shared package, if it is still the bytes the mark vouches for, copied into this profile's
    // folder from the handle that checked it. Copies left by an earlier hand-over go first.
    public Copy Prepare(string shared, string version, string source)
    {
        var digest = mark.DigestFor(version, source)
            ?? throw new PackageMismatchException($"no package downloaded for {version} from the source selected now has been checked; nothing was installed");
        using var held = DownloadMark.Hold(shared, digest)
            ?? throw new PackageMismatchException($"the package downloaded for {version} is no longer the bytes verified for it; nothing was installed, so check for updates again");
        Clear();
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, Path.GetFileName(shared));
        using (var to = new FileStream(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            held.CopyTo(to);
            to.Flush(flushToDisk: true);
        }
        return new Copy(copy, digest);
    }

    // Just before the installer starts: the copy checked again, from the handle that keeps it
    // unchanged until this process ends (the caller keeps it), then start(Update.exe's arguments).
    // Nothing starts when the copy is not those bytes.
    public FileStream Launch(Copy copy, uint waitPid, string rootDir, string packagesDir, IReadOnlyList<string> restartArgs, Action<IReadOnlyList<string>> start)
    {
        var held = DownloadMark.Hold(copy.Package, copy.Digest)
            ?? throw new PackageMismatchException("the copy of the update made for the installer is no longer the bytes verified for it; nothing was installed, so check for updates again");
        try
        {
            start(Arguments(copy.Package, waitPid, rootDir, packagesDir, restartArgs));
            return held;
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    // Update.exe's apply, as Velopack's UpdateExe.Apply builds it for ApplyUpdatesAndRestart (not
    // silent, restarting), but with --package naming the copy: the shared packages folder and the
    // installation's root are Velopack's own.
    public static IReadOnlyList<string> Arguments(string package, uint waitPid, string rootDir, string packagesDir, IReadOnlyList<string> restartArgs)
    {
        List<string> args = ["apply", "--package", package, "--waitPid", waitPid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--rootDir", rootDir, "--packageDir", packagesDir];
        if (restartArgs.Count > 0) { args.Add("--"); args.AddRange(restartArgs); }
        return args;
    }

    // An earlier hand-over's copy (installed since, or refused): about the size of a release, so
    // not kept. One this process still holds is left.
    public void Clear()
    {
        try
        {
            if (!Directory.Exists(folder)) { return; }
            foreach (var old in Directory.EnumerateFiles(folder))
            {
                try { File.Delete(old); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
