using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Windows;
using AgentBridge.Local;
using AgentBridge.Transport;

namespace AgentBridge.Host.Shadow;

// One sample: both views, compared, and (for run) recorded.
// witness: checks differences against the daemon's known bugs (ShadowWitness); without one, none
// is put down to them.
public sealed class ShadowRunner(DaemonReader daemon, IShadowHost host, ShadowLedger? ledger, Action<string> log, IDaemonHomeAssistant? homeAssistant = null, ShadowWitness? witness = null, TimeProvider? time = null)
{
    // Only a run judges usage percentages over time; one sample (once) compares what it sees.
    readonly ShadowMemory? memory = ledger is null ? null : new();

    public async Task<ShadowComparison> SampleAsync(CancellationToken ct = default)
    {
        var h = await host.ObserveAsync(ct);
        var d = daemon.Read();
        if (d.Available && h.Available)
        {
            var ha = await (homeAssistant ?? new NoDaemonHomeAssistant()).ReadAsync(h.Usage, ct);
            d = d with { Usage = ha.Usage, UsageProblem = ha.UsageProblem, Resumable = ha.Resumable, ResumableProblem = ha.ResumableProblem };
        }
        var comparison = ShadowCompare.Compare(d, h, memory, (time ?? TimeProvider.System).GetUtcNow());
        if (comparison.Compared && witness is not null) { comparison = comparison with { Differences = witness.Explain(d, h, comparison.Differences) }; }
        if (ledger is not null)
        {
            foreach (var m in ledger.Record(comparison, d, h))
            {
                log($"mismatch: {m.Kind}{(m.Session is null ? "" : $" on {m.Session}")} for {m.Seconds} s ({string.Join("; ", m.Details)})");
            }
        }
        return comparison;
    }

    public async Task RunAsync(TimeSpan interval, TimeProvider time, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval, time);
        var said = DateTimeOffset.MinValue;
        try
        {
            do
            {
                try { await SampleAsync(ct); }
                // A sample that fails, or a report that cannot be written this time, is tried
                // again next time: an observer that one bad read can stop watches nothing for
                // the rest of the week.
                catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { log($"sample not recorded: {e.GetType().Name}: {e.Message}"); }
                if (ledger?.Today is { } day && time.GetUtcNow() - said >= TimeSpan.FromHours(1))
                {
                    said = time.GetUtcNow();
                    log($"{day.Date}: {day.Compared} of {day.Samples} samples compared, {day.Mismatches.Count} mismatch(es), {day.Sessions.Count} session(s) seen, {day.Known.Values.Sum(k => k.Values.Sum(x => x.Samples))} known difference(s) in samples");
                }
            }
            while (await timer.WaitForNextTickAsync(ct));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}

// agent-bridge-host shadow run | once | summary (docs/host-shadow.md).
public static class ShadowCommand
{
    public const string Usage = "agent-bridge-host shadow run [--interval <s>] [--grace <s>] [--in-process] [--no-home-assistant] | shadow once [--in-process] [--no-home-assistant] [--json] | shadow summary [--days <n>] [--gate] [--json]   [--profile <name>] [--agent-ha-bridge-home <dir>]";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, SessionSourceOptions options, string version, bool dev, TextWriter output, TextWriter error)
    {
        string? Option(string name)
        {
            var i = args.ToList().IndexOf(name);
            return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
        }
        bool Flag(string name) => args.Contains(name);
        int Number(string name, int fallback, int min, int max)
        {
            var text = Option(name);
            if (text is null) { return fallback; }
            if (!int.TryParse(text, out var n) || n < min || n > max) { throw new ArgumentException($"{name} takes a whole number from {min} to {max}"); }
            return n;
        }

        var sub = args.Count > 1 ? args[1] : "";
        var profile = Option("--profile") ?? "default";
        if (!Identity.IsValidProfile(profile)) { error.WriteLine($"\"{profile}\" is not a profile name"); return 2; }
        var folder = Path.Combine(Identity.DefaultRoot, profile);
        var reports = Path.Combine(folder, "shadow");
        var paths = Option("--agent-ha-bridge-home") is { } home ? new AgentHaBridgePaths(Path.GetFullPath(home), options.CopilotHome) : AgentHaBridgePaths.ForCurrentUser(options.CopilotHome);
        var time = TimeProvider.System;
        // The daemon's usage and resume list, from Home Assistant with agent-ha-bridge's own token
        // (GET of this machine's entities only; docs/host-shadow.md).
        DaemonHaReader? reader = null;
        IDaemonHomeAssistant HomeAssistant()
        {
            if (Flag("--no-home-assistant")) { return new NoDaemonHomeAssistant(); }
            reader ??= new DaemonHaReader(Environment.MachineName);
            return new CachedDaemonHomeAssistant(paths, reader, time);
        }
        try
        {
            switch (sub)
            {
                case "summary":
                    {
                        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).DateTime);
                        // The gate looks at every report there is (up to 92 days), unless told otherwise.
                        var gate = Flag("--gate");
                        var days = Number("--days", gate ? ShadowSummary.DaysSinceFirst(reports, today) : 7, 1, 92);
                        // Read once, for both: its explanations change the counts the gate judges.
                        var config = ShadowGateConfig.Read(reports);
                        var summary = ShadowSummary.Build(reports, today, days, time.GetUtcNow(), config.Config);
                        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
                        if (!gate)
                        {
                            output.Write(Flag("--json") ? JsonSerializer.Serialize(summary, json) + Environment.NewLine : summary.Format());
                            return 0;
                        }
                        var verdict = ShadowGate.Evaluate(summary, config);
                        output.Write(Flag("--json") ? JsonSerializer.Serialize(verdict, json) + Environment.NewLine : summary.Format() + Environment.NewLine + verdict.Format());
                        return verdict.Outcome == ShadowGate.Pass ? 0 : verdict.Outcome == ShadowGate.Fail ? 1 : 3;
                    }
                case "once":
                    {
                        if (!Directory.Exists(paths.Home)) { error.WriteLine($"agent-ha-bridge is not installed here ({paths.Home}); there is nothing to compare with"); return 2; }
                        await using var host = await HostAsync(Flag("--in-process"), folder, options, version, dev, CancellationToken.None);
                        var comparison = await new ShadowRunner(new DaemonReader(paths, time), host.View, null, _ => { }, HomeAssistant(), new ShadowWitness(paths), time).SampleAsync();
                        if (Flag("--json")) { output.WriteLine(JsonSerializer.Serialize(comparison, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true })); }
                        else
                        {
                            output.WriteLine($"{(comparison.Compared ? "compared" : "not compared")}: daemon {comparison.DaemonSessions} session(s), host {comparison.HostSessions}; {comparison.Differences.Count} difference(s) now (a mismatch is one that lasts the grace; see shadow run)");
                            foreach (var d in comparison.Differences) { output.WriteLine($"  {d.Kind,-26} {d.Session ?? "-",-14} {d.Detail}{(d.Known is null ? "" : $" (known: {d.Known})")}"); }
                            foreach (var n in comparison.NotCompared) { output.WriteLine($"  not compared: {n}"); }
                        }
                        return comparison.Differences.All(d => d.Known is not null) ? 0 : 1;
                    }
                case "run":
                    {
                        if (!Directory.Exists(paths.Home)) { error.WriteLine($"agent-ha-bridge is not installed here ({paths.Home}); there is nothing to compare with"); return 2; }
                        if (!Directory.Exists(folder)) { error.WriteLine($"there is no host profile \"{profile}\" here ({folder}); start agent-bridge-host serve once first"); return 2; }
                        var interval = TimeSpan.FromSeconds(Number("--interval", 15, 5, 3600));
                        var grace = TimeSpan.FromSeconds(Number("--grace", 60, 0, 86400));
                        var ledger = new ShadowLedger(reports, grace, interval, time);
                        using var lockFile = HostLock.TryAcquire(ledger.Folder, TimeSpan.Zero, "shadow.lock");
                        if (lockFile is null) { error.WriteLine($"a shadow comparison is already running for profile \"{profile}\""); return 3; }
                        HostLog.OpenFile(Path.Combine(ledger.Folder, "shadow.log"));
                        using var stop = new CancellationTokenSource();
                        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
                        await using var host = await HostAsync(Flag("--in-process"), folder, options, version, dev, stop.Token);
                        HostLog.Write($"shadow comparison with agent-ha-bridge ({paths.Home}) against {host.Describe}, every {interval.TotalSeconds} s, grace {grace.TotalSeconds} s, {(Flag("--no-home-assistant") ? "not reading" : "reading (GET only)")} its Home Assistant entities; reports in {ledger.Folder}");
                        await new ShadowRunner(new DaemonReader(paths, time), host.View, ledger, HostLog.Write, HomeAssistant(), new ShadowWitness(paths), time).RunAsync(interval, time, stop.Token);
                        HostLog.Write("shadow comparison stopped");
                        return 0;
                    }
                default:
                    error.WriteLine("usage: " + Usage);
                    return 2;
            }
        }
        catch (ArgumentException e)
        {
            error.WriteLine(e.Message);
            return 2;
        }
        finally { reader?.Dispose(); }
    }

    sealed class HostHandle(IShadowHost view, string describe, IAsyncDisposable? owned) : IAsyncDisposable
    {
        public IShadowHost View { get; } = view;
        public string Describe { get; } = describe;
        public ValueTask DisposeAsync() => owned?.DisposeAsync() ?? ValueTask.CompletedTask;
    }

    // The running host through its pipe by default: what the user's devices actually see. In
    // process, a scan of its own, for when no host serves this profile.
    static async Task<HostHandle> HostAsync(bool inProcess, string folder, SessionSourceOptions options, string version, bool dev, CancellationToken ct)
    {
        if (!inProcess) { return new(new PipeShadowHost(LocalEndpoint.For(folder)), "the host serving this profile", null); }
        var source = new SessionSource(options);
        await source.StartAsync(ct);
        IAgentHaBridgeProbe? probe = OperatingSystem.IsWindows() ? new AgentHaBridgeProbe() : null;
        return new(new SourceShadowHost(source, probe, version, dev), "a scan in this process", source);
    }
}
