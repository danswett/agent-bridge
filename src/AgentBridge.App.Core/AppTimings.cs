using System.Net.Http.Headers;
using System.Text.Json;

namespace AgentBridge.App.Core;

public sealed class AppTimings(HttpClient? http = null, TimeProvider? time = null, Action<string>? log = null)
{
    static readonly HttpClient DefaultHttp = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
    readonly TimeProvider clock = time ?? TimeProvider.System;
    long? last;
    volatile bool busy;

    // Called on the UI tick: a failure drops this batch, never delays or retries a reply.
    public async Task PostAsync(Uri? relay, string? token, Func<IReadOnlyList<TurnTiming>> drain, CancellationToken ct = default)
    {
        if (relay is null || token is null) { drain(); return; }
        var now = clock.GetTimestamp();
        if (busy || (last is { } from && clock.GetElapsedTime(from, now) < TimeSpan.FromMinutes(1))) { return; }
        var samples = drain().Take(50).Select(s => new { app = TurnClock.App, route = "send-to-settled", ms = Math.Clamp(s.Ms, 0, 600000), at = s.At }).ToList();
        var body = JsonSerializer.SerializeToUtf8Bytes(new { samples });
        while (body.Length > 4096) { samples.RemoveAt(samples.Count - 1); body = JsonSerializer.SerializeToUtf8Bytes(new { samples }); }
        if (samples.Count == 0) { return; }
        last = now;
        busy = true;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(relay, "/v1/stats/app-timings"));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new ByteArrayContent(body);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            using var response = await (http ?? DefaultHttp).SendAsync(request, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != System.Net.HttpStatusCode.Accepted) { log?.Invoke($"app timings: HTTP {(int)response.StatusCode}; batch dropped"); }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            log?.Invoke("app timings: request failed; batch dropped");
        }
        finally { busy = false; }
    }
}
