using AgentBridge.HomeAssistant;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Updates;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

// Starting the Home Assistant connector with serve, while this machine's config.json permits it
// and everything it needs is there (HaGate). Null, having said why, otherwise.
public sealed class HaRunning : IAsyncDisposable
{
    readonly HaConnector connector;
    readonly HttpClient http;
    readonly Mutex claim;
    readonly CancellationTokenSource stop = new();
    Task watch = Task.CompletedTask;

    public Task Stopped => watch;

    HaRunning(HaConnector connector, HttpClient http, Mutex claim) { this.connector = connector; this.http = http; this.claim = claim; }

    public static HaRunning? Start(HostSettings settings, string folder, ISessionSource source, UsageMonitor? usage,
        Func<string?>? agentHaBridge, bool devBuild, string version, Action<string> log, ISessionAnswerer? answers = null,
        Func<HaReply, CancellationToken, Task<AgentBridge.Protocol.ReplyResult>>? reply = null, ISessionStopper? stops = null,
        AgentBridge.Host.Delivery.Launch.ISessionLauncher? launcher = null, IHostUpdates? updates = null, SessionDrivers? drivers = null,
        HaAuthGuard? auth = null, Func<AgentBridge.Host.Core.Load.ProcessLoadReport?>? load = null, AgentAuditLog? launchAudit = null,
        HttpMessageHandler? transport = null, TimeProvider? time = null)
    {
        foreach (var p in settings.HomeAssistantProblems) { log($"config.json: {p}"); }
        var ha = settings.HomeAssistant;
        if (HaGate.WhyNot(ha, devBuild, HaTokenStore.Exists(folder), agentHaBridge is not null) is { } why)
        {
            log($"the Home Assistant connector is {why}");
            return null;
        }
        var machine = ha.MachineName ?? Environment.MachineName;
        var slug = HaContract.MachineSlug(machine);
        // One connector per machine and user. Two (two profiles, say) would publish the same
        // machine's entities over each other.
        var claim = new Mutex(false, @"Local\AgentBridgeHaConnector_" + slug, out var first);
        if (!first)
        {
            claim.Dispose();
            log($"the Home Assistant connector is off: another {Product.Name} host already publishes agent_bridge_{slug} for this user");
            return null;
        }
        string? token;
        try { token = HaTokenStore.Load(folder); }
        catch (System.Security.Cryptography.CryptographicException e)
        {
            claim.Dispose();
            log($"the Home Assistant connector is off: its token could not be unprotected ({e.Message}); give it again with agent-bridge-host ha token set");
            return null;
        }
        if (string.IsNullOrEmpty(token))
        {
            claim.Dispose();
            log("the Home Assistant connector is off: no Home Assistant token; give it one with agent-bridge-host ha token set");
            return null;
        }
        var http = transport is null ? new HttpClient() : new HttpClient(transport);
        http.Timeout = TimeSpan.FromSeconds(15);
        // Answers from the cards go through the host's own delivery, as an app's do; a host that
        // cannot type into sessions here serves no answer controls rather than dead ones.
        Func<AgentBridge.Protocol.AnswerRequest, CancellationToken, Task<AgentBridge.Protocol.AnswerResult>>? answer =
            answers is null or UnsupportedPlatformAnswerer ? null : (request, ct) => answers.AnswerAsync(request, ct, sender: "home-assistant");
        Func<AgentBridge.Protocol.StopRequest, CancellationToken, Task<AgentBridge.Protocol.StopResult>>? stop =
            stops is null or UnsupportedPlatformStopper ? null : stops is StopService audited && launchAudit is not null
                ? (request, ct) => StopAudit.StopAsync(audited, source, launchAudit, request, "legacy-ha", "ha:connector", ct)
                : stops.StopAsync;
        // Launches from the card go through the host's own launcher, as an app's do (its checks,
        // its one-at-a-time start, its following to registration); whether it offers launching
        // here is its own Available, asked when the connector starts.
        var launch = launcher is null ? null : new HaLauncher(() => launcher.Available, launcher.OptionsAsync,
            (request, ct) => launchAudit is null ? launcher.LaunchAsync(request, "home-assistant", ct)
                : GateLaunchAudit.LaunchAsync(launcher, request, "home-assistant", launchAudit,
                    new("legacy-ha", "ha-component", "ha-component:local", launchAudit.ControlWriterRun), ct));
        var connector = new HaConnector(new HaConnectorOptions
        {
            MachineName = machine,
            Version = version,
            Dev = devBuild,
            DetailedActivityFallback = ha.DetailedActivity,
            DashboardUrlPath = ha.DashboardUrlPath,
            StateFile = Path.Combine(folder, "ha-connector.json"),
            AgentUserIds = ha.AgentUserIds,
            Drivers = drivers,
            Notify = ha.Notify,
            UsageAccountSensors = usage is null ? [] : HaContract.UsageAccountSensors(settings.Usage.CopilotAccounts.Select(a => a.Slot)),
            CheckForUpdates = ha.CheckForUpdates,
            UpdateCheck = TimeSpan.FromHours(ha.CheckHours),
            // Machine load totals leave the machine (the owner cleared that for totals only, 17:39 PT
            // Nov 9, #1150); per-session rows do not.
            PublishLoad = true,
            PublishSessionLoad = false,
        }, new HomeAssistantClient(ha.BaseUrl!, token, http, auth: auth ?? new HaAuthGuard(HaAuthGuard.FileFor(folder), log: log)), source, agentHaBridge!, usage is null ? null : () => usage.Current, log, answer: answer, reply: reply, stop: stop, launch: launch,
            updates: updates, load: load);
        connector.Start();
        log($"the Home Assistant connector is on: {ha.BaseUrl} as agent_bridge_{slug}{(ha.Disposable ? " (disposable)" : "")}; " +
            (answer is null ? "status only" : "status, answers" + (reply is null ? "" : ", replies") + (stop is null ? "" : ", End session")) +
            (launch is null ? "" : ", the launch card when the host launches here") +
            (updates is null || !ha.CheckForUpdates ? "" : ", the update buttons when this copy was installed by its updater") +
            (ha.Notify.Enabled ? $", notifications via {ha.Notify.Service}" : "") +
            (answer is not null && ha.Notify.ButtonsOn ? $", answer buttons to {ha.Notify.AnswerButtonServices.Count} phone(s)" : ""));
        var running = new HaRunning(connector, http, claim);
        running.watch = running.WatchOffAsync(folder, time ?? TimeProvider.System, log);
        return running;
    }

    async Task WatchOffAsync(string folder, TimeProvider time, Action<string> log)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                var (settings, problem) = HostSettings.Load(folder);
                if (settings.HomeAssistant.Enabled) { continue; }
                if (problem is not null) { log($"config.json: {problem}"); }
                foreach (var issue in settings.HomeAssistantProblems) { log($"config.json: {issue}"); }
                log("the Home Assistant connector is stopping (homeAssistant.enabled is not true); restart after ha on to reconnect");
                return;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            try { await connector.DisposeAsync(); }
            finally { http.Dispose(); claim.Dispose(); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        try { await watch; }
        finally { stop.Dispose(); }
    }
}
