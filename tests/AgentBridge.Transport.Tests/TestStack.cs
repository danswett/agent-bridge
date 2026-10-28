using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Relay;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentBridge.Transport.Tests;

public sealed record GitHubUser(long Id, string Login);

// Just enough of GitHub's device flow and /user for the relay to run offline: the same
// endpoints, field names and error codes.
public sealed class FakeGitHub
{
    sealed class Code { public required string UserCode; public GitHubUser? User; public bool Denied; }

    readonly ConcurrentDictionary<string, Code> codes = new();
    readonly ConcurrentDictionary<string, GitHubUser> tokens = new();
    public ConcurrentBag<string> Revoked { get; } = [];
    public ConcurrentBag<string> Issued { get; } = [];
    public string Url { get; private set; } = "";

    public async Task<WebApplication> StartAsync()
    {
        var b = WebApplication.CreateSlimBuilder();
        b.WebHost.UseUrls("http://127.0.0.1:0");
        b.Logging.ClearProviders();
        var app = b.Build();
        app.MapPost("/login/device/code", () =>
        {
            var device = Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
            var user = $"{RandomNumberGenerator.GetHexString(4)}-{RandomNumberGenerator.GetHexString(4)}".ToUpperInvariant();
            codes[device] = new Code { UserCode = user };
            // Interval 0: the client polls at once, and the tests approve before it does.
            return Results.Ok(new { device_code = device, user_code = user, verification_uri = $"{Url}/login/device", interval = 0, expires_in = 900 });
        });
        app.MapPost("/login/oauth/access_token", async (HttpRequest req) =>
        {
            var form = await req.ReadFormAsync();
            if (!codes.TryGetValue(form["device_code"].ToString(), out var c)) { return Results.Ok(new { error = "incorrect_device_code" }); }
            if (c.Denied) { return Results.Ok(new { error = "access_denied" }); }
            if (c.User is null) { return Results.Ok(new { error = "authorization_pending" }); }
            var token = "gho_" + RandomNumberGenerator.GetHexString(36);
            tokens[token] = c.User;
            Issued.Add(token);
            codes.TryRemove(form["device_code"].ToString(), out _);
            return Results.Ok(new { access_token = token, token_type = "bearer", scope = "" });
        });
        app.MapGet("/user", (HttpRequest req) =>
            tokens.TryGetValue(req.Headers.Authorization.ToString().Replace("Bearer ", ""), out var u)
                ? Results.Ok(new { id = u.Id, login = u.Login })
                : Results.Unauthorized());
        app.MapDelete("/applications/{client}/token", async (HttpRequest req) =>
        {
            var body = await JsonDocument.ParseAsync(req.Body);
            var t = body.RootElement.GetProperty("access_token").GetString()!;
            tokens.TryRemove(t, out _);
            Revoked.Add(t);
            return Results.NoContent();
        });
        await app.StartAsync();
        Url = app.Urls.First();
        return app;
    }

    public void Approve(string userCode, GitHubUser user) => codes.Values.First(c => c.UserCode == userCode).User = user;

    public void Deny(string userCode) => codes.Values.First(c => c.UserCode == userCode).Denied = true;
}

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

sealed class CaptureLogger(ConcurrentQueue<string> lines) : ILoggerProvider, ILogger
{
    public ILogger CreateLogger(string categoryName) => this;
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        lines.Enqueue(formatter(state, exception));
    public void Dispose() { }
}

public sealed class Device(P256KeyPair key, RelayClient relay, ChannelHub hub) : IAsyncDisposable
{
    public P256KeyPair Key => key;
    public RelayClient Relay => relay;
    public ChannelHub Hub => hub;
    public string Id => relay.DeviceId;

    public ValueTask DisposeAsync() => relay.DisposeAsync();
}

// The relay with a fake GitHub and a fake push service, all in this process on loopback
// ports, plus what a test needs to watch it: every forwarded payload and every log line.
public sealed class TestRelay : IAsyncDisposable
{
    readonly List<IAsyncDisposable> owned = [];
    readonly ConcurrentBag<Device> devices = [];

    public required WebApplication App { get; init; }
    public required Uri Url { get; init; }
    public required FakeGitHub GitHub { get; init; }
    public required FakePushService PushService { get; init; }
    public required ECDsa VapidKey { get; init; }
    public required ConcurrentQueue<byte[]> Taps { get; init; }
    public required ConcurrentQueue<string> Logs { get; init; }
    public HttpClient Http { get; } = new();
    // Plays a hostile relay: rewrites (from, to) of each forwarded frame; null leaves it.
    public Func<string, string, (string, string)?>? Route { get; set; }

    public static async Task<TestRelay> StartAsync(Action<RelayOptions>? configure = null)
    {
        var github = new FakeGitHub();
        var ghApp = await github.StartAsync();
        var push = new FakePushService();
        var pushApp = await push.StartAsync();
        var vapid = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var taps = new ConcurrentQueue<byte[]>();
        var logs = new ConcurrentQueue<string>();
        TestRelay? self = null;
        var app = RelayApp.Build([], o =>
        {
            o.GitHubBaseUrl = o.GitHubApiUrl = github.Url;
            o.GitHubClientId = "fake-client";
            o.GitHubClientSecret = "fake-secret";
            o.AllowedOrigins = ["https://pwa.example"];
            o.VapidPrivateKey = Convert.ToBase64String(vapid.ExportPkcs8PrivateKey());
            o.PushHosts = ["127.0.0.1", "web.push.apple.com"];
            o.AllowInsecurePush = true;
            o.MaxPushesPerMinute = 20;
            o.Tap = (_, _, payload) => taps.Enqueue(payload);
            o.Route = (f, t) => self?.Route?.Invoke(f, t) ?? (f, t);
            configure?.Invoke(o);
        }, b =>
        {
            b.WebHost.UseUrls("http://127.0.0.1:0");
            b.Logging.ClearProviders();
            b.Logging.AddProvider(new CaptureLogger(logs));
        });
        await app.StartAsync();
        self = new TestRelay { App = app, Url = new Uri(app.Urls.First()), GitHub = github, PushService = push, VapidKey = vapid, Taps = taps, Logs = logs };
        self.owned.Add(ghApp);
        self.owned.Add(pushApp);
        return self;
    }

    // An account token as the relay issues after sign-in, without the device flow.
    public string TokenFor(long id, string login) =>
        App.Services.GetRequiredService<Tokens>().Issue(new Account(id.ToString(), login)).Token;

    public Task<SignInResult> SignInAsync(GitHubUser user) =>
        SignIn.DeviceFlowAsync(Http, Url, (code, _) => GitHub.Approve(code, user));

    public async Task<Device> ConnectAsync(string token, string name, string kind = "test", P256KeyPair? key = null)
    {
        key ??= P256KeyPair.Generate();
        var relay = await RelayClient.ConnectAsync(Url, token, key, name, kind);
        var hub = new ChannelHub(relay, key, name, kind) { Handler = (_, op, data) => Demo(op, data) };
        var d = new Device(key, relay, hub);
        devices.Add(d);
        return d;
    }

    public static Task<object?> Demo(string op, JsonElement data) => Task.FromResult<object?>(op switch
    {
        "ping" => new { at = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() },
        "echo" => data,
        _ => null,
    });

    public async ValueTask DisposeAsync()
    {
        foreach (var d in devices) { await d.DisposeAsync(); }
        await App.DisposeAsync();
        foreach (var o in owned) { await o.DisposeAsync(); }
        VapidKey.Dispose();
        Http.Dispose();
    }
}

public static class Wait
{
    // Polls a condition instead of sleeping for a guessed time: as fast as the machine
    // allows, and only as slow as the deadline on a loaded CI runner.
    public static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (sw.Elapsed > limit) { throw new TimeoutException($"timed out waiting for {what}"); }
            await Task.Delay(10);
        }
    }
}

public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute(string why)
    {
        if (!OperatingSystem.IsWindows()) { Skip = why; }
    }
}

public sealed class NotWindowsFactAttribute : FactAttribute
{
    public NotWindowsFactAttribute(string why)
    {
        if (OperatingSystem.IsWindows()) { Skip = why; }
    }
}

public sealed class TpmFactAttribute : FactAttribute
{
    public TpmFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || !TpmP256.IsAvailable(out _)) { Skip = "this machine has no TPM that can hold a P-256 key (GitHub's runners have none)"; }
    }
}
