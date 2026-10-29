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
    bool TakesAttachments = false);

public static class Launchers
{
    public static readonly IReadOnlyList<Launcher> All =
    [
        new("agency", "Agency", AgentKind.Copilot, "agency", "copilot", TakesProfile: true, TakesAttachments: true),
        new("copilot", "Copilot", AgentKind.Copilot, "copilot", "copilot", TakesAttachments: true),
        new("claude", "Claude", AgentKind.Claude, "claude", "claude"),
        new("codex", "Codex", AgentKind.Codex, "codex", "codex", ChoosesOwnSessionId: true, NeedsPrompt: true),
    ];

    public static Launcher? Find(string? id) => All.FirstOrDefault(l => l.Id == id);

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
