using System.Text.RegularExpressions;
using Xunit;

namespace AgentBridge.Roster.Tests;

// #55 R2 part 2: the reasons an enrollment is refused (device-approval.md §4.4), as codes on the wire. The same eight are in
// devices.md's "Enrollment refusals" table, which the PWA's port (pwa/js/roster/enrollment-refusal.js) is tested against too.
public class EnrollmentRefusalTests
{
    readonly World w = new();

    public EnrollmentRefusalTests() { w.Genesis(); w.Add("addB", "G", "B", "G"); }

    static string Doc()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AgentBridge.slnx"))) { dir = dir.Parent; }
        return File.ReadAllText(Path.Combine(dir!.FullName, "docs", "protocol", "devices.md"));
    }

    [Fact]
    public void The_eight_codes_are_the_ones_the_protocol_document_lists_and_each_has_words_for_a_person()
    {
        string[] expected = ["wrong-code", "expired", "already-approved", "removed", "unknown-kind", "roster-limit", "approver-probation", "no-presence"];
        Assert.Equal(expected, EnrollmentRefusal.All);
        var section = Doc().Replace("\r\n", "\n").Split("## Enrollment refusals")[1].Split("\n## ")[0];
        var listed = Regex.Matches(section, @"^\| `([a-z-]+)` \|", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(expected, listed);
        foreach (var code in expected)
        {
            Assert.True(EnrollmentRefusal.IsKnown(code));
            Assert.False(string.IsNullOrWhiteSpace(EnrollmentRefusal.Message(code)));
        }
        Assert.False(EnrollmentRefusal.IsKnown("attempt-store"));
        Assert.Contains("sign out and in again", EnrollmentRefusal.Message(EnrollmentRefusal.Removed));
    }

    [Fact]
    public void An_approval_nothing_stands_in_the_way_of_is_not_refused()
    {
        Assert.Null(EnrollmentRefusal.Check(w.Verifier(), w.Id("G"), w.Id("X"), RosterKinds.Phone));
    }

    [Fact]
    public void A_kind_the_roster_does_not_have_is_unknown()
    {
        Assert.Equal(EnrollmentRefusal.UnknownKind, EnrollmentRefusal.Check(w.Verifier(), w.Id("G"), w.Id("X"), "laptop"));
    }

    [Fact]
    public void A_device_that_is_already_active_is_already_approved()
    {
        Assert.Equal(EnrollmentRefusal.AlreadyApproved, EnrollmentRefusal.Check(w.Verifier(), w.Id("G"), w.Id("B"), RosterKinds.Phone));
    }

    [Fact]
    public void A_key_that_was_removed_is_removed_even_though_it_is_not_active()
    {
        w.Remove("rmB", "G", "addB", "addB");
        Assert.Equal(EnrollmentRefusal.Removed, EnrollmentRefusal.Check(w.Verifier(), w.Id("G"), w.Id("B"), RosterKinds.Phone));
    }

    [Fact]
    public void A_roster_at_its_limit_is_refused_and_one_below_it_is_not()
    {
        var parent = "addB";
        for (var i = 0; i < RosterVerifier.MaxActive - 3; i++)
        {
            w.Add($"add{i}", "G", $"D{i}", parent);
            parent = $"add{i}";
        }
        var below = w.Verifier();
        Assert.Equal(RosterVerifier.MaxActive - 1, below.State.ActiveCount);
        Assert.Null(EnrollmentRefusal.Check(below, w.Id("G"), w.Id("X"), RosterKinds.Phone));
        w.Add("addLast", "G", "Last", parent);
        Assert.Equal(EnrollmentRefusal.RosterLimit, EnrollmentRefusal.Check(w.Verifier(), w.Id("G"), w.Id("X"), RosterKinds.Phone));
    }

    [Fact]
    public void An_approver_on_probation_is_refused_until_probation_ends()
    {
        var verifier = w.Verifier();
        Assert.Equal(EnrollmentRefusal.ApproverProbation, EnrollmentRefusal.Check(verifier, w.Id("B"), w.Id("X"), RosterKinds.Phone));
        w.Now += RosterVerifier.Probation + TimeSpan.FromHours(1);
        Assert.Null(EnrollmentRefusal.Check(verifier, w.Id("B"), w.Id("X"), RosterKinds.Phone));
    }

    [Fact]
    public void An_approver_that_cannot_prove_presence_is_refused_and_one_that_is_not_in_the_roster_is_too()
    {
        w.Add("addN", "G", "N", "addB", presence: Presence.None);
        var verifier = w.Verifier();
        w.Now += RosterVerifier.Probation + TimeSpan.FromHours(1);
        Assert.Equal(EnrollmentRefusal.NoPresence, EnrollmentRefusal.Check(verifier, w.Id("N"), w.Id("X"), RosterKinds.Phone));
        Assert.Equal(EnrollmentRefusal.NoPresence, EnrollmentRefusal.Check(verifier, w.Id("Nobody"), w.Id("X"), RosterKinds.Phone));
    }

    [Fact]
    public void The_first_reason_wins_in_the_documents_order()
    {
        // An unknown kind of a device already approved, asked of an approver on probation: the kind is said first.
        Assert.Equal(EnrollmentRefusal.UnknownKind, EnrollmentRefusal.Check(w.Verifier(), w.Id("B"), w.Id("B"), "laptop"));
        Assert.Equal(EnrollmentRefusal.AlreadyApproved, EnrollmentRefusal.Check(w.Verifier(), w.Id("B"), w.Id("B"), RosterKinds.Phone));
    }
}
