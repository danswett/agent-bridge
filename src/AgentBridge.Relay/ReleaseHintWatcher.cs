using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Relay;

public sealed class ReleaseHintWatcher(IHttpClientFactory clients, TimeProvider time, ILogger<ReleaseHintWatcher> log) : BackgroundService
{
    const string ListUrl = "https://releases.slipgates.com/repos/danswett/agent-bridge/releases?per_page=10&page=1";
    readonly object sync = new();
    readonly ConcurrentDictionary<Guid, Subscription> hosts = new();
    readonly Dictionary<string, Hint> latest = new();
    readonly ReleaseHints validator = new();
    long version;
    string? etag;

    sealed record Hint(string Channel, string Tag, byte[] Frame, long Version);

    public async Task<IDisposable> SubscribeAsync(Func<byte[], Task> send)
    {
        var id = Guid.NewGuid();
        var subscription = new Subscription(send, () => hosts.TryRemove(id, out _));
        Hint[] snapshot;
        lock (sync)
        {
            hosts[id] = subscription;
            snapshot = latest.Values.ToArray();
        }
        var accepted = false;
        try
        {
            await Task.WhenAll(snapshot.Select(subscription.SendAsync));
            accepted = true;
            return subscription;
        }
        finally { if (!accepted) { subscription.Dispose(); } }
    }

    sealed class Subscription(Func<byte[], Task> send, Action remove) : IDisposable
    {
        readonly SemaphoreSlim turn = new(1, 1);
        readonly Dictionary<string, long> sent = new();
        public async Task SendAsync(Hint hint)
        {
            await turn.WaitAsync();
            try
            {
                if (sent.GetValueOrDefault(hint.Channel) >= hint.Version) { return; }
                await send(hint.Frame);
                sent[hint.Channel] = hint.Version;
            }
            finally { turn.Release(); }
        }
        public void Dispose() => remove();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        do { await PollAsync(stoppingToken); }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    async Task PollAsync(CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ListUrl);
            if (etag is not null) { request.Headers.IfNoneMatch.ParseAdd(etag); }
            using var http = clients.CreateClient("release-hints");
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.NotModified) { return; }
            if (response.StatusCode != HttpStatusCode.OK)
            {
                log.LogDebug("Release hint list unavailable: HTTP {Status}", (int)response.StatusCode);
                return;
            }
            using var doc = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(ct), new JsonDocumentOptions { MaxDepth = 16 });
            if (doc.RootElement.ValueKind != JsonValueKind.Array) { log.LogWarning("Release hint list is not an array"); return; }
            var selected = new Dictionary<string, (long Run, string Tag, byte[] Frame)>();
            foreach (var release in doc.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object
                    || !release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String
                    || tag.GetString() is not { Length: > 1 } text || text[0] != 'v' || !ReleaseVersions.TryRun(text[1..], out var run)
                    || release.TryGetProperty("draft", out var draft) && draft.ValueKind != JsonValueKind.False
                    || !release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) { continue; }
                foreach (var asset in assets.EnumerateArray())
                {
                    if (asset.ValueKind != JsonValueKind.Object || !asset.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String) { continue; }
                    var channel = name.GetString() switch { "releases.win.json" => "win", "releases.win-arm64.json" => "win-arm64", _ => null };
                    if (channel is null || selected.TryGetValue(channel, out var previous) && previous.Run >= run) { continue; }
                    var now = time.GetUtcNow();
                    var frame = JsonSerializer.SerializeToUtf8Bytes(new { t = "release.hint", tag = text, channel, seenAt = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ") });
                    if (validator.Read(frame, now, field => log.LogWarning("Release hint dropped: {Field}", field)) is not null) { selected[channel] = (run, text, frame); }
                }
            }
            var changed = new List<Hint>();
            Subscription[] targets;
            lock (sync)
            {
                foreach (var (channel, hint) in selected)
                {
                    // Withdrawals and prereleases are hints too; clients decide release eligibility.
                    if (latest.TryGetValue(channel, out var before) && before.Tag == hint.Tag) { continue; }
                    var update = new Hint(channel, hint.Tag, hint.Frame, ++version);
                    latest[channel] = update;
                    changed.Add(update);
                }
                etag = response.Headers.ETag?.ToString();
                targets = hosts.Values.ToArray();
            }
            await Task.WhenAll(targets.SelectMany(host => changed.Select(host.SendAsync)));
        }
        catch (Exception e) when (!ct.IsCancellationRequested
            && e is HttpRequestException or IOException or JsonException or InvalidOperationException or OperationCanceledException)
        {
            log.LogWarning("Release hint read or delivery failed: {Kind}", e.GetType().Name);
        }
    }
}
