using System.Text.Json;
using AgentBridge.Host.Core;

namespace AgentBridge.Host.Delivery.Launch;

// Set by verified host ingress, never inferred from request fields, peer names or session ids.
public sealed record LaunchAttribution(string Path, string ActorKind, string Principal, bool FleetController = false)
{
    internal bool IsValid => Path is "local" or "relay" or "legacy-ha"
        && ActorKind is "person-client" or "agent" or "ha-component"
        && !string.IsNullOrWhiteSpace(Principal) && Principal.Length <= 1024
        && (!FleetController || Path == "local" && ActorKind == "agent");
}

internal sealed class LaunchProvenance
{
    const int MaxBytes = 4 * 1024 * 1024;
    // Wire timestamps lose sub-millisecond ticks; process identities must round-trip exactly.
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly string? path;
    readonly Action<string> log;
    readonly Lock gate = new();
    readonly Dictionary<Guid, Origin> origins = [];
    bool unavailable;
    sealed record Origin(string Session, SessionProcess? Process, LaunchAttribution? Attribution = null);

    public LaunchProvenance(string? path, Action<string> log)
    {
        this.path = path;
        this.log = log;
        if (path is null) { return; }
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxBytes) { throw new InvalidDataException(); }
            var stored = JsonSerializer.Deserialize<Dictionary<Guid, Origin>>(stream, Json)
                ?? throw new InvalidDataException();
            if (stored.Values.Any(o => o is null || o.Session is not { Length: <= 1024 } || o.Attribution is { IsValid: false } ||
                (o.Process is not null && (o.Session.Length == 0 || o.Process is not { Id: > 0, StartedAt: not null }
                    || o.Process.StartedAt == default(DateTimeOffset)))))
            { throw new InvalidDataException(); }
            foreach (var entry in stored) { origins.Add(entry.Key, entry.Value); }
        }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            unavailable = true;
            log("launch provenance could not be read; adoption and further launches are refused");
        }
    }

    public void Preparing(Guid request, string session = "", LaunchAttribution? attribution = null)
    {
        if (attribution is { IsValid: false }) { throw new ArgumentException("launch attribution is invalid", nameof(attribution)); }
        lock (gate) { Check(); origins[request] = new(session, null, attribution); Save(); }
    }

    public void Abandon(Guid request)
    {
        lock (gate) { Check(); origins.Remove(request); Save(); }
    }

    public void Bound(Guid request, string session, SessionProcess process)
    {
        lock (gate)
        {
            Check();
            var origin = new Origin(session, process, origins.GetValueOrDefault(request)?.Attribution);
            if (origins.GetValueOrDefault(request) == origin) { return; }
            origins[request] = origin;
            Save();
        }
    }

    public bool Contains(string session, SessionProcess? process)
    {
        lock (gate)
        {
            Check();
            if (origins.Values.Any(o => o.Process is null && (o.Session.Length == 0 || string.Equals(o.Session, session, StringComparison.OrdinalIgnoreCase))))
            { throw new InvalidOperationException("refused: an unfinished launch has no durable process binding"); }
            return process is not null && origins.Values.Any(o => string.Equals(o.Session, session, StringComparison.OrdinalIgnoreCase) && o.Process == process);
        }
    }

    public bool FleetContains(string session, SessionProcess process)
    {
        lock (gate)
        {
            return Contains(session, process) && origins.Values.Where(o => string.Equals(o.Session, session, StringComparison.OrdinalIgnoreCase))
                .All(o => o.Process == process && o.Attribution is { FleetController: true, ActorKind: "agent" });
        }
    }

    internal LaunchAttribution? AttributionOf(Guid request)
    {
        lock (gate) { Check(); return origins.GetValueOrDefault(request)?.Attribution; }
    }

    void Check()
    {
        if (unavailable) { throw new InvalidOperationException("refused: launch provenance is unavailable"); }
    }

    void Save()
    {
        if (path is null) { return; }
        var partial = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(origins, Json);
            if (bytes.Length > MaxBytes) { throw new IOException("launch provenance exceeds its size limit"); }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(partial, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            unavailable = true;
            log("launch provenance could not be saved; adoption and further launches are refused");
            throw new InvalidOperationException("refused: launch provenance could not be saved", e);
        }
        finally { if (File.Exists(partial)) { File.Delete(partial); } }
    }
}
