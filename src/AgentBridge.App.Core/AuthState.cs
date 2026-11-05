using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// An agent that needs sign-in, whose turn ended on an error, or that has stopped answering (#800):
// every protocol name for it, read in this one file, as Connection's producer contract has them
// (issue #800, comment 6646501884, with the push amendment 6646515428). A rename is a change here
// and nowhere else. pwa/js/auth-state.js is its twin, with the same rules.
//
//   Session.attention          { kind, since, agent, provider, account, message, errorClass,
//                                retryAt, signIn, recovery, lastEventAt }
//   capability sessions.signin, ops signin.start and signin.cancel, events signin.code and
//   signin.done, which come only to the channels in the flow: a device code is never in a
//   session, a snapshot or hello.
//
// The protocol's Session record doesn't carry attention yet, so it is read from the session's JSON
// as it arrived (HostConnection.Upserted) and kept beside the record it was read into, for as long
// as that record lives: Of(session) is what the status rules, Needs you and the banners read.
//
// What a host sends here is untrusted (a spoofed or broken host), and what it can lead a person to
// do is sign an account in. So only a verification URL on
// AllowedVerificationUris is ever shown or opened, and then as the allowlist's own string; only a
// user code of GitHub's shape is shown, after the hidden-character check the lane applies; nothing
// else of a signin.code is read, so no token can reach the app through it; an attention's message
// is the host's own sentence (from kind, errorClass, statusCode and provider, never the agent's
// text: 6649775484), shown as it is, plain and revealed; and a machine's reason is plain, revealed,
// cut short, and dropped whole when it holds anything shaped like a token. Nothing
// here signs anyone in, enters a code, or opens a page: a person presses for each.
public static partial class AuthState
{
    public const string Capability = "sessions.signin";
    public const string Start = "signin.start";
    public const string Cancel = "signin.cancel";
    public const string CodeEvent = "signin.code";
    public const string DoneEvent = "signin.done";

    public const string SignInNeeded = "signInNeeded";
    public const string Error = "error";
    public const string NotResponding = "notResponding";

    static readonly string[] Kinds = [SignInNeeded, Error, NotResponding];
    static readonly string[] ErrorClasses = ["auth", "rate", "quota", "model", "network", "other"];
    // pending: signed in, the host not yet sure the session answers (its clear gate). There is no
    // "resumed": the host clears the attention.
    static readonly string[] Recoveries = ["checking", "signingIn", "failed", "pending"];
    // The merged protocol's (#834, #841): any other reads as refused.
    static readonly string[] StartOutcomes = ["started", "busy", "refused"];
    static readonly string[] DoneOutcomes = ["signedIn", "expired", "cancelled", "failed", "refused"];

    // Exactly these strings, compared whole and by ordinal: a lookalike (an IDN, a userinfo, another
    // port or path, a trailing dot or slash, another case) is simply not on the list.
    public static readonly IReadOnlyList<string> AllowedVerificationUris = ["https://github.com/login/device"];

    // How long "signed in" or "recovered by itself" stays.
    public static readonly TimeSpan ResolvedShown = TimeSpan.FromSeconds(60);

    public static string AgentName(AgentKind agent) => agent switch
    {
        AgentKind.Copilot => "Copilot",
        AgentKind.Claude => "Claude",
        AgentKind.Codex => "Codex",
        _ => "The agent",
    };

    // What a row says of an error, by its class (ui.md §6.13): a class without words of its own there
    // reads as "Agent error". The agent's own message is in Details only.
    public static string ErrorWords(string? errorClass) => errorClass switch
    {
        "auth" => "Not authorised",
        "rate" => "Rate limited",
        _ => "Agent error",
    };

    // Whether a session's attention still needs a person to sign in: not while it is signed in and
    // waiting for the session to confirm (recovery pending).
    public static bool NeedsSignIn(Attention? a) => a is { Kind: SignInNeeded } && a.Recovery is not "pending";

    // Signed in, waiting for the session to confirm (ui.md §6.13): a person's signin.done alone never
    // gets further than this; only the host clearing signInNeeded does.
    public static bool Confirming(Attention? a) => a is { Kind: SignInNeeded, Recovery: "pending" };

    // GitHub's device codes: ABCD-1234. Anything else is not shown.
    // \z, not $: .NET's $ also matches before a final newline, which would pass "WDJB-MJHT\n".
    [GeneratedRegex(@"^[A-Z0-9]{4}-[A-Z0-9]{4}\z", RegexOptions.CultureInvariant)]
    private static partial Regex UserCode();

    // Shapes a credential has: GitHub's tokens (ghp_, gho_, ghu_, ghs_, ghr_, github_pat_), the sk-
    // keys of OpenAI and Anthropic, a JWT, and any long unbroken run of key characters. Text that
    // holds one is not shown at all: cutting it out would still say where it was.
    // The long run first, and a JWT only from the start of a run, so a run of "eyJ" with no dot is
    // read once, not again from each "eyJ" in it (as the PWA's, #828).
    [GeneratedRegex(@"[A-Za-z0-9_+=-]{40,}|gh[pousr]_[A-Za-z0-9]{16,}|github_pat_[A-Za-z0-9_]{16,}|sk-[A-Za-z0-9_-]{16,}|(?<![A-Za-z0-9_-])eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}|\b[0-9a-fA-F]{32,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex TokenShape();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Space();

    sealed class Held(Attention? attention) { public Attention? Attention { get; } = attention; }

    static readonly ConditionalWeakTable<Session, Held> Attached = new();

    public static bool TokenShaped(string? text) => text is not null && TokenShape().IsMatch(text);

    // A host's sentence for a line of plain text: revealed (a hidden or right-to-left character is
    // drawn as its escape, so the line reads in the order it runs), one line, at most max
    // characters. Nothing else: an attention's message is composed by the host (6649775484).
    public static string? PlainText(string? text, int max)
    {
        if (text is null) { return null; }
        var one = Space().Replace(HiddenText.Reveal(text, rightToLeft: true), " ").Trim();
        if (one.Length == 0) { return null; }
        return one.Length > max ? string.Concat(one.AsSpan(0, max - 1), "…") : one;
    }

    // A machine's own words (a sign-in's reason) as plain text, the same way, and none of it when it
    // holds anything shaped like a token: those can come from the agent's sign-in.
    public static string? AgentText(string? text, int max) => text is not null && TokenShaped(text) ? null : PlainText(text, max);

    // An account's name: shown only as it is, or not at all. A login with a hidden or right-to-left
    // character could pass for another.
    public static string? AccountName(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t) || t.Length > 64 || HiddenText.HasHidden(t) || HiddenText.HasRightToLeft(t) || t.Any(char.IsWhiteSpace) || TokenShaped(t)) { return null; }
        return t;
    }

    // The allowlist's own string for a URL on it, or null.
    public static string? AllowedUri(string? uri) => uri is null ? null : AllowedVerificationUris.FirstOrDefault(u => string.Equals(u, uri, StringComparison.Ordinal));

    // The code as shown, or null when it isn't one.
    public static string? ShownCode(string? code) =>
        code is null || HiddenText.HasHidden(code) || HiddenText.HasRightToLeft(code) || !UserCode().IsMatch(code) ? null : code;

    // The code as a screen reader says it, a character at a time in its two halves: "Code: W D J B,
    // M J H T".
    public static string Spelled(string code) => "Code: " + string.Join(", ", code.Split('-').Select(half => string.Join(' ', half.ToCharArray())));

    // The session's attention, read from its JSON as received, and kept beside its record.
    public static Session Attach(Session session, JsonElement raw)
    {
        var a = raw.ValueKind == JsonValueKind.Object && raw.TryGetProperty("attention", out var at) ? ReadAttention(at) : null;
        Attached.AddOrUpdate(session, new Held(a));
        return session;
    }

    public static Attention? Of(Session session) => Attached.TryGetValue(session, out var h) ? h.Attention : null;

    // Session.attention, or null when there is none. An unknown kind is an error; an unknown error
    // class is other. signIn is phone or machine, only as the host says: a value this app doesn't
    // know, or none, makes no claim (Engineering's ruling on #800, 6648000166), so neither Sign in,
    // which the host may not relay, nor "Sign in at the machine", which only a host saying so may
    // claim (the no-RDP rule, #696). An unknown recovery is failed, as the protocol has it.
    public static Attention? ReadAttention(JsonElement a)
    {
        if (a.ValueKind != JsonValueKind.Object) { return null; }
        var kind = Str(a, "kind") is { } k && Kinds.Contains(k) ? k : Error;
        var signIn = kind == SignInNeeded;
        var error = kind == Error;
        return new Attention(
            kind,
            Time(a, "since"),
            AgentOf(Str(a, "agent")),
            signIn ? AccountName(Str(a, "account")) : null,
            PlainText(Str(a, "message"), 300),
            error ? (Str(a, "errorClass") is { } c && ErrorClasses.Contains(c) ? c : "other") : null,
            error ? Time(a, "retryAt") : null,
            signIn && Str(a, "signIn") is "phone" or "machine" ? Str(a, "signIn") : null,
            signIn ? Str(a, "recovery") switch { { } r when Recoveries.Contains(r) => r, { } => "failed", null => null } : null,
            kind == NotResponding ? Time(a, "lastEventAt") : null);
    }

    // signin.start's result. An outcome this client doesn't know reads as refused: never as started,
    // which would wait for a code, nor as machine, which would say the provider has no device flow
    // when nothing proved it.
    public static StartResult ReadStartResult(JsonElement r)
    {
        if (r.ValueKind != JsonValueKind.Object) { return new StartResult("refused", null, null); }
        var outcome = Str(r, "outcome") is { } o && StartOutcomes.Contains(o) ? o : "refused";
        var flow = Str(r, "flow") is { Length: > 0 } f ? f : null;
        // Only a refused says why: an outcome read as refused has no words of its own.
        return new StartResult(outcome is "started" or "busy" && flow is null ? "refused" : outcome, flow, Str(r, "outcome") == "refused" ? AgentText(Str(r, "reason"), 200) : null);
    }

    // signin.code's data, as it may be shown, or refused when it isn't GitHub's shape. Its three
    // fields are strict: one of the wrong type refuses it whole, so no half-read code is shown.
    // Nothing else of it is read. Null without a flow id: it is no flow's.
    public static DeviceCode? ReadCode(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object || Str(d, "flow") is not { Length: > 0 } flow) { return null; }
        if (Str(d, "userCode") is not { } code || Str(d, "verificationUri") is not { } uri || Time(d, "expiresAt") is not { } expires) { return DeviceCode.RefusedFor(flow); }
        return ShownCode(code) is { } shown && AllowedUri(uri) is { } allowed ? new DeviceCode(flow, AgentOf(Str(d, "agent")), shown, allowed, expires) : DeviceCode.RefusedFor(flow);
    }

    // signin.done's data. An unknown outcome is failed.
    public static SignInDone? ReadDone(JsonElement d)
    {
        if (d.ValueKind != JsonValueKind.Object || Str(d, "flow") is not { Length: > 0 } flow) { return null; }
        var outcome = Str(d, "outcome") is { } o && DoneOutcomes.Contains(o) ? o : "failed";
        // signin.done carries no reason in the protocol: none is read, whatever a host sends.
        return new SignInDone(flow, outcome, null, Ids(d, "resumed"), Ids(d, "continue"));
    }

    // Whether a code has run out by this device's clock: it goes then, whatever the host still says.
    public static bool Expired(DeviceCode code, DateTimeOffset now) => code.ExpiresAt is not { } at || now >= at;

    // A clock time for a line ("4:27 PM"), in this device's zone.
    public static string? Clock(DateTimeOffset? at) => at?.ToLocalTime().ToString("t", CultureInfo.CurrentCulture);

    static AgentKind AgentOf(string? agent) => agent switch
    {
        "copilot" => AgentKind.Copilot,
        "claude" => AgentKind.Claude,
        "codex" => AgentKind.Codex,
        _ => AgentKind.Unknown,
    };

    public static string WireName(AgentKind agent) => agent switch
    {
        AgentKind.Copilot => "copilot",
        AgentKind.Claude => "claude",
        AgentKind.Codex => "codex",
        _ => "unknown",
    };

    static IReadOnlyList<string> Ids(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) { return []; }
        return [.. v.EnumerateArray().Select(Text).OfType<string>().Where(s => s.Length > 0)];
    }

    static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) ? Text(v) : null;

    // A string, or null for anything else, and for one that isn't text at all: an unpaired
    // surrogate (\uD800) makes GetString throw, and these are read on the UI thread.
    static string? Text(JsonElement v)
    {
        if (v.ValueKind != JsonValueKind.String) { return null; }
        try { return v.GetString(); }
        catch (InvalidOperationException) { return null; }
    }
    // The protocol's timestamps (RFC 3339) only: a lenient parse takes much else. Kept as a
    // DateTimeOffset, so nothing the host sent beside the time reaches a view.
    static DateTimeOffset? Time(JsonElement o, string name) =>
        Str(o, name) is { } t && IsoTime().IsMatch(t) && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    [GeneratedRegex(@"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}(:[0-9]{2}(\.[0-9]{1,9})?)?(Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant)]
    private static partial Regex IsoTime();
}

// Session.attention as the app uses it (AuthState.ReadAttention). Kind is signInNeeded, error or
// notResponding; Message the host's sentence for it (AuthState.PlainText), shown in Details only.
public sealed record Attention(string Kind, DateTimeOffset? Since, AgentKind Agent, string? Account, string? Message,
    string? ErrorClass, DateTimeOffset? RetryAt, string? SignIn, string? Recovery, DateTimeOffset? LastEventAt);

// signin.start's result: Outcome is started, busy or refused.
public sealed record StartResult(string Outcome, string? Flow, string? Reason);

// A signin.code as it may be shown: GitHub's code and URL, or Refused (none of it shown).
public sealed record DeviceCode(string Flow, AgentKind Agent, string? Code, string? Uri, DateTimeOffset? ExpiresAt)
{
    public static DeviceCode RefusedFor(string flow) => new(flow, AgentKind.Unknown, null, null, null);
    // A held code that ran out before its start's result came: only that it did, never the code.
    public static DeviceCode LapsedFor(string flow, DateTimeOffset at) => new(flow, AgentKind.Unknown, null, null, at);
    public bool Refused => Code is null || Uri is null;
    public bool Lapsed => Code is null && ExpiresAt is not null;
}

// signin.done: Outcome is signedIn, expired, cancelled, failed or refused.
public sealed record SignInDone(string Flow, string Outcome, string? Reason, IReadOnlyList<string> Resumed, IReadOnlyList<string> Continued);
