using System.Collections.ObjectModel;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// The sign-in banners (#800, ui.md §6.13; pwa/js/ui/sign-in-view.js holds the PWA to the same
// rules): one per machine, agent and account whose agent needs a person to sign it in, over the
// sessions list. Each lists the sessions it holds up and offers Sign in where the machine has
// sessions.signin; the sheet that press opens shows GitHub's code from this device's own
// signin.code, with Copy code and Open github.com. Then "signed in" or "recovered by itself" for a
// minute, or why it failed.
//
// The banners come from each session's attention (AuthState.Of). Everything a host sent is read
// through AuthState first: only the allowlist's URL is ever opened, only a code of GitHub's shape
// is shown. Nothing here signs anyone in, enters a code, or opens a page unpressed; and the code is
// never logged, announced, kept past its sheet or put in a toast.
public static class SignInBanners
{
    // One machine's part: its sessions, and whether a person can sign its agents in from here.
    public sealed record Source(string MachineKey, string Name, IReadOnlyList<Session> Sessions, bool CanSignIn);

    // This device's own flow for a banner: State is starting (signin.start sent), pending (its flow
    // id known, a code maybe) or done (how it ended, when that is still to be said). Attempt: the press
    // that started it, so a result that comes back after a Cancel or a later press isn't applied.
    // Joined: a flow another device started, which this one joined (busy). Cancel here only closes
    // the sheet: signin.cancel would end the other device's sign-in too.
    public sealed record Flow(string State, string? Id = null, DeviceCode? Code = null, string? Outcome = null, string? Reason = null, string? AtMachine = null, string? Problem = null, string? Attempt = null, IReadOnlyList<string>? Orphans = null, bool Joined = false)
    {
        public bool Active => State is "starting" or "pending";
    }

    // A banner whose sessions the host has cleared: Kind is signedIn (a person here signed in) or
    // recovered (no one here did).
    public sealed record Resolved(string Kind, int Count, DateTimeOffset At, AgentKind Agent, string? Account, string MachineKey, string Name);

    // State: needed, confirming (every session signed in and waiting to confirm), signedIn or
    // recovered. Code: the flow's, while it may be shown (not expired).
    public sealed record Banner(string Key, string MachineKey, AgentKind Agent, string Machine, string? Account, string State,
        IReadOnlyList<(string Id, string Title)> Sessions, bool AtMachine, string? AtMachineReason, string? Recovery, bool CanSignIn,
        Flow? Flow, DeviceCode? Code, bool Expired, int Count)
    {
        public string AgentName => AuthState.AgentName(Agent);
        public string Subject => $"{AgentName} on {Machine}";
    }

    public static string KeyOf(string machineKey, AgentKind agent, string? account) => $"{machineKey}\n{AuthState.WireName(agent)}\n{account ?? ""}";

    // What the banners show, from every machine shown. Pure, so each case is tested on its own.
    // A banner is needed while any of its sessions needs sign-in; confirming once every one is signed
    // in and waiting for the session to confirm (recovery pending); and resolved only once the host
    // has cleared them (or says resumed), never on a signin.done alone (ui.md §6.13, Engineering's
    // ruling on #800). A session that fails again while confirming makes it needed again.
    public static IReadOnlyList<Banner> For(IEnumerable<Source> machines, IReadOnlyDictionary<string, Flow> flows, IReadOnlyDictionary<string, Resolved> resolved, DateTimeOffset now)
    {
        var banners = new List<Banner>();
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in machines)
        {
            var groups = new Dictionary<string, (AgentKind Agent, string? Account, string? SignIn, string? Recovery, List<Session> Needed, List<Session> Confirming)>(StringComparer.Ordinal);
            // Newest first, so a group takes the latest word on how it can be signed in.
            foreach (var s in m.Sessions.OrderByDescending(s => AuthState.Of(s)?.Since ?? DateTimeOffset.MinValue))
            {
                var at = AuthState.Of(s);
                var needs = AuthState.NeedsSignIn(at);
                if (!needs && !AuthState.Confirming(at)) { continue; }
                var key = KeyOf(m.MachineKey, s.Agent, at!.Account);
                if (!groups.TryGetValue(key, out var g)) { g = (s.Agent, at.Account, null, null, [], []); }
                if (needs)
                {
                    // The newest session needing sign-in says how, and its saying nothing is no claim
                    // (6648000166): an older session's phone or machine never re-enables an action.
                    if (g.Needed.Count == 0) { g.SignIn = at.SignIn; }
                    g.Recovery ??= at.Recovery;
                }
                var list = needs ? g.Needed : g.Confirming;
                if (!list.Any(x => x.Id == s.Id)) { list.Add(s); }
                groups[key] = g;
            }
            // A flow of this device's still under way keeps its banner while its sessions have gone,
            // until it says how it ended. One that has ended keeps nothing alive.
            foreach (var k in flows.Where(p => p.Value.Active && p.Key.StartsWith(m.MachineKey + "\n", StringComparison.Ordinal) && !groups.ContainsKey(p.Key)).Select(p => p.Key))
            {
                var parts = k.Split('\n');
                var agent = parts[1] switch { "copilot" => AgentKind.Copilot, "claude" => AgentKind.Claude, "codex" => AgentKind.Codex, _ => AgentKind.Unknown };
                groups[k] = (agent, parts[2].Length > 0 ? parts[2] : null, null, null, [], []);
            }
            foreach (var (key, g) in groups)
            {
                live.Add(key);
                var flow = flows.GetValueOrDefault(key);
                var code = flow?.Code is { Refused: false } c && !AuthState.Expired(c, now) ? c : flow?.Code is { Refused: true } r ? r : null;
                var expired = flow?.Code is { Refused: false } && code is null || flow?.Outcome == "expired";
                // A machine-only result with no reason is still one: its presence says so, not its text.
                var atMachine = g.SignIn == "machine" || flow?.AtMachine is not null;
                var waiting = g.Needed.Count == 0 && g.Confirming.Count > 0 && flow?.Active != true;
                var shown = g.Needed.Count > 0 ? g.Needed : g.Confirming;
                banners.Add(new Banner(
                    key, m.MachineKey, g.Agent, string.IsNullOrEmpty(m.Name) ? "this machine" : m.Name, g.Account, waiting ? "confirming" : "needed",
                    [.. shown.Select(s => (s.Id, string.IsNullOrEmpty(s.Title) ? "Untitled session" : s.Title!))],
                    atMachine, flow?.AtMachine is { Length: > 0 } why ? why : null, g.Recovery,
                    // Sign in only where the host says a person's device can (signIn phone): a value this
                    // app doesn't know, or none, makes no claim, so no button either (6648000166). Only a
                    // provider the host says has no device flow is signed in at the machine (#696).
                    !waiting && m.CanSignIn && g.SignIn == "phone" && flow?.AtMachine is null,
                    flow, code, expired, shown.Count));
            }
        }
        foreach (var (key, r) in resolved)
        {
            if (live.Contains(key) || now - r.At >= AuthState.ResolvedShown) { continue; }
            banners.Add(new Banner(key, r.MachineKey, r.Agent, string.IsNullOrEmpty(r.Name) ? "this machine" : r.Name, r.Account, r.Kind, [], false, null, null, false, null, null, false, r.Count));
        }
        return banners;
    }

    // What a banner says, and what is read aloud when it comes or changes (ui.md §6.13): "Copilot on
    // DEV-VM1 needs sign-in · @octocat" over "4 sessions affected".
    public static (string Head, string Line, string Said) Words(Banner b)
    {
        var who = b.Subject;
        var acct = b.Account is { } account ? $" \u00b7 @{account}" : "";
        if (b.State == "signedIn")
        {
            var recovered = $"Signed in \u00b7 {Core.Words.Plural(b.Count, "session", "sessions")} recovered";
            return ($"{who}{acct}", recovered, $"{who}: {recovered}");
        }
        if (b.State == "recovered") { return ($"{who}{acct}", "Recovered by itself", $"{who} recovered by itself"); }
        if (b.State == "confirming")
        {
            var confirming = $"Signed in \u00b7 waiting for {Core.Words.Plural(b.Sessions.Count, "session", "sessions")} to confirm";
            return ($"{who}{acct}", confirming, $"{who}: {confirming}");
        }
        var f = b.Flow;
        var line =
            b.AtMachine ? (b.AtMachineReason is { } why ? $"Sign in at the machine \u00b7 {why}" : "Sign in at the machine")
            : f?.Outcome == "failed" ? (f.Reason is { } failed ? $"Sign-in failed \u00b7 {failed}" : "Sign-in failed")
            : f?.Outcome == "refused" ? (f.Reason is { } refused ? $"Sign-in refused \u00b7 {refused}" : "Sign-in refused")
            : b.Expired ? "Code expired"
            : b.Code is { Refused: false } code ? $"Waiting for sign-in \u00b7 code expires {AuthState.Clock(code.ExpiresAt)}"
            : b.Recovery == "checking" ? "Checking for a sign-in that still works\u2026"
            : b.Recovery == "signingIn" && f is null ? "Waiting for sign-in"
            : $"{Core.Words.Plural(b.Sessions.Count, "session", "sessions")} affected";
        return ($"{who} needs sign-in{acct}", line, $"{who} needs sign-in");
    }

    // What is said aloud as a banner changes, once each (§9.1 A6): every way an attempt can end, as the
    // banner or the sheet shows it, so none passes unsaid. Never the machine's own words, nor the code.
    public static (string State, string Words) Said(Banner b)
    {
        var who = b.Subject;
        if (b.State != "needed") { return (b.State, Words(b).Said); }
        var f = b.Flow;
        return b.AtMachine ? ("machine", $"{who} needs sign-in at the machine")
            : f?.Outcome == "failed" ? ("failed", $"Sign-in failed for {who}")
            : f?.Outcome == "refused" ? ("refused", $"Sign-in refused for {who}")
            : b.Expired ? ("expired", $"Sign-in code expired for {who}")
            : b.Code is { Refused: true } ? ("unshown", $"The sign-in code for {who} couldn\u2019t be shown")
            : ("needed", Words(b).Said);
    }

    // The one verb a banner offers (§9.1 A7): Sign in; Show code while a flow runs that this device
    // isn't showing; Try again after one that failed, expired or was refused. None while this
    // device's own flow runs (its sheet is open), nor while confirming.
    public static string? Verb(Banner b)
    {
        if (!b.CanSignIn) { return null; }
        var f = b.Flow;
        if (f is { Active: true } && !b.Expired) { return null; }
        if (b.Expired || f?.Outcome is "failed" or "refused" or "expired") { return "Try again"; }
        if (b.Recovery == "signingIn") { return "Show code"; }
        return "Sign in";
    }
}

// One banner on screen, kept by its key across redraws so a button with focus keeps it.
public sealed class SignInBannerViewModel : Observable
{
    readonly Action<SignInBannerViewModel> press;
    readonly Action<string, string> open;
    SignInBanners.Banner banner;
    bool busy;

    internal SignInBannerViewModel(SignInBanners.Banner banner, Action<SignInBannerViewModel> press, Action<string, string> open)
    {
        this.banner = banner;
        this.press = press;
        this.open = open;
        Sessions = [.. Buttons(banner.MachineKey, banner.Sessions, open)];
    }

    // Each keeps only where it goes, never the banner, whose flow may hold a code.
    static IEnumerable<SignInSession> Buttons(string machine, IEnumerable<(string Id, string Title)> sessions, Action<string, string> open) =>
        sessions.Select(s => new SignInSession(s.Id, s.Title, () => open(machine, s.Id)));

    public string Key => banner.Key;
    public string MachineKey => banner.MachineKey;
    internal SignInBanners.Banner Banner => banner;
    public bool Needed => banner.State == "needed";
    // How it is drawn: a banner that changes between these is a new one, so a needed banner's
    // button never lingers.
    internal string Kind => banner.State is "needed" or "confirming" ? banner.State : "resolved";
    public StatusGlyph Glyph => Needed ? StatusGlyph.Needs : StatusGlyph.Idle;
    public string Head => SignInBanners.Words(banner).Head;
    public string Line => SignInBanners.Words(banner).Line;
    public bool HasLine => Line.Length > 0;
    public ObservableCollection<SignInSession> Sessions { get; }
    public bool HasSessions => Sessions.Count > 0;
    public string Problem => banner.Flow?.Problem ?? "";
    public bool HasProblem => Problem.Length > 0;

    public string? Verb => SignInBanners.Verb(banner);
    public bool HasVerb => Verb is not null;
    public string VerbText => Verb ?? "";
    // Named for what it acts on (§9.1 A1): the words shown first.
    public string VerbName => Verb switch
    {
        "Show code" => $"Show code for {banner.Subject}",
        "Try again" => $"Try again to sign in to {banner.Subject}",
        _ => $"Sign in to {banner.Subject}",
    };
    // In flight, a press keeps focus and says it is busy (§9.1 A4), and a second sends nothing.
    public bool Busy { get => busy; internal set => Set(ref busy, value); }

    public void Press() { if (!Busy && HasVerb) { press(this); } }

    internal void Update(SignInBanners.Banner next)
    {
        var ids = string.Join("\n", next.Sessions.Select(s => $"{s.Id}\n{s.Title}"));
        var had = string.Join("\n", banner.Sessions.Select(s => $"{s.Id}\n{s.Title}"));
        banner = next;
        if (ids != had)
        {
            Sessions.Clear();
            foreach (var s in Buttons(next.MachineKey, next.Sessions, open)) { Sessions.Add(s); }
        }
        RaiseAll();
    }
}

public sealed record SignInSession(string Id, string Title, Action Open)
{
    public void Press() => Open();
}

// The sheet a person's Sign in opens (ui.md §6.13): Cancel is where it starts and what Escape does.
// It shows the URL as plain text with Open, the code in mono with Copy, and when it expires. The
// code is here only while the sheet is open.
public sealed class SignInSheetViewModel : Observable
{
    SignInBanners.Banner banner;
    string copied = "";

    internal SignInSheetViewModel(SignInBanners.Banner banner) => this.banner = banner;

    public string Key => banner.Key;
    internal SignInBanners.Banner Banner => banner;
    public string Title => $"Sign in to {banner.Subject}";
    public string StartingText => $"Starting {banner.AgentName}\u2019s sign-in on {banner.Machine}\u2026";
    // starting, code, refused or expired.
    public string Shape => banner.Code is { Refused: true } ? "refused" : banner.Code is not null ? "code" : banner.Expired ? "expired" : "starting";
    public bool Starting => Shape == "starting";
    public bool HasCode => Shape == "code";
    // Not GitHub's link, or not GitHub's kind of code: possibly phishing, so none of it is shown.
    public bool Refused => Shape == "refused";
    public bool Expired => Shape == "expired";
    // Try again, in the sheet: a new request (ui.md §6.13), only while the host still offers this
    // device sign-in (sessions.signin, not read-only, the newest session's signIn phone).
    public bool CanRetry => (Refused || Expired) && banner.CanSignIn;
    public string RetryName => $"Try again to sign in to {banner.Subject}";
    public string Code => HasCode ? banner.Code!.Code! : "";
    // Read a character at a time, as it is typed.
    public string CodeName => HasCode ? AuthState.Spelled(Code) : "";
    // The page, as plain text: the allowlist's own string without its scheme.
    public string Page => HasCode ? banner.Code!.Uri!["https://".Length..] : "";
    public string Expiry => HasCode ? $"Expires {AuthState.Clock(banner.Code!.ExpiresAt)}" : "";
    public string Copied { get => copied; private set => Set(ref copied, value); }
    public string CopyName => $"Copy code for {banner.Subject}";
    public string OpenName => $"Open github.com to sign in to {banner.Subject}";
    public string CancelName => $"Cancel sign-in for {banner.Subject}";
    // The allowlist's own string, for the app to open on a press, and only then.
    public Uri? OpenUri => HasCode && AuthState.AllowedUri(banner.Code!.Uri) is { } uri ? new Uri(uri) : null;

    // Said once for each Copy, and never again as the sheet is drawn again (the view announces it).
    public event Action<string>? CopiedSaid;

    public void CopiedCode(bool ok)
    {
        Copied = ok ? "Code copied" : "Couldn\u2019t copy. Select the code to copy it.";
        CopiedSaid?.Invoke(Copied);
    }

    internal void Update(SignInBanners.Banner next)
    {
        var was = Shape;
        banner = next;
        if (Shape != was) { Copied = ""; }
        RaiseAll();
    }
}
