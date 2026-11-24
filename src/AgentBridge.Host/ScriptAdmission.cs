using System.Text.Json;
using AgentBridge.Local;

namespace AgentBridge.Host;

// The host's half of the scheduled-task launcher (docs/design/script-credentials-run.md section 7, #3305 slice S1a). The launcher asks to be ADMITTED by script name,
// with no token: the host judges it with the rule it applies at agent.hello (the caller is the bound task's own running engine, outside any agent session, with the
// task and its files unchanged), writes the audit row BEFORE it answers, and hands back an admission id. When the child ends the launcher sends `scripts.finished`
// with that id: it is accepted only from the same caller (pid and start), once, and is written as a SECOND audit row, never an edit of the first. Every refusal is
// the one closed code `refused`; the cause goes to the host log and (for a refused admission) nowhere a caller can read it.
public sealed class ScriptAdmission(AgentCredentials credentials, AgentAuditLog? audit, TimeProvider? clock = null)
{
    public const string AdmitOp = "scripts.admit";
    public const string FinishedOp = "scripts.finished";
    // The newest scripts.admit version this host speaks; it answers every version it has shipped (from 1) and refuses an unknown one.
    public const int Version = 1;
    const int MaxOpen = 64;
    static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    static readonly TimeSpan Slack = TimeSpan.FromSeconds(1);

    sealed record Open(int Pid, DateTimeOffset Started, string Principal, string Name, DateTimeOffset At, IReadOnlyList<string> Files);

    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly object gate = new();
    readonly Dictionary<Guid, Open> open = [];

    static InvalidOperationException Refused() => new("refused: refused");

    // The pipe's entry: strict arguments, the closed refusal for anything else.
    public object Handle(string op, JsonElement data, ProcessAncestry caller)
    {
        if (data.ValueKind != JsonValueKind.Object || data.EnumerateObject().Count() != 2) { throw Refused(); }
        try
        {
            if (op == AdmitOp)
            {
                var name = data.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                if (name is null || !data.TryGetProperty("version", out var v) || !v.TryGetInt32(out var version)) { throw Refused(); }
                var admitted = Admit(name, version, caller);
                return new { admission = admitted.ToString("D"), version = Version, files = FilesOf(admitted) };
            }
            if (op == FinishedOp && data.TryGetProperty("admission", out var a) && a.ValueKind == JsonValueKind.String
                && Guid.TryParseExact(a.GetString(), "D", out var id) && data.TryGetProperty("exit", out var e) && e.TryGetInt32(out var exit))
            {
                Finished(id, exit, caller);
                return new { ok = true };
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or InvalidCastException or ArgumentException or JsonException) { }
        throw Refused();
    }

    public Guid Admit(string name, int version, ProcessAncestry caller)
    {
        if (version < 1 || version > Version || Self(caller) is not { } self) { throw Refused(); }
        if (credentials.AdmitScript(name, caller).Accepted is not { } credential) { throw Refused(); }
        var id = Guid.NewGuid();
        var principal = credentials.PrincipalOf(credential);
        // Audit before use: an unwritable log admits nobody.
        if (audit?.Append(new AgentAuditEntry("local", id.ToString("D"), principal, [], AdmitOp, name, "admitted")) != true) { throw Refused(); }
        var now = time.GetUtcNow();
        lock (gate)
        {
            foreach (var stale in open.Where(p => now - p.Value.At > Lifetime).Select(p => p.Key).ToList()) { open.Remove(stale); }
            if (open.Count >= MaxOpen) { open.Remove(open.OrderBy(p => p.Value.At).First().Key); }
            open[id] = new(self.Id, self.StartedAt!.Value, principal, name, now, credential.ScheduledTask?.Files.Select(file => file.Path).ToArray() ?? []);
        }
        return id;
    }

    // The files the host pinned for this admission, launcher first: what the launcher compares its own argv with.
    IReadOnlyList<string> FilesOf(Guid admission) { lock (gate) { return open.TryGetValue(admission, out var entry) ? entry.Files : []; } }

    public void Finished(Guid admission, int exit, ProcessAncestry caller)
    {
        Open? entry;
        lock (gate)
        {
            // Once, and only from the caller that was admitted: the same pid and the same start, as the host reads them now.
            if (Self(caller) is not { } self || !open.TryGetValue(admission, out entry) || entry.Pid != self.Id || (self.StartedAt!.Value - entry.Started).Duration() > Slack) { throw Refused(); }
            open.Remove(admission);
        }
        // A second row naming the admission; best effort (the admission row already stands), and never an edit of the first.
        audit?.Append(new AgentAuditEntry("local", admission.ToString("D"), entry.Principal, [], FinishedOp, entry.Name, "finished", $"exit {exit}"));
    }

    // The caller itself: the first process of its chain, with an id and a start.
    static PeerProcess? Self(ProcessAncestry caller) => caller.Processes is [{ Id: > 0, StartedAt: not null } self, ..] ? self : null;
}
