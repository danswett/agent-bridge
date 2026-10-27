using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace S4.DevStack;

public sealed record GitHubUser(long Id, string Login);

// Just enough of GitHub's device flow and /user for the relay to run offline: the same
// endpoints, field names and error codes.
public sealed class FakeGitHub
{
    sealed class Code { public required string UserCode; public GitHubUser? User; public bool Denied; }

    readonly ConcurrentDictionary<string, Code> codes = new();
    readonly ConcurrentDictionary<string, GitHubUser> tokens = new();
    public ConcurrentBag<string> Revoked { get; } = [];

    // When set, a code still pending at its first poll is approved as this user.
    public GitHubUser? AutoApprove { get; set; }
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
            return Results.Ok(new { device_code = device, user_code = user, verification_uri = $"{Url}/login/device", interval = 1, expires_in = 900 });
        });
        app.MapPost("/login/oauth/access_token", async (HttpRequest req) =>
        {
            var form = await req.ReadFormAsync();
            if (!codes.TryGetValue(form["device_code"].ToString(), out var c)) { return Results.Ok(new { error = "incorrect_device_code" }); }
            if (c.Denied) { return Results.Ok(new { error = "access_denied" }); }
            if (c.User is null)
            {
                if (AutoApprove != null) { c.User = AutoApprove; }
                return Results.Ok(new { error = "authorization_pending" });
            }
            var token = "gho_" + RandomNumberGenerator.GetHexString(36);
            tokens[token] = c.User;
            codes.TryRemove(form["device_code"].ToString(), out _);
            return Results.Ok(new { access_token = token, token_type = "bearer", scope = "" });
        });
        app.MapGet("/user", (HttpRequest req) =>
            tokens.TryGetValue(req.Headers.Authorization.ToString().Replace("Bearer ", ""), out var u)
                ? Results.Ok(new { id = u.Id, login = u.Login })
                : Results.Unauthorized());
        app.MapDelete("/applications/{client}/token", async (HttpRequest req) =>
        {
            var body = await System.Text.Json.JsonDocument.ParseAsync(req.Body);
            var t = body.RootElement.GetProperty("access_token").GetString()!;
            tokens.TryRemove(t, out _);
            Revoked.Add(t);
            return Results.NoContent();
        });
        app.MapGet("/login/device", () => Results.Text("Fake GitHub: codes are approved automatically."));
        await app.StartAsync();
        Url = app.Urls.First();
        return app;
    }

    public void Approve(string userCode, GitHubUser user) => codes.Values.First(c => c.UserCode == userCode).User = user;

    public void Deny(string userCode) => codes.Values.First(c => c.UserCode == userCode).Denied = true;
}
