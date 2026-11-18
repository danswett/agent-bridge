using System.Collections.Concurrent;
using AgentBridge.Host.Core.Load;
using AgentBridge.Protocol;
using Admission = System.Func<AgentBridge.Protocol.LaunchRequest, string, System.Func<System.Func<string?>, System.Threading.Tasks.Task<AgentBridge.Protocol.LaunchResult>>, System.Threading.Tasks.Task<AgentBridge.Protocol.LaunchResult>>;

namespace AgentBridge.Host;

internal sealed class FleetLaunchCaps(string folder, AgentAuditLog audit, Func<ProcessLoadReport?> load,
    Func<string, bool?> alive, TimeProvider time)
{
    static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    internal static Admission Create(string folder, AgentAuditLog audit, Func<ProcessLoadReport?> load,
        Func<string, bool?> alive, TimeProvider time) => new FleetLaunchCaps(folder, audit, load, alive, time).RunAsync;

    async Task<LaunchResult> RunAsync(LaunchRequest request, string principal, Func<Func<string?>, Task<LaunchResult>> dispatch)
    {
        var gate = Gates.GetOrAdd(Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), _ => new(1, 1));
        await gate.WaitAsync();
        try
        {
            var reservation = Guid.NewGuid().ToString("D");
            LaunchResult Refuse(string reason) => new(LaunchOutcome.Refused,
                audit.Append(new("local", request.Request, principal, [], "fleet.launch.refused", request.Request,
                    "refused", reason) { Grant = "fleet" }) ? reason : "cap-state: refusal audit unavailable");
            bool Mark(string phase, string target, string outcome) => audit.Append(new("local", reservation, principal, [],
                "session.launch", target, outcome, "fleet-cap:" + phase) { Grant = "fleet" });
            string? Check()
            {
                try
                {
                    if (principal != "fleet-launcher" || !Guid.TryParseExact(request.Request, "D", out _))
                    { return "cap-state: only an exact fleet-launcher request may be admitted"; }
                    var (policy, problem) = HostSettings.CurrentAgents(folder);
                    if (problem is not null || policy.FleetLaunchesPerHour < 1 || policy.FleetLiveSessions < 1)
                    { return "cap-state: fleet limits or agents policy cannot be read safely"; }
                    if (!policy.Enabled) { return "agents.enabled: fleet launches are off"; }
                    var now = time.GetUtcNow();
                    var sample = load();
                    if (sample is null || sample.SampledAt > now || now - sample.SampledAt > TimeSpan.FromSeconds(15)
                        || sample.Machine.RunQueue is null or < 0 or >= 8)
                    { return "run-queue: a fresh readable queue under 8 is required"; }
                    if (!audit.Bootstrap() || !audit.Verify().Intact || !audit.Verify(launch: true).Intact)
                    { return "cap-state: fleet launch evidence is unreadable"; }
                    var (rows, complete) = GateAuditProjection.Load(folder);
                    if (!complete) { return "cap-state: fleet launch evidence is incomplete"; }
                    var hourly = 0;
                    var live = 0;
                    foreach (var group in rows.Where(r => r.Action == "session.launch"
                        && r.Reason?.StartsWith("fleet-cap:", StringComparison.Ordinal) == true).GroupBy(r => r.Request))
                    {
                        var entries = group.ToArray();
                        var first = entries[0];
                        if (!Guid.TryParseExact(first.Request, "D", out _) || first.Reason != "fleet-cap:reserved"
                            || first.At == default || first.Outcome != "reserved" || !Guid.TryParseExact(first.Target, "D", out _)
                            || entries.Length > 2 || entries.Any(r => r.Principal != "fleet-launcher" || r.At > now || r.At < first.At)
                            || entries.Skip(1).Any(r => r.Reason == "fleet-cap:started"
                                ? r.Outcome != "started" || string.IsNullOrWhiteSpace(r.Target)
                                : r.Reason != "fleet-cap:released" || r.Outcome != "refused" || r.Target != first.Target))
                        { return "cap-state: fleet reservations are invalid or the clock moved backwards"; }
                        if (group.Key == reservation) { continue; }
                        if (first.Target == request.Request) { return "cap-state: the request key was already reserved"; }
                        if (entries[^1].Reason == "fleet-cap:released") { continue; }
                        if (now - first.At < TimeSpan.FromHours(1)) { hourly++; }
                        // Missing terminal evidence never proves an unregistered process has ended.
                        if (entries[^1].Reason != "fleet-cap:started" || alive(entries[^1].Target!) != false) { live++; }
                    }
                    return hourly >= policy.FleetLaunchesPerHour ? "hourly-limit: fleet launch hourly cap reached"
                        : live >= policy.FleetLiveSessions ? "live-limit: fleet live-session cap reached" : null;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
                { return "cap-state: fleet limits, lifecycle or audit state cannot be read safely"; }
            }
            if (Check() is { } problem) { return Refuse(problem); }
            // Launch-history rows are never age-pruned; unresolved reservations survive restarts.
            if (!Mark("reserved", request.Request, "reserved")) { return Refuse("cap-state: reservation audit unavailable"); }
            var passedPreStart = false;
            var result = await dispatch(() =>
            {
                if (Check() is { } why) { return Refuse(why).Reason; }
                passedPreStart = true;
                return null;
            });
            if (result.Outcome == LaunchOutcome.Started && result.Session is not { Length: > 0 and <= AgentAuditLog.MaxField })
            { return new(LaunchOutcome.Unconfirmed, "cap-state: registered session identity is unavailable; reservation retained"); }
            // A post-boundary Refused can hide a process that started before the launcher threw.
            var recorded = !passedPreStart && result.Outcome is (LaunchOutcome.Refused or LaunchOutcome.Busy)
                ? Mark("released", request.Request, "refused")
                : result.Outcome != LaunchOutcome.Started || result.Session is not { Length: > 0 } session
                    || Mark("started", session, "started");
            return recorded ? result : new(LaunchOutcome.Unconfirmed, "cap-state: outcome audit unavailable; reservation retained");
        }
        finally { gate.Release(); }
    }
}
