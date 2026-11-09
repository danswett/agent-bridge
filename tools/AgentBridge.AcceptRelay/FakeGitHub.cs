using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AgentBridge.AcceptRelay;

// Just enough of GitHub's device flow, /user and token revocation for the relay to sign a
// host in offline: the same endpoints, field names and error codes as the test FakeGitHub
// (tests/AgentBridge.Transport.Tests/TestStack.cs), except that every code is approved at
// once, as the one account below, so no browser or person is needed. Fixed fake values only.
public sealed class FakeGitHub
{
    public const string ClientId = "accept-fake-client";
    public const string ClientSecret = "accept-fake-secret";
    const long AccountId = 424242;
    const string AccountLogin = "accept-user";

    readonly ConcurrentDictionary<string, bool> codes = new();
    readonly ConcurrentDictionary<string, bool> tokens = new();
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
            codes[device] = true;
            // Interval 0: the client polls at once, and the code is already approved.
            return Results.Ok(new { device_code = device, user_code = user, verification_uri = $"{Url}/login/device", interval = 0, expires_in = 900 });
        });
        app.MapPost("/login/oauth/access_token", async (HttpRequest req) =>
        {
            var form = await req.ReadFormAsync();
            if (!codes.TryRemove(form["device_code"].ToString(), out _)) { return Results.Ok(new { error = "incorrect_device_code" }); }
            var token = "gho_" + RandomNumberGenerator.GetHexString(36);
            tokens[token] = true;
            return Results.Ok(new { access_token = token, token_type = "bearer", scope = "" });
        });
        app.MapGet("/user", (HttpRequest req) =>
            tokens.ContainsKey(req.Headers.Authorization.ToString().Replace("Bearer ", ""))
                ? Results.Ok(new { id = AccountId, login = AccountLogin })
                : Results.Unauthorized());
        app.MapDelete("/applications/{client}/token", async (HttpRequest req) =>
        {
            var body = await JsonDocument.ParseAsync(req.Body);
            tokens.TryRemove(body.RootElement.GetProperty("access_token").GetString()!, out _);
            return Results.NoContent();
        });
        await app.StartAsync();
        Url = app.Urls.First();
        return app;
    }
}
