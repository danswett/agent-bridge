using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// When the host notifies, what it sends, and to whom: the rules ported from agent-ha-bridge,
// on a fake clock with a fake source and push sender, so every delay is exact.
public sealed class PushNotifierTests : IDisposable
{
    const string Account = "1";
    const string Machine = "53d1997f0a1b2c3d4e5f60718293a4b5";
    const string Phone = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string Laptop = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    static readonly DateTimeOffset T = new(2026, 10, 28, 4, 0, 0, TimeSpan.Zero);

    readonly FakeTimeProvider time = new(T);
    readonly FakeSource source = new();
    readonly FakeSender sender = new(Account);
    readonly Dictionary<string, (P256KeyPair Key, byte[] Auth)> browsers = [];
    readonly List<string> logs = [];
    readonly string folder = Path.Combine(Path.GetTempPath(), "abpush-" + Guid.NewGuid().ToString("N"));
    PushNotifier notifier = null!;
    PushStore store = null!;
    RemoteAuthorization? trust;

    public PushNotifierTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        trust?.Dispose();
        foreach (var b in browsers.Values) { b.Key.Dispose(); }
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    string StorePath => Path.Combine(folder, "push-subscriptions.json");

    PushNotifier Start(PushOptions? options = null, params Session[] existing)
    {
        source.Sessions.AddRange(existing);
        store = PushStore.Load(StorePath, Account);
        notifier = new PushNotifier(source, new MachineInfo(Machine, "DEV", "windows", "0.1.0"), store, options, logs.Add, time, trust);
        if (trust is not null) { trust.Changed += notifier.RevalidateTrust; notifier.RevalidateTrust(); }
        notifier.Attach(sender);
        return notifier;
    }

    static Session S(string id, SessionStatus status, string? response = null, Question? question = null, int turns = 1, string? title = "Fix the login") => new()
    {
        Id = id,
        Agent = AgentKind.Copilot,
        Title = title,
        Status = question is null ? status : SessionStatus.Waiting,
        StatusSince = T,
        UpdatedAt = T,
        Activity = new Activity { Response = response, TurnCount = turns },
        Question = question,
    };

    static Question Q(string id, string message = "Which database?", QuestionKind kind = QuestionKind.Form) =>
        new() { Id = id, Kind = kind, Message = message, AskedAt = T };

    PushPeer Peer(string device, string account = Account, bool inAccount = true) => new(device, device == Phone ? "iPhone" : "Laptop", account, inAccount);

    object? Subscribe(string device, string? endpoint = null, PushPeer? peer = null, string? name = null, string app = "https://danswett.github.io/agent-bridge/app/")
    {
        if (!browsers.TryGetValue(device, out var b)) { browsers[device] = b = (P256KeyPair.Generate(), System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)); }
        var data = JsonSerializer.SerializeToElement(new
        {
            endpoint = endpoint ?? $"https://web.push.apple.com/{device}",
            keys = new { p256dh = B64Url.Encode(b.Key.Public), auth = B64Url.Encode(b.Auth) },
            app,
            device = name ?? (device == Phone ? "iPhone" : "Laptop"),
        });
        return notifier.Handle(peer ?? Peer(device), Ops.PushSubscribe, data);
    }

    // What the browser decrypts: the endpoint's device, the notification and its push headers.
    List<(string Device, JsonElement Notification, int Ttl, string Urgency, string? Topic)> Received() =>
        [.. sender.Sent.Select(p =>
        {
            var device = p.Endpoint.Split('/')[^1];
            var (key, auth) = browsers[device];
            var plain = WebPushCrypto.Decrypt(p.Body, key, auth);
            Assert.True(plain.Length <= PushNotice.MaxBytes);
            return (device, JsonDocument.Parse(plain).RootElement.GetProperty("notification"), p.Ttl, p.Urgency, p.Topic);
        })];

    static string Kind(JsonElement n) => n.GetProperty("data").GetProperty("kind").GetString()!;

    async Task After(TimeSpan t)
    {
        time.Advance(t);
        await notifier.PumpAsync();
    }

    [Fact]
    public async Task A_new_question_is_notified_once_after_its_delay_however_often_the_session_changes()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Working));
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));

        await After(TimeSpan.FromSeconds(1));
        Assert.Empty(sender.Sent);
        await After(TimeSpan.FromSeconds(1));
        var (device, n, ttl, urgency, topic) = Assert.Single(Received());
        Assert.Equal(Phone, device);
        Assert.Equal("Fix the login", n.GetProperty("title").GetString());
        Assert.Equal("DEV · waiting on you\nWhich database?", n.GetProperty("body").GetString());
        Assert.Equal("question", Kind(n));
        Assert.Equal("q1", n.GetProperty("data").GetProperty("question").GetString());
        Assert.Equal($"https://danswett.github.io/agent-bridge/app/?host={Machine}&session=s1&question=q1", n.GetProperty("navigate").GetString());
        Assert.Equal((86400, "high", PushNotice.Topic(Machine, "s1")), (ttl, urgency, topic));

        // The same question in later upserts, and seen pending again after an answer was
        // typed (agent-ha-bridge's reason for keeping its dedupe until the question is done).
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        source.Upsert(S("s1", SessionStatus.Waiting, response: "thinking", question: Q("q1")));
        await After(TimeSpan.FromSeconds(10));
        Assert.Single(sender.Sent);
    }

    // #780: what a reboot took, to every device, as words only. An app from before restored reads
    // it as a question; with no question and no session named, it opens the machine and nothing
    // that could be answered.
    [Fact]
    public async Task What_a_reboot_took_reaches_every_device_naming_no_question_and_opening_only_the_machine()
    {
        Start();
        Subscribe(Phone);
        Subscribe(Laptop);

        notifier.NotifyRestore(new RestoreNotice
        {
            At = T,
            Cause = RestoreCause.Reboot,
            Why = RestoreWhy.Pending,
            Offered = [new RestoreSession { Id = "s1", Agent = AgentKind.Copilot, Title = "Fix the login", MidTurn = true }, new RestoreSession { Id = "s2", Agent = AgentKind.Claude, QuestionLost = true }],
        });
        await After(TimeSpan.Zero);

        var received = Received();
        Assert.Equal([Phone, Laptop], received.Select(r => r.Device).Order(StringComparer.Ordinal));
        var (_, n, ttl, urgency, topic) = received.Single(r => r.Device == Phone);
        Assert.Equal("restored", Kind(n));
        Assert.Equal("DEV restarted", n.GetProperty("title").GetString());
        Assert.Equal("2 sessions went with it.\nMid-turn: Fix the login.\nA question went unanswered in Claude session.\nResume them from Launch.", n.GetProperty("body").GetString());
        Assert.Equal($"https://danswett.github.io/agent-bridge/app/?host={Machine}", n.GetProperty("navigate").GetString());
        Assert.Equal("", n.GetProperty("data").GetProperty("session").GetString());
        Assert.False(n.GetProperty("data").TryGetProperty("question", out _));
        Assert.Equal((86400, "normal", PushNotice.Topic(Machine, "~restore")), (ttl, urgency, topic));
    }

    // A subscribed URL that already routes somewhere (the page it was installed from was a session's)
    // still opens only the machine: its routing is replaced, not appended to, and nothing else in it
    // is lost.
    [Fact]
    public async Task What_a_reboot_took_opens_only_the_machine_from_an_app_url_that_already_routes_to_a_session()
    {
        Start();
        Subscribe(Phone, app: "https://danswett.github.io/agent-bridge/app/?v=2&host=other&session=s9&question=q9#top");

        notifier.NotifyRestore(new RestoreNotice
        {
            At = T,
            Cause = RestoreCause.Crash,
            Why = RestoreWhy.Pending,
            Offered = [new RestoreSession { Id = "s1", Agent = AgentKind.Copilot, Title = "Fix the login" }],
        });
        await After(TimeSpan.Zero);

        var n = Assert.Single(Received()).Notification;
        Assert.Equal($"https://danswett.github.io/agent-bridge/app/?v=2&host={Machine}", n.GetProperty("navigate").GetString());
    }

    // #780 Host B: what was restored is said first, and what was not after it, with those that died
    // again too soon after a restore counted.
    [Fact]
    public async Task What_a_reboot_took_says_how_many_were_restored_and_which_are_left_to_resume()
    {
        Start();
        Subscribe(Phone);

        notifier.NotifyRestore(new RestoreNotice
        {
            At = T,
            Cause = RestoreCause.Reboot,
            Why = RestoreWhy.Pending,
            Restored = 3,
            Loops = 1,
            Offered = [new RestoreSession { Id = "s1", Agent = AgentKind.Copilot, Title = "Fix the login" }],
        });
        await After(TimeSpan.Zero);

        var (_, n, _, _, _) = Assert.Single(Received());
        Assert.Equal("3 sessions were restored, idle.\n1 more went with it.\n1 died again soon after a restore, so it wasn't restored again.\nResume them from Launch.", n.GetProperty("body").GetString());
    }

    // Restored or not, a turn cut short and a question nobody answered are said.
    [Fact]
    public async Task What_restored_sessions_lost_is_said_when_every_session_was_restored()
    {
        Start();
        Subscribe(Phone);

        notifier.NotifyRestore(new RestoreNotice { At = T, Cause = RestoreCause.Reboot, Why = RestoreWhy.Pending, Restored = 2 },
            [new RestoreSession { Id = "s1", Agent = AgentKind.Copilot, Title = "Fix the login", MidTurn = true }, new RestoreSession { Id = "s2", Agent = AgentKind.Claude, QuestionLost = true }]);
        await After(TimeSpan.Zero);

        var (_, n, _, _, _) = Assert.Single(Received());
        Assert.Equal("2 sessions were restored, idle.\nMid-turn: Fix the login.\nA question went unanswered in Claude session.", n.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_question_answered_at_the_machine_before_its_delay_is_never_notified()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(1));
        source.Upsert(S("s1", SessionStatus.Working));

        await After(TimeSpan.FromSeconds(10));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public async Task An_answered_question_is_replaced_quietly_where_it_was_shown_but_not_on_the_device_that_answered()
    {
        Start();
        Subscribe(Phone);
        Subscribe(Laptop);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal(2, sender.Sent.Count);

        notifier.Answered(Laptop, "s1", "q1");
        source.Upsert(S("s1", SessionStatus.Working));
        await After(TimeSpan.FromSeconds(2));

        var resolved = Received().Skip(2).ToList();
        var (device, n, _, urgency, topic) = Assert.Single(resolved);
        Assert.Equal(Phone, device);
        Assert.Equal("resolved", Kind(n));
        Assert.True(n.GetProperty("silent").GetBoolean());
        Assert.Equal("DEV · no longer waiting", n.GetProperty("body").GetString());
        // Replaces the question: on the device (tag) and, if still undelivered, at the push
        // service (topic).
        Assert.Equal($"{Machine}:s1", n.GetProperty("tag").GetString());
        Assert.Equal((PushNotice.Topic(Machine, "s1"), "normal"), (topic, urgency));
    }

    [Fact]
    public async Task A_session_that_ends_while_its_question_is_shown_says_so()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));

        source.Remove("s1");
        await After(TimeSpan.FromSeconds(1));

        var n = Received()[^1].Notification;
        Assert.Equal(("resolved", "DEV · session ended"), (Kind(n), n.GetProperty("body").GetString()));
        Assert.Equal(2, sender.Sent.Count);
    }

    [Fact]
    public async Task A_finished_turn_is_notified_with_the_start_of_its_response_after_it_stays_idle()
    {
        Start();
        Subscribe(Phone);
        var response = "First line.\n\n" + new string('x', 2000);
        source.Upsert(S("s1", SessionStatus.Working, turns: 0));
        source.Upsert(S("s1", SessionStatus.Idle, response: response));

        await After(TimeSpan.FromSeconds(2));
        Assert.Empty(sender.Sent);
        await After(TimeSpan.FromSeconds(1));
        var (_, n, ttl, urgency, _) = Assert.Single(Received());
        Assert.Equal("finished", Kind(n));
        var body = n.GetProperty("body").GetString()!;
        Assert.StartsWith("DEV · finished\nFirst line. xxx", body);
        Assert.EndsWith("…", body);
        Assert.Equal("DEV · finished\n".Length + PushNotice.PreviewChars + 1, body.Length);
        Assert.False(n.GetProperty("data").TryGetProperty("question", out _));
        Assert.Equal($"https://danswett.github.io/agent-bridge/app/?host={Machine}&session=s1", n.GetProperty("navigate").GetString());
        Assert.Equal((21600, "normal"), (ttl, urgency));
    }

    // #913: each notice says its tier, so a client follows the host's class rather than its own
    // rules. A resolved notice withdraws one and is none itself.
    [Fact]
    public async Task A_question_goes_as_needs_you_its_finished_turn_as_done_and_its_withdrawal_with_no_class()
    {
        Start();
        Subscribe(Phone);
        Subscribe(Laptop);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        notifier.Answered(Laptop, "s1", "q1");
        source.Upsert(S("s1", SessionStatus.Working));
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s1", SessionStatus.Idle, response: "Postgres it is.", turns: 2));
        await After(TimeSpan.FromSeconds(3));

        var classes = Received()
            .Select(r => (Kind(r.Notification), r.Notification.GetProperty("data").TryGetProperty("class", out var c) ? c.GetString() : null))
            .Distinct().ToList();
        Assert.Equal(new (string, string?)[] { ("question", "needsYou"), ("resolved", null), ("finished", "done") }, classes);
    }

    // #913 (the owner's B): a finished turn is Done, which waits and never alerts, on any client.
    // The question before it still alerts.
    [Fact]
    public async Task A_finished_turn_goes_silent_while_a_question_does_not()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s1", SessionStatus.Working));
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s1", SessionStatus.Idle, response: "Postgres it is.", turns: 2));
        await After(TimeSpan.FromSeconds(3));

        var silent = Received()
            .Select(r => (Kind(r.Notification), r.Notification.TryGetProperty("silent", out var s) && s.GetBoolean()))
            .Where(r => r.Item1 != "resolved").Distinct().ToList();
        Assert.Equal(new[] { ("question", false), ("finished", true) }, silent);
    }

    // #913 Q3: a finished push is always done, even for a session whose own class is needsYou at
    // the time (here it stopped responding as its turn ended), so no client alerts it as finished.
    [Fact]
    public async Task A_finished_push_is_never_needs_you_even_when_its_session_is()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Working));
        await After(TimeSpan.FromSeconds(1));
        var stalled = S("s1", SessionStatus.Idle, response: "Postgres it is.", turns: 2) with
        {
            Attention = new Attention { Kind = AttentionKind.NotResponding, Since = T, LastEventAt = T },
        };
        Assert.Equal(NotificationClass.NeedsYou, AgentBridge.Host.Core.NotificationClasses.Of(stalled));
        source.Upsert(stalled);
        await After(TimeSpan.FromSeconds(3));

        var n = Assert.Single(Received()).Notification;
        Assert.Equal(("finished", "done"), (Kind(n), n.GetProperty("data").GetProperty("class").GetString()));
    }

    // #913: sign-in needed and sessions a crash took are needsYou; a planned reboot or restart of
    // the host is no tier of #913's, so its notice carries none and clients keep today's rules.
    [Theory]
    [InlineData(RestoreCause.Crash, "needsYou")]
    [InlineData(RestoreCause.Reboot, null)]
    [InlineData(RestoreCause.Restart, null)]
    public async Task What_a_crash_took_goes_as_needs_you_and_a_reboot_with_no_class(RestoreCause cause, string? expected)
    {
        Start();
        Subscribe(Phone);
        notifier.NotifyRestore(new RestoreNotice
        {
            At = T,
            Cause = cause,
            Why = RestoreWhy.Pending,
            Offered = [new RestoreSession { Id = "s1", Agent = AgentKind.Copilot, Title = "Fix the login", MidTurn = true }],
        });
        await After(TimeSpan.Zero);

        var data = Assert.Single(Received()).Notification.GetProperty("data");
        Assert.Equal(expected, data.TryGetProperty("class", out var c) ? c.GetString() : null);
    }

    [Fact]
    public async Task A_sign_in_notice_goes_as_needs_you()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Idle, response: "Done.", turns: 2) with
        {
            Attention = new Attention { Kind = AttentionKind.SignInNeeded, Since = T, Agent = AgentKind.Copilot, Provider = "github" },
        });
        await After(TimeSpan.Zero);

        var n = Assert.Single(Received()).Notification;
        Assert.Equal(("signIn", "needsYou"), (Kind(n), n.GetProperty("data").GetProperty("class").GetString()));
    }

    [Fact]
    public async Task A_turn_that_carries_on_within_its_delay_is_not_notified_until_it_really_finishes()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Working));
        source.Upsert(S("s1", SessionStatus.Idle, response: "one", turns: 1));
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s1", SessionStatus.Working, response: "one", turns: 1));
        await After(TimeSpan.FromSeconds(5));
        Assert.Empty(sender.Sent);

        source.Upsert(S("s1", SessionStatus.Idle, response: "two", turns: 2));
        await After(TimeSpan.FromSeconds(3));
        Assert.Equal("DEV · finished\ntwo", Assert.Single(Received()).Notification.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Only_a_turn_seen_ending_with_a_response_is_notified_and_each_once()
    {
        // Idle with a response when the host started: not news.
        Start(existing: S("old", SessionStatus.Idle, response: "earlier"));
        Subscribe(Phone);
        source.Upsert(S("old", SessionStatus.Idle, response: "earlier", title: "renamed"));
        // A turn that ends with nothing to say.
        source.Upsert(S("s1", SessionStatus.Working));
        source.Upsert(S("s1", SessionStatus.Idle, response: "  "));
        await After(TimeSpan.FromSeconds(5));
        Assert.Empty(sender.Sent);

        source.Upsert(S("s2", SessionStatus.Working));
        source.Upsert(S("s2", SessionStatus.Idle, response: "done"));
        await After(TimeSpan.FromSeconds(5));
        // The same finished turn seen ending again (a status flap) is the same notice.
        source.Upsert(S("s2", SessionStatus.Working, response: "done"));
        source.Upsert(S("s2", SessionStatus.Idle, response: "done"));
        await After(TimeSpan.FromSeconds(5));
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task A_device_on_screen_is_not_sent_a_finished_turn_it_sees_and_one_that_is_not_on_screen_is()
    {
        Start();
        Subscribe(Phone);
        Subscribe(Laptop);
        notifier.Watching = device => device == Phone;
        source.Upsert(S("s1", SessionStatus.Working, turns: 0));
        source.Upsert(S("s1", SessionStatus.Idle, response: "Done."));

        await After(TimeSpan.FromSeconds(3));
        Assert.Equal([Laptop], Received().Select(r => r.Device));
    }

    [Fact]
    public async Task A_finished_turn_waiting_for_the_relay_is_not_sent_to_a_device_that_has_come_on_screen_since()
    {
        Start();
        Subscribe(Phone);
        sender.Connected = false;
        source.Upsert(S("s1", SessionStatus.Working, turns: 0));
        source.Upsert(S("s1", SessionStatus.Idle, response: "Done."));
        await After(TimeSpan.FromSeconds(3));
        Assert.Equal(1, notifier.Pending);

        notifier.Watching = device => device == Phone;
        var next = new FakeSender(Account);
        notifier.Attach(next);
        await notifier.PumpAsync();
        Assert.Empty(next.Sent);
        Assert.Equal(0, notifier.Pending);
    }

    [Fact]
    public async Task Finished_turns_of_one_session_reach_a_device_at_most_once_in_ten_minutes_until_it_replies()
    {
        Start();
        Subscribe(Phone);
        async Task TurnAsync(string response, int turns)
        {
            source.Upsert(S("s1", SessionStatus.Working, turns: turns - 1));
            source.Upsert(S("s1", SessionStatus.Idle, response: response, turns: turns));
            await After(TimeSpan.FromSeconds(3));
        }
        string LastBody() => Received()[^1].Notification.GetProperty("body").GetString()!;

        await TurnAsync("one", 1);
        Assert.Single(sender.Sent);
        await TurnAsync("two", 2);
        await TurnAsync("three", 3);
        Assert.Single(sender.Sent);

        // Only the latest, once the ten minutes since the last are up.
        await After(new PushOptions().FinishedCollapse);
        Assert.Equal(2, sender.Sent.Count);
        Assert.Equal("DEV · finished\nthree", LastBody());

        // A reply from the phone: the turn it starts is awaited there, and goes at once.
        notifier.Replied(Phone, "s1");
        await TurnAsync("four", 4);
        Assert.Equal(3, sender.Sent.Count);
        Assert.Equal("DEV · finished\nfour", LastBody());
    }

    static readonly Attention SignedOut = new() { Kind = AttentionKind.SignInNeeded, Since = T, Agent = AgentKind.Copilot, Provider = "github" };

    // The pushes about sessions, without the machine's sign-in notices.
    List<string> SessionKinds() => [.. Received().Select(r => Kind(r.Notification)).Where(k => k != "signIn")];

    int SignInsSent() => Received().Count(r => Kind(r.Notification) == "signIn");

    // #800 P0: a 401 stopped every session on the machine, each idle beside its last answer, and
    // nothing told the owner. One notice per agent and account, at once, on every device, in ui.md
    // §6.13's words; never per session, never for an agent error; again hourly while it lasts.
    [Fact]
    public async Task Sessions_that_need_sign_in_send_one_notice_per_agent_and_account_at_once_and_again_hourly_while_any_still_does()
    {
        Start();
        Subscribe(Phone);
        Subscribe(Laptop);
        var octo = SignedOut with { Account = "octo" };
        source.Upsert(S("s1", SessionStatus.Idle, response: "Done.", turns: 2) with { Attention = octo });
        source.Upsert(S("s2", SessionStatus.Idle, response: "Done.", turns: 2) with { Attention = octo with { Since = T.AddSeconds(5) } });
        source.Upsert(S("s3", SessionStatus.Idle, response: "Done.", turns: 2) with { Attention = new Attention { Kind = AttentionKind.Error, Since = T, Agent = AgentKind.Copilot } });
        await After(TimeSpan.Zero);

        var sent = Received();
        Assert.Equal([Phone, Laptop], sent.Select(r => r.Device).Order());
        var (_, n, ttl, urgency, topic) = sent.Single(r => r.Device == Phone);
        var key = PushNotice.SignInKey(AgentKind.Copilot, "octo");
        Assert.Equal(("Copilot on DEV needs sign-in", "2 sessions affected", $"{Machine}:{key}"), (n.GetProperty("title").GetString(), n.GetProperty("body").GetString(), n.GetProperty("tag").GetString()));
        Assert.Equal($"https://danswett.github.io/agent-bridge/app/?host={Machine}&signin=copilot", n.GetProperty("navigate").GetString());
        var data = n.GetProperty("data");
        Assert.Equal(("signIn", "copilot", 2, false), (Kind(n), data.GetProperty("agent").GetString(), data.GetProperty("sessions").GetInt32(), data.TryGetProperty("session", out _)));
        Assert.Equal((21600, "high", PushNotice.Topic(Machine, key)), (ttl, urgency, topic));
        // The owner's question sound (ui.md §6.13), and nothing of the account.
        Assert.False(n.TryGetProperty("silent", out _));
        Assert.DoesNotContain("octo", n.GetRawText(), StringComparison.Ordinal);

        await After(TimeSpan.FromMinutes(59));
        Assert.Equal(2, sender.Sent.Count);
        await After(TimeSpan.FromMinutes(1));
        Assert.Equal(4, SignInsSent());
    }

    [Fact]
    public async Task Once_every_session_is_signed_in_again_a_waiting_sign_in_notice_is_withdrawn_and_the_next_failure_notifies_at_once()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Idle, response: "Done.", turns: 2) with { Attention = SignedOut });
        await After(TimeSpan.Zero);
        Assert.Equal(1, SignInsSent());
        source.Upsert(S("s1", SessionStatus.Idle, response: "Answered.", turns: 2));
        await After(TimeSpan.Zero);

        // Within the hour, but a new failure after the last was cleared: at once. Cleared again
        // before it could go: withdrawn.
        sender.Connected = false;
        source.Upsert(S("s1", SessionStatus.Idle, response: "Answered.", turns: 3) with { Attention = SignedOut with { Since = T.AddMinutes(1) } });
        await After(TimeSpan.Zero);
        Assert.Equal(1, notifier.Pending);
        source.Upsert(S("s1", SessionStatus.Idle, response: "Answered again.", turns: 3));
        await After(TimeSpan.Zero);
        Assert.Equal(0, notifier.Pending);

        sender.Connected = true;
        source.Upsert(S("s1", SessionStatus.Idle, response: "Answered again.", turns: 4) with { Attention = SignedOut with { Since = T.AddMinutes(2) } });
        await After(TimeSpan.Zero);
        Assert.Equal(2, SignInsSent());
    }

    // A machine stopped on a 401 reads idle and takes every update: each restart re-alerted every
    // phone. When a sign-in notice went is kept with the subscriptions.
    [Fact]
    public async Task A_host_that_restarts_while_sessions_still_need_sign_in_keeps_to_the_hourly_repeat()
    {
        var signedOut = S("s1", SessionStatus.Idle, response: "Done.", turns: 2) with { Attention = SignedOut };
        Start(null, signedOut);
        Subscribe(Phone);
        await After(TimeSpan.Zero);
        Assert.Equal(1, SignInsSent());

        await notifier.DisposeAsync();
        time.Advance(TimeSpan.FromMinutes(20));
        Start();
        await After(TimeSpan.Zero);
        Assert.Equal(1, SignInsSent());
        await After(TimeSpan.FromMinutes(40));
        Assert.Equal(2, SignInsSent());
    }

    // Signed in again and failing again while a push is in flight, before the next pump: a new
    // incident, notified at once rather than held for the rest of the hour.
    [Fact]
    public async Task A_sign_in_that_recovers_and_fails_again_while_a_push_is_in_flight_notifies_again_at_once()
    {
        Start();
        Subscribe(Phone);
        var first = true;
        sender.Respond = _ =>
        {
            if (first)
            {
                first = false;
                source.Upsert(S("s1", SessionStatus.Idle, response: "Answered.", turns: 2));
                source.Upsert(S("s1", SessionStatus.Idle, response: "Answered.", turns: 3) with { Attention = SignedOut with { Since = T.AddMinutes(1) } });
            }
            return new PushResult(201, null);
        };
        source.Upsert(S("s1", SessionStatus.Idle, response: "Done.", turns: 2) with { Attention = SignedOut });
        await After(TimeSpan.Zero);
        await After(TimeSpan.Zero);

        Assert.Equal(2, SignInsSent());
    }

    // A notice still waiting for the relay when the host restarted never reached anyone: the next
    // host makes it again rather than counting the hour from it.
    [Fact]
    public async Task A_sign_in_notice_lost_with_a_restart_before_it_went_is_made_again_at_once()
    {
        var signedOut = S("s1", SessionStatus.Idle, response: "Done.", turns: 2) with { Attention = SignedOut };
        Start(null, signedOut);
        Subscribe(Phone);
        sender.Connected = false;
        await After(TimeSpan.Zero);
        Assert.Equal(1, notifier.Pending);

        await notifier.DisposeAsync();
        sender.Connected = true;
        Start();
        await After(TimeSpan.Zero);
        Assert.Equal(1, SignInsSent());
    }

    [Fact]
    public async Task A_sign_in_notice_is_not_sent_to_the_next_device_once_the_sessions_are_signed_in_again()
    {
        Start();
        Subscribe(Phone);
        Subscribe(Laptop);
        sender.Respond = _ =>
        {
            source.Upsert(S("s1", SessionStatus.Idle, response: "Answered.", turns: 2));
            return new PushResult(201, null);
        };
        source.Upsert(S("s1", SessionStatus.Idle, response: "Done.", turns: 2) with { Attention = SignedOut });
        await After(TimeSpan.Zero);

        Assert.Equal(1, SignInsSent());
    }

    // Its repeat due with no device left to tell made nothing, and woke the loop at once, forever.
    [Fact]
    public async Task A_sign_in_repeat_with_no_device_to_tell_does_not_spin_the_notifier()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Idle, response: "Done.", turns: 2) with { Attention = SignedOut });
        await After(TimeSpan.Zero);
        notifier.Handle(Peer(Phone), Ops.PushUnsubscribe, default);

        time.Advance(TimeSpan.FromMinutes(61));
        Assert.Equal(Timeout.InfiniteTimeSpan, await notifier.PumpAsync());
    }

    // #945: the relay came back a second before the hourly repeat was due, and the push it was
    // waiting for was still in flight when that hour passed. Refused, it left nothing to wake
    // the notifier, and an idle signed-out machine sent no more reminders.
    [Fact]
    public async Task A_sign_in_repeat_that_comes_due_while_a_push_in_flight_is_dropped_still_goes()
    {
        Start();
        Subscribe(Phone);
        sender.Connected = false;
        source.Upsert(S("s1", SessionStatus.Idle, response: "Done.", turns: 2) with { Attention = SignedOut });
        await After(TimeSpan.Zero);
        await After(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59));
        sender.Connected = true;
        sender.Respond = _ =>
        {
            time.Advance(TimeSpan.FromSeconds(2));
            return new PushResult(400, "bad request");
        };

        var wait = await notifier.PumpAsync();
        Assert.Equal(0, SignInsSent());
        Assert.InRange(wait, TimeSpan.FromMilliseconds(1), TimeSpan.FromSeconds(1));

        sender.Respond = null;
        await After(wait);
        Assert.Equal(1, SignInsSent());
    }

    // ...and one that did reach the device counts the next hour from then (v1-sessions.md, Push signIn).
    [Fact]
    public async Task A_sign_in_push_delivered_after_its_repeat_came_due_counts_the_next_hour_from_its_delivery()
    {
        Start();
        Subscribe(Phone);
        sender.Connected = false;
        source.Upsert(S("s1", SessionStatus.Idle, response: "Done.", turns: 2) with { Attention = SignedOut });
        await After(TimeSpan.Zero);
        await After(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59));
        sender.Connected = true;
        sender.Respond = _ =>
        {
            time.Advance(TimeSpan.FromSeconds(2));
            return new PushResult(201, null);
        };

        await notifier.PumpAsync();
        Assert.Equal(1, SignInsSent());
        sender.Respond = null;
        await After(TimeSpan.FromMinutes(59) + TimeSpan.FromSeconds(59));
        Assert.Equal(1, SignInsSent());
        await After(TimeSpan.FromSeconds(1));
        Assert.Equal(2, SignInsSent());
    }

    // D (#800 P0): Daily's own session at 09:52:24 PT on 2026-11-05, captured read-only from its
    // events.jsonl and redacted (tests/fixtures/copilot/signin-401.jsonl). The CLI wrote turn_end,
    // then session.error 9 ms later, and read between the two the session was idle with the previous
    // answer, exactly as a finished turn. Read by the real SessionSource, one write at a time.
    [Fact]
    public async Task Dailys_captured_401_reads_needs_sign_in_and_sends_one_sign_in_push_and_no_finished_push()
    {
        const string id = "0d800000-0000-4000-8000-0000000000ff";
        using var f = new Fixture();
        var events = f.CopilotSession(id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        Fixture.Append(events, Copilot.User("Ship it", 1), Copilot.TurnStart(2), Copilot.Assistant("Shipped.", 3), Copilot.TurnEnd(4));
        await using var real = f.Source();
        // The notifier hears each change before this test's own handler does: it subscribes first.
        using (var first = new SemaphoreSlim(0))
        {
            void Seen(object? _, SessionChange c) => first.Release();
            real.Changed += Seen;
            await real.RefreshAsync();
            Assert.True(await first.WaitAsync(TimeSpan.FromSeconds(10)), "the session was listed");
            real.Changed -= Seen;
        }
        store = PushStore.Load(StorePath, Account);
        notifier = new PushNotifier(real, new MachineInfo(Machine, "DEV", "windows", "0.1.0"), store, null, logs.Add, f.Time);
        notifier.Attach(sender);
        Subscribe(Phone);
        using var changes = new SemaphoreSlim(0);
        real.Changed += (_, c) => { if (c.Id == id) { changes.Release(); } };

        var captured = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "copilot", "signin-401.jsonl"));
        var firstError = Array.FindIndex(captured, l => l.Contains("\"session.error\"", StringComparison.Ordinal));
        async Task Write(params string[] lines)
        {
            f.Time.Advance(TimeSpan.FromSeconds(1));
            Fixture.Append(events, lines);
            await real.RefreshAsync();
            Assert.True(await changes.WaitAsync(TimeSpan.FromSeconds(10)), "the session changed");
            await notifier.PumpAsync();
        }
        await Write(captured[..2]);
        Assert.Equal(SessionStatus.Working, real.Get(id)!.Status);
        await Write(captured[2..firstError]);
        Assert.Equal(SessionStatus.Idle, real.Get(id)!.Status);
        Assert.Null(real.Get(id)!.Attention);
        await Write(captured[firstError]);
        await Write(captured[(firstError + 1)..]);
        f.Time.Advance(TimeSpan.FromMinutes(10));
        await notifier.PumpAsync();

        var session = real.Get(id)!;
        Assert.Equal((SessionStatus.Idle, AttentionKind.SignInNeeded), (session.Status, session.Attention?.Kind));
        Assert.Equal(DateTimeOffset.Parse("2026-11-05T17:53:03.004Z", System.Globalization.CultureInfo.InvariantCulture), session.Attention!.Since);
        // The previous turn's answer is still beside it: the attention says what it is, and no push quotes it.
        Assert.Equal("Shipped.", session.Activity.Response);
        Assert.Equal(["signIn"], Received().Select(r => Kind(r.Notification)));
        await notifier.DisposeAsync();
    }

    // #845: the failed turn leaves the last answer beside a truthful idle; quoted as "finished",
    // it read as the new prompt's answer.
    [Fact]
    public async Task A_new_prompt_that_fails_sign_in_after_an_earlier_answer_sends_no_finished_push()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Working, turns: 0));
        source.Upsert(S("s1", SessionStatus.Idle, response: "The build is green."));
        await After(TimeSpan.FromSeconds(3));
        Assert.Single(sender.Sent);
        await After(new PushOptions().FinishedCollapse);

        source.Upsert(S("s1", SessionStatus.Working, response: "The build is green.", turns: 1));
        source.Upsert(S("s1", SessionStatus.Idle, response: "The build is green.", turns: 2) with { Attention = SignedOut });
        await After(new PushOptions().FinishedCollapse);

        Assert.Equal(["finished"], SessionKinds());
        Assert.Equal(1, SignInsSent());
    }

    [Fact]
    public async Task A_finished_turn_still_queued_when_the_next_prompt_fails_sign_in_is_withdrawn()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Working, turns: 0));
        source.Upsert(S("s1", SessionStatus.Idle, response: "one"));
        await After(TimeSpan.FromSeconds(3));
        source.Upsert(S("s1", SessionStatus.Working, response: "one"));
        source.Upsert(S("s1", SessionStatus.Idle, response: "two", turns: 2));
        await After(TimeSpan.FromSeconds(3));
        Assert.Equal(1, notifier.Pending);

        source.Upsert(S("s1", SessionStatus.Working, response: "two", turns: 2));
        source.Upsert(S("s1", SessionStatus.Idle, response: "two", turns: 3) with { Attention = SignedOut });
        await After(new PushOptions().FinishedCollapse);

        Assert.Equal(["finished"], SessionKinds());
        Assert.Equal(1, SignInsSent());
        Assert.Equal(0, notifier.Pending);
    }

    // A finished push in flight when the next prompt fails sign-in comes back as a retry: it is
    // checked again as it goes, and not sent while the attention lasts.
    [Fact]
    public async Task A_finished_push_retried_after_the_next_prompt_failed_sign_in_is_not_sent()
    {
        Start();
        Subscribe(Phone);
        var first = true;
        sender.Respond = _ =>
        {
            if (!first) { return new PushResult(201, null); }
            first = false;
            source.Upsert(S("s1", SessionStatus.Working, response: "The build is green.", turns: 1));
            source.Upsert(S("s1", SessionStatus.Idle, response: "The build is green.", turns: 2) with { Attention = SignedOut });
            return new PushResult(503, "unavailable");
        };
        source.Upsert(S("s1", SessionStatus.Working, turns: 0));
        source.Upsert(S("s1", SessionStatus.Idle, response: "The build is green."));

        await After(TimeSpan.FromSeconds(3));
        await After(TimeSpan.FromMinutes(1));

        // Only the sign-in notice goes: the finished one was tried once, and never again.
        Assert.Equal(["signIn"], Received().Select(r => Kind(r.Notification)));
        Assert.True(sender.Attempts == 2, $"tried {sender.Attempts} times");
    }

    // A delegate's question still pending returns before the cleanup once did: the finished
    // notice waiting for the relay went out with the stale answer when it came back.
    [Fact]
    public async Task A_finished_push_waiting_for_the_relay_is_withdrawn_when_sign_in_fails_with_a_delegates_question_pending()
    {
        Start();
        Subscribe(Phone);
        sender.Connected = false;
        source.Upsert(S("s1", SessionStatus.Working, turns: 0));
        source.Upsert(S("s1", SessionStatus.Idle, response: "The build is green."));
        await After(TimeSpan.FromSeconds(3));
        Assert.Equal(1, notifier.Pending);

        source.Upsert(S("s1", SessionStatus.Waiting, response: "The build is green.", turns: 2, question: Q("dq") with { Agent = "agent-1" }) with { WaitingOn = "agent-1", Attention = SignedOut });
        Assert.Equal(0, notifier.Pending);
        sender.Connected = true;
        await After(TimeSpan.FromSeconds(3));

        Assert.DoesNotContain(Received(), r => Kind(r.Notification) == "finished");
    }

    // #841: the finished notice that would have replaced an answered question on the phone did
    // not go, so the question's resolution goes instead; the phone never keeps it.
    [Fact]
    public async Task An_answered_question_is_resolved_when_its_finished_push_is_withdrawn_for_a_failed_sign_in()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        sender.Connected = false;
        source.Upsert(S("s1", SessionStatus.Working));
        source.Upsert(S("s1", SessionStatus.Idle, response: "Done."));
        await After(TimeSpan.FromSeconds(3));
        Assert.Equal(1, notifier.Pending);

        source.Upsert(S("s1", SessionStatus.Working, response: "Done.", turns: 1));
        source.Upsert(S("s1", SessionStatus.Idle, response: "Done.", turns: 2) with { Attention = SignedOut });
        sender.Connected = true;
        await After(TimeSpan.FromSeconds(3));

        Assert.Equal(["question", "resolved"], SessionKinds());
        Assert.Equal(1, SignInsSent());
    }

    [Fact]
    public async Task An_answered_question_is_resolved_when_its_finished_push_in_flight_meets_a_failed_sign_in()
    {
        Start();
        Subscribe(Phone);
        sender.Respond = _ =>
        {
            if (sender.Attempts != 2) { return new PushResult(201, null); }
            source.Upsert(S("s1", SessionStatus.Working, response: "Done.", turns: 1));
            source.Upsert(S("s1", SessionStatus.Idle, response: "Done.", turns: 2) with { Attention = SignedOut });
            return new PushResult(503, "unavailable");
        };
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s1", SessionStatus.Working));
        source.Upsert(S("s1", SessionStatus.Idle, response: "Done."));

        await After(TimeSpan.FromSeconds(3));
        await After(TimeSpan.FromMinutes(1));

        Assert.Equal(["question", "resolved"], SessionKinds());
        Assert.Equal(1, SignInsSent());
    }

    // Refused by one device only: the question is resolved there, while another device's
    // finished push, held by the collapse window, still goes.
    [Fact]
    public async Task An_answered_question_is_resolved_only_on_the_device_that_refused_its_finished_push()
    {
        Start();
        Subscribe(Phone);
        Subscribe(Laptop);
        var laptop = 0;
        sender.Respond = e => e.EndsWith(Laptop) && ++laptop % 2 == 1 ? new PushResult(403, "forbidden") : new PushResult(201, null);
        source.Upsert(S("s1", SessionStatus.Working, turns: 0));
        source.Upsert(S("s1", SessionStatus.Idle, response: "Earlier.", turns: 1));
        await After(TimeSpan.FromSeconds(3));
        source.Upsert(S("s1", SessionStatus.Waiting, response: "Earlier.", turns: 1, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s1", SessionStatus.Working, response: "Earlier.", turns: 1));
        source.Upsert(S("s1", SessionStatus.Idle, response: "Done.", turns: 2));

        await After(TimeSpan.FromSeconds(3));
        await After(new PushOptions().FinishedCollapse);

        Assert.Equal(["finished", "question", "finished"], Received().Where(r => r.Device == Phone).Select(r => Kind(r.Notification)));
        Assert.Equal(["question", "resolved"], Received().Where(r => r.Device == Laptop).Select(r => Kind(r.Notification)));
    }

    // Still asked: nothing is resolved.
    [Fact]
    public async Task A_question_asked_again_is_not_resolved_when_a_finished_push_is_withdrawn()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        sender.Connected = false;
        source.Upsert(S("s1", SessionStatus.Working));
        source.Upsert(S("s1", SessionStatus.Idle, response: "Done."));
        await After(TimeSpan.FromSeconds(3));

        source.Upsert(S("s1", SessionStatus.Waiting, response: "Done.", turns: 2, question: Q("q1")) with { Attention = SignedOut });
        sender.Connected = true;
        await After(TimeSpan.FromSeconds(3));

        Assert.Equal(["question"], SessionKinds());
        Assert.Equal(1, SignInsSent());
    }

    [Fact]
    public async Task A_collapsed_finished_turn_is_dropped_if_the_session_is_working_again_when_it_is_due()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Working, turns: 0));
        source.Upsert(S("s1", SessionStatus.Idle, response: "one"));
        await After(TimeSpan.FromSeconds(3));
        source.Upsert(S("s1", SessionStatus.Working, response: "one"));
        source.Upsert(S("s1", SessionStatus.Idle, response: "two", turns: 2));
        await After(TimeSpan.FromSeconds(3));
        source.Upsert(S("s1", SessionStatus.Working, response: "two", turns: 2));

        await After(new PushOptions().FinishedCollapse);
        Assert.Single(sender.Sent);
        Assert.Equal(0, notifier.Pending);
    }

    [Fact]
    public async Task A_question_already_pending_when_the_host_starts_is_notified_as_after_a_daemon_restart()
    {
        Start(existing: S("s1", SessionStatus.Waiting, question: Q("q1", "Approve?", QuestionKind.Approval)));
        Subscribe(Phone);
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal("DEV · waiting for your approval\nApprove?", Assert.Single(Received()).Notification.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_prompt_only_the_machine_can_answer_is_notified_as_waiting_at_the_machine()
    {
        Start(existing: S("s1", SessionStatus.Waiting, question: Q("q1", "Allow **Run a command**?", QuestionKind.Approval) with { AnswerAtMachine = true }));
        Subscribe(Phone);
        await After(TimeSpan.FromSeconds(2));
        var n = Assert.Single(Received()).Notification;
        Assert.Equal("DEV · waiting at the machine\nAllow **Run a command**?", n.GetProperty("body").GetString());
        Assert.Equal("question", Kind(n));
    }

    [Fact]
    public async Task A_delegates_question_names_the_delegate_and_shares_the_sessions_tag()
    {
        Start();
        Subscribe(Phone);
        var delegated = S("s1", SessionStatus.Waiting, question: Q("q1", "Allow **gh pr checks**?", QuestionKind.Approval) with { Agent = "agent-c204" }) with
        {
            WaitingOn = "agent-c204",
            Subagents = [new Subagent { Id = "agent-c204", Kind = SubagentKind.Background, Name = "Read witness shell logic", Status = SubagentStatus.Waiting }],
        };
        source.Upsert(delegated);
        await After(TimeSpan.FromSeconds(2));

        var n = Assert.Single(Received()).Notification;
        Assert.Equal("Delegate Read witness shell logic asks · Fix the login", n.GetProperty("title").GetString());
        Assert.Equal($"{Machine}:s1", n.GetProperty("tag").GetString());
        Assert.Equal("question", Kind(n));
    }

    // #263: the name is the delegate's own words. A look-alike the check misses ("Ma1n agent")
    // still follows the word Delegate, and a bidi override is gone from the title.
    [Theory]
    [InlineData("Ma1n agent", "Delegate Ma1n agent asks · Fix the login")]
    [InlineData("\u202EMain agent\u202C", "Delegate “Main agent” asks · Fix the login")]
    [InlineData("\u200B\uFEFF", "A delegate asks · Fix the login")]
    public async Task A_delegates_question_is_titled_after_the_word_delegate_whatever_its_name(string name, string title)
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1", "Allow **gh pr checks**?", QuestionKind.Approval) with { Agent = "agent-c204" }) with
        {
            WaitingOn = "agent-c204",
            Subagents = [new Subagent { Id = "agent-c204", Kind = SubagentKind.Background, Name = name, Status = SubagentStatus.Waiting }],
        });
        await After(TimeSpan.FromSeconds(2));

        Assert.Equal(title, Assert.Single(Received()).Notification.GetProperty("title").GetString());
    }

    [Fact]
    public async Task A_finished_turn_shows_the_main_agents_own_response_not_a_delegates_written_after_it()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Working, turns: 0));
        var finished = S("s1", SessionStatus.Idle, response: "HEAD: 1 failing test.");
        source.Upsert(finished with
        {
            Activity = finished.Activity with
            {
                Source = new ActivitySource { Response = "agent-3a91" },
                Own = new OwnActivity { Response = "Started 2 background agents." },
            },
        });
        await After(TimeSpan.FromSeconds(3));

        Assert.Equal("DEV · finished\nStarted 2 background agents.", Assert.Single(Received()).Notification.GetProperty("body").GetString());
    }

    [Fact]
    public async Task Answering_a_delegates_question_while_the_main_agent_was_idle_all_along_is_no_finished_turn()
    {
        Start(existing: S("s1", SessionStatus.Idle, response: "Started the delegates."));
        Subscribe(Phone);
        var asking = S("s1", SessionStatus.Waiting, response: "Started the delegates.", question: Q("q1", "Allow?", QuestionKind.Approval) with { Agent = "agent-1" }, turns: 0) with { WaitingOn = "agent-1" };
        source.Upsert(asking);
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s1", SessionStatus.Idle, response: "Started the delegates."));
        await After(TimeSpan.FromSeconds(5));

        Assert.Equal(["question", "resolved"], Received().Select(r => Kind(r.Notification)));

        // The main agent finishing a turn while a delegate's question waited is still news.
        source.Upsert(asking with { Activity = asking.Activity with { Response = "Both are done.", TurnCount = 1 } });
        source.Upsert(S("s1", SessionStatus.Idle, response: "Both are done.", turns: 2));
        await After(TimeSpan.FromSeconds(5));
        Assert.Equal("DEV · finished\nBoth are done.", Received()[^1].Notification.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_delegates_question_already_pending_when_the_host_starts_ends_no_turn_when_answered()
    {
        var asking = S("s1", SessionStatus.Waiting, response: "Started the delegates.", question: Q("q1", "Allow?", QuestionKind.Approval) with { Agent = "agent-1" }, turns: 0) with { WaitingOn = "agent-1" };
        Start(existing: asking);
        Subscribe(Phone);
        await After(TimeSpan.FromSeconds(2));

        source.Upsert(S("s1", SessionStatus.Idle, response: "Started the delegates."));
        await After(TimeSpan.FromSeconds(5));

        Assert.Equal(["question", "resolved"], Received().Select(r => Kind(r.Notification)));
    }

    [Fact]
    public async Task A_main_turn_that_gave_its_answer_and_ended_while_a_delegate_asked_is_a_finished_turn()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Working, response: "All done.", turns: 0));
        source.Upsert(S("s1", SessionStatus.Waiting, response: "All done.", question: Q("q1", "Allow?", QuestionKind.Approval) with { Agent = "agent-1" }, turns: 0) with { WaitingOn = "agent-1" });
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s1", SessionStatus.Idle, response: "All done."));
        await After(TimeSpan.FromSeconds(5));

        Assert.Equal("DEV · finished\nAll done.", Received()[^1].Notification.GetProperty("body").GetString());
    }

    [Fact]
    public async Task A_finished_turn_replaces_the_answered_question_instead_of_adding_a_notice()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s1", SessionStatus.Working));
        source.Upsert(S("s1", SessionStatus.Idle, response: "All done."));
        await After(TimeSpan.FromSeconds(3));

        Assert.Equal(["question", "finished"], Received().Select(r => Kind(r.Notification)));
    }

    [Fact]
    public async Task A_gone_subscription_404_or_410_is_removed_from_the_store_and_its_file()
    {
        Start();
        Subscribe(Phone);
        Subscribe(Laptop);
        sender.Respond = e => e.EndsWith(Phone) ? new PushResult(410, null) : e.EndsWith(Laptop) ? new PushResult(404, null) : new PushResult(201, null);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));

        Assert.Equal(0, store.Count);
        Assert.Equal(0, PushStore.Load(StorePath, Account).Count);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q2")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal(2, sender.Attempts);
        Assert.Contains(logs, l => l.Contains("is gone (410); removed"));
    }

    [Fact]
    public async Task A_gone_answer_for_an_old_endpoint_keeps_the_subscription_the_device_registered_meanwhile()
    {
        Start();
        Subscribe(Phone, endpoint: "https://web.push.apple.com/old/" + Phone);
        // While the push to the old endpoint is on its way, the browser renews its subscription
        // and the app registers the new one; then the push service says the old one is gone.
        sender.Respond = e =>
        {
            if (!e.Contains("/old/")) { return new PushResult(201, null); }
            Subscribe(Phone, endpoint: "https://web.push.apple.com/new/" + Phone);
            return new PushResult(410, null);
        };
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));

        Assert.Equal("https://web.push.apple.com/new/" + Phone, Assert.Single(store.All).Endpoint);
        Assert.Equal(1, PushStore.Load(StorePath, Account).Count);
        source.Upsert(S("s2", SessionStatus.Waiting, question: Q("q2")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal("https://web.push.apple.com/new/" + Phone, Assert.Single(sender.Sent).Endpoint);
    }

    [Fact]
    public async Task A_notice_lost_to_a_dropped_relay_connection_is_sent_once_after_reconnecting()
    {
        Start();
        Subscribe(Phone);
        sender.Throw = new IOException("relay connection ended");
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        await After(TimeSpan.FromSeconds(30));
        Assert.Equal(1, sender.Attempts);
        Assert.Equal(1, notifier.Pending);

        var next = new FakeSender(Account);
        notifier.Attach(next);
        await notifier.PumpAsync();
        await After(TimeSpan.FromSeconds(30));
        Assert.Single(next.Sent);
        Assert.Equal(0, notifier.Pending);
    }

    [Fact]
    public async Task Nothing_is_sent_while_disconnected_and_only_the_latest_state_of_a_session_goes_on_reconnecting()
    {
        Start();
        Subscribe(Phone);
        sender.Connected = false;
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s1", SessionStatus.Working));
        source.Upsert(S("s1", SessionStatus.Idle, response: "Done."));
        await After(TimeSpan.FromSeconds(3));
        Assert.Equal(0, sender.Attempts);

        var next = new FakeSender(Account);
        notifier.Attach(next);
        await notifier.PumpAsync();
        Assert.Equal("finished", Kind(Decrypt(Assert.Single(next.Sent))));
    }

    JsonElement Decrypt((string Endpoint, byte[] Body, int, string, string?) p)
    {
        var (key, auth) = browsers[p.Endpoint.Split('/')[^1]];
        return JsonDocument.Parse(WebPushCrypto.Decrypt(p.Body, key, auth)).RootElement.GetProperty("notification");
    }

    [Fact]
    public async Task A_push_the_relay_never_answers_is_retried_later_and_holds_up_nothing_else()
    {
        // Two sessions' questions, each to go as itself, not grouped (#913 rule 3).
        Start(new PushOptions { BurstWindow = TimeSpan.Zero });
        Subscribe(Phone);
        sender.Failures.Enqueue(new TimeoutException());
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal((1, 0), (sender.Attempts, sender.Sent.Count));

        source.Upsert(S("s2", SessionStatus.Waiting, question: Q("q2")));
        await After(TimeSpan.FromSeconds(2));
        await After(TimeSpan.FromSeconds(3));
        Assert.Equal(["q2", "q1"], Received().Select(r => r.Notification.GetProperty("data").GetProperty("question").GetString()));
        Assert.Equal(0, notifier.Pending);
    }

    [Fact]
    public async Task A_question_that_flickers_away_and_back_before_its_resolution_goes_is_left_on_the_phone()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));

        // Briefly not pending (an answer being typed), then pending again: still waiting on you.
        source.Upsert(S("s1", SessionStatus.Working));
        await After(TimeSpan.FromSeconds(1));
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(5));
        Assert.Equal(["question"], Received().Select(r => Kind(r.Notification)));

        // And once it really goes, it is resolved.
        source.Upsert(S("s1", SessionStatus.Working));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal(["question", "resolved"], Received().Select(r => Kind(r.Notification)));
    }

    [Fact]
    public async Task A_resolution_still_waiting_to_be_sent_is_withdrawn_when_its_question_comes_back()
    {
        Start(new PushOptions { MaxPerMinute = 1 });
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        // Gone long enough to be resolved, but the rate limit holds the resolution back...
        source.Upsert(S("s1", SessionStatus.Working));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal(1, notifier.Pending);
        // ...and before it can go, the question is pending again.
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(60));
        Assert.Equal(["question"], Received().Select(r => Kind(r.Notification)));
        Assert.Equal(0, notifier.Pending);

        source.Upsert(S("s1", SessionStatus.Working));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal(["question", "resolved"], Received().Select(r => Kind(r.Notification)));
    }

    [Fact]
    public async Task A_host_at_the_rate_limit_sleeps_until_the_window_has_room_instead_of_spinning()
    {
        Start(new PushOptions { MaxPerMinute = 1 });
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        source.Upsert(S("s2", SessionStatus.Waiting, question: Q("q2")));
        time.Advance(TimeSpan.FromSeconds(2));

        var wait = await notifier.PumpAsync();

        Assert.Single(sender.Sent);
        Assert.Equal(TimeSpan.FromMinutes(1), wait);
    }

    [Fact]
    public async Task A_notice_that_cannot_be_encrypted_does_not_consume_the_send_rate_limit()
    {
        Start(new PushOptions { MaxPerMinute = 1 });
        Subscribe(Phone);
        store.Put(store.Get(Phone)! with { P256dh = "invalid" });
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal(0, sender.Attempts);

        Subscribe(Laptop);
        source.Upsert(S("s2", SessionStatus.Waiting, question: Q("q2")));
        await After(TimeSpan.FromSeconds(2));

        Assert.Equal(Laptop, Assert.Single(Received()).Device);
    }

    [Fact]
    public async Task Sends_past_the_rate_limit_wait_for_the_next_minute_rather_than_being_refused()
    {
        Start(new PushOptions { MaxPerMinute = 3, BurstWindow = TimeSpan.Zero });
        Subscribe(Phone);
        for (var i = 0; i < 5; i++) { source.Upsert(S($"s{i}", SessionStatus.Waiting, question: Q($"q{i}"))); }
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal(3, sender.Sent.Count);

        await After(TimeSpan.FromSeconds(30));
        Assert.Equal(3, sender.Sent.Count);
        await After(TimeSpan.FromSeconds(31));
        Assert.Equal(5, sender.Sent.Count);
        Assert.Equal(5, Received().Select(r => r.Topic).Distinct().Count());
    }

    [Fact]
    public async Task A_push_service_that_is_briefly_unavailable_is_retried_with_backoff()
    {
        Start();
        Subscribe(Phone);
        var statuses = new Queue<PushResult>([new(503, null), new(0, "rate-limited"), new(201, null)]);
        sender.Respond = _ => statuses.Dequeue();
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal(1, sender.Attempts);
        await After(TimeSpan.FromSeconds(5));
        Assert.Equal(2, sender.Attempts);
        await After(TimeSpan.FromSeconds(5));
        Assert.Equal(2, sender.Attempts);
        await After(TimeSpan.FromSeconds(5));
        Assert.Equal(3, sender.Attempts);
        Assert.Equal(0, notifier.Pending);
    }

    [Fact]
    public void Only_a_device_of_the_hosts_own_account_may_register_list_or_remove()
    {
        Start();
        var otherAccount = Assert.Throws<InvalidOperationException>(() => Subscribe(Phone, peer: Peer(Phone, account: "2")));
        Assert.Contains("own account", otherAccount.Message);
        Assert.Throws<InvalidOperationException>(() => Subscribe(Phone, peer: Peer(Phone, inAccount: false)));
        Assert.Throws<InvalidOperationException>(() => notifier.Handle(Peer(Laptop, account: "2"), Ops.PushList, default));
        Assert.Equal(0, store.Count);

        Assert.Equal(new PushSubscribeResult(true, 1), Subscribe(Phone));
    }

    [Fact]
    public async Task Subscriptions_saved_for_another_account_are_neither_loaded_nor_kept()
    {
        Start();
        Subscribe(Phone);
        Assert.Equal(1, PushStore.Load(StorePath, Account).Count);
        Assert.Equal(0, PushStore.Load(StorePath, "2").Count);

        // The profile now signs in as another account: nothing of the first one's survives.
        notifier.Attach(new FakeSender("2"));
        Assert.Equal(0, store.Count);
        Assert.Equal(0, PushStore.Load(StorePath, Account).Count);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Empty(sender.Sent);
    }

    [Fact]
    public void A_subscription_is_checked_when_it_arrives()
    {
        Start();
        using var key = P256KeyPair.Generate();
        object? Try(string endpoint, string p256dh, string auth, string app) => notifier.Handle(Peer(Phone), Ops.PushSubscribe,
            JsonSerializer.SerializeToElement(new { endpoint, keys = new { p256dh, auth }, app }));
        var good = (E: "https://web.push.apple.com/x", K: B64Url.Encode(key.Public), A: B64Url.Encode(new byte[16]), App: "https://danswett.github.io/agent-bridge/app/");

        Assert.Contains("endpoint", Assert.Throws<ArgumentException>(() => Try("http://web.push.apple.com/x", good.K, good.A, good.App)).Message);
        Assert.Contains("endpoint", Assert.Throws<ArgumentException>(() => Try("https://user:pw@web.push.apple.com/x", good.K, good.A, good.App)).Message);
        Assert.Contains("p256dh", Assert.Throws<ArgumentException>(() => Try(good.E, B64Url.Encode(new byte[65]), good.A, good.App)).Message);
        Assert.Contains("auth", Assert.Throws<ArgumentException>(() => Try(good.E, good.K, B64Url.Encode(new byte[8]), good.App)).Message);
        Assert.Contains("keys", Assert.Throws<ArgumentException>(() => Try(good.E, "not base64!", good.A, good.App)).Message);
        Assert.Contains("app", Assert.Throws<ArgumentException>(() => Try(good.E, good.K, good.A, "javascript:alert(1)")).Message);
        Assert.Throws<ArgumentException>(() => notifier.Handle(Peer(Phone), Ops.PushSubscribe, JsonSerializer.SerializeToElement(new { endpoint = good.E })));
        Assert.Equal(0, store.Count);

        Assert.NotNull(Try(good.E, good.K, good.A, good.App));
        // A test stack's loopback push service is http.
        Assert.NotNull(Try("http://127.0.0.1:5000/push/x", good.K, good.A, "http://127.0.0.1:8080/app/"));
    }

    [Fact]
    public void A_device_lists_the_devices_notified_without_their_endpoints_and_removes_its_own()
    {
        Start();
        Subscribe(Phone);
        Subscribe(Laptop, endpoint: "https://fcm.googleapis.com/fcm/send/" + Laptop);

        var list = Assert.IsType<PushListResult>(notifier.Handle(Peer(Phone), Ops.PushList, default));
        Assert.Equal([(Phone, "iPhone", "web.push.apple.com", true), (Laptop, "Laptop", "fcm.googleapis.com", false)],
            list.Devices.Select(d => (d.Device, d.Name, d.Service, d.Current)));
        Assert.DoesNotContain(Phone, JsonSerializer.Serialize(list.Devices.Select(d => d.Service)));

        Assert.Equal(new PushUnsubscribeResult(true), notifier.Handle(Peer(Phone), Ops.PushUnsubscribe, default));
        Assert.Equal(new PushUnsubscribeResult(false), notifier.Handle(Peer(Phone), Ops.PushUnsubscribe, default));
        Assert.Equal(Laptop, Assert.Single(store.All).Device);
        Assert.Null(notifier.Handle(Peer(Phone), "push.other", default));
    }

    [Fact]
    public void Registering_again_replaces_a_devices_subscription_and_a_reinstalled_apps_old_one()
    {
        Start();
        Subscribe(Phone);
        Subscribe(Phone, endpoint: "https://web.push.apple.com/renewed");
        Assert.Equal("https://web.push.apple.com/renewed", Assert.Single(store.All).Endpoint);

        // The same browser endpoint registered by a new device key (the app was reinstalled).
        Subscribe(Laptop, endpoint: "https://web.push.apple.com/renewed");
        Assert.Equal(Laptop, Assert.Single(store.All).Device);
    }

    // The rebrand (#538) changes the name a device registers with ("iPhone · Agent Bridge" becomes
    // "iPhone · Slipgates") on every installed app's next registration: it must stay the same
    // device with its one subscription, not a second one or a new device to approve.
    [Fact]
    public void A_device_registering_again_under_a_new_name_keeps_its_one_subscription_with_the_new_name()
    {
        Start();
        Subscribe(Phone, name: "iPhone · Agent Bridge");
        var before = Assert.Single(store.All);
        Subscribe(Phone, name: "iPhone · Slipgates");
        var after = Assert.Single(store.All);
        Assert.Equal((Phone, before.Endpoint, before.P256dh, "iPhone · Slipgates"), (after.Device, after.Endpoint, after.P256dh, after.Name));
    }

    RemoteAuthorization Enforce(P256KeyPair host, P256KeyPair person)
    {
        var pins = new TrustedPeerStore(folder, host.Public, () => Account, time);
        pins.Reset();
        pins.Put(person.Public, TrustedPeerStore.Person, time.GetUtcNow().AddHours(1));
        return trust = new RemoteAuthorization(true, pins, time);
    }

    static PushPeer PinnedPeer(P256KeyPair key) => new(Wire.DeviceId(key.Public), "Person", Account, true, key.Public);

    [Fact]
    public async Task Enforcing_restart_drops_legacy_subscriptions_even_for_a_currently_pinned_device()
    {
        using var host = P256KeyPair.Generate();
        using var person = P256KeyPair.Generate();
        var peer = PinnedPeer(person);
        Start();
        Subscribe(peer.Device, peer: peer);
        await notifier.DisposeAsync();
        Enforce(host, person);
        Start();
        Assert.Empty(store.All);
        Assert.Equal(0, PushStore.Load(StorePath, Account).Count);
        source.Upsert(S("old", SessionStatus.Waiting, question: Q("q")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Empty(sender.Sent);

        Subscribe(peer.Device, peer: peer);
        var saved = Assert.Single(PushStore.Load(StorePath, Account).All);
        Assert.Equal(B64Url.Encode(person.Public), saved.Grant!.PublicKey);
        source.Upsert(S("new", SessionStatus.Waiting, question: Q("q")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Single(sender.Sent);
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("expire")]
    [InlineData("reset")]
    public async Task Losing_a_person_grant_removes_queued_pushes_and_reapproval_cannot_revive_them(string change)
    {
        using var host = P256KeyPair.Generate();
        using var person = P256KeyPair.Generate();
        var pins = Enforce(host, person).Store;
        Start();
        var peer = PinnedPeer(person);
        Subscribe(peer.Device, peer: peer);
        sender.Connected = false;
        source.Upsert(S("old", SessionStatus.Waiting, question: Q("q")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal(1, notifier.Pending);
        if (change == "revoke") { pins.Revoke(person.Public); }
        else if (change == "expire") { time.Advance(TimeSpan.FromHours(1)); }
        else { pins.Reset(); }
        trust!.Revalidate();
        Assert.Empty(store.All);
        Assert.Equal(0, notifier.Pending);
        Assert.Equal(0, PushStore.Load(StorePath, Account).Count);

        pins.Put(person.Public, TrustedPeerStore.Person, time.GetUtcNow().AddHours(1));
        Subscribe(peer.Device, peer: peer);
        sender.Connected = true;
        await notifier.PumpAsync();
        Assert.Empty(sender.Sent);
        source.Upsert(S("new", SessionStatus.Waiting, question: Q("q")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task A_push_in_flight_at_revocation_cannot_reinsert_a_retry_for_a_new_generation()
    {
        using var host = P256KeyPair.Generate();
        using var person = P256KeyPair.Generate();
        var pins = Enforce(host, person).Store;
        Start();
        var peer = PinnedPeer(person);
        Subscribe(peer.Device, peer: peer);
        sender.Respond = _ =>
        {
            pins.Revoke(person.Public);
            trust!.Revalidate();
            return new PushResult(503, "unreachable");
        };
        source.Upsert(S("old", SessionStatus.Waiting, question: Q("q")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal(1, sender.Attempts);
        Assert.Equal(0, notifier.Pending);
        Assert.Empty(store.All);
        pins.Put(person.Public, TrustedPeerStore.Person, time.GetUtcNow().AddHours(1));
        Subscribe(peer.Device, peer: peer);
        await After(TimeSpan.FromMinutes(1));
        Assert.Equal(1, sender.Attempts);
    }

    [Fact]
    public void Nonperson_pins_and_a_claimed_device_id_cannot_register_standing_access()
    {
        using var host = P256KeyPair.Generate();
        using var person = P256KeyPair.Generate();
        var pins = Enforce(host, person).Store;
        Start();
        var peer = PinnedPeer(person);
        Assert.Throws<InvalidOperationException>(() => Subscribe(Phone, peer: peer with { Device = Phone }));
        foreach (var role in new[] { TrustedPeerStore.Host, TrustedPeerStore.Service })
        {
            pins.Put(person.Public, role, time.GetUtcNow().AddHours(1));
            Assert.Throws<InvalidOperationException>(() => Subscribe(peer.Device, peer: peer));
        }
        Assert.Empty(store.All);
    }

    // ---- #913 rule 5: one reminder ----

    static bool Reminder(JsonElement n) => n.GetProperty("data").TryGetProperty("reminder", out var r) && r.GetBoolean();

    static bool Silent(JsonElement n) => n.TryGetProperty("silent", out var s) && s.GetBoolean();

    static string? QuestionOf(JsonElement n) => n.GetProperty("data").TryGetProperty("question", out var q) ? q.GetString() : null;

    [Fact]
    public async Task A_question_still_unanswered_15_minutes_after_its_notice_alerts_once_more_as_itself_on_every_device()
    {
        Start();
        Subscribe(Phone);
        Subscribe(Laptop);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal(2, sender.Sent.Count);

        await After(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        Assert.Equal(2, sender.Sent.Count);
        await After(TimeSpan.FromSeconds(1));

        var received = Received();
        Assert.Equal(4, received.Count);
        Assert.All(received.Take(2), r => Assert.False(Reminder(r.Notification)));
        var reminders = received.Skip(2).ToList();
        Assert.Equal([Phone, Laptop], reminders.Select(r => r.Device).Order(StringComparer.Ordinal));
        var original = received.Take(2).Single(r => r.Device == Phone);
        var again = reminders.Single(r => r.Device == Phone);
        Assert.True(Reminder(again.Notification));
        // The question's own notice again, alerting, in its place.
        Assert.Equal(("question", "needsYou", false), (Kind(again.Notification), again.Notification.GetProperty("data").GetProperty("class").GetString(), Silent(again.Notification)));
        foreach (var field in new[] { "title", "body", "tag", "navigate" })
        {
            Assert.Equal(original.Notification.GetProperty(field).GetString(), again.Notification.GetProperty(field).GetString());
        }
        Assert.Equal("q1", QuestionOf(again.Notification));
        Assert.Equal((original.Ttl, original.Urgency, original.Topic), (again.Ttl, again.Urgency, again.Topic));
    }

    [Fact]
    public async Task A_question_answered_within_15_minutes_is_not_reminded()
    {
        Start();
        Subscribe(Phone);
        Subscribe(Laptop);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        await After(TimeSpan.FromMinutes(10));
        notifier.Answered(Laptop, "s1", "q1");
        source.Upsert(S("s1", SessionStatus.Working));
        await After(TimeSpan.FromMinutes(30));

        Assert.Equal(["question", "question", "resolved"], Received().Select(r => Kind(r.Notification)));
        Assert.DoesNotContain(Received(), r => Reminder(r.Notification));
    }

    [Fact]
    public async Task A_reminder_goes_once_and_a_further_question_while_the_session_still_needs_you_brings_no_second()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        await After(TimeSpan.FromMinutes(15));
        Assert.Equal(1, Received().Count(r => Reminder(r.Notification)));
        await After(TimeSpan.FromMinutes(15));
        await After(TimeSpan.FromHours(2));

        // q1 answered as q2 is asked: still needsYou, the same episode, which has had its reminder.
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q2", "And the cache?")));
        await After(TimeSpan.FromSeconds(2));
        await After(TimeSpan.FromMinutes(15));
        await After(TimeSpan.FromMinutes(15));

        Assert.Equal(["q1", "q1", "q2"], Received().Select(r => QuestionOf(r.Notification)));
        Assert.Equal(1, Received().Count(r => Reminder(r.Notification)));
    }

    // ---- #913 rule 3: bursts group ----

    [Fact]
    public async Task Questions_of_three_sessions_within_30_seconds_go_as_the_first_and_one_grouped_notice()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s2", SessionStatus.Waiting, question: Q("q2", "Allow **Run a command**?", QuestionKind.Approval)));
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s3", SessionStatus.Waiting, question: Q("q3")));
        await After(TimeSpan.FromSeconds(10));

        var received = Received();
        Assert.Equal(("question", "q1"), (Kind(received[0].Notification), QuestionOf(received[0].Notification)));
        Assert.DoesNotContain(received, r => QuestionOf(r.Notification) is "q2" or "q3");
        var groups = received.Skip(1).Select(r => r.Notification).ToList();
        Assert.Equal(["group", "group"], groups.Select(Kind));
        // One notice, made by the second question and changed in place, quietly, by the third.
        Assert.All(groups, n => Assert.Equal($"{Machine}:~needsyou", n.GetProperty("tag").GetString()));
        Assert.Equal([false, true], groups.Select(Silent));
        Assert.Equal([2, 3], groups.Select(n => n.GetProperty("data").GetProperty("sessions").GetInt32()));
        Assert.Equal(("high", PushNotice.Topic(Machine, "~needsyou")), (received[1].Urgency, received[1].Topic));
        var last = groups[^1];
        Assert.Equal(("Needs you", "DEV · 3 sessions waiting on you"), (last.GetProperty("title").GetString(), last.GetProperty("body").GetString()));
        Assert.Equal($"https://danswett.github.io/agent-bridge/app/?host={Machine}&needsyou=1", last.GetProperty("navigate").GetString());
        // Nothing to answer from it (#253), and nothing of any session's (#263).
        var data = last.GetProperty("data");
        Assert.Equal(("", "needsYou"), (data.GetProperty("session").GetString(), data.GetProperty("class").GetString()));
        Assert.Null(QuestionOf(last));
        Assert.DoesNotContain("Which database?", last.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("Fix the login", last.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_first_question_of_a_burst_goes_after_its_usual_delay_not_after_the_window()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(1));
        Assert.Empty(sender.Sent);
        await After(TimeSpan.FromSeconds(1));
        var (_, first, _, urgency, _) = Assert.Single(Received());
        Assert.Equal(("question", "q1", false, "high"), (Kind(first), QuestionOf(first), Silent(first), urgency));

        // A second within the window is grouped: the first, already out, is not held back or taken back.
        source.Upsert(S("s2", SessionStatus.Waiting, question: Q("q2")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Equal(["question", "group"], Received().Select(r => Kind(r.Notification)));
    }

    [Fact]
    public async Task A_question_alone_in_its_window_goes_as_itself()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        // No group of one.
        await After(TimeSpan.FromSeconds(30));
        Assert.Single(sender.Sent);

        // After the first's window: as itself, opening a window of its own.
        source.Upsert(S("s2", SessionStatus.Waiting, question: Q("q2")));
        await After(TimeSpan.FromSeconds(2));
        // Within that window, but its question is answered: nothing pending to group the next with.
        source.Upsert(S("s2", SessionStatus.Working));
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s3", SessionStatus.Waiting, question: Q("q3")));
        await After(TimeSpan.FromSeconds(2));
        await After(TimeSpan.FromSeconds(30));

        Assert.Equal(["question", "question", "resolved", "question"], Received().Select(r => Kind(r.Notification)));
        Assert.Equal(["q1", "q2", "q2", "q3"], Received().Select(r => QuestionOf(r.Notification)));
    }

    [Fact]
    public async Task A_sign_in_within_a_burst_goes_as_its_own_notice_and_is_not_counted_in_the_group()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s2", SessionStatus.Idle, response: "Done.", turns: 2) with { Attention = SignedOut });
        await After(TimeSpan.Zero);
        source.Upsert(S("s3", SessionStatus.Waiting, question: Q("q3")));
        await After(TimeSpan.FromSeconds(2));

        var received = Received();
        Assert.Equal(["question", "signIn", "group"], received.Select(r => Kind(r.Notification)));
        var signIn = received[1].Notification;
        Assert.Equal($"{Machine}:{PushNotice.SignInKey(AgentKind.Copilot, null)}", signIn.GetProperty("tag").GetString());
        Assert.Equal(2, received[2].Notification.GetProperty("data").GetProperty("sessions").GetInt32());
    }

    [Fact]
    public async Task A_grouped_notice_changes_quietly_as_its_questions_are_answered_and_is_withdrawn_with_the_last()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s2", SessionStatus.Waiting, question: Q("q2")));
        await After(TimeSpan.FromSeconds(2));

        source.Upsert(S("s2", SessionStatus.Working));
        await After(TimeSpan.FromSeconds(2));
        var fewer = Received()[^1].Notification;
        Assert.Equal(("group", 1, true), (Kind(fewer), fewer.GetProperty("data").GetProperty("sessions").GetInt32(), Silent(fewer)));
        Assert.Equal(("Needs you", "DEV · 1 session waiting on you"), (fewer.GetProperty("title").GetString(), fewer.GetProperty("body").GetString()));

        source.Upsert(S("s1", SessionStatus.Working));
        await After(TimeSpan.FromSeconds(2));
        var received = Received();
        Assert.Equal(["question", "group", "group", "resolved", "resolved"], received.Select(r => Kind(r.Notification)));
        Assert.Equal($"{Machine}:s1", received[3].Notification.GetProperty("tag").GetString());
        var withdrawn = received[4].Notification;
        Assert.Equal($"{Machine}:~needsyou", withdrawn.GetProperty("tag").GetString());
        Assert.True(Silent(withdrawn));
        Assert.Equal(("", false), (withdrawn.GetProperty("data").GetProperty("session").GetString(), withdrawn.GetProperty("data").TryGetProperty("class", out _)));
        Assert.Equal(0, notifier.Pending);

        await After(TimeSpan.FromMinutes(1));
        Assert.Equal(5, sender.Sent.Count);
    }

    // The group's alert counts once it reaches a device, not once it is made: a session joining while
    // the relay is down replaces the alerting notice still waiting, and its replacement alerts in its
    // place. Once delivered, what changes it is silent.
    [Fact]
    public async Task A_group_whose_alert_never_left_while_the_relay_was_down_still_alerts_once_on_every_device()
    {
        Start();
        Subscribe(Phone);
        Subscribe(Laptop);
        sender.Connected = false;
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s2", SessionStatus.Waiting, question: Q("q2")));
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s3", SessionStatus.Waiting, question: Q("q3")));
        await After(TimeSpan.FromSeconds(2));
        Assert.Empty(sender.Sent);

        sender.Connected = true;
        await After(TimeSpan.Zero);
        source.Upsert(S("s4", SessionStatus.Waiting, question: Q("q4")));
        await After(TimeSpan.FromSeconds(2));

        foreach (var device in new[] { Phone, Laptop })
        {
            var groups = Received().Where(r => r.Device == device && Kind(r.Notification) == "group").ToList();
            Assert.Equal([(3, false, "high"), (4, true, "normal")],
                groups.Select(g => (g.Notification.GetProperty("data").GetProperty("sessions").GetInt32(), Silent(g.Notification), g.Urgency)));
        }
    }

    // Only a session joining it alerts: one leaving it, after a later question opened a new window,
    // changes it quietly.
    [Fact]
    public async Task A_grouped_notice_that_only_loses_a_question_after_a_later_window_opened_stays_quiet()
    {
        Start();
        Subscribe(Phone);
        source.Upsert(S("s1", SessionStatus.Waiting, question: Q("q1")));
        await After(TimeSpan.FromSeconds(2));
        source.Upsert(S("s2", SessionStatus.Waiting, question: Q("q2")));
        await After(TimeSpan.FromSeconds(2));
        await After(TimeSpan.FromSeconds(60));
        source.Upsert(S("s3", SessionStatus.Waiting, question: Q("q3")));
        await After(TimeSpan.FromSeconds(2));

        source.Upsert(S("s2", SessionStatus.Working));
        await After(TimeSpan.FromSeconds(2));

        var received = Received();
        Assert.Equal(["question", "group", "question", "group"], received.Select(r => Kind(r.Notification)));
        var fewer = received[^1];
        Assert.Equal((1, true, "normal"), (fewer.Notification.GetProperty("data").GetProperty("sessions").GetInt32(), Silent(fewer.Notification), fewer.Urgency));
    }

    sealed class FakeSource : ISessionSource
    {
        public List<Session> Sessions { get; } = [];
        public long Revision => 0;
        public event EventHandler<SessionChange>? Changed;
        public IReadOnlyList<Session> Snapshot() => Sessions;
        public SessionList List(string? cursor = null) => new(Sessions, 0);
        public Session? Get(string id) => Sessions.FirstOrDefault(s => s.Id == id);
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public void Upsert(Session s) => Changed?.Invoke(this, new SessionChange(SessionChangeKind.Upserted, s.Id, s, 0));
        public void Remove(string id) => Changed?.Invoke(this, new SessionChange(SessionChangeKind.Removed, id, null, 0));
    }

    sealed class FakeSender(string account) : IPushSender
    {
        public string AccountId => account;
        public bool Connected { get; set; } = true;
        public Func<string, PushResult>? Respond { get; set; }
        public Exception? Throw { get; set; }
        public Queue<Exception> Failures { get; } = new();
        public int Attempts { get; private set; }
        public List<(string Endpoint, byte[] Body, int Ttl, string Urgency, string? Topic)> Sent { get; } = [];

        public Task<PushResult> PushAsync(string endpoint, byte[] body, int ttl, string urgency, string? topic)
        {
            Attempts++;
            if (Throw is { } e) { throw e; }
            if (Failures.TryDequeue(out var once)) { throw once; }
            var r = Respond?.Invoke(endpoint) ?? new PushResult(201, null);
            if (r.Status is >= 200 and < 300) { Sent.Add((endpoint, body, ttl, urgency, topic)); }
            return Task.FromResult(r);
        }
    }
}
