using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;

namespace AgentBridge.Relay;

public static class RelayApp
{
    public static WebApplication Build(string[] args, Action<RelayOptions>? configure = null, Action<WebApplicationBuilder>? builderHook = null)
    {
        // A dev stack's PWA (ServePwa) sits next to the binaries, so this works from bin/ and from a
        // container. Only when it is there: ASP.NET creates a web root it is given, and the product
        // image has none and runs as a user that cannot write /app, so naming it crashed at start.
        var webRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = args,
            WebRootPath = Directory.Exists(webRoot) ? webRoot : null,
        });
        var options = builder.Configuration.GetSection("Relay").Get<RelayOptions>() ?? new RelayOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<Tokens>();
        builder.Services.AddSingleton<RelayHub>();
        builder.Services.AddSingleton<PushGateway>();
        builder.Services.AddSingleton(sp => new RelayMetrics(sp.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(), () => sp.GetRequiredService<RelayHub>().Online));
        // Request logs say nothing an operator needs and cost ingestion; warnings still show.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(k =>
        {
            // The only request bodies are sign-in JSON of a few hundred bytes. WebSocket
            // frames are bounded separately, by Wire.MaxFrame.
            k.Limits.MaxRequestBodySize = 16 * 1024;
            // Memory per replica is the binding limit (design §5.9). Past this a replica
            // refuses upgrades instead of falling over, and the devices retry elsewhere.
            k.Limits.MaxConcurrentUpgradedConnections = options.MaxConnections;
        });
        // Long enough to send every device its 1012 and let the sockets close, and inside
        // the platform's termination grace period (30 s in infra/).
        builder.Services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(20));
        builder.Services.AddHttpClient("push", c => c.Timeout = TimeSpan.FromSeconds(15))
            // Never follow a redirect: it would take the request off the push-service allowlist.
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            // HttpClientFactory logs every request URL at Information, and a push endpoint is a
            // bearer capability: anyone holding it can send that phone notifications.
            .RemoveAllLoggers();
        builder.Services.AddHttpClient("github", c =>
        {
            c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("agent-bridge-relay", "0.1"));
            c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            c.Timeout = TimeSpan.FromSeconds(15);
        }).RemoveAllLoggers();
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
            // Reconnect storms and socket abuse: a device reconnects with backoff, so even a
            // NAT shared by many devices stays well under this.
            o.AddPolicy("connect", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = options.MaxConnectsPerMinutePerIp, Window = TimeSpan.FromMinutes(1) }));
        });
        builder.Services.AddCors(o => o.AddPolicy("pwa", p => p.WithOrigins(options.AllowedOrigins).WithMethods("GET", "POST").WithHeaders("content-type")));
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
        app.UseCors();
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(20),
            // Ping and require a pong, so a connection that died without a close frame is dropped.
            KeepAliveTimeout = TimeSpan.FromSeconds(30),
        });
        app.Lifetime.ApplicationStopping.Register(() => app.Services.GetRequiredService<RelayHub>().DrainAsync().Wait(TimeSpan.FromSeconds(5)));
        if (options.ServePwa)
        {
            app.UseDefaultFiles();
            app.UseStaticFiles(new StaticFileOptions
            {
                OnPrepareResponse = c =>
                {
                    if (c.File.Name.EndsWith(".webmanifest")) { c.Context.Response.ContentType = "application/manifest+json"; }
                    c.Context.Response.Headers.CacheControl = "no-cache";
                },
            });
        }
        else { app.MapGet("/", () => Results.Text("Agent Bridge relay. It serves no app code; the PWA is published separately.")); }

        // Liveness: the process answers. Version lets a deploy check which build is serving.
        app.MapGet("/healthz", (RelayHub hub) => Results.Ok(new { ok = true, online = hub.Online, version = options.Version }));
        // Readiness: false once draining, so the platform stops sending new sockets here.
        app.MapGet("/readyz", (RelayHub hub) => hub.Draining
            ? Results.Json(new { ready = false }, statusCode: StatusCodes.Status503ServiceUnavailable)
            : Results.Ok(new { ready = true }));

        // The key a browser subscribes with (applicationServerKey). Public by design.
        app.MapGet("/v1/push/key", (PushGateway push) =>
            push.PublicKey is { } k ? Results.Ok(new { key = k }) : Results.NotFound()).RequireCors("pwa");

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
        }).RequireRateLimiting("auth").RequireCors("pwa");

        app.MapPost("/v1/auth/token", async (TokenRequest req, IHttpClientFactory f, Tokens tokens, RelayMetrics metrics, ILoggerFactory lf) =>
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
                    _ => SignInFailed(metrics, error),
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
            // No login or id: logs carry no identities (design §5.10).
            metrics.SignedIn("ok");
            log.LogInformation("sign-in completed");
            return Results.Ok(new { token, accountId = account.Id, login = account.Login, expiresAt = expires });
        }).RequireRateLimiting("auth").RequireCors("pwa");

        app.Map("/v1/ws", async (HttpContext ctx, RelayHub hub, RelayMetrics metrics) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            if (hub.Draining) { ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable; return; }
            // Browsers always send Origin; only the relay's own page and the allowed origins
            // may open a socket. Native clients send none.
            var origin = ctx.Request.Headers.Origin.ToString();
            if (origin.Length > 0 && !options.AllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)
                && !(Uri.TryCreate(origin, UriKind.Absolute, out var o) && o.Authority.Equals(ctx.Request.Host.Value, StringComparison.OrdinalIgnoreCase)))
            {
                metrics.Refused(StatusCodes.Status403Forbidden);
                ctx.Response.StatusCode = 403;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await hub.RunAsync(ws, ctx.RequestAborted);
        }).RequireRateLimiting("connect");

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

    static IResult SignInFailed(RelayMetrics metrics, string? error)
    {
        metrics.SignedIn("refused");
        return Results.BadRequest(new { error });
    }

    public sealed record TokenRequest(string? DeviceCode);
}
