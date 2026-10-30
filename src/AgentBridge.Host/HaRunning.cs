using AgentBridge.HomeAssistant;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery;

namespace AgentBridge.Host;

// Starting the Home Assistant connector with serve, when this machine's config.json turns it on
// and everything it needs is there (HaGate). Null, having said why, otherwise.
public sealed class HaRunning : IAsyncDisposable
{
    readonly HaConnector connector;
    readonly HttpClient http;
    readonly Mutex claim;

    HaRunning(HaConnector connector, HttpClient http, Mutex claim) { this.connector = connector; this.http = http; this.claim = claim; }

    public static HaRunning? Start(HostSettings settings, string folder, ISessionSource source, UsageMonitor usage,
        Func<string?>? agentHaBridge, bool devBuild, string version, Action<string> log, ISessionAnswerer? answers = null,
        Func<HaReply, CancellationToken, Task<AgentBridge.Protocol.ReplyResult>>? reply = null, ISessionStopper? stops = null,
        AgentBridge.Host.Delivery.Launch.ISessionLauncher? launcher = null)
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
            log($"the Home Assistant connector is off: another Agent Bridge host already publishes agent_bridge_{slug} for this user");
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
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        // Answers from the cards go through the host's own delivery, as an app's do; a host that
        // cannot type into sessions here serves no answer controls rather than dead ones.
        Func<AgentBridge.Protocol.AnswerRequest, CancellationToken, Task<AgentBridge.Protocol.AnswerResult>>? answer =
            answers is null or UnsupportedPlatformAnswerer ? null : answers.AnswerAsync;
        Func<AgentBridge.Protocol.StopRequest, CancellationToken, Task<AgentBridge.Protocol.StopResult>>? stop =
            stops is null or UnsupportedPlatformStopper ? null : stops.StopAsync;
        // Launches from the card go through the host's own launcher, as an app's do (its checks,
        // its one-at-a-time start, its following to registration); whether it offers launching
        // here is its own Available, asked when the connector starts.
        var launch = launcher is null ? null : new HaLauncher(() => launcher.Available, launcher.OptionsAsync,
            (request, ct) => launcher.LaunchAsync(request, "home-assistant", ct));
        var connector = new HaConnector(new HaConnectorOptions
        {
            MachineName = machine,
            Version = version,
            Dev = devBuild,
            DetailedActivityFallback = ha.DetailedActivity,
            DashboardUrlPath = ha.DashboardUrlPath,
            StateFile = Path.Combine(folder, "ha-connector.json"),
            AgentUserIds = ha.AgentUserIds,
        }, new HomeAssistantClient(ha.BaseUrl!, token, http), source, agentHaBridge!, () => usage.Current, log, answer: answer, reply: reply, stop: stop, launch: launch);
        connector.Start();
        log($"the Home Assistant connector is on: {ha.BaseUrl} as agent_bridge_{slug}{(ha.Disposable ? " (disposable)" : "")}; " +
            (answer is null ? "status only" : "status, answers" + (reply is null ? "" : ", replies") + (stop is null ? "" : ", End session")) +
            (launch is null ? "" : ", the launch card when the host launches here"));
        return new HaRunning(connector, http, claim);
    }

    public async ValueTask DisposeAsync()
    {
        await connector.DisposeAsync();
        http.Dispose();
        claim.Dispose();
    }
}
