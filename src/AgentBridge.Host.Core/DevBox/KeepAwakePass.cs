using System.Globalization;
using System.Text.Json;

namespace AgentBridge.Host.Core.DevBox;

// What one keep-awake pass came to, in agent-ha-bridge's words (its log and exit code):
//   skipped, delayed   a pending stop was cleared or pushed back
//   safe               the only pending stop is more than 24 h away, where it cannot be moved and
//                      need not be
//   no-action          nothing is scheduled: the ordinary state while anyone is connected
//   blocked            a stop is pending that the service would not let us move: the only outcome
//                      a person needs to act on, so it wins the summary
//   dry-run            what a pass would have cleared, having changed nothing
//   failed             the pass could not finish (no az login, Dev Center unreachable)
//   not-a-devbox       this machine is not a Dev Box
// and this host's own:
//   stood-back         agent-ha-bridge's keep-awake task is enabled here, so it is that task's
//   busy               another pass was already running
public sealed record KeepAwakeResult(string Outcome, string Detail)
{
    public const string Skipped = "skipped", Delayed = "delayed", Safe = "safe", NoAction = "no-action", Blocked = "blocked",
        DryRun = "dry-run", Failed = "failed", NotADevBox = "not-a-devbox", StoodBack = "stood-back", Busy = "busy";

    // What a person needs to look at: the Dev Box may hibernate.
    public bool Alarming => Outcome is Failed or Blocked;
}

// One keep-awake pass, as agent-ha-bridge's Invoke-BridgeDevBoxKeepAwake: find any pending stop
// on this Dev Box close enough to act on, and push it away through Dev Center's user-scoped
// developer API. Only this Dev Box's own Stop actions are ever read or moved: nothing else on the
// Dev Box, no other Dev Box, and nothing of the pool's.
//
// Two service-enforced limits shape it, both established by agent-ha-bridge against the live API
// on 2026-09-30:
//   * DelayUntilTimeExceedLimit: a delay may not exceed 8 hours past where the action first
//     landed, so delays do not stack, and the ladder asks for less until one is accepted;
//   * DevBoxActionPreferenceUpdateNotInAllowedTimeWindow: neither lever works while the action is
//     more than 24 hours away, which is the state worth reaching, so that is success.
// Skip is tried first because it clears the occurrence instead of moving it, leaving nothing to
// delay again on the next pass.
public static class KeepAwakePass
{
    public const string ApiVersion = "2024-02-01";
    // Largest first: the 8-hour ceiling is measured from where the action originally landed,
    // which the API never reports, so the only way to find the headroom is to ask for less.
    public static readonly int[] DelayLadder = [8, 6, 4, 2, 1];
    public const double ActionableHours = 24;

    public static async Task<KeepAwakeResult> RunAsync(DevBoxIdentity? identity, IDevCenterToken tokens, IDevCenterHttp http, DateTimeOffset now, bool dryRun,
        Action<string> log, CancellationToken cancellationToken = default)
    {
        if (identity is null) { return new(KeepAwakeResult.NotADevBox, "this machine is not a Dev Box"); }
        string token;
        try { token = await tokens.GetAsync(cancellationToken); }
        catch (DevBoxException e) { return new(KeepAwakeResult.Failed, e.Message); }

        DevCenterResponse listed;
        try { listed = await http.SendAsync(HttpMethod.Get, identity.Url("actions", $"api-version={ApiVersion}"), token, cancellationToken); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return new(KeepAwakeResult.Failed, $"Dev Center could not be reached to list {identity.DevBox}'s actions ({e.Message})");
        }
        if (!listed.Ok) { return new(KeepAwakeResult.Failed, $"Dev Center refused to list {identity.DevBox}'s actions ({ErrorCode(listed)})"); }

        List<(string Name, DateTimeOffset? Scheduled)> stops;
        try { stops = Stops(listed.Body); }
        catch (JsonException e) { return new(KeepAwakeResult.Failed, $"Dev Center's list of {identity.DevBox}'s actions could not be read ({e.Message})"); }

        var outcomes = new List<KeepAwakeResult>();
        foreach (var (name, scheduled) in stops)
        {
            // The ordinary state while someone is connected: the pool's Stop action is defined, but
            // no occurrence of it is scheduled. Reading next unconditionally took agent-ha-bridge's
            // whole pass down on 15 of 24 passes on 2026-10-02/03, so a Dev Box simply still
            // connected looked exactly like an expired login.
            if (scheduled is not { } at) { continue; }
            var hoursAway = Math.Round((at - now).TotalHours, 2);
            if (hoursAway > ActionableHours)
            {
                outcomes.Add(new(KeepAwakeResult.Safe, $"'{name}' is {Hours(hoursAway)}h away (> 24h); already safe"));
                continue;
            }
            log($"keep-awake: '{name}' fires in {Hours(hoursAway)}h at {at.ToLocalTime():yyyy-MM-dd HH:mm} local");
            if (dryRun)
            {
                outcomes.Add(new(KeepAwakeResult.DryRun, $"would clear '{name}'"));
                continue;
            }
            outcomes.Add(await ReprieveAsync(identity, token, http, name, at, log, cancellationToken));
        }

        if (outcomes.Count == 0) { return new(KeepAwakeResult.NoAction, $"no pending stop on {identity.DevBox}"); }
        var status = outcomes.Any(o => o.Outcome == KeepAwakeResult.Blocked) ? KeepAwakeResult.Blocked : outcomes[0].Outcome;
        return new(status, string.Join("; ", outcomes.Select(o => o.Detail)));
    }

    // Clears one pending stop: skip, then delay as far as the service still allows.
    static async Task<KeepAwakeResult> ReprieveAsync(DevBoxIdentity identity, string token, IDevCenterHttp http, string name, DateTimeOffset scheduled,
        Action<string> log, CancellationToken cancellationToken)
    {
        var action = $"actions/{Uri.EscapeDataString(name)}";
        var skipped = await PostAsync(http, identity.Url($"{action}:skip", $"api-version={ApiVersion}"), token, cancellationToken);
        if (skipped.Ok) { return new(KeepAwakeResult.Skipped, $"skipped '{name}'"); }
        log($"keep-awake: skip refused for '{name}' ({skipped.Code}); trying delay");

        foreach (var hours in DelayLadder)
        {
            var until = scheduled.ToUniversalTime().AddHours(hours).ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            var delayed = await PostAsync(http, identity.Url($"{action}:delay", $"until={until}&api-version={ApiVersion}"), token, cancellationToken);
            if (delayed.Ok)
            {
                var moved = delayed.Body is { Length: > 0 } body ? Next(body) ?? until : until;
                return new(KeepAwakeResult.Delayed, $"delayed '{name}' by {hours}h to {moved}");
            }
            log($"keep-awake: delay +{hours}h refused ({delayed.Code})");
        }
        return new(KeepAwakeResult.Blocked, $"could not move '{name}'; it is already as far out as the service allows");
    }

    // A refusal and a request that never got an answer are both "that lever did not work": as in
    // agent-ha-bridge, the next one is tried.
    static async Task<(bool Ok, string Code, string? Body)> PostAsync(IDevCenterHttp http, Uri uri, string token, CancellationToken cancellationToken)
    {
        try
        {
            var response = await http.SendAsync(HttpMethod.Post, uri, token, cancellationToken);
            return (response.Ok, response.Ok ? "" : ErrorCode(response), response.Body);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return (false, e.Message, null);
        }
    }

    // This Dev Box's Stop actions, with when each is next due (UTC), or none when nothing is
    // scheduled. actionType in any case, as PowerShell's -eq compares it.
    internal static List<(string Name, DateTimeOffset? Scheduled)> Stops(string body)
    {
        var stops = new List<(string, DateTimeOffset?)>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("value", out var value) || value.ValueKind != JsonValueKind.Array) { return stops; }
        foreach (var action in value.EnumerateArray())
        {
            if (action.ValueKind != JsonValueKind.Object) { continue; }
            if (!(action.TryGetProperty("actionType", out var type) && type.ValueKind == JsonValueKind.String && string.Equals(type.GetString(), "Stop", StringComparison.OrdinalIgnoreCase))) { continue; }
            if (!action.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(n.GetString())) { continue; }
            stops.Add((n.GetString()!, Scheduled(action)));
        }
        return stops;
    }

    // TryParse, not Parse: a time the service renders in another shape drops that action, not the
    // pass.
    static DateTimeOffset? Scheduled(JsonElement action) =>
        action.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.Object
        && next.TryGetProperty("scheduledTime", out var time) && time.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(time.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.ToUniversalTime() : null;

    static string? Next(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.Object
                && next.TryGetProperty("scheduledTime", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    // The service's own code ("DelayUntilTimeExceedLimit" is a diagnosis; "400" is not): the
    // codes in error.details, else error.code, else the status.
    internal static string ErrorCode(DevCenterResponse response)
    {
        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
            {
                if (error.TryGetProperty("details", out var details) && details.ValueKind == JsonValueKind.Array)
                {
                    var codes = details.EnumerateArray()
                        .Where(d => d.ValueKind == JsonValueKind.Object && d.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String && c.GetString() is { Length: > 0 })
                        .Select(d => d.GetProperty("code").GetString()!).ToList();
                    if (codes.Count > 0) { return string.Join(", ", codes); }
                }
                if (error.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.String && code.GetString() is { Length: > 0 } c2) { return c2; }
            }
        }
        catch (JsonException) { }
        return $"HTTP {response.Status}";
    }

    static string Hours(double hours) => hours.ToString("0.##", CultureInfo.InvariantCulture);
}
