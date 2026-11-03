using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using AgentBridge.Host.Core.Usage;

namespace AgentBridge.Host.Delivery.Launch;

// Starts the agents' usage probes (#200): windowless, in the host's own folder, a batch file (an
// npm shim such as codex.cmd) through cmd.exe with WindowsCommandLine's quoting, as BatchFileRunner
// starts one. Unlike CommandRunner it never kills what it started, on a timeout or otherwise: an
// agent ended part-way through refreshing its sign-in can lose the rotated token and sign the
// machine out (spike #197, E4). Disposing a probe lets go of it; the process runs on.
public sealed class ProbeLauncher : IProbeLauncher
{
    // Enough for `claude auth status`; the probes' own output is not read for anything.
    const int MaxOutput = 16 * 1024;

    public IProbeProcess? Start(ProbeCommand command)
    {
        ProcessStartInfo start;
        if (OperatingSystem.IsWindows() && WindowsCommandLine.IsBatch(command.Program))
        {
            if (WindowsCommandLine.For(command.Program, command.Arguments, out _) is not { } line) { return null; }
            // .NET writes the program back in quotes ahead of Arguments, as the line begins (For
            // builds it so); a line that did not would be cut in the wrong place, so it is not run.
            var prefix = $"\"{line.Application}\" ";
            if (!line.CommandLine.StartsWith(prefix, StringComparison.Ordinal)) { return null; }
            start = new ProcessStartInfo(line.Application) { Arguments = line.CommandLine[prefix.Length..] };
        }
        else
        {
            start = new ProcessStartInfo(command.Program);
            foreach (var a in command.Arguments) { start.ArgumentList.Add(a); }
        }
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = true;
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.WorkingDirectory = command.WorkingDirectory;
        foreach (var (name, value) in command.Environment)
        {
            if (value is null) { start.Environment.Remove(name); }
            else { start.Environment[name] = value; }
        }
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException(); }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or FileNotFoundException or DirectoryNotFoundException) { return null; }
        return new Started(process, command.Stdio);
    }

    sealed class Started : IProbeProcess
    {
        readonly Process process;

        public Started(Process process, bool stdio)
        {
            this.process = process;
            Id = process.Id;
            // Read and dropped, so a probe that writes to standard error never blocks on a full pipe.
            _ = Drain(process.StandardError);
            if (stdio)
            {
                Input = process.StandardInput.BaseStream;
                Reader = process.StandardOutput.BaseStream;
                Output = Task.FromResult("");
            }
            else
            {
                // No standard input: what it would read is the end of it.
                process.StandardInput.Close();
                Output = Collect(process.StandardOutput);
            }
            Exited = Wait(process);
        }

        public int Id { get; }
        public Task<int> Exited { get; }
        public Task<string> Output { get; }
        public Stream? Input { get; }
        public Stream? Reader { get; }

        public void CloseInput()
        {
            try { process.StandardInput.Close(); }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        }

        // Lets go of the process, never ends it.
        public void Dispose() => process.Dispose();

        static async Task<int> Wait(Process process)
        {
            await process.WaitForExitAsync();
            return process.ExitCode;
        }

        static async Task Drain(StreamReader reader)
        {
            var buffer = new char[4096];
            try { while (await reader.ReadAsync(buffer) > 0) { } }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        }

        static async Task<string> Collect(StreamReader reader)
        {
            var text = new StringBuilder();
            var buffer = new char[4096];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer)) > 0)
                {
                    if (text.Length < MaxOutput) { text.Append(buffer, 0, Math.Min(read, MaxOutput - text.Length)); }
                }
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
            return text.ToString();
        }
    }
}
