using System.Text.RegularExpressions;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Intake;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Local;

namespace AgentBridge.Host;

public sealed class AgentSignInService(
    string profile, IntakeState.AgentHomes homes, IAgentLocator locator, Action refreshPaths,
    IAgentStarter starter, ICommandRunner commands, Action<string> preflight,
    CancellationToken stopping = default)
{
    readonly SemaphoreSlim operations = new(1, 1);
    readonly Dictionary<string, Started> windows = [];

    public async Task<AgentSignInResult> OpenAsync(AgentSignInRequest request)
    {
        string[] arguments = request.Agent switch
        {
            "copilot" or "codex" => ["login"],
            "claude" => ["auth", "login"],
            _ => throw new ArgumentException("Choose Copilot, Claude or Codex; Agency sign-in is managed by your organization."),
        };
        if (!request.Confirmed) { throw new ArgumentException("Confirm opening the CLI's own sign-in flow first."); }
        if (!await operations.WaitAsync(0, stopping)) { throw new InvalidOperationException("Another CLI sign-in window is being opened. Wait for its result."); }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        limit.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            limit.Token.ThrowIfCancellationRequested();
            preflight(request.Agent);
            if (starter.Unavailable() is { } unavailable) { throw new InvalidOperationException(unavailable); }
            if (windows.TryGetValue(request.Agent, out var previous) && starter.Running(previous))
            {
                return new(true, "A sign-in window is already open for this CLI. Complete or cancel there; account access is still unverified.");
            }
            var (settings, problem) = HostSettings.Load(profile);
            if (problem is not null || settings.LaunchProblems.Count > 0)
            {
                throw new InvalidOperationException($"CLI sign-in cannot use this profile's launch settings: {problem ?? string.Join("; ", settings.LaunchProblems)}");
            }
            refreshPaths();
            var executable = locator.Find(Launchers.Find(request.Agent)!, settings.Launch.Agent(request.Agent));
            if (executable is null) { throw new InvalidOperationException("The selected CLI executable is missing. Install it or correct its configured path first."); }
            // Older versions must not interpret an unsupported login subcommand as a prompt.
            var help = await commands.RunAsync(executable, ["--help"], TimeSpan.FromSeconds(5), limit.Token);
            limit.Token.ThrowIfCancellationRequested();
            if (!help.Ran || help.TimedOut || help.ExitCode != 0
                || !Regex.IsMatch(help.Output, @"(?m)^\s*" + arguments[0] + @"(?:\s|\[|$)", RegexOptions.CultureInvariant))
            {
                return new(false, "This CLI did not confirm support for its standalone sign-in command. Update it or sign in directly in its terminal. Nothing was opened.");
            }
            preflight(request.Agent);
            var directory = PrivateFolder.Ensure(Path.Combine(profile, "cli-sign-in"),
                "the CLI sign-in folder", "no sign-in window is opened");
            var environment = AgentEnvironment.For(homes.Copilot, homes.Claude, homes.Codex,
                homes.ClaudeConfigDirExplicit, locator.ChildPath());
            var spec = new StartSpec(executable, arguments, directory, environment);
            if (starter.CommandLine(spec, out var why) is null) { throw new InvalidOperationException($"The CLI sign-in command cannot start: {why}"); }
            limit.Token.ThrowIfCancellationRequested();
            windows[request.Agent] = starter.Start(spec);
            return new(true, "The CLI's sign-in window was opened. Complete or cancel there, including any browser approval. Agent Bridge does not collect credentials. Opening or closing that window does not verify account access, entitlement, hooks or trust.");
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested && limit.IsCancellationRequested)
        {
            return new(false, "Checking CLI sign-in support timed out. No sign-in window was opened.");
        }
        finally { operations.Release(); }
    }
}
