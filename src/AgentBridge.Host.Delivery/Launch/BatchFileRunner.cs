using System.Diagnostics;
using AgentBridge.Host.Core.Usage;

namespace AgentBridge.Host.Delivery.Launch;

// Runs a command for its output, as CommandRunner does, except that a batch file (Agency
// installed as agency.cmd, as npm installs agents) goes through cmd.exe with
// WindowsCommandLine's quoting. Started directly, as CommandRunner starts everything, a batch
// file does not run at all, so Agency's profiles and sessions would quietly go missing.
public sealed class BatchFileRunner(ICommandRunner inner) : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows() || !WindowsCommandLine.IsBatch(program)) { return await inner.RunAsync(program, arguments, timeout, cancellationToken); }
        if (WindowsCommandLine.For(program, arguments, out _) is not { } line) { return new CommandResult(false, false, -1, ""); }
        // .NET writes the program back in quotes ahead of Arguments, as the line begins.
        var prefix = $"\"{line.Application}\" ";
        var start = new ProcessStartInfo(line.Application)
        {
            Arguments = line.CommandLine[prefix.Length..],
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
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
