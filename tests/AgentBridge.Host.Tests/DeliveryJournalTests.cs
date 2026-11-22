using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Protocol;
using Xunit;

public sealed class DeliveryJournalTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ab-delivery-" + Guid.NewGuid().ToString("N"));
    static readonly DeliveryHeader Header = DeliveryEnvelope.Prepare(new("agent:user", "r-1", "q1",
        DateTimeOffset.UtcNow, "normal", 0), "secret words").Header;
    public void Dispose() { if (Directory.Exists(folder)) { Directory.Delete(folder, true); } }

    [Fact]
    public void Journal_records_are_ordered_metadata_only_and_only_handoff_verifies()
    {
        var lines = new List<byte[]>();
        using var journal = new DeliveryJournal(RandomNumberGenerator.GetBytes(32), lines.Add, _ => { });
        Assert.False(journal.VerifyHandoff("session", Header, "secret words"));
        foreach (var state in new[] { DeliveryState.Enqueued, DeliveryState.Handoff, DeliveryState.Typed, DeliveryState.Delivered, DeliveryState.Acked })
        {
            Assert.Equal(state, journal.Write("session", state == DeliveryState.Enqueued ? Header with { Seq = null } : Header, "secret words", state));
        }
        Assert.Equal(new[] { "enqueued", "handoff", "typed", "delivered", "acked" },
            lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("state").GetString()));
        Assert.All(lines, line => Assert.DoesNotContain("secret words", Encoding.UTF8.GetString(line)));
        Assert.True(journal.VerifyHandoff("session", Header, "secret words"));
        Assert.False(journal.VerifyHandoff("session", Header with { Priority = "urgent" }, "secret words"));
        Assert.False(journal.VerifyHandoff("other", Header, "secret words"));
        Assert.False(journal.VerifyHandoff("session", Header with { From = "owner:user" }, "secret words"));
        Assert.False(journal.VerifyHandoff("session", Header, "forged words"));
        Assert.Null(Assert.Single(DeliveryEnvelope.Parse("other", DeliveryEnvelope.Format(Header, "secret words"), journal.VerifyHandoff)).Header);
    }

    [Fact]
    public void A_transcript_emoji_cannot_alias_a_handed_off_replacement_character()
    {
        var prepared = DeliveryEnvelope.Prepare(Header, "\uFFFD");
        using var journal = new DeliveryJournal(RandomNumberGenerator.GetBytes(32), _ => { }, _ => { });
        journal.Write("session", prepared.Header with { Seq = null }, prepared.Body, DeliveryState.Enqueued);
        journal.Write("session", prepared.Header, prepared.Body, DeliveryState.Handoff);
        var frame = DeliveryEnvelope.Format(prepared.Header, prepared.Body);
        Assert.Equal(prepared.Header,
            Assert.Single(DeliveryEnvelope.Parse("session", frame, journal.VerifyHandoff)).Header);
        var forged = frame[..^prepared.Body.Length] + "\U0001F600";
        var entry = Assert.Single(DeliveryEnvelope.Parse("session", forged, journal.VerifyHandoff));
        Assert.Null(entry.Header);
        Assert.Equal(forged, entry.Text);
        Assert.True(journal.VerifyHandoff("session", prepared.Header, prepared.Body));
    }

    [Theory]
    [InlineData("\uFFFD")]
    [InlineData("\U0001F600")]
    [InlineData("h\u00E9llo \U0001F44D")]
    public void Valid_Unicode_bodies_verify_through_the_real_parser(string body)
    {
        var prepared = DeliveryEnvelope.Prepare(Header, body);
        using var journal = new DeliveryJournal(RandomNumberGenerator.GetBytes(32), _ => { }, _ => { });
        journal.Write("session", prepared.Header, prepared.Body, DeliveryState.Enqueued);
        journal.Write("session", prepared.Header, prepared.Body, DeliveryState.Handoff);
        var entry = Assert.Single(DeliveryEnvelope.Parse("session",
            DeliveryEnvelope.Format(prepared.Header, prepared.Body), journal.VerifyHandoff));
        Assert.Equal(prepared.Header, entry.Header);
        Assert.Equal(prepared.Body, entry.Text);
    }

    [Theory]
    [InlineData(0xD83D)]
    [InlineData(0xDE00)]
    public void Isolated_surrogates_are_refused_without_losing_the_handoff(int codeUnit)
    {
        var body = ((char)codeUnit).ToString();
        var prepared = DeliveryEnvelope.Prepare(Header, "\uFFFD");
        var logs = new List<string>();
        using var journal = new DeliveryJournal(RandomNumberGenerator.GetBytes(32), _ => { }, logs.Add);
        journal.Write("session", prepared.Header, prepared.Body, DeliveryState.Enqueued);
        journal.Write("session", prepared.Header, prepared.Body, DeliveryState.Handoff);
        Assert.False(journal.VerifyHandoff("session", prepared.Header, body));
        Assert.Single(logs);
        Assert.Equal(DeliveryState.Handoff, journal.State("session", prepared.Header.Id));
        Assert.True(journal.VerifyHandoff("session", prepared.Header, prepared.Body));
    }

    [Fact]
    public void Full_disk_refuses_enqueue_and_permits_no_ack()
    {
        using var journal = new DeliveryJournal(RandomNumberGenerator.GetBytes(32), _ => throw new IOException("full"), _ => { });
        Assert.Throws<IOException>(() => journal.Write("s", Header, "secret words", DeliveryState.Enqueued));
        Assert.Null(journal.State("s", Header.Id));
        Assert.Throws<InvalidOperationException>(() => journal.Write("s", Header, "secret words", DeliveryState.Acked));
    }

    [Fact]
    public void Post_key_write_failure_keeps_handoff_unconfirmed_never_acknowledged()
    {
        var lines = new List<byte[]>();
        var logs = new List<string>();
        var fail = false;
        using var journal = new DeliveryJournal(RandomNumberGenerator.GetBytes(32), b =>
        {
            if (fail) { throw new IOException("full"); }
            lines.Add(b);
        }, logs.Add);
        journal.Write("s", Header, "secret words", DeliveryState.Enqueued);
        journal.Write("s", Header, "secret words", DeliveryState.Handoff);
        fail = true;
        Assert.Equal(DeliveryState.Unconfirmed, journal.Write("s", Header, "secret words", DeliveryState.Typed));
        Assert.Equal(DeliveryState.Unconfirmed, journal.State("s", Header.Id));
        Assert.Equal(2, lines.Count);
        Assert.Single(logs);
        Assert.Throws<InvalidOperationException>(() => journal.Write("s", Header, "secret words", DeliveryState.Acked));
        Assert.True(journal.VerifyHandoff("s", Header, "secret words"));
        fail = false;
        Assert.Equal(DeliveryState.Unconfirmed, journal.Write("s", Header, "secret words", DeliveryState.Unconfirmed));
        Assert.Equal(DeliveryState.Delivered, journal.Write("s", Header, "secret words", DeliveryState.Delivered));
        Assert.Equal(DeliveryState.Acked, journal.Write("s", Header, "secret words", DeliveryState.Acked));
    }

    [Fact]
    public void Journal_is_beside_diagnostics_and_reopens_its_DPAPI_key_without_replacement()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var diagnostics = Path.Combine(folder, "diagnostics.jsonl");
        using (var journal = new DeliveryJournal(diagnostics, _ => { }))
        {
            journal.Write("s", Header, "secret words", DeliveryState.Enqueued);
        }
        using (var reopened = new DeliveryJournal(diagnostics, _ => { }))
        {
            reopened.Write("s", Header, "secret words", DeliveryState.Dropped);
        }
        var lines = File.ReadAllLines(Path.Combine(folder, "delivery.jsonl"));
        Assert.Equal(2, lines.Length);
        Assert.All(lines, line => Assert.DoesNotContain("secret words", line));
    }

    [Fact]
    public void A_damaged_DPAPI_key_is_refused_not_regenerated()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        Directory.CreateDirectory(folder);
        var key = Path.Combine(folder, "delivery.key");
        File.WriteAllBytes(key, [1, 2, 3]);
        Assert.Throws<CryptographicException>(() => new DeliveryJournal(Path.Combine(folder, "diagnostics.jsonl"), _ => { }));
        Assert.Equal(3, new FileInfo(key).Length);
    }

    [Fact]
    public void An_incomplete_disk_record_refuses_enqueue_without_appending_over_it()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var journal = new DeliveryJournal(Path.Combine(folder, "diagnostics.jsonl"), _ => { });
        var file = Path.Combine(folder, "delivery.jsonl");
        File.WriteAllText(file, "{partial");
        Assert.Throws<InvalidDataException>(() => journal.Write("s", Header, "secret words", DeliveryState.Enqueued));
        Assert.Equal("{partial", File.ReadAllText(file));
    }
}
