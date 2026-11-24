using System.Text.Json;
using AgentBridge.Host.Core;

namespace AgentBridge.Host.Delivery.Launch;

internal sealed class LaunchProvenance
{
    const int MaxBytes = 4 * 1024 * 1024;
    static readonly System.Text.RegularExpressions.Regex RoleName = new("^[A-Za-z0-9-]{1,64}$");
    // Wire timestamps lose sub-millisecond ticks; process identities must round-trip exactly.
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly string? path;
    readonly Action<string> log;
    readonly Lock gate = new();
    readonly Dictionary<Guid, Origin> origins = [];
    readonly HashSet<Guid> superseding = [];
    bool unavailable;
    // Role: the launch's own role name (a coordinator's role, #4318), kept so a later caller is judged by what was recorded, never by a name.
    sealed record Origin(string Session, SessionProcess? Process, string? Grant = null, string? Role = null);

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
            if (stored.Values.Any(o => o is null || o.Grant is not (null or "fleet") || o.Role is { } role && !RoleName.IsMatch(role) || o.Session is not { Length: <= 1024 } ||
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

    // supersedes: a fleet worker resumed in place (#4318): its earlier records go when this one is bound, and only if every one of
    // them is a fleet launch (a session a person ever launched is never taken over).
    public void Preparing(Guid request, string session = "", string? grant = null, string? role = null, bool supersedes = false)
    {
        lock (gate)
        {
            Check();
            if (supersedes)
            {
                var earlier = origins.Values.Where(o => string.Equals(o.Session, session, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (earlier.Length == 0 || grant != "fleet" || earlier.Any(o => o.Grant != "fleet" || o.Process is null))
                { throw new InvalidOperationException("refused: only a fleet launch's own session is resumed in place"); }
                superseding.Add(request);
            }
            origins[request] = new(session, null, grant, role);
            Save();
        }
    }

    public void Abandon(Guid request)
    {
        lock (gate) { Check(); origins.Remove(request); superseding.Remove(request); Save(); }
    }

    public void Bound(Guid request, string session, SessionProcess process)
    {
        lock (gate)
        {
            Check();
            var origin = new Origin(session, process, origins.GetValueOrDefault(request)?.Grant, origins.GetValueOrDefault(request)?.Role);
            if (origins.GetValueOrDefault(request) == origin) { return; }
            origins[request] = origin;
            if (superseding.Remove(request))
            {
                foreach (var old in origins.Where(e => e.Key != request && string.Equals(e.Value.Session, session, StringComparison.OrdinalIgnoreCase)).Select(e => e.Key).ToList()) { origins.Remove(old); }
            }
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

    public bool IsFleetWorker(string session, SessionProcess? process)
    {
        lock (gate)
        {
            return Contains(session, process) && origins.Values
                .Where(o => string.Equals(o.Session, session, StringComparison.OrdinalIgnoreCase))
                .All(o => o.Grant == "fleet" && o.Process == process);
        }
    }

    // Whether the record alone shows every launch of this session was a fleet launch, whether or not its process still runs
    // (a resume, #4318, is of a session that has ended). An unfinished launch with no durable binding throws, fail closed.
    public bool WasFleetLaunched(string session)
    {
        lock (gate)
        {
            Check();
            Unfinished(session);
            var mine = origins.Values.Where(o => string.Equals(o.Session, session, StringComparison.OrdinalIgnoreCase)).ToArray();
            return mine.Length > 0 && mine.All(o => o.Grant == "fleet" && o.Process is not null);
        }
    }

    // The one role every record of this session carries; none when there is no record, no role, or two roles.
    public string? RoleOf(string session)
    {
        lock (gate)
        {
            Check();
            Unfinished(session);
            var roles = origins.Values.Where(o => string.Equals(o.Session, session, StringComparison.OrdinalIgnoreCase)).Select(o => o.Role).Distinct().ToArray();
            return roles is [{ } role] ? role : null;
        }
    }

    // The role a launch may record (#4318, R10 guards): a person's launch (no agent principal) records what it names; an agent's
    // launch records it only when it equals the role the launching session itself was recorded with, so a session can never
    // set or raise its own role and a builder can never record a coordinator role. Anything unclear records none.
    public string? RoleFor(string? requested, string? agentPrincipal)
    {
        if (requested is null) { return null; }
        if (agentPrincipal is null) { return requested; }
        try { return RoleOf(agentPrincipal[(agentPrincipal.LastIndexOf('/') + 1)..]) is { } own && own == requested ? requested : null; }
        catch (InvalidOperationException) { return null; }
    }

    void Unfinished(string session)
    {
        if (origins.Values.Any(o => o.Process is null && (o.Session.Length == 0 || string.Equals(o.Session, session, StringComparison.OrdinalIgnoreCase))))
        { throw new InvalidOperationException("refused: an unfinished launch has no durable process binding"); }
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
