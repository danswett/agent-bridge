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
    Func<string, IHostEnrollmentKey>? openKey = null, string name = "host", TimeProvider? time = null,
    Func<string, FileStream>? createLog = null, Action<string, string>? moveLog = null) : IDisposable
{
    sealed record Attempt(DeviceDescriptor Own, IDisposable Held, ITimer Expiry, EnrollmentLockout Lockout, DateTimeOffset Expires, long Started, string Log)
    {
        public EnrollmentJoiner? Joiner; public byte[]? H, Genesis; public string? Approver, PeerName; public bool Matched;
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
                var started = Clock.GetTimestamp();
                charged = [.. marks.Where(m => now - DateTimeOffset.FromUnixTimeSeconds(m) < EnrollmentLockout.Window + EnrollmentLockout.LockFor)];
                // §4.4, brief 198: only the person's wrong code 2 typed here is charged (never opening, binding, abandoning, expiry
                // or a peer's message); replayed so a restart cannot reset it, kept for hour + lock (a third mark's lock outlasts the first's hour).
                DateTimeOffset? replay = null;
                var lockout = new EnrollmentLockout(EnrollmentLockout.Counts.WrongEntries, () => replay ?? Clock.GetUtcNow());
                foreach (var mark in charged) { replay = DateTimeOffset.FromUnixTimeSeconds(mark); lockout.RecordWrong("replay"); }
                replay = null;
                if (lockout.IsBlocked("attempt")) { return (null, "locked"); }
                var (descriptor, json, refused) = v2 && OperatingSystem.IsWindows() ? HostSigned(profile, dh) : V1(dh);
                if (descriptor is null) { return (null, refused); }
                Attempt? attempt = null;
                var expiry = Clock.CreateTimer(_ => End(attempt!), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                (open, held) = (attempt = new Attempt(descriptor, held, expiry, lockout, now + EnrollmentLockout.AttemptLifetime, started, log), null);
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

    bool Expired(Attempt attempt) => Clock.GetElapsedTime(attempt.Started) >= EnrollmentLockout.AttemptLifetime;

    DateTimeOffset AttemptNow(Attempt attempt) =>
        attempt.Expires - EnrollmentLockout.AttemptLifetime + Clock.GetElapsedTime(attempt.Started);

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

    // The approver's steps (§4.2), over its Noise channel: only from a device the relay asserts is a person's, only into
    // an attempt the person opened here, and only from the channel (handshake hash h, authenticated static key) whose commit
    // bound it. A peer can end its attempt but never charge the lockout. The full 32-byte genesis is taken only after code 2 matched.
    public (byte[]? Value, string? Refusal) Exchange(byte[] h, byte[] peerStatic, PresenceEntry? asserted, string step, byte[] value)
    {
        lock (sync) { return Admit(peerStatic, asserted, step) is { } refusal ? (null, refusal) : Step(h, peerStatic, asserted, step, value); }
    }

    // Admission, before any wire value is decoded: a commit from a peer inside its window is throttled, and every commit that
    // gets this far (malformed or oversized included) starts that window, so a peer cannot probe the decoder at will.
    internal string? Admit(byte[] peerStatic, PresenceEntry? asserted, string step)
    {
        lock (sync)
        {
            var approver = Ids.Device(peerStatic);
            if (asserted is null || asserted.Id != approver || !PersonKinds.Contains(asserted.Kind)) { return "not-person"; }
            if (open is null) { return "no-attempt"; }
            if (step != "commit") { return null; }
            var now = Clock.GetUtcNow();
            if (commits.TryGetValue(approver, out var last) && now - last < CommitEvery) { return "throttled"; }
            foreach (var stale in commits.Where(c => now - c.Value >= CommitEvery).Select(c => c.Key).ToList()) { commits.Remove(stale); }
            commits[approver] = now;
            return null;
        }
    }

    // The step itself, for a peer Admit let through; the attempt may have ended since, so it is read again here.
    internal (byte[]? Value, string? Refusal) Step(byte[] h, byte[] peerStatic, PresenceEntry? asserted, string step, byte[] value)
    {
        lock (sync)
        {
            var approver = Ids.Device(peerStatic);
            if (asserted is null) { return (null, "not-person"); }
            if (open is not { } attempt) { return (null, "no-attempt"); }
            if (Expired(attempt)) { End(attempt); return (null, "expired"); }
            if (step == "commit")
            {
                if (attempt.Joiner is not null) { return (null, "attempt-bound"); }
                if (value.Length != 0) { return (null, "invalid-value"); }
                (attempt.H, attempt.Approver, attempt.PeerName) = (h.ToArray(), approver, HostConfigFile.CleanName(asserted.Name));
                attempt.Joiner = new EnrollmentJoiner(account, attempt.H, attempt.Lockout, () => AttemptNow(attempt), expires: attempt.Expires);
                return attempt.Joiner.Commit() is { } commit ? (commit, null) : (null, "locked");
            }
            if (attempt.Joiner is null || !CryptographicOperations.FixedTimeEquals(attempt.H, h) || attempt.Approver != approver) { return (null, "attempt-bound"); }
            if (step == "abandon") { End(attempt); return ([], null); }
            if (step == "offer" && attempt.Joiner.OnOffer(value) is { } reveal) { return (reveal, null); }
            if (step == "offer") { End(attempt); return (null, "attempt-ended"); } // a failed offer ends it, charging nothing
            if (step != "genesis") { return (null, "unknown-step"); }
            if (!attempt.Matched) { return (null, "not-matched"); }
            if (value.Length != 32 || attempt.Genesis is not null) { return (null, "invalid-genesis"); }
            attempt.Genesis = value.ToArray();
            return ([], null);
        }
    }

    // Code 2 as the person typed it here: the only thing charged (persisted before the answer). Three wrong end the attempt; three an hour lock.
    public string EnterCode2(string typed)
    {
        lock (sync)
        {
            if (open is not { Joiner: { } joiner } attempt) { return "no-exchange"; }
            if (Expired(attempt)) { End(attempt); return "ended"; }
            var live = joiner.Code1Shown is not null;
            var result = joiner.EnterCode2(typed);
            attempt.Matched |= result == CodeResult.Match;
            string? stored = null;
            if ((result == CodeResult.Wrong || result == CodeResult.Ended && live) && !Expired(attempt)) { stored = Charge(attempt.Log); }
            if (result == CodeResult.Ended || stored is not null) { End(attempt); }
            return stored ?? result.ToString().ToLowerInvariant();
        }
    }

    string? Charge(string log)
    {
        charged.Add(Clock.GetUtcNow().ToUnixTimeSeconds());
        var temporary = log + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = (createLog ?? PrivateFolder.CreateNew)(temporary))
            {
                JsonSerializer.Serialize(stream, charged);
                // Acknowledged marks are the lockout: on disk before the rename publishes them, as the repo's other durable writers do.
                stream.Flush(flushToDisk: true);
            }
            (moveLog ?? ((from, to) => File.Move(from, to, overwrite: true)))(temporary, log);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            try { File.Delete(temporary); } catch (Exception f) when (f is IOException or UnauthorizedAccessException) { }
            return "attempt-store";
        }
    }

    // Ruling (b): the host verifies the relay's chain itself and pins only when its root is the genesis sent on the
    // matched channel and it holds this host's exact add by the matched approver (an active person device, not on
    // probation or held), carrying this exchange's transcript. Only the 182 writer pins. The attempt and account are
    // captured before the roster read and checked again with the write, under sync; Program.SignedIn ends the attempt first.
    public static async Task<string> FinishAsync(HostEnrollment? joining, Func<Task<RelayRoster?>>? roster, string pinPath,
        Func<TimeSpan> clock, Func<string?> currentAccount, Func<byte[], bool>? isCapSignGenesis = null)
    {
        if (joining is null) { return "signed-out"; }
        var attempt = Volatile.Read(ref joining.open);
        RelayRoster? relay;
        try { relay = await (roster ?? throw new InvalidOperationException())(); }
        catch (Exception e) when (e is not OutOfMemoryException) { return "no-roster"; }
        return relay is null ? "no-roster" : joining.Finish(attempt, relay, pinPath, clock, currentAccount, isCapSignGenesis);
    }

    internal string Finish(RelayRoster relay, string pinPath, Func<TimeSpan> clock, Func<byte[], bool>? isCapSignGenesis = null) =>
        Finish(Volatile.Read(ref open), relay, pinPath, clock, () => account, isCapSignGenesis);

    string Finish(Attempt? expected, RelayRoster relay, string pinPath, Func<TimeSpan> clock, Func<string?> currentAccount, Func<byte[], bool>? isCapSignGenesis)
    {
        lock (sync)
        {
            if (open is not { } attempt || !ReferenceEquals(attempt, expected)) { return "no-attempt"; }
            if (currentAccount() != account) { return "account-changed"; }
            if (Expired(attempt)) { End(attempt); return "expired"; }
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
            var history = reader.Enrolling<string?>(account, generationId, earlier =>
            {
                if (earlier is null) { return null; }
                lock (earlier)
                {
                    if (earlier.Graph.Count == 0 || !CryptographicOperations.FixedTimeEquals(earlier.Graph[0].Hash, genesis)) { return "genesis-mismatch"; }
                    for (var i = 0; i < verifier.Graph.Count; i++)
                    {
                        var node = verifier.Graph[i];
                        if (earlier.FirstAccepted(node.Hash) is { } at) { verifier.AdoptFirstAccepted(node.Hash, at); }
                    }
                }
                return null;
            }, code => code);
            if (history is not null) { return history; }
            if (!verifier.State.PersonDevices.Contains(attempt.Approver) || verifier.State.ActiveAdd(attempt.Own.Id) is not { } add
                || !joiner.Accepts(verifier.Graph[add].Entry, attempt.Own, attempt.Approver!)) { return "not-added"; }
            if (verifier.State.ActiveAdd(attempt.Approver!) is not { } by || verifier.OnProbation(by) || verifier.Held.Contains(attempt.Approver!)) { return "approver-probation"; }
            var written = HostRosterPinWriter.Write(pinPath, new PersonGenesis("enrollment", genesis), relay, account, clock, isCapSignGenesis);
            if (written == "pinned") { reader.Update(relay, account); }
            return written;
        }
    }

    // The remote steps' wire form (ops enroll.commit, enroll.offer, enroll.genesis, enroll.abandon; value
    // base64url); asserted is the relay's presence entry for the channel's peer.
    public const string StepPrefix = "enroll.";
    // A legitimate value is a 32-byte nonce or genesis, at most 43 base64url characters; anything above this is refused unread.
    internal const int MaxValueChars = 256;
    public static object Remote(HostEnrollment? enrollment, byte[] h, byte[]? peerStatic, PresenceEntry? asserted, string op, JsonElement data)
    {
        if (enrollment is null || peerStatic is null) { return new { value = (string?)null, refusal = "no-attempt" }; }
        var step = op[StepPrefix.Length..];
        if (enrollment.Admit(peerStatic, asserted, step) is { } refused) { return new { value = (string?)null, refusal = refused }; }
        byte[] value = [];
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("value", out var v) && v.ValueKind != JsonValueKind.Null)
        {
            // The raw token (quotes included) is measured first, so an oversized value is neither copied nor decoded.
            if (v.ValueKind != JsonValueKind.String || System.Runtime.InteropServices.JsonMarshal.GetRawUtf8Value(v).Length > MaxValueChars + 2)
            { return new { value = (string?)null, refusal = "invalid-value" }; }
            try { value = Codec.FromBase64Url(v.GetString() ?? "", "value"); }
            catch (RosterFormatException) { return new { value = (string?)null, refusal = "invalid-value" }; }
        }
        var (reply, refusal) = enrollment.Step(h, peerStatic, asserted, step, value);
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
