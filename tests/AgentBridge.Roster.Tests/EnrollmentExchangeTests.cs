using System.Text;
using AgentBridge.Roster;
using Xunit;
using static RosterFixture;

namespace AgentBridge.Roster.Tests;

public sealed class EnrollmentExchangeTests
{
    static readonly byte[] H = Encoding.UTF8.GetBytes("channel one handshake hash");
    static readonly byte[] OtherH = Encoding.UTF8.GetBytes("channel two handshake hash");
    static readonly byte[] NA = Encoding.UTF8.GetBytes("approver nonce 0123456789abcdef!");
    static readonly byte[] NN = Encoding.UTF8.GetBytes("joiner nonce 0123456789abcdefgh!");

    DateTimeOffset _now = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);

    DateTimeOffset Now() => _now;

    EnrollmentLockout ApproverLimits() => new(EnrollmentLockout.Counts.WrongEntries, Now);

    EnrollmentLockout JoinerLimits() => new(EnrollmentLockout.Counts.EndedAttempts, Now);

    // Runs the messages of §4.2 steps 1-4 between one approver and one joiner on the same channel.
    (EnrollmentApprover A, EnrollmentJoiner N) Exchange(byte[] approverH, byte[] joinerH, string pending = "P", EnrollmentLockout? approverLimits = null, EnrollmentLockout? joinerLimits = null)
    {
        var a = new EnrollmentApprover(Account, approverH, pending, approverLimits ?? ApproverLimits(), Now);
        var n = new EnrollmentJoiner(Account, joinerH, joinerLimits ?? JoinerLimits(), Now, NN);
        var offer = a.OnCommit(n.Commit()!, NA);
        var reveal = n.OnOffer(offer!);
        Assert.True(a.OnReveal(reveal!));
        return (a, n);
    }

    static string Digits(string shown) => shown.Replace(" ", "", StringComparison.Ordinal);

    [Fact]
    public void Both_codes_match_when_both_ends_share_one_channel_and_the_person_types_them()
    {
        var (a, n) = Exchange(H, H);

        Assert.Null(a.Code2Shown);
        Assert.Equal(CodeResult.Match, a.EnterCode1(n.Code1Shown!));
        Assert.Equal(CodeResult.Match, n.EnterCode2(a.Code2Shown!));
        Assert.Equal(Enrollment.Transcript(Account, H, NA, NN), a.TranscriptToSign);
    }

    [Fact]
    public void A_relay_between_the_two_devices_cannot_make_either_code_match()
    {
        // The relay ends both channels and plays the other side of each, with commitments that are
        // valid on its own leg. The two legs have different handshake hashes, so what the person
        // reads off one device is not what the other computed.
        var a = new EnrollmentApprover(Account, OtherH, "P", ApproverLimits(), Now);
        var relayAsJoiner = new EnrollmentJoiner(Account, OtherH, JoinerLimits(), Now, Encoding.UTF8.GetBytes("relay joiner nonce 0123456789ab!"));
        var n = new EnrollmentJoiner(Account, H, JoinerLimits(), Now, NN);
        var relayAsApprover = new EnrollmentApprover(Account, H, "P", ApproverLimits(), Now);
        Assert.True(a.OnReveal(relayAsJoiner.OnOffer(a.OnCommit(relayAsJoiner.Commit()!, NA)!)!));
        Assert.True(relayAsApprover.OnReveal(n.OnOffer(relayAsApprover.OnCommit(n.Commit()!, NA)!)!));

        Assert.Equal(CodeResult.Wrong, a.EnterCode1(n.Code1Shown!));
        Assert.Null(a.Code2Shown);
        Assert.Null(a.TranscriptToSign);
    }
    [Fact]
    public void A_reveal_that_is_not_the_committed_nonce_ends_the_attempt()
    {
        var a = new EnrollmentApprover(Account, H, "P", ApproverLimits(), Now);
        var n = new EnrollmentJoiner(Account, H, JoinerLimits(), Now, NN);
        a.OnCommit(n.Commit()!, NA);

        Assert.False(a.OnReveal(Encoding.UTF8.GetBytes("a different nonce, 32 bytes long!")));
        Assert.Equal(CodeResult.Ended, a.EnterCode1(n.Code1Shown ?? "000 000"));
    }

    [Fact]
    public void Three_wrong_code_one_entries_end_the_attempt_and_block_that_device_for_fifteen_minutes()
    {
        var limits = ApproverLimits();
        var (a, _) = Exchange(H, H, "P", limits);

        Assert.Equal(CodeResult.Wrong, a.EnterCode1("000 000"));
        Assert.Equal(CodeResult.Wrong, a.EnterCode1("abcdef"));
        Assert.Equal(CodeResult.Ended, a.EnterCode1("12"));
        Assert.True(limits.IsBlocked("P"));

        _now += TimeSpan.FromMinutes(14);
        Assert.True(limits.IsBlocked("P"));
        _now += TimeSpan.FromMinutes(2);
        Assert.False(limits.IsBlocked("P"));
    }

    [Fact]
    public void Three_wrong_entries_in_an_hour_lock_the_approver_even_across_different_pending_devices()
    {
        var limits = ApproverLimits();
        foreach (var pending in new[] { "P1", "P2", "P3" })
        {
            var (a, _) = Exchange(H, H, pending, limits);
            a.EnterCode1("000 000");
        }

        Assert.True(limits.IsBlocked("P4"));
        var late = new EnrollmentApprover(Account, H, "P4", limits, Now);
        late.OnCommit(Enrollment.Commit(H, NN), NA);
        Assert.True(late.OnReveal(NN));
        Assert.Equal(CodeResult.Locked, late.EnterCode1("000 000"));
    }

    [Fact]
    public void An_attempt_expires_after_five_minutes()
    {
        var (a, n) = Exchange(H, H);
        var code1 = n.Code1Shown!;

        _now += TimeSpan.FromMinutes(6);

        Assert.Equal(CodeResult.Ended, a.EnterCode1(code1));
        Assert.Equal(CodeResult.Ended, n.EnterCode2("000 000"));
    }

    [Fact]
    public void Three_ended_attempts_lock_the_joiner_and_it_stops_committing()
    {
        var limits = JoinerLimits();
        for (var i = 0; i < 3; i++)
        {
            var (_, n) = Exchange(H, H, joinerLimits: limits);
            n.EnterCode2("000 000");
            n.EnterCode2("000 000");
            Assert.Equal(CodeResult.Ended, n.EnterCode2("000 000"));
        }

        var joiner = new EnrollmentJoiner(Account, H, limits, Now, NN);
        Assert.Null(joiner.Commit());
    }

    [Fact]
    public void The_joiner_takes_only_an_add_of_itself_by_the_confirmed_approver_with_its_own_transcript()
    {
        var (a, n) = Exchange(H, H);
        a.EnterCode1(n.Code1Shown!);
        n.EnterCode2(a.Code2Shown!);
        var t = a.TranscriptToSign!;
        var w = new World();
        w.Genesis();
        w.Add("+B", "G", "B", "G", transcript: t);
        w.Add("+Forged", "G", "C", "G", transcript: Enrollment.Transcript(Account, OtherH, NA, NN));
        w.Add("+Other", "G", "D", "G", transcript: t);
        var own = w.Entry("+B").Body.Device!;

        Assert.True(n.Accepts(w.Entry("+B"), own, w.Id("G")));
        Assert.False(n.Accepts(w.Entry("+B"), own, w.Id("H")));
        Assert.False(n.Accepts(w.Entry("+Other"), own, w.Id("G")));
        var forged = w.Entry("+Forged");
        Assert.False(n.Accepts(forged, forged.Body.Device!, w.Id("G")));
    }

    [Fact]
    public void The_joiner_accepts_nothing_before_the_person_has_typed_code_two()
    {
        var (a, n) = Exchange(H, H);
        a.EnterCode1(n.Code1Shown!);
        var w = new World();
        w.Genesis();
        w.Add("+B", "G", "B", "G", transcript: a.TranscriptToSign);
        var add = w.Entry("+B");

        Assert.False(n.Accepts(add, add.Body.Device!, w.Id("G")));
    }
}
