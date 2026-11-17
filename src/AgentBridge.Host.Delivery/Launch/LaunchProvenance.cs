using System.Text.Json;
using AgentBridge.Host.Core;

namespace AgentBridge.Host.Delivery.Launch;

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
    sealed record Origin(string Session, SessionProcess? Process, string? Grant = null);

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
            if (stored.Values.Any(o => o is null || o.Grant is not (null or "fleet") || o.Session is not { Length: <= 1024 } ||
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

    public void Preparing(Guid request, string session = "", string? grant = null)
    {
        lock (gate) { Check(); origins[request] = new(session, null, grant); Save(); }
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
            var origin = new Origin(session, process, origins.GetValueOrDefault(request)?.Grant);
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

    internal SessionProcess? ProcessFor(Guid request)
    {
        lock (gate) { Check(); return origins.GetValueOrDefault(request)?.Process; }
    }

    public bool IsFleetWorker(string session, SessionProcess? process)
    {
        lock (gate)
        {
            Check();
            if (origins.Values.Any(o => o.Grant == "fleet" && o.Process is null
                && (o.Session.Length == 0 || string.Equals(o.Session, session, StringComparison.OrdinalIgnoreCase))))
            { throw new InvalidOperationException("refused: a fleet launch has no durable process binding"); }
            return process is not null && origins.Values.Any(o => o.Grant == "fleet"
                && string.Equals(o.Session, session, StringComparison.OrdinalIgnoreCase) && o.Process == process);
        }
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
