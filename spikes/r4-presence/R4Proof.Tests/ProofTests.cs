using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using AgentBridge.Roster;
using R4Proof;
using Xunit;

// Everything but Windows Hello itself: the order of steps, how each outcome is judged, cleanup and
// exit codes, against a fake Hello and fake attacks.
public class ProofTests
{
    const string Rp = "r4-presence-proof.invalid";
    static readonly ECDsa Key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    static readonly BigInteger N = BigInteger.Parse("0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551", System.Globalization.NumberStyles.HexNumber);

    sealed class FakeHello(Action<FakeHello>? onBegin = null, bool synced = false, bool badAttestation = false) : IHello
    {
        public TaskCompletionSource<Call>? Pending;
        public int Cancels, Deletes;
        bool made;
        public Created Create(byte[] challenge)
        {
            made = true;
            return badAttestation ? throw new IndexOutOfRangeException("attestation parse") : new(0, [1, 2, 3], Point(), synced, true, "tpm");
        }
        public Call AssertWithPerson(byte[] challenge) => new(0, Signed(challenge));
        public (Task<Call>, Action) Begin(byte[] challenge, bool requireUv)
        {
            Pending = new();
            onBegin?.Invoke(this);
            return (Pending.Task, () => { Cancels++; Pending.TrySetResult(new(Hello.Cancelled, null)); });
        }
        public int? DeleteMade() { if (!made) { return null; } Deletes++; return 0; }
    }

    static byte[] Point() { var p = Key.ExportParameters(false).Q; return [0x04, .. p.X!, .. p.Y!]; }

    static Assertion Signed(byte[] challenge)
    {
        byte[] data = [.. SHA256.HashData(Encoding.UTF8.GetBytes(Rp)), 0x05, 0, 0, 0, 1];
        var client = Hello.ClientData("webauthn.get", challenge, Rp);
        var rs = Key.SignData([.. data, .. SHA256.HashData(client)], HashAlgorithmName.SHA256);
        var r = new BigInteger(rs.AsSpan(0, 32), true, true);
        var s = new BigInteger(rs.AsSpan(32), true, true);
        return new(data, client, P256.Der(r, s > N / 2 ? N - s : s));
    }

    static readonly Prompt Seen = new(42, 7, "CredentialUIBroker/Credential Dialog Xaml Host");

    static Report Run(FakeHello hello, Func<bool>? stop = null, params Attack[] attacks) =>
        Runner.Run(hello, Rp, attacks, () => () => Seen, stop ?? (() => false), _ => { }, TimeSpan.FromMilliseconds(50));

    static Attack Inject(Func<Prompt?, (bool, string)> run, bool endingMeansDelivered = true) =>
        new("inject", "test", NeedsPrompt: true, endingMeansDelivered, RequireUv: true, run);

    [Fact]
    public void A_silent_assertion_with_nobody_at_the_keyboard_fails_the_proof()
    {
        var hello = new FakeHello(h => h.Pending!.SetResult(new(0, Signed([9]))));
        var report = Run(hello, null, new Attack("silent", "test", false, false, true, _ => (true, "")));
        Assert.Equal(Outcome.Failed, report.Steps.Single(s => s.Name == "silent").Outcome);
        Assert.Equal(1, report.ExitCode);
    }

    [Fact]
    public void An_injection_that_ends_the_prompt_fails_the_proof_even_without_a_signature()
    {
        FakeHello hello = new();
        var report = Run(hello, null, Inject(_ => { hello.Pending!.SetResult(new(Hello.Cancelled, null)); return (true, "esc"); }));
        Assert.Equal(Outcome.Failed, report.Steps.Single(s => s.Name == "inject").Outcome);
        Assert.Equal(Conclusion.Failed, report.Verdict);
    }

    [Fact]
    public void An_injection_the_prompt_ignores_is_blocked_and_the_call_is_cancelled_through_the_api()
    {
        FakeHello hello = new();
        var report = Run(hello, null, Inject(_ => (true, "esc")));
        Assert.Equal(Outcome.Blocked, report.Steps.Single(s => s.Name == "inject").Outcome);
        Assert.Equal(1, hello.Cancels);
        Assert.Equal(0, report.ExitCode);
        Assert.Contains("## Claim", report.Markdown([]));
    }

    [Fact]
    public void With_no_prompt_found_nothing_is_sent_and_the_proof_is_inconclusive()
    {
        FakeHello hello = new();
        var report = Runner.Run(hello, Rp, [Inject(_ => throw new InvalidOperationException("sent"))], () => () => null, () => false, _ => { }, TimeSpan.FromMilliseconds(50));
        Assert.Equal(Outcome.Inconclusive, report.Steps.Single(s => s.Name == "inject").Outcome);
        Assert.Equal(2, report.ExitCode);
        Assert.DoesNotContain("## Claim", report.Markdown([]));
    }

    [Fact]
    public void Closing_the_prompt_fails_the_proof_only_if_a_signature_comes_back()
    {
        FakeHello hello = new();
        var closed = Run(hello, null, Inject(_ => { hello.Pending!.SetResult(new(Hello.Cancelled, null)); return (true, "WM_CLOSE"); }, endingMeansDelivered: false));
        Assert.Equal(Outcome.Blocked, closed.Steps.Single(s => s.Name == "inject").Outcome);
        var signed = Run(hello, null, Inject(_ => { hello.Pending!.SetResult(new(0, Signed([7]))); return (true, "minimised"); }, endingMeansDelivered: false));
        Assert.Equal(Outcome.Failed, signed.Steps.Single(s => s.Name == "inject").Outcome);
    }

    [Fact]
    public void The_person_s_assertion_passes_the_roster_verifier_and_its_replay_for_a_new_challenge_is_refused()
    {
        var report = Run(new FakeHello());
        Assert.Contains(report.Setup, l => l.Contains("roster verifier: accepted"));
        var replay = report.Steps.Single(s => s.Name == "replay");
        Assert.Equal(Outcome.Blocked, replay.Outcome);
        Assert.Contains(Refusal.UvChallenge, replay.Raw);
    }

    [Fact]
    public void The_test_credential_is_deleted_when_an_attack_throws_and_when_Ctrl_C_stops_the_run()
    {
        FakeHello hello = new();
        var stops = 0;
        var report = Run(hello, () => stops++ > 0, Inject(_ => throw new InvalidOperationException("boom")), Inject(_ => (true, "never")));
        Assert.Equal(1, hello.Deletes);
        Assert.Equal(1, hello.Cancels);
        Assert.Equal(Outcome.Inconclusive, report.Steps.Single(s => s.Name == "inject").Outcome);
        Assert.Contains("stopped", report.Cleanup);
        Assert.Equal(Conclusion.Inconclusive, report.Verdict);
    }

    [Fact]
    public void A_backup_eligible_credential_never_holds_even_when_every_attack_is_blocked()
    {
        var report = Run(new FakeHello(synced: true), null, Inject(_ => (true, "esc")));
        Assert.Equal(Conclusion.Inconclusive, report.Verdict);
    }

    [Fact]
    public void The_test_credential_is_deleted_when_parsing_its_attestation_throws()
    {
        var hello = new FakeHello(badAttestation: true);
        var report = Run(hello, null, Inject(_ => (true, "esc")));
        Assert.Equal(1, hello.Deletes);
        Assert.Contains(report.Setup, l => l.Contains("attestation parse"));
        Assert.Equal(2, report.ExitCode);
    }

    [Fact]
    public void The_test_credential_is_deleted_when_the_first_prompt_finder_throws()
    {
        FakeHello hello = new();
        var report = Runner.Run(hello, Rp, [Inject(_ => (true, "esc"))], () => () => throw new InvalidOperationException("finder"), () => false, _ => { }, TimeSpan.FromMilliseconds(50));
        Assert.Equal(1, hello.Deletes);
        Assert.Empty(report.Steps);
        Assert.Equal(2, report.ExitCode);
    }

    [Fact]
    public void Only_a_new_window_owned_by_the_harness_s_own_window_is_bound_to_the_call()
    {
        const nint mine = 100;
        Assert.Equal(7, Attacks.Pick([(5, []), (6, [200]), (7, [300, mine])], mine));
        Assert.Equal(0, Attacks.Pick([(5, []), (6, [200])], mine));
        Assert.Equal(0, Attacks.Pick([(7, [mine]), (8, [mine])], mine));
    }
}