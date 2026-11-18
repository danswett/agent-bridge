using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Roster;

namespace AgentBridge.Host;

internal interface IHostEnrollmentKey : IDisposable
{
    byte[] PublicPoint { get; }
    byte[] Sign(byte[] message);
}

internal sealed class HostEnrollment(HostRosterReader reader, string account, string generation,
    Func<string, IHostEnrollmentKey>? openKey = null)
{
    DeviceDescriptor? own;
    readonly object sync = new();

    public (string? Descriptor, string? Refusal) Describe(string profile, byte[] dh)
    {
        lock (sync)
        {
            var refusal = reader.Enrollment<string?>(account, generation, _ => null, code => code);
            if (refusal is not null) { return (null, refusal); }
            // Per instance only; cross-instance/process single-flight belongs to host wiring.
            if (own is not null) { return (null, "attempt-already-open"); }
            if (!OperatingSystem.IsWindows()) { return (null, "missing-key"); }
            dh = dh.ToArray();
            if (!P256.IsPoint(dh)) { return (null, "invalid-descriptor"); }
            try
            {
                DeviceDescriptor descriptor;
                using (var key = openKey is null ? HostSignKey.OpenExisting(profile) : openKey(profile))
                {
                    var point = key.PublicPoint;
                    if (point.AsSpan().SequenceEqual(dh)) { return (null, "invalid-descriptor"); }
                    var proof = key.Sign(HostKey.Binding(account, generation, dh));
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
                return reader.Enrollment<(string? Descriptor, string? Refusal)>(account, generation,
                    _ => { own = descriptor; return (json, null); }, code => (null, code));
            }
            catch (Exception e) when (e is InvalidDataException or CryptographicException or IOException
                or UnauthorizedAccessException or ArgumentException or InvalidOperationException
                or PlatformNotSupportedException or System.Security.SecurityException)
            { return (null, "missing-key"); }
        }
    }

    public string Status(EnrollmentJoiner? exchange, string approverId) =>
        reader.Enrollment(account, generation, verifier =>
            own is null ? "not-enrolled" :
            exchange is not null && verifier.State.ActiveAdd(own.Id) is { } add &&
            verifier.Graph[add].Body.Version == 2 && exchange.Accepts(verifier.Graph[add].Entry, own, approverId)
                ? "enrolled" : "pending", _ => own is null ? "not-enrolled" : "pending");
}

public sealed partial class HostRosterReader
{
    internal T Enrollment<T>(string account, string generation, Func<RosterVerifier, T> ready, Func<string, T> refused)
    {
        lock (sync)
        {
            if (problem is not null || pin is null) { return refused("not-pinned"); }
            if (pin.Account != account || Verified is { } a && a.Account != account) { return refused("account"); }
            if (pin.Generation != generation || Verified is { } g && g.Generation != generation) { return refused("generation"); }
            if (Verified is not { AcceptHostSign: true } verifier) { return refused("no-switch"); }
            return ready(verifier);
        }
    }
}
