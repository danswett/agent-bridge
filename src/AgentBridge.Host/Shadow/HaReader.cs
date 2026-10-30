using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentBridge.HomeAssistant;

namespace AgentBridge.Host.Shadow;

// agent-ha-bridge keeps two things it shows only in Home Assistant: each agent's usage (one
// sensor per agent) and the resume list (the options of its launch card's resume select). The
// owner approved reading those, and only those, with agent-ha-bridge's own token, read from its
// config.json at the moment of each read. Never a POST, never another entity, never the token
// in a log, a report or an exception, and an unreachable Home Assistant is "not compared", not
// a disagreement.

// Where to read and with what. Deliberately not a record: a record's ToString would print the
// token into whatever logged it.
public sealed class HomeAssistantAccess(Uri baseUrl, string token)
{
    public Uri BaseUrl { get; } = baseUrl;
    internal string Token { get; } = token;
    // How the daemon paces what it publishes there, from the same config.
    public DaemonUsagePace UsagePace { get; init; } = DaemonUsagePace.Default;
    public override string ToString() => $"Home Assistant at {BaseUrl.Host}";
}

// How long agent-ha-bridge's daemon can leave a usage sensor as it is (Sync-DaemonUsage). It asks
// the vendor at most Poll apart (usage.idleIntervalSeconds while no session of that agent is
// open, which is the slower) and publishes a change at once, but an unchanged figure only once
// Reassert has passed: half an hour with no dashboard.publication, two intervals with one. So a
// sensor whose measured_at is old still says what the daemon found at its last poll, and that
// poll was no more than Poll ago; measured_at alone made a reading look stale for half an hour
// at a time, and a host reading every fifteen minutes might never land beside it (#40). Past
// Reassert and a Poll, the daemon has stopped confirming it (another machine reads the account,
// or the vendor is failing), and only measured_at counts.
public sealed record DaemonUsagePace(TimeSpan Poll, TimeSpan Reassert)
{
    public static DaemonUsagePace Default { get; } = new(TimeSpan.FromSeconds(900), TimeSpan.FromSeconds(1800));

    static readonly TimeSpan Slack = TimeSpan.FromMinutes(1);

    // The latest moment the figure is known to have held, read at readAt; null when that is no
    // later than measuredAt itself.
    public DateTimeOffset? ConfirmedAt(DateTimeOffset? measuredAt, DateTimeOffset readAt) =>
        measuredAt is { } m && readAt - m <= Reassert + Poll + Slack && readAt - Poll - Slack is var c && c > m ? c : null;

    // Past Reassert and a Poll: the daemon is no longer reading it here, and what it shows is no
    // reading of now to compare with.
    public bool Lapsed(DateTimeOffset? measuredAt, DateTimeOffset readAt) => measuredAt is { } m && readAt - m > Reassert + Poll + Slack;
}

public static class AgentHaBridgeConfig
{
    // homeAssistant.baseUrl and homeAssistant.token from agent-ha-bridge's config.json, and
    // nothing else from it. Read each time, so a token the user rotates is the one used.
    public static (HomeAssistantAccess? Access, string? Problem) Read(AgentHaBridgePaths paths)
    {
        var text = DaemonReader.ReadShared(Path.Combine(paths.Home, "config.json"), 1024 * 1024);
        if (text is null) { return (null, "agent-ha-bridge's config could not be read"); }
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("homeAssistant", out var ha) || ha.ValueKind != JsonValueKind.Object)
            {
                return (null, "agent-ha-bridge's config names no Home Assistant");
            }
            var url = ha.TryGetProperty("baseUrl", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            var token = ha.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (string.IsNullOrWhiteSpace(token)) { return (null, "agent-ha-bridge's config holds no Home Assistant token"); }
            // The API is read from the root (ReadOnlyHomeAssistantHandler), so an address with a
            // path is not one it can use.
            if (!Uri.TryCreate(url, UriKind.Absolute, out var baseUrl) || baseUrl.Scheme is not ("https" or "http") || baseUrl.Query.Length > 0 || baseUrl.AbsolutePath.TrimEnd('/').Length > 0)
            {
                return (null, "agent-ha-bridge's config holds no usable Home Assistant address");
            }
            baseUrl = new Uri(baseUrl.GetLeftPart(UriPartial.Authority) + "/");
            return (new HomeAssistantAccess(baseUrl, token.Trim()) { UsagePace = Pace(doc.RootElement) }, null);
        }
        // The parser's message can quote the file, and the file holds the token.
        catch (JsonException) { return (null, "agent-ha-bridge's config is not valid JSON"); }
    }

    // usage.intervalSeconds and usage.idleIntervalSeconds as Get-BridgeUsageSetting reads them (a
    // number, or text that is one), the idle pace never quicker than the busy one; and whether a
    // dashboard.publication is configured, which shortens the daemon's re-assert to two intervals.
    static DaemonUsagePace Pace(JsonElement root)
    {
        var usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object ? u : default;
        var interval = Seconds(usage, "intervalSeconds", 120);
        var idle = Math.Max(interval, Seconds(usage, "idleIntervalSeconds", 900));
        var published = root.TryGetProperty("dashboard", out var d) && d.ValueKind == JsonValueKind.Object
            && d.TryGetProperty("publication", out var p) && p.ValueKind != JsonValueKind.Null;
        return new(TimeSpan.FromSeconds(idle), TimeSpan.FromSeconds(published ? interval * 2 : 1800));

        static double Seconds(JsonElement section, string name, double fallback)
        {
            if (section.ValueKind != JsonValueKind.Object || !section.TryGetProperty(name, out var v)) { return fallback; }
            var x = v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) ? n
                : v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s
                : double.NaN;
            return double.IsFinite(x) && x > 0 ? x : fallback;
        }
    }
}

// A request the shadow comparison was about to make that is not a read of an allowed entity.
// Thrown before anything is sent; a bug, never a condition to carry on from.
public sealed class HomeAssistantRequestRefusedException(string message) : InvalidOperationException(message);

// Every request the comparison makes of Home Assistant passes through this, and only a GET of
// /api/states/<one of the named entities>, with no body and no query, gets through. It is the
// enforcement, not the reader's good behaviour: anything else is refused before it leaves.
public sealed class ReadOnlyHomeAssistantHandler(IEnumerable<string> entities, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    readonly HashSet<string> allowed = entities.ToHashSet(StringComparer.Ordinal);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Check(request);
        return base.SendAsync(request, cancellationToken);
    }

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Check(request);
        return base.Send(request, cancellationToken);
    }

    void Check(HttpRequestMessage request)
    {
        if (request.Method != HttpMethod.Get) { throw new HomeAssistantRequestRefusedException($"a {request.Method} to Home Assistant was refused: the shadow comparison only reads"); }
        if (request.Content is not null) { throw new HomeAssistantRequestRefusedException("a request to Home Assistant with a body was refused: the shadow comparison only reads"); }
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            throw new HomeAssistantRequestRefusedException("a request to Home Assistant for anything but an entity's state was refused");
        }
        // Exactly /api/states/<entity>, from the root: Home Assistant serves its API there, and
        // anything under another prefix is not the read that was approved.
        const string States = "/api/states/";
        var path = uri.AbsolutePath;
        if (!path.StartsWith(States, StringComparison.Ordinal) || !allowed.Contains(Uri.UnescapeDataString(path[States.Length..])))
        {
            throw new HomeAssistantRequestRefusedException("a request to Home Assistant for an entity the shadow comparison does not read was refused");
        }
    }
}

public sealed record DaemonHaReading(
    IReadOnlyDictionary<string, ShadowUsage>? Usage, string? UsageProblem,
    IReadOnlyList<string>? Resumable, string? ResumableProblem);

// This machine's usage sensors and resume select, as agent-ha-bridge's daemon published them.
public sealed class DaemonHaReader : IDisposable
{
    // The resume select's first option, which starts a new session rather than resuming one
    // ($script:CopilotMqttNewSessionOption).
    const string NewSessionOption = "New session";
    const int MaxStateBytes = 1024 * 1024;

    sealed class Unavailable(string reason) : Exception(reason);

    // Each agent's figure at the last read and since when it has stood unchanged; in memory only.
    // With a dashboard.publication the daemon republishes an unchanged figure at every poll, so
    // measured_at keeps moving while the figure does not, and on its own it never reached back to
    // a host reading taken between two polls (#73).
    readonly Dictionary<string, (string Figure, DateTimeOffset Since, DateTimeOffset ReadAt)> standing = new(StringComparer.Ordinal);
    // Reads further apart than this may have missed a change in between, so the figure starts again.
    static readonly TimeSpan Unbroken = TimeSpan.FromMinutes(5);

    readonly HttpClient http;
    readonly TimeProvider clock;
    readonly TimeSpan patience;
    readonly string resumeEntity;
    readonly IReadOnlyList<(string Client, string Entity)> usageEntities;

    public DaemonHaReader(string machineName, HttpMessageHandler? transport = null, TimeSpan? timeout = null, TimeProvider? time = null)
    {
        clock = time ?? TimeProvider.System;
        Slug = HaContract.MachineSlug(machineName);
        // The numbered sensors of further Copilot logins too (usage.copilotAccounts), each compared
        // with the host's reading in the same place.
        usageEntities = [.. HaContract.UsageSensorKeys.Select(c => (c, HaContract.MachineEntityId("sensor", $"usage_{c}", Slug)))];
        resumeEntity = HaContract.MachineEntityId("select", "new_resume", Slug);
        Entities = [.. usageEntities.Select(u => u.Entity), resumeEntity];
        patience = timeout ?? TimeSpan.FromSeconds(10);
        // No redirects: the token is for the address in the config and nowhere else.
        http = new HttpClient(new ReadOnlyHomeAssistantHandler(Entities, transport ?? new SocketsHttpHandler { AllowAutoRedirect = false }))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public string Slug { get; }

    // Exactly what may be read, and nothing else.
    public IReadOnlyList<string> Entities { get; }

    public async Task<DaemonHaReading> ReadAsync(HomeAssistantAccess access, CancellationToken ct = default)
    {
        try
        {
            var readAt = clock.GetUtcNow();
            var usage = new Dictionary<string, ShadowUsage>(StringComparer.Ordinal);
            foreach (var (client, entity) in usageEntities)
            {
                if (await GetAsync(access, entity, ct) is { } state && Usage(state) is var u)
                {
                    usage[client] = Stood(client, u with { ConfirmedAt = access.UsagePace.ConfirmedAt(u.MeasuredAt, readAt), Lapsed = access.UsagePace.Lapsed(u.MeasuredAt, readAt) }, readAt);
                }
                else { standing.Remove(client); }
            }
            var resume = await GetAsync(access, resumeEntity, ct);
            return resume is not { } r
                ? new(usage, null, null, "the daemon has not published a resume list")
                : new(usage, null, Labels(r), null);
        }
        catch (Unavailable u) { return new(null, u.Message, null, u.Message); }
        catch (Exception e) when (e is HttpRequestException or IOException || e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new(null, "Home Assistant is unreachable", null, "Home Assistant is unreachable");
        }
    }

    async Task<JsonElement?> GetAsync(HomeAssistantAccess access, string entity, CancellationToken outer)
    {
        // One deadline for the request and its body: HttpClient.Timeout stops at the headers
        // when the body is streamed, and a Home Assistant that stalled mid-body would then hold
        // the whole sampling loop.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(outer);
        deadline.CancelAfter(patience);
        var ct = deadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(access.BaseUrl, "api/states/" + Uri.EscapeDataString(entity)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.Token);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) { return null; }
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { throw new Unavailable("Home Assistant refused agent-ha-bridge's token"); }
        if (!response.IsSuccessStatusCode) { throw new Unavailable($"Home Assistant answered {(int)response.StatusCode}"); }
        if (response.Content.Headers.ContentLength > MaxStateBytes) { throw new Unavailable("Home Assistant sent a state too large to be one"); }
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxStateBytes) { throw new Unavailable("Home Assistant sent a state too large to be one"); }
            buffer.Write(chunk, 0, read);
        }
        try
        {
            using var doc = JsonDocument.Parse(buffer.ToArray());
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : throw new Unavailable("Home Assistant sent a state that is not one");
        }
        catch (JsonException) { throw new Unavailable("Home Assistant sent a state that is not JSON"); }
    }

    // The same figure read again, republished or not, has held since it was first measured: the
    // daemon publishes every change, and percent used only rises within a window.
    ShadowUsage Stood(string client, ShadowUsage u, DateTimeOffset readAt)
    {
        if (!u.Reading || u.MeasuredAt is not { } m) { standing.Remove(client); return u; }
        var figure = string.Join('\u001f', new[] { u.Account ?? "", u.AccountKey ?? "" }
            .Concat(u.Windows.OrderBy(w => w.Key, StringComparer.Ordinal).Select(w => w.Key + "=" + w.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        var since = standing.TryGetValue(client, out var was) && was.Figure == figure && readAt - was.ReadAt <= Unbroken && was.Since <= m ? was.Since : m;
        standing[client] = (figure, since, readAt);
        return since < m ? u with { HeldSince = since } : u;
    }

    // New-BridgeUsageRecord as Publish-CopilotMqttUsage puts it in the sensor's attributes.
    static ShadowUsage Usage(JsonElement state)
    {
        var attributes = state.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object ? a : default;
        var windows = new Dictionary<string, double>(StringComparer.Ordinal);
        if (attributes.ValueKind == JsonValueKind.Object && attributes.TryGetProperty("windows", out var ws) && ws.ValueKind == JsonValueKind.Array)
        {
            foreach (var w in ws.EnumerateArray())
            {
                if (w.ValueKind == JsonValueKind.Object && Str(w, "key") is { Length: > 0 } key && w.TryGetProperty("percent", out var p) && p.TryGetDouble(out var percent))
                {
                    windows[key] = percent;
                }
            }
        }
        return new ShadowUsage
        {
            Reading = windows.Count > 0,
            Account = attributes.ValueKind == JsonValueKind.Object ? Str(attributes, "account") : null,
            AccountKey = attributes.ValueKind == JsonValueKind.Object ? Str(attributes, "account_id") : null,
            Windows = windows,
            MeasuredAt = attributes.ValueKind == JsonValueKind.Object && Str(attributes, "measured_at") is { } m
                && DateTimeOffset.TryParse(m, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at) ? at : null,
        };
    }

    static List<string> Labels(JsonElement state)
    {
        if (!state.TryGetProperty("attributes", out var a) || a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("options", out var o) || o.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return [.. o.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String && x.GetString() != NewSessionOption && !FromAnotherMachine(x.GetString()!))
            .Select(x => ShadowHash.Normalize(x.GetString()!))];
    }

    // Sessions other machines share, which the daemon adds after its own
    // (Get-DaemonMergedResumable), labelled "<agent> on <machine>: …" by
    // Get-DaemonRemoteResumeLabel. This host does not offer them yet (parity §11, transfer in
    // flight), so they are left out rather than reported missing.
    static bool FromAnotherMachine(string label) => RemoteLabel.IsMatch(label);

    static readonly System.Text.RegularExpressions.Regex RemoteLabel = new(
        @"\A(?:" + string.Join('|', AgentBridge.Host.Delivery.Launch.Launchers.All.Select(l => System.Text.RegularExpressions.Regex.Escape(l.Label)).Append("Session")) + @") on [^:]{1,100}: ",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    public void Dispose() => http.Dispose();
}

// What the comparison asks of the daemon's Home Assistant side each sample.
public interface IDaemonHomeAssistant
{
    Task<DaemonHaReading> ReadAsync(CancellationToken ct = default);
}

// Reads at most once a minute and gives the last reading in between: the daemon publishes usage
// every two minutes and its resume list every three, so a read each 15 s sample would only be
// load on Home Assistant. The config, and so the token, is read afresh for each read and held
// no longer than it.
public sealed class CachedDaemonHomeAssistant(AgentHaBridgePaths paths, DaemonHaReader reader, TimeProvider time, TimeSpan? refresh = null) : IDaemonHomeAssistant
{
    readonly TimeSpan every = refresh ?? TimeSpan.FromMinutes(1);
    DateTimeOffset? read;
    DaemonHaReading last = new(null, "not read yet", null, "not read yet");

    public async Task<DaemonHaReading> ReadAsync(CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        if (read is { } at && now - at < every) { return last; }
        read = now;
        var (access, problem) = AgentHaBridgeConfig.Read(paths);
        last = access is null ? new(null, problem, null, problem) : await reader.ReadAsync(access, ct);
        return last;
    }
}

// --no-home-assistant: usage and the resume list are then not compared, and say so.
public sealed class NoDaemonHomeAssistant : IDaemonHomeAssistant
{
    const string Why = "Home Assistant is not read (--no-home-assistant)";
    public Task<DaemonHaReading> ReadAsync(CancellationToken ct = default) => Task.FromResult(new DaemonHaReading(null, Why, null, Why));
}
