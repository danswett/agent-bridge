using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Fleet;

public sealed partial class FleetLaunchNames
{
    readonly object gate = new();
    readonly Dictionary<Guid, Claim> pending = [];
    readonly ISessionSource source;
    readonly FleetNames names;
    readonly IFleetRoster roster;
    readonly string config;
    readonly Action<string> log;
    int warned;
    sealed record Claim(string Name, string Peer, HashSet<string> Before)
    {
        public bool Saved;
        public bool Failed;
    }

    public FleetLaunchNames(ISessionLauncher launcher, ISessionSource source, FleetNames names, IFleetRoster roster, string folder, Action<string> log)
    {
        this.source = source;
        this.names = names;
        this.roster = roster;
        this.log = log;
        config = Path.Combine(folder, HostSettings.FileName);
        launcher.RequestRegistered += Bind;
    }

    public async Task<LaunchResult> RunAsync(LaunchRequest request, string peer, Func<Task<LaunchResult>> start)
    {
        if (request.FleetName is not { } name) { return await start(); }
        if (request.Resume is not null || request.Transfer is not null) { return new(LaunchOutcome.Refused, "fleetName is only for a new session"); }
        if (!Allowed().IsMatch(name)) { return new(LaunchOutcome.Refused, "fleetName is outside the fixed SG name list"); }
        if (!Enabled()) { return new(LaunchOutcome.Refused, "fleetNames.launchRule is off or unreadable; named launches are disabled"); }
        var key = Guid.Parse(request.Request);
        var claim = new Claim(name, peer, source.Snapshot().Select(s => s.Id).ToHashSet(StringComparer.Ordinal));
        lock (gate)
        {
            if (pending.Values.Any(c => c.Name == name) || source.Snapshot().Any(s => names.Get(s.Id) == name))
            {
                return new(LaunchOutcome.Refused, "fleetName is already held by a live or starting session");
            }
            pending.Add(key, claim);
        }
        var retain = false;
        try
        {
            IReadOnlyList<FleetMachine> machines = [];
            try
            {
                machines = await roster.ReadAsync(CancellationToken.None);
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or OperationCanceledException or JsonException or System.Net.WebSockets.WebSocketException)
            {
                log("fleet name launch: account registry unreadable; checking local live names only");
            }
            if (machines.Any(m => m.Online && m.Sessions.Any(s => s.Name == name)))
            {
                return new(LaunchOutcome.Refused, "fleetName is already held by a live account session");
            }
            var result = await start();
            if (result is { Outcome: LaunchOutcome.Started, Session: { } id }) { Bind(key, id); }
            lock (gate)
            {
                retain = !claim.Saved && (result.Outcome == LaunchOutcome.Unconfirmed || claim.Failed);
                return claim.Failed ? result with { Outcome = LaunchOutcome.Unconfirmed, Reason = "session started, but its fleet name was not saved" } : result;
            }
        }
        finally { if (!retain) { lock (gate) { pending.Remove(key); } } }
    }

    void Bind(Guid key, string id)
    {
        lock (gate)
        {
            if (!pending.TryGetValue(key, out var claim) || claim.Saved || claim.Failed) { return; }
            try
            {
                if (claim.Before.Contains(id) || !names.TryNameNew(source, id, claim.Name)) { throw new ArgumentException("not a new unnamed session"); }
                claim.Saved = true;
                pending.Remove(key);
                log($"fleet name launch: name={claim.Name}; session={id}; caller={claim.Peer}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                claim.Failed = true;
                log($"fleet name launch: name={claim.Name}; session={id}; caller={claim.Peer}; not saved");
            }
        }
    }

    bool Enabled()
    {
        try
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(config));
            if (!json.RootElement.TryGetProperty("fleetNames", out var section)) { return true; }
            if (!section.TryGetProperty("launchRule", out var value)) { return true; }
            if (value.ValueKind == JsonValueKind.String && value.GetString() is "on" or "off") { return value.GetString() == "on"; }
        }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            // Invalid or unreadable policy must never silently re-enable named launches.
        }
        if (Interlocked.Exchange(ref warned, 1) == 0) { log("fleet name launch: invalid or unreadable fleetNames.launchRule; named launches disabled"); }
        return false;
    }

    [GeneratedRegex(@"\ASG (?:Chief of Staff|Engineering Lead|Quality Lead|Product Lead|Creative Lead|Migration Lead|SRE Lead|Reviewer [A-Z]|(?:Builder|Worker) [0-9]{1,4})\z", RegexOptions.CultureInvariant)]
    private static partial Regex Allowed();
}
