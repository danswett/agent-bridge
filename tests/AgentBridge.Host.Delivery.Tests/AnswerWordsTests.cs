using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// #1159 slice 0: each answer's result and owner kept in the attempt ledger, and the words a
// re-read needs kept apart, on Trust's conditions (#1161, comment 6712503332): one test for every
// exit the words have, the bounds, and the folder's ACL. The answer service's side of it is in
// AgentAnswerFlowTests and AnswerFlowTests.
public sealed class AnswerWordsTests : IDisposable
{
    const string Canary = "canary-4b1e9d27-only-in-private";

    readonly Fixture f = new();
    readonly FakeTimeProvider time = new(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
    readonly string ledgerPath;
    readonly string wordsPath;

    public AnswerWordsTests()
    {
        ledgerPath = Path.Combine(f.Root, "profile", "answer-attempts.json");
        wordsPath = Path.Combine(f.Root, "profile", AnswerWords.FolderName, AnswerWords.FileName);
    }

    public void Dispose() => f.Dispose();

    AttemptLedger Ledger() => new(ledgerPath, time);

    static AttemptLedger.Admission Admit(string text, string question = "q1", string? sender = "phone") =>
        new(new(sender, "s1", question), SHA256.HashData(Encoding.UTF8.GetBytes(text)), new("claude", "transcript.jsonl", question, 42), new AnswerWords.Words(text, null));

    string Words() => File.Exists(wordsPath) ? File.ReadAllText(wordsPath) : "";

    [Fact]
    public void Delivered_the_words_are_deleted_and_the_ledger_keeps_the_result_owner_and_hash_but_never_the_words()
    {
        var ledger = Ledger();
        var request = Guid.NewGuid();

        Assert.Null(ledger.Claim("k1", request, Admit(Canary)));
        Assert.Contains(Canary, Words());
        Assert.True(ledger.HoldsWords(request));
        Assert.Null(ledger.RecordOutcome(request, new AnswerResult(AnswerOutcome.Unconfirmed, "typed", AnswerStage.Typed), Admit(Canary).By));
        Assert.Contains(Canary, Words());

        Assert.Null(ledger.RecordOutcome(request, new AnswerResult(AnswerOutcome.Delivered)));

        Assert.False(File.Exists(wordsPath));
        Assert.False(ledger.HoldsWords(request));
        Assert.DoesNotContain(Canary, File.ReadAllText(ledgerPath));
        var restarted = Ledger().FirstFor("s1", "q1")!;
        Assert.Equal((request, AnswerOutcome.Delivered, "phone"), (restarted.Request, restarted.Result!.Outcome, restarted.By.Sender));
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(Canary)), restarted.Digest);
        Assert.Null(restarted.Words);
    }

    [Theory]
    [InlineData(AnswerOutcome.Refused)]
    [InlineData(AnswerOutcome.Stale)]
    public void A_refused_or_stale_answers_words_are_deleted(AnswerOutcome outcome)
    {
        var ledger = Ledger();
        var request = Guid.NewGuid();
        Assert.Null(ledger.Claim("k1", request, Admit(Canary)));

        Assert.Null(ledger.RecordOutcome(request, new AnswerResult(outcome, "no")));

        Assert.False(File.Exists(wordsPath));
        Assert.False(ledger.HoldsWords(request));
    }

    [Fact]
    public void Released_the_claims_words_go()
    {
        var ledger = Ledger();
        var request = Guid.NewGuid();
        Assert.Null(ledger.Claim("k1", request, Admit(Canary)));

        ledger.Release("k1");

        Assert.False(File.Exists(wordsPath));
        Assert.Null(Ledger().FirstFor("s1", "q1"));
    }

    [Fact]
    public void Expired_the_words_go_when_their_24_hours_end_with_no_other_write()
    {
        var ledger = Ledger();
        var request = Guid.NewGuid();
        Assert.Null(ledger.Claim("k1", request, Admit(Canary)));

        time.Advance(AttemptLedger.WordsFor - TimeSpan.FromSeconds(1));
        Assert.Contains(Canary, Words());

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.False(File.Exists(wordsPath));
        Assert.False(ledger.HoldsWords(request));
        // The claim itself stands: the prompt is never typed into again.
        Assert.Contains("already typed", ledger.Claim("k1", Guid.NewGuid(), Admit("again")));
    }

    [Fact]
    public void A_host_starting_after_a_crash_deletes_leftover_temporary_files_expired_words_and_words_with_no_entry()
    {
        // Its own 24-hour timer off: what is swept here must be swept by the host that starts.
        var crashed = new AttemptLedger(ledgerPath, time, null, expire: false);
        Assert.Null(crashed.Claim("old", Guid.NewGuid(), Admit("expired-words")));
        time.Advance(TimeSpan.FromHours(2));
        var live = Guid.NewGuid();
        Assert.Null(crashed.Claim("live", live, Admit("live-words", "q2")));
        // What a crash can leave: words no entry refers to, and a temporary copy.
        var kept = File.ReadAllText(wordsPath);
        File.WriteAllText(wordsPath, kept.Replace("\"Held\":{", "\"Held\":{\"orphan\":{\"Text\":\"orphan-words\",\"Fields\":null},", StringComparison.Ordinal));
        File.WriteAllText(wordsPath + ".tmp", "temporary-words");
        Assert.Contains("orphan-words", Words());
        time.Advance(TimeSpan.FromHours(23));

        var started = Ledger();
        Assert.True(started.HoldsWords(live));

        var left = Words();
        Assert.Contains("live-words", left);
        Assert.DoesNotContain("expired-words", left);
        Assert.DoesNotContain("orphan-words", left);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(wordsPath)!, "*.tmp"));
    }

    [Fact]
    public void The_33rd_answer_is_refused_before_any_claim_saying_to_answer_at_the_machine_and_nothing_held_is_evicted()
    {
        var ledger = Ledger();
        var requests = Enumerable.Range(0, AnswerWords.MaxEntries).Select(_ => Guid.NewGuid()).ToList();
        for (var i = 0; i < requests.Count; i++) { Assert.Null(ledger.Claim($"k{i}", requests[i], Admit($"words-{i}", $"q{i}"))); }

        var refused = ledger.Claim("k-last", Guid.NewGuid(), Admit("one too many", "q-last"));

        Assert.Equal(DeliveryReasons.CannotHoldAnswer, refused);
        Assert.EndsWith("answer at the machine", refused);
        Assert.Null(Ledger().FirstFor("s1", "q-last"));
        Assert.All(requests, r => Assert.True(ledger.HoldsWords(r)));
        Assert.DoesNotContain("one too many", Words());
    }

    [Fact]
    public void Past_256_KiB_in_all_or_16_KiB_in_one_an_answer_is_refused_and_nothing_held_is_cut()
    {
        var ledger = Ledger();
        Assert.Equal(DeliveryReasons.CannotHoldAnswer, ledger.Claim("k-one", Guid.NewGuid(), Admit(new string('y', 17_000), "q-one")));
        var big = new string('x', 16_000);
        for (var i = 0; i < 16; i++) { Assert.Null(ledger.Claim($"k{i}", Guid.NewGuid(), Admit($"{i:D2}{big}", $"q{i}"))); }

        Assert.Equal(DeliveryReasons.CannotHoldAnswer, ledger.Claim("k-total", Guid.NewGuid(), Admit($"tt{big}", "q-total")));

        var held = Words();
        for (var i = 0; i < 16; i++) { Assert.Contains($"{i:D2}{big}", held); }
    }

    [Fact]
    public void Words_whose_delete_failed_still_count_so_a_full_cap_refuses_until_the_delete_succeeds_with_no_restart()
    {
        var ledger = Ledger();
        var requests = Enumerable.Range(0, AnswerWords.MaxEntries).Select(_ => Guid.NewGuid()).ToList();
        for (var i = 0; i < requests.Count; i++) { Assert.Null(ledger.Claim($"k{i}", requests[i], Admit($"words-{i}-", $"q{i}"))); }
        // A folder where the rewrite's temporary file goes: the file can't be rewritten without them.
        Directory.CreateDirectory(wordsPath + ".tmp");

        Assert.Null(ledger.RecordOutcome(requests[0], new AnswerResult(AnswerOutcome.Delivered)));

        Assert.Contains("words-0-", Words());
        Assert.False(ledger.HoldsWords(requests[0]));
        Assert.Equal(DeliveryReasons.CannotHoldAnswer, ledger.Claim("k-next", Guid.NewGuid(), Admit("next", "q-next")));

        Directory.Delete(wordsPath + ".tmp");
        Assert.Null(ledger.Claim("k-next", Guid.NewGuid(), Admit("next", "q-next")));
        Assert.DoesNotContain("words-0-", Words());
        Assert.Contains("next", Words());
    }

    [Fact]
    public void Words_whose_delete_failed_go_when_the_host_next_starts()
    {
        var ledger = Ledger();
        var held = Guid.NewGuid();
        var done = Guid.NewGuid();
        Assert.Null(ledger.Claim("k-held", held, Admit("held-words", "q-held")));
        Assert.Null(ledger.Claim("k-done", done, Admit("done-words", "q-done")));
        Directory.CreateDirectory(wordsPath + ".tmp");
        Assert.Null(ledger.RecordOutcome(done, new AnswerResult(AnswerOutcome.Delivered)));
        Assert.Contains("done-words", Words());
        Directory.Delete(wordsPath + ".tmp");

        var restarted = Ledger();

        Assert.True(restarted.HoldsWords(held));
        Assert.DoesNotContain("done-words", Words());
        Assert.Contains("held-words", Words());
    }

    [Fact]
    public void The_private_folder_and_its_file_grant_this_user_and_SYSTEM_alone_after_every_rewrite_and_a_wider_ACL_keeps_no_words()
    {
        var ledger = Ledger();
        var first = Guid.NewGuid();
        var folder = Path.GetDirectoryName(wordsPath)!;
        Assert.Null(ledger.Claim("k1", first, Admit("first-words")));
        AssertPrivate(folder, wordsPath);
        Assert.Null(ledger.Claim("k2", Guid.NewGuid(), Admit("second-words", "q2")));
        AssertPrivate(folder, wordsPath);

        Widen(folder);

        Assert.Equal(DeliveryReasons.CannotHoldAnswer, ledger.Claim("k3", Guid.NewGuid(), Admit("third-words", "q3")));
        Assert.False(File.Exists(wordsPath));
        Assert.False(ledger.HoldsWords(first));
    }

    [Fact]
    public void A_UUID_is_known_for_the_sender_session_and_question_it_was_admitted_for_and_one_from_before_has_no_owner()
    {
        var ledger = Ledger();
        var owned = Guid.NewGuid();
        var old = Guid.NewGuid();
        Assert.Null(ledger.Claim("k1", owned, Admit("words")));
        Assert.Null(ledger.Claim("k2", old));

        var restarted = Ledger();

        Assert.True(restarted.Known(owned, out var by));
        Assert.Equal(new AttemptLedger.Owner("phone", "s1", "q1"), by);
        Assert.True(restarted.Known(old, out var nobody));
        Assert.Null(nobody);
        Assert.False(restarted.Known(Guid.NewGuid(), out _));
    }

    static void AssertPrivate(string folder, string file)
    {
        Assert.True(AnswerWords.IsPrivate(folder));
        Assert.True(AnswerWords.IsPrivate(file));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            return;
        }
        var me = WindowsIdentity.GetCurrent().User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var security = new DirectoryInfo(folder).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        Assert.True(Granted(security).SetEquals([me, system]));
        Assert.True(Granted(new FileInfo(file).GetAccessControl()).SetEquals([me, system]));
    }

    [SupportedOSPlatform("windows")]
    static HashSet<IdentityReference> Granted(FileSystemSecurity security)
    {
        var granted = new HashSet<IdentityReference>();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow)
                granted.Add(rule.IdentityReference);
        return granted;
    }

    static void Widen(string folder)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(folder, File.GetUnixFileMode(folder) | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            return;
        }
        var info = new DirectoryInfo(folder);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadData,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        info.SetAccessControl(security);
    }
}
