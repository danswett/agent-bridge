using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Roster;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// Dormant foundation: the enrollment writer and live observe integration are separate slices.
// The disk pin is read once, never created or advanced by a relay snapshot.
public sealed partial class HostRosterReader
{
    sealed record Pin(string Account, string Generation, byte[] Genesis, byte[][] Heads);
    readonly Pin? pin;
    readonly string? problem;
    readonly Func<TimeSpan> clock;
    readonly object sync = new();

    public HostRosterReader(string pinPath, Func<TimeSpan> clock)
    {
        (pin, problem) = ReadPin(pinPath);
        this.pinPath = pin is null ? pinPath : Path.GetFullPath(pinPath);
        this.clock = clock;
    }

    public RosterVerifier? Verified { get; private set; }

    public RosterCheck Update(RelayRoster? relay, string account)
    {
        lock (sync) { return UpdateCore(relay, account); }
    }

    RosterCheck UpdateCore(RelayRoster? relay, string account)
    {
        RosterCheck Failure(string code) =>
            new(code, relay?.Mode ?? "", relay?.Entries.Count ?? 0, 0, 0, 0, 0, 0, [], [code]);
        if (problem is not null) { return Failure(problem); }
        if (pin is null) { return relay is null ? Failure("unpinned") : HostRoster.Verify(relay, account, clock); }
        if (relay is null || relay.Entries.Count == 0) { return Failure("no-roster"); }
        if (account != pin.Account || relay.Generation != pin.Generation) { return Failure("pin-mismatch"); }

        // Validate independently before offering anything to the retained, probation-bearing reader.
        var (check, candidate) = HostRoster.Read(relay, account, clock, new RosterVerifier(account, pin.Generation, clock));
        if (candidate is null || candidate.Graph.Count == 0 || !candidate.Graph[0].Hash.AsSpan().SequenceEqual(pin.Genesis))
        {
            return Failure("pin-mismatch");
        }
        if (pin.Heads.Concat(Verified?.Heads ?? []).Any(h => !Descends(candidate, h))) { return Failure("head-regress"); }
        if (Verified is not null) { return HostRoster.Read(relay, account, clock, Verified).Check; }
        Verified = candidate;
        return check;
    }

    static bool Descends(RosterVerifier verifier, byte[] head, IReadOnlyList<byte[]>? frontier = null) =>
        verifier.Holds(head) && verifier.Graph.Find(head) is { } before &&
        (frontier ?? verifier.Heads).Any(h => verifier.Holds(h) && verifier.Graph.Find(h) is { } after &&
            (before == after || verifier.Graph.IsAncestor(before, after)));

    static (T? Value, string? Problem) ReadPrivate<T>(string path, int limit, Func<byte[], T> parse) where T : class
    {
        try
        {
            var full = Path.GetFullPath(path);
            var attributes = File.GetAttributes(full);
            var parent = Path.GetDirectoryName(full)!;
            if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
                new DirectoryInfo(parent).Attributes.HasFlag(FileAttributes.ReparsePoint) || !PrivateFolder.IsPrivate(parent))
            {
                throw new InvalidDataException();
            }
            if (OperatingSystem.IsWindows())
            {
                var acl = new FileInfo(full).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
                using var identity = WindowsIdentity.GetCurrent();
                var me = identity.User!;
                var owner = acl.GetOwner(typeof(SecurityIdentifier));
                // Elevated tokens can give new files their default owner group rather than User.
                if (!me.Equals(owner) && identity.Owner?.Equals(owner) != true)
                {
                    throw new InvalidDataException();
                }
                foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                {
                    if (!me.Equals(rule.IdentityReference)) { throw new InvalidDataException(); }
                }
            }
            else if (!PrivateFolder.IsPrivate(full)) { throw new InvalidDataException(); }
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            var bytes = new byte[limit + 1];
            var count = stream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (count > limit) { throw new InvalidDataException(); }
            return (parse(bytes[..count]), null);
        }
        catch (FileNotFoundException) { return (null, null); }
        catch (DirectoryNotFoundException) { return (null, null); }
        catch (InvalidDataException) { return (null, "pin-unreadable"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException
            or ArgumentException or FormatException or RosterFormatException or System.Security.SecurityException)
        {
            return (null, "pin-unreadable");
        }
    }

    static (Pin? Pin, string? Problem) ReadPin(string path) =>
        ReadPrivate(path, 4096, bytes =>
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { throw new InvalidDataException(); }
            var names = root.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Length != 4 || !names.ToHashSet(StringComparer.Ordinal).SetEquals(["account", "generation", "genesis", "heads"]))
            {
                throw new InvalidDataException();
            }
            string Text(string name) => root.GetProperty(name).GetString() ?? throw new InvalidDataException();
            var account = Text("account");
            var generation = Text("generation");
            if (Encoding.UTF8.GetByteCount(account) is < 1 or > 128 || account.Any(char.IsControl) || !Codec.IsId(generation))
            {
                throw new InvalidDataException();
            }
            var genesis = Codec.FromBase64Url(Text("genesis"), 32, "genesis");
            var heads = root.GetProperty("heads").EnumerateArray()
                .Select(h => Codec.FromBase64Url(h.GetString() ?? "", 32, "head")).ToArray();
            if (heads.Length is < 1 or > 4 || heads.Zip(heads.Skip(1), (a, b) => Ids.HeadOrder.Compare(a, b) >= 0).Any(b => b))
            {
                throw new InvalidDataException();
            }
            return new Pin(account, generation, genesis, heads);
        });
}
