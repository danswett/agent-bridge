using System.Threading.Channels;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

// Asked counts answer attempts; Answered counts host-delivered answers. AnsweredAway identifies the relay path,
// not the phone's physical location. Delivered counts push-service acceptance, never confirmed phone receipts.
internal sealed record PhoneClientEvidence(string Client, int Asked, int Answered, int AnsweredAway, int Delivered = 0);

internal sealed record PhoneApprovalEvidenceReport(IReadOnlyList<PhoneClientEvidence> Clients, IReadOnlyList<string> Missing, bool Complete,
    IReadOnlyList<PushNoticeOutcome> Notices)
{
    internal bool Passed { get; init; }
}

// Bound queued metadata without waiting for space; count and log rejections, and drain accepted rows at shutdown.
internal sealed class PhoneNoticeRecorder : IAsyncDisposable
{
    readonly Channel<PushNoticeOutcome> queue = Channel.CreateBounded<PushNoticeOutcome>(
        new BoundedChannelOptions(64) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    readonly Task drain;
    readonly Action<string> log;
    long dropped;

    internal PhoneNoticeRecorder(AgentAuditLog audit, Action<string> log)
    {
        this.log = log;
        drain = Task.Run(async () =>
        {
            await foreach (var notice in queue.Reader.ReadAllAsync())
            {
                if (notice.Kind != NoticeKind.Question
                    || notice.Outcome is not ("delivered" or "gone" or "not-allowed" or "retrying" or "refused"))
                { log("phone notice evidence has an unsupported outcome; not recorded"); continue; }
                var device = notice.Device is { Length: 32 } id && id.All(char.IsAsciiHexDigit) ? id : "unknown";
                // Existing audit fields only: recipient, question, session, kind and push-service outcome.
                var entry = new AgentAuditEntry("out", notice.Question ?? "", $"person:{device}", [], "push",
                    notice.Session, notice.Outcome, "question");
                if (!audit.Append(entry)) { log("phone notice evidence could not be recorded"); }
            }
        });
    }

    internal void Record(PushNoticeOutcome notice)
    {
        if (!queue.Writer.TryWrite(notice))
        {
            var count = Interlocked.Increment(ref dropped);
            log($"phone notice evidence queue full or closed; dropped={count}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        queue.Writer.TryComplete();
        await drain;
    }
}

// Reads existing audit metadata only; recording a notice never creates an answer or claims a phone received it.
internal static class PhoneApprovalEvidence
{
    internal static PhoneApprovalEvidenceReport Read(string profile, IReadOnlyCollection<string> expectedClients)
    {
        var coverage = false;
        var adopted = false;
        long retained = 0;
        var (rows, complete) = GateAuditProjection.Load(profile, fault => coverage |= fault == "coverage-break",
            boundary: (floor, unproven) => { retained = floor; adopted = unproven; });
        return Project(rows, complete, expectedClients, adopted, coverage, retained);
    }

    // The same soundness checks as GateAuditProjection.Read: an adopted, trimmed or coverage-broken log, or one
    // with a repeated or missing sequence, may lack rows, so it is never complete. Each contributing run also goes
    // through the real operation-coherence validation, so an operation with conflicting terminals or a changed
    // path, target, incarnation or question is invalidated rather than counted.
    internal static PhoneApprovalEvidenceReport Project(IReadOnlyList<AuditCommands.Row> rows, bool complete, IReadOnlyCollection<string> expectedClients,
        bool adopted = false, bool coverage = false, long retainedThrough = 0)
    {
        var answers = rows
            .Where(r => r.Action == "session.answer" && r.Gate is { ActorKind: "person-client", Phase: not "control" }
                && r.Principal.StartsWith("person:", StringComparison.Ordinal) && r.Principal != "person:local")
            .ToArray();
        var unsound = false;
        foreach (var run in answers.Select(r => r.Gate!.Run).Distinct().DefaultIfEmpty(Guid.NewGuid()))
        {
            var check = GateAuditProjection.Project(rows, complete, run, retainedThrough: retainedThrough);
            unsound |= check.Invalidated || check.IncompleteReasons.Contains("sequence-break");
        }
        complete = complete && !adopted && !coverage && retainedThrough == 0 && !unsound;
        var notices = rows.Where(r => r.Action == "push" && r.Direction == "out" && r.Reason == "question"
                && r.Principal.StartsWith("person:", StringComparison.Ordinal))
            .Select(r => new PushNoticeOutcome(r.Principal["person:".Length..] is "" or "local" ? "unknown" : r.Principal["person:".Length..],
                r.Target ?? "", string.IsNullOrEmpty(r.Request) ? null : r.Request, NoticeKind.Question, r.Outcome)).ToArray();
        var perClient = answers
            .GroupBy(r => r.Principal["person:".Length..])
            .ToDictionary(g => g.Key, g => g.GroupBy(r => r.Gate!.Operation).Select(op =>
            {
                var delivered = op.Any(r => r.Gate!.Phase == "terminal" && r.Outcome == "delivered");
                return (Delivered: delivered, Away: delivered && op.First().Gate!.Path == "relay");
            }).ToArray());
        var deliveries = notices.Where(n => n.Device != "unknown" && n.Outcome == "delivered"
                && n.Session.Length > 0 && !string.IsNullOrEmpty(n.Question))
            .GroupBy(n => n.Device).ToDictionary(g => g.Key, g => g.Select(n => (n.Session, n.Question)).Distinct().Count());
        var clients = perClient.Keys.Union(notices.Select(n => n.Device), StringComparer.Ordinal)
            .Union(expectedClients, StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(c => perClient.TryGetValue(c, out var ops)
                ? new PhoneClientEvidence(c, ops.Length, ops.Count(o => o.Delivered), ops.Count(o => o.Away), deliveries.GetValueOrDefault(c))
                : new PhoneClientEvidence(c, 0, 0, 0, deliveries.GetValueOrDefault(c)))
            .ToArray();
        var missing = expectedClients.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(c => c == "unknown" || clients.Single(x => x.Client == c) is { } client
                && (client.AnsweredAway == 0 || client.Delivered == 0)).ToArray();
        return new(clients, missing, complete, notices) { Passed = complete && expectedClients.Count > 0 && missing.Length == 0 };
    }
}
