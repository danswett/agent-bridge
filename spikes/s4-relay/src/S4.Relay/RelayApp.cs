using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;

namespace S4.Relay;

public static class RelayApp
{
    public static WebApplication Build(string[] args, Action<RelayOptions>? configure = null, Action<WebApplicationBuilder>? builderHook = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = args,
            // The PWA is copied next to the binaries, so this works from bin/ and from a container.
            WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
        });
        var options = builder.Configuration.GetSection("Relay").Get<RelayOptions>() ?? new RelayOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<Tokens>();
        builder.Services.AddSingleton<RelayHub>();
        builder.Services.AddHttpClient("github", c =>
        {
            c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("agent-bridge-relay-s4", "0.1"));
            c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            c.Timeout = TimeSpan.FromSeconds(15);
        });
        builder.Services.Configure<ForwardedHeadersOptions>(o =>
        {
            // Behind the hosting platform's ingress, the client address arrives in X-Forwarded-For.
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        });
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("auth", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1) }));
        });
        builderHook?.Invoke(builder);

        var app = builder.Build();
        app.UseForwardedHeaders();
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["Referrer-Policy"] = "no-referrer";
            // 'self' alone does not cover WebSockets in every Safari, so name the socket origin.
            var wsScheme = ctx.Request.IsHttps ? "wss" : "ws";
            h["Content-Security-Policy"] = $"default-src 'self'; connect-src 'self' {wsScheme}://{ctx.Request.Host}; img-src 'self' data:; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'";
            await next();
        });
        app.UseRateLimiter();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = c =>
            {
                if (c.File.Name.EndsWith(".webmanifest")) { c.Context.Response.ContentType = "application/manifest+json"; }
                c.Context.Response.Headers.CacheControl = "no-cache";
            },
        });

        app.MapGet("/healthz", (RelayHub hub) => Results.Ok(new { ok = true, online = hub.Online }));

        app.MapPost("/v1/auth/device", async (IHttpClientFactory f) =>
        {
            var gh = f.CreateClient("github");
            using var r = await gh.PostAsync($"{options.GitHubBaseUrl}/login/device/code",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = options.GitHubClientId }));
            if (!r.IsSuccessStatusCode) { return Results.StatusCode(StatusCodes.Status502BadGateway); }
            var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            if (!j.TryGetProperty("device_code", out var code)) { return Results.StatusCode(StatusCodes.Status502BadGateway); }
            return Results.Ok(new
            {
                deviceCode = code.GetString(),
                userCode = j.GetProperty("user_code").GetString(),
                verificationUri = j.GetProperty("verification_uri").GetString(),
                interval = j.GetProperty("interval").GetInt32(),
                expiresIn = j.GetProperty("expires_in").GetInt32(),
            });
        }).RequireRateLimiting("auth");

        app.MapPost("/v1/auth/token", async (TokenRequest req, IHttpClientFactory f, Tokens tokens, ILoggerFactory lf) =>
        {
            var log = lf.CreateLogger("auth");
            if (string.IsNullOrEmpty(req.DeviceCode) || req.DeviceCode.Length > 200) { return Results.BadRequest(new { error = "bad_request" }); }
            var gh = f.CreateClient("github");
            using var r = await gh.PostAsync($"{options.GitHubBaseUrl}/login/oauth/access_token", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = options.GitHubClientId,
                ["device_code"] = req.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
            }));
            var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
            if (!j.TryGetProperty("access_token", out var at))
            {
                var error = j.TryGetProperty("error", out var e) ? e.GetString() : "github_error";
                return error switch
                {
                    "authorization_pending" => Results.Json(new { pending = true }, statusCode: 202),
                    "slow_down" => Results.Json(new { pending = true, interval = j.TryGetProperty("interval", out var i) ? i.GetInt32() : 10 }, statusCode: 202),
                    _ => Results.BadRequest(new { error }),
                };
            }
            var githubToken = at.GetString()!;
            using var userReq = new HttpRequestMessage(HttpMethod.Get, $"{options.GitHubApiUrl}/user");
            userReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", githubToken);
            using var ur = await gh.SendAsync(userReq);
            if (!ur.IsSuccessStatusCode) { return Results.StatusCode(StatusCodes.Status502BadGateway); }
            var user = JsonDocument.Parse(await ur.Content.ReadAsStringAsync()).RootElement;
            var account = new Account(user.GetProperty("id").GetInt64().ToString(), user.GetProperty("login").GetString()!);
            await RevokeAsync(gh, githubToken, log);
            var (token, expires) = tokens.Issue(account);
            log.LogInformation("signed in {Login} ({Id})", account.Login, account.Id);
            return Results.Ok(new { token, accountId = account.Id, login = account.Login, expiresAt = expires });
        }).RequireRateLimiting("auth");

        app.Map("/v1/ws", async (HttpContext ctx, RelayHub hub) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.RunAsync(ws, ctx.RequestAborted);
        });

        return app;

        async Task RevokeAsync(HttpClient gh, string githubToken, ILogger log)
        {
            if (string.IsNullOrEmpty(options.GitHubClientSecret)) { return; }
            using var del = new HttpRequestMessage(HttpMethod.Delete, $"{options.GitHubApiUrl}/applications/{options.GitHubClientId}/token")
            {
                Content = JsonContent.Create(new { access_token = githubToken }),
            };
            del.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($"{options.GitHubClientId}:{options.GitHubClientSecret}")));
            try
            {
                using var d = await gh.SendAsync(del);
                if (!d.IsSuccessStatusCode) { log.LogWarning("revoking the GitHub token returned {Status}", (int)d.StatusCode); }
            }
            catch (HttpRequestException e) { log.LogWarning("revoking the GitHub token failed: {Message}", e.Message); }
        }
    }

    public sealed record TokenRequest(string? DeviceCode);
}
