using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace AgentBridge.Host.Delivery.Windows;

// Types a plan through agent-bridge-injector, a NativeAOT WinExe started for each answer.
// The host never attaches to a console itself: Windows ends every process attached to a
// console when that console's window closes (spike S1), and a person closing a session
// window while an answer is typed into it must cost that answer, not the host.
//
// The worker says "writing <n>" (flushed) before each write to the console, so the host
// knows whether anything may have landed whatever happens to the worker after that.
public sealed class WorkerInjector : IConsoleInjector
{
    public const string FileName = "agent-bridge-injector.exe";

    // One worker at a time: a process holds one console, and two answers in flight to two
    // sessions gain nothing from racing each other's keys.
    static readonly SemaphoreSlim One = new(1, 1);

    readonly string path;

    public WorkerInjector(string? path = null) =>
        this.path = path ?? Path.Combine(AppContext.BaseDirectory, FileName);

    public async Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) { return Nothing($"the injector is missing beside the host ({FileName})"); }
        await One.WaitAsync(cancellationToken);
        try { return await RunAsync(processId, plan); }
        finally { One.Release(); }
    }

    async Task<InjectionResult> RunAsync(int processId, KeyPlan plan)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        Process? process;
        try { process = Process.Start(start); }
        catch (Exception e) { return Nothing($"the injector could not start: {e.Message}"); }
        if (process is null) { return Nothing("the injector could not start"); }

        using (process)
        {
            var wrote = false;
            string? final = null;
            var errors = process.StandardError.ReadToEndAsync();
            try
            {
                await process.StandardInput.WriteAsync(plan.ToWorkerScript());
                process.StandardInput.Close();
            }
            // A worker that has already gone is reported by its output below.
            catch (IOException) { }

            void Read(string line)
            {
                if (line.StartsWith("writing", StringComparison.Ordinal)) { wrote = true; }
                else if (line == "ok" || line.StartsWith("fail ", StringComparison.Ordinal)) { final = line; }
            }

            using var patience = new CancellationTokenSource(plan.Duration + TimeSpan.FromSeconds(15));
            try
            {
                while (await process.StandardOutput.ReadLineAsync(patience.Token) is { } line) { Read(line); }
                await process.WaitForExitAsync(patience.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync();
                // Whatever it announced before it was stopped still counts.
                foreach (var line in (await process.StandardOutput.ReadToEndAsync()).Split('\n')) { Read(line.TrimEnd('\r')); }
                return wrote ? Interrupted("the injector stopped responding while typing") : Nothing("the injector stopped responding before typing");
            }
            await errors;

            if (final == "ok") { return new InjectionResult(InjectionStatus.Typed, "ok"); }
            if (final is not null)
            {
                var parts = final.Split(' ');
                var stage = parts.Length > 1 ? parts[1] : "?";
                var code = parts.Length > 2 && int.TryParse(parts[2], CultureInfo.InvariantCulture, out var c) ? c : 0;
                return wrote ? Interrupted($"the injector failed while typing ({stage} error {code})") : Nothing(Explain(stage, code));
            }
            var exit = process.ExitCode;
            // STATUS_CONTROL_C_EXIT: what Windows ends an attached process with when the
            // console it is attached to closes.
            var closed = unchecked((uint)exit) == 0xC000013A;
            if (wrote)
            {
                return Interrupted(closed ? "the session's console closed while the answer was being typed" : $"the injector stopped unexpectedly while typing (exit 0x{exit:X8})");
            }
            return Nothing(closed ? "the session's console closed before anything was typed" : $"the injector stopped before typing (exit 0x{exit:X8})");
        }
    }

    // What a person can do about a failure that happened before any key was written.
    internal static string Explain(string stage, int code) => (stage, code) switch
    {
        ("attach", 5) => "the session runs as administrator, and Windows blocks typing into it from here; answer at the machine",
        ("attach", 6 or 31 or 87) => "the session's process has no console the host can reach; answer at the machine",
        ("attach", _) => $"the host could not reach the session's console (error {code}); answer at the machine",
        ("input", _) => $"the host could not open the session's console input (error {code}); answer at the machine",
        _ => $"the injector could not type ({stage} error {code}); answer at the machine",
    };

    static InjectionResult Nothing(string detail) => new(InjectionStatus.NothingTyped, detail);
    static InjectionResult Interrupted(string detail) => new(InjectionStatus.Interrupted, detail);
}
