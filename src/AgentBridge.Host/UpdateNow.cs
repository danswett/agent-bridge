using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

// What Update now (#1159) would go over, from the host's own session snapshot, never the app's
// (docs/protocol/v1-update.md, "What the phone sees before the press"). Every busy session, by id
// and state only: a turn or delegated work running (working), a question waiting (question), or an
// approval waiting (approval, so one is never called anything else). Unknown: the host can't tell
// whether one is busy (discovery held), which counts as busy (Product: err toward warning).
public sealed class UpdateBusyView
{
    UpdateBusyView(IReadOnlyList<UpdateBusySession> busy, bool unknown) => (All, Unknown) = (busy, unknown);

    // Approvals first, then questions, then turns, each in the snapshot's order.
    public IReadOnlyList<UpdateBusySession> All { get; }
    public bool Unknown { get; }
    public int Working => All.Count(s => s.State == UpdateBusyState.Working);
    public int Waiting => All.Count - Working;
    public bool Quiet => All.Count == 0 && !Unknown;

    public static UpdateBusyView Of(IReadOnlyList<Session> sessions, bool unknown)
    {
        var busy = sessions.Select(s => (s.Id, State: StateOf(s))).Where(s => s.State is not null).ToList();
        int Rank(string state) => state switch { UpdateBusyState.Approval => 0, UpdateBusyState.Question => 1, _ => 2 };
        return new([.. busy.Select((s, i) => (s, i)).OrderBy(x => Rank(x.s.State!)).ThenBy(x => x.i).Select(x => new UpdateBusySession(x.s.Id, x.s.State!))], unknown);
    }

    // As session.stop counts a turn: working, or idle with delegated work in flight; and a question
    // or a waiting status. A session that has both is waiting. Ending is the host stopping it.
    static string? StateOf(Session s) =>
        s.Question is { Kind: QuestionKind.Approval } ? UpdateBusyState.Approval
        : s.Question is not null || s.Status == SessionStatus.Waiting ? UpdateBusyState.Question
        : s.Status == SessionStatus.Working || s.Status == SessionStatus.Idle && s.Activity?.Background is not null ? UpdateBusyState.Working
        : null;

    // What the state carries: at most BusyListed sessions, and how many of each kind it left out.
    public UpdateBusy Shown()
    {
        var listed = All.Take(UpdateProtocol.BusyListed).ToList();
        var left = All.Skip(UpdateProtocol.BusyListed).ToList();
        var leftApprovals = left.Count(s => s.State == UpdateBusyState.Approval);
        return new UpdateBusy
        {
            Working = Working,
            Waiting = Waiting,
            Unknown = Unknown ? true : null,
            Sessions = listed.Count > 0 ? listed : null,
            MoreApprovals = left.Count > 0 ? leftApprovals : null,
            MoreOther = left.Count > 0 ? left.Count - leftApprovals : null,
        };
    }

    public enum Cover { Covered, Unknown, Question, Turn }

    // Whether what is busy now is covered by what the person confirmed (accept): every busy session
    // now that the confirmation listed has the same state there; the ones it didn't list are, by
    // kind, no more than it left out (approvals no more than moreApprovals, all of them no more than
    // moreApprovals plus moreOther, turns no more than working less the listed turns, questions and
    // approvals no more than waiting less the listed ones); working and waiting are each no more than
    // accepted; and the host is unsure only if the confirmation said so. With no accept, only a quiet
    // machine is covered. Otherwise why not, as the waiting code it becomes.
    public Cover CoveredBy(UpdateBusy? accept)
    {
        if (Quiet) { return Cover.Covered; }
        if (accept is null) { return Unknown ? Cover.Unknown : All.Any(s => s.State != UpdateBusyState.Working) ? Cover.Question : Cover.Turn; }
        if (Unknown && accept.Unknown != true) { return Cover.Unknown; }
        var listed = (accept.Sessions ?? []).ToDictionary(s => s.Id, s => s.State, StringComparer.Ordinal);
        var unlisted = new List<UpdateBusySession>();
        var question = false;
        var turn = false;
        foreach (var s in All)
        {
            if (!listed.TryGetValue(s.Id, out var was)) { unlisted.Add(s); continue; }
            if (was == s.State) { continue; }
            if (s.State == UpdateBusyState.Working) { turn = true; } else { question = true; }
        }
        var listedWorking = listed.Values.Count(v => v == UpdateBusyState.Working);
        var listedWaiting = listed.Count - listedWorking;
        var moreApprovals = accept.MoreApprovals ?? 0;
        var moreOther = accept.MoreOther ?? 0;
        var unlistedApprovals = unlisted.Count(s => s.State == UpdateBusyState.Approval);
        var unlistedWorking = unlisted.Count(s => s.State == UpdateBusyState.Working);
        var unlistedWaiting = unlisted.Count - unlistedWorking;
        if (unlistedApprovals > moreApprovals || unlistedWaiting > accept.Waiting - listedWaiting || Waiting > accept.Waiting) { question = true; }
        if (unlistedWorking > accept.Working - listedWorking || Working > accept.Working) { turn = true; }
        if (unlisted.Count > moreApprovals + moreOther)
        {
            if (unlistedWaiting > 0) { question = true; } else { turn = true; }
        }
        return question ? Cover.Question : turn ? Cover.Turn : Cover.Covered;
    }

    public static string WaitingCode(Cover c) => c switch
    {
        Cover.Unknown => UpdateWaitingCode.Unknown,
        Cover.Question => UpdateWaitingCode.QuestionWaiting,
        _ => UpdateWaitingCode.TurnRunning,
    };
}

// How an Update now's departure ended (UpdateService.DepartAsync): committed (the host is fenced
// until it exits, and the app stops it and installs), waiting (the machine is busier than the person
// confirmed: Code is the waiting code, and the download is kept for the next quiet moment), or
// refused (Code is why, and the host serves on).
public enum UpdateDepartureOutcome { Committed, Waiting, Refused }

public sealed record UpdateDeparture(UpdateDepartureOutcome Outcome, string? Code = null, string? Transition = null)
{
    public static UpdateDeparture Committed(string transition) => new(UpdateDepartureOutcome.Committed, null, transition);
    public static UpdateDeparture Waiting(string code) => new(UpdateDepartureOutcome.Waiting, code);
    public static UpdateDeparture Refused(string code) => new(UpdateDepartureOutcome.Refused, code);
}

// The rules an Update now press's signed args follow (docs/protocol/v1-update.md, "The press"): now
// true, the release shown, and accept, and nothing else; no contentSha256. Anything else is bad-args.
public static class UpdateNowRules
{
    static readonly HashSet<string> ArgNames = new(["now", "release", "accept"], StringComparer.Ordinal);
    static readonly HashSet<string> AcceptNames = new(["working", "waiting", "unknown", "sessions", "moreApprovals", "moreOther"], StringComparer.Ordinal);
    static readonly HashSet<string> SessionNames = new(["id", "state"], StringComparer.Ordinal);

    public static (UpdateNowArgs? Args, string? Problem) Read(CommandBody body)
    {
        if (body.ContentSha256 is not null) { return (null, "an Update now carries nothing beside its command"); }
        if (body.Args is not { ValueKind: JsonValueKind.Object } a) { return (null, "an Update now's args are now, release and accept"); }
        if (a.EnumerateObject().Any(p => !ArgNames.Contains(p.Name))) { return (null, "an Update now's args are now, release and accept, and nothing else"); }
        if (!a.TryGetProperty("now", out var now) || now.ValueKind != JsonValueKind.True) { return (null, "an Update now's args must say now: true"); }
        if (!a.TryGetProperty("release", out var release) || release.ValueKind != JsonValueKind.String || release.GetString() is not { Length: > 0 } version
            || Encoding.UTF8.GetByteCount(version) > UpdateProtocol.Version)
        {
            return (null, "an Update now's args must name the release the person was shown");
        }
        UpdateBusy? accept = null;
        if (a.TryGetProperty("accept", out var given))
        {
            if (AcceptShape(given) is { } shape) { return (null, shape); }
            try { accept = given.Deserialize<UpdateBusy>(ProtocolJson.Options); }
            catch (JsonException) { return (null, "an Update now's accept is not one"); }
            if (accept is null) { return (null, "an Update now's accept is not one"); }
            if (Check(accept) is { } problem) { return (null, problem); }
        }
        return (new UpdateNowArgs { Now = true, Release = version, Accept = accept }, null);
    }

    static string? AcceptShape(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || e.EnumerateObject().Any(p => !AcceptNames.Contains(p.Name))) { return "an Update now's accept has fields it doesn't define"; }
        foreach (var name in new[] { "working", "waiting", "moreApprovals", "moreOther" })
        {
            if (e.TryGetProperty(name, out var n) && (n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out var v) || v < 0)) { return $"an Update now's accept has a {name} that is not a count"; }
        }
        if (!e.TryGetProperty("working", out _) || !e.TryGetProperty("waiting", out _)) { return "an Update now's accept must count working and waiting"; }
        if (e.TryGetProperty("unknown", out var u) && u.ValueKind != JsonValueKind.True) { return "an Update now's accept says unknown only as true"; }
        if (e.TryGetProperty("sessions", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array) { return "an Update now's accept lists its sessions"; }
            foreach (var s in list.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object || s.EnumerateObject().Any(p => !SessionNames.Contains(p.Name))
                    || !s.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String
                    || !s.TryGetProperty("state", out var state) || state.ValueKind != JsonValueKind.String)
                {
                    return "an Update now's accept lists each session by id and state";
                }
            }
        }
        return null;
    }

    // An accept whose numbers don't match its list (docs/protocol/v1-update.md): left-out numbers
    // with fewer than BusyListed listed; moreApprovals unless every one listed is an approval
    // (approvals come first); or the listed sessions and the two numbers not adding up to working
    // plus waiting. So a client can't skip the list where 32 or fewer are busy.
    public static string? Check(UpdateBusy accept)
    {
        var sessions = accept.Sessions ?? [];
        if (sessions.Count > UpdateProtocol.BusyListed) { return $"an Update now's accept lists at most {UpdateProtocol.BusyListed} sessions"; }
        if (sessions.Any(s => s is null || string.IsNullOrEmpty(s.Id) || s.Id.Length > 128 || !UpdateBusyState.Is(s.State))) { return "an Update now's accept lists each session by id and a state it knows"; }
        if (sessions.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != sessions.Count) { return "an Update now's accept lists a session twice"; }
        var moreApprovals = accept.MoreApprovals ?? 0;
        var moreOther = accept.MoreOther ?? 0;
        if (accept.Working < 0 || accept.Waiting < 0 || moreApprovals < 0 || moreOther < 0) { return "an Update now's accept has a count below zero"; }
        if ((moreApprovals > 0 || moreOther > 0) && sessions.Count < UpdateProtocol.BusyListed) { return "an Update now's accept leaves sessions out of a list that isn't full"; }
        if (moreApprovals > 0 && sessions.Any(s => s.State != UpdateBusyState.Approval)) { return "an Update now's accept leaves out an approval while listing something else"; }
        if ((long)sessions.Count + moreApprovals + moreOther != (long)accept.Working + accept.Waiting) { return "an Update now's accept doesn't add up to what it counts"; }
        return null;
    }
}

// The presses of Update now this host has verified (docs/protocol/v1-update.md, "Replay, durably"):
// update-now.json in the profile folder, each written through before its ask is handed to the app,
// and kept for the press's longest life, 15 minutes from issuedAt plus Skew, across the host's own
// restart. A request recorded is the same press: it gets that press's answer and is never acted on
// again. An accepted press keeps its signed command, which its departure verifies again (by a host
// that has restarted since, too), and is marked done once it has left or been refused there. The
// app's record of its last 32 is a second line, not the authority.
//
// A record that can't be read fails closed: presses signed before it was found unreadable (allowing
// for a clock up to Skew ahead) are refused until they could no longer verify anyway. One that
// can't be written refuses the press it was for, before anything is asked of the app.
sealed class UpdateNowRecord
{
    public const string FileName = "update-now.json";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    // At most this many kept, each for its press's life; past it a press is refused. Three presses a
    // device in ten minutes keeps a fleet of devices well inside.
    const int Kept = 256;

    readonly string path;
    readonly Action<string> log;
    readonly List<Entry> entries = [];
    // The requests read from the file: presses a host before this one took.
    readonly HashSet<string> earlier = new(StringComparer.Ordinal);
    // When a record that couldn't be read was found: presses signed before this (plus Skew) are refused.
    DateTimeOffset? unreadableAt;

    // A press's answer, once it has one. An accepted press keeps its signed command, so its
    // departure can be verified again by a host that has restarted since; Done once it has left or
    // been refused at the departure, so it never leaves twice.
    public sealed record Entry(string Request, DateTimeOffset IssuedAt, string By, string? Outcome = null, string? Code = null, string? Reason = null,
        SignedCommand? Command = null, bool Done = false);

    sealed class Saved
    {
        public int V { get; set; } = 1;
        public List<Entry> Presses { get; set; } = [];
    }

    public UpdateNowRecord(string folder, DateTimeOffset now, Action<string> log)
    {
        path = Path.Combine(folder, FileName);
        this.log = log;
        try
        {
            if (File.Exists(path))
            {
                var s = JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), Json);
                if (s is not { V: 1 } || s.Presses is null) { throw new JsonException("not a record of presses"); }
                entries.AddRange(s.Presses.Where(e => e?.Request is not null));
                earlier.UnionWith(entries.Select(e => e.Request));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            unreadableAt = now;
            log($"update now: {FileName} could not be read ({e.GetType().Name}); presses signed before now are refused");
        }
    }

    static DateTimeOffset Until(DateTimeOffset issued) => issued + UpdateProtocol.NowLife + ManageService.Skew;

    // Under the caller's lock.
    public Entry? Find(string request, DateTimeOffset now)
    {
        entries.RemoveAll(e => Until(e.IssuedAt) < now);
        return entries.FirstOrDefault(e => e.Request == request);
    }

    // Whether this record can vouch that a press signed at issued was never acted on.
    public bool Knows(DateTimeOffset issued) => unreadableAt is not { } at || issued > at + ManageService.Skew;

    // Whether a host before this one took the press: its ask and its departure went with that host.
    public bool Earlier(string request) => earlier.Contains(request);

    // Written through before the caller goes on; false if it could not be, and nothing is kept. A
    // full record refuses the press rather than forget one still inside its life.
    public bool Add(Entry e, DateTimeOffset now)
    {
        entries.RemoveAll(x => Until(x.IssuedAt) < now || x.Request == e.Request);
        if (entries.Count >= Kept)
        {
            log($"update now: {FileName} holds {Kept} presses inside their life already; this one is refused");
            return false;
        }
        entries.Add(e);
        if (Write()) { return true; }
        entries.Remove(e);
        return false;
    }

    // The press's answer, once it has one, with its signed command if it was accepted. A failed
    // write keeps the answer here; the press stays recorded either way, so it is never acted on again.
    public void Answer(string request, string outcome, string? code, string? reason, SignedCommand? command = null)
    {
        var i = entries.FindIndex(e => e.Request == request);
        if (i < 0) { return; }
        entries[i] = entries[i] with { Outcome = outcome, Code = code, Reason = reason, Command = command ?? entries[i].Command };
        Write();
    }

    // The press has left, or been refused at its departure: it is never departed with again.
    public void Finish(string request)
    {
        var i = entries.FindIndex(e => e.Request == request);
        if (i < 0) { return; }
        entries[i] = entries[i] with { Done = true };
        Write();
    }

    bool Write()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Saved { Presses = [.. entries] }, Json), Encoding.UTF8);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"update now: {FileName} could not be written ({e.GetType().Name})");
            return false;
        }
    }
}
