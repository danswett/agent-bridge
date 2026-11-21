using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Roster;

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
// and processes until the exchange's lifetime ends. Reached only through the app's local pipe.
internal sealed class HostEnrollment(HostRosterReader reader, string account, string? generation,
    Func<string, IHostEnrollmentKey>? openKey = null, string name = "host", TimeProvider? time = null) : IDisposable
{
    sealed record Attempt(DeviceDescriptor Own, IDisposable Held, ITimer Expiry);
    Attempt? open;
    readonly object sync = new();

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
                var (descriptor, json, refused) = v2 && OperatingSystem.IsWindows() ? HostSigned(profile, dh) : V1(dh);
                if (descriptor is null) { return (null, refused); }
                Attempt? attempt = null;
                var expiry = (time ?? TimeProvider.System).CreateTimer(_ => End(attempt!), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                (open, held) = (attempt = new Attempt(descriptor, held, expiry), null);
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