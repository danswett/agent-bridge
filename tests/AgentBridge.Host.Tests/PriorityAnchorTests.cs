using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// #3720: a sealed local head of the priority chain detects a journal that was rolled back on its own.
// A rollback of both the journal and the head (a VM snapshot) is a documented gap, not tested here.
public sealed class PriorityAnchorTests
{
    static readonly DeliverySender Person = new("person", Owner: true);
    static readonly DeliverySender Agent = new("principal");
    static readonly GrantAudience Receiver = new(new string('a', 32), "profile");

    sealed class FakeAnchor : IPriorityAnchor
    {
        public byte[]? Blob;
        public bool FailStore, DamagedLoad;
        public byte[]? Load() => DamagedLoad ? throw new CryptographicException("damaged") : Blob;
        public void Store(byte[] head) { if (FailStore) { throw new IOException("outage"); } Blob = head; }
    }

    sealed class Node : IDisposable
    {
        public readonly FakeTimeProvider Clock = new();
        public readonly List<byte[]> Disk = [];
        public readonly DeliveryJournal Journal;
        public Node(FakeAnchor anchor, IEnumerable<DeliveryJournalRecord>? replay = null) =>
            Journal = new(new byte[32], Disk.Add, _ => { }, replay, anchor);
        public DeliveryJournalRecord[] Records => Disk.Select(b => JsonSerializer.Deserialize<DeliveryJournalRecord>(b, ProtocolJson.Options)!).ToArray();
        public PriorityCapability Cap() => new(Guid.NewGuid(), Agent.Id,
            new(Person.Id, Receiver.Device, "verified-person-proof", Clock.GetUtcNow(), new string('b', 64)),
            Receiver, Clock.GetUtcNow(), Clock.GetUtcNow().AddHours(2));
        public PriorityCapability Issue() { var c = Cap(); Journal.IssuePriority(c, Person, Receiver, () => true, Clock.GetUtcNow()); return c; }
        public PriorityDecision Decide() => Journal.DecidePriority(Guid.NewGuid(), "target", Agent, "body",
            new("urgent", "stop wrong work"), Receiver, Clock.GetUtcNow(), _ => true);
        public void Dispose() => Journal.Dispose();
    }

    static (int N, string Tail) Head(FakeAnchor a)
    {
        using var doc = JsonDocument.Parse(a.Blob!);
        return (doc.RootElement.GetProperty("n").GetInt32(), doc.RootElement.GetProperty("tail").GetString()!);
    }

    [Fact]
    public void Every_priority_append_reseals_the_head_to_the_chain_tail()
    {
        var anchor = new FakeAnchor(); using var n = new Node(anchor);
        n.Issue(); n.Decide();
        var records = n.Records;
        Assert.Equal((records.Length, records[^1].Hmac), Head(anchor));
    }

    [Theory]
    [InlineData("consumed")]
    [InlineData("revoked")]
    public void A_rolled_back_journal_prefix_never_restores_authority_or_quota(string rolledBack)
    {
        var anchor = new FakeAnchor(); using var n = new Node(anchor);
        var cap = n.Issue(); var afterIssue = n.Records.Length;
        if (rolledBack == "consumed") { Assert.Equal("urgent", n.Decide().Priority); }
        else { n.Journal.RevokePriority(cap.Id, Person, Receiver, () => true, n.Clock.GetUtcNow()); }
        using var replay = new Node(anchor, n.Records[..afterIssue]);
        replay.Clock.SetUtcNow(n.Clock.GetUtcNow());
        Assert.NotEqual("urgent", replay.Decide().Priority);
        Assert.Throws<InvalidOperationException>(() => replay.Issue());
    }

    [Fact]
    public void A_refused_rollback_is_not_laundered_by_later_appends_across_a_second_restart()
    {
        var anchor = new FakeAnchor(); using var n = new Node(anchor);
        n.Issue(); var afterIssue = n.Records.Length; Assert.Equal("urgent", n.Decide().Priority);
        var prefix = n.Records[..afterIssue];
        using var first = new Node(anchor, prefix);
        first.Clock.SetUtcNow(n.Clock.GetUtcNow());
        Assert.NotEqual("urgent", first.Decide().Priority);
        Assert.NotEmpty(first.Records);
        using var second = new Node(anchor, prefix.Concat(first.Records));
        second.Clock.SetUtcNow(n.Clock.GetUtcNow());
        Assert.NotEqual("urgent", second.Decide().Priority);
        Assert.Throws<InvalidOperationException>(() => second.Issue());
    }

    [Theory]
    [InlineData("""{"n":99999999999,"tail":"x"}""")]
    [InlineData("""{"n":-1,"tail":"x"}""")]
    [InlineData("""{"n":2147483647,"tail":"x"}""")]
    [InlineData("""{"n":1.5,"tail":"x"}""")]
    [InlineData("""{"tail":"x"}""")]
    public void An_out_of_range_or_malformed_head_count_quarantines_instead_of_throwing(string head)
    {
        var anchor = new FakeAnchor(); using var n = new Node(anchor); n.Issue();
        anchor.Blob = System.Text.Encoding.UTF8.GetBytes(head);
        using var replay = new Node(anchor, n.Records);
        Assert.Throws<InvalidOperationException>(() => replay.Issue());
        Assert.NotEqual("urgent", replay.Decide().Priority);
    }

    [Fact]
    public void A_journal_emptied_while_the_head_remains_refuses_priority()
    {
        var anchor = new FakeAnchor(); using var n = new Node(anchor); n.Issue();
        using var replay = new Node(anchor, []);
        Assert.Throws<InvalidOperationException>(() => replay.Issue());
    }

    [Fact]
    public void A_missing_head_refuses_priority_when_records_exist_but_not_for_a_fresh_journal()
    {
        var anchor = new FakeAnchor(); using var n = new Node(anchor); n.Issue();
        using var stale = new Node(new FakeAnchor(), n.Records);
        Assert.Throws<InvalidOperationException>(() => stale.Issue());
        using var fresh = new Node(new FakeAnchor());
        fresh.Issue();
    }

    [Theory]
    [InlineData("damaged")]
    [InlineData("wrong-tail")]
    [InlineData("garbage")]
    public void A_damaged_or_mismatching_head_refuses_priority(string fault)
    {
        var anchor = new FakeAnchor(); using var n = new Node(anchor); n.Issue();
        var (count, _) = Head(anchor);
        if (fault == "damaged") { anchor.DamagedLoad = true; }
        else if (fault == "wrong-tail") { anchor.Blob = JsonSerializer.SerializeToUtf8Bytes(new { n = count, tail = "other" }); }
        else { anchor.Blob = [1, 2, 3]; }
        using var replay = new Node(anchor, n.Records);
        Assert.Throws<InvalidOperationException>(() => replay.Issue());
        Assert.NotEqual("urgent", replay.Decide().Priority);
    }

    [Fact]
    public void A_journal_ahead_of_the_head_after_a_crash_before_sealing_is_accepted_and_resealed()
    {
        var anchor = new FakeAnchor(); using var n = new Node(anchor); n.Issue();
        var stale = anchor.Blob; n.Decide(); anchor.Blob = stale;
        using var replay = new Node(anchor, n.Records);
        Assert.Equal(n.Records.Length, Head(anchor).N);
        Assert.Equal("urgent", replay.Decide().Priority);
    }

    [Fact]
    public void A_failed_seal_leaves_the_journal_uncertain_and_grants_no_authority()
    {
        var anchor = new FakeAnchor(); using var n = new Node(anchor); n.Issue();
        anchor.FailStore = true;
        Assert.Throws<IOException>(() => n.Decide());
        anchor.FailStore = false;
        Assert.Throws<InvalidOperationException>(() => n.Decide());
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void The_DPAPI_head_round_trips_and_a_damaged_file_is_refused()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var dir = Path.Combine(Path.GetTempPath(), "priority-anchor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var file = Path.Combine(dir, "delivery.head"); var anchor = new DpapiPriorityAnchor(file);
            Assert.Null(anchor.Load());
            anchor.Store([1, 2, 3]);
            Assert.Equal<byte>([1, 2, 3], anchor.Load()!);
            Assert.NotEqual<byte>([1, 2, 3], File.ReadAllBytes(file));
            File.WriteAllBytes(file, [9, 9, 9]);
            Assert.ThrowsAny<CryptographicException>(() => anchor.Load());
        }
        finally { Directory.Delete(dir, true); }
    }
}
