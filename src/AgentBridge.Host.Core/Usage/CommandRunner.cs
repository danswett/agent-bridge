using System.Diagnostics;

namespace AgentBridge.Host.Core.Usage;

// Ran: the program started. Output is standard output alone, which for gh auth token is the
// token itself: callers never log it.
public sealed record CommandResult(bool Ran, bool TimedOut, int ExitCode, string Output);

public interface ICommandRunner
{
    Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default);
}

public sealed class CommandRunner : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(program)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) { start.ArgumentList.Add(a); }
        // Whatever gh would otherwise ask, it must not wait for an answer nobody is there to give.
        start.Environment["GH_PROMPT_DISABLED"] = "1";
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException(); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException) { return new CommandResult(false, false, -1, ""); }
        using (process)
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errors = process.StandardError.ReadToEndAsync(cancellationToken);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try { await process.WaitForExitAsync(limit.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                cancellationToken.ThrowIfCancellationRequested();
                return new CommandResult(true, true, -1, "");
            }
            await errors;
            return new CommandResult(true, false, process.ExitCode, await output);
        }
    }
}
