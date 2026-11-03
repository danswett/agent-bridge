using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Relay;
using AgentBridge.Transport;

namespace AgentBridge.AppleInterop;

internal sealed class InteropStack : IAsyncDisposable
{
    public const string InitialMarker = "The .NET host parsed this temporary transcript.";
    public const string UpdatedMarker = "Live update received from the real .NET session parser.";
    readonly List<IAsyncDisposable> owned = [];
    readonly CancellationTokenSource lifetime = new();
    readonly SyntheticSessions fixture;
    readonly SessionSource source;
    readonly P256KeyPair key = P256KeyPair.Generate();
    readonly string manifestPath;
    bool manifestWritten;
    bool disposed;
    Task? triggerTask;
    RelayClient? host;
    string hostToken = "";
    SessionService? sessions;
    readonly SemaphoreSlim commandGate = new(1);

    public string Root { get; } = PrivateFiles.CreateRoot();
    public Uri RelayUrl { get; private set; } = null!;
    public Uri InternalRelayUrl { get; private set; } = null!;
    public string LocalSocketPath { get; private set; } = "";
    public string HostDeviceId => Wire.DeviceId(key.Public);
    public string ClientToken { get; private set; } = "";
    public string UpdateTriggerPath => Path.Combine(Root, "advance.trigger");

    InteropStack(string manifestPath)
    {
        this.manifestPath = manifestPath;
        fixture = new SyntheticSessions(Root);
        source = new SessionSource(fixture.Options, fixture, TimeProvider.System);
        owned.Add(source);
    }

    // Null certificate is available only to this project's tests. The command-line tool
    // always requires a PFX; its Apple endpoint never relaxes HTTPS or certificate checks.
    internal static async Task<InteropStack> StartAsync(string manifestPath, X509Certificate2? certificate, CancellationToken ct = default)
    {
        var stack = new InteropStack(manifestPath);
        try { await stack.StartAsync(certificate, ct); return stack; }
        catch { await stack.DisposeAsync(); throw; }
    }

    async Task StartAsync(X509Certificate2? certificate, CancellationToken ct)
    {
        if (!HttpClient.DefaultProxy.IsBypassed(new Uri("http://127.0.0.1/")))
        { throw new InvalidOperationException("isolated interop requires loopback traffic to bypass all proxies"); }
        if (File.Exists(manifestPath) || Directory.Exists(manifestPath)) { throw new IOException("the manifest path already exists"); }
        var github = await LocalGitHub.StartAsync(ct);
        owned.Add(github);
        var githubUrl = github.Urls.Single();
        var relay = RelayApp.Build([], options =>
        {
            options.GitHubBaseUrl = options.GitHubApiUrl = githubUrl;
            options.GitHubClientId = "isolated-apple-interop";
            options.GitHubClientSecret = "local-provider-only";
            // Never inherit a developer's cloud roster, credentials, push provider or PWA.
            options.TokenKey = null;
            options.TokenLifetime = TimeSpan.FromHours(1);
            options.Blobs = new BlobOptions { Backend = "local", MaxBytes = 9 * 1024 * 1024, MaxBytesPerAccountPerDay = 64 * 1024 * 1024 };
            options.Roster = new RosterOptions();
            options.VapidPrivateKey = null;
            options.PushHosts = [];
            options.AllowedOrigins = [];
            options.ServePwa = false;
            options.MaxConnections = 16;
            options.Version = "apple-interop-synthetic";
        }, builder =>
        {
            // Ambient ASPNETCORE/Kestrel settings must not add a non-loopback listener.
            builder.Configuration.Sources.Clear();
            builder.Configuration.AddInMemoryCollection();
            builder.Logging.ClearProviders();
            builder.Services.AddHttpClient("github").ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false });
            builder.WebHost.ConfigureKestrel(server =>
            {
                // This second loopback-only listener keeps .NET peer setup independent of
                // workstation certificate installation. Both listeners use the same product
                // relay/token/Noise services. The Apple manifest names the HTTPS listener.
                server.Listen(IPAddress.Loopback, 0);
                if (certificate is not null) { server.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(certificate)); }
            });
        });
        owned.Add(relay);
        await relay.StartAsync(ct);
        InternalRelayUrl = new Uri(relay.Urls.Single(url => url.StartsWith("http://", StringComparison.Ordinal)));
        RelayUrl = certificate is null ? InternalRelayUrl
            : new UriBuilder(relay.Urls.Single(url => url.StartsWith("https://", StringComparison.Ordinal))) { Host = "localhost" }.Uri;
        var tokens = relay.Services.GetRequiredService<Tokens>();
        hostToken = tokens.Issue(new Account(LocalGitHub.AccountId, LocalGitHub.Login)).Token;
        ClientToken = tokens.Issue(new Account(LocalGitHub.AccountId, LocalGitHub.Login)).Token;
        var machine = new MachineInfo(HostDeviceId, "Isolated .NET interoperability host", "macos", "interop", Dev: true, Instance: Guid.NewGuid().ToString("N"));
        sessions = new SessionService(source, machine)
        {
            ReadOnly = () => "Synthetic transcript fixture: no real agent actions are connected.",
        };
        await source.StartAsync(lifetime.Token);
        await source.RefreshAsync();
        await ConnectHostAsync(ct);
        var control = new HostControl(new HostControlOptions
        {
            Profile = "isolated-interop", Folder = Root, Version = "interop", Dev = true, Machine = machine,
            Running = new HostSettingsView(RemoteApproval: false), Relay = () => RelayUrl,
            SignIn = (_, _, _) => throw new InvalidOperationException("host management is not part of this fixture"),
            SignedIn = (_, _) => throw new InvalidOperationException("host management is not part of this fixture"),
            Stop = () => throw new InvalidOperationException("use the harness lifetime control"),
            Restart = () => throw new InvalidOperationException("use the harness lifetime control"),
        });
        var endpoint = LocalEndpoint.For(Root);
        LocalSocketPath = endpoint.Address;
        // Do not impersonate the privileged app. Unix peer ancestry remains unknown, exactly
        // as it is in the product, so management requests remain refused.
        var callers = new CallerGuard(() => [], appImage: null);
        owned.Add(LocalServer.Start(endpoint, LocalApi.Handler(sessions, control, callers)));
        var manifest = new
        {
            relayUrl = RelayUrl.ToString().TrimEnd('/'), localSocketPath = LocalSocketPath,
            accountID = LocalGitHub.AccountId, login = LocalGitHub.Login, clientToken = ClientToken,
            hostDeviceID = HostDeviceId, fixtureRoot = Root, sessionID = SyntheticSessions.SessionId,
            initialMarker = InitialMarker, updatedMarker = UpdatedMarker, updateTriggerPath = UpdateTriggerPath,
            delegateID = SyntheticSessions.DelegateId,
            certificate = certificate is null ? null : new { subject = certificate.Subject, sha256 = certificate.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256), expiresAt = certificate.NotAfter.ToUniversalTime() },
            syntheticTranscripts = true, actualAgentTyping = false,
        };
        PrivateFiles.WriteNew(manifestPath, JsonSerializer.Serialize(manifest));
        manifestWritten = true;
        triggerTask = WatchTriggerAsync();
    }

    async Task ConnectHostAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        host = await RelayClient.ConnectAsync(InternalRelayUrl, hostToken, key, "Isolated .NET interoperability host", "host", timeout.Token);
        _ = new ChannelHub(host, key, "Isolated .NET interoperability host", "host")
        { JsonOptions = ProtocolJson.Options, Handler = sessions!.HandleAsync };
    }

    public async Task CommandAsync(string command, CancellationToken ct = default)
    {
        await commandGate.WaitAsync(ct);
        try
        {
            switch (command)
            {
                case "advance": fixture.Advance(); await source.RefreshAsync(); break;
                case "remove": fixture.Remove(); await source.RefreshAsync(); break;
                case "reconnect":
                    if (host is not null) { await host.DisposeAsync(); host = null; }
                    await ConnectHostAsync(ct);
                    break;
                default: throw new ArgumentException("supported commands are advance, remove, reconnect and quit");
            }
        }
        finally { commandGate.Release(); }
    }

    async Task WatchTriggerAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                if (File.Exists(UpdateTriggerPath))
                {
                    var size = new FileInfo(UpdateTriggerPath).Length;
                    if (size <= 16 && (await File.ReadAllTextAsync(UpdateTriggerPath, lifetime.Token)).Trim() == "advance")
                    {
                        File.Delete(UpdateTriggerPath);
                        // A late trigger after remove is an invalid fixture command, not a
                        // reason to strand the relay or its temporary credentials on exit.
                        try { await CommandAsync("advance", lifetime.Token); }
                        catch (InvalidOperationException) { }
                    }
                    else { File.Delete(UpdateTriggerPath); }
                }
                await Task.Delay(100, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) { return; }
        disposed = true;
        var errors = new List<Exception>();
        async Task Attempt(Func<Task> action)
        {
            try { await action(); } catch (Exception e) { errors.Add(e); }
        }
        await Attempt(() => lifetime.CancelAsync());
        if (triggerTask is not null) { await Attempt(() => triggerTask); }
        if (host is not null) { await Attempt(() => host.DisposeAsync().AsTask()); }
        for (var i = owned.Count - 1; i >= 0; i--) { await Attempt(() => owned[i].DisposeAsync().AsTask()); }
        if (manifestWritten) { await Attempt(() => { File.Delete(manifestPath); return Task.CompletedTask; }); }
        await Attempt(() => { Directory.Delete(Root, recursive: true); return Task.CompletedTask; });
        lifetime.Dispose();
        commandGate.Dispose();
        if (errors.Count > 0) { throw new AggregateException("isolated interop cleanup failed", errors); }
    }
}
