using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The account's other machines in the app (pwa/js/fleet.js and its tests): each host the relay
// lists becomes a machine with cards of its own, everything done on a card goes to that card's
// machine, and this machine's own host is never listed twice.
[Trait("Category", "Network")]
public sealed class FleetTests : IAsyncLifetime
{
    readonly FakeHost localHost = new();
    readonly FakeHost remoteHost = new();
    readonly LockedDispatcher ui = new();
    readonly FakeFleetLink link = new();
    readonly FakeTimeProvider clock = new(Make.T0);
    readonly List<ToastNotice> shown = [];
    readonly List<string> withdrawn = [];
    // The usage this app keeps between runs (#172).
    readonly string cacheFile = Path.Combine(Path.GetTempPath(), "abuc-" + Guid.NewGuid().ToString("N")[..8] + ".json");
    HostConnection local = null!;
    MainViewModel main = null!;
    Fleet fleet = null!;

    sealed class Sink(List<ToastNotice> shown, List<string>? withdrawn = null) : IToastSink
    {
        public void Show(ToastNotice notice) { lock (shown) { shown.Add(notice); } }
        public void Withdraw(string tag) { if (withdrawn is not null) { lock (withdrawn) { withdrawn.Add(tag); } } }
    }

    public Task InitializeAsync()
    {
        localHost.Sessions.Add(Make.Session("here", updated: Make.T0));
        localHost.Start();
        remoteHost.On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("remote-id", "Build server", "windows", "0.1.0", Instance: "run-7"),
            [Capabilities.SessionsRead, Capabilities.SessionsAnswer, Capabilities.SessionsReply, Capabilities.SessionsAttach, Capabilities.SessionsStop, UsageProtocol.Capability]));
        remoteHost.Sessions.Add(Make.Session("there", question: Make.Choice(), updated: Make.T0.AddMinutes(1)));
        remoteHost.Start();
        link.Endpoints["remote-id"] = remoteHost.Endpoint;
        local = localHost.Connect(ui);
        main = new MainViewModel(Build(Machine.LocalKey, local), new UsageViewModel(clock, new UsageCache(cacheFile, clock)));
        fleet = new Fleet(link, main, ui, h => Build(h.Id, new HostConnection(ct => link.OpenAsync(h.Id, ct), ui) { RetryDelay = TimeSpan.FromMilliseconds(100), RefusedRetryDelay = refusedRetryDelay }), clock);
        return Task.CompletedTask;
    }

    // How long the other machines wait after a refusal; set before they are listed.
    TimeSpan? refusedRetryDelay;

    Machine Build(string key, HostConnection host)
    {
        Machine? m = null;
        var cards = new CardContext
        {
            Deliveries = new Deliveries(host, ui, clock),
            Folds = new FoldStore(null),
            Capabilities = () => host.Capabilities,
            MachineName = () => m?.Name,
            ShowMachine = () => main?.ShowMachines == true,
            Host = host,
            Time = clock,
        };
        var toasts = new ToastPlanner(new Sink(shown, withdrawn), clock) { Capabilities = () => host.Capabilities, MachineName = () => m?.Name, MachineKey = key == Machine.LocalKey ? null : key };
        return m = new Machine(key, host, cards, toasts);
    }

    public async Task DisposeAsync()
    {
        await fleet.DisposeAsync();
        await local.DisposeAsync();
        await localHost.DisposeAsync();
        await remoteHost.DisposeAsync();
        File.Delete(cacheFile);
    }

    SessionCardViewModel? Card(string id)
    {
        SessionCardViewModel? found = null;
        ui.Post(() => found = main.AllCards.FirstOrDefault(c => c.Id == id));
        return found;
    }

    async Task BothMachinesAsync()
    {
        await Wait.UntilAsync(() => local.State == LinkState.Connected, "this machine's host");
        // Connected comes with the subscribe's result, ahead of the snapshot it sends. Each
        // machine's first list goes to the top once it is whole (ui.md §6.1), so this machine's
        // must be whole before the other's is asked for, as the order these tests expect
        // (["there", "here"]) says. A slow runner once let the other machine's finish first,
        // and "here" went on top (#414).
        await Wait.UntilAsync(() => local.Sessions.Complete, "this machine's whole list");
        // The relay lists this machine's own host too: it is reached over the pipe, not again.
        link.Set(FleetState.Connected, ("host-id", "Test PC"), ("remote-id", "Build server"));
        await Wait.UntilAsync(() => Card("here") is not null && Card("there") is not null, "both machines' cards");
        // Its rows are acted on only once its snapshot has confirmed them (#269).
        await Wait.UntilAsync(() => main.MachineOf("remote-id")?.Current == true, "the other machine's sessions confirmed");
        // And each connection all the way in: a card arrives before its machine's list is complete
        // (which drops a toast's open for a session not on it) and before its connection says
        // Connected (the machines page's "Online"), and under load a test could get between (#101).
        await Wait.UntilAsync(() => main.AllMachines.Count == 2 && main.AllMachines.All(m => m.Host.State == LinkState.Connected && m.Host.Sessions.Complete), "both machines' lists");
    }

    Machine Remote() => TestUi.Read(() => main.MachineOf("remote-id")!);

    // The other machine's host leaves the relay's presence, as its relay link drops; and comes back.
    void Leave() => link.Set(FleetState.Connected, ("host-id", "Test PC"));
    void Return() => link.Set(FleetState.Connected, ("host-id", "Test PC"), ("remote-id", "Build server"));

    async Task GoneAsync()
    {
        Leave();
        await Wait.UntilAsync(() => Card("there")?.Away == true, "the other machine's rows marked offline");
    }

    [Fact]
    public async Task Each_other_machine_the_relay_lists_gets_its_cards_and_this_machines_own_host_is_not_listed_twice()
    {
        await BothMachinesAsync();

        Assert.Equal(2, main.AllMachines.Count);
        Assert.Equal(["All machines", "Test PC (this machine) · 1 session", "Build server · 1 session"], main.Machines.Select(m => m.Label));
        Assert.True(main.ShowMachines);
        Assert.StartsWith("Build server · Copilot", Card("there")!.Meta);
        Assert.StartsWith("Test PC · Copilot", Card("here")!.Meta);
        Assert.Equal(1, main.Waiting);
    }

    // #293: each machine's Discovery held banner is its own, and dismissing one hides only it.
    [Fact]
    public async Task Dismissing_one_machines_discovery_held_banner_leaves_every_other_machines_banner_shown()
    {
        await BothMachinesAsync();
        string[] Held() => TestUi.Read<string[]>(() => [.. main.HeldNotices.Select(n => n.Text)]);
        await localHost.SendAsync(Events.DiscoveryChanged, new DiscoveryChanged(new DiscoveryHeld("the claude processes could not be listed", [AgentKind.Claude])));
        await remoteHost.SendAsync(Events.DiscoveryChanged, new DiscoveryChanged(new DiscoveryHeld("an unrecognised copilot process (pid 4120)", [AgentKind.Copilot])));
        await Wait.UntilAsync(() => Held().Length == 2, "both machines' banners");
        Assert.StartsWith("Test PC: Discovery held: the claude processes", Held()[0]);
        Assert.StartsWith("Build server: Discovery held: an unrecognised copilot process", Held()[1]);
        Assert.Equal("Dismiss Discovery held on Build server", TestUi.Read(() => main.HeldNotices[1].DismissName));

        TestUi.Read(() => { main.Dismiss(main.HeldNotices.Single(n => n.Machine == "remote-id")); return true; });
        Assert.StartsWith("Test PC: Discovery held:", Assert.Single(Held()));

        // The other machine's hold changing brings neither back but its own.
        await localHost.SendAsync(Events.DiscoveryChanged, new DiscoveryChanged(new DiscoveryHeld("a claude registration changed while it was read", [AgentKind.Claude])));
        await Wait.UntilAsync(() => Held().SingleOrDefault()?.Contains("a claude registration", StringComparison.Ordinal) == true, "this machine's new hold");
        Assert.Single(Held());

        // Dismissing this machine's hides it too, and leaves the other's dismissal as it was.
        TestUi.Read(() => { main.Dismiss(main.HeldNotices.Single()); return true; });
        Assert.Empty(Held());
        await remoteHost.SendAsync(Events.DiscoveryChanged, new DiscoveryChanged(new DiscoveryHeld("an unrecognised copilot process (pid 5120)", [AgentKind.Copilot])));
        await Wait.UntilAsync(() => Held().Length == 1, "the other machine's banner back for its new hold");
        Assert.StartsWith("Build server: Discovery held: an unrecognised copilot process (pid 5120)", Held()[0]);
    }
    // a refusal of the sessions the app asks for next. Asked again every 5 s for hours (#602).
    async Task RefusedAsync()
    {
        const string notApproved = "This device is not an explicitly approved operator; approve its public device ID locally.";
        remoteHost.On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("remote-id", "Build server", "linux", "0.1.0", ReadOnly: notApproved), []));
        remoteHost.On(Ops.SessionsSubscribe, _ => throw new InvalidOperationException(notApproved));
        refusedRetryDelay = TimeSpan.FromMinutes(5);
        await Wait.UntilAsync(() => local.State == LinkState.Connected, "this machine's host");
        link.Set(FleetState.Connected, ("host-id", "Test PC"), ("remote-id", "Build server"));
        await Wait.UntilAsync(() => TestUi.Read(() => main.MachineOf("remote-id"))?.Host.State == LinkState.Refused, "the refusal");
        // Several times the ordinary retry delay (100 ms here), which asked again each time before.
        await Task.Delay(TimeSpan.FromMilliseconds(600));
        Assert.Single(remoteHost.Sent(Ops.Hello));
    }

    [Fact]
    public async Task A_machine_whose_host_refused_this_device_is_asked_again_at_once_when_the_accounts_machines_change()
    {
        await RefusedAsync();

        link.Known = [new KnownMachine("laptop-id", "Laptop", "host", Make.T0)];
        link.Raise();

        await Wait.UntilAsync(() => remoteHost.Sent(Ops.Hello).Count == 2, "the host asked again");
    }

    [Fact]
    public async Task A_machine_whose_host_refused_this_device_is_asked_again_at_once_when_it_is_opened()
    {
        await RefusedAsync();

        ui.Post(() => main.SelectedMachine = main.Machines.Single(m => m.Key == "remote-id"));

        await Wait.UntilAsync(() => remoteHost.Sent(Ops.Hello).Count == 2, "the host asked again");
    }

    [Fact]
    public async Task Another_machines_questions_listed_as_its_list_is_read_or_read_again_on_reconnecting_are_not_arrivals()
    {
        var lane = new System.Collections.Concurrent.ConcurrentQueue<(string Id, bool Arriving)>();
        ui.Post(() => main.NeedsYou.CollectionChanged += (_, e) =>
        {
            foreach (NeedsYouEntry n in e.NewItems ?? Array.Empty<object>()) { lane.Enqueue((n.Card.Id, main.IsArriving(n))); }
        });
        await BothMachinesAsync();
        Assert.Contains(lane, a => a.Id == "there");

        await GoneAsync();
        Return();
        await Wait.UntilAsync(() => Remote().Current && TestUi.Read(() => main.NeedsYou.Any(e => e.Card.Id == "there")), "the other machine back, its question confirmed");
        Assert.True(lane.Count(a => a.Id == "there") >= 2);
        Assert.DoesNotContain(lane, a => a.Arriving);
    }

    [Fact]
    public async Task An_answer_on_another_machines_card_goes_to_that_machines_host_only()
    {
        remoteHost.On(Ops.SessionAnswer, _ => new AnswerResult(AnswerOutcome.Delivered));
        await BothMachinesAsync();
        var q = Card("there")!.Question!;

        ui.Post(() => q.Choose(q.Fields[0].Options.Single(o => o.Value == "sqlite")));

        await Wait.UntilAsync(() => q.StatusText == "Delivered. The session has your answer.", "delivered");
        Assert.Equal("there", Assert.Single(remoteHost.Sent(Ops.SessionAnswer)).GetProperty("id").GetString());
        Assert.Empty(localHost.Sent(Ops.SessionAnswer));
    }

    [Fact]
    public async Task Needs_you_lists_every_machines_questions_oldest_first_whatever_the_picker_shows_and_an_approval_goes_to_its_own_machine()
    {
        localHost.On(Ops.SessionAnswer, _ => new AnswerResult(AnswerOutcome.Delivered));
        await BothMachinesAsync();
        await localHost.SendAsync(Events.SessionUpsert, Make.Session("here", question: Make.Approval() with { AskedAt = Make.T0.AddMinutes(2) }, updated: Make.T0.AddMinutes(2)));
        await Wait.UntilAsync(() => main.NeedsYou.Count == 2, "both machines' questions");
        ui.Post(() => main.SelectedMachine = main.Machines.Single(m => m.Key == "remote-id"));

        TestUi.Read(() =>
        {
            Assert.Equal(["there", "here"], main.NeedsYou.Select(e => e.Card.Id));
            Assert.StartsWith("Build server · Copilot · ", main.NeedsYou[0].Meta);
            Assert.StartsWith("Test PC · Copilot · ", main.NeedsYou[1].Meta);
            // Two machines: each control says which machine as well as which session.
            Assert.EndsWith(" on Test PC", main.NeedsYou[1].ApproveName);
            Assert.EndsWith(" on Test PC", main.NeedsYou[1].DenyName);
            Assert.EndsWith(" on Test PC", main.NeedsYou[1].ResendName);
            Assert.EndsWith(" on Build server: open the session", main.NeedsYou[0].OpenName);
            Assert.Equal(2, main.Waiting);
            return true;
        });

        ui.Post(() => main.NeedsYou[1].Approve());
        await Wait.UntilAsync(() => localHost.Sent(Ops.SessionAnswer).Count == 1, "the approval");
        Assert.Equal("here", localHost.Sent(Ops.SessionAnswer)[0].GetProperty("id").GetString());
        Assert.Empty(remoteHost.Sent(Ops.SessionAnswer));
    }

    [Fact]
    public async Task Search_finds_the_same_sessions_in_the_same_order_whichever_machine_the_tabs_show_newest_first()
    {
        await BothMachinesAsync();
        await localHost.SendAsync(Events.SessionUpsert, Make.Session("here-deploy", title: "Deploy notes", updated: Make.T0.AddMinutes(1)));
        await remoteHost.SendAsync(Events.SessionUpsert, Make.Session("there-deploy", title: "Deploy notes", updated: Make.T0.AddMinutes(5)));
        await Wait.UntilAsync(() => Card("here-deploy") is not null && Card("there-deploy") is not null, "both deploy sessions");

        List<string> Found(string? key)
        {
            ui.Post(() => main.SelectedMachine = main.Machines.Single(m => m.Key == key));
            // The newest match, whatever the tabs left listed: a cap once dropped a newer one hidden by them.
            return TestUi.Read(() => main.Search("deploy", most: 1).Select(h => h.SessionId).ToList());
        }

        var keys = TestUi.Read(() => main.Machines.Select(m => m.Key).ToList());
        Assert.All(keys, key => Assert.Equal(["there-deploy"], Found(key)));
    }
    [Fact]
    public async Task A_relay_that_drops_for_a_moment_keeps_every_machines_cards_and_one_gone_fifteen_minutes_takes_its_cards_and_tab()
    {
        await BothMachinesAsync();
        ui.Post(() => main.SelectedMachine = main.Machines.Single(m => m.Key == "remote-id"));

        // The relay connection itself went: nothing says the machine has. The fleet reads the link
        // on its Changed, posted to this test's UI turn and run there, so it is done when Set returns.
        link.Set(FleetState.Connecting);
        Assert.NotNull(Card("there"));

        link.Set(FleetState.Connected, ("host-id", "Test PC"));
        await Wait.UntilAsync(() => Remote().Offline, "the other machine to be offline");
        clock.Advance(Fleet.OfflineKeep);
        await Wait.UntilAsync(() => Card("there") is null, "the other machine's cards to go");
        Assert.Single(main.AllMachines);
        Assert.False(main.ShowMachines);
        Assert.Null(main.SelectedMachine!.Key);
        Assert.Equal(["here"], main.Sessions.Select(c => c.Id));
    }

    [Fact]
    public async Task A_machine_whose_channel_outlived_its_absence_is_asked_for_a_new_snapshot_before_its_rows_can_be_acted_on()
    {
        await BothMachinesAsync();
        var question = Card("there")!.Question!;
        var subscribed = remoteHost.Sent(Ops.SessionsSubscribe).Count;

        // Gone from presence and back, its channel spared (the hub's presence grace) throughout.
        link.Quietly(FleetState.Connected, ("host-id", "Test PC"));
        link.Raise();
        await Wait.UntilAsync(() => Card("there")!.Away, "its rows marked offline");
        Assert.False(TestUi.Read(() => question.CanAnswer));
        link.Quietly(FleetState.Connected, ("host-id", "Test PC"), ("remote-id", "Build server"));
        link.Raise();

        await Wait.UntilAsync(() => Remote().Current && question.CanAnswer, "its rows confirmed by a new snapshot");
        Assert.Equal(subscribed + 1, remoteHost.Sent(Ops.SessionsSubscribe).Count);
    }

    [Fact]
    public async Task One_of_two_machines_that_leaves_the_relay_for_twenty_seconds_keeps_its_rows_marked_offline_and_the_other_is_untouched()
    {
        await BothMachinesAsync();
        var there = Card("there")!;
        var here = Card("here")!;
        var hereLine = TestUi.Read(() => here.StatusText);
        Assert.Equal(["there", "here"], TestUi.Read(() => main.Sessions.Select(c => c.Id).ToList()));

        Leave();
        clock.Advance(TimeSpan.FromSeconds(20));
        await Wait.UntilAsync(() => there.Away, "its rows marked offline");

        TestUi.Read(() =>
        {
            Assert.Same(there, Card("there"));
            Assert.Equal(2, main.AllMachines.Count);
            Assert.Equal(("Offline · last seen 20s ago", StatusGlyph.Failed, StatusTone.Muted), (there.StatusText, there.Glyph, there.StatusTone));
            Assert.Equal("Offline · Build server", there.RowFacts);
            Assert.Contains("offline · last seen 20s ago, Build server", there.GlyphLabel);
            Assert.Equal("Build server · offline", main.Machines.Single(m => m.Key == "remote-id").Label);
            Assert.False(here.Away);
            Assert.Equal(hereLine, here.StatusText);
            Assert.Equal(["there", "here"], main.Sessions.Select(c => c.Id));
            return true;
        });
    }

    [Fact]
    public async Task Nothing_on_an_offline_machines_rows_can_be_answered_replied_to_ended_or_counted_as_waiting_on_you()
    {
        remoteHost.On(Ops.SessionAnswer, _ => new AnswerResult(AnswerOutcome.Delivered));
        remoteHost.On(Ops.SessionReply, _ => new ReplyResult(AnswerOutcome.Delivered));
        remoteHost.On(Ops.SessionStop, _ => new StopResult(StopOutcome.Ended));
        await BothMachinesAsync();
        remoteHost.Sessions.Add(Make.Session("there-idle", updated: Make.T0.AddMinutes(2)));
        await remoteHost.SendAsync(Events.SessionUpsert, remoteHost.Sessions[^1]);
        await Wait.UntilAsync(() => Card("there-idle")?.Reply is not null && Card("there-idle")?.End is not null, "an idle session to reply to");
        var question = Card("there")!.Question!;
        var idle = Card("there-idle")!;
        ui.Post(() => idle.Reply!.Text = "carry on");
        Assert.True(TestUi.Read(() => question.CanAnswer && idle.Reply!.CanSend && idle.Reply.CanAttach && idle.End!.CanPress && main.Waiting == 1));

        await GoneAsync();

        TestUi.Read(() =>
        {
            Assert.False(question.CanAnswer);
            Assert.Equal($"Waiting for the {Product.HostName} on Build server. Answer once it is back.", question.StatusText);
            Assert.False(idle.Reply!.CanSend);
            Assert.False(idle.Reply.CanAttach);
            Assert.False(idle.End!.CanPress);
            Assert.Equal(0, main.Waiting);
            Assert.Empty(main.NeedsYou);
            return true;
        });
        ui.Post(() =>
        {
            question.Choose(question.Fields[0].Options.Single(o => o.Value == "sqlite"));
            idle.Reply!.Send();
            idle.End!.Press();
        });
        // Nor straight through its deliveries, as a toast's answer goes.
        var held = TestUi.Read(() => Remote().Deliveries.Submit("answer|there|q1", Ops.SessionAnswer, r => new AnswerRequest { Id = "there", Question = "q1", Request = r, Text = "sqlite" }, _ => true));
        Assert.Equal((DeliveryState.Done, Deliveries.Held), (held.State, held.Outcome));
        var sink = new Recorder();
        var responder = new ToastResponder(key => main.MachineOf(key)?.Deliveries, sink, (_, _) => { }) { MachineWait = TimeSpan.FromMilliseconds(300) };
        // As this build's toast draws it (#377): an older toast's button only opens its question,
        // so without the version it would never reach the offline refusal this checks.
        var button = $"action=answer&machine=remote-id&session=there&question=q1&kind=option&field=answer&value=sqlite&{ToastXml.AnswerVersionKey}={ToastXml.AnswerVersion}";
        await responder.HandleAsync(button, new Dictionary<string, string>());
        Assert.StartsWith("Not sent: the machine is not connected now", ToastText(sink.Shown.Last().Xml));
        // An older build's button for the same answer opens the question and sends nothing.
        var shownBefore = sink.Shown.Count;
        await responder.HandleAsync(button.Replace($"&{ToastXml.AnswerVersionKey}={ToastXml.AnswerVersion}", ""), new Dictionary<string, string>());
        Assert.Equal(shownBefore, sink.Shown.Count);

        await Task.Delay(300);
        Assert.Empty(remoteHost.Sent(Ops.SessionAnswer));
        Assert.Empty(remoteHost.Sent(Ops.SessionReply));
        Assert.Empty(remoteHost.Sent(Ops.SessionStop));
    }

    static string ToastText(string xml) => System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Matches(xml, "<text[^>]*>([^<]*)</text>").Last().Groups[1].Value);

    [Fact]
    public async Task An_offline_machine_back_again_is_reconciled_by_its_new_snapshot_and_its_rows_are_acted_on_only_once_that_snapshot_is_whole()
    {
        remoteHost.On(Ops.SessionAnswer, _ => new AnswerResult(AnswerOutcome.Delivered));
        await BothMachinesAsync();
        remoteHost.Sessions.Add(Make.Session("there-ended", updated: Make.T0.AddMinutes(2)));
        await remoteHost.SendAsync(Events.SessionUpsert, remoteHost.Sessions[^1]);
        await Wait.UntilAsync(() => Card("there-ended") is not null, "a second session there");
        var there = Card("there")!;
        var question = there.Question!;
        await GoneAsync();

        // It ends while the machine is away; the other is still asking. The new snapshot is held
        // before its end.
        remoteHost.Sessions.RemoveAll(s => s.Id == "there-ended");
        var synced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        remoteHost.HoldSynced = synced;
        Return();
        await Wait.UntilAsync(() => Remote().Host.State == LinkState.Connected && !there.Away, "the machine connected again");

        TestUi.Read(() =>
        {
            Assert.False(Remote().Current);
            Assert.False(question.CanAnswer);
            Assert.Equal(0, main.Waiting);
            Assert.NotNull(Card("there-ended"));
            Assert.Single(main.Sessions, c => c.Id == "there");
            return true;
        });
        var early = TestUi.Read(() => Remote().Deliveries.Submit(question.Key, Ops.SessionAnswer, r => new AnswerRequest { Id = "there", Question = "q1", Request = r, Text = "sqlite" }, _ => true));
        Assert.Equal(Deliveries.Held, early.Outcome);

        synced.SetResult();
        await Wait.UntilAsync(() => Card("there-ended") is null, "the session that ended to go");
        await Wait.UntilAsync(() => question.CanAnswer && main.Waiting == 1, "the question answerable again");
        Assert.Same(there, Card("there"));
        Assert.Equal(["there", "here"], main.Sessions.Select(c => c.Id));
        Assert.Contains(main.Ended, e => e.Title == "A session");

        ui.Post(() => question.Choose(question.Fields[0].Options.Single(o => o.Value == "sqlite")));
        await Wait.UntilAsync(() => question.StatusText == "Delivered. The session has your answer.", "delivered");
        Assert.Single(remoteHost.Sent(Ops.SessionAnswer));
    }

    [Fact]
    public async Task A_gap_in_another_machines_events_holds_its_rows_until_the_snapshot_its_recovery_asks_for_is_whole()
    {
        remoteHost.On(Ops.SessionAnswer, _ => new AnswerResult(AnswerOutcome.Delivered));
        await BothMachinesAsync();
        var question = Card("there")!.Question!;
        Assert.True(TestUi.Read(() => question.CanAnswer && main.Waiting == 1));
        // The recovery reads usage again before it subscribes; held there, the moment between the
        // gap and the new snapshot beginning (which the rows already waited out) is seen.
        using var usage = new ManualResetEventSlim();
        remoteHost.On(UsageProtocol.Get, _ => { usage.Wait(FakeHost.Patience); return UsageReport.Empty; });

        foreach (var ch in remoteHost.Channels.OfType<LocalChannel>().Where(c => c.IsOpen).Distinct()) { ch.SkipEventSeq(3); }
        await remoteHost.SendAsync(Events.SessionUpsert, remoteHost.Sessions[0]);
        await Wait.UntilAsync(() => remoteHost.Sent(UsageProtocol.Get).Count > 0, "the recovery under way");

        TestUi.Read(() =>
        {
            Assert.False(Remote().Current);
            Assert.False(question.CanAnswer);
            Assert.Equal(0, main.Waiting);
            return true;
        });
        var early = TestUi.Read(() => Remote().Deliveries.Submit(question.Key, Ops.SessionAnswer, r => new AnswerRequest { Id = "there", Question = "q1", Request = r, Text = "sqlite" }, _ => true));
        Assert.Equal(Deliveries.Held, early.Outcome);

        usage.Set();
        await Wait.UntilAsync(() => Remote().Current && question.CanAnswer && main.Waiting == 1, "the rows confirmed by the new snapshot");
        Assert.Empty(remoteHost.Sent(Ops.SessionAnswer));
    }

    [Fact]
    public async Task A_question_that_ended_while_its_machine_was_offline_goes_without_ever_being_answerable()
    {
        await BothMachinesAsync();
        var there = Card("there")!;
        var question = there.Question!;
        await GoneAsync();
        var answerable = false;
        ui.Post(() =>
        {
            question.PropertyChanged += (_, _) => answerable |= question.CanAnswer;
            main.NeedsYou.CollectionChanged += (_, _) => answerable |= main.NeedsYou.Count > 0;
        });

        remoteHost.Sessions[0] = Make.Session("there", updated: Make.T0.AddMinutes(3));
        Return();

        await Wait.UntilAsync(() => Remote().Current && there.Question is null, "the session, no longer asking");
        Assert.False(answerable);
        // The question's model, let go of by the card, answers nothing either.
        Assert.False(TestUi.Read(() => question.CanAnswer));
        ui.Post(() => question.Choose(question.Fields[0].Options.Single(o => o.Value == "sqlite")));
        Assert.Equal(0, TestUi.Read(() => main.Waiting));
        // An answer sent would have been written as it was chosen: once the other machine's host has
        // everything sent before this, none was.
        await Remote().Host.CallAsync(FakeHost.Barrier);
        Assert.Empty(remoteHost.Sent(Ops.SessionAnswer));
    }

    [Fact]
    public async Task The_open_session_the_machine_tab_your_order_and_pins_survive_a_machine_leaving_and_coming_back()
    {
        await BothMachinesAsync();
        remoteHost.Sessions.Add(Make.Session("there-2", updated: Make.T0.AddMinutes(2)));
        await remoteHost.SendAsync(Events.SessionUpsert, remoteHost.Sessions[^1]);
        await Wait.UntilAsync(() => Card("there-2") is not null, "a second session there");
        ui.Post(() =>
        {
            main.SelectedMachine = main.Machines.Single(m => m.Key == "remote-id");
            main.SetPinned(Card("there")!, true);
            main.Selected = Card("there-2");
        });
        var order = TestUi.Read(() => main.Sessions.Select(c => c.Id).ToList());
        var open = Card("there-2")!;

        await GoneAsync();
        TestUi.Read(() =>
        {
            Assert.Equal(("remote-id", open), (main.SelectedMachine!.Key, main.Selected));
            Assert.Equal(order, main.Sessions.Select(c => c.Id));
            Assert.True(Card("there")!.Pinned);
            return true;
        });

        Return();
        await Wait.UntilAsync(() => Remote().Current, "the machine back, its sessions confirmed");
        TestUi.Read(() =>
        {
            Assert.Equal(("remote-id", open), (main.SelectedMachine!.Key, main.Selected));
            Assert.Same(open, Card("there-2"));
            Assert.Equal(order, main.Sessions.Select(c => c.Id));
            Assert.True(Card("there")!.Pinned);
            return true;
        });
    }

    [Theory]
    [InlineData("forgotten")]
    [InlineData("signed out")]
    [InlineData("refused")]
    [InlineData("another account")]
    public async Task An_offline_machine_still_goes_with_its_rows_when_it_is_forgotten_signed_out_refused_or_on_another_account(string how)
    {
        link.Known = [];
        await BothMachinesAsync();
        link.Known = [new KnownMachine("remote-id", "Build server", "host", Make.T0)];
        await GoneAsync();

        switch (how)
        {
            case "forgotten": Assert.Null(await link.ForgetAsync("remote-id", default)); break;
            case "signed out": link.Set(FleetState.SignedOut); break;
            case "refused": link.Set(FleetState.Refused); break;
            default: link.Switch(); break;
        }

        await Wait.UntilAsync(() => Card("there") is null, "its rows to go");
        Assert.Single(main.AllMachines);
    }

    [Fact]
    public async Task An_offline_machines_rows_go_after_fifteen_minutes_and_it_stays_listed_with_the_offline_machines()
    {
        link.Known = [];
        await BothMachinesAsync();
        link.Known = [new KnownMachine("remote-id", "Build server", "host", Make.T0)];
        await GoneAsync();
        // Listed once, as an offline machine with its Forget, while its rows are kept.
        Assert.Equal(["local", "offline:remote-id"], TestUi.Read(() => main.MachineRows.Select(r => r.Key).ToList()));

        clock.Advance(Fleet.OfflineKeep - TimeSpan.FromSeconds(1));
        Assert.NotNull(Card("there"));
        clock.Advance(TimeSpan.FromSeconds(1));

        await Wait.UntilAsync(() => Card("there") is null, "its rows to go");
        Assert.Single(main.AllMachines);
        Assert.Equal(["local", "offline:remote-id"], TestUi.Read(() => main.MachineRows.Select(r => r.Key).ToList()));
    }

    [Fact]
    public async Task An_offline_machines_question_toast_goes_and_comes_back_quietly_once_its_snapshot_confirms_the_question()
    {
        await BothMachinesAsync();
        await Wait.UntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            main.Tick();
            lock (shown) { return shown.Count == 1; }
        }, "the question's toast");
        Assert.False(shown[0].Quiet);

        await GoneAsync();
        lock (withdrawn) { Assert.Contains(ToastPlanner.Tag("there", "remote-id"), withdrawn); }
        for (var i = 0; i < 5; i++) { clock.Advance(TimeSpan.FromSeconds(1)); ui.Post(main.Tick); }
        Assert.Single(shown);

        Return();
        await Wait.UntilAsync(() => Remote().Current, "the machine back, its sessions confirmed");
        await Wait.UntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            main.Tick();
            lock (shown) { return shown.Count == 2; }
        }, "the question's toast again");
        Assert.True(shown[1].Quiet);
    }

    [Fact]
    public async Task With_more_than_one_machine_a_row_names_its_machine_and_the_tabs_name_each_machine_short()
    {
        await BothMachinesAsync();
        var there = Card("there")!;

        Assert.Equal("Build server", TestUi.Read(() => there.RowPlace));
        Assert.EndsWith(" · Build server", TestUi.Read(() => there.RowFacts));
        Assert.StartsWith("Build server · ", TestUi.Read(() => there.HeaderPlace));
        Assert.Equal(["All", "Test PC", "Build server"], TestUi.Read(() => main.Machines.Select(m => m.Short).ToList()));
    }

    [Fact]
    public async Task A_machine_tab_that_hides_the_open_session_closes_it_and_one_that_still_lists_it_keeps_it_open()
    {
        await BothMachinesAsync();
        ui.Post(() => main.Selected = main.AllCards.Single(c => c.Id == "there"));

        ui.Post(() => main.SelectedMachine = main.Machines.Single(m => m.Key == "remote-id"));
        Assert.Equal("there", TestUi.Read(() => main.Selected?.Id));
        ui.Post(() => main.SelectedMachine = main.Machines[0]);
        Assert.Equal("there", TestUi.Read(() => main.Selected?.Id));

        ui.Post(() => main.SelectedMachine = main.Machines.Single(m => m.Key is not null && m.Key != "remote-id"));
        Assert.Null(TestUi.Read(() => main.Selected));
        Assert.False(TestUi.Read(() => Card("there")!.Selected));
    }

    [Theory]
    [InlineData(FleetState.SignedOut)]
    [InlineData(FleetState.Refused)]
    [InlineData(FleetState.Elsewhere)]
    public async Task Without_an_account_to_find_them_through_or_with_another_copy_showing_them_the_other_machines_go(FleetState state)
    {
        await BothMachinesAsync();

        link.Set(state);

        await Wait.UntilAsync(() => Card("there") is null, "the other machine's cards to go");
        Assert.Equal(["here"], main.AllCards.Select(c => c.Id));
    }

    [Fact]
    public async Task Another_account_takes_the_last_ones_machines_away_even_before_it_connects()
    {
        await BothMachinesAsync();

        link.Switch();

        await Wait.UntilAsync(() => Card("there") is null, "the last account's machine to go");
        Assert.Single(main.AllMachines);
    }

    [Fact]
    public async Task A_switch_between_reading_the_hosts_and_the_account_is_still_seen_on_the_next_pass()
    {
        await BothMachinesAsync();

        // This pass reads the last account's hosts, and the account changes right after.
        link.AfterHostsRead = () => link.Switch(announce: false);
        link.Raise();
        await Wait.UntilAsync(() => link.AfterHostsRead is null, "the pass");
        link.Raise();

        await Wait.UntilAsync(() => Card("there") is null, "the last account's machine to go");
    }

    [Fact]
    public async Task The_fleets_note_is_shown_as_soon_as_the_fleet_is_attached()
    {
        await Wait.UntilAsync(() => local.State == LinkState.Connected, "this machine's host");
        var other = new MainViewModel(Build(Machine.LocalKey, local), new UsageViewModel(clock));
        var raised = new List<string?>();
        other.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        var signedOut = new FakeFleetLink();
        signedOut.Set(FleetState.SignedOut);

        await using var late = new Fleet(signedOut, other, ui, h => throw new InvalidOperationException("no machines"));

        // Read under the UI gate: posted actions from the host link still raise into it.
        var seen = TestUi.Read(() => raised.ToList());
        Assert.Contains(nameof(MainViewModel.FleetNote), seen);
        Assert.Contains(nameof(MainViewModel.HasFleetNote), seen);
    }

    [Fact]
    public async Task A_reconnect_landing_while_the_hosts_are_read_does_not_pass_for_every_machine_gone()
    {
        await BothMachinesAsync();
        Machine? before = null;
        ui.Post(() => before = main.MachineOf("remote-id"));
        link.Set(FleetState.Connecting);

        // This pass reads no hosts (still connecting), and the reconnect lands right after.
        link.AfterHostsRead = () => link.Quietly(FleetState.Connected, ("host-id", "Test PC"), ("remote-id", "Build server"));
        link.Raise();
        await Wait.UntilAsync(() => link.AfterHostsRead is null, "the pass");
        Machine? after = null;
        ui.Post(() => after = main.MachineOf("remote-id"));
        Assert.Same(before, after);
        Assert.NotNull(Card("there"));

        // The reconnect's own announcement: the same machine, still there.
        link.Raise();
        ui.Post(() => after = main.MachineOf("remote-id"));
        Assert.Same(before, after);
    }

    [Fact]
    public async Task Another_machines_host_is_known_by_the_run_it_names_and_sessions_opened_on_two_machines_both_open()
    {
        await BothMachinesAsync();
        Machine? remote = null;
        ui.Post(() => remote = main.MachineOf("remote-id"));
        await Wait.UntilAsync(() => remote!.Host.HostInstance == "run-7", "the remote host's run");

        // Two launches start, on two machines, before either card arrives.
        var opened = new List<string>();
        ui.Post(() =>
        {
            main.OpenRequested += c => opened.Add(c.Id);
            main.Open("remote-id", "there-new");
            main.Open(null, "here-new");
        });
        await remoteHost.SendAsync(Events.SessionUpsert, Make.Session("there-new", updated: Make.T0.AddMinutes(5)));
        await localHost.SendAsync(Events.SessionUpsert, Make.Session("here-new", updated: Make.T0.AddMinutes(5)));

        await Wait.UntilAsync(() => { lock (opened) { return opened.Count == 2; } }, "both sessions to open");
    }

    [Fact]
    public async Task A_launched_session_still_opens_when_it_registers_after_a_reconnect()
    {
        await BothMachinesAsync();
        var opened = new List<string>();
        ui.Post(() =>
        {
            main.OpenRequested += c => opened.Add(c.Id);
            main.Open(null, "late", untilItArrives: true);
        });

        // The host's list after reconnecting does not have it yet: it is still asking about trust.
        await localHost.StopAsync();
        await Wait.UntilAsync(() => local.State != LinkState.Connected, "the host to go");
        localHost.Start();
        await Wait.UntilAsync(() => local.State == LinkState.Connected, "the host to come back");
        await localHost.SendAsync(Events.SessionUpsert, Make.Session("late", updated: Make.T0.AddMinutes(5)));

        await Wait.UntilAsync(() => { lock (opened) { return opened.Contains("late"); } }, "the session to open");
    }

    [Fact]
    public async Task A_launched_session_still_opens_when_its_machine_drops_out_and_comes_back_first()
    {
        await BothMachinesAsync();
        var opened = new List<string>();
        ui.Post(() =>
        {
            main.OpenRequested += c => opened.Add(c.Id);
            main.Open("remote-id", "late", untilItArrives: true);
        });

        link.Set(FleetState.Connected, ("host-id", "Test PC"));
        clock.Advance(Fleet.OfflineKeep);
        await Wait.UntilAsync(() => Card("there") is null, "the other machine to go");
        link.Set(FleetState.Connected, ("host-id", "Test PC"), ("remote-id", "Build server"));
        await Wait.UntilAsync(() => Card("there") is not null, "the other machine to come back");
        await remoteHost.SendAsync(Events.SessionUpsert, Make.Session("late", updated: Make.T0.AddMinutes(5)));

        await Wait.UntilAsync(() => { lock (opened) { return opened.Contains("late"); } }, "the session to open");
    }

    [Fact]
    public async Task Machines_still_come_and_go_while_this_machines_host_is_away()
    {
        await BothMachinesAsync();
        await localHost.StopAsync();
        await Wait.UntilAsync(() => local.State != LinkState.Connected, "this machine's host to go");

        link.Set(FleetState.Connected, ("host-id", "Test PC"));
        await Wait.UntilAsync(() => main.MachineOf("remote-id")?.Offline == true, "the other machine to be offline");
        clock.Advance(Fleet.OfflineKeep);

        await Wait.UntilAsync(() => Card("there") is null, "the other machine's cards to go");
    }

    [Fact]
    public async Task The_picker_shows_one_machines_cards_and_opening_a_card_it_hides_shows_them_all_again()
    {
        await BothMachinesAsync();
        Assert.Equal(["there", "here"], main.Sessions.Select(c => c.Id));

        ui.Post(() => main.SelectedMachine = main.Machines.Single(m => m.Key == "remote-id"));
        Assert.Equal(["there"], main.Sessions.Select(c => c.Id));
        await remoteHost.SendAsync(Events.SessionUpsert, Make.Session("there-too", updated: Make.T0.AddMinutes(9)));
        await Wait.UntilAsync(() => main.Sessions.Count == 2, "the new card on the chosen machine");
        await localHost.SendAsync(Events.SessionUpsert, Make.Session("here-too", updated: Make.T0.AddMinutes(10)));
        await Wait.UntilAsync(() => Card("here-too") is not null, "the new card on this machine");
        Assert.Equal(["there-too", "there"], main.Sessions.Select(c => c.Id));

        var opened = new List<string>();
        main.OpenRequested += c => opened.Add(c.Id);
        ui.Post(() => main.Open(Machine.LocalKey, "here-too"));
        Assert.Equal(["here-too"], opened);
        Assert.Null(main.SelectedMachine!.Key);
        Assert.Equal(["here-too", "there-too", "there", "here"], main.Sessions.Select(c => c.Id));
    }

    [Fact]
    public async Task A_question_on_another_machine_is_toasted_with_its_machine_and_a_toast_answer_goes_there()
    {
        remoteHost.On(Ops.SessionAnswer, _ => new AnswerResult(AnswerOutcome.Delivered));
        await BothMachinesAsync();
        await Wait.UntilAsync(() =>
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            ui.Post(main.Tick);
            lock (shown) { return shown.Count == 1; }
        }, "the other machine's question toast");
        var toast = shown.Single();
        Assert.Equal(ToastPlanner.Tag("there", "remote-id"), toast.Tag);
        Assert.Contains("Build server · waiting on you", toast.Xml);
        Assert.Contains("machine=remote-id", toast.Xml);

        var sink = new Sink(shown);
        var responder = new ToastResponder(key => main.MachineOf(key)?.Deliveries, sink, (_, _) => { }) { MachineWait = TimeSpan.FromSeconds(3) };
        var button = System.Text.RegularExpressions.Regex.Match(toast.Xml, "arguments=\"([^\"]*value=sqlite[^\"]*)\"").Groups[1].Value.Replace("&amp;", "&");
        await responder.HandleAsync(button, new Dictionary<string, string>());

        Assert.Equal("sqlite", Assert.Single(remoteHost.Sent(Ops.SessionAnswer)).GetProperty("fields").GetProperty("answer").GetString());
        Assert.Empty(localHost.Sent(Ops.SessionAnswer));
        Assert.Equal(ToastPlanner.OutcomeTag("there", "remote-id"), shown.Last().Tag);

        // A click that started the app comes before the relay lists the machine: the answer
        // waits for it rather than being dropped.
        link.Set(FleetState.Connected, ("host-id", "Test PC"));
        clock.Advance(Fleet.OfflineKeep);
        await Wait.UntilAsync(() => Card("there") is null, "the machine to go");
        var early = responder.HandleAsync(button, new Dictionary<string, string>());
        await Task.Delay(300);
        link.Set(FleetState.Connected, ("host-id", "Test PC"), ("remote-id", "Build server"));
        await early;
        Assert.Equal(2, remoteHost.Sent(Ops.SessionAnswer).Count);

        // A machine gone since the toast went up: nothing is sent anywhere.
        await responder.HandleAsync(button.Replace("remote-id", "gone-id"), new Dictionary<string, string>());
        Assert.Contains("that machine is not connected", shown.Last().Xml);
        Assert.Equal(2, remoteHost.Sent(Ops.SessionAnswer).Count);
    }

    [Fact]
    public async Task A_machine_that_leaves_takes_its_answer_outcome_toasts_and_an_answer_on_its_way_there_says_nothing_more()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        remoteHost.OnAsync(Ops.SessionAnswer, async _ =>
        {
            if (Interlocked.Increment(ref calls) > 1) { await FakeHost.Until(release.Task); }
            return new AnswerResult(AnswerOutcome.Delivered);
        });
        await BothMachinesAsync();
        var sink = new Recorder();
        var responder = new ToastResponder(key => main.MachineOf(key)?.Deliveries, sink, (_, _) => { }) { MachineWait = TimeSpan.FromSeconds(3), OutcomeWait = TimeSpan.FromSeconds(2) };
        main.MachineRemoved += responder.MachineGone;
        // As this build's toast draws it (ToastXml.AnswerVersion).
        var button = $"action=answer&machine=remote-id&session=there&question=q1&kind=option&field=answer&value=sqlite&{ToastXml.AnswerVersionKey}={ToastXml.AnswerVersion}";
        await responder.HandleAsync(button, new Dictionary<string, string>());
        var outcome = ToastPlanner.OutcomeTag("there", "remote-id");
        Assert.Equal(outcome, sink.Shown.Last().Tag);

        // A second answer, held at the host, and the machine leaves meanwhile.
        var held = responder.HandleAsync(button.Replace("q1", "q2"), new Dictionary<string, string>());
        await Wait.UntilAsync(() => Volatile.Read(ref calls) == 2, "the second answer at the host");
        link.Set(FleetState.Connected, ("host-id", "Test PC"));
        clock.Advance(Fleet.OfflineKeep);
        await Wait.UntilAsync(() => Card("there") is null, "the machine to go");

        Assert.Contains(outcome, sink.Withdrawn);
        var before = sink.Shown.Count;
        await held;
        release.TrySetResult();
        Assert.Equal(before, sink.Shown.Count);
    }

    sealed class Recorder : IToastSink
    {
        public List<ToastNotice> Shown { get; } = [];
        public List<string> Withdrawn { get; } = [];
        public void Show(ToastNotice notice) { lock (Shown) { Shown.Add(notice); } }
        public void Withdraw(string tag) { lock (Withdrawn) { Withdrawn.Add(tag); } }
    }

    [Fact]
    public void This_machines_host_is_asked_for_its_account_again_on_a_switch_a_return_or_a_finished_sign_in()
    {
        static HostStatus S(string? account, string relay = "https://relay.example", SignInState signIn = SignInState.Idle) => FakeHost.StatusOf() with
        {
            Account = account is null ? null : new HostAccount("octo", account),
            Relay = new RelayStatus(account is null ? RelayState.SignedOut : RelayState.Connected, relay),
            SignIn = new SignInStatus(signIn),
        };
        var watch = new LocalAccountWatch(S("7"), connected: true);

        Assert.False(watch.Next(S("7"), true));
        Assert.True(watch.Next(S("8"), true));
        Assert.True(watch.Next(S("8", "https://other.example"), true));
        Assert.False(watch.Next(S("8", "https://other.example"), false));
        Assert.True(watch.Next(S("8", "https://other.example"), true));
        // Signing in again to the same account and relay: nothing differs but the sign-in ending.
        Assert.False(watch.Next(S("8", "https://other.example", SignInState.Pending), true));
        Assert.True(watch.Next(S("8", "https://other.example"), true));
        Assert.False(watch.Next(S("8", "https://other.example", SignInState.Pending), true));
        Assert.False(watch.Next(S("8", "https://other.example", SignInState.Failed), true));
    }

    [Fact]
    public async Task A_kept_reading_shows_with_its_age_and_the_machine_that_read_it_when_no_machine_offers_it()
    {
        // This machine's host is signed in to the account, reads its own Copilot, and the build
        // server is the only one signed in to Claude.
        localHost.Status = FakeHost.StatusOf(RelayState.Connected) with { Account = new HostAccount("octo", "1") };
        var copilot = new UsageReport([new AgentUsage { Agent = AgentKind.Copilot, Label = "Copilot", Account = "octo", Source = UsageSource.Api, MeasuredAt = Make.T0, Windows = [new UsageWindow { Key = "plan", Label = "Plan", Percent = 10 }] }]);
        var claude = new UsageReport([new AgentUsage { Agent = AgentKind.Claude, Label = "Claude", AccountKey = "c1a0de000000", Source = UsageSource.Api, MeasuredAt = Make.T0, Windows = [new UsageWindow { Key = "week", Label = "Week", Percent = 41 }] }]);
        localHost.On(UsageProtocol.Subscribe, _ => copilot);
        remoteHost.On(UsageProtocol.Subscribe, _ => claude);
        await BothMachinesAsync();
        // Said again, in case either was subscribed before the test said what to answer.
        await localHost.SendAsync(HostOps.StatusEvent, localHost.Status);
        await localHost.SendAsync(UsageProtocol.Update, copilot);
        await remoteHost.SendAsync(UsageProtocol.Update, claude);
        await Wait.UntilAsync(() => main.Usage.Rows.Count == 2 && local.Status?.Account is not null, "both machines' usage, and this machine's account");
        // Read as the UI thread would: the rows are redrawn on it whenever anything changes.
        static T OnUi<T>(Func<T> read) => TestUi.Read(read);
        UsageRowViewModel Row(AgentKind agent) => OnUi(() => main.Usage.Rows.Single(r => r.Agent == agent));
        await Wait.UntilAsync(() => Row(AgentKind.Claude).ReadOn == "read on Build server", "the build server's reading, marked as its");
        // This machine's own reading needs no saying where it came from.
        Assert.Null(Row(AgentKind.Copilot).ReadOn);

        // The build server goes offline, and two hours pass.
        link.Known = [new KnownMachine("remote-id", "Build server", "host", Make.T0)];
        link.Set(FleetState.Connected, ("host-id", "Test PC"));
        clock.Advance(Fleet.OfflineKeep);
        await Wait.UntilAsync(() => main.AllMachines.Count == 1, "the build server to go");
        clock.Advance(TimeSpan.FromHours(2));
        ui.Post(main.Usage.Refresh);

        var kept = Row(AgentKind.Claude);
        Assert.Equal(("41%", "read on Build server", "2h ago", true), (kept.Windows[0].PercentText, kept.ReadOn, kept.Age, kept.Stale));

        // And after the app restarts, before any machine has said anything.
        var restarted = new UsageViewModel(clock, new UsageCache(cacheFile, clock));
        restarted.Update(UsageReport.Empty, "host-id", account: null);
        Assert.Equal([null, "read on Build server"], restarted.Rows.Select(r => r.ReadOn).Order());
        // A device that has never seen an allowance shows nothing for it; another account sees none.
        restarted.Update(UsageReport.Empty, "host-id", account: "2");
        Assert.Empty(restarted.Rows);

        // Forgotten, the build server takes its reading with it, here and in what is kept.
        Assert.Null(await link.ForgetAsync("remote-id", default));
        await Wait.UntilAsync(() => main.Usage.Rows.Count == 1, "the forgotten machine's reading to go");
        var afterwards = new UsageViewModel(clock, new UsageCache(cacheFile, clock));
        afterwards.Update(UsageReport.Empty, "host-id", account: null);
        Assert.Equal(AgentKind.Copilot, Assert.Single(afterwards.Rows).Agent);
    }

    [Fact]
    public async Task What_this_machines_host_passed_on_for_the_last_account_is_not_kept_for_the_next_before_it_reports_again()
    {
        static AgentUsage Copilot(DateTimeOffset at) => new() { Agent = AgentKind.Copilot, Label = "Copilot", Account = "octo", Source = UsageSource.Api, MeasuredAt = at, Windows = [new UsageWindow { Key = "plan", Label = "Plan", Percent = 10 }] };
        // This machine's host reads its own Copilot, and passes on a Claude reading taken on account 1's VM.
        var passedOn = new AgentUsage { Agent = AgentKind.Claude, Label = "Claude", AccountKey = "c1a0de000000", Source = UsageSource.Api, MeasuredAt = Make.T0, Windows = [new UsageWindow { Key = "week", Label = "Week", Percent = 41 }], ReadBy = new UsageReader(new string('a', 32), "VM") };
        localHost.Status = FakeHost.StatusOf(RelayState.Connected) with { Account = new HostAccount("octo", "1") };
        localHost.On(UsageProtocol.Subscribe, _ => new UsageReport([Copilot(Make.T0), passedOn]));
        await Wait.UntilAsync(() => local.State == LinkState.Connected, "this machine's host");
        await localHost.SendAsync(HostOps.StatusEvent, localHost.Status);
        await localHost.SendAsync(UsageProtocol.Update, new UsageReport([Copilot(Make.T0), passedOn]));
        static T OnUi<T>(Func<T> read) => TestUi.Read(read);
        await Wait.UntilAsync(() => OnUi(() => main.Usage.Rows.Count) == 2 && local.Status?.Account?.AccountId == "1", "both readings, for account 1");

        // Signed in to account 2; the host's next report, without the VM's reading, has not come yet.
        localHost.Status = FakeHost.StatusOf(RelayState.Connected) with { Account = new HostAccount("other", "2") };
        await localHost.SendAsync(HostOps.StatusEvent, localHost.Status);
        await Wait.UntilAsync(() => OnUi(() => main.Usage.Rows.Select(r => r.Agent).ToList()) is [AgentKind.Copilot], "only this machine's own reading");
        // Then it comes, and what it passes on now is account 2's.
        var laptop = passedOn with { AccountKey = "c1a0de000002", ReadBy = new UsageReader(new string('b', 32), "Laptop") };
        await localHost.SendAsync(UsageProtocol.Update, new UsageReport([Copilot(Make.T0.AddMinutes(1)), laptop]));
        await Wait.UntilAsync(() => OnUi(() => main.Usage.Rows.Count) == 2, "the laptop's reading, passed on for account 2");

        var restarted = new UsageViewModel(clock, new UsageCache(cacheFile, clock));
        restarted.Update(UsageReport.Empty, "host-id", account: "2");
        Assert.Equal([null, "read on Laptop"], restarted.Rows.Select(r => r.ReadOn).Order());
    }

    [Fact]
    public async Task A_reading_past_two_weeks_on_one_machine_does_not_hide_a_current_failure_on_another()
    {
        var key = "c1a0de000000";
        localHost.Status = FakeHost.StatusOf(RelayState.Connected) with { Account = new HostAccount("octo", "1") };
        var expired = new UsageReport([new AgentUsage { Agent = AgentKind.Claude, Label = "Claude", AccountKey = key, Source = UsageSource.Api, MeasuredAt = Make.T0.AddDays(-15), Windows = [new UsageWindow { Key = "week", Label = "Week", Percent = 41 }] }]);
        var failed = new UsageReport([new AgentUsage { Agent = AgentKind.Claude, Label = "Claude", AccountKey = key, Source = UsageSource.None, MeasuredAt = Make.T0, Error = "rate limited" }]);
        localHost.On(UsageProtocol.Subscribe, _ => expired);
        remoteHost.On(UsageProtocol.Subscribe, _ => failed);
        await BothMachinesAsync();
        await localHost.SendAsync(HostOps.StatusEvent, localHost.Status);
        await localHost.SendAsync(UsageProtocol.Update, expired);
        await remoteHost.SendAsync(UsageProtocol.Update, failed);
        static T OnUi<T>(Func<T> read) => TestUi.Read(read);
        await Wait.UntilAsync(() => OnUi(() => main.Usage.Rows.SingleOrDefault()?.Error) == "rate limited", "the build server's failure");
    }

    [Fact]
    public async Task A_reading_kept_from_a_machine_the_first_roster_does_not_list_goes()
    {
        // This machine's host once passed on a reading by a laptop; it has been forgotten since,
        // while this app was closed, so no later list of offline machines will ever drop it.
        var byGone = new AgentUsage { Agent = AgentKind.Claude, Label = "Claude", AccountKey = "c1a0de000000", Source = UsageSource.Api, MeasuredAt = Make.T0, Windows = [new UsageWindow { Key = "week", Label = "Week", Percent = 41 }], ReadBy = new UsageReader(new string('a', 32), "Old laptop") };
        localHost.Status = FakeHost.StatusOf(RelayState.Connected) with { Account = new HostAccount("octo", "1") };
        localHost.On(UsageProtocol.Subscribe, _ => new UsageReport([byGone]));
        await Wait.UntilAsync(() => local.State == LinkState.Connected, "this machine's host");
        await localHost.SendAsync(HostOps.StatusEvent, localHost.Status);
        await localHost.SendAsync(UsageProtocol.Update, new UsageReport([byGone]));
        static T OnUi<T>(Func<T> read) => TestUi.Read(read);
        await Wait.UntilAsync(() => OnUi(() => main.Usage.Rows.Count) == 1 && local.Status?.Account?.AccountId == "1", "the laptop's reading, kept");
        await localHost.SendAsync(UsageProtocol.Update, UsageReport.Empty);

        // The relay's roster: the build server online, nothing offline.
        link.Known = [];
        link.Set(FleetState.Connected, ("host-id", "Test PC"), ("remote-id", "Build server"));
        await Wait.UntilAsync(() => OnUi(() => main.Usage.Rows.Count) == 0, "the forgotten laptop's reading to go");
        var restarted = new UsageViewModel(clock, new UsageCache(cacheFile, clock));
        restarted.Update(UsageReport.Empty, "host-id", account: "1");
        Assert.Empty(restarted.Rows);

        // Another copy of the app takes the relay link: with no current roster, a machine that joins
        // meanwhile is not held back by the old one.
        link.Set(FleetState.Elsewhere);
        var byNew = byGone with { AccountKey = "c1a0de000002", ReadBy = new UsageReader(new string('c', 32), "New desktop") };
        await localHost.SendAsync(UsageProtocol.Update, new UsageReport([byNew]));
        await Wait.UntilAsync(() => OnUi(() => main.Usage.Rows.Count) == 1, "the new machine's reading");
    }

    [Fact]
    public async Task A_machine_listed_for_both_accounts_shows_its_usage_for_the_next_when_the_switch_is_first_seen_connected()
    {
        var claude = new UsageReport([new AgentUsage { Agent = AgentKind.Claude, Label = "Claude", AccountKey = "c1a0de000000", Source = UsageSource.Api, MeasuredAt = Make.T0, Windows = [new UsageWindow { Key = "week", Label = "Week", Percent = 41 }] }]);
        link.AccountId = "1";
        localHost.Status = FakeHost.StatusOf(RelayState.Connected) with { Account = new HostAccount("octo", "1") };
        localHost.On(UsageProtocol.Subscribe, _ => UsageReport.Empty);
        remoteHost.On(UsageProtocol.Subscribe, _ => claude);
        await BothMachinesAsync();
        await localHost.SendAsync(HostOps.StatusEvent, localHost.Status);
        await remoteHost.SendAsync(UsageProtocol.Update, claude);
        static T OnUi<T>(Func<T> read) => TestUi.Read(read);
        await Wait.UntilAsync(() => OnUi(() => main.Usage.Rows.Count) == 1 && local.Status?.Account?.AccountId == "1", "the build server's reading, for account 1");

        // Both this machine's host and the fleet are on account 2 by the time the fleet looks, and
        // the build server is listed there too.
        localHost.Status = FakeHost.StatusOf(RelayState.Connected) with { Account = new HostAccount("other", "2") };
        await localHost.SendAsync(HostOps.StatusEvent, localHost.Status);
        await Wait.UntilAsync(() => local.Status?.Account?.AccountId == "2", "this machine's new account");
        link.Switch(announce: false);
        link.AccountId = "2";
        link.Set(FleetState.Connected, ("host-id", "Test PC"), ("remote-id", "Build server"));
        await Wait.UntilAsync(() => OnUi(() => main.Usage.Rows.Count) == 1, "the build server's reading, for account 2");
    }

    [Fact]
    public async Task Other_machines_readings_are_not_kept_for_the_account_this_machine_switched_to_while_the_fleet_follows()
    {
        // Two other machines on account 1, each reading its own Claude account.
        await using var second = new FakeHost();
        second.On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("second-id", "Laptop", "windows", "0.1.0"), [Capabilities.SessionsRead, UsageProtocol.Capability]));
        second.Start();
        link.Endpoints["second-id"] = second.Endpoint;
        static UsageReport Claude(string key) => new([new AgentUsage { Agent = AgentKind.Claude, Label = "Claude", AccountKey = key, Source = UsageSource.Api, MeasuredAt = Make.T0, Windows = [new UsageWindow { Key = "week", Label = "Week", Percent = 41 }] }]);
        link.AccountId = "1";
        localHost.Status = FakeHost.StatusOf(RelayState.Connected) with { Account = new HostAccount("octo", "1") };
        localHost.On(UsageProtocol.Subscribe, _ => UsageReport.Empty);
        remoteHost.On(UsageProtocol.Subscribe, _ => Claude("c1a0de000001"));
        second.On(UsageProtocol.Subscribe, _ => Claude("c1a0de000002"));
        await Wait.UntilAsync(() => local.State == LinkState.Connected, "this machine's host");
        link.Set(FleetState.Connected, ("host-id", "Test PC"), ("remote-id", "Build server"), ("second-id", "Laptop"));
        await localHost.SendAsync(HostOps.StatusEvent, localHost.Status);
        static T OnUi<T>(Func<T> read) => TestUi.Read(read);
        await Wait.UntilAsync(() => OnUi(() => main.Usage.Rows.Count) == 2 && local.Status?.Account?.AccountId == "1", "both machines' readings, for account 1");

        // This machine's host signs in to account 2 before the fleet has followed it.
        localHost.Status = FakeHost.StatusOf(RelayState.Connected) with { Account = new HostAccount("other", "2") };
        await localHost.SendAsync(HostOps.StatusEvent, localHost.Status);
        await Wait.UntilAsync(() => local.Status?.Account?.AccountId == "2", "this machine's new account");
        await Wait.UntilAsync(() => OnUi(() => main.Usage.Rows.Count) == 0, "account 1's readings to go from view");
        // Then the fleet follows: account 1's machines leave one at a time, and account 2 has none.
        link.Switch();
        await Wait.UntilAsync(() => OnUi(() => main.AllMachines.Count) == 1, "account 1's machines to go");
        link.AccountId = "2";
        link.Set(FleetState.Connected, ("host-id", "Test PC"));

        var restarted = new UsageViewModel(clock, new UsageCache(cacheFile, clock));
        restarted.Update(UsageReport.Empty, "host-id", account: "2");
        Assert.Empty(restarted.Rows);
    }

    [Fact]
    public async Task A_machine_whose_channel_drops_keeps_its_name_on_what_it_read_shown_and_kept()
    {
        var claude = new UsageReport([new AgentUsage { Agent = AgentKind.Claude, Label = "Claude", AccountKey = "c1a0de000000", Source = UsageSource.Api, MeasuredAt = Make.T0, Windows = [new UsageWindow { Key = "week", Label = "Week", Percent = 41 }] }]);
        localHost.Status = FakeHost.StatusOf(RelayState.Connected) with { Account = new HostAccount("octo", "1") };
        localHost.On(UsageProtocol.Subscribe, _ => UsageReport.Empty);
        remoteHost.On(UsageProtocol.Subscribe, _ => claude);
        await BothMachinesAsync();
        await localHost.SendAsync(HostOps.StatusEvent, localHost.Status);
        await remoteHost.SendAsync(UsageProtocol.Update, claude);
        static T OnUi<T>(Func<T> read) => TestUi.Read(read);
        await Wait.UntilAsync(() => OnUi(() => main.Usage.Rows.SingleOrDefault()?.ReadOn) == "read on Build server" && local.Status?.Account?.AccountId == "1", "the build server's reading");

        // Its channel drops: its hello goes, its last report stays.
        await remoteHost.StopAsync();
        await Wait.UntilAsync(() => OnUi(() => main.MachineOf("remote-id")?.Host.Hello) is null, "the build server's channel to drop");
        Assert.Equal("read on Build server", OnUi(() => Assert.Single(main.Usage.Rows).ReadOn));
        var restarted = new UsageViewModel(clock, new UsageCache(cacheFile, clock));
        restarted.Update(UsageReport.Empty, "host-id", account: "1");
        Assert.Equal("read on Build server", Assert.Single(restarted.Rows).ReadOn);
    }

    [Fact]
    public async Task Usage_is_merged_across_machines()
    {
        var older = new AgentUsage { Agent = AgentKind.Copilot, Label = "Copilot", Account = "octocat", Source = UsageSource.Api, MeasuredAt = Make.T0, Windows = [new UsageWindow { Key = "premium", Label = "Premium", Percent = 10 }] };
        localHost.On(UsageProtocol.Subscribe, _ => new UsageReport([older]));
        remoteHost.On(UsageProtocol.Subscribe, _ => new UsageReport([older with { MeasuredAt = Make.T0.AddMinutes(5), Windows = [new UsageWindow { Key = "premium", Label = "Premium", Percent = 40 }] },
            new AgentUsage { Agent = AgentKind.Codex, Label = "Codex", AccountKey = "c0de", Source = UsageSource.Transcript, MeasuredAt = Make.T0, Windows = [new UsageWindow { Key = "week", Label = "Week", Percent = 5 }] }]));
        await BothMachinesAsync();

        await Wait.UntilAsync(() => main.Usage.Rows.Count == 2, "both agents' usage");
        Assert.Equal(40, main.Usage.Rows.Single(r => r.Agent == AgentKind.Copilot).Headline);
    }

    [Fact]
    public void Merging_keeps_each_allowances_freshest_reading_and_a_newer_failure_only_as_its_note()
    {
        var reading = new AgentUsage { Agent = AgentKind.Claude, Label = "Claude", AccountKey = "aaaa1111", Source = UsageSource.Api, MeasuredAt = Make.T0, Windows = [new UsageWindow { Key = "w", Label = "Week", Percent = 50 }] };
        var failure = reading with { Windows = [], Error = "rate limited", MeasuredAt = Make.T0.AddMinutes(1) };
        var other = reading with { AccountKey = "bbbb2222", MeasuredAt = Make.T0.AddMinutes(2) };

        var merged = UsageMerge.Merge([new UsageReport([reading]), new UsageReport([failure, other])]).Agents;

        Assert.Equal(2, merged.Count);
        var first = merged.Single(a => a.AccountKey == "aaaa1111");
        Assert.Equal((50.0, "rate limited", "#aaaa"), (first.Windows[0].Percent, first.Error, first.Account));
        Assert.Equal("#bbbb", merged.Single(a => a.AccountKey == "bbbb2222").Account);
        // A failure alone is all there is to say.
        Assert.Equal("rate limited", Assert.Single(UsageMerge.Merge([new UsageReport([failure])]).Agents).Error);
    }

    [Fact]
    public void Two_keyless_Codex_readings_from_different_machines_stay_two_rows_a_keyed_one_still_merges_and_one_machine_shows_the_kind_it_took_last()
    {
        var vm = new UsageReader(new string('a', 32), "VM");
        var laptop = new UsageReader(new string('b', 32), "Laptop");
        var keyless = new AgentUsage { Agent = AgentKind.Codex, Label = "Codex", Source = UsageSource.Transcript, MeasuredAt = Make.T0, Windows = [new UsageWindow { Key = "primary", Label = "5h limit", Percent = 10 }] };

        var apart = UsageMerge.Merge([new UsageReport([keyless with { ReadBy = vm }]), new UsageReport([keyless with { ReadBy = laptop, MeasuredAt = Make.T0.AddMinutes(5) }])]).Agents;

        Assert.Equal(["VM", "Laptop"], apart.Select(a => a.ReadBy!.Name));

        var keyed = keyless with { AccountKey = "c0dec0dec0de", Source = UsageSource.Api };
        var together = UsageMerge.Merge([new UsageReport([keyed with { ReadBy = vm }]), new UsageReport([keyed with { ReadBy = laptop, MeasuredAt = Make.T0.AddMinutes(5) }])]).Agents;
        Assert.Equal("Laptop", Assert.Single(together).ReadBy!.Name);

        // One machine going from a probe's keyed reading to its transcripts' keyless one and back
        // shows one row, whichever it took last; another machine's keyed reading still has its own.
        var probe = keyed with { ReadBy = vm, MeasuredAt = Make.T0.AddMinutes(10) };
        var transcript = keyless with { ReadBy = vm, MeasuredAt = Make.T0.AddMinutes(20), Windows = [new UsageWindow { Key = "primary", Label = "5h limit", Percent = 30 }] };
        Assert.Equal(30, Assert.Single(UsageMerge.Merge([new UsageReport([probe]), new UsageReport([transcript])]).Agents).Windows[0].Percent);
        Assert.Equal(UsageSource.Api, Assert.Single(UsageMerge.Merge([new UsageReport([transcript]), new UsageReport([probe with { MeasuredAt = Make.T0.AddMinutes(30) }])]).Agents).Source);
        Assert.Equal(2, UsageMerge.Latest([new UsageReport([probe, transcript, keyed with { ReadBy = laptop }])]).Count);
    }

    [Fact]
    public void A_machines_older_keyless_sign_out_is_dropped_once_it_gives_a_newer_keyed_reading_and_a_newer_one_still_stands()
    {
        var vm = new UsageReader(new string('a', 32), "VM");
        var signedOut = new AgentUsage { Agent = AgentKind.Codex, Label = "Codex", Source = UsageSource.None, Error = "Codex needs signing in again on this machine", MeasuredAt = Make.T0, ReadBy = vm };
        var probe = new AgentUsage
        {
            Agent = AgentKind.Codex, Label = "Codex", AccountKey = "c0dec0dec0de", Source = UsageSource.Api, MeasuredAt = Make.T0.AddMinutes(10), ReadBy = vm,
            Windows = [new UsageWindow { Key = "primary", Label = "5h limit", Percent = 10 }],
        };

        var row = Assert.Single(UsageMerge.Merge([new UsageReport([signedOut]), new UsageReport([probe])]).Agents);
        Assert.Equal((UsageSource.Api, null), (row.Source, row.Error));
        Assert.Single(UsageMerge.Latest([new UsageReport([signedOut, probe])]));

        var later = signedOut with { MeasuredAt = Make.T0.AddMinutes(20) };
        Assert.Contains(UsageMerge.Merge([new UsageReport([later]), new UsageReport([probe])]).Agents, a => a.Error == later.Error);
    }

    [Fact]
    public void One_machines_keyed_and_keyless_readings_taken_at_the_same_moment_are_one_row_the_first_reported()
    {
        var vm = new UsageReader(new string('a', 32), "VM");
        var transcript = new AgentUsage
        {
            Agent = AgentKind.Codex, Label = "Codex", Source = UsageSource.Transcript, MeasuredAt = Make.T0, ReadBy = vm,
            Windows = [new UsageWindow { Key = "primary", Label = "5h limit", Percent = 10 }],
        };
        var probe = transcript with { AccountKey = "c0dec0dec0de", Source = UsageSource.Api };

        Assert.Equal(UsageSource.Transcript, Assert.Single(UsageMerge.Merge([new UsageReport([transcript]), new UsageReport([probe])]).Agents).Source);
        Assert.Equal(UsageSource.Api, Assert.Single(UsageMerge.Merge([new UsageReport([probe]), new UsageReport([transcript])]).Agents).Source);
        Assert.Single(UsageMerge.Latest([new UsageReport([probe, transcript])]));
    }

    OfflineMachineViewModel[] Offline()
    {
        OfflineMachineViewModel[] now = [];
        ui.Post(() => now = [.. main.OfflineMachines]);
        return now;
    }

    MachineRowViewModel[] Rows() => TestUi.Read(() => main.MachineRows.ToArray());

    [Fact]
    public async Task The_machines_page_lists_this_machine_then_the_others_then_those_offline_each_with_its_status_trailing_and_at_most_two_facts()
    {
        await BothMachinesAsync();
        link.Known = [Gone("laptop-id", "Travel laptop", 5)];
        link.Raise();
        await Wait.UntilAsync(() => Rows().Length == 3, "every machine's row");

        var rows = Rows();
        Assert.Equal(["Test PC", "Build server", "Travel laptop"], rows.Select(r => r.Name));
        Assert.Equal(["This machine", "Online", "Offline"], rows.Select(r => r.Status));
        Assert.Equal(["1 session", "1 session", ""], rows.Select(r => r.Facts));
        Assert.Equal("Last seen 5m ago", rows[2].Offline!.SeenText);
        // ui.md §6.1: never an id on a row, and line 2 holds at most two facts.
        Assert.All(rows, r => Assert.DoesNotContain("-id", r.Facts + r.Name));
        Assert.All(rows, r => Assert.True(r.Facts.Split(" · ").Length <= 2));
        Assert.Equal([true, true, false], rows.Select(r => r.CanShowSessions));
    }

    [Fact]
    public async Task A_read_only_machines_row_says_view_only_and_keeps_the_hosts_reason_for_its_tooltip_not_the_row()
    {
        const string why = "read-only: this is a read-only comparison host (delivery.readOnly in its config); answer at the machine";
        remoteHost.On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("remote-id", "Build server", "windows", "0.1.0", ReadOnly: why, Instance: "run-7"), [Capabilities.SessionsRead]));
        await BothMachinesAsync();
        await Wait.UntilAsync(() => Rows().Any(r => r.Name == "Build server" && r.ViewOnly), "the read-only machine's row");

        var row = Rows().Single(r => r.Name == "Build server");
        Assert.Equal("1 session · View only", row.Facts);
        Assert.Equal(why, row.ViewOnlyWhy);
        Assert.DoesNotContain("read-only", row.Facts);
        Assert.False(Rows().Single(r => r.Name == "Test PC").ViewOnly);
    }

    // #788: the row says so from the host's hold itself, whatever the banner shows, as ui.md §6.6
    // and the PWA's machine row do.
    [Fact]
    public async Task A_held_machines_row_says_discovery_held_with_the_hosts_reason_for_its_tooltip_until_the_hold_clears()
    {
        await BothMachinesAsync();
        const string reason = "an unrecognised copilot process (pid 4120)";
        await remoteHost.SendAsync(Events.DiscoveryChanged, new DiscoveryChanged(new DiscoveryHeld(reason, [AgentKind.Copilot])));
        await Wait.UntilAsync(() => Rows().Single(r => r.Name == "Build server").Held, "the held machine's row");

        var row = Rows().Single(r => r.Name == "Build server");
        Assert.Equal("1 session · Discovery held", row.Facts);
        Assert.Equal($"Discovery held: {reason}", row.FactsWhy);
        Assert.DoesNotContain("pid", row.Facts);
        var here = Rows().Single(r => r.Name == "Test PC");
        Assert.Equal(("1 session", null), (here.Facts, here.FactsWhy));

        await remoteHost.SendAsync(Events.DiscoveryChanged, new DiscoveryChanged());
        await Wait.UntilAsync(() => !Rows().Single(r => r.Name == "Build server").Held, "the hold to clear");
        Assert.Equal(("1 session", null), (row.Facts, row.FactsWhy));
    }

    [Fact]
    public async Task A_held_read_only_machines_row_says_discovery_held_in_view_onlys_place_and_both_reasons_in_its_tooltip()
    {
        const string why = "read-only: answer at the machine";
        remoteHost.On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("remote-id", "Build server", "windows", "0.1.0", ReadOnly: why, Instance: "run-7"), [Capabilities.SessionsRead]));
        remoteHost.Held = new DiscoveryHeld("the claude processes could not be listed", [AgentKind.Claude]);
        await BothMachinesAsync();
        await Wait.UntilAsync(() => Rows().Any(r => r.Name == "Build server" && r.ViewOnly && r.Held), "the held read-only machine's row");

        var row = Rows().Single(r => r.Name == "Build server");
        Assert.Equal("1 session · Discovery held", row.Facts);
        Assert.Equal($"View only: {why} · Discovery held: the claude processes could not be listed", row.FactsWhy);
    }

    [Fact]
    public async Task A_machine_chosen_from_a_menu_after_it_was_forgotten_changes_nothing_and_the_list_keeps_its_rows()
    {
        await BothMachinesAsync();
        // As More's menu held it while open: the choice, then its machine forgotten.
        var stale = TestUi.Read(() => main.Machines.Single(m => m.Key == "remote-id"));
        ui.Post(() => main.Remove("remote-id"));

        ui.Post(() => main.SelectedMachine = stale);

        TestUi.Read(() =>
        {
            Assert.Null(main.SelectedMachine!.Key);
            Assert.Same(main.Machines[0], main.SelectedMachine);
            Assert.Equal(["here"], main.Sessions.Select(c => c.Id));
            return true;
        });
    }

    [Fact]
    public async Task A_read_only_machines_tab_says_view_only_in_its_name_and_tooltip_and_the_reason_stays_off_it()
    {
        const string why = "read-only: this is a read-only comparison host (delivery.readOnly in its config); answer at the machine";
        remoteHost.On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("remote-id", "Build server", "windows", "0.1.0", ReadOnly: why, Instance: "run-7"), [Capabilities.SessionsRead]));
        await BothMachinesAsync();

        await Wait.UntilAsync(() => TestUi.Read(() => main.Machines.Single(m => m.Key == "remote-id").Label) == "Build server · 1 session · View only", "the read-only machine's tab");
        Assert.Equal("Build server", TestUi.Read(() => main.Machines.Single(m => m.Key == "remote-id").Short));
        Assert.Equal("Test PC (this machine) · 1 session", TestUi.Read(() => main.Machines.Single(m => m.Key == Machine.LocalKey).Label));
    }

    [Fact]
    public void A_machine_rows_button_names_follow_the_machine_being_renamed()
    {
        var row = new MachineRowViewModel("remote-id") { Name = "Connecting machine" };
        var raised = new List<string?>();
        row.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        row.Name = "Build server";

        Assert.Contains(nameof(MachineRowViewModel.ShowSessionsName), raised);
        Assert.Contains(nameof(MachineRowViewModel.CheckHereName), raised);
        Assert.Equal("Show the sessions on Build server", row.ShowSessionsName);
    }
    static KnownMachine Gone(string id, string name, int minutesAgo) => new(id, name, "host", Make.T0.AddMinutes(-minutesAgo));

    [Fact]
    public async Task The_accounts_offline_machines_are_listed_most_recent_first_with_when_each_was_last_seen_but_never_this_machines_own_or_one_online()
    {
        await BothMachinesAsync();

        link.Known = [Gone("laptop-id", "Travel laptop", 5), Gone("host-id", "Test PC", 1), Gone("remote-id", "Build server", 2), Gone("old-id", "Old desktop", 3 * 24 * 60)];
        link.Raise();

        await Wait.UntilAsync(() => Offline().Length == 2, "the offline machines");
        Assert.Equal(["Travel laptop", "Old desktop"], Offline().Select(o => o.Name));
        Assert.Equal(["Offline \u00b7 last seen 5m ago", "Offline \u00b7 last seen 3d ago"], Offline().Select(o => o.LastSeenText));
        Assert.True(main.ShowOfflineMachines);
        // Not machines with cards: nothing is reachable there.
        Assert.Equal(2, main.AllMachines.Count);
    }

    [Fact]
    public async Task Forget_takes_two_presses_and_asks_the_relay_for_that_machine_alone()
    {
        await BothMachinesAsync();
        link.Known = [Gone("laptop-id", "Travel laptop", 5), Gone("old-id", "Old desktop", 60)];
        link.Raise();
        await Wait.UntilAsync(() => Offline().Length == 2, "the offline machines");
        var laptop = Offline()[0];

        await laptop.PressAsync();
        Assert.True(laptop.Armed);
        Assert.Equal("Press again to forget", laptop.ForgetText);
        Assert.Empty(link.Forgotten);

        // An arm lapses after five seconds, and the next press arms again rather than forgetting.
        clock.Advance(TimeSpan.FromSeconds(OfflineMachineViewModel.ConfirmSeconds));
        ui.Post(main.Tick);
        Assert.False(laptop.Armed);
        await laptop.PressAsync();
        Assert.Empty(link.Forgotten);
        // Even when the tick that would lapse it has not come yet.
        clock.Advance(TimeSpan.FromSeconds(OfflineMachineViewModel.ConfirmSeconds));
        await laptop.PressAsync();
        Assert.Empty(link.Forgotten);
        Assert.True(laptop.Armed);

        await laptop.PressAsync();

        Assert.Equal(["laptop-id"], link.Forgotten);
        await Wait.UntilAsync(() => Offline().Select(o => o.Name).SequenceEqual(["Old desktop"]), "the forgotten machine to go");
        Assert.False(Offline()[0].Armed);
    }

    [Fact]
    public async Task A_refused_forget_says_why_and_keeps_the_machine()
    {
        await BothMachinesAsync();
        link.Known = [Gone("laptop-id", "Travel laptop", 5)];
        link.Refusal = "online";
        link.Raise();
        await Wait.UntilAsync(() => Offline().Length == 1, "the offline machine");
        var laptop = Offline()[0];

        await laptop.PressAsync();
        await laptop.PressAsync();

        Assert.Equal("It is back online, so it was not forgotten.", laptop.Error);
        Assert.Single(Offline());
        Assert.False(laptop.Armed);
    }

    [Fact]
    public async Task A_forget_whose_connection_drops_mid_send_ends_and_says_the_relay_did_not_answer()
    {
        await BothMachinesAsync();
        link.Known = [Gone("laptop-id", "Travel laptop", 5)];
        link.Throw = new System.Net.WebSockets.WebSocketException("the socket dropped");
        link.Raise();
        await Wait.UntilAsync(() => Offline().Length == 1, "the offline machine");
        var laptop = Offline()[0];

        await laptop.PressAsync();
        await laptop.PressAsync();

        Assert.False(laptop.Busy);
        Assert.True(laptop.CanForget);
        Assert.Equal("The relay did not answer. Try again when connected.", laptop.Error);
    }

    [Fact]
    public async Task A_machine_that_comes_back_takes_its_arm_with_it_and_is_not_armed_when_it_goes_again()
    {
        await BothMachinesAsync();
        link.Known = [Gone("laptop-id", "Travel laptop", 5)];
        link.Raise();
        await Wait.UntilAsync(() => Offline().Length == 1, "the offline machine");
        await Offline()[0].PressAsync();
        Assert.True(Offline()[0].Armed);

        // Back: the relay no longer lists it as offline, and it is online.
        link.Known = [];
        link.Set(FleetState.Connected, ("host-id", "Test PC"), ("remote-id", "Build server"), ("laptop-id", "Travel laptop"));
        await Wait.UntilAsync(() => Offline().Length == 0, "the machine to leave the offline list");

        link.Known = [Gone("laptop-id", "Travel laptop", 0)];
        link.Set(FleetState.Connected, ("host-id", "Test PC"), ("remote-id", "Build server"));
        await Wait.UntilAsync(() => Offline().Length == 1, "the machine offline again");
        Assert.False(Offline()[0].Armed);
        Assert.Empty(link.Forgotten);
    }

    [Fact]
    public async Task An_arm_goes_when_the_machine_was_seen_again_or_the_list_is_another_accounts()
    {
        await BothMachinesAsync();
        link.Known = [Gone("laptop-id", "Travel laptop", 5)];
        link.Raise();
        await Wait.UntilAsync(() => Offline().Length == 1, "the offline machine");
        await Offline()[0].PressAsync();

        // Back and gone again before the app looked: only its last-seen time says so.
        link.Known = [Gone("laptop-id", "Travel laptop", 0)];
        link.Raise();
        await Wait.UntilAsync(() => !Offline()[0].Armed, "the arm to go");
        await Offline()[0].PressAsync();
        Assert.True(Offline()[0].Armed);
        Assert.Empty(link.Forgotten);

        // Another account, seen first already connected, with a machine of the same id.
        link.Switch(announce: false);
        link.Known = [Gone("laptop-id", "Travel laptop", 0)];
        link.Set(FleetState.Connected, ("host-id", "Test PC"));
        await Wait.UntilAsync(() => Offline().Length == 1 && !Offline()[0].Armed, "a new entry, not armed");
        await Offline()[0].PressAsync();
        Assert.Empty(link.Forgotten);
    }

    [Fact]
    public async Task Offline_machines_stay_while_the_relay_drops_for_a_moment_and_go_with_the_account_or_a_relay_without_a_roster()
    {
        await BothMachinesAsync();
        link.Known = [Gone("laptop-id", "Travel laptop", 5)];
        link.Raise();
        await Wait.UntilAsync(() => Offline().Length == 1, "the offline machine");

        // The fleet reads the link on its Changed, run on this test's UI turn: done when Set returns.
        link.Set(FleetState.Connecting);
        Assert.Single(Offline());

        // A drop between reading the state and reading the list is the same.
        link.Set(FleetState.Connected, ("host-id", "Test PC"), ("remote-id", "Build server"));
        await Wait.UntilAsync(() => Offline().Length == 1, "the offline machine");
        link.BeforeOfflineRead = () => link.Quietly(FleetState.Connecting, ("host-id", "Test PC"), ("remote-id", "Build server"));
        link.Raise();
        Assert.Single(Offline());

        // An older relay keeps no roster: nothing listed, and nothing to forget.
        link.Known = null;
        link.Set(FleetState.Connected, ("host-id", "Test PC"), ("remote-id", "Build server"));
        await Wait.UntilAsync(() => Offline().Length == 0, "no offline machines");
        Assert.False(main.ShowOfflineMachines);

        link.Known = [Gone("laptop-id", "Travel laptop", 5)];
        link.Raise();
        await Wait.UntilAsync(() => Offline().Length == 1, "the offline machine");
        link.Switch();
        await Wait.UntilAsync(() => Offline().Length == 0, "the other account's offline machines to go");
    }

    // The relay's presence as the test says, and channels to the fake hosts on their pipes.
    internal sealed class FakeFleetLink : IFleetLink
    {
        // The relay's roster: offline machines, null for a relay without one.
        public IReadOnlyList<KnownMachine>? Known { get; set; }
        // Runs once, as the next read of the offline machines starts: a drop just before it.
        public Action? BeforeOfflineRead { get; set; }
        public IReadOnlyList<KnownMachine>? Offline
        {
            get
            {
                var then = BeforeOfflineRead;
                BeforeOfflineRead = null;
                then?.Invoke();
                return State == FleetState.Connected ? Known : null;
            }
        }
        public string? Refusal { get; set; }
        public Exception? Throw { get; set; }
        public List<string> Forgotten { get; } = [];

        public Task<string?> ForgetAsync(string machineId, CancellationToken ct)
        {
            if (Throw is { } e) { return Task.FromException<string?>(e); }
            if (Refusal is { } why) { return Task.FromResult<string?>(why); }
            Forgotten.Add(machineId);
            Known = [.. (Known ?? []).Where(k => k.Id != machineId)];
            Changed?.Invoke();
            return Task.FromResult<string?>(null);
        }

        public Dictionary<string, LocalEndpoint> Endpoints { get; } = [];
        public FleetState State { get; private set; } = FleetState.Connecting;
        public string? Detail => null;
        IReadOnlyList<FleetHost> hosts = [];
        public event Action? Changed;

        public int Account { get; private set; }
        public string? AccountId { get; set; }

        // Runs once, right after the next read of the hosts: for a change between two reads.
        public Action? AfterHostsRead { get; set; }

        public IReadOnlyList<FleetHost> Hosts
        {
            get
            {
                var now = hosts;
                var then = AfterHostsRead;
                AfterHostsRead = null;
                then?.Invoke();
                return now;
            }
            private set => hosts = value;
        }

        // Another account, still connecting; announced or not.
        public void Switch(bool announce = true)
        {
            Account++;
            State = FleetState.Connecting;
            hosts = [];
            if (announce) { Changed?.Invoke(); }
        }

        public void Raise() => Changed?.Invoke();

        // A change not yet announced, as the link's own thread makes one before it says so.
        public void Quietly(FleetState state, params (string Id, string Name)[] now)
        {
            State = state;
            hosts = [.. now.Select(h => new FleetHost(h.Id, h.Name))];
        }

        public void Set(FleetState state, params (string Id, string Name)[] hosts)
        {
            State = state;
            Hosts = [.. hosts.Select(h => new FleetHost(h.Id, h.Name))];
            DropAbsent();
            Changed?.Invoke();
        }

        // As the relay's hub does (ChannelHub.DropAbsent): a channel to a host the relay no longer
        // lists ends. (A relay that drops ends them all, which these tests leave out.)
        readonly List<(string Host, IClientChannel Channel)> open = [];

        void DropAbsent()
        {
            if (State != FleetState.Connected) { return; }
            List<IClientChannel> gone;
            lock (open)
            {
                gone = [.. open.Where(o => !hosts.Any(h => h.Id == o.Host)).Select(o => o.Channel)];
                open.RemoveAll(o => gone.Contains(o.Channel));
            }
            foreach (var ch in gone) { _ = ch.DisposeAsync().AsTask(); }
        }

        public void Start() { }

        // What the account's machines hand on, and the commands signed: (source, session, to).
        public List<TransferSource> Sources { get; } = [];
        public List<(string From, string Session, string To)> Signed { get; } = [];
        // Holds every answer about sources until it completes: machines slow to answer.
        public TaskCompletionSource? HoldSources { get; set; }
        public int Asked;

        public async Task<IReadOnlyList<TransferSource>> TransferSourcesAsync(string target, CancellationToken ct)
        {
            Interlocked.Increment(ref Asked);
            if (HoldSources is { } hold) { await hold.Task; }
            // As RelayFleetLink: nothing to ask the machines through while the relay is not up.
            return State != FleetState.Connected ? [] : [.. Sources.Where(s => s.HostId != target)];
        }

        public SignedCommand? SignTransfer(TransferSource from, string session, string to)
        {
            lock (Signed)
            {
                Signed.Add((from.HostId, session, to));
                return new SignedCommand { Body = $"body{Signed.Count}", By = "by", Mac = "mac" };
            }
        }

        // This app's device id, the people's devices the relay lists, and the folder-trust answers
        // signed: (machine, question, choice, request).
        public string? DeviceId { get; set; } = "a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1a1";
        public List<FleetHost> PeopleNow { get; } = [];
        public IReadOnlyList<FleetHost> People => PeopleNow;
        public List<(string Machine, string Question, string Choice, string Request)> Trusted { get; } = [];

        public SignedCommand? SignTrust(byte[]? machinePublic, string machine, string question, string choice, string request)
        {
            lock (Trusted)
            {
                Trusted.Add((machine, question, choice, request));
                return new SignedCommand { Body = $"trust{Trusted.Count}", By = "by", Mac = "mac" };
            }
        }

        public async Task<IClientChannel> OpenAsync(string hostId, CancellationToken ct)
        {
            if (State != FleetState.Connected || !Hosts.Any(h => h.Id == hostId) || !Endpoints.TryGetValue(hostId, out var e)) { throw new HostNotRunningException("the machine is offline"); }
            IClientChannel ch = await LocalClient.ConnectAsync(e, TimeSpan.FromSeconds(1), ct);
            if (Through is { } through) { ch = through(hostId, ch); }
            lock (open) { open.Add((hostId, ch)); }
            return ch;
        }

        // What each channel opened is seen through, when a test changes how it answers.
        public Func<string, IClientChannel, IClientChannel>? Through { get; set; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

// The real way there: a device of the app's own on the relay, with the account this machine's
// host signed in to, opening a channel to another machine's host.
public sealed class RelayFleetLinkTests
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    [Trait("Category", "Network")]
    public async Task The_app_reaches_another_machines_host_through_the_relay_with_this_hosts_account()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(7, "octo");
        await using var host = await relay.ConnectAsync(token, "Build server", "host");
        host.Hub.JsonOptions = ProtocolJson.Options;
        host.Hub.Handler = (_, op, _) => Task.FromResult<object?>(op == Ops.Hello ? new HelloResult(1, new MachineInfo(host.Id, "Build server", "linux", "0.1.0"), [Capabilities.SessionsRead]) : null);
        var asked = 0;
        await using var link = new RelayFleetLink(_ => { Interlocked.Increment(ref asked); return Task.FromResult<RelayAccount?>(new RelayAccount(relay.Url.ToString(), token, "7", "octo")); }, P256KeyPair.Generate(), Product.DeviceName("Test PC"));

        link.Start();
        await Wait.UntilAsync(() => link.State == FleetState.Connected && link.Hosts.Any(h => h.Id == host.Id), "the other machine in presence", Patience);

        Assert.Equal("Build server", link.Hosts.Single().Name);
        await using var ch = await link.OpenAsync(host.Id, default);
        var hello = await ch.HelloAsync();
        Assert.Equal("Build server", hello.Machine.Name);
        await Wait.UntilAsync(() => host.Relay.Presence.Any(p => p.Id == link.DeviceId), "the app in the host's presence", Patience);
        Assert.Equal("app", host.Relay.Presence.Single(p => p.Id == link.DeviceId).Kind);
        Assert.Equal(1, Volatile.Read(ref asked));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_renamed_app_comes_back_under_its_new_name_as_the_same_device_not_a_second_one()
    {
        // An install from before the rebrand (#537) connected as "… · Agent Bridge app"; after the
        // update it connects with the same key under the new name. The name is display only: the
        // device is its key, so other machines see one device, renamed, not a new one to approve.
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(7, "octo");
        await using var host = await relay.ConnectAsync(token, "Build server", "host");
        var key = P256KeyPair.Generate();
        RelayAccount Account() => new(relay.Url.ToString(), token, "7", "octo");
        string id;
        await using (var before = new RelayFleetLink(_ => Task.FromResult<RelayAccount?>(Account()), key, "Test PC · Agent Bridge app"))
        {
            before.Start();
            await Wait.UntilAsync(() => host.Relay.Presence.Any(p => p.Id == before.DeviceId), "the app before the update", Patience);
            id = before.DeviceId!;
            Assert.Equal("Test PC · Agent Bridge app", host.Relay.Presence.Single(p => p.Id == id).Name);
        }
        await Wait.UntilAsync(() => host.Relay.Presence.All(p => p.Id != id), "the app gone for its update", Patience);

        await using var after = new RelayFleetLink(_ => Task.FromResult<RelayAccount?>(Account()), key, Product.DeviceName("Test PC"));
        after.Start();
        await Wait.UntilAsync(() => host.Relay.Presence.Any(p => p.Id == after.DeviceId), "the app after the update", Patience);

        Assert.Equal(id, after.DeviceId);
        var app = Assert.Single(host.Relay.Presence, p => p.Kind == "app");
        Assert.Equal(id, app.Id);
        Assert.Equal($"Test PC · {Product.Name}", app.Name);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task The_app_lists_a_machine_that_has_gone_offline_and_as_a_persons_device_can_forget_it()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(7, "octo");
        var host = await relay.ConnectAsync(token, "Build server", "host");
        await using var link = new RelayFleetLink(_ => Task.FromResult<RelayAccount?>(new RelayAccount(relay.Url.ToString(), token, "7", "octo")), P256KeyPair.Generate(), Product.DeviceName("Test PC"));
        link.Start();
        await Wait.UntilAsync(() => link.State == FleetState.Connected && link.Hosts.Any(h => h.Id == host.Id), "the other machine in presence", Patience);
        Assert.Empty(link.Offline!);

        await host.DisposeAsync();

        await Wait.UntilAsync(() => link.Offline?.Any(k => k.Id == host.Id) == true, "the machine listed as offline", Patience);
        Assert.Equal("Build server", link.Offline!.Single().Name);
        Assert.Null(await link.ForgetAsync(host.Id, default));
        await Wait.UntilAsync(() => link.Offline is { Count: 0 }, "the forgotten machine to go", Patience);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_large_file_goes_sealed_through_the_relays_blob_store_to_the_one_machine_it_is_for()
    {
        await using var relay = await TestRelay.StartAsync(o => { o.Blobs.Backend = "local"; o.Blobs.MaxBytesPerAccountPerDay = 8L * 1024 * 1024; });
        var token = relay.TokenFor(7, "octo");
        await using var host = await relay.ConnectAsync(token, "Build server", "host");
        await using var link = new RelayFleetLink(_ => Task.FromResult<RelayAccount?>(new RelayAccount(relay.Url.ToString(), token, "7", "octo")), P256KeyPair.Generate(), Product.DeviceName("Test PC"));
        link.Start();
        await Wait.UntilAsync(() => link.State == FleetState.Connected && link.Blobs.Max > 0, "the relay's blob store", Patience);
        var file = System.Security.Cryptography.RandomNumberGenerator.GetBytes(3 * 1024 * 1024);
        var progress = new List<long>();

        var blob = await link.Blobs.PutAsync(host.Id, "photo.png", file, n => { lock (progress) { progress.Add(n); } });

        var received = new MemoryStream();
        using var http = BlobHttp.CreateClient();
        var hash = await BlobTransfer.ReceiveAsync(host.Relay, http, blob.Id, BlobFormat.FromBase64Url(blob.Key)!, file.Length, received);
        Assert.Equal(file, received.ToArray());
        Assert.Equal(System.Security.Cryptography.SHA256.HashData(file), hash);
        Assert.Equal(file.Length, progress.Max());
        Assert.Equal(progress.OrderBy(n => n), progress);

        // Over the account's allowance: the relay's reason, said as a person would read it.
        var e = await Assert.ThrowsAsync<HostErrorException>(() => link.Blobs.PutAsync(host.Id, "video.mp4", new byte[6 * 1024 * 1024], _ => { }));
        Assert.Equal("the relay's daily allowance for large attachments is used up; try again tomorrow, or send files of up to 256 KiB", e.Message);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task An_upload_that_storage_stalls_gives_up_in_time_as_one_to_try_again()
    {
        await using var relay = await TestRelay.StartAsync(o => o.Blobs.Backend = "local");
        var token = relay.TokenFor(7, "octo");
        await using var host = await relay.ConnectAsync(token, "Build server", "host");
        await using var app = await relay.ConnectAsync(token, Product.DeviceName("Test PC"), "app");
        var blobs = new RelayBlobStore(() => app.Relay, new HttpClient(new Stalls())) { UploadFor = TimeSpan.FromMilliseconds(300) };

        var e = await Assert.ThrowsAsync<IOException>(() => blobs.PutAsync(host.Id, "photo.png", new byte[400_000], _ => { }));

        Assert.Equal("the upload of photo.png took over 0 minutes", e.Message);
    }

    // Storage that takes the request and never answers.
    sealed class Stalls : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task The_account_s_machines_that_hand_sessions_on_are_listed_with_their_keys_and_a_command_signed_for_one_is_theirs_alone()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(7, "octo");
        await using var laptop = await relay.ConnectAsync(token, "Laptop", "host");
        await using var server = await relay.ConnectAsync(token, "Build server", "host");
        await using var old = await relay.ConnectAsync(token, "Old PC", "host");
        var session = new ResumableSession { Id = "0f3c4d5e-6a7b-4c8d-9e0f-a1b2c3d4e5f6", Agent = AgentKind.Copilot, Launcher = "copilot", Title = "Fix the flaky test" };
        foreach (var (d, name, serves) in new[] { (laptop, "Laptop", true), (server, "Build server", true), (old, "Old PC", false) })
        {
            d.Hub.JsonOptions = ProtocolJson.Options;
            d.Hub.Handler = (_, op, _) => Task.FromResult<object?>(op switch
            {
                Ops.Hello => new HelloResult(1, new MachineInfo(d.Id, name, "windows", "0.1.0"), serves ? [LaunchProtocol.Capability, TransferProtocol.ServeCapability] : [LaunchProtocol.Capability]),
                LaunchProtocol.Options => new LaunchOptions { Resumable = [session], Defaults = new LaunchDefaults { Permissions = LaunchProtocol.Ask } },
                _ => null,
            });
        }
        var me = P256KeyPair.Generate();
        await using var link = new RelayFleetLink(_ => Task.FromResult<RelayAccount?>(new RelayAccount(relay.Url.ToString(), token, "7", "octo")), me, Product.DeviceName("Test PC"));
        link.Start();
        await Wait.UntilAsync(() => link.State == FleetState.Connected && link.Hosts.Count == 3, "the machines in presence", Patience);

        // To bring one to the build server: the others are asked, and only the laptop hands it on.
        var source = Assert.Single(await link.TransferSourcesAsync(server.Id, default));

        Assert.Equal((laptop.Id, "Laptop", "Fix the flaky test"), (source.HostId, source.MachineName, Assert.Single(source.Resumable).Title));
        Assert.Equal(laptop.Key.Public, source.Key);
        var command = link.SignTransfer(source, session.Id, server.Id);
        var body = BlobFormat.FromBase64Url(command.Body)!;
        // Checked as the laptop checks it: the MAC from its own key and the app's.
        var expected = HMACSHA256.HashData(CommandMac.Key(laptop.Key, me.Public, Wire.DeviceId(me.Public), laptop.Id), body);
        Assert.Equal(BlobFormat.Base64Url(expected), command.Mac);
        var read = JsonSerializer.Deserialize<CommandBody>(body, ProtocolJson.Options)!;
        Assert.Equal(("user", link.DeviceId, laptop.Id, session.Id, server.Id), (read.Principal.Kind, read.Principal.Device, read.Target.Machine, read.Target.Session, read.To));
        Assert.Equal(300, read.ExpiresAt - read.IssuedAt);
    }

    [Fact]
    public void The_app_signs_the_same_command_the_PWA_and_the_host_agree_on()
    {
        // SignedCommandTests' vector, which pwa/test/unit/transfer.test.mjs signs too.
        var phone = P256KeyPair.FromParameters(
            Convert.FromHexString("905900d4260fa339e62fdd5e34ca9db632ff099271432b0b957d44c389d4576e"),
            Convert.FromHexString("accab67f642cf9f9c35c24175fb780e76d00f0a2782a5af26dba7ac634647b7f"),
            Convert.FromHexString("73d0820dc17ec237ae270a993b2b7400d662284d1b921c83c1b839136163138f"));
        var source = P256KeyPair.FromParameters(
            Convert.FromHexString("5f659de036a5d83c281f6c80a44633fc4cae31a37a140c73c21bff1d917b3597"),
            Convert.FromHexString("5d7f19384abde7014a17903abac0264f1add24f8639ab69fb9da7bbfaf24f910"),
            Convert.FromHexString("48ceda706c4697a48859a8fd3cc690992b208a4dcdd4ae0b2c32f3c65cfcde4a"));

        var command = Transfers.Sign(phone, source.Public, "0f3c4d5e-6a7b-4c8d-9e0f-a1b2c3d4e5f6", "00112233445566778899aabbccddeeff",
            DateTimeOffset.FromUnixTimeSeconds(1_793_300_000), "7d0f3a2e-1c4b-4d5e-8f60-112233445566");

        Assert.Equal("_PJ9LEydaL4_cLc4dUQ-wkKfhFj1DfQi-9K7Bl_0fxs", command.Mac);
        Assert.Equal(BlobFormat.Base64Url(phone.Public), command.By);
    }

    // #603: the app reconnecting after sleep opened each machine's channel with XX and then asked
    // hello, two round trips before anything. To a machine it has had a channel to, hello is
    // answered in the IK handshake, as HostConnection asks it.
    [Fact]
    [Trait("Category", "Network")]
    public async Task A_machine_the_app_has_had_a_channel_to_answers_its_hello_in_the_handshake_of_the_next()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(7, "octo");
        await using var host = await relay.ConnectAsync(token, "Build server", "host");
        // Whether the channel was open when each hello ran: not yet, for one answered early.
        var hellos = new System.Collections.Concurrent.ConcurrentQueue<bool>();
        host.Hub.JsonOptions = ProtocolJson.Options;
        host.Hub.EarlyOps = [Ops.Hello];
        host.Hub.Handler = (ch, op, _) =>
        {
            if (op != Ops.Hello) { return Task.FromResult<object?>(null); }
            hellos.Enqueue(ch.IsOpen);
            return Task.FromResult<object?>(new HelloResult(1, new MachineInfo(host.Id, "Build server", "windows", "0.1.0"), [Capabilities.SessionsRead]));
        };
        await using var link = new RelayFleetLink(_ => Task.FromResult<RelayAccount?>(new RelayAccount(relay.Url.ToString(), token, "7", "octo")), P256KeyPair.Generate(), Product.DeviceName("Test PC"));
        link.Start();
        await Wait.UntilAsync(() => link.Hosts.Any(h => h.Id == host.Id), "the other machine in presence", Patience);

        await using (var first = await link.OpenAsync(host.Id, default)) { Assert.Equal(host.Id, (await first.HelloAsync(changes: true)).Machine.Id); }
        await using var second = await link.OpenAsync(host.Id, default);
        var hello = await second.HelloAsync(changes: true);

        Assert.Equal("Build server", hello.Machine.Name);
        Assert.Equal(new[] { true, false }, hellos);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_host_whose_introduction_is_malformed_is_offline_for_now_not_the_end_of_trying()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(7, "octo");
        await using var host = await relay.ConnectAsync(token, "Build server", "host");
        // A peer that introduces itself without a name or kind.
        foreach (var field in new[] { "name", "kind" }) { typeof(ChannelHub).GetField(field, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(host.Hub, null); }
        await using var link = new RelayFleetLink(_ => Task.FromResult<RelayAccount?>(new RelayAccount(relay.Url.ToString(), token, "7", "octo")), P256KeyPair.Generate(), Product.DeviceName("Test PC"));
        link.Start();
        await Wait.UntilAsync(() => link.Hosts.Any(h => h.Id == host.Id), "the other machine in presence", Patience);

        var e = await Assert.ThrowsAsync<HostNotRunningException>(() => link.OpenAsync(host.Id, default));
        Assert.IsType<KeyNotFoundException>(e.InnerException);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Without_an_account_the_app_waits_signed_out_and_a_refused_token_says_so()
    {
        await using var relay = await TestRelay.StartAsync();
        RelayAccount? account = null;
        await using var link = new RelayFleetLink(_ => Task.FromResult(account), P256KeyPair.Generate(), Product.DeviceName("Test PC")) { Recheck = TimeSpan.FromSeconds(30) };

        link.Start();
        await Task.Delay(300);
        Assert.Equal(FleetState.SignedOut, link.State);
        await Assert.ThrowsAsync<HostNotRunningException>(() => link.OpenAsync("00112233445566778899aabbccddeeff", default));

        account = new RelayAccount(relay.Url.ToString(), "not-a-token", "7", "octo");
        link.Poke();
        await Wait.UntilAsync(() => link.State == FleetState.Refused, "the refusal", Patience);
        Assert.Equal("the account's sign-in is no longer accepted", link.Detail);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Signing_this_machines_host_in_to_another_account_moves_a_connected_app_to_it()
    {
        await using var relay = await TestRelay.StartAsync();
        var seven = relay.TokenFor(7, "octo");
        var eight = relay.TokenFor(8, "other");
        await using var host7 = await relay.ConnectAsync(seven, "Seven's PC", "host");
        await using var host8 = await relay.ConnectAsync(eight, "Eight's PC", "host");
        var account = new RelayAccount(relay.Url.ToString(), seven, "7", "octo");
        await using var link = new RelayFleetLink(_ => Task.FromResult<RelayAccount?>(account), P256KeyPair.Generate(), Product.DeviceName("Test PC"));
        link.Start();
        await Wait.UntilAsync(() => link.Hosts.Any(h => h.Id == host7.Id), "account 7's machine", Patience);

        // A poke that changes nothing keeps the connection.
        link.Poke();
        await Task.Delay(300);
        Assert.Equal(FleetState.Connected, link.State);

        var before = link.Account;
        account = new RelayAccount(relay.Url.ToString(), eight, "8", "other");
        link.Poke();
        await Wait.UntilAsync(() => link.Account != before, "the switch noticed", Patience);

        await Wait.UntilAsync(() => link.State == FleetState.Connected && link.Hosts.Any(h => h.Id == host8.Id), "account 8's machine", Patience);
        Assert.DoesNotContain(link.Hosts, h => h.Id == host7.Id);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Another_copy_of_the_app_taking_the_connection_is_left_alone_until_this_machines_host_signs_in_again()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(7, "octo");
        await using var host = await relay.ConnectAsync(token, "Build server", "host");
        var key = P256KeyPair.Generate();
        await using var link = new RelayFleetLink(_ => Task.FromResult<RelayAccount?>(new RelayAccount(relay.Url.ToString(), token, "7", "octo")), key, Product.DeviceName("Test PC"));
        var seen = new List<(FleetState, int)>();
        link.Changed += () => { lock (seen) { seen.Add((link.State, link.Hosts.Count)); } };
        link.Start();
        await Wait.UntilAsync(() => link.Hosts.Count == 1, "the other machine", Patience);
        lock (seen) { seen.Clear(); }

        // The same key connecting again (the app in this person's other Windows session): the
        // relay drops this one, which must not take the connection back. Two copies doing that
        // would evict each other every second.
        await using var again = await relay.ConnectAsync(token, "the app again", "app", key);

        await Wait.UntilAsync(() => link.State == FleetState.Elsewhere && link.Hosts.Count == 0, "the hand-over", Patience);
        Assert.Equal(RelayFleetLink.ReplacedNote, link.Detail);
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.False(again.Relay.Completion.IsCompleted, "this copy took the connection back");
        lock (seen)
        {
            Assert.DoesNotContain((FleetState.Connected, 0), seen);
            Assert.DoesNotContain(seen, s => s.Item1 is FleetState.Connecting or FleetState.Connected);
        }

        // Asked to (this machine's host signed in again, or came back): it connects again.
        link.Poke();
        await Wait.UntilAsync(() => link.State == FleetState.Connected && link.Hosts.Count == 1, "the reconnection", Patience);
        await again.Relay.Completion.WaitAsync(Patience);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_copy_of_the_app_without_the_relay_lock_never_connects_and_connects_once_it_has_it()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(7, "octo");
        await using var host = await relay.ConnectAsync(token, "Build server", "host");
        var mine = false;
        var asked = 0;
        await using var link = new RelayFleetLink(_ => { Interlocked.Increment(ref asked); return Task.FromResult<RelayAccount?>(new RelayAccount(relay.Url.ToString(), token, "7", "octo")); },
            P256KeyPair.Generate(), Product.DeviceName("Test PC"), claim: () => Volatile.Read(ref mine)) { Recheck = TimeSpan.FromSeconds(30) };

        link.Start();
        await Wait.UntilAsync(() => link.State == FleetState.Elsewhere, "the lock held elsewhere", Patience);
        Assert.Equal(RelayFleetLink.ElsewhereNote, link.Detail);
        await Task.Delay(300);
        Assert.Equal(0, Volatile.Read(ref asked));
        Assert.DoesNotContain(host.Relay.Presence, p => p.Id == link.DeviceId);

        Volatile.Write(ref mine, true);
        link.Poke();
        await Wait.UntilAsync(() => link.State == FleetState.Connected && link.Hosts.Count == 1, "the connection", Patience);
    }
}
