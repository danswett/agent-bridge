using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

public enum SetupStepState { Done, Next, Waiting, Yours, Optional }

public sealed record SetupStep(string Id, string Title, SetupStepState State, string Detail, int Position = 0, int Total = 0)
{
    public string Mark => State switch
    {
        SetupStepState.Done => "Done",
        SetupStepState.Next => "Next",
        SetupStepState.Yours => "Yours to check",
        SetupStepState.Optional => "Optional",
        _ => "Later",
    };
    public string Line => $"{Title} · {Mark} · {Detail}";
    public string AccessibleName => $"{Mark}, step {Position} of {Total}, {Title}. {Detail}";
    // Nothing left for the app to say: only Done, Yours to check or Optional remain.
    public bool Settled => State is SetupStepState.Done or SetupStepState.Yours or SetupStepState.Optional;
}

// "Get started" on Settings › Agents (#384 slice 1): what a first-time person does next, read from the
// rows the page already shows. It adds no state and no host operation, so Settings stays the one place
// that changes anything, and a step is Done only on the host's evidence, never because a button was
// pressed. CLI sign-in is not evidence the host has (#691), so it is said as the person's own step and
// is never marked Done.
public static class SetupGuide
{
    public static IReadOnlyList<SetupStep> Steps(bool hostRunning, IReadOnlyList<AgentReadinessRow> agents, bool signedIn, IReadOnlyList<AgentVerificationReport>? verifications = null)
    {
        var installed = agents.Where(a => a.IsInstalled).ToList();
        var toSetUp = installed.Where(a => a.ShowSetUp || a.Offered).ToList();
        var connected = installed.Any(a => a.Setup == HookSetup.Completed || a.HooksWorking);
        // A passed new-session test (#715) is the host's own evidence that a hook arrived from a session it opened.
        var verified = verifications?.Any(v => v.State == VerificationState.Verified && installed.Any(a => a.Id == v.Agent)) == true;
        var tested = installed.Any(a => a.HooksWorking) || verified;

        var steps = new List<(string Id, string Title, bool Done, string Todo, string Done_)>
        {
            ("bridge", "Start the bridge", hostRunning, "Settings can't reach this machine's bridge yet; wait a moment or choose Start under This machine.", "Running"),
            ("install", "Install an agent's CLI", installed.Count > 0,
                agents.Any(a => a.ShowInstall) ? "Choose Install CLI on one of the agents below." : "Install Copilot CLI, Claude Code or Codex on this machine, then Check agents again.",
                $"{Names(installed)} found"),
            ("hooks", "Connect its hooks", connected,
                toSetUp.Count > 0 ? $"Choose {Verbs(toSetUp)} below. {Product.Name} adds only its own hooks." : installed.Count > 0 ? "The row below says why its hooks can't be changed from here." : "Needs an installed agent first.",
                "Set up"),
        };

        var result = new List<SetupStep>();
        var nextTaken = false;
        foreach (var (id, title, done, todo, doneText) in steps)
        {
            if (done) { result.Add(new(id, title, SetupStepState.Done, doneText)); }
            else if (!nextTaken) { nextTaken = true; result.Add(new(id, title, SetupStepState.Next, todo)); }
            else { result.Add(new(id, title, SetupStepState.Waiting, todo)); }
        }

        result.Add(new("cli-signin", "Sign in to the CLI itself", SetupStepState.Yours,
            "Open the CLI once in a terminal and sign in if it asks. Settings can't see this yet."));
        result.Add(tested
            ? new("session", "Start a new session", SetupStepState.Done, verified ? "A test session on this machine sent its hook" : "A hook arrived from a session on this machine")
            : new("session", "Start a new session", nextTaken ? SetupStepState.Waiting : SetupStepState.Next,
                "Restart the bridge if Settings asks, then start a session in the CLI; its first hook shows here as working."));
        result.Add(new("account", $"Sign in to {Product.Name} for your phone", signedIn ? SetupStepState.Done : SetupStepState.Optional,
            signedIn ? "Signed in" : "Only needed to reach this machine from another device; use Sign in below."));
        return [.. result.Select((s, i) => s with { Position = i + 1, Total = result.Count })];
    }

    static string Names(IEnumerable<AgentReadinessRow> rows) => string.Join(", ", rows.Select(r => r.Label));
    static string Verbs(IEnumerable<AgentReadinessRow> rows) => string.Join(", ", rows.Select(r => $"{r.SetUpVerb} on {r.Label}"));
}
