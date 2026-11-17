using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Launch;

namespace AgentBridge.Host;

internal sealed class AgentLaunchLifecycle
{
    readonly AgentLaunchGate gate;
    readonly ISessionLauncher launcher;
    readonly AgentAuditLog audit;
    readonly Action<string> log;
    readonly object sync = new();
    internal AgentLaunchLifecycle(AgentLaunchGate gate, ISessionLauncher launcher, ISessionSource? source, AgentAuditLog audit, Action<string> log)
    {
        this.gate = gate; this.launcher = launcher; this.audit = audit; this.log = log;
        launcher.RequestRegistered += (_, _) => Reconcile();
        launcher.RequestFailed += _ => Reconcile();
        if (source is not null) { source.Changed += (_, change) => { if (change.Kind == SessionChangeKind.Removed) { Reconcile(); } }; }
        Reconcile();
    }

    internal void Reconcile()
    {
        lock (sync)
        {
            try
            {
                var pending = gate.Reservations();
                if (pending.Length == 0) { return; }
                var (rows, complete) = GateAuditProjection.Load(audit.Profile);
                if (!complete || !audit.Verify().Intact || !audit.Verify(launch: true).Intact)
                { throw new IOException("launch settlement audit is incomplete"); }
                foreach (var item in pending)
                {
                    var matching = rows.Where(r => r.Action == "session.launch" && r.Request == item.Request
                        && r.Principal == item.Origin && r.Gate is not null).ToArray();
                    var intent = matching.FirstOrDefault();
                    var terminals = matching.Where(r => r.Gate?.Phase == "terminal").ToArray();
                    var terminal = terminals.FirstOrDefault();
                    var bound = intent?.Gate is { Phase: "intent" } first && first.LaunchBinding == item.Hash
                        && matching.All(r => r.Gate is { } g && g.Operation == first.Operation
                            && g.LaunchBinding == first.LaunchBinding && g.Path == first.Path && g.ActorKind == first.ActorKind)
                        && terminals.Select(r => (r.Outcome, r.Target, r.Grant)).Distinct().Count() == 1;
                    if (bound && terminal is { Outcome: "failed", Grant: "agent-closed" })
                    { gate.LaunchFinished(item.Origin, item.Request); continue; }
                    var session = item.Live!.Session;
                    if (session is null && bound && terminal is { Outcome: "started", Target: { } started })
                    { if (!gate.BindIfReserved(item.Origin, item.Request, started)) { continue; } session = started; }
                    var exited = Guid.TryParse(item.Request, out var request) ? launcher.RequestExited(request) : null;
                    if (exited is null)
                    { log("agent launches: needs-person; process state is unknown; live capacity stays held"); continue; }
                    if (exited != true) { continue; }
                    if (session is not null) { gate.SessionEnded(session); }
                    else { gate.LaunchFinished(item.Origin, item.Request); }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            { log($"agent launches: needs-person; settlement evidence is unavailable ({e.GetType().Name}); live capacity stays held"); }
        }
    }
}
