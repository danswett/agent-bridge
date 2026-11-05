using System.Diagnostics;
using System.Text;

namespace AgentBridge.Host.Delivery.Mac;

internal sealed record TmuxResult(int ExitCode, string Output);

internal interface ITmuxCommand
{
    Task<TmuxResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, string? standardInput = null);
}

internal sealed class TmuxCommand(string executable, string socketPath) : ITmuxCommand
{
    public async Task<TmuxResult> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken = default, string? standardInput = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false),
        };
        // No personal tmux configuration or default server: neither can redirect keys or
        // install hooks into a launch. All commands address this instance's private socket.
        foreach (var arg in new[] { "-S", socketPath, "-f", "/dev/null" }.Concat(arguments)) { info.ArgumentList.Add(EscapeArgument(arg)); }
        info.Environment.Remove("TMUX");
        using var process = Process.Start(info) ?? throw new IOException("tmux could not start");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var error = ReadBoundedAsync(process.StandardError, timeout.Token);
        var input = WriteInputAsync();
        try
        {
            await Task.WhenAll(input, output, error, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false));
        }
        catch
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            // Drain both readers before disposing their streams; never leak terminal text
            // or a launch environment through an exception's stderr detail.
            try { await Task.WhenAll(input, output, error).ConfigureAwait(false); } catch (Exception) { }
            throw;
        }

        async Task WriteInputAsync()
        {
            if (standardInput is not null) { await process.StandardInput.WriteAsync(standardInput.AsMemory(), timeout.Token).ConfigureAwait(false); }
            process.StandardInput.Close();
        }
    }

    // tmux parses a trailing semicolon as a command separator even in an argv entry.
    // Its argv parser removes exactly one preceding backslash; escape it even when the
    // caller's value already ends in backslash-semicolon. Never permit a second command.
    // https://github.com/tmux/tmux/blob/3.6a/cmd-parse.y#L1000
    internal static string EscapeArgument(string argument) => argument.EndsWith(';') ? argument[..^1] + "\\;" : argument;

    static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, token).ConfigureAwait(false) is > 0 and var count)
        {
            if (text.Length + count > 256 * 1024) { throw new IOException("tmux output exceeded the screen limit"); }
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
