using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Hooks;

// perf-hook (quality/perf.md): how long an agent waits on agent-bridge-host-hook, from starting it
// to its reply and to its exit.
//
//   dotnet run -c Release --project tests/AgentBridge.PerfHook -- --hook <published hook>
//       [--runs 50] [--cold-runs 20] [--warmup 3] [--out <file.json>] [--work <folder>]
//
// Every copy of the hook runs from a folder of its own made here, beside a pointer to a runtime
// folder of its own, as HookProcessTests does: nothing reaches a real installation's spool,
// heartbeat or log. The host is stood in for by its heartbeat, which is all the hook reads of
// it: "up" writes it before each run and empties the spool after (as the intake would), "down"
// leaves none. Warm runs reuse one copy; each cold run is the first start of a fresh copy, as
// after an install or an update (the OS's file cache still holds the bytes just copied, so this
// is the cost of a new executable, Defender's scan of it on Windows, not of a cold disk).

var options = Options.Parse(args);
if (options is null)
{
    Console.Error.WriteLine("usage: --hook <published agent-bridge-host-hook> [--runs N] [--cold-runs N] [--warmup N] [--out file.json] [--work folder]");
    Console.Error.WriteLine("       (or AGENT_BRIDGE_HOOK=<path>, as HookProcessTests reads it)");
    return 2;
}

// A Copilot permission request as its hook receives one: the hook Copilot runs before every tool
// call, so the one an agent waits on most. Its reply is {} whether or not it spools.
const string Agent = "copilot";
const string Hook = "permission";
const string Reply = "{}\n";
var eventJson = JsonSerializer.SerializeToUtf8Bytes(new
{
    sessionId = "66666666-6666-4666-8666-666666666666",
    timestamp = 1793720000000,
    cwd = @"C:\Users\someone\repos\agent-bridge",
    toolName = "powershell",
    toolArgs = JsonSerializer.Serialize(new { command = "dotnet test AgentBridge.slnx -c Release --filter \"Category!=Process\"", description = "Run the tests", mode = "sync", initial_wait = 120 }),
});

var root = Path.Combine(options.Work ?? Path.GetTempPath(), "abperf-" + Guid.NewGuid().ToString("N")[..12]);
var runtime = Path.Combine(root, "runtime");
Directory.CreateDirectory(runtime);
try
{
    var source = Path.GetDirectoryName(Path.GetFullPath(options.Hook))!;
    var exeName = Path.GetFileName(options.Hook);
    var warmExe = Install(Path.Combine(root, "warm"));
    var version = RunVersion(warmExe);

    var scenarios = new[]
    {
        new Scenario("warm, host up", Cold: false, HostUp: true, options.Runs),
        new Scenario("warm, host down", Cold: false, HostUp: false, options.Runs),
        new Scenario("cold, host up", Cold: true, HostUp: true, options.ColdRuns),
        new Scenario("cold, host down", Cold: true, HostUp: false, options.ColdRuns),
    };

    for (var i = 0; i < options.Warmup; i++)
    {
        Measure(warmExe, hostUp: true);
        Measure(warmExe, hostUp: false);
    }

    // Round robin, so a moment of noise on the machine lands on every scenario alike rather than
    // on whichever was running then.
    var cold = 0;
    for (var i = 0; i < scenarios.Max(s => s.Runs); i++)
    {
        foreach (var s in scenarios.Where(s => i < s.Runs))
        {
            var exe = s.Cold ? Install(Path.Combine(root, $"cold-{cold++}")) : warmExe;
            s.Add(Measure(exe, s.HostUp));
        }
    }

    var result = new JsonObject
    {
        ["suite"] = "perf-hook",
        ["schema"] = 1,
        ["platform"] = Platform(),
        ["measuredAt"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
        ["environment"] = new JsonObject
        {
            ["runner"] = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ? "github-actions" : "local",
            ["os"] = RuntimeInformation.OSDescription,
            ["processors"] = Environment.ProcessorCount,
        },
        ["hook"] = new JsonObject
        {
            ["file"] = exeName,
            ["version"] = version,
            ["agent"] = Agent,
            ["hook"] = Hook,
            ["eventBytes"] = eventJson.Length,
        },
        ["warmup"] = options.Warmup,
        ["unit"] = "ms",
        // reply: from starting the process to the end of its output, which is when the agent has
        // its answer; exit: to the process having exited.
        ["scenarios"] = new JsonArray([.. scenarios.Select(s => s.ToJson())]),
    };
    var json = result.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    if (options.Out is { } outPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(outPath, json);
    }

    Console.WriteLine($"perf-hook: {exeName} {version}, {Platform()}, {Agent}/{Hook}");
    Console.WriteLine();
    Console.WriteLine("| Scenario | Runs | Reply p50 | p95 | max | Exit p50 | p95 | max |");
    Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|---:|");
    foreach (var s in scenarios) { Console.WriteLine(s.Row()); }
    return 0;

    string Install(string bin)
    {
        Directory.CreateDirectory(bin);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            // The hook and, for a build that is not NativeAOT, what it loads; never its symbols.
            var wanted = name.StartsWith("agent-bridge-host-hook", StringComparison.Ordinal) || name == "AgentBridge.Hooks.dll";
            if (wanted && !name.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) && !name.EndsWith(".dbg", StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(file, Path.Combine(bin, name));
            }
        }
        File.WriteAllBytes(Path.Combine(bin, HookRunner.PointerFile), HookRunner.Pointer(runtime));
        return Path.Combine(bin, exeName);
    }

    (double Reply, double Exit) Measure(string exe, bool hostUp)
    {
        var heartbeat = SpoolContract.HeartbeatPath(runtime);
        var spool = SpoolContract.SpoolDirectory(runtime);
        if (Directory.Exists(spool)) { Directory.Delete(spool, recursive: true); }
        if (hostUp) { File.WriteAllText(heartbeat, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
        else if (File.Exists(heartbeat)) { File.Delete(heartbeat); }

        var start = new ProcessStartInfo(exe) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        start.ArgumentList.Add(Agent);
        start.ArgumentList.Add(Hook);
        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        process.StandardInput.BaseStream.Write(eventJson);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEnd();
        var reply = watch.Elapsed.TotalMilliseconds;
        process.WaitForExit();
        var exit = watch.Elapsed.TotalMilliseconds;

        // Timing the wrong path would be worse than timing nothing: each run must have answered
        // as the hook does, and spooled exactly when the host was up.
        var spooled = Directory.Exists(spool) ? Directory.GetFiles(spool, "*.json").Length : 0;
        if (process.ExitCode != 0 || stdout != Reply || spooled != (hostUp ? 1 : 0))
        {
            throw new InvalidOperationException($"the hook did not behave as expected (host {(hostUp ? "up" : "down")}): exit {process.ExitCode}, reply {JsonSerializer.Serialize(stdout)}, {spooled} spooled");
        }
        return (reply, exit);
    }

    static string RunVersion(string exe)
    {
        var start = new ProcessStartInfo(exe, "--version") { RedirectStandardOutput = true, RedirectStandardInput = true, UseShellExecute = false };
        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var text = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return text;
    }
}
finally
{
    try { Directory.Delete(root, recursive: true); }
    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.Error.WriteLine($"left {root}: {e.Message}"); }
}

// As quality/catalog.yml names platforms.
static string Platform() =>
    (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "macos" : "linux") + "-" + RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

sealed record Options(string Hook, int Runs, int ColdRuns, int Warmup, string? Out, string? Work)
{
    public static Options? Parse(string[] args)
    {
        string? hook = Environment.GetEnvironmentVariable("AGENT_BRIDGE_HOOK");
        int runs = 50, coldRuns = 20, warmup = 3;
        string? output = null, work = null;
        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--hook" when value is not null: hook = value; i++; break;
                case "--runs" when int.TryParse(value, out var n) && n > 0: runs = n; i++; break;
                case "--cold-runs" when int.TryParse(value, out var n) && n > 0: coldRuns = n; i++; break;
                case "--warmup" when int.TryParse(value, out var n) && n >= 0: warmup = n; i++; break;
                case "--out" when value is not null: output = value; i++; break;
                case "--work" when value is not null: work = value; i++; break;
                default: return null;
            }
        }
        return hook is { Length: > 0 } && File.Exists(hook) ? new Options(hook, runs, coldRuns, warmup, output, work) : null;
    }
}

sealed record Scenario(string Name, bool Cold, bool HostUp, int Runs)
{
    readonly List<double> _reply = [];
    readonly List<double> _exit = [];

    public void Add((double Reply, double Exit) run)
    {
        _reply.Add(run.Reply);
        _exit.Add(run.Exit);
    }

    public JsonObject ToJson() => new()
    {
        ["name"] = Name,
        ["start"] = Cold ? "cold" : "warm",
        ["host"] = HostUp ? "up" : "down",
        ["runs"] = _reply.Count,
        ["replyMs"] = Stats(_reply),
        ["exitMs"] = Stats(_exit),
    };

    public string Row()
    {
        var (r, e) = (Sorted(_reply), Sorted(_exit));
        return $"| {Name} | {_reply.Count} | {P(r, 0.50):F1} | {P(r, 0.95):F1} | {r[^1]:F1} | {P(e, 0.50):F1} | {P(e, 0.95):F1} | {e[^1]:F1} |";
    }

    static JsonObject Stats(List<double> values)
    {
        var s = Sorted(values);
        return new JsonObject { ["p50"] = Math.Round(P(s, 0.50), 2), ["p95"] = Math.Round(P(s, 0.95), 2), ["max"] = Math.Round(s[^1], 2) };
    }

    static double[] Sorted(List<double> values) => [.. values.Order()];

    // Nearest rank: a value that was measured, never one between two.
    static double P(double[] sorted, double p) => sorted[Math.Max(0, (int)Math.Ceiling(p * sorted.Length) - 1)];
}
