using System.Text;
using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Host.Core.Diagnostics;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Diagnose on another machine's row (#802, docs/protocol/v1-diagnostics.md): asked only of a host
// that offers it, read strictly, shown as plain text without anything the host should not have
// sent, and copied only after the person says they reviewed it. What the host serves, and to
// whom, is RemoteDiagnosticsTests' and RemoteAuthorizationTests'.
[Trait("Category", "Network")]
public sealed class MachineDiagnosisTests : IAsyncLifetime
{
    const string Canary = "ghp_CANARYdiagnosisAPPtoken0123456789abcdef";
    readonly FakeHost remoteHost = new();
    readonly LockedDispatcher ui = new();
    readonly FakeTimeProvider clock = new(Make.T0);
    HostConnection remote = null!;
    int asked;

    public Task InitializeAsync()
    {
        remoteHost.On(DiagnosticsProtocol.Get, _ => { Interlocked.Increment(ref asked); return JsonDocument.Parse(Served()).RootElement.Clone(); });
        return Task.CompletedTask;
    }

    async Task<MachineDiagnosisViewModel> ConnectedAsync(bool offers)
    {
        if (offers) { remoteHost.Capabilities.Add(DiagnosticsProtocol.Capability); }
        remoteHost.On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("remote-id", "Desk PC", "windows", "0.4.0"), [.. remoteHost.Capabilities]));
        remoteHost.Start();
        remote = new HostConnection(ct => LocalClient.ConnectAsync(remoteHost.Endpoint, TimeSpan.FromSeconds(1), ct), ui) { RetryDelay = TimeSpan.FromMilliseconds(100) };
        remote.Start();
        await Wait.UntilAsync(() => remote.Capabilities.Count > 0, "the other machine's hello");
        var machine = new Machine("remote-id", remote, new CardContext
        {
            Deliveries = new Deliveries(remote, ui, clock),
            Folds = new FoldStore(null),
            Capabilities = () => remote.Capabilities,
            Host = remote,
            Time = clock,
        });
        return new MachineDiagnosisViewModel(machine, clock);
    }

    public async Task DisposeAsync()
    {
        if (remote is not null) { await remote.DisposeAsync(); }
        await remoteHost.DisposeAsync();
    }

    // A served snapshot with what a hostile or broken host might add: an unknown property, a
    // version that is not one, and journal entries that do not read, each carrying the canary.
    static string Served()
    {
        var good = Encoding.UTF8.GetString(JournalCodec.Write(new JournalEntry(1, 7, Make.T0, JournalFamily.HostLifecycle, JournalKind.HostStarted,
            JournalOutcome.Ok, null, "0.4.0", null, [], null, JournalOmissions.None)));
        var bad = $$"""{"schema":1,"seq":8,"at":"2026-10-28T04:00:00.000Z","family":"HostLifecycle","kind":"HostStarted","outcome":"Failed","message":"{{Canary}}"}""";
        var strange = $$$"""{"schema":1,"seq":9,"at":"2026-10-28T04:00:00.000Z","family":"HostLifecycle","kind":"HostStarted","outcome":"Failed","exception":{"type":"System.{{{Canary}}}","frames":[]}}""";
        var badVersion = $$"""{"schema":1,"seq":11,"at":"2026-10-28T04:00:00.000Z","family":"HostLifecycle","kind":"HostStarted","outcome":"Failed","version":"1.2.3-{{Canary}}"}""";
        var crossed = Crossed();
        return $$"""
        {
          "schema": 1, "at": "2025-01-01T00:00:00Z", "token": "{{Canary}}",
          "host": { "version": "0.4.0 {{Canary}}", "dev": false, "startedAt": "2025-01-01T00:00:00Z", "uptimeSeconds": 60 },
          "lastRestart": { "at": "2025-01-01T00:00:00Z", "causeMissing": "notRecorded" },
          "relay": { "state": "connected", "since": "2025-01-01T00:00:00Z" },
          "holds": { "readOnly": "none", "discoveryHeld": [] },
          "readiness": { "checkedAt": null, "agents": [], "omitted": 0, "missing": "notStarted" },
          "journal": { "entries": [{{good}}, {{bad}}, {{strange}}, {{crossed}}, {{badVersion}}], "omittedByLimit": 0, "omittedBySize": 0, "invalid": 1,
            "evictedByCount": 0, "evictedByAge": 0, "evictedByBytes": 0, "rejectedOversized": 0, "rejectedMalformed": 0,
            "droppedOnLoad": 0, "loadFailed": false, "persistFailures": 0 }
        }
        """;
    }

    // A record as the host's own producer writes it: a real exception thrown inside Host.Core and
    // projected at capture, then, as another process's host might list them, a frame of the
    // host's executable and one carrying the canary that this app does not know.
    static string Crossed()
    {
        var produced = new DiagnosticsJournal(new FakeTimeProvider(Make.T0));
        try { produced.Read(-1, 0); }
        catch (ArgumentOutOfRangeException e) { Assert.True(produced.Record(JournalEvents.Failure(JournalKind.UnhandledException, e))); }
        var entry = Assert.Single(produced.Read(10, JournalContract.MaxTotalBytes).Entries);
        Assert.Contains(entry.Exception!.Frames, f => f.Type == "AgentBridge.Host.Core.Diagnostics.DiagnosticsJournal");
        var json = Encoding.UTF8.GetString(JournalCodec.Write(entry));
        Assert.Contains("\"frames\":[", json);
        return json.Replace("\"frames\":[", $$"""
            "frames":[{"type":"AgentBridge.Host.Program","method":"Main"},{"type":"System.{{Canary}}","method":"Read"},
            """.Trim());
    }
    [Fact]
    public async Task A_host_that_does_not_offer_diagnostics_is_never_asked_and_says_so()
    {
        var diagnosis = await ConnectedAsync(offers: false);

        await diagnosis.DiagnoseAsync();

        Assert.Equal(0, Volatile.Read(ref asked));
        Assert.False(diagnosis.HasText);
        Assert.Null(diagnosis.Note);
        Assert.False(diagnosis.Offered);
        Assert.True(diagnosis.NotOffered);
        Assert.Equal($"Can't be diagnosed from this device · update {Product.Name} on {diagnosis.Name}", diagnosis.UpdateHint);
        Assert.False(diagnosis.Copy(_ => throw new InvalidOperationException("nothing to copy")));
    }

    [Fact]
    public async Task What_the_host_sends_is_shown_without_anything_unread_and_copied_only_once_reviewed()
    {
        var diagnosis = await ConnectedAsync(offers: true);

        await diagnosis.DiagnoseAsync();

        var text = Assert.IsType<string>(diagnosis.Text);
        Assert.Equal(1, Volatile.Read(ref asked));
        // Times in Pacific, as the person reads everything here; the wire keeps them in UTC.
        Assert.StartsWith($"{Product.Name} diagnostics · {diagnosis.Name} · 2024-12-31 16:00:00 PST\n", text);
        Assert.DoesNotContain("#802", text);
        Assert.DoesNotContain("CANARY", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("host: unavailable (invalid)", text);
        Assert.Contains("last restart: 2024-12-31 16:00:00 PST, cause unavailable (notRecorded)", text);
        Assert.Contains("readiness: unavailable (notStarted)", text);
        // Three read; the host's own invalid one and the two that are not the contract (an unknown
        // property, a version that is not one) do not. Names only the host knows read as outside
        // the product, marked, and the record is kept.
        Assert.Contains("journal: 3 entries, newest first; 3 unreadable", text);
        Assert.Contains("#9 2026-10-27 21:00:00 PDT HostLifecycle.HostStarted Failed exception <unrecognized> omitted ExceptionTypeUnrecognized", text);
        Assert.Contains("#1 2026-10-27 21:00:00 PDT Crash.UnhandledException Failed exception System.ArgumentOutOfRangeException at <external>.<external> < AgentBridge.Host.Core.Diagnostics.DiagnosticsJournal.", text);
        Assert.Contains("omitted FrameUnrecognized", text);
        Assert.DoesNotContain("AgentBridge.Host.Program", text);
        Assert.Contains("#7 2026-10-27 21:00:00 PDT HostLifecycle.HostStarted Ok 0.4.0", text);

        // Asking again at once is not sent: the host would refuse it.
        await diagnosis.DiagnoseAsync();
        Assert.Equal(1, Volatile.Read(ref asked));

        string? copied = null;
        Assert.False(diagnosis.CanCopy);
        Assert.False(diagnosis.Copy(t => { copied = t; return true; }));
        Assert.Null(copied);
        diagnosis.Reviewed = true;
        Assert.True(diagnosis.Copy(t => { copied = t; return true; }));
        Assert.Equal(text, copied);
        Assert.Equal("Copied", diagnosis.CopyText);

        diagnosis.Close();
        Assert.False(diagnosis.HasText);
        Assert.False(diagnosis.Reviewed);
    }

    // As a host writes them, with ProtocolJson (which leaves nulls out): a host that knows little
    // yet, and one whose agent has no version and has handled no hook. Each reads, with what is not
    // known said as unavailable; and a field taken away, or replaced with text, still refuses it.
    [Fact]
    public void A_snapshot_written_as_a_host_writes_it_reads_whatever_it_does_not_know_yet()
    {
        var started = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var unknowing = Snapshot(started, new DiagnosticsReadiness(null, [], 0, DiagnosticsMissing.NotStarted));
        var noHook = Snapshot(started, new DiagnosticsReadiness(started, [new DiagnosticsAgent(DiagnosticsAgentId.Copilot, null,
            DiagnosticsCheck.Working, DiagnosticsCheck.Unknown, DiagnosticsCheck.Missing, DiagnosticsCheck.Working, DiagnosticsCheck.Working, null,
            DiagnosticsMissing.NotRecorded)], 0));

        var wire = JsonSerializer.SerializeToElement(unknowing, ProtocolJson.Options);
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("relay").GetProperty("since").ValueKind);
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("host").GetProperty("version").ValueKind);
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("holds").GetProperty("discoveryHeld").ValueKind);
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("holds").GetProperty("readOnly").ValueKind);
        Assert.Equal(JsonValueKind.Null, wire.GetProperty("readiness").GetProperty("checkedAt").ValueKind);
        var text = MachineDiagnosisViewModel.Format(Assert.IsType<DiagnosticsSnapshot>(        MachineDiagnosisViewModel.Read(wire)), "Desk PC");
                Assert.Contains("host: unavailable (invalid), started 2024-12-31 16:00:00 PST, up 5s", text);
        Assert.Contains("relay: connecting\n", text);
        Assert.Contains("read-only: unavailable (notStarted)\n", text);
        Assert.Contains("discovery held: unavailable (notStarted)", text);
        // Held kinds sent as null with no reason are not known, never none: only an empty list is.
        string Held(Action<System.Text.Json.Nodes.JsonNode> change) => MachineDiagnosisViewModel.Format(Assert.IsType<DiagnosticsSnapshot>(
            MachineDiagnosisViewModel.Read(Changed(wire, n => { n["holds"]!.AsObject().Remove("discoveryMissing"); change(n); }))), "Desk PC");
        Assert.Contains("discovery held: unavailable (unknown)\n", Held(_ => { }));
        Assert.Contains("discovery held: none\n", Held(n => n["holds"]!["discoveryHeld"] = new System.Text.Json.Nodes.JsonArray()));
        Assert.Contains("discovery held: claude\n", Held(n => n["holds"]!["discoveryHeld"] = new System.Text.Json.Nodes.JsonArray("claude")));
        // A kind from a newer host this app doesn't know is held too: not known alone, never none, and said beside known kinds.
        Assert.Contains("discovery held: unavailable (unknown)\n", Held(n => n["holds"]!["discoveryHeld"] = new System.Text.Json.Nodes.JsonArray("futureAgent")));
        Assert.Contains("discovery held: claude, unknown\n", Held(n => n["holds"]!["discoveryHeld"] = new System.Text.Json.Nodes.JsonArray("claude", "futureAgent")));
        Assert.Contains("readiness: unavailable (notStarted)", text);
        Assert.Contains("journal: unavailable (notConfigured)", text);

        var agentWire = JsonSerializer.SerializeToElement(noHook, ProtocolJson.Options);
        var agentText = MachineDiagnosisViewModel.Format(Assert.IsType<DiagnosticsSnapshot>(        MachineDiagnosisViewModel.Read(agentWire)), "Desk PC");
                Assert.Contains("readiness: checked 2024-12-31 16:00:00 PST", agentText);
        Assert.Contains("  copilot unavailable (notRecorded): installation working, versionCheck unknown, hooks missing, authentication working, activation working\n", agentText);
        Assert.DoesNotContain("last hook", agentText);

        Assert.Null(MachineDiagnosisViewModel.Read(Changed(wire, n => n["relay"]!.AsObject().Remove("since"))));
        Assert.Null(MachineDiagnosisViewModel.Read(Changed(wire, n => n["readiness"]!.AsObject().Remove("checkedAt"))));
        Assert.Null(MachineDiagnosisViewModel.Read(Changed(wire, n => n["holds"]!.AsObject().Remove("readOnly"))));
        Assert.Null(MachineDiagnosisViewModel.Read(Changed(agentWire, n => n["readiness"]!["agents"]![0]!.AsObject().Remove("version"))));
        Assert.Null(MachineDiagnosisViewModel.Read(Changed(wire, n => n["host"]!.AsObject().Remove("startedAt"))));
        Assert.Null(MachineDiagnosisViewModel.Read(Changed(wire, n => n["relay"]!["since"] = Canary)));
        Assert.Null(MachineDiagnosisViewModel.Read(Changed(wire, n => n["holds"]!["discoveryHeld"] = Canary)));
        // A list element sent as null, and a time that is not one, refuse it rather than throw.
        Assert.Null(MachineDiagnosisViewModel.Read(Changed(agentWire, n => n["readiness"]!["agents"]![0] = null)));
        Assert.Null(MachineDiagnosisViewModel.Read(Changed(wire, n => n["relay"]!["since"] = "2025-02-30T00:00:00Z")));
        Assert.Null(MachineDiagnosisViewModel.Read(Changed(wire, n => n["at"] = "yesterday")));
    }

    [Fact]
    public void Times_are_shown_in_Pacific_across_daylight_saving()
    {
        static string Header(DateTimeOffset at) => MachineDiagnosisViewModel.Format(
            Snapshot(at.AddSeconds(-5), new DiagnosticsReadiness(null, [], 0, DiagnosticsMissing.NotStarted)), "Desk PC").Split('\n')[0];

        Assert.Equal($"{Product.Name} diagnostics · Desk PC · 2026-07-04 05:00:00 PDT", Header(new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.Zero)));
        Assert.Equal($"{Product.Name} diagnostics · Desk PC · 2026-03-08 01:59:59 PST", Header(new DateTimeOffset(2026, 3, 8, 9, 59, 59, TimeSpan.Zero)));
        Assert.Equal($"{Product.Name} diagnostics · Desk PC · 2026-03-08 03:00:00 PDT", Header(new DateTimeOffset(2026, 3, 8, 10, 0, 0, TimeSpan.Zero)));
    }

    // A refusal is said in the person's words for the host's three reasons, and as couldn't be
    // reached for anything else, never with the host's text; the row is left ready to ask again.
    [Fact]
    public async Task A_refusal_is_said_in_plain_words_and_leaves_the_row_ready()
    {
        var reason = DiagnosticsProtocol.Refused;
        remoteHost.On(DiagnosticsProtocol.Get, _ => { Interlocked.Increment(ref asked); throw new InvalidOperationException(reason); });
        var diagnosis = await ConnectedAsync(offers: true);

        foreach (var (sent, said) in new[]
        {
            (DiagnosticsProtocol.Refused, $"Approve this device at {diagnosis.Name} to diagnose it"),
            (DiagnosticsProtocol.TooSoon, "Diagnosed moments ago · try again shortly"),
            (DiagnosticsProtocol.Unfinished, $"{diagnosis.Name} didn't answer in time · try again"),
            (Canary, $"{diagnosis.Name} couldn't be reached · try again"),
        })
        {
            reason = sent;
            clock.Advance(DiagnosticsProtocol.MinInterval);
            await diagnosis.DiagnoseAsync();
            Assert.Equal(said, diagnosis.Note);
            Assert.True(diagnosis.CanDiagnose);
            Assert.False(diagnosis.HasText);
        }
        Assert.Equal(4, Volatile.Read(ref asked));
    }

    // A host that sends an agent as null is not shown, and the row is not left asking.
    [Fact]
    public async Task A_snapshot_that_does_not_read_is_said_so_and_leaves_the_row_ready()
    {
        remoteHost.On(DiagnosticsProtocol.Get, _ =>
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(Served())!;
            node["readiness"] = System.Text.Json.Nodes.JsonNode.Parse("""{"checkedAt":null,"agents":[null],"omitted":0}""");
            return JsonSerializer.SerializeToElement(node);
        });
        var diagnosis = await ConnectedAsync(offers: true);

        await diagnosis.DiagnoseAsync();

        Assert.False(diagnosis.HasText);
        Assert.Equal($"{diagnosis.Name} sent diagnostics this app can't read", diagnosis.Note);
        Assert.True(diagnosis.CanDiagnose);
    }

    static DiagnosticsSnapshot Snapshot(DateTimeOffset started, DiagnosticsReadiness readiness) => new(DiagnosticsProtocol.Schema, started.AddSeconds(5),
        new DiagnosticsHost(null, false, started, 5, DiagnosticsMissing.Invalid),
        new DiagnosticsRestart(started, DiagnosticsMissing.NotRecorded),
        new DiagnosticsRelay(DiagnosticsRelayState.Connecting, null),
        new DiagnosticsHolds(null, null, DiagnosticsMissing.NotStarted, DiagnosticsMissing.NotStarted),
        readiness,
        new DiagnosticsJournalPart([], 0, 0, 0, 0, 0, 0, 0, 0, 0, false, 0, DiagnosticsMissing.NotConfigured));

    static JsonElement Changed(JsonElement wire, Action<System.Text.Json.Nodes.JsonNode> change)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(wire.GetRawText())!;
        change(node);
        return JsonSerializer.SerializeToElement(node);
    }
}
