using System.Diagnostics;
using System.Globalization;
using System.Text;
using AgentBridge.Host.Core.Processes;

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
    readonly Action<string> log;

    // log: the host's log, which alone is told why the worker could not start (#995): the
    // result's detail reaches a person's receipt, and an exception's text must not (#802).
    public WorkerInjector(string? path = null, Action<string>? log = null)
    {
        this.path = path ?? Path.Combine(AppContext.BaseDirectory, FileName);
        this.log = log ?? (_ => { });
    }

    public async Task<InjectionResult> InjectAsync(int processId, KeyPlan plan, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) { return Nothing($"the injector is missing beside the host ({FileName})"); }
        await One.WaitAsync(cancellationToken);
        try
        {
            if (plan.Proceed?.Invoke() is { } stop) { return Nothing($"stopped before typing: {stop}"); }
            return await RunAsync(processId, plan);
        }
        finally { One.Release(); }
    }

    // agent-bridge-injector <pid> --screen: one attach that only reads, a "row <base64>" line
    // for each row of the visible window, then "ok".
    public async Task<ConsoleScreen> ReadScreenAsync(int processId, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) { return new ConsoleScreen(null, $"the injector is missing beside the host ({FileName})"); }
        await One.WaitAsync(cancellationToken);
        try { return await ScreenAsync(processId); }
        finally { One.Release(); }
    }

    // agent-bridge-injector <pid> --widen <columns>: one attach that types nothing and makes the
    // console's screen buffer at least that wide, "width <n>" as read back, then "ok".
    public async Task<ConsoleWidth> WidenAsync(int processId, int columns, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) { return new ConsoleWidth(null, $"the injector is missing beside the host ({FileName})", false); }
        await One.WaitAsync(cancellationToken);
        try
        {
            var start = new ProcessStartInfo(path)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            start.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
            start.ArgumentList.Add("--widen");
            start.ArgumentList.Add(columns.ToString(CultureInfo.InvariantCulture));
            Process? process;
            try { process = ProcessLaunch.Start(start); }
            catch (Exception e) { return new ConsoleWidth(null, CouldNotStart(processId, e), false); }
            if (process is null) { return new ConsoleWidth(null, NotStarted, false); }
            using (process)
            {
                process.StandardInput.Close();
                var errors = process.StandardError.ReadToEndAsync();
                int? width = null;
                string? final = null;
                using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                try
                {
                    while (await process.StandardOutput.ReadLineAsync(patience.Token) is { } line)
                    {
                        if (line.StartsWith("width ", StringComparison.Ordinal) && int.TryParse(line[6..], NumberStyles.None, CultureInfo.InvariantCulture, out var w)) { width = w; }
                        else if (line == "ok" || line.StartsWith("fail ", StringComparison.Ordinal)) { final = line; }
                    }
                    await process.WaitForExitAsync(patience.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    return new ConsoleWidth(null, "the injector stopped responding while widening the console", false);
                }
                await errors;
                if (final == "ok" && width is not null) { return new ConsoleWidth(width, null, false); }
                if (final is not null)
                {
                    var parts = final.Split(' ');
                    var stage = parts.Length > 1 ? parts[1] : "?";
                    var code = parts.Length > 2 && int.TryParse(parts[2], CultureInfo.InvariantCulture, out var c) ? c : 0;
                    // A console not yet allocated to a process just started: not attached yet.
                    return new ConsoleWidth(null, $"the console could not be widened ({stage} error {code})", stage == "attach" && code is 6 or 31 or 87);
                }
                return new ConsoleWidth(null, $"the injector stopped before widening the console (exit 0x{process.ExitCode:X8})", false);
            }
        }
        finally { One.Release(); }
    }

    async Task<ConsoleScreen> ScreenAsync(int processId)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        start.ArgumentList.Add(processId.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--screen");
        Process? process;
        try { process = ProcessLaunch.Start(start); }
        catch (Exception e) { return new ConsoleScreen(null, CouldNotStart(processId, e)); }
        if (process is null) { return new ConsoleScreen(null, NotStarted); }
        using (process)
        {
            process.StandardInput.Close();
            var errors = process.StandardError.ReadToEndAsync();
            var rows = new List<string>();
            string? final = null;
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                while (await process.StandardOutput.ReadLineAsync(patience.Token) is { } line)
                {
                    if (line.StartsWith("row ", StringComparison.Ordinal))
                    {
                        try { rows.Add(Encoding.UTF8.GetString(Convert.FromBase64String(line[4..]))); }
                        catch (FormatException) { return new ConsoleScreen(null, "the injector's screen read was garbled"); }
                    }
                    else if (line == "ok" || line.StartsWith("fail ", StringComparison.Ordinal)) { final = line; }
                }
                await process.WaitForExitAsync(patience.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return new ConsoleScreen(null, "the injector stopped responding while reading the screen");
            }
            await errors;
            if (final == "ok") { return new ConsoleScreen(rows, null); }
            if (final is not null)
            {
                var parts = final.Split(' ');
                var code = parts.Length > 2 && int.TryParse(parts[2], CultureInfo.InvariantCulture, out var c) ? c : 0;
                return new ConsoleScreen(null, Explain(parts.Length > 1 ? parts[1] : "?", code));
            }
            return new ConsoleScreen(null, $"the injector stopped before reading the screen (exit 0x{process.ExitCode:X8})");
        }
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
        try { process = ProcessLaunch.Start(start); }
        catch (Exception e) { return Nothing(CouldNotStart(processId, e)); }
        if (process is null) { return Nothing(NotStarted); }

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
                // A later send held to the screen, refused after an earlier one went: what went
                // stays, and nothing after it does.
                if (wrote && (stage, code) == ("screen", 0)) { return Interrupted(ScreenChangedPartway); }
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

    // What a send held to the screen (KeySend.Expect) comes to when the screen no longer reads so.
    public const string ScreenChanged = "the prompt on the session's screen changed before anything was typed; look again and answer, or answer at the machine";

    // The same, for a send after the first: the keys before it went, and nothing after.
    public const string ScreenChangedPartway = "the session's screen changed after part of it was typed, so nothing more was";

    // What a person can do about a failure that happened before any key was written.
    internal static string Explain(string stage, int code) => (stage, code) switch
    {
        ("attach", 5) => "the session runs as administrator, and Windows blocks typing into it from here; answer at the machine",
        ("attach", 6 or 31 or 87) => "the session's process has no console the host can reach; answer at the machine",
        ("attach", _) => $"the host could not reach the session's console (error {code}); answer at the machine",
        ("input", _) => $"the host could not open the session's console input (error {code}); answer at the machine",
        ("output", _) => $"the host could not read the session's screen (error {code}); answer at the machine",
        ("screen", 0) => ScreenChanged,
        ("screen", _) => $"the host could not read the session's screen (error {code}); answer at the machine",
        _ => $"the injector could not type ({stage} error {code}); answer at the machine",
    };

    // The worker did not start, so nothing was typed. Fixed words: each caller says it in its own
    // (a reply or an answer "the machine couldn't deliver it", DeliveryReasons.Of; Launch's folder
    // trust its own, #995), and why goes to the host's log only (#802).
    public const string NotStarted = "the injector could not start";

    string CouldNotStart(int processId, Exception e)
    {
        log($"the injector could not start for pid {processId}: {e.GetType().Name}: {e.Message}");
        return NotStarted;
    }

    static InjectionResult Nothing(string detail) => new(InjectionStatus.NothingTyped, detail);
    static InjectionResult Interrupted(string detail) => new(InjectionStatus.Interrupted, detail);
}
