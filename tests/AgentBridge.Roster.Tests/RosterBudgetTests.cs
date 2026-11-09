using AgentBridge.Roster;

// §10's budget cases: removals counted per signer across branches, and a fabricated copy's
// entries counted against the copy, never the real device.
public sealed class RosterBudgetTests
{
    [Fact]
    public void An_approver_signing_removals_on_one_head_and_more_on_each_stops_at_64()
    {
        var w = new World();
        w.Genesis();
        w.Add("+C", "G", "C", "G");
        w.Add("+B", "G", "B", "+C");
        for (var i = 0; i < 33; i++)
        {
            w.Remove($"R{i}", "G", "+C", "+B");
            w.Remove($"S{i}", "G", "+B", $"R{i}");
        }
        var offered = w.Feed(new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now), [.. w.Labels]);
        var removals = offered.Skip(3).ToList();
        Assert.All(removals.Take(RosterVerifier.SignerBudget), o => Assert.Equal(Verdict.Accepted, o.Verdict));
        Assert.Equal(Invalid.Budget, removals[RosterVerifier.SignerBudget].Reason);
    }

    [Fact]
    public void A_copys_removals_count_against_the_copy_and_the_real_device_keeps_its_own_64()
    {
        var w = new World();
        w.Genesis();
        w.Add("+H", "G", "H", "G");
        w.Add("+A'", "H", "A", "+H", copy: true);
        w.Add("+Z", "A", "Z", "+A'", signAs: "H");
        for (var i = 0; i < RosterVerifier.SignerBudget + 1; i++) { w.Remove($"F{i}", "A", "+Z", "+Z", signAs: "H"); }
        w.Add("+A", "G", "A", "G");
        w.Add("+Y", "A", "Y", "+A");
        w.Remove("A-Y", "A", "+Y", "+Y");
        var offered = w.Feed(new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now), [.. w.Labels]);
        var at = w.Labels.ToList();
        Assert.Equal(Verdict.Accepted, offered[at.IndexOf($"F{RosterVerifier.SignerBudget - 1}")].Verdict);
        Assert.Equal(Invalid.Budget, offered[at.IndexOf($"F{RosterVerifier.SignerBudget}")].Reason);
        Assert.Null(offered[at.IndexOf("A-Y")].Reason);
    }

    [Fact]
    public void The_generations_caps_are_512_adds_and_4096_entries_however_a_test_shrinks_them()
    {
        Assert.Equal((512, 4096), (RosterVerifier.MaxAdds, RosterVerifier.MaxEntries));
        var v = new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => TimeSpan.Zero);
        Assert.Equal((RosterVerifier.MaxAdds, RosterVerifier.MaxEntries), (v.AddCap, v.EntryCap));
    }

    // The cap, shrunk to 40 entries so it can be filled: H fills it with adds and renames, and G's
    // removal of H is still accepted (removals are never refused for the generation's caps).
    [Fact]
    public void A_roster_at_its_entry_cap_filled_by_one_device_still_accepts_that_devices_removal()
    {
        var w = new World();
        w.Genesis();
        w.Add("+H", "G", "H", "G");
        var last = "+H";
        for (var a = 0; a < 5; a++)
        {
            w.Add($"+A{a}", "H", $"A{a}", last);
            last = $"+A{a}";
            for (var n = 0; n < (a < 4 ? 7 : 5); n++) { w.Rename($"n{a}.{n}", "H", $"+A{a}", last, $"Name {a}.{n}"); last = $"n{a}.{n}"; }
        }
        w.Rename("over", "H", "+H", last, "One too many");
        w.Remove("G-H", "G", "+H", last);
        var v = new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now) { EntryCap = 40 };
        var offered = w.Feed(v, [.. w.Labels]);
        Assert.Equal(40, offered.Count(o => o.Verdict == Verdict.Accepted) - 1);
        Assert.Equal(Invalid.Cap, offered[^2].Reason);
        Assert.Equal(Verdict.Accepted, offered[^1].Verdict);
        Assert.Equal(["G"], w.Active(v));
    }

    [Fact]
    public void The_add_past_the_generations_add_cap_is_refused()
    {
        var w = new World();
        w.Genesis();
        var last = "G";
        for (var i = 0; i < 5; i++) { w.Add($"+N{i}", "G", $"N{i}", last); last = $"+N{i}"; }
        var offered = w.Feed(new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now) { AddCap = 5 }, [.. w.Labels]);
        Assert.All(offered.SkipLast(1), o => Assert.Equal(Verdict.Accepted, o.Verdict));
        Assert.Equal(Invalid.Cap, offered[^1].Reason);
    }

    [Fact]
    public void The_evidence_limits_are_256_per_signer_and_1024_in_all_however_a_test_shrinks_them()
    {
        Assert.Equal((256, 1024), (RosterVerifier.EvidencePerSigner, RosterVerifier.EvidenceTotal));
        var v = new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => TimeSpan.Zero);
        Assert.Equal((RosterVerifier.EvidencePerSigner, RosterVerifier.EvidenceTotal), (v.EvidenceSignerCap, v.EvidenceCap));
    }

    // §10's subtree that spreads its over-budget entries two to a signer, scaled down: J1 to J3,
    // which confirmed T added, each rename themselves twice past the eight an add allows, all on
    // one line, and G removes T on it, needing all six as evidence.
    internal static World SpreadOverBudget()
    {
        var w = new World();
        w.Genesis();
        w.Add("+T", "G", "T", "G");
        w.Confirm("cT", "G", "+T", "+T");
        w.Add("+J1", "T", "J1", "cT");
        w.Add("+J2", "T", "J2", "+J1");
        w.Add("+J3", "T", "J3", "+J2");
        var last = "+J3";
        foreach (var (from, to) in new[] { (0, RosterVerifier.RenamesPerAdd), (RosterVerifier.RenamesPerAdd, RosterVerifier.RenamesPerAdd + 2) })
        {
            for (var j = 1; j <= 3; j++)
            {
                for (var n = from; n < to; n++) { w.Rename($"J{j}.{n}", $"J{j}", $"+J{j}", last, $"J{j} {n}"); last = $"J{j}.{n}"; }
            }
        }
        w.Remove("G-T", "G", "+T", last);
        return w;
    }

    [Theory]
    [InlineData(0, 0, true)]
    [InlineData(0, 5, false)]
    [InlineData(1, 0, false)]
    public void A_removal_brings_evidence_only_within_the_verifiers_per_signer_and_total_limits(int perSigner, int total, bool applies)
    {
        var w = SpreadOverBudget();
        var v = new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now)
        {
            EvidenceSignerCap = perSigner > 0 ? perSigner : RosterVerifier.EvidencePerSigner,
            EvidenceCap = total > 0 ? total : RosterVerifier.EvidenceTotal,
        };
        var offered = w.Feed(v, [.. w.Labels]);
        Assert.Equal(6, offered.Count(o => o.Reason == Invalid.Budget));
        Assert.Equal(applies ? null : Invalid.Evidence, offered[^1].Reason);
        Assert.Equal(applies ? ["G"] : ["G", "J1", "J2", "J3", "T"], w.Active(v));
    }

    // With room for two evidence entries: T removes J1, bringing J1's two over-budget renames, and
    // its removal of J2, which needs one more, is refused. G's removal of T, signed on the line
    // before any of them, needs none and applies.
    [Fact]
    public void Once_a_host_has_used_its_evidence_a_removal_signed_on_the_common_parent_needs_none()
    {
        var w = new World();
        w.Genesis();
        w.Add("+T", "G", "T", "G");
        w.Confirm("cT", "G", "+T", "+T");
        w.Add("+J1", "T", "J1", "cT");
        w.Add("+J2", "T", "J2", "+J1");
        var last = "+J2";
        for (var n = 0; n < RosterVerifier.RenamesPerAdd + 2; n++) { w.Rename($"J1.{n}", "J1", "+J1", last, $"J1 {n}"); last = $"J1.{n}"; }
        w.Remove("T-J1", "T", "+J1", last);
        last = "T-J1";
        for (var n = 0; n < RosterVerifier.RenamesPerAdd + 1; n++) { w.Rename($"J2.{n}", "J2", "+J2", last, $"J2 {n}"); last = $"J2.{n}"; }
        w.Remove("T-J2", "T", "+J2", last);
        w.Remove("G-T", "G", "+T", "+J2");
        var v = new RosterVerifier(RosterFixture.Account, RosterFixture.Generation, () => w.Now) { EvidenceCap = 2 };
        var offered = w.Feed(v, [.. w.Labels]);
        var at = w.Labels.ToList();
        // Above its evidence, which is never applied, and applied as a removal there is.
        Assert.Equal(Verdict.Quarantined, offered[at.IndexOf("T-J1")].Verdict);
        Assert.Equal(Invalid.Evidence, offered[at.IndexOf("T-J2")].Reason);
        Assert.Equal(Verdict.Accepted, offered[at.IndexOf("G-T")].Verdict);
        Assert.Equal(["G"], w.Active(v));
    }
}