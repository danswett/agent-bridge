using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace AgentBridge.AppleInterop;

// Only the identity-provider boundary is simulated. Requests still traverse the product
// relay's real device-flow handlers, token issuer, admission checks and Noise routing.
internal sealed class LocalGitHub
{
    public const string AccountId = "9000001";
    public const string Login = "apple-interop-fixture";
    readonly ConcurrentDictionary<string, DateTimeOffset> codes = new();
    readonly ConcurrentDictionary<string, byte> tokens = new();

    public static async Task<WebApplication> StartAsync(CancellationToken ct)
    {
        var provider = new LocalGitHub();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapPost("/login/device/code", () =>
        {
            foreach (var expired in provider.codes.Where(c => c.Value < DateTimeOffset.UtcNow.AddMinutes(-5))) { provider.codes.TryRemove(expired.Key, out _); }
            if (provider.codes.Count >= 32) { return Results.StatusCode(429); }
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(20));
            provider.codes[code] = DateTimeOffset.UtcNow;
            return Results.Ok(new { device_code = code, user_code = "TEST-ONLY", verification_uri = "https://github.com/login/device", interval = 1, expires_in = 300 });
        });
        app.MapPost("/login/oauth/access_token", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            var code = form["device_code"].ToString();
            if (!provider.codes.TryRemove(code, out var created) || created < DateTimeOffset.UtcNow.AddMinutes(-5))
            { return Results.Ok(new { error = "expired_token" }); }
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            provider.tokens[token] = 0;
            return Results.Ok(new { access_token = token, token_type = "bearer", scope = "" });
        });
        app.MapGet("/user", (HttpRequest request) =>
        {
            var header = request.Headers.Authorization.ToString();
            return header.StartsWith("Bearer ", StringComparison.Ordinal) && provider.tokens.ContainsKey(header[7..])
                ? Results.Ok(new { id = 9000001, login = Login }) : Results.Unauthorized();
        });
        app.MapDelete("/applications/{client}/token", async (HttpRequest request) =>
        {
            var body = await request.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            if (body.TryGetProperty("access_token", out var token)) { provider.tokens.TryRemove(token.GetString() ?? "", out _); }
            return Results.NoContent();
        });
        try { await app.StartAsync(ct); return app; }
        catch { await app.DisposeAsync(); throw; }
    }
}
