using System.Text.Json;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// Moving an ended session between the account's machines (docs/host-transfer.md,
// docs/protocol/v1-transfer.md), both halves, over whichever relay connection the host has now
// (Attach is called on every connect and disconnect):
//   * The target (FetchAsync, for LaunchService): asked by a person's device to resume a session
//     that is on another machine, it opens a channel to that machine, hands it the command the
//     device signed for it, fetches the bundle that machine sealed into the blob store for it,
//     checks the SHA-256, deletes the blob, and gives LaunchService the file to install.
//   * The source (ServeAsync, for session.transfer): it acts only on a command a device of the
//     account signed for this machine (SignedCommands), naming the machine that asks, once,
//     within its lifetime, for a session this machine offers for resume. It packs the session
//     read-only into its private staging, seals it into the blob store for the asking machine
//     alone, and answers with the key, inside the channel.
// The relay sees two small requests and ciphertext; it can neither read a transcript nor send
// one anywhere the person did not ask. This replaces agent-ha-bridge's fleet secret.
public sealed class SessionTransfers : ISessionFetcher, IDisposable
{
    readonly P256KeyPair key;
    readonly string staging;
    readonly Action<string> log;
    readonly TimeProvider time;
    readonly HttpClient http;
    readonly CommandMemory memory;
    // Bundles packed and sent at once: each reads a session and writes it out again.
    readonly SemaphoreSlim sending = new(2, 2);
    Link? link;

    sealed record Link(RelayClient Relay, ChannelHub Hub);

    // http: storage's client, for a test to stand in for storage. Its own by default.
    public SessionTransfers(P256KeyPair key, string staging, Action<string>? log = null, TimeProvider? time = null, HttpClient? http = null)
    {
        this.key = key;
        this.http = http ?? BlobHttp.CreateClient();
        this.staging = staging;
        this.log = log ?? (_ => { });
        this.time = time ?? TimeProvider.System;
        memory = new CommandMemory(this.time);
        Sweep();
    }

    // What a transfer the host did not finish left behind (it stopped part way): only names it
    // makes, only in its own folder, never through a link.
    void Sweep()
    {
        try
        {
            var dir = new DirectoryInfo(staging);
            if (!dir.Exists || dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) { return; }
            foreach (var entry in dir.EnumerateFileSystemInfos())
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) { continue; }
                if (entry is FileInfo f && System.Text.RegularExpressions.Regex.IsMatch(f.Name, "^[0-9a-f]{32}\\.zip$")) { f.Delete(); }
                else if (entry is DirectoryInfo d && System.Text.RegularExpressions.Regex.IsMatch(d.Name, "^unpack-[0-9a-f]{32}$")) { d.Delete(recursive: true); }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { this.log($"could not clear unfinished transfers: {e.Message}"); }
    }

    // Set once the launcher exists: the source's half asks it what is offered.
    public LaunchService? Launcher { get; set; }

    // How long a source may take to pack and seal a bundle, and the target to fetch it.
    public TimeSpan SourceWait { get; init; } = TimeSpan.FromMinutes(10);

    // How long the source may take to pack and upload a bundle once it starts: less than the
    // target waits, so the target hears why. Storage that accepts an upload and then stalls
    // would otherwise hold the bundle and one of the two sending slots until the host restarts,
    // and two such would stop every transfer from this machine.
    public TimeSpan SendFor { get; set; } = TimeSpan.FromMinutes(9);
    public TimeSpan FetchWait { get; init; } = TimeSpan.FromMinutes(20);
    public TimeSpan PeerWait { get; init; } = TimeSpan.FromSeconds(10);

    public string Me => Wire.DeviceId(key.Public);

    // The private folder bundles wait in, here and while one is unpacked to be installed.
    public string Staging => staging;

    public void Attach(RelayClient? relay, ChannelHub? hub) => Volatile.Write(ref link, relay is null || hub is null ? null : new Link(relay, hub));

    bool HasBlobStore => Volatile.Read(ref link) is { Relay.BlobMaxBytes: > 0 };

    // hello's sessions.transfer: session.launch takes a transfer.
    public bool Receives => HasBlobStore && Launcher is { Available: true, TransferConfig.Receive: true };

    // hello's sessions.transfer.serve: this machine answers session.transfer.
    public bool Serves => HasBlobStore && Launcher is { ServeUnavailable: null };

    public string? Unavailable => Volatile.Read(ref link) is not { } l ? "this machine is not connected to the relay now"
        : l.Relay.BlobMaxBytes <= 0 ? "the relay this machine uses has no blob store, which a transfer goes through"
        : null;

    string Folder() => PrivateFolder.Ensure(staging);

    // ---- the target ----

    public async Task<FetchedBundle> FetchAsync(LaunchTransfer transfer, string peer, long maxBytes, CancellationToken cancellationToken)
    {
        var l = Volatile.Read(ref link) ?? throw new TransferRefusedException("this machine is not connected to the relay now");
        if (l.Relay.BlobMaxBytes <= 0) { throw new TransferRefusedException("the relay has no blob store, which a transfer goes through"); }
        // This machine cannot check the MAC (it is for the source), but it can refuse to carry a
        // command that is not about this launch, by the device asking for it, for this machine.
        if (Unsigned(transfer.Command!) is not { } c || c.Action != TransferProtocol.Action || c.Principal.Device != peer || c.To != Me
            || c.Target.Machine != transfer.From || !string.Equals(c.Target.Session, transfer.Session, StringComparison.OrdinalIgnoreCase))
        {
            throw new TransferRefusedException("the signed command is not for this launch: it must be signed by the device asking, for this machine to receive that session");
        }
        if (transfer.From == Me) { throw new TransferRefusedException("that session is on this machine; resume it instead"); }

        PresenceEntry source;
        try { source = await l.Relay.WaitForPeerAsync(p => p.Id == transfer.From, PeerWait); }
        catch (TimeoutException) { throw new TransferRefusedException("the machine it is on is not connected to the relay; it may be asleep or offline"); }
        catch (Exception e) { throw new TransferRefusedException($"this machine lost the relay ({e.Message})"); }

        SecureChannel ch;
        try { ch = await l.Hub.OpenAsync(transfer.From, TimeSpan.FromSeconds(15)); }
        // Whatever goes wrong with the relay or the handshake (a WebSocket that drops included) is
        // the launch's refusal, never an exception out of it.
        catch (Exception e) { throw new TransferRefusedException($"{source.Name} did not answer ({e.Message})"); }
        TransferResult result;
        try
        {
            var response = await ch.RequestAsync(TransferProtocol.Op, new TransferRequest { Command = transfer.Command! }, SourceWait, cancellationToken);
            if (!response.GetProperty("ok").GetBoolean())
            {
                var error = response.TryGetProperty("error", out var e) ? e.GetString() : null;
                throw new TransferRefusedException($"{source.Name} refused: {error ?? "no reason given"}");
            }
            result = response.GetProperty("result").Deserialize<TransferResult>(ProtocolJson.Options) ?? throw new TransferRefusedException($"{source.Name} sent no bundle");
        }
        catch (TimeoutException) { throw new TransferRefusedException($"{source.Name} took over {SourceWait.TotalMinutes:0} minutes to send it"); }
        catch (IOException e) { throw new TransferRefusedException($"the channel to {source.Name} ended ({e.Message})"); }
        catch (JsonException) { throw new TransferRefusedException($"{source.Name}'s answer was not a bundle"); }
        catch (Exception e) when (e is not TransferRefusedException) { throw new TransferRefusedException($"the channel to {source.Name} failed ({e.Message})"); }
        finally
        {
            try { await l.Hub.CloseAsync(ch); }
            catch (Exception) { }
        }

        if (TransferLimits.CheckResult(result, maxBytes) is { } bad)
        {
            await ForgetAsync(l.Relay, result.Blob?.Id);
            throw new TransferRefusedException(bad);
        }
        var bundleKey = BlobFormat.FromBase64Url(result.Blob.Key);
        if (bundleKey is not { Length: 32 }) { await ForgetAsync(l.Relay, result.Blob.Id); throw new TransferRefusedException("the bundle's key is malformed"); }

        string? zip = null;
        try
        {
            // Inside: a staging folder that cannot be made still forgets the blob.
            zip = Path.Combine(Folder(), $"{Guid.NewGuid():N}.zip");
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(FetchWait);
            byte[] sha;
            await using (var file = PrivateFolder.CreateNew(zip))
            {
                sha = await BlobTransfer.ReceiveAsync(l.Relay, http, result.Blob.Id, bundleKey, result.Size, file, ct: limit.Token);
            }
            // The SHA-256 the source gave, inside the channel: a blob that opens but is not what
            // was sent (or not all of it) is never installed.
            if (!System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(sha, Attachments.Sha256(result.Sha256)!))
            {
                throw new TransferRefusedException("the bundle that arrived does not match its sha256; nothing was installed");
            }
            log($"fetched a session bundle of {TransferLimits.Mib(result.Size)} from {transfer.From[..8]}");
            return new FetchedBundle(zip, result, transfer.From);
        }
        catch (Exception e) when (e is not TransferRefusedException)
        {
            Delete(zip);
            throw new TransferRefusedException(e switch
            {
                BlobIntegrityException i => $"the bundle in the blob store is not what was sent ({i.Message})",
                BlobRefusedException r => $"the relay refused the bundle ({r.Reason})",
                OperationCanceledException => $"fetching the bundle took over {FetchWait.TotalMinutes:0} minutes",
                IOException or UnauthorizedAccessException when zip is null => $"this machine's transfer folder could not be prepared ({e.Message})",
                _ => $"the bundle could not be fetched ({e.Message})",
            });
        }
        catch
        {
            Delete(zip);
            throw;
        }
        finally
        {
            // Whatever became of it, nobody else can use it.
            await ForgetAsync(l.Relay, result.Blob.Id);
        }
    }

    // A command's body as it reads, without checking who signed it.
    static CommandBody? Unsigned(SignedCommand command)
    {
        if (TransferProtocol.Check(command) is not null || BlobFormat.FromBase64Url(command.Body) is not { } body) { return null; }
        try
        {
            var c = JsonSerializer.Deserialize<CommandBody>(body, ProtocolJson.Options);
            return c is { Principal: not null, Target: not null } ? c : null;
        }
        catch (JsonException) { return null; }
    }

    async Task ForgetAsync(RelayClient relay, string? blob)
    {
        if (blob is null) { return; }
        try { await relay.BlobDeleteAsync(blob); }
        catch (Exception e) when (e is not OperationCanceledException) { log($"could not delete a session bundle from the blob store: {e.GetType().Name}"); }
    }

    // ---- the source ----

    public async Task<TransferResult> ServeAsync(SecureChannel channel, JsonElement data)
    {
        if (Launcher is not { } launcher) { throw new InvalidOperationException("this machine does not hand its sessions to other machines"); }
        if (launcher.ServeUnavailable is { } off) { throw new InvalidOperationException(off); }
        var l = Volatile.Read(ref link) ?? throw new InvalidOperationException("this machine is not connected to the relay now");
        if (l.Relay.BlobMaxBytes <= 0) { throw new InvalidOperationException("the relay has no blob store, which a transfer goes through"); }
        if (data.ValueKind != JsonValueKind.Object) { throw new ArgumentException("session.transfer needs a command"); }
        var request = data.Deserialize<TransferRequest>(ProtocolJson.Options);
        if (request?.Command is null) { throw new ArgumentException("session.transfer needs a command"); }

        var (c, problem) = SignedCommands.Verify(key, request.Command, TransferProtocol.Action, time.GetUtcNow());
        if (c is null)
        {
            log($"transfer refused from {channel.PeerId[..8]}: {problem}");
            throw new InvalidOperationException(problem);
        }
        // Only the machine the person chose may receive it: the one asking.
        if (c.To != channel.PeerId) { throw new InvalidOperationException("the command is for another machine to receive the session"); }
        if (c.Target.Session is not { } id || !Guid.TryParseExact(id, "D", out _)) { throw new InvalidOperationException("the command names no session"); }
        if (!memory.Take(c)) { throw new InvalidOperationException("that command has been carried out already; press again"); }

        var (entry, title, refused) = await launcher.TransferableAsync(id);
        if (entry is null)
        {
            log($"transfer of {id[..8]} to {channel.PeerId[..8]} refused: {refused}");
            throw new InvalidOperationException(refused);
        }
        var config = launcher.TransferConfig;
        var most = Math.Min(config.MaxBytes, BlobFormat.MaxPlain(l.Relay.BlobMaxBytes));
        using var deadline = new CancellationTokenSource(SendFor, time);
        if (!await sending.WaitAsync(TimeSpan.FromSeconds(30))) { throw new InvalidOperationException("this machine is sending other sessions; try again in a minute"); }
        string? zip = null;
        // Everything after the slot is taken is inside, so whatever fails gives it back: two
        // failures holding one each would stop every transfer from this machine.
        try
        {
            try { zip = Path.Combine(Folder(), $"{Guid.NewGuid():N}.zip"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException($"this machine's transfer folder could not be prepared ({e.Message})");
            }
            // Checked again: resumed at the machine since the list was made, it is being written.
            if (launcher.Running(entry.Id)) { throw new InvalidOperationException("that session is running on its machine; it can be brought over once it has ended there"); }
            SessionBundles.Packed packed;
            try { packed = await Task.Run(() => SessionBundles.Pack(entry.Agent, launcher.Homes, entry.Id, zip, config.MaxSourceBytes), deadline.Token); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new InvalidOperationException($"that session could not be read ({e.Message})"); }
            if (packed.Bytes > most)
            {
                throw new InvalidOperationException($"that session is {TransferLimits.Mib(packed.Bytes)} compressed, over the {TransferLimits.Mib(most)} a transfer carries; /compact it first, or raise launch.transfer.maxBytes on both machines");
            }
            var sent = await BlobTransfer.SendFileAsync(l.Relay, http, channel.PeerId, zip, deadline.Token);
            log($"sent session {entry.Id[..8]} to {channel.PeerId[..8]} for {c.Principal.Device[..8]}: {TransferLimits.Mib(packed.Bytes)}, {packed.Files} file(s)");
            return new TransferResult
            {
                Blob = new BlobRef { Id = sent.Blob, Key = BlobFormat.Base64Url(sent.Key) },
                Size = sent.Size,
                Sha256 = BlobFormat.Base64Url(sent.Sha256),
                Agent = entry.Agent,
                Launcher = entry.Launcher,
                Title = title ?? entry.Summary,
                Files = packed.Files,
            };
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            log($"transfer of {entry.Id[..8]} to {channel.PeerId[..8]} stopped: not sent within {SendFor}");
            throw new InvalidOperationException("the bundle could not be uploaded in time; try again");
        }
        catch (BlobRefusedException e) { throw new InvalidOperationException($"the relay would not take the bundle ({e.Reason})"); }
        catch (HttpRequestException e) { throw new InvalidOperationException($"the bundle could not be uploaded ({e.Message})"); }
        finally
        {
            Delete(zip);
            sending.Release();
        }
    }

    static void Delete(string? path)
    {
        if (path is null) { return; }
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        http.Dispose();
        sending.Dispose();
    }
}
