using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

// Admission only, not a launch grant. Dispatch remains held until trusted lifecycle wiring
// reserves before starting and releases only after proving the reserved process is gone.
internal sealed class FleetLaunchAdmission(string folder, Func<DateTimeOffset>? clock = null)
{
    internal const string FileName = "fleet-launch-admission.json";
    const int MaxEntries = 1024, MaxBytes = 512 * 1024;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    sealed record Reservation([property: JsonRequired] Guid Request, [property: JsonRequired] string Principal,
        [property: JsonRequired] string Binding, [property: JsonRequired] DateTimeOffset At, [property: JsonRequired] bool Live);
    sealed record State([property: JsonRequired] int Version, [property: JsonRequired] DateTimeOffset LastSeen,
        [property: JsonRequired] List<Reservation> Entries);

    internal AgentLimitVerdict Reserve(string principal, LaunchRequest request)
    {
        if (!ValidPrincipal(principal) || !Guid.TryParseExact(request.Request, "D", out var id) || id == Guid.Empty)
        { return new(false, "fleet admission needs a script principal and request UUID", 0); }
        var (policy, problem) = HostSettings.CurrentAgents(folder);
        if (problem is not null || !policy.Enabled) { return new(false, problem ?? "agents.enabled is off", 0); }
        return Change(create: true, (state, now) =>
        {
            var binding = LaunchService.RequestBinding(request);
            if (state.Entries.FirstOrDefault(e => e.Request == id) is { } known)
            {
                var duplicate = known.Principal == principal && known.Binding == binding;
                return (new(false, duplicate ? "duplicate" : "requestReused", 0, duplicate), false);
            }
            if (state.Entries.Count(e => e.Live) >= policy.Limits.Launch.LiveFleetWorkers)
            { return (new(false, "fleet live worker limit reached; wait for proven worker exit", 0), false); }
            var hourly = state.Entries.Where(e => now - e.At < TimeSpan.FromHours(1)).OrderBy(e => e.At).ToArray();
            if (hourly.Length >= policy.Limits.Launch.FleetPerHour)
            {
                var oldest = hourly[hourly.Length - policy.Limits.Launch.FleetPerHour].At;
                return (new(false, "fleet hourly launch limit reached",
                    Math.Max(1, (int)Math.Ceiling((TimeSpan.FromHours(1) - (now - oldest)).TotalSeconds))), false);
            }
            state.Entries.RemoveAll(e => !e.Live && now - e.At >= TimeSpan.FromHours(25));
            if (state.Entries.Count >= MaxEntries) { return (new(false, "fleet admission history is full", 0), false); }
            state.Entries.Add(new(id, principal, binding, now, true));
            return (AgentLimitVerdict.Admit, true);
        });
    }

    // Trusted lifecycle seam only; never exposed as a caller-requested release operation.
    internal string? Release(string principal, string request)
    {
        if (!ValidPrincipal(principal) || !Guid.TryParseExact(request, "D", out var id) || id == Guid.Empty)
        { return "fleet release needs its original script principal and request UUID"; }
        var result = Change(create: false, (state, _) =>
        {
            var index = state.Entries.FindIndex(e => e.Request == id && e.Principal == principal);
            if (index < 0) { return (new(false, "fleet reservation does not belong to this principal", 0), false); }
            state.Entries[index] = state.Entries[index] with { Live = false };
            return (AgentLimitVerdict.Admit, true);
        });
        return result.Allowed ? null : result.Reason;
    }

    AgentLimitVerdict Change(bool create, Func<State, DateTimeOffset, (AgentLimitVerdict Verdict, bool Save)> change)
    {
        try
        {
            var path = Path.Combine(PrivateFolder.Ensure(folder, "fleet admission storage", "fleet launches are refused"), FileName);
            var fresh = false;
            FileStream stream;
            if (create)
            {
                try { stream = new(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None); fresh = true; }
                catch (IOException) when (File.Exists(path)) { stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            }
            else { stream = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            using (stream)
            {
                if (fresh)
                {
                    using var initialized = new FileStream(path + ".initialized", FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    initialized.WriteByte(1);
                    initialized.Flush(flushToDisk: true);
                }
                else if (!File.Exists(path + ".initialized")) { throw new InvalidDataException(); }
                if (stream.Length > MaxBytes) { throw new InvalidDataException(); }
                var now = (clock ?? (() => DateTimeOffset.UtcNow))();
                var state = fresh ? new State(1, now, []) : JsonSerializer.Deserialize<State>(stream, Json);
                if (state is null || state.Version != 1 || state.LastSeen == default || state.Entries is null
                    || state.Entries.Count > MaxEntries || state.Entries.Any(e => e is null || e.Request == Guid.Empty
                        || !ValidPrincipal(e.Principal) || e.Binding is not { Length: 64 } || !e.Binding.All(Uri.IsHexDigit)
                        || e.At == default || e.At > state.LastSeen)
                    || state.Entries.Select(e => e.Request).Distinct().Count() != state.Entries.Count)
                { throw new InvalidDataException(); }
                // Persist the high-water clock, so a rollback cannot age reservations out early.
                if (now < state.LastSeen) { now = state.LastSeen; }
                var (verdict, save) = change(state, now);
                if (save)
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(state with { LastSeen = now }, Json);
                    if (bytes.Length > MaxBytes) { throw new InvalidDataException(); }
                    stream.Position = 0;
                    stream.Write(bytes);
                    stream.SetLength(bytes.Length);
                    stream.Flush(flushToDisk: true);
                }
                return verdict;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { return new(false, "fleet admission storage is unavailable or invalid; no launch is admitted", 0); }
    }

    static bool ValidPrincipal(string principal) =>
        principal is { Length: > 7 and <= AgentAuditLog.MaxField } && principal.StartsWith("script:", StringComparison.Ordinal);
}
