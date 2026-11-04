using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Host.Core.Intake;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;

namespace AgentBridge.Host;

// Read-only evidence shared by Settings and first-run. It never authenticates, changes hooks,
// accepts trust or starts a session. Version commands are skipped on read-only/legacy hosts.
public sealed class AgentReadinessService(
    string profile,
    IntakeState.AgentHomes homes,
    LaunchConfig config,
    IAgentLocator locator,
    ICommandRunner commands,
    Func<string?> readOnly,
    HookIntake? intake = null,
    TimeProvider? time = null) : IAsyncDisposable
{
    readonly TimeProvider time = time ?? TimeProvider.System;
    readonly SemaphoreSlim scan = new(1, 1);
    readonly ConcurrentDictionary<string, DateTimeOffset> heard = new(StringComparer.Ordinal);
    readonly CancellationTokenSource stop = new();
    AgentReadinessReport? report;
    Task loop = Task.CompletedTask;
    long scanStarted;
    long scanCompleted;
    int started;

    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(10);
    public TimeSpan VersionTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public event Action? Changed;

    public AgentReadinessReport? Snapshot => Volatile.Read(ref report) is { } current
        ? current with
        {
            Agents = [.. current.Agents.Select(a => a with
            {
                LastHookHandledAt = heard.TryGetValue(a.Id == "agency" ? "copilot" : a.Id, out var at) ? at : null,
            })],
        } : null;

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 0) { loop = Task.WhenAll(HearAsync(stop.Token), PollAsync(stop.Token)); }
    }

    async Task PollAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                await RefreshAsync(ct);
                await Task.Delay(Interval, time, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    async Task HearAsync(CancellationToken ct)
    {
        if (intake is null) { return; }
        var notices = intake.Subscribe();
        try
        {
            await foreach (var notice in notices.ReadAllAsync(ct))
            {
                heard.AddOrUpdate(notice.Agent, notice.HandledAt, (_, previous) => notice.HandledAt > previous ? notice.HandledAt : previous);
                Changed?.Invoke();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { intake.Unsubscribe(notices); }
    }

    public async Task<AgentReadinessReport> RefreshAsync(CancellationToken ct = default, bool fresh = false)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, ct);
        var before = fresh ? Interlocked.Read(ref scanStarted) : Interlocked.Read(ref scanCompleted);
        await scan.WaitAsync(lifetime.Token);
        try
        {
            // Explicit checks may share a scan only if it started after their request, so
            // queued callers observe mutations without adding another full scan each.
            if (Interlocked.Read(ref scanCompleted) > before && Snapshot is { } checkedAlready) { return checkedAlready; }
            var currentScan = Interlocked.Increment(ref scanStarted);
            var (state, problem) = IntakeState.Load(profile);
            var rows = new List<AgentReadiness>();
            foreach (var launcher in Launchers.All)
            {
                lifetime.Token.ThrowIfCancellationRequested();
                rows.Add(await CheckAsync(launcher, state, problem, lifetime.Token));
            }
            Volatile.Write(ref report, new AgentReadinessReport(time.GetUtcNow(), rows, readOnly()));
            Interlocked.Exchange(ref scanCompleted, currentScan);
        }
        finally { scan.Release(); }
        Changed?.Invoke();
        return Snapshot!;
    }

    async Task<AgentReadiness> CheckAsync(Launcher launcher, IntakeState? state, string? problem, CancellationToken ct)
    {
        var home = launcher.Tuning switch { "claude" => homes.Claude, "codex" => homes.Codex, _ => homes.Copilot };
        var row = new AgentReadiness(launcher.Id, launcher.Label, home, null, null,
            new(ReadinessState.Missing, "Not installed. Install this CLI, then check again."),
            new(ReadinessState.Unknown, "Version not checked."),
            Hooks(home, state, problem),
            new(ReadinessState.Unknown, "CLI sign-in and entitlement have not been verified."),
            new(ReadinessState.Unknown, "Folder trust, hook activation and a new session have not been verified."));
        try
        {
            var executable = locator.Find(launcher, config.Agent(launcher.Id));
            if (executable is null) { return row; }
            row = row with { Executable = executable, Installation = new(ReadinessState.Working, "Executable found using the launch configuration.") };
            using var launch = ProcessLaunch.HoldInheritableHandles();
            ct.ThrowIfCancellationRequested();
            if (readOnly() is { } why) { return row with { VersionCheck = new(ReadinessState.Blocked, $"Version command not run while the host is read-only: {why}") }; }
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(VersionTimeout);
            CommandResult result;
            var pending = commands.RunAsync(executable, ["--version"], VersionTimeout, limit.Token);
            launch.Dispose();
            try { result = await pending.WaitAsync(limit.Token); }
            catch (OperationCanceledException) when (limit.IsCancellationRequested)
            {
                // Production probes terminate their job and drain the pipes before returning.
                // Give that cleanup time to finish before releasing the scan or shutting down.
                try { await pending.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (OperationCanceledException) when (limit.IsCancellationRequested) { }
                catch (TimeoutException)
                {
                    _ = pending.ContinueWith(t => _ = t.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    ct.ThrowIfCancellationRequested();
                    return row with { VersionCheck = new(ReadinessState.Failed, "The version command timed out without confirming cleanup.") };
                }
                ct.ThrowIfCancellationRequested();
                result = new CommandResult(true, true, -1, "");
            }
            var version = result.Ran && !result.TimedOut && result.ExitCode == 0
                ? Regex.Match(result.Output, @"\b\d+\.\d+(?:\.\d+)?(?:[-+][0-9A-Za-z.-]+)?\b", RegexOptions.CultureInvariant).Value : "";
            return row with
            {
                Version = version.Length > 0 ? version : null,
                VersionCheck = version.Length > 0 ? new(ReadinessState.Working, $"Version {version}")
                    : new(ReadinessState.NeedsAction, !result.Ran ? "Installed, but the version command could not start."
                        : result.TimedOut ? "Installed, but the version command timed out."
                        : result.ExitCode != 0 ? $"Installed, but the version command exited with code {result.ExitCode}."
                        : "Installed, but the version response was not recognized."),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
            return row.Executable is null
                ? row with { Installation = new(ReadinessState.Failed, $"Detection failed: {e.Message}") }
                : row with { VersionCheck = new(ReadinessState.Failed, $"The version command failed: {e.Message}") };
        }
    }

    ReadinessCheck Hooks(string home, IntakeState? state, string? problem)
    {
        if (readOnly() is { } why) { return new(ReadinessState.Blocked, $"Hook setup is unavailable while this host is read-only: {why}"); }
        if (problem is not null) { return new(ReadinessState.Failed, problem); }
        try
        {
            byte[] bytes;
            try { bytes = File.ReadAllBytes(Path.Combine(home, "agent-bridge-owner.json")); }
            catch (Exception absent) when (absent is FileNotFoundException or DirectoryNotFoundException)
            {
                return state is null ? new(ReadinessState.NeedsAction, "Hook intake is off for this profile. Explicit setup or migration is required.")
                    : new(ReadinessState.Unknown, "Intake is enabled; this agent's registration and ownership have not been verified.");
            }
            using var doc = JsonDocument.Parse(bytes);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1
                || !root.TryGetProperty("profile", out var owner) || owner.ValueKind != JsonValueKind.String
                || owner.GetString() is not { } path || !Path.IsPathFullyQualified(path))
            {
                return new(ReadinessState.Failed, "The hook ownership record is not recognized. Inspect it before repairing setup.");
            }
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (!string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(profile)), comparison))
            {
                return new(ReadinessState.Blocked, "Another bridge profile owns this agent's hook setup. Do not overwrite its registrations.");
            }
            return state is null ? new(ReadinessState.NeedsAction, "An owned setup record exists, but this profile's hook intake is off.")
                : new(ReadinessState.Unknown, "An owned setup record exists; registration and activation still need verification.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        {
            return new(ReadinessState.Failed, $"Hook ownership could not be checked: {e.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        await loop;
        await scan.WaitAsync();
        scan.Release();
        stop.Dispose();
    }
}
