using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Roster;

namespace AgentBridge.Host;

internal sealed class HostEnrollment(HostRosterReader reader, string account, string generation)
{
    DeviceDescriptor? own;

    public (string? Descriptor, string? Refusal) Describe(string profile, byte[] dh) =>
        reader.Enrollment<(string? Descriptor, string? Refusal)>(account, generation, _ =>
        {
            // Per instance only; cross-instance/process single-flight belongs to host wiring.
            if (own is not null) { return (null, "attempt-already-open"); }
            if (!OperatingSystem.IsWindows()) { return (null, "missing-key"); }
            if (!P256.IsPoint(dh)) { return (null, "invalid-descriptor"); }
            try
            {
                using var key = HostSignKey.OpenExisting(profile);
                var point = key.PublicPoint;
                if (point.AsSpan().SequenceEqual(dh)) { return (null, "invalid-descriptor"); }
                var proof = key.Sign(HostKey.Binding(account, generation, dh));
                own = new(Ids.Device(dh), dh.ToArray(), null, null, null, null, RosterKinds.Host, "windows", "host",
                    point, EntryBody.SignAlgorithm, proof);
                return (JsonSerializer.Serialize(new
                {
                    id = own.Id, dh = Codec.Base64Url(own.Dh), kind = own.Kind, platform = own.Platform, name = own.Name,
                    hostSign = Codec.Base64Url(point), hostSignAlg = own.HostSignAlg, hostSignProof = Codec.Base64Url(proof),
                }), null);
            }
            catch (Exception e) when (e is InvalidDataException or CryptographicException)
            { return (null, "missing-key"); }
        }, code => (null, code));

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
