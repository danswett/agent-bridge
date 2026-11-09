using System.Globalization;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// Update now's words in the Windows app, for another machine (#1159, docs/protocol/v1-update.md
// "What the person sees"): the same as the PWA's (pwa/js/update.js), word for word, so a person
// reads one product on either device. Every line comes from the machine's codes, never its own
// text (#826): Creative's rules (6711605555) and fixed lines (#1185, 6723869235).
public static class UpdateNowWords
{
    // An Update now waiting for a quiet moment, by its waitingCode.
    public static readonly IReadOnlyDictionary<string, string> Waiting = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [UpdateWaitingCode.TurnRunning] = "Update ready \u00b7 waiting for the current turn to finish",
        [UpdateWaitingCode.QuestionWaiting] = "Update ready \u00b7 waiting for a question to be answered",
        [UpdateWaitingCode.UsageCheck] = "Update ready \u00b7 waiting for a Codex usage check to finish",
        [UpdateWaitingCode.Unknown] = "Update ready \u00b7 waiting until sessions here are quiet",
    };

    public const string Restarting = "Restarting to update\u2026";
    // How long a machine leaving for an Update now reads Restarting once its channel has closed,
    // then as any other: #815's 90 s, until its planned announcement is negotiated (v1-update.md
    // "Announced as planned").
    public static readonly TimeSpan RestartShown = TimeSpan.FromSeconds(90);

    // A press that never got an answer (the machine unreachable, or nothing to sign with): this
    // device's words, never an error's text.
    public const string NotSent = "Not updated: the request didn't go through; try again.";

    // The confirmation's lines.
    public const string PauseLine = "They pause while the bridge restarts, usually under a minute.";
    public const string UnknownLine = "Some sessions there haven't reported yet.";
    public const string WarnLine = "A question or approval waiting may have to be answered at the machine afterwards.";
    public const string ConfirmButton = "Update now";
    public const string CancelButton = "Cancel";
    // A busy session this device has no name for.
    public const string Unnamed = "A session";

    public static string StateWord(string state) => state switch
    {
        UpdateBusyState.Working => "Mid-turn",
        UpdateBusyState.Question => "Question waiting",
        _ => "Approval waiting",
    };

    // A time Update now works again, in the viewer's own clock (the product shows local time).
    static string? Clock(DateTimeOffset? at) => at?.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    // Why Update now can't be pressed, in place of its button (updateNowWhy). An unknown code says
    // nothing.
    public static string? Reason(string? why, DateTimeOffset? at) => why switch
    {
        UpdateNowWhy.Disabled => "Update now is off on this machine",
        UpdateNowWhy.ReadOnly => "View only",
        UpdateNowWhy.OldApp => "Update now needs a newer app there. Install still works.",
        UpdateNowWhy.Unavailable => "The app there can't install updates now",
        UpdateNowWhy.SignIn => "An agent sign-in is under way there",
        UpdateNowWhy.Busy => "Another update or restart is under way there",
        UpdateNowWhy.RestartCap => Clock(at) is { } t ? $"Update now is available again at {t}" : "Update now is available again later",
        _ => null,
    };

    static readonly Dictionary<string, string> Reasons = new(StringComparer.Ordinal)
    {
        [ManageReason.NotQuiet] = "a message was being delivered there the whole time",
        [ManageReason.Changed] = "something changed on the machine; try again",
        [ManageReason.RateLimited] = "too many presses just now; try again in a few minutes",
        [ManageReason.Busy] = "another update or restart is under way there",
        [UpdateCode.SignIn] = "an agent sign-in is under way there",
        [ManageReason.AnnounceFailed] = "the bridge couldn't tell your devices it was restarting",
        [UpdateCode.OtherProfile] = "another bridge profile is running there",
        [ManageReason.Disabled] = "Update now is off on this machine",
        [ManageReason.ReadOnly] = "Update now is off on this machine",
        [ManageReason.Future] = "the request didn't verify; try again",
        [ManageReason.BadSignature] = "the request didn't verify; try again",
        [ManageReason.BadArgs] = "the request wasn't one this machine understands; try again",
        [ManageReason.NotPerson] = "this device can't update that machine",
        [ManageReason.NotPeer] = "this device can't update that machine",
        [ManageReason.NotConnected] = "this device can't update that machine",
        [ManageReason.LocalDevice] = "this device can't update that machine",
        [ManageReason.NeedsRoster] = "this needs your approved devices first",
        [ManageReason.Unavailable] = "the app there can't install it now",
        [UpdateCode.OldApp] = "the app there can't install it now",
    };

    // Why an Update now didn't happen, from its code, as a whole line. at: when Update now works
    // again, for restart-cap. failed: it was attempted and didn't take. An unknown code is "the
    // machine refused it", never what the machine wrote.
    public static string NotUpdated(string? code, DateTimeOffset? at = null, bool failed = false)
    {
        if (failed) { return "Not updated: the update didn't take."; }
        switch (code)
        {
            case ManageReason.Expired: return "Not updated: not done within 15 minutes. Press Update now again.";
            case UpdateCode.UsageCheck: return "Not updated: a Codex usage check was still running";
            case UpdateCode.ReleaseChanged: return "Not updated: the update changed. Check for updates again.";
            case UpdateCode.RestartCap:
                return $"Not updated: Update now has used its restarts for the last 6 hours; {(Clock(at) is { } t ? $"try after {t}" : "try again later")}.";
        }
        return $"Not updated: {(code is not null && Reasons.TryGetValue(code, out var why) ? why : "the machine refused it")}.";
    }

    // UpdateBusy as this device reads it, as the PWA's readBusy: sessions in a state it can word,
    // approvals first, then questions, then turns, at most BusyListed; every count within 0 and
    // BusyMost. Absent reads as "can't tell", which asks first.
    public static UpdateBusy Read(UpdateBusy? busy)
    {
        if (busy is null) { return CantTell; }
        static int Rank(string s) => s switch { UpdateBusyState.Approval => 0, UpdateBusyState.Question => 1, UpdateBusyState.Working => 2, _ => 3 };
        static int Count(int? n) => n is { } v ? Math.Clamp(v, 0, UpdateProtocol.BusyMost) : 0;
        var sessions = (busy.Sessions ?? [])
            .Where(s => s is not null && Rank(s.State) < 3)
            .Select((s, i) => (Session: new UpdateBusySession(s.Id ?? "", s.State), i))
            .OrderBy(x => Rank(x.Session.State)).ThenBy(x => x.i)
            .Select(x => x.Session).Take(UpdateProtocol.BusyListed).ToList();
        return new UpdateBusy
        {
            Working = Count(busy.Working),
            Waiting = Count(busy.Waiting),
            Unknown = busy.Unknown == true ? true : null,
            Sessions = sessions,
            MoreApprovals = Count(busy.MoreApprovals),
            MoreOther = Count(busy.MoreOther),
        };
    }

    static readonly UpdateBusy CantTell = new() { Unknown = true, Sessions = [], MoreApprovals = 0, MoreOther = 0 };

    // Nothing working, no question or approval waiting, and its sessions known: one press, no dialog.
    public static bool Quiet(UpdateBusy b) =>
        b.Unknown != true && b.Working == 0 && b.Waiting == 0 && (b.Sessions?.Count ?? 0) == 0 && (b.MoreApprovals ?? 0) == 0 && (b.MoreOther ?? 0) == 0;

    // The press's accept: exactly what the confirmation showed (v1-update.md "Covered by the
    // confirmation"), in the PWA's shape: unknown only when true, sessions only when listed, and the
    // two left-out counts together only when either is above 0.
    public static UpdateBusy AcceptOf(UpdateBusy b)
    {
        var more = (b.MoreApprovals ?? 0) > 0 || (b.MoreOther ?? 0) > 0;
        return new UpdateBusy
        {
            Working = b.Working,
            Waiting = b.Waiting,
            Unknown = b.Unknown == true ? true : null,
            Sessions = b.Sessions is { Count: > 0 } s ? [.. s.Select(x => new UpdateBusySession(x.Id, x.State))] : null,
            MoreApprovals = more ? b.MoreApprovals ?? 0 : null,
            MoreOther = more ? b.MoreOther ?? 0 : null,
        };
    }

    // The confirmation: each busy session by the name this device shows for it, its state in words,
    // how many it left out, whether the bridge can't tell, the pause, and, while a question or
    // approval waits or it can't tell, that one may have to be answered at the machine. nameOf: the
    // name of a session this device has, or null.
    public static UpdateNowSheet Sheet(string machine, UpdateBusy? busy, Func<string, string?> nameOf)
    {
        var b = busy ?? CantTell;
        var sessions = b.Sessions ?? [];
        var approvals = b.MoreApprovals ?? 0;
        var left = (long)approvals + (b.MoreOther ?? 0);
        var asking = b.Waiting > 0 || approvals > 0 || sessions.Any(s => s.State != UpdateBusyState.Working);
        return new UpdateNowSheet(
            $"Update {machine} now?",
            [.. sessions.Select(s => new UpdateNowSheetRow((s.Id is { Length: > 0 } id ? nameOf(id) : null) ?? Unnamed, StateWord(s.State)))],
            left > 0 ? approvals > 0 ? $"and {left} more, {approvals} of them with an approval waiting" : $"and {left} more" : null,
            b.Unknown == true ? UnknownLine : null,
            PauseLine,
            // Until #954 lands (settled decision 8): err toward warning.
            asking || b.Unknown == true ? WarnLine : null,
            b);
    }
}

// What Update now's confirmation shows (UpdateNowWords.Sheet), and the busy it was made from, which
// the press then signs as its accept.
public sealed record UpdateNowSheet(string Title, IReadOnlyList<UpdateNowSheetRow> Sessions, string? More, string? Unknown, string Pause, string? Warn, UpdateBusy Busy);

public sealed record UpdateNowSheetRow(string Name, string State);

// The Update now presses this app sent that a machine took (their requests), the last few only:
// their outcome, which the machine reports after its restart, is worded as an Update now's.
public sealed class UpdateNowPresses
{
    const int Kept = 32;
    readonly Lock gate = new();
    readonly List<string> requests = [];

    public void Add(string request)
    {
        lock (gate)
        {
            requests.Add(request);
            if (requests.Count > Kept) { requests.RemoveAt(0); }
        }
    }

    public bool Contains(string? request)
    {
        if (request is null) { return false; }
        lock (gate) { return requests.Contains(request); }
    }
}
