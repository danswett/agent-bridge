using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Update now (#1159 slice 1, docs/protocol/v1-update.md): a person's device signs an update.install
// that skips the app's wait for a quiet machine. The press passes remote management's signed-command
// gate (SignedPress), is recorded durably, and is answered confirm when the machine is busier than
// the person confirmed; the host's half of the departure (UpdateService.DepartAsync) leaves through
// #805's lease as purpose Update and spends Update now's own restart slot before a shared one. The
// app's half is slice 2, and the pipe op that calls DepartAsync is #870's: here the test calls it.
[Trait("Category", "Network")]
public sealed class UpdateNowTests : IAsyncLifetime
{
    static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    const string Release = "0.5.0";
    // The shared cap's restarts in 6 hours, before the signed roster (#55).
    const int Shared = ManageService.RestartsBeforeRoster;

    static readonly UpdateState Reported = new()
    {
        Current = "0.4.0",
        Installer = true,
        Available = Release,
        Notes = "What 0.5.0 changes",
        CheckedAt = Now,
        Automatic = true,
    };

    readonly Fixture f = new();
    readonly FakeTimeProvider time = new(Now);
    readonly List<string> log = [];
    readonly List<string> notices = [];
    readonly FakeSessions sessions = new();
    readonly AppChannel app = new();
    DeliveryGate gate = null!;
    string? readOnly;
    string? otherHost;
    // Runs where another profile's host is looked for, just before the commit: what may happen
    // while the departure's conditions are read.
    Func<string?>? composedOtherHosts;
    TaskCompletionSource restarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    TestRelay relay = null!;
    Device host = null!;
    ManageComposition composed = null!;
    UpdateService updates = null!;
    string token = "";
    string folder = "";

    public async Task InitializeAsync()
    {
        folder = Path.Combine(f.Root, "profile");
        Directory.CreateDirectory(folder);
        relay = await TestRelay.StartAsync();
        token = relay.TokenFor(1, "octo");
        host = await relay.ConnectAsync(token, "Test host", "host");
        host.Hub.JsonOptions = ProtocolJson.Options;
        Compose();
    }

    public async Task DisposeAsync()
    {
        await relay.DisposeAsync();
        f.Dispose();
    }

    // A host as Program composes one; another over the same folder is the host after a restart.
    // The app beside it reports an update ready and that it carries Update now, unless told not to.
    void Compose(HostSettings? settings = null, bool carries = true)
    {
        gate = new DeliveryGate(time);
        ManageService? made = null;
        restarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        composed = ManageComposition.For(settings ?? new HostSettings(true), host.Key, folder, gate, () => readOnly, null,
            () => { made!.HandingOver(); restarted.TrySetResult(); },
            new Notices(notices), "Test host", Log, time, otherHosts: () => composedOtherHosts is { } look ? look() : otherHost);
        made = composed.Manage;
        made.Start();
        made.Attach(host.Relay);
        updates = new UpdateService("0.4.0", Log, time)
        {
            ReadOnly = () => readOnly,
            Now = composed.UpdateNow(sessions, folder),
            DepartWait = TimeSpan.Zero,
        };
        updates.Attach(host.Relay);
        var service = new SessionService(sessions, new MachineInfo("host-id", "Test host", "windows", "0.4.0")) { ReadOnly = () => readOnly, Updates = updates, Manage = made };
        host.Hub.Handler = service.HandleAsync;
        updates.Report(app, new UpdateReport(Reported, null, carries));
    }

    void Log(string line) { lock (log) { log.Add(line); } }

    async Task<(Device Device, SecureChannel Channel)> OpenAsync(string name = "Phone", string kind = "pwa")
    {
        var device = await relay.ConnectAsync(token, name, kind);
        await device.Relay.WaitForPeerAsync(p => p.Id == host.Id, Bound);
        await host.Relay.WaitForPeerAsync(p => p.Id == device.Relay.DeviceId, Bound);
        return (device, await device.Hub.OpenAsync(host.Id));
    }

    SignedCommand Sign(P256KeyPair signer, object? args, string action = UpdateProtocol.NowAction, string? request = null, DateTimeOffset? issued = null, TimeSpan? lifetime = null)
    {
        var at = issued ?? time.GetUtcNow();
        var body = new CommandBody
        {
            V = 1,
            Action = action,
            Request = request ?? Guid.NewGuid().ToString(),
            Principal = new CommandPrincipal { Kind = "user", Device = Wire.DeviceId(signer.Public) },
            Target = new CommandTarget { Machine = host.Id },
            To = host.Id,
            IssuedAt = at.ToUnixTimeSeconds(),
            ExpiresAt = (at + (lifetime ?? TimeSpan.FromSeconds(60))).ToUnixTimeSeconds(),
            Args = args is null ? null : JsonSerializer.SerializeToElement(args, ProtocolJson.Options),
        };
        return SignedCommands.Sign(signer, host.Key.Public, body);
    }

    // An Update now's signed args: now, the release shown, and what was confirmed, if anything.
    static Dictionary<string, object> Args(UpdateBusy? accept = null, string release = Release)
    {
        var args = new Dictionary<string, object> { ["now"] = true, ["release"] = release };
        if (accept is not null) { args["accept"] = accept; }
        return args;
    }

    SignedCommand Press(Device phone, UpdateBusy? accept = null, string release = Release, DateTimeOffset? issued = null) =>
        Sign(phone.Key, Args(accept, release), issued: issued);

    static string RequestOf(SignedCommand command) =>
        JsonSerializer.Deserialize<CommandBody>(BlobFormat.FromBase64Url(command.Body)!, ProtocolJson.Options)!.Request;

    static async Task<UpdateCommandResult> SendAsync(SecureChannel ch, object data)
    {
        var response = await ch.RequestAsync(UpdateProtocol.Install, data);
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        return response.GetProperty("result").Deserialize<UpdateCommandResult>(ProtocolJson.Options)!;
    }

    static Task<UpdateCommandResult> PressAsync(SecureChannel ch, SignedCommand now) => SendAsync(ch, new UpdateCommand { Now = now });

    static async Task<UpdateState> StateAsync(SecureChannel ch)
    {
        var response = await ch.RequestAsync(UpdateProtocol.Get, new { });
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        return response.GetProperty("result").Deserialize<UpdateState>(ProtocolJson.Options)!;
    }

    static async Task<ManageState> ManageStateAsync(SecureChannel ch)
    {
        var response = await ch.RequestAsync(ManageProtocol.Get, new { });
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        return response.GetProperty("result").Deserialize<ManageState>(ProtocolJson.Options)!;
    }

    // A press accepted and then left with: committed, the host fenced.
    async Task<string> UpdateNowAsync(Device phone, SecureChannel ch)
    {
        var press = Press(phone);
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, press)).Outcome);
        var request = RequestOf(press);
        Assert.Equal(UpdateDepartureOutcome.Committed, (await updates.DepartAsync(request)).Outcome);
        return request;
    }

    // manage.restart through the relay, committed, and the host after it.
    async Task RemoteRestartAsync(Device phone, SecureChannel ch)
    {
        var response = await ch.RequestAsync(ManageProtocol.Restart, new ManageCommand { Command = Sign(phone.Key, null, ManageProtocol.Restart) });
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        Assert.Equal(ManageOutcome.Accepted, response.GetProperty("result").Deserialize<ManageResult>(ProtocolJson.Options)!.Outcome);
        await restarted.Task.WaitAsync(Bound);
        Compose();
    }

    static Session S(string id, SessionStatus status = SessionStatus.Idle, QuestionKind? question = null, string? title = null) => new()
    {
        Id = id,
        Agent = AgentKind.Copilot,
        Title = title,
        Status = question is null ? status : SessionStatus.Waiting,
        StatusSince = Now,
        UpdatedAt = Now,
        Activity = new Activity(),
        Question = question is { } kind ? new Question { Id = "q-" + id[..8], Kind = kind, Message = "Allow **shell**: rm -rf the-secret-folder?", AskedAt = Now } : null,
    };

    static string Id(int n) => $"00000000-0000-4000-8000-{n:D12}";

    static IReadOnlyList<Session> Many(int count, SessionStatus status = SessionStatus.Working, QuestionKind? question = null, int from = 0) =>
        [.. Enumerable.Range(from, count).Select(i => S(Id(i), status, question))];

    [Fact]
    public async Task Update_now_on_a_quiet_machine_is_accepted_on_one_press()
    {
        var (phone, ch) = await OpenAsync();
        var offered = await StateAsync(ch);
        Assert.True(offered.CanUpdateNow);
        Assert.Equal((0, 0), (offered.Busy!.Working, offered.Busy.Waiting));

        var press = Press(phone);
        var result = await PressAsync(ch, press);

        Assert.Equal(UpdateAck.Accepted, result.Outcome);
        var ask = Assert.Single(updates.Asks);
        Assert.Equal((RequestOf(press), UpdateAskKind.Install, Release), (ask.Request, ask.Kind, ask.Now!.Release));
        Assert.Null(ask.Now.Accept);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(Now.ToUnixTimeSeconds()) + UpdateProtocol.NowLife, ask.Now.Until);
        Assert.True(File.Exists(Path.Combine(folder, "update-now.json")));

        var departure = await updates.DepartAsync(ask.Request);
        Assert.Equal(UpdateDepartureOutcome.Committed, departure.Outcome);
        Assert.Contains(log, l => l.StartsWith("restart for update: 0.4.0 to 0.5.0, asked from pwa ", StringComparison.Ordinal));
        Assert.Single(notices, n => n.Contains("was updated now to 0.5.0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_apps_departure_op_answers_committed_with_the_departure_and_a_press_already_gone_refused_changed()
    {
        var (phone, ch) = await OpenAsync();
        var press = Press(phone);
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, press)).Outcome);

        var left = await updates.DepartForAppAsync(RequestOf(press));

        Assert.Equal(UpdateDepartOutcome.Committed, left.Outcome);
        Assert.False(string.IsNullOrEmpty(left.Transition));
        Assert.Equal(new UpdateDepartResult(UpdateDepartOutcome.Refused, ManageReason.Changed), await updates.DepartForAppAsync(RequestOf(press)));
        // The app's host.stop, which follows, still takes the host's stop fence: the departure's
        // own fence doesn't keep the host from stopping.
        using var stop = await gate.PrepareStopAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(stop);
    }

    [Fact]
    public async Task Update_now_with_a_turn_running_and_no_accept_is_answered_confirm_and_asks_the_app_nothing()
    {
        sessions.Set(S(Id(1), SessionStatus.Working));
        var (phone, ch) = await OpenAsync();

        var result = await PressAsync(ch, Press(phone));

        Assert.Equal(UpdateAck.Confirm, result.Outcome);
        Assert.Equal((1, 0), (result.State!.Busy!.Working, result.State.Busy.Waiting));
        Assert.Equal(new UpdateBusySession(Id(1), UpdateBusyState.Working), Assert.Single(result.State.Busy.Sessions!));
        Assert.Empty(updates.Asks);
    }

    [Fact]
    public async Task Update_now_goes_over_no_busy_session_the_confirmation_didnt_list()
    {
        sessions.Set(S(Id(1), SessionStatus.Working));
        var (phone, ch) = await OpenAsync();
        var shown = (await StateAsync(ch)).Busy!;
        // Another turn starts after the confirmation was shown, before the press.
        sessions.Set(S(Id(1), SessionStatus.Working), S(Id(2), SessionStatus.Working));

        var result = await PressAsync(ch, Press(phone, shown));

        Assert.Equal(UpdateAck.Confirm, result.Outcome);
        Assert.Equal(2, result.State!.Busy!.Working);
        Assert.Empty(updates.Asks);
        // Confirmed with both, it goes.
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, Press(phone, result.State.Busy))).Outcome);
    }

    [Fact]
    public void With_40_busy_sessions_a_confirmation_of_the_32_listed_and_8_more_is_covered_while_nothing_changes_and_not_once_a_ninth_unlisted_session_or_a_listed_sessions_new_question_appears()
    {
        var busy = Many(40);
        var accept = UpdateBusyView.Of(busy, false).Shown();
        Assert.Equal((32, (int?)0, (int?)8), (accept.Sessions!.Count, accept.MoreApprovals, accept.MoreOther));
        Assert.Null(UpdateNowRules.Check(accept));

        Assert.Equal(UpdateBusyView.Cover.Covered, UpdateBusyView.Of(busy, false).CoveredBy(accept));
        Assert.Equal(UpdateBusyView.Cover.Turn, UpdateBusyView.Of([.. busy, S(Id(40), SessionStatus.Working)], false).CoveredBy(accept));
        var asked = busy.Select((s, i) => i == 0 ? S(s.Id, question: QuestionKind.Form) : s).ToList();
        Assert.Equal(UpdateBusyView.Cover.Question, UpdateBusyView.Of(asked, false).CoveredBy(accept));
    }

    [Fact]
    public void With_more_than_32_busy_an_unlisted_turn_that_gains_an_approval_while_a_listed_approval_clears_is_not_covered()
    {
        IReadOnlyList<Session> busy = [S(Id(0), question: QuestionKind.Approval), .. Many(39, from: 1)];
        var accept = UpdateBusyView.Of(busy, false).Shown();
        Assert.Equal(((int?)0, (int?)8), (accept.MoreApprovals, accept.MoreOther));
        Assert.Equal(new UpdateBusySession(Id(0), UpdateBusyState.Approval), accept.Sessions![0]);

        // The listed approval is answered, and a turn left out of the list stops at an approval: the
        // left-out count still fits, but that approval was never confirmed.
        var now = busy.Select(s => s.Id == Id(0) ? S(s.Id, SessionStatus.Idle) : s.Id == Id(39) ? S(s.Id, question: QuestionKind.Approval) : s).ToList();

        Assert.Equal(UpdateBusyView.Cover.Question, UpdateBusyView.Of(now, false).CoveredBy(accept));
    }

    [Fact]
    public void With_32_listed_approvals_and_1_left_out_question_that_question_becoming_an_approval_is_not_covered()
    {
        IReadOnlyList<Session> busy = [.. Many(32, question: QuestionKind.Approval), S(Id(32), question: QuestionKind.Form)];
        var accept = UpdateBusyView.Of(busy, false).Shown();
        Assert.Equal((32, (int?)0, (int?)1), (accept.Sessions!.Count, accept.MoreApprovals, accept.MoreOther));
        Assert.Equal(UpdateBusyView.Cover.Covered, UpdateBusyView.Of(busy, false).CoveredBy(accept));

        IReadOnlyList<Session> now = [.. Many(32, question: QuestionKind.Approval), S(Id(32), question: QuestionKind.Approval)];

        Assert.Equal(UpdateBusyView.Cover.Question, UpdateBusyView.Of(now, false).CoveredBy(accept));
    }

    [Fact]
    public async Task An_accept_with_left_out_numbers_and_a_short_list_or_moreApprovals_beside_a_listed_question_or_turn_is_refused_bad_args()
    {
        var (phone, ch) = await OpenAsync();
        var short_ = new UpdateBusy { Working = 3, Waiting = 0, Sessions = [new(Id(1), UpdateBusyState.Working), new(Id(2), UpdateBusyState.Working)], MoreOther = 1 };
        List<UpdateBusySession> listed = [.. Enumerable.Range(0, 31).Select(i => new UpdateBusySession(Id(i), UpdateBusyState.Approval))];
        var besideQuestion = new UpdateBusy { Working = 0, Waiting = 33, Sessions = [.. listed, new(Id(31), UpdateBusyState.Question)], MoreApprovals = 1 };
        var besideTurn = new UpdateBusy { Working = 1, Waiting = 32, Sessions = [.. listed, new(Id(31), UpdateBusyState.Working)], MoreApprovals = 1 };

        foreach (var accept in new[] { short_, besideQuestion, besideTurn })
        {
            var result = await PressAsync(ch, Press(phone, accept));
            Assert.Equal((UpdateAck.Refused, ManageReason.BadArgs), (result.Outcome, result.Code));
        }
        Assert.Empty(updates.Asks);
    }

    [Fact]
    public async Task Update_now_is_refused_on_a_read_only_machine_reads_included()
    {
        var (phone, ch) = await OpenAsync();
        var press = Press(phone);
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, press)).Outcome);
        readOnly = "delivery.readOnly is set in config.json";

        foreach (var (op, data) in new (string, object)[] { (UpdateProtocol.Get, new { }), (UpdateProtocol.Install, new UpdateCommand { Now = Press(phone) }) })
        {
            var response = await ch.RequestAsync(op, data);
            Assert.False(response.GetProperty("ok").GetBoolean(), op);
            Assert.Contains("read-only", response.GetProperty("error").GetString());
        }
        // The press taken before it turned read-only never leaves for it.
        Assert.Equal((UpdateDepartureOutcome.Refused, ManageReason.ReadOnly), Outcome(await updates.DepartAsync(RequestOf(press))));
        AssertServing();
    }

    [Fact]
    public async Task Update_now_is_refused_with_remote_management_off()
    {
        Compose(new HostSettings(true) { RemoteManage = false });
        var (phone, ch) = await OpenAsync();
        var state = await StateAsync(ch);
        Assert.False(state.CanUpdateNow);
        Assert.Equal(UpdateNowWhy.Disabled, state.UpdateNowWhy);

        var result = await PressAsync(ch, Press(phone));

        Assert.Equal((UpdateAck.Refused, ManageReason.Disabled), (result.Outcome, result.Code));
        Assert.Empty(updates.Asks);
        // Install still works.
        Assert.Equal(UpdateAck.Accepted, (await SendAsync(ch, new UpdateCommand(Guid.NewGuid().ToString()))).Outcome);
    }

    [Fact]
    public async Task Update_now_from_this_machines_own_app_is_refused()
    {
        var (own, ch) = await OpenAsync("This machine's app", "app");
        composed.Manage.NoteLocalApp(own.Id);

        var result = await PressAsync(ch, Press(own));

        Assert.Equal((UpdateAck.Refused, ManageReason.LocalDevice), (result.Outcome, result.Code));
        Assert.Empty(updates.Asks);
    }

    [Fact]
    public async Task An_Update_now_signed_more_than_two_minutes_ago_is_refused()
    {
        var (phone, ch) = await OpenAsync();

        var old = await PressAsync(ch, Press(phone, issued: time.GetUtcNow() - TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1)));
        var long_ = await PressAsync(ch, Sign(phone.Key, Args(), lifetime: TimeSpan.FromMinutes(3)));

        Assert.Equal((UpdateAck.Refused, ManageReason.Expired), (old.Outcome, old.Code));
        Assert.Equal((UpdateAck.Refused, ManageReason.Expired), (long_.Outcome, long_.Code));
        Assert.Empty(updates.Asks);
    }

    [Fact]
    public async Task Update_now_spends_its_own_slot_before_a_shared_one()
    {
        var (phone, ch) = await OpenAsync();

        await UpdateNowAsync(phone, ch);
        Compose();

        Assert.Equal(Shared, (await ManageStateAsync(ch)).Restarts.Left);
        // Its own slot spent, the next takes a shared one.
        await UpdateNowAsync(phone, ch);
        Compose();
        Assert.Equal(Shared - 1, (await ManageStateAsync(ch)).Restarts.Left);
    }

    [Fact]
    public async Task An_Update_now_is_refused_when_its_own_slot_is_spent_and_a_remote_restart_and_an_Update_now_took_the_shared_ones()
    {
        var (phone, ch) = await OpenAsync();
        await UpdateNowAsync(phone, ch);
        Compose();
        await UpdateNowAsync(phone, ch);
        Compose();
        await RemoteRestartAsync(phone, ch);

        var state = await StateAsync(ch);
        var result = await PressAsync(ch, Press(phone));

        Assert.Equal((false, UpdateNowWhy.RestartCap), (state.CanUpdateNow, state.UpdateNowWhy));
        Assert.Equal(Now + ManageService.UpdateNowWindow, state.UpdateNowAt);
        Assert.Equal((UpdateAck.Refused, UpdateCode.RestartCap), (result.Outcome, result.Code));
        Assert.Empty(updates.Asks);
    }

    [Fact]
    public async Task A_remote_restart_never_spends_Update_nows_own_slot()
    {
        var (phone, ch) = await OpenAsync();
        await RemoteRestartAsync(phone, ch);
        Assert.Equal(Shared - 1, (await ManageStateAsync(ch)).Restarts.Left);

        await UpdateNowAsync(phone, ch);
        Compose();

        // The Update now took its own slot, still free after the restart: the shared one left is untouched.
        Assert.Equal(Shared - 1, (await ManageStateAsync(ch)).Restarts.Left);
    }

    [Fact]
    public async Task A_remote_restart_committed_while_an_Update_now_downloads_taking_the_last_shared_slot_with_the_own_slot_spent_leaves_the_Update_now_refused_restart_cap()
    {
        var (phone, ch) = await OpenAsync();
        await UpdateNowAsync(phone, ch);
        Compose();
        await RemoteRestartAsync(phone, ch);
        var press = Press(phone);
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, press)).Outcome);

        // While the app downloads, a remote restart takes the last shared slot; the host comes back.
        time.Advance(TimeSpan.FromMinutes(1));
        await RemoteRestartAsync(phone, ch);
        var departure = await updates.DepartAsync(RequestOf(press));

        Assert.Equal((UpdateDepartureOutcome.Refused, UpdateCode.RestartCap), Outcome(departure));
        Assert.Equal(0, (await ManageStateAsync(ch)).Restarts.Left);
        AssertServing();
    }

    [Fact]
    public async Task Busy_names_a_waiting_approval_as_an_approval_and_never_carries_any_text()
    {
        sessions.Set(S(Id(1), question: QuestionKind.Approval, title: "The secret project"), S(Id(2), question: QuestionKind.Form), S(Id(3), SessionStatus.Working));
        var (_, ch) = await OpenAsync();

        var response = await ch.RequestAsync(UpdateProtocol.Get, new { });
        var busy = response.GetProperty("result").Deserialize<UpdateState>(ProtocolJson.Options)!.Busy!;

        Assert.Equal(
            [new UpdateBusySession(Id(1), UpdateBusyState.Approval), new UpdateBusySession(Id(2), UpdateBusyState.Question), new UpdateBusySession(Id(3), UpdateBusyState.Working)],
            busy.Sessions);
        Assert.Equal((1, 2), (busy.Working, busy.Waiting));
        var wire = response.GetRawText();
        Assert.DoesNotContain("secret", wire, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("shell", wire, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_departure_is_refused_while_an_answer_is_being_typed_and_nothing_is_typed_twice()
    {
        var (phone, ch) = await OpenAsync();
        var press = Press(phone);
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, press)).Outcome);
        await gate.WaitToTypeAsync();

        var departure = await updates.DepartAsync(RequestOf(press));

        Assert.Equal((UpdateDepartureOutcome.Refused, ManageReason.NotQuiet), Outcome(departure));
        // The answer being typed was never cut off, so there is nothing to type again; the host serves on.
        gate.DoneTyping();
        using var next = gate.TryEnterDelivery();
        Assert.NotNull(next);
        Assert.DoesNotContain(log, l => l.StartsWith("restart for update", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_Update_now_press_with_no_signed_request_is_refused_never_taken_as_an_Install()
    {
        var (phone, ch) = await OpenAsync();
        var unsigned = Press(phone) with { Mac = "" };
        var malformed = await ch.RequestAsync(UpdateProtocol.Install, new { now = "install it now" });
        var empty = await PressAsync(ch, new SignedCommand { Body = "", By = "", Mac = "" });
        var noMac = await PressAsync(ch, unsigned);

        Assert.False(malformed.GetProperty("ok").GetBoolean());
        Assert.Equal((UpdateAck.Refused, ManageReason.BadSignature), (empty.Outcome, empty.Code));
        Assert.Equal((UpdateAck.Refused, ManageReason.BadSignature), (noMac.Outcome, noMac.Code));
        Assert.Empty(updates.Asks);
    }

    [Fact]
    public async Task A_plain_Install_cant_be_replayed_as_an_Update_now()
    {
        var (phone, ch) = await OpenAsync();
        var request = Guid.NewGuid().ToString();
        Assert.Equal(UpdateAck.Accepted, (await SendAsync(ch, new UpdateCommand(request))).Outcome);
        Assert.Null(Assert.Single(updates.Asks).Now);

        // Its request was never signed: the host leaves for no such press.
        Assert.Equal((UpdateDepartureOutcome.Refused, ManageReason.Changed), Outcome(await updates.DepartAsync(request)));
        // Nor does a signature for anything else pass as one.
        var restart = await PressAsync(ch, Sign(phone.Key, null, ManageProtocol.Restart, request));
        Assert.Equal((UpdateAck.Refused, ManageReason.BadSignature), (restart.Outcome, restart.Code));
        Assert.Null(Assert.Single(updates.Asks).Now);
    }

    [Fact]
    public async Task A_turn_started_after_the_press_sends_the_Update_now_back_to_waiting_with_its_download_when_the_host_acts()
    {
        var (phone, ch) = await OpenAsync();
        var press = Press(phone);
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, press)).Outcome);
        sessions.Set(S(Id(1), SessionStatus.Working));

        var waiting = await updates.DepartAsync(RequestOf(press));

        Assert.Equal((UpdateDepartureOutcome.Waiting, UpdateWaitingCode.TurnRunning), Outcome(waiting));
        AssertServing();
        // The press is kept: at the next quiet moment within its life it goes.
        sessions.Set(S(Id(1), SessionStatus.Idle));
        Assert.Equal((UpdateDepartureOutcome.Committed, null), Outcome(await updates.DepartAsync(RequestOf(press))));
    }

    [Fact]
    public async Task A_request_the_host_verified_is_refused_as_the_same_press_after_the_host_restarts()
    {
        var (phone, ch) = await OpenAsync();
        // Pressed for a release no longer offered: refused, and recorded.
        var stale = Press(phone, release: "0.4.9");
        Assert.Equal(UpdateCode.ReleaseChanged, (await PressAsync(ch, stale)).Code);
        var taken = Press(phone);
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, taken)).Outcome);

        Compose();
        updates.Report(app, new UpdateReport(Reported with { Available = "0.4.9" }, null, true));
        var again = await PressAsync(ch, stale);
        var resent = await PressAsync(ch, taken);

        Assert.Equal((UpdateAck.Refused, UpdateCode.ReleaseChanged), (again.Outcome, again.Code));
        // Taken before the restart, it is never handed to the app again.
        Assert.Equal(UpdateAck.Accepted, resent.Outcome);
        Assert.Empty(updates.Asks);
    }

    [Fact]
    public async Task A_press_carrying_request_beside_now_is_refused_bad_args()
    {
        var (phone, ch) = await OpenAsync();
        var press = Press(phone);

        var result = await SendAsync(ch, new UpdateCommand(RequestOf(press)) { Now = press });

        Assert.Equal((UpdateAck.Refused, ManageReason.BadArgs), (result.Outcome, result.Code));
        Assert.Empty(updates.Asks);
    }

    [Fact]
    public async Task An_Update_now_past_15_minutes_from_its_signature_is_dropped_expired_spending_no_cap_slot_and_cancelling_no_sign_in()
    {
        var (phone, ch) = await OpenAsync();
        var press = Press(phone);
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, press)).Outcome);

        time.Advance(UpdateProtocol.NowLife);
        var departure = await updates.DepartAsync(RequestOf(press));

        Assert.Equal((UpdateDepartureOutcome.Refused, ManageReason.Expired), Outcome(departure));
        // No lease was taken, so no sign-in was cut off, and no slot was spent.
        AssertServing();
        Assert.True((await StateAsync(ch)).CanUpdateNow);
        Assert.Equal(Shared, (await ManageStateAsync(ch)).Restarts.Left);
        Assert.Equal(UpdateDepartureOutcome.Committed, (await UpdateNowAndOutcomeAsync(phone, ch)).Outcome);
    }

    async Task<UpdateDeparture> UpdateNowAndOutcomeAsync(Device phone, SecureChannel ch)
    {
        var press = Press(phone);
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, press)).Outcome);
        return await updates.DepartAsync(RequestOf(press));
    }

    [Fact]
    public async Task A_committed_update_departure_refuses_new_deliveries_until_the_host_exits()
    {
        var (phone, ch) = await OpenAsync();

        var request = await UpdateNowAsync(phone, ch);

        Assert.Null(gate.TryEnterDelivery());
        await Assert.ThrowsAsync<InvalidOperationException>(gate.WaitToTypeAsync);
        // Nor does the same press leave twice.
        var again = await updates.DepartAsync(request);
        Assert.Equal((UpdateDepartureOutcome.Refused, ManageReason.Changed), Outcome(again));
    }

    [Fact]
    public async Task An_app_too_old_for_Update_now_says_old_app_and_Install_still_works()
    {
        Compose(carries: false);
        var (phone, ch) = await OpenAsync();
        var state = await StateAsync(ch);

        var result = await PressAsync(ch, Press(phone));

        Assert.Equal((false, UpdateNowWhy.OldApp), (state.CanUpdateNow, state.UpdateNowWhy));
        Assert.Equal((UpdateAck.Refused, UpdateNowWhy.OldApp), (result.Outcome, result.Code));
        Assert.Equal(UpdateAck.Accepted, (await SendAsync(ch, new UpdateCommand(Guid.NewGuid().ToString()))).Outcome);
    }

    [Fact]
    public async Task A_departure_with_another_profiles_host_running_is_refused_other_profile()
    {
        var (phone, ch) = await OpenAsync();
        var press = Press(phone);
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, press)).Outcome);
        otherHost = "profile work's host (pid 4120)";

        var departure = await updates.DepartAsync(RequestOf(press));

        Assert.Equal((UpdateDepartureOutcome.Refused, UpdateCode.OtherProfile), Outcome(departure));
        AssertServing();
        // Refused at the commit, it spends no slot: Update now's own one is still there for the next.
        otherHost = null;
        Compose();
        Assert.Equal(UpdateDepartureOutcome.Committed, (await UpdateNowAndOutcomeAsync(phone, ch)).Outcome);
        Compose();
        Assert.Equal(Shared, (await ManageStateAsync(ch)).Restarts.Left);
    }

    [Fact]
    public async Task A_host_that_cant_tell_whether_a_session_is_busy_counts_it_as_busy()
    {
        sessions.Held = new DiscoveryHeld("the claude processes could not be listed", [AgentKind.Claude]);
        var (phone, ch) = await OpenAsync();

        var result = await PressAsync(ch, Press(phone));

        Assert.Equal(UpdateAck.Confirm, result.Outcome);
        Assert.True(result.State!.Busy!.Unknown);
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, Press(phone, result.State.Busy))).Outcome);
    }

    [Fact]
    public async Task A_second_press_is_refused_busy_when_the_app_starts_carrying_out_an_Update_now_while_it_is_checked()
    {
        var (phone, ch) = await OpenAsync();
        var first = Press(phone);
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, first)).Outcome);
        // The app took it and waits for a quiet moment: a new press may replace that one.
        updates.Report(app, new UpdateReport(Reported with { Phase = UpdatePhase.Waiting, Request = RequestOf(first), RequestNow = true }, [RequestOf(first)], true));
        Assert.True((await StateAsync(ch)).CanUpdateNow);
        // While the second press is checked, the app starts carrying out the first after all.
        sessions.OnSnapshot = () => updates.Report(app, new UpdateReport(Reported with { Phase = UpdatePhase.Downloading, Request = RequestOf(first), RequestNow = true }, null, true));

        var second = await PressAsync(ch, Press(phone));

        Assert.Equal((UpdateAck.Refused, ManageReason.Busy), (second.Outcome, second.Code));
        Assert.Empty(updates.Asks);
    }

    static (UpdateDepartureOutcome, string?) Outcome(UpdateDeparture d) => (d.Outcome, d.Code);

    // A file's next write fails: a directory stands where it writes its temporary copy.
    void BlockWrites(string file) => Directory.CreateDirectory(Path.Combine(folder, file + ".tmp"));

    void UnblockWrites(string file) => Directory.Delete(Path.Combine(folder, file + ".tmp"));

    bool SpentOnDisk(string request) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "update-now.json"))).RootElement.GetProperty("presses").EnumerateArray()
            .Single(p => p.GetProperty("request").GetString() == request).GetProperty("done").GetBoolean();

    [Fact]
    public async Task A_departure_whose_press_cant_be_recorded_spent_is_refused_and_starts_nothing()
    {
        var (phone, ch) = await OpenAsync();
        var press = Press(phone);
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, press)).Outcome);
        BlockWrites("update-now.json");

        var departure = await updates.DepartAsync(RequestOf(press));

        Assert.Equal((UpdateDepartureOutcome.Refused, ManageReason.Unavailable), Outcome(departure));
        AssertServing();
        Assert.DoesNotContain(log, l => l.StartsWith("restart for update", StringComparison.Ordinal));
        Assert.Equal(Shared, (await ManageStateAsync(ch)).Restarts.Left);
        // Spent on this host all the same: it is never tried again here.
        UnblockWrites("update-now.json");
        Assert.Equal((UpdateDepartureOutcome.Refused, ManageReason.Changed), Outcome(await updates.DepartAsync(RequestOf(press))));
    }

    [Fact]
    public async Task A_press_that_departed_is_refused_after_the_host_restarts_even_when_a_write_after_its_commit_failed()
    {
        var (phone, ch) = await OpenAsync();
        var press = Press(phone);
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, press)).Outcome);
        // Every write of the record from here on fails: it must already say the press is spent.
        composedOtherHosts = () => { BlockWrites("update-now.json"); return null; };
        Assert.Equal(UpdateDepartureOutcome.Committed, (await updates.DepartAsync(RequestOf(press))).Outcome);
        composedOtherHosts = null;
        UnblockWrites("update-now.json");

        Compose();
        var again = await updates.DepartAsync(RequestOf(press));

        Assert.Equal((UpdateDepartureOutcome.Refused, ManageReason.Changed), Outcome(again));
        AssertServing();
    }

    [Fact]
    public async Task A_host_that_stops_after_spending_the_press_and_before_the_commit_never_lets_it_depart_again()
    {
        var (phone, ch) = await OpenAsync();
        var press = Press(phone);
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, press)).Outcome);
        var crashed = new InvalidOperationException("the host stopped here");
        updates.BeforeCommit = () =>
        {
            Assert.True(SpentOnDisk(RequestOf(press)));
            throw crashed;
        };

        Assert.Same(crashed, await Assert.ThrowsAsync<InvalidOperationException>(() => updates.DepartAsync(RequestOf(press))));

        Compose();
        Assert.Equal((UpdateDepartureOutcome.Refused, ManageReason.Changed), Outcome(await updates.DepartAsync(RequestOf(press))));
        AssertServing();
    }

    [Fact]
    public async Task A_departure_whose_restart_slot_cant_be_recorded_is_refused_and_commits_nothing()
    {
        var (phone, ch) = await OpenAsync();
        var press = Press(phone);
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, press)).Outcome);
        BlockWrites(ManageService.FileName);

        var departure = await updates.DepartAsync(RequestOf(press));

        Assert.Equal((UpdateDepartureOutcome.Refused, ManageReason.Unavailable), Outcome(departure));
        AssertServing();
        Assert.DoesNotContain(log, l => l.StartsWith("restart for update", StringComparison.Ordinal));
        UnblockWrites(ManageService.FileName);
        Compose();
        Assert.Equal(Shared, (await ManageStateAsync(ch)).Restarts.Left);
    }

    [Fact]
    public void Coverage_counts_left_out_sessions_without_overflowing_however_large_the_counts()
    {
        var busy = Many(40);
        var shown = UpdateBusyView.Of(busy, false).Shown();
        var huge = shown with { MoreApprovals = int.MaxValue, MoreOther = int.MaxValue };

        Assert.Equal(UpdateBusyView.Cover.Covered, UpdateBusyView.Of(busy, false).CoveredBy(huge));
        Assert.NotNull(UpdateNowRules.Check(huge));
        Assert.NotNull(UpdateNowRules.Check(new UpdateBusy { Working = UpdateProtocol.BusyMost + 1, Waiting = 0, Sessions = [.. shown.Sessions!], MoreOther = UpdateProtocol.BusyMost + 1 - 32 }));
    }

    [Fact]
    public async Task A_look_at_busy_that_fails_once_doesnt_stop_later_pushes_of_it()
    {
        var (_, ch) = await OpenAsync();
        var subscribed = await ch.RequestAsync(UpdateProtocol.Subscribe, new { });
        Assert.True(subscribed.GetProperty("ok").GetBoolean(), subscribed.GetRawText());
        sessions.OnSnapshot = () => throw new IOException("the sessions could not be read just now");

        sessions.Set(S(Id(1), SessionStatus.Working));
        sessions.Set(S(Id(1), SessionStatus.Working), S(Id(2), SessionStatus.Working));

        using var cts = new CancellationTokenSource(Bound);
        var pushed = await ch.Events.ReadAsync(cts.Token);
        Assert.Equal(UpdateProtocol.Event, pushed.Name);
        Assert.Equal(2, pushed.Data.Deserialize<UpdateState>(ProtocolJson.Options)!.Busy!.Working);
    }

    [Fact]
    public async Task An_Update_now_ask_is_never_handed_to_an_app_that_doesnt_say_it_carries_one()
    {
        var (phone, ch) = await OpenAsync();
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, Press(phone))).Outcome);
        Assert.NotNull(Assert.Single(updates.Asks).Now);

        // An older app reports before taking it: it would read the ask as a plain Install.
        updates.Report(app, new UpdateReport(Reported, null, CarriesUpdateNow: false));

        Assert.Empty(updates.Asks);
    }

    [Fact]
    public async Task A_turn_that_starts_while_the_departures_conditions_are_read_sends_it_back_to_waiting_spending_no_slot()
    {
        var (phone, ch) = await OpenAsync();
        var press = Press(phone);
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, press)).Outcome);
        composedOtherHosts = () => { sessions.Set(S(Id(1), SessionStatus.Working)); return null; };

        var departure = await updates.DepartAsync(RequestOf(press));

        Assert.Equal((UpdateDepartureOutcome.Waiting, UpdateWaitingCode.TurnRunning), Outcome(departure));
        AssertServing();
        Assert.Equal(Shared, (await ManageStateAsync(ch)).Restarts.Left);
        // Its own slot is still there for it at the next quiet moment.
        composedOtherHosts = null;
        sessions.Set(S(Id(1), SessionStatus.Idle));
        Assert.Equal(UpdateDepartureOutcome.Committed, (await updates.DepartAsync(RequestOf(press))).Outcome);
        Compose();
        Assert.Equal(Shared, (await ManageStateAsync(ch)).Restarts.Left);
    }

    [Fact]
    public async Task A_press_whose_15_minutes_end_while_the_departures_conditions_are_read_commits_nothing()
    {
        var (phone, ch) = await OpenAsync();
        var press = Press(phone);
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, press)).Outcome);
        composedOtherHosts = () => { time.Advance(UpdateProtocol.NowLife); return null; };

        var departure = await updates.DepartAsync(RequestOf(press));

        Assert.Equal((UpdateDepartureOutcome.Refused, ManageReason.Expired), Outcome(departure));
        AssertServing();
        Assert.DoesNotContain(log, l => l.StartsWith("restart for update", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_second_departure_for_a_press_already_leaving_is_refused_and_one_refused_never_leaves_again()
    {
        var (phone, ch) = await OpenAsync();
        var press = Press(phone);
        Assert.Equal(UpdateAck.Accepted, (await PressAsync(ch, press)).Outcome);
        UpdateDeparture? during = null;
        composedOtherHosts = () =>
        {
            during = updates.DepartAsync(RequestOf(press)).GetAwaiter().GetResult();
            return "profile work's host (pid 4120)";
        };

        var first = await updates.DepartAsync(RequestOf(press));
        composedOtherHosts = () => null;
        var after = await updates.DepartAsync(RequestOf(press));

        Assert.Equal((UpdateDepartureOutcome.Refused, ManageReason.Busy), Outcome(during!));
        Assert.Equal((UpdateDepartureOutcome.Refused, UpdateCode.OtherProfile), Outcome(first));
        Assert.Equal((UpdateDepartureOutcome.Refused, ManageReason.Changed), Outcome(after));
        AssertServing();
    }

    // The host serves on: a delivery may start, and none is left holding the gate.
    void AssertServing()
    {
        using var delivery = gate.TryEnterDelivery();
        Assert.NotNull(delivery);
    }

    // The live sessions, as the test sets them.
    sealed class FakeSessions : ISessionSource
    {
        IReadOnlyList<Session> all = [];
        long revision;
        public long Revision => Interlocked.Read(ref revision);
        public event EventHandler<SessionChange>? Changed;
        public DiscoveryHeld? Held { get; set; }
        // Runs once, at the next look at the sessions: what may happen while a press is checked.
        public Action? OnSnapshot { get; set; }

        public void Set(params Session[] sessions)
        {
            Volatile.Write(ref all, sessions);
            foreach (var s in sessions) { Changed?.Invoke(this, new SessionChange(SessionChangeKind.Upserted, s.Id, s, Interlocked.Increment(ref revision))); }
        }

        public IReadOnlyList<Session> Snapshot()
        {
            if (OnSnapshot is { } once)
            {
                OnSnapshot = null;
                once();
            }
            return Volatile.Read(ref all);
        }
        public SessionList List(string? cursor = null) => new(Snapshot(), Revision);
        public Session? Get(string id) => Snapshot().FirstOrDefault(s => s.Id == id);
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class AppChannel : IProtocolChannel
    {
        public string PeerId => "local-app";
        public string PeerName => "app";
        public string PeerKind => "local";
        public bool IsOpen => true;
        public Task Closed => Task.Delay(Timeout.Infinite);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }

    sealed class Notices(List<string> into) : IManageNotices
    {
        public Task Notify(string exceptDevice, string machineName, string body)
        {
            lock (into) { into.Add(body); }
            return Task.CompletedTask;
        }
    }
}
