using System.Text.Json;
using AgentBridge.Host.Core;

namespace AgentBridge.Host.Delivery.Launch;

// The fleet route's admission limits: launches an hour and workers running at once (#2770).
public readonly record struct FleetLimits(int PerHour, int Live);

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
    sealed record Origin(string Session, SessionProcess? Process, string? Grant = null, string? Role = null, DateTimeOffset? ReservedAt = null);

    // How long a fleet reservation that never got a process counts against the caps: the longest follow window (a folder-trust question waits up to 30 minutes)
    // plus 10 minutes. After it the row stays (so an unregistered process that is still running is never adoptable) but no longer counts, which is also how a restart
    // frees a reservation its host crashed on. A proven failure releases it at once (ReleaseFleetReservation).
    public static readonly TimeSpan FleetReservationLifetime = TimeSpan.FromMinutes(40);

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

    // A NEW fleet launch (never a resume) is admitted against the hourly and live caps and reserved in the SAME locked step, so two at once cannot both
    // take the last slot, and the count is this durable record, so a restart cannot reset it. An unfinished reservation (no process yet) counts against both.
    // Returns the refusal, or null when admitted and reserved (the same row Preparing writes). Coordinator and person launches are never counted.
    public string? ReserveFleet(Guid request, string session, string? role, FleetLimits limits, DateTimeOffset now, Func<string, SessionProcess?> processOf)
    {
        if (limits.PerHour < 1 || limits.Live < 1) { throw new ArgumentOutOfRangeException(nameof(limits), "a fleet limit is at least 1"); }
        lock (gate)
        {
            Check();
            var fleet = origins.Where(e => e.Key != request && e.Value.Grant == "fleet" && (e.Value.Process is not null || e.Value.ReservedAt is { } reserved && now - reserved < FleetReservationLifetime))
                .Select(e => e.Value).ToList();
            var windowStart = now - TimeSpan.FromHours(1);
            if (fleet.Count(o => o.Process?.StartedAt is not { } at || at > windowStart) >= limits.PerHour)
            { return $"fleet launch limit: {limits.PerHour} an hour on this machine; the next slot opens when the oldest of them is an hour old (limits in force: {limits.PerHour} an hour, {limits.Live} live)"; }
            if (fleet.Count(o => o.Process is null || o.Session.Length > 0 && processOf(o.Session) == o.Process) >= limits.Live)
            { return $"fleet launch limit: {limits.Live} live fleet workers on this machine; one must end first (limits in force: {limits.PerHour} an hour, {limits.Live} live)"; }
            origins[request] = new(session, null, "fleet", role, now);
            Save();
            return null;
        }
    }

    // The worker is proven gone before its session registered (the launch failed): its reservation is released, so it stops counting at once. Only an unbound
    // fleet reservation is released; a launch that has a process is never touched.
    public bool ReleaseFleetReservation(Guid request)
    {
        lock (gate)
        {
            Check();
            if (!origins.TryGetValue(request, out var origin) || origin.Grant != "fleet" || origin.Process is not null || origin.ReservedAt is null) { return false; }
            origins.Remove(request);
            Save();
            return true;
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

    // The role a launch may record (#4318): only a person's launch (no agent principal) records what it names. Owner decision of
    // Nov 23, about 19:52 PT, item 2: NEW coordinator sessions still need the owner's direct approval, so an agent's launch of a
    // new session records no role at all, even its own: a coordinator is never minted by an agent. A role survives an agent's
    // action only on the same session resumed in place (RoleKept).
    public string? RoleFor(string? requested, string? agentPrincipal) => agentPrincipal is null ? requested : null;

    // The role a resumed or restored session keeps (#4318): what its own earlier record says, so a resume never drops the role it
    // is judged by. None when unknown or unreadable.
    public string? RoleKept(string session)
    {
        try { return RoleOf(session); }
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
