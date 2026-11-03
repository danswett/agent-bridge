using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AgentBridge.Host.Delivery.Mac;

internal static class MacFileHolders
{
    internal const int MaxProcesses = 256;
    const int MaxOutput = 16 * 1024;

    internal static IReadOnlyList<int> Find(string path, IReadOnlyList<int> candidates) =>
        OperatingSystem.IsMacOS() ? FindAsync(path, candidates).GetAwaiter().GetResult() : [];

    static async Task<IReadOnlyList<int>> FindAsync(string path, IReadOnlyList<int> candidates)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0') || candidates.Count is 0 or > MaxProcesses
            || candidates.Any(pid => pid <= 0)) { return []; }
        try
        {
            var info = new ProcessStartInfo("/usr/sbin/lsof")
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                RedirectStandardInput = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            // -a intersects the explicit PID set and this exact file. -Fp asks only for
            // PIDs (and lsof's mandatory descriptor fields): neither command lines,
            // file contents nor environment are collected.
            foreach (var arg in new[] { "-nP", "-Fp", "-a", "-p", string.Join(',', candidates), "--", path })
            { info.ArgumentList.Add(arg); }
            using var process = Process.Start(info);
            if (process is null) { return []; }
            process.StandardInput.Close();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var output = ReadBoundedAsync(process.StandardOutput, timeout.Token);
            var error = ReadBoundedAsync(process.StandardError, timeout.Token);
            try
            {
                await Task.WhenAll(output, error, process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
                // A warning can mean one process was unreadable. Do not turn a partial
                // holder set into an apparently unique match, even with exit status 0.
                return process.ExitCode == 0 && (await error.ConfigureAwait(false)).Length == 0
                    ? Parse(await output.ConfigureAwait(false), candidates) : [];
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException or Win32Exception)
            {
                timeout.Cancel();
                try { process.Kill(); } catch (Exception failure) when (failure is InvalidOperationException or Win32Exception) { }
                try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch (Exception) { }
                return [];
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        { return []; }
    }

    internal static IReadOnlyList<int> Parse(string output, IReadOnlyList<int> candidates)
    {
        if (output.Length > MaxOutput || candidates.Count is 0 or > MaxProcesses) { return []; }
        var allowed = candidates.ToHashSet();
        var found = new HashSet<int>();
        int? current = null;
        var descriptor = false;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length < 2 || !int.TryParse(line.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out var value)) { return []; }
            if (line[0] == 'p' && value > 0 && allowed.Contains(value))
            {
                if (current is not null && !descriptor) { return []; }
                current = value;
                descriptor = false;
            }
            else if (line[0] == 'f' && value >= 0 && current is { } pid)
            {
                descriptor = true;
                found.Add(pid);
            }
            else { return []; }
        }
        return current is not null && descriptor ? found.ToArray() : [];
    }

    static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken token)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer, token).ConfigureAwait(false) is > 0 and var count)
        {
            if (text.Length + count > MaxOutput) { throw new IOException("file-holder output exceeded its limit"); }
            text.Append(buffer, 0, count);
        }
        return text.ToString();
    }
}
