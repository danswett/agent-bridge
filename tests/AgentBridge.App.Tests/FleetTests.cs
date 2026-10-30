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
public sealed class FleetTests : IAsyncLifetime
{
    readonly FakeHost localHost = new();
    readonly FakeHost remoteHost = new();
    readonly LockedDispatcher ui = new();
    readonly FakeFleetLink link = new();
    readonly FakeTimeProvider clock = new(Make.T0);
    readonly List<ToastNotice> shown = [];
    HostConnection local = null!;
    MainViewModel main = null!;
    Fleet fleet = null!;

    sealed class Sink(List<ToastNotice> shown) : IToastSink
    {
        public void Show(ToastNotice notice) { lock (shown) { shown.Add(notice); } }
        public void Withdraw(string tag) { }
    }

    public Task InitializeAsync()
    {
        localHost.Sessions.Add(Make.Session("here", updated: Make.T0));
        localHost.Start();
        remoteHost.On(Ops.Hello, _ => new HelloResult(1, new MachineInfo("remote-id", "Build server", "windows", "0.1.0"),
            [Capabilities.SessionsRead, Capabilities.SessionsAnswer, Capabilities.SessionsReply, Capabilities.SessionsAttach, Capabilities.SessionsStop, UsageProtocol.Capability]));
        remoteHost.Sessions.Add(Make.Session("there", question: Make.Choice(), updated: Make.T0.AddMinutes(1)));
        remoteHost.Start();
        link.Endpoints["remote-id"] = remoteHost.Endpoint;
        local = localHost.Connect(ui);
        main = new MainViewModel(Build(Machine.LocalKey, local), new UsageViewModel(clock));
        fleet = new Fleet(link, main, ui, h => Build(h.Id, new HostConnection(ct => link.OpenAsync(h.Id, ct), ui) { RetryDelay = TimeSpan.FromMilliseconds(100) }));
        return Task.CompletedTask;
    }

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
        var toasts = new ToastPlanner(new Sink(shown), clock) { Capabilities = () => host.Capabilities, MachineName = () => m?.Name, MachineKey = key == Machine.LocalKey ? null : key };
        return m = new Machine(key, host, cards, toasts);
    }

    public async Task DisposeAsync()
    {
        await fleet.DisposeAsync();
        await local.DisposeAsync();
        await localHost.DisposeAsync();
        await remoteHost.DisposeAsync();
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
        // The relay lists this machine's own host too: it is reached over the pipe, not again.
        link.Set(FleetState.Connected, ("host-id", "Test PC"), ("remote-id", "Build server"));
        await Wait.UntilAsync(() => Card("here") is not null && Card("there") is not null, "both machines' cards");
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
    public async Task A_machine_that_leaves_takes_its_cards_with_it_but_a_relay_that_drops_for_a_moment_does_not()
    {
        await BothMachinesAsync();
        ui.Post(() => main.SelectedMachine = main.Machines.Single(m => m.Key == "remote-id"));

        // The relay connection itself went: nothing says the machine has.
        link.Set(FleetState.Connecting);
        await Task.Delay(200);
        Assert.NotNull(Card("there"));

        link.Set(FleetState.Connected, ("host-id", "Test PC"));
        await Wait.UntilAsync(() => Card("there") is null, "the other machine's cards to go");
        Assert.Single(main.AllMachines);
        Assert.False(main.ShowMachines);
        Assert.Null(main.SelectedMachine!.Key);
        Assert.Equal(["here"], main.Sessions.Select(c => c.Id));
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

        Assert.Contains(nameof(MainViewModel.FleetNote), raised);
        Assert.Contains(nameof(MainViewModel.HasFleetNote), raised);
    }

    [Fact]
    public async Task Machines_still_come_and_go_while_this_machines_host_is_away()
    {
        await BothMachinesAsync();
        await localHost.StopAsync();
        await Wait.UntilAsync(() => local.State != LinkState.Connected, "this machine's host to go");

        link.Set(FleetState.Connected, ("host-id", "Test PC"));

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
        var release = new ManualResetEventSlim();
        var calls = 0;
        remoteHost.On(Ops.SessionAnswer, _ =>
        {
            if (Interlocked.Increment(ref calls) > 1) { release.Wait(TimeSpan.FromSeconds(10)); }
            return new AnswerResult(AnswerOutcome.Delivered);
        });
        await BothMachinesAsync();
        var sink = new Recorder();
        var responder = new ToastResponder(key => main.MachineOf(key)?.Deliveries, sink, (_, _) => { }) { MachineWait = TimeSpan.FromSeconds(3), OutcomeWait = TimeSpan.FromSeconds(2) };
        main.MachineRemoved += responder.MachineGone;
        var button = "action=answer&machine=remote-id&session=there&question=q1&kind=option&field=answer&value=sqlite";
        await responder.HandleAsync(button, new Dictionary<string, string>());
        var outcome = ToastPlanner.OutcomeTag("there", "remote-id");
        Assert.Equal(outcome, sink.Shown.Last().Tag);

        // A second answer, held at the host, and the machine leaves meanwhile.
        var held = responder.HandleAsync(button.Replace("q1", "q2"), new Dictionary<string, string>());
        await Wait.UntilAsync(() => Volatile.Read(ref calls) == 2, "the second answer at the host");
        link.Set(FleetState.Connected, ("host-id", "Test PC"));
        await Wait.UntilAsync(() => Card("there") is null, "the machine to go");

        Assert.Contains(outcome, sink.Withdrawn);
        var before = sink.Shown.Count;
        await held;
        release.Set();
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

    // The relay's presence as the test says, and channels to the fake hosts on their pipes.
    sealed class FakeFleetLink : IFleetLink
    {
        public Dictionary<string, LocalEndpoint> Endpoints { get; } = [];
        public FleetState State { get; private set; } = FleetState.Connecting;
        public string? Detail => null;
        IReadOnlyList<FleetHost> hosts = [];
        public event Action? Changed;

        public int Account { get; private set; }

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

        public void Set(FleetState state, params (string Id, string Name)[] hosts)
        {
            State = state;
            Hosts = [.. hosts.Select(h => new FleetHost(h.Id, h.Name))];
            Changed?.Invoke();
        }

        public void Start() { }

        public async Task<IClientChannel> OpenAsync(string hostId, CancellationToken ct) =>
            State == FleetState.Connected && Hosts.Any(h => h.Id == hostId) && Endpoints.TryGetValue(hostId, out var e)
                ? await LocalClient.ConnectAsync(e, TimeSpan.FromSeconds(1), ct)
                : throw new HostNotRunningException("the machine is offline");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

// The real way there: a device of the app's own on the relay, with the account this machine's
// host signed in to, opening a channel to another machine's host.
public sealed class RelayFleetLinkTests
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task The_app_reaches_another_machines_host_through_the_relay_with_this_hosts_account()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(7, "octo");
        await using var host = await relay.ConnectAsync(token, "Build server", "host");
        host.Hub.JsonOptions = ProtocolJson.Options;
        host.Hub.Handler = (_, op, _) => Task.FromResult<object?>(op == Ops.Hello ? new HelloResult(1, new MachineInfo(host.Id, "Build server", "linux", "0.1.0"), [Capabilities.SessionsRead]) : null);
        var asked = 0;
        await using var link = new RelayFleetLink(_ => { Interlocked.Increment(ref asked); return Task.FromResult<RelayAccount?>(new RelayAccount(relay.Url.ToString(), token, "7", "octo")); }, P256KeyPair.Generate(), "Test PC · Agent Bridge app");

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
    public async Task Without_an_account_the_app_waits_signed_out_and_a_refused_token_says_so()
    {
        await using var relay = await TestRelay.StartAsync();
        RelayAccount? account = null;
        await using var link = new RelayFleetLink(_ => Task.FromResult(account), P256KeyPair.Generate(), "Test PC · Agent Bridge app") { Recheck = TimeSpan.FromSeconds(30) };

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
    public async Task Signing_this_machines_host_in_to_another_account_moves_a_connected_app_to_it()
    {
        await using var relay = await TestRelay.StartAsync();
        var seven = relay.TokenFor(7, "octo");
        var eight = relay.TokenFor(8, "other");
        await using var host7 = await relay.ConnectAsync(seven, "Seven's PC", "host");
        await using var host8 = await relay.ConnectAsync(eight, "Eight's PC", "host");
        var account = new RelayAccount(relay.Url.ToString(), seven, "7", "octo");
        await using var link = new RelayFleetLink(_ => Task.FromResult<RelayAccount?>(account), P256KeyPair.Generate(), "Test PC · Agent Bridge app");
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
    public async Task Another_copy_of_the_app_taking_the_connection_is_left_alone_until_this_machines_host_signs_in_again()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(7, "octo");
        await using var host = await relay.ConnectAsync(token, "Build server", "host");
        var key = P256KeyPair.Generate();
        await using var link = new RelayFleetLink(_ => Task.FromResult<RelayAccount?>(new RelayAccount(relay.Url.ToString(), token, "7", "octo")), key, "Test PC · Agent Bridge app");
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
    public async Task A_copy_of_the_app_without_the_relay_lock_never_connects_and_connects_once_it_has_it()
    {
        await using var relay = await TestRelay.StartAsync();
        var token = relay.TokenFor(7, "octo");
        await using var host = await relay.ConnectAsync(token, "Build server", "host");
        var mine = false;
        var asked = 0;
        await using var link = new RelayFleetLink(_ => { Interlocked.Increment(ref asked); return Task.FromResult<RelayAccount?>(new RelayAccount(relay.Url.ToString(), token, "7", "octo")); },
            P256KeyPair.Generate(), "Test PC · Agent Bridge app", claim: () => Volatile.Read(ref mine)) { Recheck = TimeSpan.FromSeconds(30) };

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
