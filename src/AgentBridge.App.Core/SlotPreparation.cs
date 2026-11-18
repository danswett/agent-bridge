using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentBridge.Host.Delivery.Staging;

namespace AgentBridge.App.Core;

public static class SlotPreparation
{
    public sealed record Prepared(string RelativePath, string Version, PackageDigest Digest, string ManifestIdentity);
    static readonly string[] Required = ["AgentBridge.exe", "AgentBridge.pri", "agent-bridge-host.exe",
        "agent-bridge-hostw.exe", "agent-bridge-injector.exe", "agent-bridge-host-hook.exe",
        "AgentBridge.dll", "AgentBridge.deps.json", "AgentBridge.runtimeconfig.json",
        "agent-bridge-host.dll", "agent-bridge-host.deps.json", "agent-bridge-host.runtimeconfig.json",
        "agent-bridge-hostw.dll", "agent-bridge-hostw.deps.json", "agent-bridge-hostw.runtimeconfig.json",
        "Assets/AgentBridge.ico", "Assets/AgentBridge.png", "Assets/Fonts/SlipgatesSans.ttc",
        "Assets/Fonts/SlipgatesSans-OFL.txt", "Assets/Fonts/SlipgatesMono.ttc", "Assets/Fonts/SlipgatesMono-OFL.txt",
        "coreclr.dll", "hostfxr.dll", "hostpolicy.dll", "System.Private.CoreLib.dll"];

    // Preparation has no activation side effects. Existing slots are only read, never repaired.
    public static Prepared Prepare(string root, string version, string package, PackageDigest digest, Func<string, string?> releaseIdentity)
    {
        if (!Regex.IsMatch(version, @"\A[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z")
            || version.Split('-')[0].Split('+')[0] == "0.0.0"
            || !Regex.IsMatch(digest.Hex, digest.Algorithm switch { PackageDigest.Sha256 => @"\A[0-9a-fA-F]{64}\z", PackageDigest.Sha1 => @"\A[0-9a-fA-F]{40}\z", _ => @"\b\B" }))
        { throw new InvalidDataException("A slot requires a known relative release identity and release package digest."); }
        var relative = Path.Combine("slots", $"{version}-{digest.Hex.ToLowerInvariant()}");
        var destination = Path.Combine(Path.GetFullPath(root), relative);
        NoLinks(destination);
        NoLinks(Path.GetFullPath(package));
        using var held = DownloadMark.Hold(package, digest)
            ?? throw new PackageMismatchException("Slot preparation refused: package bytes do not match the release digest.");
        using var zip = new ZipArchive(held, ZipArchiveMode.Read);
        var (files, manifestIdentity, runtime, bundledHostw) = Payload(zip, version, releaseIdentity);
        Prepared Winner()
        {
            Validate(destination, files, runtime, bundledHostw);
            return new(relative, version, digest, manifestIdentity);
        }
        if (Directory.Exists(destination)) { return Winner(); }
        if (File.Exists(destination)) { throw new InvalidDataException("A file already occupies the slot path."); }
        var slots = Path.GetDirectoryName(destination)!;
        NoLinks(slots);
        Directory.CreateDirectory(slots);
        var staging = Path.Combine(slots, ".preparing-" + Guid.NewGuid().ToString("N"));
        PrivateFolder.Ensure(staging, "slot staging", "no slot is prepared");
        try
        {
            foreach (var (name, entry) in files)
            {
                var target = Path.Combine(staging, name);
                NoLinks(target);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = new PayloadStream(entry);
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                input.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            Validate(staging, files, runtime, bundledHostw);
            NoLinks(destination);
            try { Directory.Move(staging, destination); }
            catch (IOException) when (Directory.Exists(destination)) { return Winner(); }
            return new(relative, version, digest, manifestIdentity);
        }
        finally
        {
            if (Directory.Exists(staging)) { Directory.Delete(staging, recursive: true); }
        }
    }

    static (Dictionary<string, ZipArchiveEntry> Files, string Identity, string Runtime, bool BundledHostw) Payload(ZipArchive zip, string version, Func<string, string?> releaseIdentity)
    {
        const string prefix = "lib/app/";
        if (zip.Entries.Count > 10000) { throw new InvalidDataException("Slot archive exceeds the entry limit."); }
        long expanded = 0;
        var files = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            if (entry.Length < 0 || entry.Length > 512L * 1024 * 1024 || expanded > 1024L * 1024 * 1024 - entry.Length)
            { throw new InvalidDataException("Slot archive exceeds the expanded size limits."); }
            expanded += entry.Length;
            var path = entry.FullName.TrimEnd('/');
            if (!SafeRelative(path) || !names.Add(path)
                || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0
                || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000)
            { throw new InvalidDataException($"Slot preparation refused unsafe archive entry: {entry.FullName}"); }
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) || entry.FullName.EndsWith('/')) { continue; }
            files.Add(entry.FullName[prefix.Length..].Replace('/', Path.DirectorySeparatorChar), entry);
        }
        var hostwSidecars = Required.Where(n => n.StartsWith("agent-bridge-hostw.", StringComparison.Ordinal) && !n.EndsWith(".exe", StringComparison.Ordinal)).ToArray();
        var bundledHostw = hostwSidecars.All(n => !files.ContainsKey(n));
        foreach (var required in Required.Where(n => !bundledHostw || !hostwSidecars.Contains(n))) { Require(files, required); }
        var manifests = zip.Entries.Where(e => !e.FullName.Contains('/') && e.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (manifests.Length != 1) { throw new InvalidDataException("Slot package requires its release manifest."); }
        using var manifest = new PayloadStream(manifests[0]);
        var metadata = XDocument.Load(manifest).Root?.Elements().SingleOrDefault(e => e.Name.LocalName == "metadata");
        if (metadata?.Elements().SingleOrDefault(e => e.Name.LocalName == "id")?.Value != "AgentBridgeApp"
            || metadata?.Elements().SingleOrDefault(e => e.Name.LocalName == "version")?.Value is not { } actualVersion
            || !string.Equals(releaseIdentity(actualVersion), version, StringComparison.OrdinalIgnoreCase))
        { throw new InvalidDataException("Slot package identity/version does not match the requested release."); }
        string? runtimeTarget = null;
        foreach (var executable in new[] { "AgentBridge", "agent-bridge-host", "agent-bridge-hostw" })
        {
            if (executable == "agent-bridge-hostw" && bundledHostw) { continue; } // Its embedded closure is mandatory after extraction.
            using var stream = new PayloadStream(files[executable + ".deps.json"]);
            using var deps = JsonDocument.Parse(stream);
            var target = deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
            if ((!target.EndsWith("/win-x64", StringComparison.Ordinal) && !target.EndsWith("/win-arm64", StringComparison.Ordinal))
                || (runtimeTarget is not null && runtimeTarget != target))
            { throw new InvalidDataException("Slot runtime closures must target the same Windows architecture."); }
            runtimeTarget = target;
            foreach (var (name, _) in RuntimeAssets(deps.RootElement, target)) { Require(files, name); }
        }
        return (files, "AgentBridgeApp/" + actualVersion, runtimeTarget!, bundledHostw);
    }

    internal static IEnumerable<(string Name, byte Type)> RuntimeAssets(JsonElement deps, string target)
    {
        var libraries = deps.GetProperty("targets").GetProperty(target);
        foreach (var library in libraries.EnumerateObject())
        {
            if (library.Value.TryGetProperty("dependencies", out var dependencies))
            foreach (var dependency in dependencies.EnumerateObject())
            { if (!libraries.TryGetProperty(dependency.Name + "/" + dependency.Value.GetString(), out _)) { throw new InvalidDataException("Missing runtime dependency: " + dependency.Name); } }
            foreach (var group in library.Value.EnumerateObject().Where(g => g.Name is "runtime" or "native" or "resources" or "runtimeTargets"))
            foreach (var asset in group.Value.EnumerateObject())
            {
                if (group.Name == "runtimeTargets" && asset.Value.GetProperty("rid").GetString() != target.Split('/')[^1]) { continue; }
                var name = asset.Name.Split('/')[^1];
                if (name == "_._") { continue; }
                yield return (group.Name == "resources" ? asset.Value.GetProperty("locale").GetString() + "/" + name : name,
                    group.Name == "native" || group.Name == "runtimeTargets" && asset.Value.GetProperty("assetType").GetString() == "native" ? (byte)2 : (byte)1);
            }
        }
    }

    static void Require(Dictionary<string, ZipArchiveEntry> files, string name)
    {
        if (!files.TryGetValue(name.Replace('/', Path.DirectorySeparatorChar), out var entry) || entry.Length == 0)
        { throw new InvalidDataException($"Slot payload is incomplete: missing or empty {name}."); }
    }

    internal static bool SafeRelative(string path) => path.Length > 0 && !path.Contains('\\')
        && path.Split('/').All(p => p.Length > 0 && p is not "." and not ".." && !p.EndsWith('.')
            && !p.EndsWith(' ') && !p.Any(c => c < 32 || "<>:\"|?*~".Contains(c))
            && !Regex.IsMatch(p, @"\A(?:CON(?:IN\$|OUT\$)?|PRN|AUX|NUL|(?:COM|LPT)[0-9\u00B9\u00B2\u00B3])(?:\.|\z)", RegexOptions.IgnoreCase));

    static void Validate(string directory, Dictionary<string, ZipArchiveEntry> expected, string runtime, bool bundledHostw)
    {
        var pending = new Stack<string>([directory]);
        var count = 0;
        while (pending.TryPop(out var folder))
        {
            NoLinks(folder);
            foreach (var path in Directory.EnumerateFileSystemEntries(folder))
            {
                NoLinks(path); // Check before descending: recursive enumeration would follow a junction.
                var name = Path.GetRelativePath(directory, path);
                if (Directory.Exists(path))
                {
                    if (!expected.Keys.Any(n => n.StartsWith(name + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
                    { throw new InvalidDataException($"Existing slot has an unexpected directory: {name}"); }
                    pending.Push(path); continue;
                }
                if (!expected.TryGetValue(name, out var entry))
                { throw new InvalidDataException($"Existing slot has an unexpected file: {name}"); }
                using var actual = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var source = new PayloadStream(entry);
                var digest = new PackageDigest(PackageDigest.Sha256, Convert.ToHexString(SHA256.HashData(source)));
                if (actual.Length != entry.Length || !digest.Matches(actual))
                { throw new InvalidDataException($"Slot payload differs from the verified package: {name}"); }
                if (bundledHostw && string.Equals(name, "agent-bridge-hostw.exe", StringComparison.OrdinalIgnoreCase)) { HostwBundle.Verify(actual, digest, runtime); }
                count++;
            }
        }
        if (count != expected.Count) { throw new InvalidDataException("Slot payload is incomplete."); }
    }

    sealed class PayloadStream(ZipArchiveEntry entry) : Stream
    {
        readonly Stream source = entry.Open();
        long remaining = entry.Length;
        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            if (buffer.IsEmpty) { return 0; }
            var read = source.Read(buffer[..(int)Math.Min(buffer.Length, remaining + 1)]);
            if (read > remaining || read == 0 && remaining != 0)
            { throw new InvalidDataException("Slot archive content differs from its declared size."); }
            remaining -= read;
            return read;
        }
        protected override void Dispose(bool disposing) { if (disposing) { source.Dispose(); } base.Dispose(disposing); }
        public override bool CanRead => source.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length { get; } = entry.Length;
        public override long Position { get => Length - remaining; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    static void NoLinks(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            { throw new InvalidDataException($"Slot preparation refuses reparse points: {current}"); }
        }
    }
}
