using System.Diagnostics;

namespace AgentBridge.Host.SignIn;

// The launch contract CopilotSignInAdapter starts (#972); it parses no output itself.
internal static class CopilotDeviceCommand
{
    public const string SideEffects = "the machine opens a browser and copies the code";

    public static ProcessStartInfo Create(string trustedExecutable, string resolvedHome)
    {
        if (!Path.IsPathFullyQualified(trustedExecutable) || !Path.IsPathFullyQualified(resolvedHome))
        {
            throw new ArgumentException("sign-in requires host-resolved absolute paths");
        }
        var start = new ProcessStartInfo(trustedExecutable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = resolvedHome,
        };
        // The installed CLI declares 'none' and forwards logLevel into its login
        // handler. Never inherit login's DEBUG default or substitute a custom OAuth flow.
        foreach (var argument in new[] { "login", "--device-code", "--host", "https://github.com", "--log-level", "none" })
        {
            start.ArgumentList.Add(argument);
        }
        start.Environment["COPILOT_HOME"] = resolvedHome;
        // Remove by name without inspecting values. This flow must not authenticate
        // using a token inherited from the hosting process.
        foreach (var name in new[] { "COPILOT_GITHUB_TOKEN", "GH_TOKEN", "GITHUB_TOKEN" }) { start.Environment.Remove(name); }
        // Nor follow the CLI's debug overrides (its browser command, API URL, offer timeout): this
        // login is the CLI's own, as shipped.
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("COPILOT_DEBUG", StringComparison.OrdinalIgnoreCase)).ToArray()) { start.Environment.Remove(name); }
        return start;
    }
}
