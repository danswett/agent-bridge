using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AgentBridge.Host.Core.Usage;

namespace AgentBridge.Host.Delivery.Launch;

// Only managed Mac launches use this policy. Changed executable metadata invalidates the probe;
// help/version probes never start an agent turn or read an existing session.
public sealed partial class MacLaunchCompatibility
{
    readonly ICommandRunner runner;
    readonly SemaphoreSlim gate = new(1, 1);
    readonly Dictionary<string, (DateTimeOffset At, Compatibility Value)> cache = new(StringComparer.Ordinal);

    public MacLaunchCompatibility() : this(new BoundedHelpRunner()) { }
    internal MacLaunchCompatibility(ICommandRunner runner) => this.runner = runner;

    internal sealed record Compatibility(bool CodexNoDaemon = true, IReadOnlyList<string>? Efforts = null, string? Refusal = null, string? Stamp = null);

    internal async Task<Compatibility> InspectAsync(Launcher launcher, string executable, IReadOnlyList<string> extras, CancellationToken ct)
    {
        if (launcher.Id is not ("codex" or "copilot")) { return new(); }
        // A free-form argument list can select an explicit remote server or another command.
        // No ad-hoc flag parser can prove arbitrary future CLI arguments preserve this mode.
        if (launcher.Id == "codex" && extras.Any(e => !string.IsNullOrWhiteSpace(e)))
        {
            return new(Refusal: "managed Mac Codex launches require empty launch.agents.codex.extraArgs; remove those extras at the machine so the agent stays in its owned terminal");
        }
        await gate.WaitAsync(ct);
        try
        {
            var stamp = Fingerprint(executable);
            if (stamp is null) { return Unreadable(launcher); }
            var key = launcher.Id + "\n" + stamp;
            if (cache.TryGetValue(key, out var saved) && DateTimeOffset.UtcNow - saved.At < TimeSpan.FromMinutes(1)) { return saved.Value; }
            var help = await runner.RunAsync(executable, ["--help"], TimeSpan.FromSeconds(3), ct);
            Compatibility result;
            if (!Success(help)) { result = Unreadable(launcher); }
            else if (launcher.Id == "codex")
            {
                if (NoDaemonOption().IsMatch(help.Output)) { result = new(); }
                else
                {
                    var version = await runner.RunAsync(executable, ["--version"], TimeSpan.FromSeconds(3), ct);
                    // rust-v0.140.0 tui/src/lib.rs:829-840,899-914: any CLI -c override
                    // disables implicit daemon reuse. Launchers always supplies one. Explicit
                    // --remote wins over that rule, hence the refusal of extras above.
                    result = Success(version) && version.Output.Trim() == "codex-cli 0.140.0"
                        ? new(CodexNoDaemon: false)
                        : new(Refusal: "this Codex version cannot be verified for a managed Mac launch; install a version whose --help offers --no-daemon, or use the verified 0.140.0 version with no extra arguments");
                }
            }
            else
            {
                var match = CopilotEfforts().Match(help.Output);
                var efforts = match.Success ? QuotedEffort().Matches(match.Groups[1].Value).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray() : [];
                result = efforts.Length is > 0 and <= 16
                    ? new(Efforts: efforts)
                    : new(Refusal: "this Copilot installation did not report its reasoning-effort choices; check copilot --help at the machine before launching from another device");
            }
            if (Fingerprint(executable) != stamp) { return Unreadable(launcher); }
            result = result with { Stamp = stamp };
            if (cache.Count >= 16) { cache.Clear(); }
            cache[key] = (DateTimeOffset.UtcNow, result);
            return result;
        }
        finally { gate.Release(); }
    }

    internal static bool IsCurrent(string executable, Compatibility compatibility) => compatibility.Stamp is null || Fingerprint(executable) == compatibility.Stamp;

    static Compatibility Unreadable(Launcher launcher) => new(Refusal: $"could not verify {launcher.Label}'s launch flags; check its executable and --help at the machine, then try again");
    static bool Success(CommandResult result) => result.Ran && !result.TimedOut && result.ExitCode == 0 && result.Output.Length <= BoundedHelpRunner.MaxCharacters;

    static string? Fingerprint(string executable)
    {
        try
        {
            var file = new FileInfo(executable);
            if (file.ResolveLinkTarget(true) is FileInfo target) { file = target; }
            file.Refresh();
            return file.Exists ? $"{file.FullName}\n{file.Length}\n{file.LastWriteTimeUtc.Ticks}\n{file.CreationTimeUtc.Ticks}" : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    [GeneratedRegex(@"(?m)^\s+--no-daemon(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex NoDaemonOption();
    [GeneratedRegex(@"--reasoning-effort\s+<[^>\r\n]+>[^()]{0,256}\(choices:\s*([^)]{1,512})\)", RegexOptions.CultureInvariant)]
    private static partial Regex CopilotEfforts();
    [GeneratedRegex("\"([a-z][a-z0-9_-]{0,31})\"", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedEffort();
}

// CommandRunner is intentionally general and unbounded. Help is untrusted executable
// output, so these short probes cap both pipes and include pipe completion in the deadline.
internal sealed class BoundedHelpRunner : ICommandRunner
{
    internal const int MaxCharacters = 64 * 1024;

    public async Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) { start.ArgumentList.Add(argument); }
        using var process = new Process { StartInfo = start };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            if (!process.Start()) { return new(false, false, -1, ""); }
            process.StandardInput.Close();
            var output = ReadAsync(process.StandardOutput, limit.Token);
            var errors = ReadAsync(process.StandardError, limit.Token);
            var exited = process.WaitForExitAsync(limit.Token);
            // A full pipe must cancel the other reads immediately, not wait for the process
            // to exit while it is blocked writing more output.
            await Task.WhenAll(Observe(output), Observe(errors), Observe(exited));
            return new(true, false, process.ExitCode, await output);
        }
        catch (Exception e) when (e is OperationCanceledException or InvalidDataException or IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            limit.Cancel();
            try { process.Kill(entireProcessTree: true); } catch (Exception kill) when (kill is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            cancellationToken.ThrowIfCancellationRequested();
            return new(false, e is OperationCanceledException, -1, "");
        }

        async Task Observe(Task task)
        {
            try { await task; }
            catch { limit.Cancel(); throw; }
        }
    }

    static async Task<string> ReadAsync(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer.AsMemory(), ct) is var read && read > 0)
        {
            if (text.Length + read > MaxCharacters) { throw new InvalidDataException("help output limit"); }
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }
}
