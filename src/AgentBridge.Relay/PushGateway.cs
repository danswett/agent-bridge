using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AgentBridge.Relay;

// Signs and forwards Web Push messages that a device has already encrypted to a browser
// (RFC 8291). The relay learns the push endpoint, the size and the time; not the content.
public sealed class PushGateway(RelayOptions options, IHttpClientFactory http, ILogger<PushGateway> log)
{
    readonly Vapid? vapid = string.IsNullOrEmpty(options.VapidPrivateKey) ? null : Vapid.FromPkcs8(options.VapidPrivateKey, options.VapidSubject);
    readonly ConcurrentDictionary<string, Queue<long>> recent = new();

    public string? PublicKey => vapid?.PublicKey;

    public static readonly HashSet<string> Urgencies = ["very-low", "low", "normal", "high"];

    public sealed record Result(int Status, string? Error = null);

    // Refuses before making any request: the endpoint comes from a device, so an unchecked
    // one would make the relay a proxy into whatever it can reach.
    public string? Refusal(string? endpoint, int bodyLength)
    {
        if (vapid is null) { return "push-disabled"; }
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var u)) { return "bad-endpoint"; }
        if (u.Scheme != Uri.UriSchemeHttps && !(options.AllowInsecurePush && u.Scheme == Uri.UriSchemeHttp)) { return "endpoint-not-https"; }
        if (!u.IsDefaultPort && !options.AllowInsecurePush) { return "endpoint-port"; }
        if (!options.PushHosts.Any(h => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase)))
        {
            return "endpoint-not-allowed";
        }
        if (bodyLength is <= 0 or > WebPushCrypto.RecordSize) { return "bad-body-size"; }
        return null;
    }

    bool WithinRate(string accountId)
    {
        var q = recent.GetOrAdd(accountId, _ => new Queue<long>());
        lock (q)
        {
            var now = Environment.TickCount64;
            while (q.Count > 0 && now - q.Peek() > 60_000) { q.Dequeue(); }
            if (q.Count >= options.MaxPushesPerMinute) { return false; }
            q.Enqueue(now);
            return true;
        }
    }

    public async Task<Result> SendAsync(string accountId, JsonElement message)
    {
        var endpoint = message.TryGetProperty("endpoint", out var e) ? e.GetString() : null;
        byte[] body;
        try { body = Convert.FromBase64String(message.GetProperty("body").GetString() ?? ""); }
        catch (Exception x) when (x is FormatException or KeyNotFoundException or InvalidOperationException) { return new Result(0, "bad-body"); }
        if (Refusal(endpoint, body.Length) is { } why) { return new Result(0, why); }
        if (!WithinRate(accountId)) { return new Result(0, "rate-limited"); }

        var ttl = message.TryGetProperty("ttl", out var t) && t.TryGetInt32(out var tv) ? Math.Clamp(tv, 0, 86_400) : 3600;
        var urgency = message.TryGetProperty("urgency", out var ug) && Urgencies.Contains(ug.GetString() ?? "") ? ug.GetString()! : "normal";
        var uri = new Uri(endpoint!);
        using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Content.Headers.ContentEncoding.Add("aes128gcm");
        req.Headers.TryAddWithoutValidation("TTL", ttl.ToString());
        req.Headers.TryAddWithoutValidation("Urgency", urgency);
        // A topic lets a newer push replace an undelivered older one (a question superseded).
        // It is optional, so one a push service would refuse is dropped, not sent.
        if (message.TryGetProperty("topic", out var tp) && tp.GetString() is { } topic && PushTopic.IsValid(topic))
        {
            req.Headers.TryAddWithoutValidation("Topic", topic);
        }
        req.Headers.TryAddWithoutValidation("Authorization", vapid!.Header(uri, DateTimeOffset.UtcNow));
        try
        {
            using var r = await http.CreateClient("push").SendAsync(req);
            log.LogInformation("push to {Host}: {Status}, {Bytes} bytes", uri.Host, (int)r.StatusCode, body.Length);
            return new Result((int)r.StatusCode);
        }
        catch (Exception x) when (x is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("push to {Host} failed: {Message}", uri.Host, x.Message);
            return new Result(0, "unreachable");
        }
    }
}
