using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Roster;
using AgentBridge.Transport;

namespace AgentBridge.Host;

internal interface IHostEnrollmentKey : IDisposable
{
    byte[] PublicPoint { get; }
    byte[] Sign(byte[] message);
}

// Approve-gesture enrollment, host side (#3450, brief 184). Host-sign v2 stays off, so the host
// describes itself as a v1 host (id dh kind platform name), pinned or not; a v2 descriptor is made
// only while a verified pin's AcceptHostSign switch is on. One attempt is open per profile: a lock
// file beside the pin, under HostRosterPinWriter's private roster folder, holds it across instances
// and processes until the exchange's lifetime ends. Opened only through the app's local pipe; the
// approver's exchange steps then reach it over the approver's Noise channel (brief 184b).
internal sealed class HostEnrollment(HostRosterReader reader, string account, string? generation,
    Func<string, IHostEnrollmentKey>? openKey = null, string name = "host", TimeProvider? time = null) : IDisposable
{
    sealed record Attempt(DeviceDescriptor Own, IDisposable Held, ITimer Expiry, EnrollmentLockout Lockout, DateTimeOffset Expires, string Log)
    {
        public EnrollmentJoiner? Joiner;
        public byte[]? H;
        public string? Approver;
        public string? PeerName;
        public bool Matched;
        public byte[]? Genesis;
    }
    Attempt? open;
    readonly object sync = new();
    TimeProvider Clock => time ?? TimeProvider.System;
    // The relay's person kinds (RelayHub.PersonKinds); a host, test or agent principal never reaches an attempt.
    static readonly HashSet<string> PersonKinds = ["app", "pwa", "phone"];
    public static readonly TimeSpan CommitEvery = TimeSpan.FromSeconds(10);
    readonly Dictionary<string, DateTimeOffset> commits = new(StringComparer.Ordinal);
    List<long> charged = [];
    internal IReadOnlyList<long> Charged { get { lock (sync) { return [.. charged]; } } }

    public string Account => account;

    // Ends the open attempt, if any, and frees the profile's attempt lock.
    public void Dispose() { if (Volatile.Read(ref open) is { } attempt) { End(attempt); } }

    public (string? Descriptor, string? Refusal) Describe(string profile, byte[] dh)
    {
        lock (sync)
        {
            var (refusal, v2) = reader.Enrolling<(string?, bool)>(account, generation, v => (null, v is { AcceptHostSign: true }), code => (code, false));
            if (refusal is not null) { return (null, refusal); }
            if (open is not null) { return (null, "attempt-already-open"); }
            if (v2 && (!OperatingSystem.IsWindows() || generation is null)) { return (null, generation is null ? "generation" : "missing-key"); }
            dh = dh.ToArray();
            if (!P256.IsPoint(dh)) { return (null, "invalid-descriptor"); }
            var folder = Path.GetDirectoryName(HostRosterPinWriter.PinPath(profile))!;
            IDisposable? held;
            try { PrivateFolder.Ensure(folder); held = HostLock.TryAcquire(folder, TimeSpan.Zero, "enrollment.lock"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { return (null, "attempt-store"); }
            if (held is null) { return (null, "attempt-already-open"); }
            try
            {
                var log = Path.Combine(folder, "enrollment-attempts.json");
                long[] marks;
                try { marks = File.Exists(log) ? JsonSerializer.Deserialize<long[]>(File.ReadAllBytes(log)) ?? throw new InvalidDataException() : []; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { return (null, "attempt-store"); }
                var now = Clock.GetUtcNow();
                charged = [.. marks.Where(m => now - DateTimeOffset.FromUnixTimeSeconds(m) < EnrollmentLockout.Window + EnrollmentLockout.LockFor)];
                // §4.4, brief 198: only the person's wrong code 2 typed here is charged (never opening, binding,
                // abandoning, expiry or a peer's message); replayed so a restart cannot reset the lockout, and
                // kept for hour + lock since a third mark's lock outlasts the first mark's hour.
                DateTimeOffset? replay = null;
                var lockout = new EnrollmentLockout(EnrollmentLockout.Counts.WrongEntries, () => replay ?? Clock.GetUtcNow());
                foreach (var mark in charged) { replay = DateTimeOffset.FromUnixTimeSeconds(mark); lockout.RecordWrong("replay"); }
                replay = null;
                if (lockout.IsBlocked("attempt")) { return (null, "locked"); }
                var (descriptor, json, refused) = v2 && OperatingSystem.IsWindows() ? HostSigned(profile, dh) : V1(dh);
                if (descriptor is null) { return (null, refused); }
                Attempt? attempt = null;
                var expiry = Clock.CreateTimer(_ => End(attempt!), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                (open, held) = (attempt = new Attempt(descriptor, held, expiry, lockout, now + EnrollmentLockout.AttemptLifetime, log), null);
                expiry.Change(EnrollmentLockout.AttemptLifetime, Timeout.InfiniteTimeSpan);
                return (json, null);
            }
            finally { held?.Dispose(); }
        }
    }

    void End(Attempt attempt)
    {
        lock (sync) { if (ReferenceEquals(open, attempt)) { open = null; } }
        attempt.Held.Dispose();
        attempt.Expiry.Dispose();
    }

    (DeviceDescriptor?, string?, string?) V1(byte[] dh)
    {
        var shown = name.Length is > 0 and <= 64 && !name.Any(char.IsControl) ? name : "host";
        var platform = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        var descriptor = new DeviceDescriptor(Ids.Device(dh), dh, null, null, null, null, RosterKinds.Host, platform, shown);
        return (descriptor, JsonSerializer.Serialize(new { id = descriptor.Id, dh = Codec.Base64Url(dh), kind = descriptor.Kind, platform, name = shown }), null);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    (DeviceDescriptor?, string?, string?) HostSigned(string profile, byte[] dh)
    {
        try
        {
            DeviceDescriptor descriptor;
            using (var key = openKey is null ? HostSignKey.OpenExisting(profile) : openKey(profile))
            {
                var point = key.PublicPoint;
                if (point.AsSpan().SequenceEqual(dh)) { return (null, null, "invalid-descriptor"); }
                var proof = key.Sign(HostKey.Binding(account, generation!, dh));
                descriptor = new(Ids.Device(dh), dh, null, null, null, null, RosterKinds.Host, "windows", "host",
                    point, EntryBody.SignAlgorithm, proof);
            }
            var json = JsonSerializer.Serialize(new
            {
                id = descriptor.Id, dh = Codec.Base64Url(dh), kind = descriptor.Kind, platform = descriptor.Platform, name = descriptor.Name,
                hostSign = Codec.Base64Url(descriptor.HostSign!), hostSignAlg = descriptor.HostSignAlg,
                hostSignProof = Codec.Base64Url(descriptor.HostSignProof!),
            });
            // Custody can block; publish only after rechecking the current reader state.
            return reader.Enrollment<(DeviceDescriptor?, string?, string?)>(account, generation,
                _ => (descriptor, json, null), code => (null, null, code));
        }
        catch (Exception e) when (e is InvalidDataException or CryptographicException or IOException
            or UnauthorizedAccessException or ArgumentException or InvalidOperationException
            or PlatformNotSupportedException or System.Security.SecurityException)
        { return (null, null, "missing-key"); }
    }

    // A4: the host is the joiner, so the person reads code 1, its expiry and the bound peer here.
    public (string? Code, DateTimeOffset? Expires, string? Peer) Shown { get { lock (sync) { return (open?.Joiner?.Code1Shown, open?.Expires, open?.PeerName); } } }

    // The approver's steps (§4.2), over its Noise channel: only from a device the relay asserts is a
    // person's, only into an attempt the person opened here, and only from the channel (handshake hash
    // h, authenticated static key) whose commit bound it. A peer can end its attempt but never charge
    // the lockout. The full 32-byte genesis is taken only after code 2 matched, so it is bound to that channel.
    public (byte[]? Value, string? Refusal) Exchange(byte[] h, byte[] peerStatic, PresenceEntry? asserted, string step, byte[] value)
    {
        lock (sync)
        {
            var approver = Ids.Device(peerStatic);
            if (asserted is null || asserted.Id != approver || !PersonKinds.Contains(asserted.Kind)) { return (null, "not-person"); }
            if (open is not { } attempt) { return (null, "no-attempt"); }
            if (step == "commit")
            {
                var now = Clock.GetUtcNow();
                if (commits.TryGetValue(approver, out var last) && now - last < CommitEvery) { return (null, "throttled"); }
                foreach (var stale in commits.Where(c => now - c.Value >= CommitEvery).Select(c => c.Key).ToList()) { commits.Remove(stale); }
                commits[approver] = now;
                if (attempt.Joiner is not null) { return (null, "attempt-bound"); }
                if (value.Length != 0) { return (null, "invalid-value"); }
                (attempt.H, attempt.Approver, attempt.PeerName) = (h.ToArray(), approver, HostConfigFile.CleanName(asserted.Name));
                attempt.Joiner = new EnrollmentJoiner(account, attempt.H, attempt.Lockout, Clock.GetUtcNow);
                return attempt.Joiner.Commit() is { } commit ? (commit, null) : (null, "locked");
            }
            if (attempt.Joiner is null || !CryptographicOperations.FixedTimeEquals(attempt.H, h) || attempt.Approver != approver) { return (null, "attempt-bound"); }
            if (step == "abandon") { End(attempt); return ([], null); }
            if (step == "offer")
            {
                if (attempt.Joiner.OnOffer(value) is { } reveal) { return (reveal, null); }
                End(attempt);
                return (null, "attempt-ended");
            }
            if (step != "genesis") { return (null, "unknown-step"); }
            if (!attempt.Matched) { return (null, "not-matched"); }
            if (value.Length != 32 || attempt.Genesis is not null) { return (null, "invalid-genesis"); }
            attempt.Genesis = value.ToArray();
            return ([], null);
        }
    }

    // Code 2 as the person typed it here; the only thing charged to the lockout (persisted before the
    // answer is given). Three wrong entries end the attempt; three in an hour lock joining.
    public string EnterCode2(string typed)
    {
        Attempt? ended = null;
        CodeResult result;
        string? stored = null;
        lock (sync)
        {
            if (open is not { Joiner: { } joiner } attempt) { return "no-exchange"; }
            var live = joiner.Code1Shown is not null && Clock.GetUtcNow() < attempt.Expires;
            result = joiner.EnterCode2(typed);
            attempt.Matched |= result == CodeResult.Match;
            if (result == CodeResult.Wrong || result == CodeResult.Ended && live) { stored = Charge(attempt.Log); }
            if (result == CodeResult.Ended || stored is not null) { ended = attempt; }
        }
        if (ended is not null) { End(ended); }
        return stored ?? result.ToString().ToLowerInvariant();
    }

    string? Charge(string log)
    {
        charged.Add(Clock.GetUtcNow().ToUnixTimeSeconds());
        var temporary = log + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = PrivateFolder.CreateNew(temporary)) { JsonSerializer.Serialize(stream, charged); }
            File.Move(temporary, log, overwrite: true);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch (Exception f) when (f is IOException or UnauthorizedAccessException) { }
            return "attempt-store";
        }
    }

    // Ruling (b): the host verifies the relay's chain itself and pins only when its root is the genesis
    // sent on the matched channel and it holds this exact host's add, by the approver the person
    // matched (an active person device), carrying this exchange's transcript. Only the 182 writer pins.
    public string Finish(RelayRoster relay, string pinPath, Func<TimeSpan> clock, Func<byte[], bool>? isCapSignGenesis = null)
    {
        lock (sync)
        {
            if (open is not { } attempt) { return "no-attempt"; }
            if (!attempt.Matched || attempt.Joiner is not { } joiner) { return "not-matched"; }
            if (attempt.Genesis is not { } genesis) { return "no-genesis"; }
            if (relay.Generation is not { } generationId || relay.Entries.Count == 0) { return "no-roster"; }
            var verifier = new RosterVerifier(account, generationId, clock);
            try
            {
                if (verifier.Offer(relay.Entries.Select(t => Entry.Parse(System.Text.Encoding.UTF8.GetBytes(t)))).Any(o => o.Verdict == Verdict.Refused)
                    || verifier.Graph.Count == 0) { return "chain-invalid"; }
            }
            catch (RosterFormatException) { return "chain-invalid"; }
            if (!CryptographicOperations.FixedTimeEquals(verifier.Graph[0].Hash, genesis)) { return "genesis-mismatch"; }
            if (!verifier.State.PersonDevices.Contains(attempt.Approver) || verifier.State.ActiveAdd(attempt.Own.Id) is not { } add
                || !joiner.Accepts(verifier.Graph[add].Entry, attempt.Own, attempt.Approver!)) { return "not-added"; }
            return HostRosterPinWriter.Write(pinPath, new PersonGenesis("enrollment", genesis), relay, account, clock, isCapSignGenesis);
        }
    }

    // The remote steps' wire form (ops enroll.commit, enroll.offer, enroll.genesis, enroll.abandon; value
    // base64url); asserted is the relay's presence entry for the channel's peer.
    public const string StepPrefix = "enroll.";
    public static object Remote(HostEnrollment? enrollment, byte[] h, byte[]? peerStatic, PresenceEntry? asserted, string op, JsonElement data)
    {
        byte[] value;
        try { value = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("value", out var v) ? Codec.FromBase64Url(v.GetString() ?? "", "value") : []; }
        catch (RosterFormatException) { return new { value = (string?)null, refusal = "invalid-value" }; }
        var (reply, refusal) = enrollment is null || peerStatic is null ? (null, "no-attempt") : enrollment.Exchange(h, peerStatic, asserted, op[StepPrefix.Length..], value);
        return new { value = reply is null ? null : Codec.Base64Url(reply), refusal };
    }

    public string Status() { var attempt = Volatile.Read(ref open); return Status(attempt?.Joiner, attempt?.Approver ?? ""); }

    // enrolled: the host's verified roster holds the exact active add of this attempt's descriptor
    // (v1, or v2 only while the switch is on) that the matched exchange accepts from that approver.
    public string Status(EnrollmentJoiner? exchange, string approverId)
    {
        var own = Volatile.Read(ref open)?.Own;
        return reader.Enrolling(account, generation, verifier =>
            own is null ? "not-enrolled" :
            exchange is not null && verifier?.State.ActiveAdd(own.Id) is { } add &&
            verifier.Graph[add].Body.Version == (own.HostSign is null ? 1 : 2) && (own.HostSign is null || verifier.AcceptHostSign) &&
            exchange.Accepts(verifier.Graph[add].Entry, own, approverId) ? "enrolled" : "pending", _ => own is null ? "not-enrolled" : "pending");
    }
}

public sealed partial class HostRosterReader
{
    // Record signing's gate (FleetTrust): a verified pin of this account and generation with the switch on.
    internal T Enrollment<T>(string account, string? generation, Func<RosterVerifier, T> ready, Func<string, T> refused)
    {
        lock (sync)
        {
            RefreshPin();
            if (problem is not null || pin is null) { return refused("not-pinned"); }
            if (pin.Account != account || Verified is { } a && a.Account != account) { return refused("account"); }
            if (pin.Generation != generation || Verified is { } g && g.Generation != generation) { return refused("generation"); }
            if (Verified is not { AcceptHostSign: true } verifier) { return refused("no-switch"); }
            return ready(verifier);
        }
    }

    // Enrollment's gate: no pin is needed (ready gets null), but a held pin must be readable and this
    // account's, and this generation's when one is named; ready gets its verified roster, if any yet.
    internal T Enrolling<T>(string account, string? generation, Func<RosterVerifier?, T> ready, Func<string, T> refused)
    {
        lock (sync)
        {
            RefreshPin();
            if (problem is not null) { return refused("pin-unreadable"); }
            if (pin is null) { return ready(null); }
            if (pin.Account != account || Verified is { } a && a.Account != account) { return refused("account"); }
            if (generation is not null && (pin.Generation != generation || Verified is { } g && g.Generation != generation)) { return refused("generation"); }
            return ready(Verified);
        }
    }
}
