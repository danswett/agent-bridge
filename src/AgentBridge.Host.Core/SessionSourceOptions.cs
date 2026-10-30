namespace AgentBridge.Host.Core;

// Where the agents keep their state, and how hard the host looks at it. Every root is
// explicit so tests (and a host serving another user's profile) never touch the real
// ~/.copilot, ~/.claude or ~/.codex by accident.
public sealed class SessionSourceOptions
{
    // ~/.copilot: session-state/<id>/ with events.jsonl, workspace.yaml and inuse.<pid>.lock.
    public required string CopilotHome { get; init; }

    // ~/.claude: projects/<slug>/<id>.jsonl.
    public required string ClaudeHome { get; init; }

    // ~/.codex: sessions/<yyyy>/<MM>/<dd>/rollout-*-<id>.jsonl.
    public required string CodexHome { get; init; }

    // Directories holding the <id>.json registrations agent-ha-bridge's Claude hooks write.
    // Each is listed itself and never its parent: the hook spool is a sibling of these
    // and must never even be enumerated.
    public IReadOnlyList<string> ClaudeRegistrationRoots { get; init; } = [];

    // The same for Codex, which also keeps <id>.approval.json markers there.
    public IReadOnlyList<string> CodexRegistrationRoots { get; init; } = [];

    // The polling fallback. FileSystemWatcher only shortens the wait; every change is
    // still found by polling, because watchers drop events (buffer overflow, network
    // drives, inotify limits) without saying so.
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);

    // At most one upsert per session per interval; the latest state always follows.
    public TimeSpan CoalesceInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public bool UseFileSystemWatcher { get; init; } = true;

    // Where discovery says what holds it and what it carried on without. Null: nowhere.
    public Action<string>? Log { get; init; }

    // Runs between reading a registration and checking it did not change meanwhile, so a test
    // can change it there. Null outside tests.
    public Action<string>? AfterRegistrationRead { get; init; }

    // The host's permissions.remoteApproval: whether Copilot's permission prompts are offered
    // to other devices to approve or deny. Off, they show as answered only at the machine.
    public bool CopilotRemoteApproval { get; init; } = true;

    // On first sight only this much of the end of a transcript is parsed. The rest is
    // skimmed for counts and the first user message, never parsed as JSON wholesale:
    // a Copilot events.jsonl of 18 MB is ordinary and some reach hundreds.
    public long InitialTailBytes { get; init; } = 4 * 1024 * 1024;

    // A skim stops after this many bytes. Beyond it, counts (turns, compactions) start
    // from what the tail shows rather than costing seconds at startup.
    public long MaxSkimBytes { get; init; } = 512L * 1024 * 1024;

    // A single transcript line longer than this is skipped rather than buffered. Tool
    // results can be megabytes; nothing the host shows needs a line this long.
    public int MaxLineBytes { get; init; } = 32 * 1024 * 1024;

    // The defaults for the current user, as each agent resolves them: COPILOT_HOME,
    // CLAUDE_CONFIG_DIR and CODEX_HOME when set, else the dot folder in the profile. The
    // registration roots are agent-ha-bridge's: its runtime folder for an installation
    // with metadata, and the temp folder for a legacy one. Both are read when present.
    public static SessionSourceOptions ForCurrentUser()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string Root(string variable, string folder) =>
            Environment.GetEnvironmentVariable(variable) is { Length: > 0 } set ? set : Path.Combine(home, folder);

        var runtime = Path.Combine(home, ".agent-ha-bridge", "runtime");
        var temp = Path.GetTempPath();
        return new SessionSourceOptions
        {
            CopilotHome = Root("COPILOT_HOME", ".copilot"),
            ClaudeHome = Root("CLAUDE_CONFIG_DIR", ".claude"),
            CodexHome = Root("CODEX_HOME", ".codex"),
            ClaudeRegistrationRoots = [Path.Combine(runtime, "agent-bridge-claude"), Path.Combine(temp, "agent-bridge-claude")],
            CodexRegistrationRoots = [Path.Combine(runtime, "agent-bridge-codex"), Path.Combine(temp, "agent-bridge-codex")],
        };
    }
}
