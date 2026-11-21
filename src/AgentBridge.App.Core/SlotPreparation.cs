using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

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

    internal static (Dictionary<string, ZipArchiveEntry> Files, string Identity) Payload(ZipArchive zip, string version, Func<string, string?> releaseIdentity)
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
        foreach (var required in Required) { Require(files, required); }
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
            using var stream = new PayloadStream(files[executable + ".deps.json"]);
            using var deps = JsonDocument.Parse(stream);
            var target = deps.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
            if ((!target.EndsWith("/win-x64", StringComparison.Ordinal) && !target.EndsWith("/win-arm64", StringComparison.Ordinal))
                || (runtimeTarget is not null && runtimeTarget != target))
            { throw new InvalidDataException("Slot runtime closures must target the same Windows architecture."); }
            runtimeTarget = target;
            foreach (var library in deps.RootElement.GetProperty("targets").GetProperty(target).EnumerateObject())
            foreach (var group in library.Value.EnumerateObject().Where(g => g.Name is "runtime" or "native" or "resources" or "runtimeTargets"))
            foreach (var asset in group.Value.EnumerateObject())
            {
                if (group.Name == "runtimeTargets" && asset.Value.GetProperty("rid").GetString() != target.Split('/')[^1]) { continue; }
                var name = asset.Name.Split('/')[^1];
                if (name == "_._") { continue; }
                Require(files, group.Name == "resources" ? asset.Value.GetProperty("locale").GetString() + "/" + name : name);
            }
        }
        return (files, "AgentBridgeApp/" + actualVersion);
    }

    static void Require(Dictionary<string, ZipArchiveEntry> files, string name)
    {
        if (!files.TryGetValue(name.Replace('/', Path.DirectorySeparatorChar), out var entry) || entry.Length == 0)
        { throw new InvalidDataException($"Slot payload is incomplete: missing or empty {name}."); }
    }

    static bool SafeRelative(string path) => path.Length > 0 && !path.Contains('\\')
        && path.Split('/').All(p => p.Length > 0 && p is not "." and not ".." && !p.EndsWith('.')
            && !p.EndsWith(' ') && !p.Any(c => c < 32 || "<>:\"|?*~".Contains(c))
            && !Regex.IsMatch(p, @"\A(?:CON(?:IN\$|OUT\$)?|PRN|AUX|NUL|(?:COM|LPT)[0-9\u00B9\u00B2\u00B3])(?:\.|\z)", RegexOptions.IgnoreCase));

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
}
