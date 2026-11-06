namespace AgentBridge.Host.Core.Usage;

// Where each agent keeps what its allowance is read from, and how often it is asked. The
// pacing is agent-ha-bridge's (hooks/daemon-usage.ps1), kept so the two never ask a vendor
// more often between them than that bridge alone did; usage.intervalSeconds and
// usage.idleIntervalSeconds change it as they change that bridge's (UsageSettings).
public sealed class UsageOptions
{
    // ~/.copilot: config.json names the signed-in login.
    public required string CopilotHome { get; init; }

    // Where Copilot CLI caches its last quota answer (copilot-user-cache.json), in the order
    // agent-ha-bridge probes them: the first that exists wins.
    public IReadOnlyList<string> CopilotCacheRoots { get; init; } = [];

    // ~/.claude: the Claude profile, CLAUDE_CONFIG_DIR for a probe of a profile that is not the
    // default. Its credentials are never opened (#200).
    public required string ClaudeHome { get; init; }

    // ~/.claude.json: oauthAccount names the account, which only its digest leaves here, and
    // cachedUsageUtilization is Claude Code's own last reading of the allowance.
    public string? ClaudeAccountPath { get; init; }

    // ~/.codex: sessions/**/rollout-*.jsonl, whose token_count events carry the rate limits, and
    // app-server-control/app-server-control.sock, the managed daemon's control socket.
    public required string CodexHome { get; init; }

    // Where the managed daemon listens, if it runs: Codex's own path under its home, unless set.
    public string? CodexControlSocket { get; init; }

    public string ControlSocket => CodexControlSocket ?? Path.Combine(CodexHome, "app-server-control", "app-server-control.sock");

    // The pace for an agent with a session open on this machine. The figures move while a
    // session runs (Copilot's were measured changing inside three minutes), so this is a
    // poll, and two minutes is agent-ha-bridge's compromise between "now" and asking vendors.
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(120);

    // The pace for one with none. The allowance belongs to the account, not the machine, so a
    // machine that is not spending it has little reason to keep asking.
    public TimeSpan IdleInterval { get; init; } = TimeSpan.FromSeconds(900);

    // How soon after the last ask a session opening or closing may bring the next forward, so
    // a session that keeps crashing and restarting cannot turn into a poll of its own.
    public TimeSpan MinimumGap { get; init; } = TimeSpan.FromSeconds(30);

    // How long an unchanged reading goes without being sent again. A client judges a reading
    // older than an hour stale, so an unchanged one is resent inside that, or a host still
    // confirming it would look like one that had stopped.
    public TimeSpan Republish { get; init; } = TimeSpan.FromMinutes(30);

    // How often the monitor looks at whether anything is due.
    public TimeSpan Tick { get; init; } = TimeSpan.FromSeconds(15);

    // One vendor request: a timeout, and a few attempts close together to ride out a reset
    // handshake (agent-ha-bridge measured 6 of 14 handshakes completing on one path).
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public int RequestAttempts { get; init; } = 3;
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(400);

    // gh: how long it may take to print a token, how long one that did not answer (a keyring
    // holding it at a prompt) is left alone, and how soon after that a Copilot session
    // opening here may ask again.
    public TimeSpan GhTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan GhHold { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan GhPresent { get; init; } = TimeSpan.FromMinutes(15);

    // Enough to refuse a file that is not the small state document it should be, and how much
    // of the end of a long Codex transcript is searched for its latest limits.
    public long MaxStateBytes { get; init; } = 8L * 1024 * 1024;

    // How many of Codex's newest transcripts are searched for a rate-limit event.
    public int CodexFiles { get; init; } = 5;

    // Asking the agents themselves for a reading (#200, spike #197). A probe is due only once the
    // newest reading is ProbeIdle old, at most every ProbeIdle with no session of the agent open here
    // and every ProbeBusyInterval with one. ProbeIdle is IdleInterval but never under 15 minutes:
    // usage.idleIntervalSeconds may be as short as 2 minutes for reading what the agents saved,
    // and an agent started that often would be the very load the probes were paced to avoid.
    public TimeSpan ProbeBusyInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan ProbeIdleMinimum { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan ProbeIdle => IdleInterval > ProbeIdleMinimum ? IdleInterval : ProbeIdleMinimum;

    // After a failed or rate-limited probe: this, doubling with each failure in a row, at most
    // ProbeBackoffMax. A sign-out holds the probes for ProbeBackoffMax.
    public TimeSpan ProbeBackoff { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan ProbeBackoffMax { get; init; } = TimeSpan.FromHours(2);

    // How long `claude -p /usage` is waited for. A probe still running then is a failure, and is
    // left running: it may be part-way through refreshing Claude Code's token (spike E4).
    public TimeSpan ClaudeProbeTimeout { get; init; } = TimeSpan.FromSeconds(120);

    // `claude auth status`: how often it is asked, and how long it is waited for. It only reads
    // Claude Code's stored state, and is never ended either.
    public TimeSpan ClaudeAuthEvery { get; init; } = TimeSpan.FromHours(1);
    public TimeSpan ClaudeAuthTimeout { get; init; } = TimeSpan.FromSeconds(30);

    // Codex's app-server: how long its answers are waited for before the probe reports nothing
    // (it starts by refreshing under a 20 s limit, twice when the first times out, so 40 s before
    // initialize answers is possible: E13 on Linux); how long after its last answer standard input
    // is kept open (longer than its own 15 s and 20 s refresh limits, E13); and when a process that
    // has still not answered is given up on as hung.
    public TimeSpan CodexReportTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan CodexGrace { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan CodexCeiling { get; init; } = TimeSpan.FromMinutes(10);

    public static UsageOptions For(SessionSourceOptions sessions, UsageSettings? usage = null)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        List<string> cacheRoots = [];
        if (OperatingSystem.IsWindows() && local.Length > 0) { cacheRoots.Add(Path.Combine(local, "copilot")); }
        cacheRoots.Add(Path.Combine(home, "Library", "Application Support", "copilot"));
        cacheRoots.Add(Path.Combine(home, ".local", "share", "copilot"));
        cacheRoots.Add(sessions.CopilotHome);
        return new UsageOptions
        {
            CopilotHome = sessions.CopilotHome,
            CopilotCacheRoots = cacheRoots,
            ClaudeHome = sessions.ClaudeHome,
            ClaudeAccountPath = ClaudeAccountFor(sessions.ClaudeHome, sessions.ClaudeConfigDirExplicit),
            CodexHome = sessions.CodexHome,
            Interval = (usage ?? UsageSettings.Default).Interval,
            IdleInterval = (usage ?? UsageSettings.Default).IdleInterval,
        };
    }

    // Where the Claude profile at claudeHome names its account, worked out from that profile
    // alone, so a --claude-home for another profile is never tagged with this user's account.
    // Claude Code keeps .claude.json inside CLAUDE_CONFIG_DIR when that is set, and beside
    // ~/.claude otherwise. When neither file is there the reading goes ungrouped rather than
    // under a guessed account.
    public static string ClaudeAccountFor(string claudeHome, bool? explicitConfigDir = null)
    {
        var inside = Path.Combine(claudeHome, ".claude.json");
        if (explicitConfigDir is { } explicitMode)
        {
            return explicitMode ? inside : Path.GetFullPath(Path.Combine(claudeHome, "..", ".claude.json"));
        }
        if (File.Exists(inside)) { return inside; }
        var trimmed = Path.TrimEndingDirectorySeparator(claudeHome);
        return Path.GetFileName(trimmed) == ".claude" && Path.GetDirectoryName(trimmed) is { Length: > 0 } parent
            ? Path.Combine(parent, ".claude.json")
            : inside;
    }
}