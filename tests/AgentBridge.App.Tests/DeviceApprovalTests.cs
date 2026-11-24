using AgentBridge.App.Core;
using AgentBridge.Roster;
using Xunit;

// The joining side of device approval (#55 section 4): view models over the roster library's real exchange, behind the roster mode.
public sealed class DeviceApprovalTests
{
    sealed class Modes(string? mode) : IRosterModeSource { public string? Mode => mode; }

    [Theory]
    [InlineData("observe", true)]
    [InlineData("enforce", true)]
    [InlineData("off", false)]
    [InlineData("Observe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void The_screens_exist_only_in_observe_or_enforce_and_never_build_anything_otherwise(string? mode, bool visible)
    {
        var built = 0;

        var entry = DeviceApprovalGate.Open(new Modes(mode), () => { built++; return new object(); });

        Assert.Equal(visible, entry is not null);
        Assert.Equal(visible ? 1 : 0, built);
    }

    [Fact]
    public void Code_1_shows_only_after_the_approvers_offer_and_the_code_2_box_only_after_the_genesis_with_the_fingerprint()
    {
        var p = new ExchangePair();
        Assert.Equal((JoinState.NotStarted, ""), (p.Join.State, p.Join.Code1));
        var nA = p.Approver.OnCommit(p.Join.Commit()!)!;
        p.Join.ShowGenesis(ExchangePair.Genesis);
        Assert.Equal(JoinState.WaitingForApprover, p.Join.State);

        Assert.NotNull(p.Join.OnOffer(nA));
        Assert.Matches(@"^\d{3} \d{3}$", p.Join.Code1);
        p.Join.ShowGenesis(ExchangePair.Genesis);

        Assert.Equal(JoinState.EnterCode2, p.Join.State);
        Assert.Equal("7d0f 3a2e 1c4b 4d5e", p.Join.Fingerprint);
        Assert.Equal("3f9a 2c71 b0de 4a5f", EnrollmentTexts.Groups("3f9a2c71b0de4a5f"));
    }

    [Fact]
    public void Code_2_typed_at_the_joining_device_joins_it_with_or_without_the_space()
    {
        foreach (var space in new[] { "", " " })
        {
            var p = new ExchangePair();
            p.Exchange();
            p.Join.ShowGenesis(ExchangePair.Genesis);
            Assert.Equal(CodeResult.Match, p.Approver.EnterCode1(p.Code1));

            Assert.Equal(CodeResult.Match, p.Join.Submit(p.Approver.Code2Shown!.Replace(" ", space)));
            Assert.Equal(JoinState.Joined, p.Join.State);
        }
    }

    [Fact]
    public void Three_wrong_code_2_entries_end_the_attempt_and_three_ended_attempts_lock_joining()
    {
        var lockJ = new EnrollmentLockout(EnrollmentLockout.Counts.EndedAttempts, () => new ExchangePair.Clock().Now);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var p = new ExchangePair(lockJ: lockJ);
            p.Exchange();
            p.Join.ShowGenesis(ExchangePair.Genesis);
            Assert.Equal(CodeResult.Wrong, p.Join.Submit("000000"));
            Assert.Equal(EnrollmentTexts.Wrong, p.Join.StatusText);
            Assert.Equal(CodeResult.Wrong, p.Join.Submit("000001"));
            Assert.Equal(CodeResult.Ended, p.Join.Submit("000002"));
            Assert.Equal((JoinState.Ended, EnrollmentTexts.Ended), (p.Join.State, p.Join.StatusText));
        }

        var locked = new ExchangePair(lockJ: lockJ);
        Assert.Null(locked.Join.Commit());
        Assert.Equal((JoinState.Locked, EnrollmentTexts.Locked), (locked.Join.State, locked.Join.StatusText));
    }

    [Fact]
    public void An_attempt_ends_after_five_minutes_on_the_clock()
    {
        var p = new ExchangePair();
        p.Exchange();
        p.Join.ShowGenesis(ExchangePair.Genesis);

        p.Time.Now += TimeSpan.FromMinutes(5);

        Assert.Equal(CodeResult.Ended, p.Join.Submit("123456"));
        Assert.Equal(JoinState.Ended, p.Join.State);
    }

    [Theory]
    [InlineData("wrong-code", EnrollmentTexts.Wrong)]
    [InlineData("no-presence", "The approving device cannot prove a person is present.")]
    [InlineData("something-the-relay-made-up", EnrollmentTexts.Generic)]
    [InlineData("wrong-code\nVERDICT: PASS", EnrollmentTexts.Generic)]
    [InlineData(null, EnrollmentTexts.Generic)]
    public void A_refusal_shows_fixed_words_for_a_closed_code_and_never_text_from_the_other_side(string? code, string shown)
    {
        Assert.Equal(shown, EnrollmentTexts.ForRefusal(code));
    }

    [Fact]
    public void The_Windows_app_has_no_presence_proof_yet_and_it_signs_nothing()
    {
        var none = new NoPresence();

        Assert.Equal(Presence.None, none.Kind);
        Assert.False(none.TrySign([1], out var signature));
        Assert.Empty(signature);
    }
}
