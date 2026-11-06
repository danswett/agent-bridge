using System.Security.Cryptography;

namespace AgentBridge.App.Core;

// A package's checksum as its release feed gives it: SHA-256 where the feed has one, else SHA-1,
// as Velopack's releases list carries them (VelopackAsset.SHA256, SHA1), in hex.
public sealed record PackageDigest(string Algorithm, string Hex)
{
    public const string Sha256 = "SHA256";
    public const string Sha1 = "SHA1";

    // Null when the feed names neither: such a package is never verified, so never offered.
    public static PackageDigest? Of(string? sha256, string? sha1) =>
        !string.IsNullOrWhiteSpace(sha256) ? new(Sha256, sha256.Trim()) : !string.IsNullOrWhiteSpace(sha1) ? new(Sha1, sha1.Trim()) : null;

    public bool Matches(Stream bytes)
    {
        bytes.Position = 0;
        var hash = Algorithm switch
        {
            Sha256 => SHA256.HashData(bytes),
            Sha1 => SHA1.HashData(bytes),
            _ => null,
        };
        return hash is not null && string.Equals(Convert.ToHexString(hash), Hex, StringComparison.OrdinalIgnoreCase);
    }

    public bool Matches(string file)
    {
        try
        {
            using var bytes = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Matches(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}

// A package that is not the chosen release's bytes: never marked, never installed.
public sealed class PackageMismatchException(string message) : IOException(message);

// Which source (the feed, and the pre-release choice) the release last downloaded came from, and
// the checksum its package was verified against (#516). Velopack keeps the package in its folder
// across starts whatever the source selected since, so a start offers a package an earlier run
// downloaded only when this says it came from the source selected now and the file is still those
// bytes. One that cannot be read vouches for nothing.
//
// Velopack (1.2.161) takes a package already in its folder under the release's file name for the
// release without checking its bytes, so one downloaded from another feed with the same name and
// version would pass for the chosen one (Engineering's second-family review of #719, 6676612221).
// So the bytes are checked here, against the chosen release's checksum from its feed, before they
// are marked, and against the mark before they are offered after a restart and before they are
// installed.
public sealed class DownloadMark(string file)
{
    // Before a download: a package already there that is not the chosen release's bytes goes, so
    // the download fetches the release from the chosen source rather than reusing it.
    public static void DropMismatch(string package, PackageDigest expected)
    {
        try
        {
            if (File.Exists(package) && !expected.Matches(package)) { File.Delete(package); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new PackageMismatchException($"a package already downloaded under the release's name is not its bytes and could not be removed: {e.Message}");
        }
    }

    // After a download: marked only when the file is the chosen release's bytes. Otherwise it goes
    // (when it can), and the download has failed.
    public void Accept(string package, string version, string source, PackageDigest expected)
    {
        if (!expected.Matches(package))
        {
            try { File.Delete(package); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Clear();
            throw new PackageMismatchException($"the package downloaded for {version} does not match its release's {expected.Algorithm} checksum");
        }
        Save(version, source, expected);
    }

    // Offered after a restart: the mark's version and source, and the file still its bytes.
    public bool Vouches(string package, string version, string source) =>
        Read() is { } m && m.Version == version && m.Source == source && m.Digest.Matches(package);

    // Just before install: the file, held open so it cannot be changed or replaced until this
    // process ends, and checked from that handle against the mark. The installer opens it by its
    // path once the app has exited, after the hold. Null when it is not the bytes the mark vouches for.
    public FileStream? Hold(string package, string version, string source)
    {
        if (Read() is not { } m || m.Version != version || m.Source != source) { return null; }
        FileStream held;
        try { held = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (m.Digest.Matches(held)) { held.Position = 0; return held; }
        held.Dispose();
        return null;
    }

    void Save(string version, string source, PackageDigest digest)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, $"{version}\n{source}\n{digest.Algorithm}\n{digest.Hex}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    void Clear()
    {
        try { File.Delete(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // A mark from before the checksum (version and source only) vouches for nothing: its package is
    // downloaded again.
    (string Version, string Source, PackageDigest Digest)? Read()
    {
        try
        {
            if (!File.Exists(file)) { return null; }
            var lines = File.ReadAllText(file).Split('\n');
            return lines.Length == 4 && lines[2] is PackageDigest.Sha256 or PackageDigest.Sha1 && lines[3].Length > 0
                ? (lines[0], lines[1], new PackageDigest(lines[2], lines[3]))
                : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}