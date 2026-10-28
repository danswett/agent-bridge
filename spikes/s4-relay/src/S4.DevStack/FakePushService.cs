using System.Collections.Concurrent;

namespace S4.DevStack;

// A stand-in push service (what web.push.apple.com is to a real phone): records what the
// relay sends. Paths under /gone/ answer 410, as a service does for an expired subscription.
public sealed class FakePushService
{
    public sealed record Push(string Path, Dictionary<string, string> Headers, byte[] Body);

    public ConcurrentQueue<Push> Received { get; } = new();
    public string Url { get; private set; } = "";

    public async Task<WebApplication> StartAsync()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        var app = b.Build();
        app.MapPost("/{**path}", async (HttpRequest req) =>
        {
            using var ms = new MemoryStream();
            await req.Body.CopyToAsync(ms);
            var headers = req.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            Received.Enqueue(new Push(req.Path, headers, ms.ToArray()));
            return req.Path.StartsWithSegments("/gone") ? Results.StatusCode(410) : Results.StatusCode(201);
        });
        await app.StartAsync();
        Url = app.Urls.First();
        return app;
    }
}
