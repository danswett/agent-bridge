using System.Security.Cryptography;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host.Fleet.Grants;
using Xunit;

public sealed class GrantReadProducerTests : IDisposable
{
    readonly ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    readonly FleetGrantProofContext context = new("account", "default", new string('a', 32), "grant.read", Guid.NewGuid());
    int reads, signed, checks;
    byte[] Point { get { var q = key.ExportParameters(false).Q; return [4, .. q.X!, .. q.Y!]; } }
    IEnvelopeSigner Signer => new Counting(key, () => signed++);
    public void Dispose() => key.Dispose();
    sealed class Counting(ECDsa key, Action onSign) : IEnvelopeSigner
    {
        public byte[] Sign(ReadOnlySpan<byte> message) { onSign(); return key.SignData(message, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence); }
    }
    GrantReadProducer Producer(GrantStoreProblem problem, bool ready = true, bool signer = true, bool throws = false, Func<bool>? evidence = null) =>
        new(() => { reads++; return throws ? throw new IOException("C:\\secret") : new GrantSnapshotResult(problem, 0, null, [], []); },
            () => ready, () => signer ? Signer : null, evidence ?? (() => { checks++; return false; }));
    FleetGrantReadProblem Sealed(GrantReadProducer producer) => FleetGrantReadProof.Verify(producer.Produce(context), Point, context);

    [Theory]
    [InlineData(GrantStoreProblem.None, FleetGrantReadProblem.None)]
    [InlineData(GrantStoreProblem.RotationRequired, FleetGrantReadProblem.Unavailable)]
    [InlineData(GrantStoreProblem.BadMac, FleetGrantReadProblem.Other)]
    [InlineData(GrantStoreProblem.AuditIncomplete, FleetGrantReadProblem.Other)]
    [InlineData(GrantStoreProblem.Unreadable, FleetGrantReadProblem.Other)]
    public void The_sealed_problem_is_what_the_real_store_snapshot_said(GrantStoreProblem store, FleetGrantReadProblem expected) =>
        Assert.Equal(expected, Sealed(Producer(store)));

    [Theory]
    [InlineData("proven", FleetGrantReadProblem.RotationRequired)]
    [InlineData("unproven", FleetGrantReadProblem.Unavailable)]
    [InlineData("cannot-check", FleetGrantReadProblem.Unavailable)]
    public void Rotation_required_is_sealed_only_when_independent_evidence_proves_never_issued(string evidence, FleetGrantReadProblem expected) =>
        Assert.Equal(expected, Sealed(Producer(GrantStoreProblem.RotationRequired, evidence: evidence switch
        {
            "proven" => () => true, "unproven" => () => false, _ => () => throw new IOException("secret")
        })));

    [Fact]
    public void The_evidence_is_asked_only_when_the_store_says_rotation_required()
    {
        Assert.Equal(FleetGrantReadProblem.None, Sealed(Producer(GrantStoreProblem.None)));
        Assert.Equal(FleetGrantReadProblem.Other, Sealed(Producer(GrantStoreProblem.BadMac)));
        Assert.Equal(0, checks);
    }

    [Fact]
    public void A_receiver_that_is_not_ready_seals_unavailable_without_reading_the_store()
    {
        Assert.Equal(FleetGrantReadProblem.Unavailable, Sealed(Producer(GrantStoreProblem.RotationRequired, ready: false)));
        Assert.Equal(0, reads);
    }

    [Fact]
    public void A_store_read_that_throws_is_never_sealed_as_none_or_rotation_required() =>
        Assert.Equal(FleetGrantReadProblem.Other, Sealed(Producer(GrantStoreProblem.None, throws: true)));

    [Fact]
    public void Readiness_that_ends_during_the_store_read_seals_unavailable()
    {
        var calls = 0;
        var producer = new GrantReadProducer(() => new GrantSnapshotResult(GrantStoreProblem.RotationRequired, 0, null, [], []), () => ++calls < 2, () => Signer, () => true);
        Assert.Equal(FleetGrantReadProblem.Unavailable, Sealed(producer));
    }

    [Fact]
    public void A_none_without_a_real_grants_array_is_other() => Assert.Equal(FleetGrantReadProblem.Other,
        Sealed(new GrantReadProducer(() => new GrantSnapshotResult(GrantStoreProblem.None, 0, null, default, []), () => true, () => Signer, () => true)));

    [Fact]
    public void Without_a_signer_nothing_is_answered_at_all()
    {
        Assert.Throws<InvalidOperationException>(() => Producer(GrantStoreProblem.None, signer: false).Produce(context));
        Assert.Equal(0, signed);
    }

    [Fact]
    public void Only_a_grant_read_context_is_sealed()
    {
        var other = context with { Action = "grant.binding" };
        Assert.Throws<InvalidDataException>(() => Producer(GrantStoreProblem.None).Produce(other));
        Assert.Equal(0, signed);
    }
}
