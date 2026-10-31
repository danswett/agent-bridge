namespace AgentBridge.Relay;

public sealed class RelayOptions
{
    public string GitHubClientId { get; set; } = "";
    // Optional. With it the relay revokes each GitHub token as soon as it has read the
    // account id, so no GitHub credential outlives sign-in anywhere.
    public string? GitHubClientSecret { get; set; }
    public string GitHubBaseUrl { get; set; } = "https://github.com";
    public string GitHubApiUrl { get; set; } = "https://api.github.com";
    // Base64, 32 bytes. Without it tokens are signed with a per-process key and every
    // restart signs everyone out.
    public string? TokenKey { get; set; }
    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromDays(30);
    public int MaxFramesPerSecond { get; set; } = 200;
    public int MaxDevicesPerAccount { get; set; } = 32;
    // Per replica. Sized for the lean stamp (0.5 GiB); raise it with the replica's memory.
    public long MaxConnections { get; set; } = 10_000;
    // New sockets per client address per minute. Devices reconnect with backoff, so this
    // only bites a reconnect loop or abuse.
    public int MaxConnectsPerMinutePerIp { get; set; } = 120;
    // The build that is serving, reported by /healthz so a deploy can check it. Set by the
    // deployment (infra/), never trusted for anything.
    public string? Version { get; set; }
    // Web origins allowed to call the sign-in API and open sockets, such as the PWA on
    // GitHub Pages. Native clients send no Origin and are unaffected.
    public string[] AllowedOrigins { get; set; } = [];
    // Production serves no app code: a relay that could change the PWA it serves could
    // read what the PWA decrypts. A dev stack can serve one same-origin from wwwroot. Off
    // unless asked for, and the product image ships no wwwroot (the spike defaulted to on).
    public bool ServePwa { get; set; }

    // Web Push (spike S5). The relay signs and forwards pushes that hosts have already
    // encrypted to the browser; it cannot read them. Unset key: pushes are refused.
    public string? VapidPrivateKey { get; set; }
    public string VapidSubject { get; set; } = "https://github.com/danswett/agent-bridge";
    // The relay only ever POSTs to these push services (host or a subdomain of it), over
    // HTTPS, so a device cannot use it to reach anything else.
    public string[] PushHosts { get; set; } = ["web.push.apple.com", "fcm.googleapis.com", "push.services.mozilla.com", "notify.windows.com"];
    // Test only: allows http:// push endpoints, for a local fake push service.
    public bool AllowInsecurePush { get; set; }
    public int MaxPushesPerMinute { get; set; } = 30;

    // The blob store for files over 256 KiB (design §4.6; Blobs.cs). Off unless configured.
    public BlobOptions Blobs { get; set; } = new();

    // The account's machines, online or not (Roster.cs, docs/protocol/roster.md). In memory
    // unless a store is configured.
    public RosterOptions Roster { get; set; } = new();

    // Test hooks. Tap observes every forwarded payload; Route lets a test play a hostile
    // relay that redirects or forges routing.
    public Action<string, string, byte[]>? Tap { get; set; }
    public Func<string, string, (string From, string To)>? Route { get; set; }
}
