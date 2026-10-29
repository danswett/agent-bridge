using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// What HostControl needs from the process it manages, so tests can stand in for it.
public sealed class HostControlOptions
{
    public required string Profile { get; init; }
    public required string Folder { get; init; }
    public required string Version { get; init; }
    public bool Dev { get; init; }
    public bool Elevated { get; init; }
    public required MachineInfo Machine { get; init; }
    // What the host is running with; config.json may since have changed.
    public required HostSettingsView Running { get; init; }
    public bool LocalOnly { get; init; }
    public string? Log { get; init; }
    public Func<HostAccount?> Account { get; init; } = () => null;
    public Func<Uri?> Relay { get; init; } = () => null;
    public Func<string?> AgentHaBridge { get; init; } = () => null;
    // GitHub's device flow through a relay: (relay, show(code, uri), ct) -> the account.
    public required Func<Uri, Action<string, string>, CancellationToken, Task<SignInResult>> SignIn { get; init; }
    // Keeps a finished sign-in (the profile's account and relay) and starts using it.
    public required Action<SignInResult, Uri> SignedIn { get; init; }
    public required Action Stop { get; init; }
    public required Action Restart { get; init; }
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

// The host's management ops (AgentBridge.Local.HostOps), for the desktop app on the local
// pipe only: what the host is and how it is doing, signing it in to the relay, its settings,
// and stopping or restarting it. The relay's channels never reach this.
public sealed class HostControl
{
    readonly HostControlOptions options;
    readonly Action<string> log;
    readonly object gate = new();
    readonly DateTimeOffset startedAt;
    readonly Dictionary<IProtocolChannel, Subscriber> subscribers = [];
    RelayStatus relay;
    SignInStatus signIn = new(SignInState.Idle);
    CancellationTokenSource? signInStop;
    long version;

    public HostControl(HostControlOptions options, Action<string>? log = null)
    {
        this.options = options;
        this.log = log ?? (_ => { });
        startedAt = options.Time.GetUtcNow();
        relay = options.LocalOnly ? new RelayStatus(RelayState.Off, Detail: "this host serves this machine's apps only (--local-only)")
            : options.Account() is null ? new RelayStatus(RelayState.SignedOut, options.Relay()?.ToString())
            : new RelayStatus(RelayState.Connecting, options.Relay()?.ToString());
    }

    // agent-ha-bridge's state is read for every status the app asks for; looking at most every
    // few seconds keeps that from listing the session's named objects on every event. The first
    // read always looks: a "never looked" sentinel of long.MinValue overflowed the subtraction
    // and kept it from ever looking.
    public static Func<string?> Cached(Func<string?> probe, Func<long> milliseconds, long everyMs = 5000)
    {
        var gate = new object();
        long? at = null;
        string? last = null;
        return () =>
        {
            lock (gate)
            {
                var now = milliseconds();
                if (at is not { } then || now - then > everyMs) { last = probe(); at = now; }
                return last;
            }
        };
    }

    public HostStatus Status()
    {
        lock (gate) { return StatusLocked(); }
    }

    HostStatus StatusLocked()
    {
        var running = options.Running;
        var now = Configured();
        return new HostStatus
        {
            Version = options.Version,
            Dev = options.Dev,
            Profile = options.Profile,
            Folder = options.Folder,
            ProcessId = Environment.ProcessId,
            StartedAt = startedAt,
            Elevated = options.Elevated,
            Machine = options.Machine,
            Account = options.Account(),
            Relay = relay,
            SignIn = signIn,
            AgentHaBridge = options.AgentHaBridge(),
            Settings = now,
            RestartRequired = now != running,
            Log = options.Log,
        };
    }

    HostSettingsView Configured()
    {
        var (settings, _) = HostSettings.Load(options.Folder);
        return new HostSettingsView(settings.RemoteApproval, HostConfigFile.ReadName(options.Folder));
    }

    // The relay loop reports each change of its connection here.
    public void SetRelay(RelayState state, string? detail = null)
    {
        lock (gate)
        {
            if (relay.State == state && relay.Detail == detail) { return; }
            relay = new RelayStatus(state, options.Relay()?.ToString(), detail, options.Time.GetUtcNow());
        }
        Changed();
    }

    public async Task<object?> HandleAsync(IProtocolChannel channel, string op, JsonElement data)
    {
        switch (op)
        {
            case HostOps.Status:
                return Status();
            case HostOps.Subscribe:
                return Subscribe(channel);
            case HostOps.SignIn:
                return await SignInAsync(Read<SignInRequest>(data) ?? new SignInRequest());
            case HostOps.SignInCancel:
                CancelSignIn();
                return Status().SignIn;
            case HostOps.Settings:
                return Settings(Read<SettingsRequest>(data) ?? new SettingsRequest());
            case HostOps.Stop:
                log("stopping: asked by the app");
                return new ChannelReply(new { stopping = true }, _ => { options.Stop(); return Task.CompletedTask; });
            case HostOps.Restart:
                log("restarting: asked by the app");
                return new ChannelReply(new { restarting = true }, _ => { options.Restart(); return Task.CompletedTask; });
            default:
                return null;
        }
    }

    static T? Read<T>(JsonElement data) where T : class
    {
        if (data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) { return null; }
        try { return data.Deserialize<T>(ProtocolJson.Options); }
        catch (JsonException e) { throw new ArgumentException($"the request could not be read: {e.Message}"); }
    }

    object Settings(SettingsRequest request)
    {
        if (request.Name is { } name && name.Length > 0 && HostConfigFile.CleanName(name) is null) { throw new ArgumentException("the name has no visible characters"); }
        HostConfigFile.Patch(options.Folder, request.RemoteApproval, request.Name);
        log($"settings changed by the app{(request.RemoteApproval is { } r ? $": remote approval {(r ? "on" : "off")}" : "")}{(request.Name is not null ? "; machine name changed" : "")}");
        var now = Configured();
        Changed();
        return new SettingsResult(now, now != options.Running);
    }

    async Task<SignInStatus> SignInAsync(SignInRequest request)
    {
        if (options.LocalOnly) { throw new InvalidOperationException("this host was started with --local-only and does not use the relay"); }
        var relayUri = request.Relay is { Length: > 0 } r ? ParseRelay(r) : options.Relay() ?? throw new ArgumentException("which relay? give its URL");
        CancellationTokenSource stop;
        lock (gate)
        {
            // One at a time: a second press while the person is entering the code shows that code.
            if (signIn.State == SignInState.Pending && signInStop is not null) { return signIn; }
            signInStop = stop = new CancellationTokenSource(TimeSpan.FromMinutes(20));
            signIn = new SignInStatus(SignInState.Pending);
        }
        Changed();
        var shown = new TaskCompletionSource<SignInStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await options.SignIn(relayUri, (code, uri) =>
                {
                    SignInStatus now;
                    lock (gate) { now = signIn = new SignInStatus(SignInState.Pending, code, uri); }
                    Changed();
                    shown.TrySetResult(now);
                }, stop.Token);
                options.SignedIn(result, relayUri);
                log($"signed in as {result.Login} through {relayUri.Host}");
                lock (gate)
                {
                    signIn = new SignInStatus(SignInState.Idle);
                    relay = new RelayStatus(RelayState.Connecting, relayUri.ToString(), null, options.Time.GetUtcNow());
                }
            }
            catch (Exception e)
            {
                var why = stop.IsCancellationRequested ? "sign-in was cancelled or the code expired" : e.Message;
                log($"sign-in failed: {why}");
                lock (gate) { signIn = new SignInStatus(SignInState.Failed, Error: why); }
                shown.TrySetResult(signIn);
            }
            finally
            {
                lock (gate) { if (signInStop == stop) { signInStop = null; } }
                stop.Dispose();
            }
            Changed();
        });
        return await shown.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    void CancelSignIn()
    {
        lock (gate) { signInStop?.Cancel(); }
    }

    // https, or http to this machine for a test stack; nothing else carries a token.
    static Uri ParseRelay(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !(uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)) || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("the relay must be an https URL");
        }
        return uri;
    }

    // host.subscribe: the status now, then a host.status event whenever it changes. As for
    // usage.subscribe, nothing is sent ahead of the response.
    object Subscribe(IProtocolChannel channel)
    {
        Subscriber s;
        HostStatus status;
        lock (gate)
        {
            if (!subscribers.TryGetValue(channel, out s!))
            {
                subscribers[channel] = s = new Subscriber();
                _ = channel.Closed.ContinueWith(_ => { lock (gate) { subscribers.Remove(channel); } }, TaskScheduler.Default);
            }
            status = StatusLocked();
            s.Sent = version;
            s.Open = false;
        }
        return new ChannelReply(status, ch =>
        {
            lock (gate) { s.Open = true; }
            return PushAsync(ch, s);
        });
    }

    void Changed()
    {
        List<(IProtocolChannel, Subscriber)> open;
        lock (gate)
        {
            version++;
            open = [.. subscribers.Select(kv => (kv.Key, kv.Value))];
        }
        foreach (var (ch, s) in open) { _ = PushAsync(ch, s); }
    }

    async Task PushAsync(IProtocolChannel channel, Subscriber s)
    {
        await s.Gate.WaitAsync();
        try
        {
            HostStatus status;
            long at;
            lock (gate)
            {
                if (!s.Open || version <= s.Sent) { return; }
                status = StatusLocked();
                at = version;
            }
            if (!channel.IsOpen) { return; }
            await channel.SendEventAsync(HostOps.StatusEvent, status);
            lock (gate) { s.Sent = at; }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally { s.Gate.Release(); }
    }

    // Anything outside the ops that changes what the status says (agent-ha-bridge starting,
    // say) is announced by whoever notices it.
    public void Refresh() => Changed();

    sealed class Subscriber
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public long Sent;
        public bool Open;
    }
}
