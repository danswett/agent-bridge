using AgentBridge.App.Core;
using AgentBridge.Roster;
using Xunit;

// The approving side of device approval (#55 section 4): the person types code 1, and nothing is signed without a presence proof.
public sealed class ApproveViewModelTests
{
    sealed class Spy(string kind) : IPresenceProof
    {
        public int Signs;
        public string Kind => kind;
        public bool TrySign(byte[] message, out byte[] signature) { Signs++; signature = [1]; return true; }
    }

    sealed class Setup
    {
        public readonly ExchangePair Pair;
        public readonly Spy Proof;
        public readonly ApproveViewModel Approve;
        public readonly List<ApprovalRequest> Signed = [];
        public bool SignResult = true;

        public Setup(string presence = Presence.Key, ExchangePair? pair = null, string id = "3f9a2c71b0de4a5f0123456789abcdef")
        {
            Pair = pair ?? new ExchangePair();
            Proof = new Spy(presence);
            Approve = new ApproveViewModel(Pair.Approver, new PendingDevice("Kitchen laptop", "app", "windows", id, Pair.Time.Now.AddMinutes(-2)), Proof,
                r => { Signed.Add(r); return SignResult; }, Pair.Time);
            Pair.Exchange();
        }
    }

    [Fact]
    public void The_approvers_code_2_stays_hidden_until_the_person_has_typed_code_1_right()
    {
        var s = new Setup();
        Assert.Null(s.Approve.Code2);

        Assert.Equal(CodeResult.Wrong, s.Approve.EnterCode1(s.Pair.WrongCode));
        Assert.Null(s.Approve.Code2);
        Assert.Equal((ApproveState.WaitingForCode1, EnrollmentTexts.Wrong), (s.Approve.State, s.Approve.StatusText));

        Assert.Equal(CodeResult.Match, s.Approve.EnterCode1(s.Pair.Join.Code1));
        Assert.Matches(@"^\d{3} \d{3}$", s.Approve.Code2);
        Assert.Equal(ApproveState.Code2Shown, s.Approve.State);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("abcdef")]
    [InlineData("12 34 5\n")]
    public void Anything_but_six_digits_is_not_a_match_and_shows_no_code_2(string typed)
    {
        var s = new Setup();

        Assert.Equal(CodeResult.Wrong, s.Approve.EnterCode1(typed));
        Assert.Null(s.Approve.Code2);
    }

    [Fact]
    public void Three_wrong_code_1_entries_end_that_devices_attempts_and_three_in_an_hour_lock_the_approver()
    {
        var lockA = new EnrollmentLockout(EnrollmentLockout.Counts.WrongEntries, () => new ExchangePair.Clock().Now);
        var first = new Setup(pair: new ExchangePair(lockA: lockA, pendingId: "aaaa0000aaaa0000aaaa0000aaaa0000"));
        Assert.Equal(CodeResult.Wrong, first.Approve.EnterCode1(first.Pair.WrongCode));
        Assert.Equal(CodeResult.Wrong, first.Approve.EnterCode1(first.Pair.WrongCode));
        Assert.Equal(CodeResult.Ended, first.Approve.EnterCode1(first.Pair.WrongCode));
        Assert.Equal((ApproveState.Ended, EnrollmentTexts.Ended), (first.Approve.State, first.Approve.StatusText));

        var other = new Setup(pair: new ExchangePair(lockA: lockA, pendingId: "bbbb0000bbbb0000bbbb0000bbbb0000"));
        Assert.Equal(CodeResult.Locked, other.Approve.EnterCode1(other.Pair.Join.Code1));
        Assert.Equal((ApproveState.Locked, EnrollmentTexts.Locked), (other.Approve.State, other.Approve.StatusText));
    }

    [Fact]
    public void A_name_is_one_to_sixty_four_characters_with_no_control_characters_and_a_refusal_keeps_the_old_one()
    {
        var s = new Setup();

        Assert.True(s.Approve.SetName("  Office PC  "));
        Assert.Equal("Office PC", s.Approve.Name);
        Assert.False(s.Approve.SetName("   "));
        Assert.False(s.Approve.SetName(new string('x', 65)));
        Assert.False(s.Approve.SetName("bad\u0007name"));
        Assert.Equal("Office PC", s.Approve.Name);
        Assert.True(s.Approve.SetName(new string('x', 64)));
    }

    [Fact]
    public void The_banner_names_the_device_its_kind_its_id_and_how_long_ago_it_signed_in_in_fixed_phrases()
    {
        Assert.Equal("A new device wants to join: Kitchen laptop, says it is a Windows app, id 3f9a 2c71 b0de 4a5f, signed in 2 min ago", new Setup().Approve.Banner);
        Assert.Equal("a device of an unknown kind", ApproveTexts.KindText("<script>", "x"));
    }

    [Fact]
    public void With_no_presence_proof_approving_ends_in_CannotApprove_and_never_signs_or_asks_the_proof()
    {
        var s = new Setup(Presence.None);
        s.Approve.EnterCode1(s.Pair.Join.Code1);
        s.Approve.OnJoinerOk();
        Assert.False(s.Approve.CanApprove);

        Assert.Equal(ApproveResult.CannotApprove, s.Approve.Approve());

        Assert.Equal((ApproveState.CannotApprove, ApproveTexts.CannotApprove), (s.Approve.State, s.Approve.StatusText));
        Assert.Empty(s.Signed);
        Assert.Equal(0, s.Proof.Signs);
        Assert.False(new Setup("something-else").Approve.CanApprove);
    }

    [Fact]
    public void With_a_presence_proof_approving_waits_for_the_joiners_ok_then_signs_this_exchanges_transcript_and_the_edited_name()
    {
        var s = new Setup(Presence.Key);
        Assert.Equal(ApproveResult.NotReady, s.Approve.Approve());
        s.Approve.EnterCode1(s.Pair.Join.Code1);
        s.Approve.SetName("Office PC");
        Assert.Equal(ApproveResult.NotReady, s.Approve.Approve());
        Assert.Empty(s.Signed);

        s.Approve.OnJoinerOk();
        Assert.Equal(ApproveResult.Approved, s.Approve.Approve());

        var request = Assert.Single(s.Signed);
        Assert.Equal(s.Pair.Approver.TranscriptToSign, request.Transcript);
        Assert.Equal("Office PC", request.Name);
        Assert.Same(s.Proof, request.Proof);
        Assert.Equal(ApproveState.Approved, s.Approve.State);
    }

    [Fact]
    public void A_joiner_ok_before_code_1_matched_does_not_make_the_approval_ready_and_a_failed_signing_can_be_tried_again()
    {
        var s = new Setup(Presence.WebAuthn);
        s.Approve.OnJoinerOk();
        Assert.Equal(ApproveState.WaitingForCode1, s.Approve.State);

        s.Approve.EnterCode1(s.Pair.Join.Code1);
        s.Approve.OnJoinerOk();
        s.SignResult = false;
        Assert.Equal(ApproveResult.Failed, s.Approve.Approve());
        Assert.Equal((ApproveState.ReadyToApprove, EnrollmentTexts.Generic), (s.Approve.State, s.Approve.StatusText));
        s.SignResult = true;
        Assert.Equal(ApproveResult.Approved, s.Approve.Approve());
    }
}
