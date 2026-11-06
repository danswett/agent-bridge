using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery.Launch;

// Every agent this host can start, and what differs between them, as agent-ha-bridge's
// $script:BridgeLaunchers table has it (hooks/session-launch.ps1). The order is the one "auto"
// prefers: Agency first, because a machine that has it expects sessions to carry an Agency
// profile. A launcher is not a session kind: Agency starts Copilot sessions.
public sealed record Launcher(
    string Id,
    string Label,
    AgentKind Kind,
    // The executable's name on PATH, without an extension.
    string Command,
    // Whose tuning table (models, efforts, contexts) it uses: Agency passes Copilot's flags on.
    string Tuning,
    // It picks its own session id, so none is invented for it (Codex).
    bool ChoosesOwnSessionId = false,
    // It creates its session only on a first message (Codex), so a launch must carry one.
    bool NeedsPrompt = false,
    bool TakesProfile = false,
    // The opening prompt can mention files as @path, as a reply's attachments are typed.
    bool TakesAttachments = false,
    // Its session registers before its window is ready, so a folder-trust question can still
    // come after (Copilot: inuse.<pid>.lock at 5.3 s, the question at 9.4 s, seen live with
    // 1.0.105). Its launch is started only once its window shows it ready.
    bool RegistersBeforeReady = false);

public static class Launchers
{
    // Set in every agent a launch starts, to its session id ("launched" for Codex, which picks
    // its own), and inherited by everything it runs, however detached: what the host's own
    // command line reads to tell that it is running inside a session (§6.3 invariant 13).
    public const string SessionVariable = "AGENT_BRIDGE_SESSION";

    public static readonly IReadOnlyList<Launcher> All =
    [
        new("agency", "Agency", AgentKind.Copilot, "agency", "copilot", TakesProfile: true, TakesAttachments: true, RegistersBeforeReady: true),
        new("copilot", "Copilot", AgentKind.Copilot, "copilot", "copilot", TakesAttachments: true, RegistersBeforeReady: true),
        new("claude", "Claude", AgentKind.Claude, "claude", "claude"),
        new("codex", "Codex", AgentKind.Codex, "codex", "codex", ChoosesOwnSessionId: true, NeedsPrompt: true),
    ];

    public static Launcher? Find(string? id) => All.FirstOrDefault(l => l.Id == id);

    // The argument Arguments puts a prompt behind on this agent's command line: -i for Copilot and
    // Agency, -- for Claude and Codex. A restore passes no prompt; one in the extra arguments would
    // make it submit one, and nothing is ever typed into a restored session automatically (#780).
    public static string PromptFlag(Launcher launcher) => launcher.Tuning is "claude" or "codex" ? "--" : "-i";

    // Why a restore may not pass on these extra arguments (launch.agents.<id>.extraArgs, imported from
    // agent-ha-bridge's newSession.extraArgs too), or null when it may. Engineering's ruling on #837
    // (r4487705409), beside PromptFlag's check: a restore opens idle with its recorded permission mode,
    // and an extra argument could submit a prompt (-i, or a bare positional one) or grant what that
    // mode does not (--allow-all, --dangerously-skip-permissions, --ask-for-approval never), and one an
    // agent adds later could do either. So every token must be an option on the agent's allowlist,
    // with its value in the expected form: no positional token, no --, and nothing after it. Said by
    // the option's name only, never a value, as the reason is logged.
    public static string? RestoreRefusal(Launcher launcher, IReadOnlyList<string> extras)
    {
        var allowed = RestoreOptions.TryGetValue(launcher.Id, out var options) ? options : NoOptions;
        var tokens = extras.Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token == "--") { return "the -- separator"; }
            if (!token.StartsWith('-')) { return "a positional argument"; }
            var eq = token.IndexOf('=');
            var name = eq > 0 ? token[..eq] : token;
            if (!allowed.TryGetValue(name, out var form)) { return OptionName(name) is { } shown ? $"the option {shown}" : "an option (its name not shown)"; }
            var value = eq > 0 ? token[(eq + 1)..] : i + 1 < tokens.Count ? tokens[++i] : null;
            if (value is null || !form(value)) { return $"a value for {name} that is not in its expected form"; }
        }
        return null;
    }

    // The options a restore passes on, by launcher id, each with the form its value must take. Only
    // ones confirmed from the agent's own --help, carrying no prompt: --add-dir <directory> on Copilot
    // (`copilot --help`), Claude (`claude --help`) and Codex (`codex --help`, and CodexValueOptions,
    // from codex-rs). Agency's pass through to its Copilot, unconfirmed, so it has none: its restores
    // are offered rather than automatic. Leave out anything unsure.
    static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, Func<string, bool>>> RestoreOptions =
        new Dictionary<string, IReadOnlyDictionary<string, Func<string, bool>>>(StringComparer.Ordinal)
        {
            ["copilot"] = new Dictionary<string, Func<string, bool>>(StringComparer.Ordinal) { ["--add-dir"] = FullPath },
            ["claude"] = new Dictionary<string, Func<string, bool>>(StringComparer.Ordinal) { ["--add-dir"] = FullPath },
            ["codex"] = new Dictionary<string, Func<string, bool>>(StringComparer.Ordinal) { ["--add-dir"] = FullPath },
        };
    static readonly IReadOnlyDictionary<string, Func<string, bool>> NoOptions = new Dictionary<string, Func<string, bool>>(StringComparer.Ordinal);

    // A directory, as a full path: never words that could be read as a prompt, nor another option.
    static bool FullPath(string value) => value.Length > 0 && value[0] != '-' && !value.Any(char.IsControl) && Path.IsPathFullyQualified(value);

    // An option's name, when it is plainly one: a short option glued to its value (-ifix) could show
    // the value.
    static string? OptionName(string name) =>
        (name.Length == 2 && char.IsAsciiLetterOrDigit(name[1])) || (name.Length > 2 && name.StartsWith("--", StringComparison.Ordinal) && name[2..].All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            ? name : null;

    // Effort names ordered weakest first, as agent-ha-bridge's $script:BridgeEffortOrder.
    public static readonly IReadOnlyList<string> EffortOrder = ["none", "minimal", "low", "medium", "high", "xhigh", "max", "ultra"];

    // Each agent's built-in lists, used when the config gives none: agent-ha-bridge's
    // $script:BridgeLauncherTuning. Copilot's model list is only "auto" there too until its
    // roster is discovered; this host offers the models its sessions have used as well.
    public static TuningTable TuningFor(string tuning) => tuning switch
    {
        "copilot" => new(["auto"], ["none", "minimal", "low", "medium", "high", "xhigh", "max"], ["default", "long_context"]),
        "claude" => new(["opus", "sonnet", "haiku", "fable"], ["low", "medium", "high", "xhigh", "max"], ["auto", "200k", "500k", "1m"]),
        "codex" => new(["gpt-6.1-sol", "gpt-6-sol", "gpt-6-luna"], ["low", "medium", "high", "xhigh", "max", "ultra"], ["200000", "400000", "1000000"]),
        _ => new([], [], []),
    };

    // The command line for a session, kept apart from starting it so it can be asserted on
    // without starting anything: agent-ha-bridge's Get-BridgeNewSessionArguments, exactly.
    //   * Copilot and Agency: --session-id <id> --banner, model, effort, context, --allow-all,
    //     extra arguments, then -i <prompt> last, so a stray extra argument cannot displace it.
    //     -i starts interactive mode and runs the prompt; resuming is the same --session-id.
    //     Agency puts `copilot [--profile-only <profile>] --session-id <id>` ahead of Copilot's.
    //   * Claude: --session-id <id> (--resume <id> to resume, as it refuses an id in use),
    //     --model, --effort, --autocompact, --dangerously-skip-permissions, extras, then
    //     -- <prompt>: a prompt typed as "--flag" must stay a prompt.
    //   * Codex: --no-daemon -c model_reasoning_summary=detailed, effort and context as -c
    //     overrides, `resume` to resume, --model, --ask-for-approval never, extras, the id to
    //     resume, then -- <prompt>.
    // Model, effort and context are already validated against what the agent offers; empty
    // means pass nothing. The prompt is already flattened to one line (LaunchProtocol.Flatten).
    public static IReadOnlyList<string> Arguments(Launcher launcher, string sessionId, bool resume, string model, string effort, string context,
        bool allowAll, IReadOnlyList<string> extras, string profile, string prompt)
    {
        var args = new List<string>();
        var extra = extras.Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
        switch (launcher.Tuning)
        {
            case "claude":
                if (sessionId.Length > 0) { args.AddRange(resume ? ["--resume", sessionId] : ["--session-id", sessionId]); }
                if (model.Length > 0) { args.AddRange(["--model", model]); }
                if (effort.Length > 0) { args.AddRange(["--effort", effort]); }
                if (context.Length > 0) { args.AddRange(["--autocompact", context]); }
                if (allowAll) { args.Add("--dangerously-skip-permissions"); }
                args.AddRange(extra);
                if (prompt.Length > 0) { args.AddRange(["--", prompt]); }
                return args;
            case "codex":
                // Without its shared background daemon, Codex runs hooks from its own window's
                // process; reasoning summaries are what a card streams.
                args.AddRange(["--no-daemon", "-c", "model_reasoning_summary=detailed"]);
                if (effort.Length > 0) { args.AddRange(["-c", $"model_reasoning_effort={effort}"]); }
                if (context.Length > 0) { args.AddRange(["-c", $"model_context_window={context}"]); }
                if (resume) { args.Add("resume"); }
                if (model.Length > 0) { args.AddRange(["--model", model]); }
                // Codex keeps its sandbox; only approvals go.
                if (allowAll) { args.AddRange(["--ask-for-approval", "never"]); }
                args.AddRange(extra);
                if (resume) { args.Add(sessionId); }
                if (prompt.Length > 0) { args.AddRange(["--", prompt]); }
                return args;
            default:
                var copilot = new List<string> { "--banner" };
                if (model.Length > 0) { copilot.AddRange(["--model", model]); }
                if (effort.Length > 0) { copilot.AddRange(["--reasoning-effort", effort]); }
                if (context.Length > 0) { copilot.AddRange(["--context", context]); }
                // --allow-all, not --allow-all-tools: the narrow flag grants tools only, and a
                // session launched with it still stopped on "allow access to this folder?" with
                // nobody watching. --allow-all is tools, paths and URLs, as Claude's and Codex's
                // flags already cover everything.
                if (allowAll) { copilot.Add("--allow-all"); }
                copilot.AddRange(extra);
                if (prompt.Length > 0) { copilot.AddRange(["-i", prompt]); }
                if (launcher.Id == "agency")
                {
                    args.Add("copilot");
                    // --profile-only, not --profile: the named profile is the whole configuration,
                    // so a launched session matches a hand-launched one instead of loading every
                    // MCP server on the machine.
                    if (profile.Length > 0) { args.AddRange(["--profile-only", profile]); }
                }
                args.AddRange(["--session-id", sessionId]);
                args.AddRange(copilot);
                return args;
        }
    }
}

public sealed record TuningTable(IReadOnlyList<string> Models, IReadOnlyList<string> Efforts, IReadOnlyList<string> Contexts);
