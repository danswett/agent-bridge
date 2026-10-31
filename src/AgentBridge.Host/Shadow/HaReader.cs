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
    // dashboard.publication.writer: a machine that may be reading an account this one stands down for.
    public string? PublicationWriter { get; init; }
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
            return (new HomeAssistantAccess(baseUrl, token.Trim()) { UsagePace = Pace(doc.RootElement), PublicationWriter = Writer(doc.RootElement) }, null);
        }
        // The parser's message can quote the file, and the file holds the token.
        catch (JsonException) { return (null, "agent-ha-bridge's config is not valid JSON"); }
    }

    static string? Writer(JsonElement root) =>
        root.TryGetProperty("dashboard", out var d) && d.ValueKind == JsonValueKind.Object
            && d.TryGetProperty("publication", out var p) && p.ValueKind == JsonValueKind.Object
            && p.TryGetProperty("writer", out var w) && w.ValueKind == JsonValueKind.String && w.GetString() is { Length: > 0 } s ? s : null;

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
// also: entities named one at a time besides the fixed ones (the usage sensors of the machines
// that may be reading an account, DaemonHaReader.ReaderEntities); never a pattern.
public sealed class ReadOnlyHomeAssistantHandler(IEnumerable<string> entities, HttpMessageHandler inner, Func<string, bool>? also = null) : DelegatingHandler(inner)
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
        if (!path.StartsWith(States, StringComparison.Ordinal) || Uri.UnescapeDataString(path[States.Length..]) is var entity && !allowed.Contains(entity) && also?.Invoke(entity) != true)
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
        http = new HttpClient(new ReadOnlyHomeAssistantHandler(Entities, transport ?? new SocketsHttpHandler { AllowAutoRedirect = false }, e => readerEntities.Contains(e)))
        {
            Timeout = Timeout.InfiniteTimeSpan,
        };
    }

    public string Slug { get; }

    // Exactly what may be read, and nothing else, but for ReaderEntities.
    public IReadOnlyList<string> Entities { get; }

    // agent-ha-bridge asks the vendor about an account on one machine at a time; the others stand
    // down while its readings are current (Test-BridgeUsageCovered), and their own sensors keep the
    // last figure they read, hours old (#40). For an agent this machine's daemon has stood down
    // for, the daemon's reading is the account's reader's: the usage sensor of the machine, other
    // than this one, whose current reading is of the same agent and the same account. The machines
    // looked at are those the daemon itself names (its resume list's "<agent> on <machine>: …", and
    // its dashboard.publication.writer) and the reader found last, at most MaxReaders of them, each
    // by its one exact entity, allowed for that read only. Nothing found, or nothing readable, and
    // the daemon's own lapsed figure stands: not compared.
    //
    // The same for a place where this machine's daemon has no reading at all and the host has one:
    // a further Copilot login the host is given (usage.copilotAccounts) and this machine's daemon is
    // not, because another machine reads it for the account. That machine's sensor, of the host's
    // login, is the daemon's figure; with none, the daemon has no reading, a mismatch. Seen on
    // DEV-VM1 as usage.missingOnDaemon on copilot_2 for hours while DSWETT-HOME read it (#40).
    public IReadOnlyCollection<string> ReaderEntities => readerEntities;
    volatile HashSet<string> readerEntities = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> lastReader = new(StringComparer.Ordinal);
    const int MaxReaders = 8;

    // host: the host's readings, by place, whose logins are looked for where this machine's daemon
    // has none.
    public async Task<DaemonHaReading> ReadAsync(HomeAssistantAccess access, IReadOnlyDictionary<string, ShadowUsage>? host = null, CancellationToken ct = default)
    {
        try
        {
            var readAt = clock.GetUtcNow();
            var usage = new Dictionary<string, ShadowUsage>(StringComparer.Ordinal);
            // Places with no reading of this daemon's own: no sensor, or one with no figure.
            var absent = new Dictionary<string, ShadowUsage?>(StringComparer.Ordinal);
            foreach (var (client, entity) in usageEntities)
            {
                if (await GetAsync(access, entity, ct) is { } state && Usage(state) is var u)
                {
                    // A lapsed figure keeps no history: the account reader's, which stands in for it
                    // (ReadersAsync), keeps it under the same agent, and alternating the two would
                    // start the reader's afresh at every read.
                    // An empty one is held back until the readers are looked at, however old its
                    // measured_at: put through Stood first, it cleared the history of the reader's
                    // figure that stands in for it, and taken as lapsed, its reader was looked for by
                    // its own account, not the host's (both found in review, #105).
                    if (!u.Reading) { absent[client] = u; }
                    else if (access.UsagePace.Lapsed(u.MeasuredAt, readAt)) { usage[client] = u with { Lapsed = true }; }
                    else { usage[client] = Stood(client, u with { ConfirmedAt = access.UsagePace.ConfirmedAt(u.MeasuredAt, readAt) }, readAt); }
                }
                else { absent[client] = null; }
            }
            var resume = await GetAsync(access, resumeEntity, ct);
            var wanted = usage.Where(u => u.Value.Lapsed).Select(u => (u.Key, Account: AccountOf(u.Value)))
                .Concat(absent.Keys.Select(c => (Key: c, Account: host?.GetValueOrDefault(c) is { Reading: true } h ? AccountOf(h) : null)))
                .Where(w => w.Account is { Length: > 0 })
                .Select(w => (w.Key, w.Account!))
                .DistinctBy(w => w.Key, StringComparer.Ordinal)
                .ToList();
            if (wanted.Count > 0) { await ReadersAsync(access, wanted, usage, resume, readAt, ct); }
            // A place with nothing of its own and no reader's figure either starts afresh next time,
            // and shows what its own sensor has, if anything.
            foreach (var (client, own) in absent.Where(a => !usage.ContainsKey(a.Key)))
            {
                standing.Remove(client);
                if (own is not null) { usage[client] = own; }
            }
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

    static string? AccountOf(ShadowUsage u) => u.AccountKey is { Length: > 0 } ? u.AccountKey : u.Account;

    // wanted: each place to find the account's reader for, and the account.
    async Task ReadersAsync(HomeAssistantAccess access, IReadOnlyList<(string Key, string Account)> wanted, Dictionary<string, ShadowUsage> usage, JsonElement? resume, DateTimeOffset readAt, CancellationToken ct)
    {
        var names = new List<string>();
        if (resume is { } r && r.TryGetProperty("attributes", out var a) && a.ValueKind == JsonValueKind.Object && a.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in o.EnumerateArray())
            {
                if (x.ValueKind == JsonValueKind.String && RemoteMachine.Match(x.GetString()!) is { Success: true } m) { names.Add(m.Groups[1].Value); }
            }
        }
        // Each sensor read once a pass, however many places look at it.
        var read = new Dictionary<string, JsonElement?>(StringComparer.Ordinal);
        foreach (var (key, account) in wanted)
        {
            // The agent the sensor is for: a further Copilot login's (copilot_2 to _4) is Copilot's,
            // and the reader may carry that login on any of its own Copilot sensors.
            var client = key.StartsWith("copilot_", StringComparison.Ordinal) ? "copilot" : key;
            var sensors = client == "copilot" ? HaContract.UsageSensorKeys.Where(k => k == "copilot" || k.StartsWith("copilot_", StringComparison.Ordinal)).ToList() : [key];
            var slugs = (lastReader.TryGetValue(key, out var last) ? [last] : Array.Empty<string>())
                .Concat(access.PublicationWriter is { } w ? [w] : [])
                .Concat(names)
                .Select(HaContract.MachineSlug)
                .Where(s => s != Slug)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxReaders)
                .ToList();
            ShadowUsage? best = null;
            string? bestSlug = null;
            // The latest reading of the account that has lapsed, should no current one be found.
            ShadowUsage? stale = null;
            string? staleSlug = null;
            foreach (var slug in slugs)
            {
                foreach (var sensor in sensors)
                {
                    var entity = HaContract.MachineEntityId("sensor", $"usage_{sensor}", slug);
                    if (!read.TryGetValue(entity, out var state))
                    {
                        readerEntities = new HashSet<string>([entity], StringComparer.Ordinal);
                        // One sensor that cannot be read is one fewer to choose from, never a reason
                        // to stop comparing the rest.
                        try { state = await GetAsync(access, entity, ct); }
                        catch (Exception e) when (e is Unavailable or HttpRequestException or IOException || e is OperationCanceledException && !ct.IsCancellationRequested) { state = null; }
                        finally { readerEntities = new HashSet<string>(StringComparer.Ordinal); }
                        read[entity] = state;
                    }
                    if (state is not { } s || !s.TryGetProperty("attributes", out var attributes) || attributes.ValueKind != JsonValueKind.Object) { continue; }
                    // Its own reading of this agent and this account, as Test-BridgeUsageCovered matches one.
                    if (Str(attributes, "client") != client || Str(attributes, "machine_slug") != slug) { continue; }
                    var u = Usage(s);
                    if (!u.Reading || !SameAccount(client, u.AccountKey is { Length: > 0 } ? u.AccountKey : u.Account, account) || u.MeasuredAt is not { } at) { continue; }
                    // Neither old nor ahead of now: a clock running ahead would otherwise outrank a
                    // current reader, as Test-BridgeUsageCovered refuses one more than a poll ahead.
                    if (at - readAt > access.UsagePace.Poll) { continue; }
                    if (access.UsagePace.Lapsed(at, readAt))
                    {
                        if (stale?.MeasuredAt is not { } older || older < at) { (stale, staleSlug) = (u, slug); }
                        continue;
                    }
                    if (best?.MeasuredAt is { } was && was >= at) { continue; }
                    (best, bestSlug) = (u, slug);
                }
            }
            if (best is null || bestSlug is null)
            {
                // A place with no figure of this daemon's own whose account reader has stopped
                // reading: what the daemon's machines show for it is that lapsed figure, which says
                // nothing about now, as a lapsed figure of its own does not (UsageLapsed). Taken as
                // no reading at all, it was a mismatch: on DEV-VM1, 2026-10-30 22:40 PT, while
                // DSWETT-HOME went 35 minutes without reading the login (#40).
                if (stale is not null && staleSlug is not null && !usage.ContainsKey(key))
                {
                    // Kept as the reader found last, as a current one is: found through a resume
                    // option that is gone by the next read, it would otherwise not be looked at
                    // again, and the place would read as missing (found in review, #136).
                    lastReader[key] = staleSlug;
                    standing.Remove(key);
                    usage[key] = stale with { Lapsed = true };
                }
                continue;
            }
            lastReader[key] = bestSlug;
            // Not confirmed beyond its measurement: how often that machine re-asserts an unchanged
            // figure is its own configuration, not this one's. What it held across reads here still
            // counts (Stood).
            usage[key] = Stood(key, best, readAt);
        }
    }

    // A GitHub login is one account however it is cased; the others' keys are compared exactly.
    static bool SameAccount(string client, string? a, string b) =>
        string.Equals(a, b, client == "copilot" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    // The machine a resume option from another machine names ("<agent> on <machine>: …").
    static readonly System.Text.RegularExpressions.Regex RemoteMachine = new(
        @"\A(?:" + string.Join('|', AgentBridge.Host.Delivery.Launch.Launchers.All.Select(l => System.Text.RegularExpressions.Regex.Escape(l.Label)).Append("Session")) + @") on ([^:]{1,100}): ",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

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
    // host: the host's usage readings this sample, when it has them.
    Task<DaemonHaReading> ReadAsync(IReadOnlyDictionary<string, ShadowUsage>? host = null, CancellationToken ct = default);
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

    public async Task<DaemonHaReading> ReadAsync(IReadOnlyDictionary<string, ShadowUsage>? host = null, CancellationToken ct = default)
    {
        var now = time.GetUtcNow();
        if (read is { } at && now - at < every) { return last; }
        read = now;
        var (access, problem) = AgentHaBridgeConfig.Read(paths);
        last = access is null ? new(null, problem, null, problem) : await reader.ReadAsync(access, host, ct);
        return last;
    }
}

// --no-home-assistant: usage and the resume list are then not compared, and say so.
public sealed class NoDaemonHomeAssistant : IDaemonHomeAssistant
{
    const string Why = "Home Assistant is not read (--no-home-assistant)";
    public Task<DaemonHaReading> ReadAsync(IReadOnlyDictionary<string, ShadowUsage>? host = null, CancellationToken ct = default) => Task.FromResult(new DaemonHaReading(null, Why, null, Why));
}
