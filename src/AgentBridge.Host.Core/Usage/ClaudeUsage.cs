using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Usage;

// Claude's session and weekly windows, as Claude Code itself last read them (#200, spike #197).
//
// Claude Code saves its last answer from Anthropic's usage endpoint in the profile's .claude.json
// (cachedUsageUtilization), and that is what is read here: no credential is opened. When the
// reading has gone stale, Claude Code is asked to read it again, with
// `claude -p "/usage" --safe-mode --no-session-persistence --output-format json`: /usage is a local
// command with a print-mode form that starts no model turn, and it refreshes Claude Code's token
// through Claude Code's own lock and compare-and-swap write. The file is then read again.
//
// Anthropic issues a new refresh token with each use and retires the old one. A refresh from
// here would sign Claude Code out or race its own write of the new one, and Anthropic's terms do
// not allow a third party to collect or intermediate those tokens, so the host never opens
// .credentials.json or Claude Code's credential store: the token read this collector used to make
// (api.anthropic.com/api/oauth/usage with Claude Code's access token) is retired.
//
// A probe is never ended by the host, on a timeout or at shutdown: one ended between the token
// response and Claude Code's write loses the rotated token, and the next refresh signs the
// machine out (spike E4). Its exit code says nothing about a sign-out either (E4: it exited 0 with
// the cached bars after one), so `claude auth status` gates the probes and says when Claude Code
// is signed out.
public sealed class ClaudeUsageCollector(UsageOptions options, TimeProvider? time = null, UsageProbes? probes = null, Action<string>? log = null) : IUsageCollector
{
    public const string SignedOutError = "Claude Code is signed out on this machine";

    // The probe, exactly: anything else, and a prompt could reach the model.
    internal static readonly string[] ProbeArguments = ["-p", "/usage", "--safe-mode", "--no-session-persistence", "--output-format", "json"];

    internal enum Auth { Unknown, ClaudeAi, OtherLogin, SignedOut, Unavailable }

    readonly TimeProvider clock = time ?? TimeProvider.System;
    readonly Action<string> say = log ?? (_ => { });
    readonly ProbePace pace = new(options);
    readonly object gate = new();
    Auth auth = Auth.Unknown;
    DateTimeOffset authCheckedAt = DateTimeOffset.MinValue;
    // When auth status first said signed out: the failure's own time (below).
    DateTimeOffset? signedOutAt;
    string? plan;
    // The saved account auth status was asked about. Another one signed in since has not been
    // asked about: what was said (signed out, the plan) is not taken as its.
    string? authKey;
    bool sessionOpen, busy;
    // A pass of auth status and the probe, while it runs; and processes it stopped waiting for
    // that have not ended yet, each of which holds the next one off.
    Task? cycle;
    int lingering;
    // A probe or auth status finished since the last read, so the file is worth reading now.
    bool finished;
    DateTimeOffset? nextDue;

    public AgentKind Agent => AgentKind.Claude;

    public void Pace(bool sessionOpen, bool busy)
    {
        lock (gate) { (this.sessionOpen, this.busy) = (sessionOpen, busy); }
    }

    public DateTimeOffset? WakeAt
    {
        get
        {
            lock (gate)
            {
                if (finished) { return DateTimeOffset.MinValue; }
                return cycle is null && lingering == 0 ? nextDue : null;
            }
        }
    }

    // Whether a probe or auth status the host started is still running (tests, and the log).
    internal bool Probing { get { lock (gate) { return cycle is not null || lingering > 0; } } }

    // The background pass, while one runs, for tests to wait on.
    internal Task Cycle { get { lock (gate) { return cycle ?? Task.CompletedTask; } } }

    public Task<AgentUsage?> CollectAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var saved = Read(options.ClaudeAccountPath, options.MaxStateBytes);
        // Never signed in to claude.ai here: nothing to show, and nothing to ask Claude Code.
        if (saved.Account is null) { return Task.FromResult<AgentUsage?>(null); }
        lock (gate) { finished = false; }
        if (probes is not null) { Consider(saved.Reading?.MeasuredAt, saved.Key, now); }
        Auth known;
        string? named;
        DateTimeOffset? outAt;
        lock (gate)
        {
            (known, named, outAt) = authKey == saved.Key ? (auth, plan, signedOutAt) : (Auth.Unknown, null, null);
        }
        var reading = saved.Reading is { } r ? r with { Plan = named ?? r.Plan } : null;
        if (known == Auth.SignedOut)
        {
            // A failure of its own, timed when the sign-out was seen and under the account's key:
            // a client keeps the last reading (from this machine or another) and shows this beside
            // it once that is stale. The error laid on the reading itself kept the reading's time,
            // which a peer already has, and a newer reading from another machine hid it.
            return Task.FromResult<AgentUsage?>(UsageRead.Failure(AgentKind.Claude, SignedOutError, outAt ?? now, accountKey: saved.Key, plan: named ?? reading?.Plan));
        }
        return Task.FromResult(reading);
    }

    // Whether to ask Claude Code now: auth status at most hourly, and only when a probe would
    // otherwise run or Claude Code was found signed out (to see it sign in again); the probe only
    // on a claude.ai login, when the pace allows.
    void Consider(DateTimeOffset? readingAt, string? key, DateTimeOffset now)
    {
        bool checkAuth, probe;
        lock (gate)
        {
            nextDue = null;
            if (cycle is not null || lingering > 0) { return; }
            var authDue = now - authCheckedAt >= options.ClaudeAuthEvery || now < authCheckedAt;
            var probeDue = pace.Due(readingAt, busy, now);
            switch (authKey == key ? auth : Auth.Unknown)
            {
                case Auth.ClaudeAi:
                    checkAuth = probeDue && authDue;
                    probe = probeDue;
                    if (!probeDue) { nextDue = pace.DueAt(readingAt, busy); }
                    break;
                case Auth.Unknown:
                    // Not yet asked, or its answer could not be read: asked again at most hourly,
                    // and no probe without it.
                    checkAuth = probe = probeDue && authDue;
                    if (!probeDue) { nextDue = pace.DueAt(readingAt, busy); }
                    else if (!authDue) { nextDue = authCheckedAt + options.ClaudeAuthEvery; }
                    break;
                default:
                    // Signed out, another kind of login, or no claude to ask: only auth status, hourly.
                    checkAuth = authDue;
                    probe = authDue && probeDue;
                    if (!authDue) { nextDue = authCheckedAt + options.ClaudeAuthEvery; }
                    break;
            }
            if (!checkAuth && !probe) { return; }
            cycle = Task.Run(() => CycleAsync(checkAuth, probe, key, now));
        }
    }

    async Task CycleAsync(bool checkAuth, bool probe, string? key, DateTimeOffset now)
    {
        try
        {
            if (checkAuth) { await CheckAuthAsync(key, now); }
            if (!probe) { return; }
            lock (gate)
            {
                if (auth != Auth.ClaudeAi || authKey != key) { return; }
                pace.Started(now);
            }
            var ok = await ProbeAsync();
            lock (gate)
            {
                if (ok) { pace.Succeeded(); }
                else { pace.Failed(clock.GetUtcNow()); }
            }
        }
        catch (Exception e)
        {
            say($"usage: asking Claude Code for its allowance failed: {e.GetType().Name}");
            lock (gate) { pace.Failed(clock.GetUtcNow()); }
        }
        finally
        {
            lock (gate)
            {
                cycle = null;
                finished = true;
            }
        }
    }

    // key: the saved account when the check began, which the answer is taken to be about.
    async Task CheckAuthAsync(string? key, DateTimeOffset now)
    {
        var (state, subscription) = await AuthStatusAsync();
        // When the answer came: auth status can take its 30 s, and a reading taken meanwhile must
        // not outrank the sign-out it then reports.
        var answeredAt = clock.GetUtcNow();
        Auth before;
        lock (gate)
        {
            var same = authKey == key;
            before = same ? auth : Auth.Unknown;
            authCheckedAt = now;
            authKey = key;
            // Not knowing is not a change: an answer that could not be read keeps what was known,
            // except that it is no claude.ai login to probe.
            auth = state == Auth.Unknown && before != Auth.ClaudeAi ? before : state;
            plan = subscription ?? (same ? plan : null);
            signedOutAt = auth == Auth.SignedOut ? (same ? signedOutAt : null) ?? answeredAt : null;
        }
        if (state == before) { return; }
        var said = state switch
        {
            Auth.SignedOut => "usage: Claude Code is signed out on this machine; it is not asked for its allowance until it signs in again",
            Auth.OtherLogin => "usage: Claude Code is not signed in with claude.ai here, so it is not asked for its allowance",
            Auth.Unavailable => "usage: claude could not be found or started, so it is not asked for its allowance",
            Auth.ClaudeAi when before is Auth.SignedOut => "usage: Claude Code is signed in again",
            _ => null,
        };
        if (said is not null) { say(said); }
    }

    // claude auth status prints its state as JSON and exits 1 when signed out (spike C-5). It only
    // reads what Claude Code stored.
    async Task<(Auth State, string? Plan)> AuthStatusAsync()
    {
        if (probes!.Find(AgentKind.Claude) is not { } exe) { return (Auth.Unavailable, null); }
        if (Environment() is not { } environment)
        {
            say($"usage: Claude Code's home ({options.ClaudeHome}) is a relative path, which a probe could not be given without changing whose sign-in it reads; not asking it");
            return (Auth.Unavailable, null);
        }
        var process = probes.Launcher.Start(new ProbeCommand(exe, ["auth", "status"], WorkingDirectory(), environment));
        if (process is null) { return (Auth.Unavailable, null); }
        if (!await EndsWithinAsync(process, options.ClaudeAuthTimeout, "claude auth status")) { return (Auth.Unknown, null); }
        using (process)
        {
            var code = await process.Exited;
            var output = await process.Output;
            return ReadAuth(code, output);
        }
    }

    internal static (Auth State, string? Plan) ReadAuth(int exitCode, string output)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(output.Trim()); }
        catch (JsonException) { return (Auth.Unknown, null); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || UsageRead.Property(root, "loggedIn") is not { } logged
                || logged.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                return (Auth.Unknown, null);
            }
            var subscription = UsageRead.String(root, "subscriptionType") is { Length: > 0 } s ? s : null;
            if (logged.ValueKind == JsonValueKind.False) { return (Auth.SignedOut, null); }
            return UsageRead.String(root, "authMethod") == "claude.ai" && exitCode == 0 ? (Auth.ClaudeAi, subscription) : (Auth.OtherLogin, subscription);
        }
    }

    // Success is Claude Code's saved reading having moved. One that did not (the endpoint is rate
    // limited, and /usage then shows the bars it last loaded) is a failure, and backs off.
    async Task<bool> ProbeAsync()
    {
        if (probes!.Find(AgentKind.Claude) is not { } exe) { return false; }
        // Taken before the look at the machine's processes and kept until the probe ends, so a host
        // serving another bridge profile cannot see none running and start a second beside it.
        if (probes.TryLease(AgentKind.Claude, options.ClaudeHome) is not { } lease)
        {
            say("usage: another host here is already asking Claude Code for its allowance; not starting another");
            return false;
        }
        var kept = false;
        try
        {
            // One at a time, on the machine and not only in this host: another host of this user, or
            // this one before a restart, may have left one running. A list that could not be read, or
            // a Claude process whose command line could not be, cannot show there is none.
            var observed = probes.Processes.Observe(AgentKind.Claude);
            if (!observed.Listed || observed.Processes.Any(p => p.CommandLine is null) || observed.Unidentified.Count > 0)
            {
                say("usage: Claude Code's processes could not all be read; not starting a /usage probe");
                return false;
            }
            if (observed.Processes.Any(p => CommandLine.MayBeClaudeUsageProbe(p.CommandLine)))
            {
                say("usage: a Claude Code /usage probe is already running here; not starting another");
                return false;
            }
            if (Environment() is not { } environment) { return false; }
            var before = FetchedAt(options.ClaudeAccountPath, options.MaxStateBytes);
            var process = probes.Launcher.Start(new ProbeCommand(exe, ProbeArguments, WorkingDirectory(), environment));
            if (process is null)
            {
                say("usage: claude could not be started to read its allowance");
                return false;
            }
            if (!await EndsWithinAsync(process, options.ClaudeProbeTimeout, "Claude Code's /usage probe"))
            {
                kept = true;
                _ = process.Exited.ContinueWith(_ => lease.Dispose(), TaskScheduler.Default);
                return false;
            }
            int code;
            using (process) { code = await process.Exited; }
            var after = FetchedAt(options.ClaudeAccountPath, options.MaxStateBytes);
            if (code == 0 && after is { } a && (before is not { } b || a > b)) { return true; }
            say(code != 0 ? $"usage: Claude Code's /usage probe exited {code}" : "usage: Claude Code's /usage probe did not read the allowance afresh (rate limited, or signed out)");
            return false;
        }
        finally
        {
            if (!kept) { lease.Dispose(); }
        }
    }

    // Waits for a process the host started, at most timeout. One still running then is left to
    // finish: it may be refreshing Claude Code's sign-in. Until it ends, nothing else is started.
    async Task<bool> EndsWithinAsync(IProbeProcess process, TimeSpan timeout, string what)
    {
        using var stop = new CancellationTokenSource();
        var limit = Task.Delay(timeout, clock, stop.Token);
        if (await Task.WhenAny(process.Exited, limit) == process.Exited)
        {
            await stop.CancelAsync();
            return true;
        }
        say($"usage: {what} has not finished in {timeout.TotalSeconds:0} s; it is left running, since it may be refreshing Claude Code's sign-in");
        lock (gate) { lingering++; }
        _ = process.Exited.ContinueWith(_ =>
        {
            process.Dispose();
            lock (gate)
            {
                lingering--;
                finished = true;
            }
        }, TaskScheduler.Default);
        return false;
    }

    string WorkingDirectory()
    {
        Directory.CreateDirectory(probes!.WorkingDirectory);
        return probes.WorkingDirectory;
    }

    Dictionary<string, string?>? Environment() => ProbeEnvironment.Home("CLAUDE_CONFIG_DIR", options.ClaudeHome, ".claude", namespaced: true);

    // What .claude.json says: whose account it is (its digest), and Claude Code's last reading of
    // its allowance when that reading is the same account's. A reading saved for another account
    // (Claude Code drops it on a switch, but a file caught mid-switch could hold one) is not shown
    // under this one.
    internal sealed record Saved(string? Account, string? Key, AgentUsage? Reading);

    internal static Saved Read(string? path, long maxBytes)
    {
        using var document = UsageRead.Json(path, maxBytes);
        if (document is null || UsageRead.Property(document.RootElement, "oauthAccount") is not { } account) { return new(null, null, null); }
        if (UsageRead.String(account, "accountUuid") is not { } uuid || string.IsNullOrWhiteSpace(uuid)) { return new(null, null, null); }
        var key = AccountKey(account);
        if (UsageRead.Property(document.RootElement, "cachedUsageUtilization") is not { } cached
            || UsageRead.String(cached, "accountUuid") is not { } cachedFor
            || !string.Equals(cachedFor.Trim(), uuid.Trim(), StringComparison.OrdinalIgnoreCase)
            || UsageRead.Number(cached, "fetchedAtMs") is not { } fetched || fetched <= 0
            || UsageRead.Property(cached, "utilization") is not { } utilization)
        {
            return new(uuid, key, null);
        }
        var measured = DateTimeOffset.FromUnixTimeMilliseconds((long)fetched);
        return new(uuid, key, Convert(utilization, key, null, measured, UsageSource.Cache));
    }

    static DateTimeOffset? FetchedAt(string? path, long maxBytes)
    {
        using var document = UsageRead.Json(path, maxBytes);
        return document is not null && UsageRead.Property(document.RootElement, "cachedUsageUtilization") is { } cached
            && UsageRead.Number(cached, "fetchedAtMs") is { } fetched && fetched > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)fetched)
            : null;
    }

    // limits is the list Claude Code itself draws, every entry of which applies to this plan,
    // the session window included: it reads 0% whenever no five-hour window is open, and
    // dropping it made Claude look as if it had only a weekly cap.
    internal static AgentUsage? Convert(JsonElement response, string? accountKey, string? plan, DateTimeOffset measuredAt, UsageSource source = UsageSource.Cache)
    {
        if (UsageRead.Property(response, "limits") is not { ValueKind: JsonValueKind.Array } limits) { return null; }
        var started = UsageRead.Property(response, "seven_day_breakdown") is { } breakdown ? UsageRead.Instant(breakdown, "window_started_at") : null;
        var windows = new List<UsageWindow>();
        foreach (var limit in limits.EnumerateArray())
        {
            if (UsageRead.Number(limit, "percent") is not { } percent) { continue; }
            var kind = UsageRead.String(limit, "kind") ?? "limit";
            var label = kind switch
            {
                "session" => "Session (5h)",
                "weekly_all" => "Weekly",
                "weekly_opus" => "Weekly (Opus)",
                _ => Capitalise(kind.Replace('_', ' ')),
            };
            windows.Add(UsageRead.Window(kind, label, percent, UsageRead.Instant(limit, "resets_at"), kind.StartsWith("weekly", StringComparison.Ordinal) ? started : null));
        }
        if (windows.Count == 0) { return null; }
        return new AgentUsage
        {
            Agent = AgentKind.Claude,
            Label = UsageRead.Label(AgentKind.Claude),
            AccountKey = accountKey,
            Plan = plan,
            Source = source,
            Windows = windows,
            MeasuredAt = measuredAt,
        };
    }

    // A short digest of the account's and its organisation's UUIDs from ~/.claude.json, never
    // either of them nor anything else there (the email address is beside them). Clients need
    // to know whether two readings are the same allowance and nothing more. The organisation
    // counts because one person in two (a personal plan and a team seat) has two allowances.
    // Anything unreadable is null, and the reading goes ungrouped.
    internal static string? AccountKey(string? path, long maxBytes)
    {
        using var document = UsageRead.Json(path, maxBytes);
        return document is not null && UsageRead.Property(document.RootElement, "oauthAccount") is { } account ? AccountKey(account) : null;
    }

    static string? AccountKey(JsonElement account)
    {
        if (UsageRead.String(account, "accountUuid") is not { } uuid || string.IsNullOrWhiteSpace(uuid)) { return null; }
        var identity = $"claude:{uuid.Trim().ToLowerInvariant()}";
        if (UsageRead.String(account, "organizationUuid") is { } org && !string.IsNullOrWhiteSpace(org)) { identity += $":{org.Trim().ToLowerInvariant()}"; }
        return System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12];
    }

    static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
