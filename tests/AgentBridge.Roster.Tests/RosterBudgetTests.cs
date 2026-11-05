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
}