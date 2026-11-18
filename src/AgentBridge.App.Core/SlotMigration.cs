using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Local;

namespace AgentBridge.App.Core;

public sealed class SlotMigration
{
    readonly string root, runtime;
    readonly Func<Stream, string?>? metadata;
    static readonly JsonSerializerOptions Json = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public SlotMigration(string root, string runtime) : this(root, runtime, null) { }
    internal SlotMigration(string root, string runtime, Func<Stream, string?>? metadata)
    { this.root = Path.GetFullPath(root); this.runtime = runtime; this.metadata = metadata; }

    public SlotPointer.Target Activate(string package, string version, PackageDigest digest,
        Action? beforeCommit = null, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        var packages = Path.Combine(root, "packages");
        SlotPreparation.NoLinks(packages);
        Directory.CreateDirectory(packages);
        var gate = Path.Combine(packages, ".velopack_lock");
        SlotPreparation.NoLinks(gate);
        using var writer = File.Open(gate, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var target = Prepare(package, version, digest);
        new SlotPointer(root).Publish(target, () => { beforeCommit?.Invoke(); cancel.ThrowIfCancellationRequested(); });
        return target;
    }

    public SlotPointer.Target Prepare(string package, string version, PackageDigest digest, Action<string>? checkpoint = null)
    {
        if (digest is not { Hex: not null } || digest.Hex.Length != (digest.Algorithm switch
            { PackageDigest.Sha256 => 64, PackageDigest.Sha1 => 40, _ => -1 }) || !digest.Hex.All(Uri.IsHexDigit))
        { throw new InvalidDataException("Invalid migration package digest."); }
        SlotPreparation.NoLinks(root);
        SlotPreparation.NoLinks(package = Path.GetFullPath(package));
        using var source = DownloadMark.Hold(package, digest)
            ?? throw new PackageMismatchException("Migration package differs from the verified release.");
        digest = new(PackageDigest.Sha256, Convert.ToHexString(SHA256.HashData(source)));
        var gate = Path.Combine(root, ".migration.lock");
        SlotPreparation.NoLinks(gate);
        using var writer = new FileStream(gate, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var cache = Path.Combine(root, "packages");
        SlotPreparation.NoLinks(cache);
        Directory.CreateDirectory(cache);
        var name = $"slot-{digest.Algorithm}-{digest.Hex.ToLowerInvariant()}.nupkg";
        Copy(source, Path.Combine(cache, name), digest);
        version = UpdateVersions.Identity(version) ?? throw new InvalidDataException("Missing migration release identity.");
        var target = new SlotPointer.Target(SlotPreparation.Prepare(root, version, Path.Combine(cache, name),
            digest, UpdateVersions.Identity), "packages/" + name);
        var pointers = new SlotPointer(root);
        using var prepared = pointers.Pin(target);
        using (var candidate = Image(prepared)) { }
        var ledger = Path.Combine(root, "stable-entry.json");
        SlotPreparation.NoLinks(ledger);
        SlotPointer.Target origin;
        if (File.Exists(ledger))
        {
            using var file = new FileStream(ledger, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is <= 0 or > 8192) { throw new InvalidDataException("Invalid bootstrap provenance size."); }
            using var document = JsonDocument.Parse(file, new() { AllowDuplicateProperties = false, MaxDepth = 8 });
            origin = document.RootElement.Deserialize<SlotPointer.Target>(Json)
                ?? throw new InvalidDataException("Missing bootstrap provenance.");
        }
        else
        {
            origin = target;
            var bytes = JsonSerializer.SerializeToUtf8Bytes(origin, Json);
            using var input = new MemoryStream(bytes, writable: false);
            Copy(input, ledger, new(PackageDigest.Sha256, Convert.ToHexString(SHA256.HashData(bytes))));
        }
        checkpoint?.Invoke("ledger");
        // The first verified bootstrap stays immutable while root images may be running.
        using var retained = pointers.Pin(origin);
        using var image = Image(retained);
        var imageDigest = new PackageDigest(PackageDigest.Sha256, Convert.ToHexString(SHA256.HashData(image)));
        Copy(image, Path.Combine(root, "agent-bridge-hostw.exe"), imageDigest);
        checkpoint?.Invoke("host-alias");
        Copy(image, Path.Combine(root, "AgentBridge.exe"), imageDigest,
            replaceLegacy: !File.Exists(Path.Combine(root, "active-slot.json")));
        checkpoint?.Invoke("app-alias");
        return target;
    }

    FileStream Image(SlotPointer.Snapshot slot)
    {
        var image = File.OpenRead(slot.EntryPoint("agent-bridge-hostw.exe"));
        try
        {
            var digest = new PackageDigest(PackageDigest.Sha256, Convert.ToHexString(SHA256.HashData(image)));
            HostwBundle.Verify(image, digest, runtime);
            CoordinatorImage.RequireSlotApplyV1(image, metadata);
            image.Position = 0;
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    static void Copy(Stream source, string destination, PackageDigest digest, bool replaceLegacy = false)
    {
        SlotPreparation.NoLinks(destination);
        if (File.Exists(destination))
        {
            using var existing = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (digest.Matches(existing)) { return; }
            if (!replaceLegacy) { throw new InvalidDataException("Existing bootstrap/cache differs from verified provenance."); }
        }
        var staging = Path.Combine(Path.GetDirectoryName(destination)!, ".bootstrap-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            source.Position = 0;
            using (var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                source.CopyTo(output);
                output.Flush(flushToDisk: true);
            }
            if (!digest.Matches(staging)) { throw new PackageMismatchException("Bootstrap/cache copy differs from its verified source."); }
            SlotPreparation.NoLinks(destination);
            if (replaceLegacy && File.Exists(destination)) { File.Replace(staging, destination, null); }
            else { File.Move(staging, destination); }
        }
        finally { File.Delete(staging); }
    }
}
