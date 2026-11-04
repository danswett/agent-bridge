using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;

namespace AgentBridge.Host;

// Only the authenticated local App reaches this; package names and sources are never supplied
// by a request. WinGet keeps its hash checks, policy, dependency handling and Windows approvals.
public sealed class AgentInstallService(
    string profile, IAgentLocator locator, Action refreshPaths, ICommandRunner commands,
    Func<string?> packageManager, Action<string> preflight, CancellationToken stopping = default)
{
    readonly SemaphoreSlim mutations = new(1, 1);
    readonly object gate = new();
    CancellationTokenSource? active;

    public bool Cancel()
    {
        lock (gate)
        {
            if (active is null) { return false; }
            active.Cancel();
            return true;
        }
    }

    public async Task<AgentInstallResult> InstallAsync(AgentInstallRequest request)
    {
        var package = request.Agent switch
        {
            "copilot" => "GitHub.Copilot",
            "claude" => "Anthropic.ClaudeCode",
            "codex" => "OpenAI.Codex",
            _ => throw new ArgumentException("Choose Copilot, Claude or Codex; Agency is installed through your organization."),
        };
        if (!request.AcceptAgreements) { throw new ArgumentException("Confirm the CLI, required runtimes and package/source agreements before installing."); }
        if (!await mutations.WaitAsync(0, stopping)) { throw new InvalidOperationException("Another CLI installation is still running."); }
        using var cancel = new CancellationTokenSource();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopping, cancel.Token);
        limit.CancelAfter(TimeSpan.FromMinutes(10));
        lock (gate) { active = cancel; }
        try
        {
            limit.Token.ThrowIfCancellationRequested();
            preflight(request.Agent);
            var (settings, problem) = HostSettings.Load(profile);
            if (problem is not null || settings.LaunchProblems.Count > 0)
            {
                throw new InvalidOperationException($"CLI installation cannot use this profile's launch settings: {problem ?? string.Join("; ", settings.LaunchProblems)}");
            }
            var launcher = Launchers.Find(request.Agent)!;
            var config = settings.Launch.Agent(request.Agent);
            refreshPaths();
            if (locator.Find(launcher, config) is { } existing)
            {
                return new(true, "An existing executable was found and left unchanged. Check its version and sign-in separately.", existing);
            }
            if (config.Path is not null)
            {
                throw new InvalidOperationException("The configured executable is missing. Correct that path before installing; no replacement was attempted.");
            }
            if (packageManager() is not { } winget)
            {
                return new(false, "Windows Package Manager is unavailable. Install or update Microsoft's App Installer, then try again. No CLI was installed.");
            }
            preflight(request.Agent);
            var result = await commands.RunAsync(winget,
                ["install", "--id", package, "--exact", "--source", "winget", "--dependency-source", "winget",
                    "--scope", "user", "--silent", "--disable-interactivity", "--no-upgrade",
                    "--accept-package-agreements", "--accept-source-agreements"],
                TimeSpan.FromMinutes(10), limit.Token);
            limit.Token.ThrowIfCancellationRequested();
            refreshPaths();
            if (!result.Ran || result.TimedOut || result.ExitCode != 0)
            {
                return new(false, !result.Ran ? "Windows Package Manager could not start. No installation was confirmed."
                    : result.TimedOut ? "Installation timed out. A runtime or CLI may already have been installed; check again before retrying."
                    : $"Windows Package Manager exited with code 0x{result.ExitCode:X8}. Check network access, package policy and required runtime approvals, then check agents again. Partial installations were not removed.");
            }
            return locator.Find(launcher, config) is { } installed
                ? new(true, "The CLI executable is now available. Its version, account, hooks and trust still need separate checks.", installed)
                : new(false, "Windows Package Manager finished, but the launch executable was not found on the refreshed PATH. Check the installation and app execution aliases before retrying.");
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested && limit.IsCancellationRequested)
        {
            return new(false, cancel.IsCancellationRequested
                ? "Installation cancelled. Windows may already have installed a runtime or CLI; check agents again. Nothing was uninstalled."
                : "Installation timed out. Windows may already have installed a runtime or CLI; check agents again. Nothing was uninstalled.");
        }
        finally
        {
            lock (gate) { active = null; }
            mutations.Release();
        }
    }
}
