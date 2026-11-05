using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

// #800: a session whose agent needs sign-in, whose turn ended on an error, or that has stopped
// answering, as Connection's contract sends it (AuthState); the banner per machine, agent and
// account; and the sheet a person's Sign in opens, with GitHub's code. pwa/test/unit/
// auth-state.test.mjs holds the PWA to the same rules.
public sealed class SignInTests
{
    static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    static Session WithAttention(Session s, string? attention) => AuthState.Attach(s, Json(attention is null ? "{}" : $$"""{"attention":{{attention}}}"""));

    static Session Needing(string id, string account = "octocat", AgentKind agent = AgentKind.Copilot, string? signIn = "phone", string? recovery = null) =>
        WithAttention(Make.Session(id: id, title: $"Session {id}") with { Agent = agent },
            $$"""{"kind":"signInNeeded","since":"2026-11-05T00:40:00Z","agent":"{{AuthState.WireName(agent)}}","account":"{{account}}"{{(signIn is null ? "" : $",\"signIn\":\"{signIn}\"")}}{{(recovery is null ? "" : $",\"recovery\":\"{recovery}\"")}}}""");

    static string Code(string userCode = "WDJB-MJHT", string uri = "https://github.com/login/device") =>
        JsonSerializer.Serialize(new { flow = "flow-1", agent = "copilot", userCode, verificationUri = uri, expiresAt = "2099-01-01T00:00:00Z" });

    // Built at run time, so no scanner takes the tests for a leak.
    static readonly string GitHubToken = "gh" + "p_" + new string('a', 36);
    static readonly string Jwt = "ey" + "J" + new string('b', 20) + "." + new string('c', 20) + "." + new string('d', 20);

    [Theory]
    [InlineData("http://github.com/login/device")]
    [InlineData("https://evil.example/login/device")]
    [InlineData("https://github.com.evil.example/login/device")]
    [InlineData("https://githu\u0431.com/login/device")]
    [InlineData("https://xn--githb-4ra.com/login/device")]
    [InlineData("https://github.com@evil.example/login/device")]
    [InlineData("https://user:pass@github.com/login/device")]
    [InlineData("https://github.com/login/device/")]
    [InlineData("https://github.com:8443/login/device")]
    [InlineData("https://GitHub.com/login/device")]
    [InlineData("https://github.com/login/device?next=https://evil.example")]
    [InlineData("javascript:alert(1)")]
    public void Only_GitHubs_device_page_passes_the_allowlist_and_a_signin_code_with_any_other_is_refused_whole(string uri)
    {
        Assert.Null(AuthState.AllowedUri(uri));
        var code = AuthState.ReadCode(Json(Code(uri: uri)))!;
        Assert.True(code.Refused);
        Assert.Null(code.Code);
        Assert.Null(code.Uri);
        Assert.Equal("https://github.com/login/device", AuthState.AllowedUri("https://github.com/login/device"));
    }

    [Theory]
    [InlineData("wdjb-mjht")]
    [InlineData("WDJBMJHT")]
    [InlineData("WDJB-MJHT-1234")]
    [InlineData("WDJ\u200BB-MJHT")]
    [InlineData("WDJB-MJH\u202E")]
    [InlineData(" WDJB-MJHT")]
    // .NET's $ matches before a final newline: the whole string is the code, or nothing is.
    [InlineData("WDJB-MJHT\n")]
    [InlineData("<b>WDJB</b>")]
    public void Only_a_code_of_GitHubs_shape_is_shown(string userCode)
    {
        Assert.Null(AuthState.ShownCode(userCode));
        Assert.True(AuthState.ReadCode(Json(Code(userCode: userCode)))!.Refused);
    }

    [Fact]
    public void A_signin_code_is_read_for_its_flow_code_link_and_expiry_and_nothing_else()
    {
        var code = AuthState.ReadCode(Json("""{"flow":"flow-1","agent":"copilot","userCode":"WDJB-MJHT","verificationUri":"https://github.com/login/device","expiresAt":"2099-01-01T00:00:00Z","deviceCode":"secret","accessToken":"secret"}"""))!;
        Assert.Equal(new DeviceCode("flow-1", AgentKind.Copilot, "WDJB-MJHT", "https://github.com/login/device", DateTimeOffset.Parse("2099-01-01T00:00:00Z")), code);
        // A field of the wrong type refuses it whole: no half-read code is shown.
        Assert.True(AuthState.ReadCode(Json("""{"flow":"flow-1","userCode":"WDJB-MJHT","verificationUri":"https://github.com/login/device","expiresAt":42}"""))!.Refused);
        // Text that isn't text (an unpaired surrogate makes GetString throw) is refused, never thrown on
        // the UI thread.
        Assert.True(AuthState.ReadCode(Json("""{"flow":"flow-1","userCode":"\uD800","verificationUri":"https://github.com/login/device","expiresAt":"2099-01-01T00:00:00Z"}"""))!.Refused);
        var done = AuthState.ReadDone(Json("""{"flow":"flow-1","outcome":"failed","reason":"\uD800","resumed":["\uD800","a"]}"""))!;
        Assert.Equal(((string?)null, "a"), (done.Reason, done.Resumed.Single()));
        Assert.Null(AuthState.ReadAttention(Json("""{"kind":"error","message":"\uD800"}"""))!.Message);
        // An expiry is an RFC 3339 time and nothing else: a lenient parse would take a comment beside it.
        Assert.Equal(DateTimeOffset.Parse("2099-01-01T00:00:00Z"), AuthState.ReadCode(Json("""{"flow":"f","userCode":"WDJB-MJHT","verificationUri":"https://github.com/login/device","expiresAt":"2099-01-01T01:00:00.1234567+01:00"}"""))!.ExpiresAt!.Value.AddTicks(-1234567));
        foreach (var bad in new[] { "Fri, 01 Jan 2099 00:00:00 GMT (" + GitHubToken + ")", "Fri, 01 Jan 2099 00:00:00 GMT", "2099-01-01", "2099-01-01T00:00:00", "01/01/2099 00:00:00 +00:00", "soon", GitHubToken })
        {
            Assert.True(AuthState.ReadCode(Json(JsonSerializer.Serialize(new { flow = "f", userCode = "WDJB-MJHT", verificationUri = "https://github.com/login/device", expiresAt = bad })))!.Refused, bad);
        }
        Assert.Null(AuthState.ReadCode(Json("""{"userCode":"WDJB-MJHT","verificationUri":"https://github.com/login/device","expiresAt":"2099-01-01T00:00:00Z"}""")));
        Assert.Equal("Code: W D J B, M J H T", AuthState.Spelled("WDJB-MJHT"));
    }

    [Fact]
    public void Text_holding_anything_shaped_like_a_token_is_never_shown_and_an_account_must_be_plain()
    {
        Assert.Null(AuthState.AgentText($"Bad credentials: {GitHubToken}", 300));
        Assert.Null(AuthState.AgentText($"token {Jwt}", 300));
        Assert.Null(AuthState.AgentText("key sk-" + new string('e', 30), 300));
        Assert.Null(AuthState.AgentText(new string('f', 40), 300));
        Assert.Equal("Not signed in. Run copilot login.", AuthState.AgentText("Not signed in.\n  Run copilot login.", 300));
        // An attention's message is the host's sentence: shown as it is, one line and revealed, nothing else.
        Assert.Equal("Copilot hit a rate limit (429).", AuthState.ReadAttention(Json("""{"kind":"error","errorClass":"rate","message":"Copilot hit a rate limit (429)."}"""))!.Message);
        Assert.Equal("Copilot stopped \\u202Ewith an error.", AuthState.PlainText("Copilot\nstopped  \u202Ewith an error.", 300));
        Assert.Null(AuthState.AccountName("octo cat"));
        Assert.Null(AuthState.AccountName("octo\u202Ecat"));
        Assert.Equal("octocat", AuthState.AccountName(" octocat "));
    }

    [Fact]
    public void Attention_reads_unknown_words_as_not_known_never_as_at_the_machine()
    {
        var a = AuthState.ReadAttention(Json("""{"kind":"somethingNew","errorClass":"weird","signIn":"rdp","message":"Boom"}"""))!;
        Assert.Equal((AuthState.Error, "other", "Boom"), (a.Kind, a.ErrorClass, a.Message));
        // A way to sign in this app doesn't know makes no claim, as none said does (6648000166). An
        // unknown recovery is failed, as the protocol reads it.
        var s = AuthState.ReadAttention(Json("""{"kind":"signInNeeded","signIn":"rdp","recovery":"later","account":"octocat"}"""))!;
        Assert.Equal((AuthState.SignInNeeded, (string?)null, "failed", "octocat"), (s.Kind, s.SignIn, s.Recovery, s.Account));
        Assert.Null(AuthState.ReadAttention(Json("""{"kind":"signInNeeded"}"""))!.SignIn);
        Assert.Null(AuthState.ReadAttention(Json("null")));
        // A start outcome this app doesn't know, or started with no flow, is refused: never a wait for
        // a code, nor a claim that the provider has no device flow.
        Assert.Equal("refused", AuthState.ReadStartResult(Json("""{"outcome":"later","flow":"flow-1"}""")).Outcome);
        Assert.Equal("refused", AuthState.ReadStartResult(Json("""{"outcome":"started"}""")).Outcome);
        Assert.Equal("failed", AuthState.ReadDone(Json("""{"flow":"flow-1","outcome":"later"}"""))!.Outcome);
        // Outcomes the protocol doesn't define are refused too, recovered and machine among them: only
        // signIn: machine on the attention says "Sign in at the machine" (Engineering, 6655456962). A
        // done's reason isn't in the protocol, and is never read.
        foreach (var outcome in new[] { "recovered", "machine" })
        {
            var r = AuthState.ReadStartResult(Json(JsonSerializer.Serialize(new { outcome, flow = "flow-1", reason = "this CLI has no device flow" })));
            Assert.Equal(("refused", (string?)null), (r.Outcome, r.Reason));
        }
        Assert.Null(AuthState.ReadDone(Json("""{"flow":"flow-1","outcome":"failed","reason":"The code was declined."}"""))!.Reason);
    }

    [Fact]
    public void Rule_10_a_session_that_needs_sign_in_waits_on_you_with_its_agent_and_account()
    {
        var s = StatusRules.For(Needing("a"));
        Assert.Equal((StatusRules.SignIn, StatusGlyph.Needs, "Needs sign-in · Copilot · @octocat", StatusTone.Needs), (s.Rule, s.Glyph, s.Text, s.Tone));
        // A question of its own still comes first.
        var asking = WithAttention(Make.Session(question: Make.Free()) with { Status = SessionStatus.Waiting }, """{"kind":"signInNeeded"}""");
        Assert.Equal(1, StatusRules.For(asking).Rule);
    }

    [Fact]
    public void Rule_11_an_error_says_its_class_in_the_apps_words_and_never_the_agents_message()
    {
        var s = StatusRules.For(WithAttention(Make.Session(), """{"kind":"error","errorClass":"auth","message":"Bad credentials"}"""));
        Assert.Equal((StatusRules.Error, StatusGlyph.Failed, "Not authorised"), (s.Rule, s.Glyph, s.Text));
        Assert.Equal("Rate limited", StatusRules.For(WithAttention(Make.Session(), """{"kind":"error","errorClass":"rate"}""")).Text);
        // A class without words of its own in ui.md §6.13 reads as "Agent error", as an unknown one does.
        Assert.Equal("Agent error", StatusRules.For(WithAttention(Make.Session(), """{"kind":"error","errorClass":"quota"}""")).Text);
        // Before rules 4 and 5: background work that survived the turn never hides the error.
        var delegating = StatusRules.For(WithAttention(Make.Session(background: Background.Of(2, 0)) with { Subagents = [new Subagent { Id = "d", Kind = SubagentKind.Background, Status = SubagentStatus.Working }] }, """{"kind":"error"}"""));
        Assert.Equal((StatusRules.Error, "Agent error · 2 delegates still running"), (delegating.Rule, delegating.Text));
    }

    [Fact]
    public void Rule_10_comes_after_the_sessions_own_question_and_before_a_delegates()
    {
        var asking = Make.Session(question: Make.Free() with { Agent = "d1" }) with
        {
            Status = SessionStatus.Waiting,
            WaitingOn = "d1",
            Subagents = [new Subagent { Id = "d1", Kind = SubagentKind.Background, Status = SubagentStatus.Waiting }],
        };
        Assert.Equal(2, StatusRules.For(asking).Rule);
        // The main agent can't run: a delegate's question never hides it (ui.md §6.13).
        var needing = StatusRules.For(WithAttention(asking, """{"kind":"signInNeeded"}"""));
        Assert.Equal((StatusRules.SignIn, "Needs sign-in"), (needing.Rule, needing.FirstFact));
        Assert.Equal("Signed in · waiting for the session to confirm", StatusRules.For(WithAttention(asking, """{"kind":"signInNeeded","recovery":"pending"}""")).Text);
    }

    [Fact]
    public void Rule_11_counts_every_delegate_still_running_task_ones_that_survived_the_turn_too()
    {
        var both = Make.Session(background: Background.Of(1, 0)) with
        {
            Subagents = [new Subagent { Id = "a", Kind = SubagentKind.Background, Status = SubagentStatus.Working }, new Subagent { Id = "t", Kind = SubagentKind.Task, Status = SubagentStatus.Working }],
        };
        Assert.Equal("Rate limited · 2 delegates still running", StatusRules.For(WithAttention(both, """{"kind":"error","errorClass":"rate"}""")).Text);
    }

    [Fact]
    public void Rule_12_a_working_session_the_host_has_not_heard_from_reads_Not_responding_never_Working()
    {
        var s = StatusRules.For(WithAttention(Make.Session(status: SessionStatus.Working), """{"kind":"notResponding","lastEventAt":"2026-11-05T00:30:00Z"}"""));
        Assert.Equal((StatusRules.NotResponding, StatusGlyph.Failed, "Not responding"), (s.Rule, s.Glyph, s.FirstFact));
        Assert.StartsWith("since ", s.Facts[1]);
        // Attention gone (null), the session's own status is back.
        Assert.Equal(3, StatusRules.For(WithAttention(Make.Session(status: SessionStatus.Working), "null")).Rule);
    }

    static IReadOnlyList<SignInBanners.Banner> Banners(IReadOnlyList<Session> sessions, bool canSignIn = true, Dictionary<string, SignInBanners.Flow>? flows = null, Dictionary<string, SignInBanners.Resolved>? resolved = null) =>
        SignInBanners.For([new SignInBanners.Source("m1", "DEV-VM1", sessions, canSignIn)], flows ?? [], resolved ?? [], DateTimeOffset.Parse("2026-11-05T00:45:00Z"));

    [Fact]
    public void One_banner_per_machine_agent_and_account_lists_its_sessions()
    {
        var other = WithAttention(Make.Session(id: "e"), """{"kind":"error","errorClass":"quota"}""");
        var banners = Banners([Needing("a"), Needing("b"), Needing("c", account: "hubot"), Needing("d", agent: AgentKind.Claude), other]);
        Assert.Equal(3, banners.Count);
        var b = banners.Single(x => x.Account == "octocat" && x.Agent == AgentKind.Copilot);
        Assert.Equal(new[] { "a", "b" }, b.Sessions.Select(s => s.Id).Order());
        Assert.Equal(("Copilot on DEV-VM1 needs sign-in · @octocat", "2 sessions affected", "Copilot on DEV-VM1 needs sign-in"), SignInBanners.Words(b));
        Assert.Equal("Sign in", SignInBanners.Verb(b));
    }

    [Fact]
    public void Only_the_host_saying_the_provider_has_no_device_flow_makes_it_sign_in_at_the_machine()
    {
        Assert.False(Banners([Needing("a")]).Single().AtMachine);
        Assert.False(Banners([Needing("a")], canSignIn: false).Single().AtMachine);
        // None said, as a word this app doesn't know: "Needs sign-in" with no action and no claim.
        var unsaid = Banners([Needing("a", signIn: null)]).Single();
        Assert.Equal((false, (string?)null), (unsaid.AtMachine, SignInBanners.Verb(unsaid)));
        // No capability: no button, and no claim about the machine either.
        Assert.Null(SignInBanners.Verb(Banners([Needing("a")], canSignIn: false).Single()));
        Assert.Equal("1 session affected", SignInBanners.Words(Banners([Needing("a")], canSignIn: false).Single()).Line);
        // A word this app doesn't know: no Sign in the host may not relay, and no claim about the machine.
        var unknown = Banners([Needing("a", signIn: "rdp")]).Single();
        Assert.Equal((false, (string?)null, "1 session affected"), (unknown.AtMachine, SignInBanners.Verb(unknown), SignInBanners.Words(unknown).Line));
        var machine = Banners([Needing("a", signIn: "machine")]).Single();
        Assert.True(machine.AtMachine);
        Assert.Null(SignInBanners.Verb(machine));
        Assert.Equal("Sign in at the machine", SignInBanners.Words(machine).Line);
    }

    [Fact]
    public void The_newest_session_needing_sign_in_decides_how_and_its_no_claim_wins_over_an_older_sessions()
    {
        Session At(string id, int minutes, string? signIn)
        {
            var how = signIn is null ? "" : ",\"signIn\":\"" + signIn + "\"";
            return WithAttention(Make.Session(id: id), $$"""{"kind":"signInNeeded","since":"2026-11-04T23:{{minutes:D2}}:00Z","agent":"copilot","account":"octocat"{{how}}}""");
        }
        foreach (var (newest, older) in new (string?, string)[] { ("future-flow", "phone"), (null, "phone"), ("future-flow", "machine"), (null, "machine") })
        {
            var b = Banners([At("old", 0, older), At("new", 30, newest)]).Single();
            Assert.Equal((false, false, (string?)null, "2 sessions affected"), (b.CanSignIn, b.AtMachine, SignInBanners.Verb(b), SignInBanners.Words(b).Line));
        }
        Assert.Equal("Sign in", SignInBanners.Verb(Banners([At("old", 0, "machine"), At("new", 30, "phone")]).Single()));
    }

    [Fact]
    public void A_flow_another_device_runs_offers_Show_code_and_one_that_failed_offers_Try_again_with_why()
    {
        Assert.Equal("Show code", SignInBanners.Verb(Banners([Needing("a", recovery: "signingIn")]).Single()));
        var key = SignInBanners.KeyOf("m1", AgentKind.Copilot, "octocat");
        var failed = Banners([Needing("a")], flows: new() { [key] = new SignInBanners.Flow("done", Outcome: "failed", Reason: "The code was not entered in time") }).Single();
        Assert.Equal("Try again", SignInBanners.Verb(failed));
        Assert.Equal("Sign-in failed · The code was not entered in time", SignInBanners.Words(failed).Line);
        // While this device's own flow runs, its sheet is open and the banner offers nothing.
        Assert.Null(SignInBanners.Verb(Banners([Needing("a")], flows: new() { [key] = new SignInBanners.Flow("pending", "flow-1") }).Single()));
        var signedIn = Banners([], resolved: new() { [key] = new SignInBanners.Resolved("signedIn", 2, DateTimeOffset.Parse("2026-11-05T00:44:30Z"), AgentKind.Copilot, "octocat", "m1", "DEV-VM1") }).Single();
        Assert.Equal(("Copilot on DEV-VM1 · @octocat", "Signed in · 2 sessions recovered"), (SignInBanners.Words(signedIn).Head, SignInBanners.Words(signedIn).Line));
        Assert.Empty(Banners([], resolved: new() { [key] = new SignInBanners.Resolved("signedIn", 2, DateTimeOffset.Parse("2026-11-05T00:43:00Z"), AgentKind.Copilot, "octocat", "m1", "DEV-VM1") }));
    }

    [Fact]
    public void Signed_in_and_waiting_for_the_sessions_to_confirm_is_never_recovered_and_a_new_failure_makes_it_needed_again()
    {
        var waiting = StatusRules.For(Needing("a", recovery: "pending"));
        Assert.Equal((StatusRules.SignIn, StatusGlyph.Idle, "Signed in · waiting for the session to confirm", StatusTone.Plain), (waiting.Rule, waiting.Glyph, waiting.Text, waiting.Tone));
        Assert.Equal("Needs sign-in", StatusRules.For(Needing("a", recovery: "failed")).FirstFact);
        // No recovery value says recovered (#818): one the app doesn't know is failed, as the protocol has it.
        Assert.Equal("Needs sign-in", StatusRules.For(Needing("a", recovery: "resumed")).FirstFact);
        // Driven by an agent, rules 10-12 keep their own tone (as the PWA's do), and the badge.
        // (Driver set before the attention is attached: it is kept beside the record it came with.)
        var driven = Make.Session() with { Driver = DriverKind.Agent };
        Assert.Equal((true, StatusTone.Plain), (StatusRules.For(WithAttention(driven, """{"kind":"signInNeeded","signIn":"phone","recovery":"pending"}""")) is var d ? (d.Driven, d.Tone) : default));
        Assert.Equal((true, StatusRules.For(Needing("a")).Tone), (StatusRules.For(WithAttention(driven, """{"kind":"signInNeeded","signIn":"phone"}""")) is var n ? (n.Driven, n.Tone) : default));
        var stalled = StatusRules.For(WithAttention(driven with { Status = SessionStatus.Working }, """{"kind":"notResponding"}"""));
        Assert.Equal((StatusRules.NotResponding, true, false), (stalled.Rule, stalled.Driven, stalled.Tone == StatusTone.Agent));
        Assert.Equal(StatusRules.For(WithAttention(Make.Session(), """{"kind":"error"}""")).Tone, StatusRules.For(WithAttention(Make.Session() with { Driver = DriverKind.Agent }, """{"kind":"error"}""")).Tone);
        Assert.Equal(StatusTone.Agent, StatusRules.For(Make.Session() with { Driver = DriverKind.Agent }).Tone);
        var b = Banners([Needing("a", recovery: "pending"), Needing("b", recovery: "pending")]).Single();
        Assert.Equal(("confirming", "Signed in · waiting for 2 sessions to confirm", (string?)null), (b.State, SignInBanners.Words(b).Line, SignInBanners.Verb(b)));
        // One fails sign-in again: needed, counting it alone.
        var again = Banners([Needing("a", recovery: "pending"), Needing("b", recovery: "failed")]).Single();
        Assert.Equal(("needed", "1 session affected", "Sign in"), (again.State, SignInBanners.Words(again).Line, SignInBanners.Verb(again)));
    }

    // The demo's other machine, as the app reaches it, running #800's scenario.
    sealed class Demo : IAsyncDisposable
    {
        public DemoHost Host { get; } = DemoHost.Start(remote: true);
        public HostConnection Connection { get; }
        public MainViewModel Main { get; }
        public List<string> Said { get; } = [];

        public Demo()
        {
            Connection = new HostConnection(ct => LocalClient.ConnectAsync(Host.Endpoint, TimeSpan.FromSeconds(1), ct), new LockedDispatcher()) { RetryDelay = TimeSpan.FromMilliseconds(100) };
            var deliveries = new Deliveries(Connection, new LockedDispatcher());
            Main = new MainViewModel(Connection, new CardContext { Deliveries = deliveries, Folds = new FoldStore(null), Capabilities = () => Connection.Capabilities, Host = Connection }, new UsageViewModel());
            Main.Announce += Said.Add;
            Connection.Start();
        }

        public SignInBannerViewModel? Banner => Main.SignIns.FirstOrDefault(b => b.Needed);

        // Every word the app's models hold for the banner and the sheet.
        public string Shown() => string.Join("\n", Main.SignIns.SelectMany(b => new[] { b.Head, b.Line, b.Problem, b.VerbName }.Concat(b.Sessions.Select(s => s.Title)))
            .Concat(Main.SignInSheet is { } s ? new[] { s.Title, s.StartingText, s.Page, s.Code, s.CodeName, s.Expiry, s.Copied, s.CopyName, s.OpenName, s.CancelName } : Array.Empty<string>())
            .Concat(Main.Sessions.SelectMany(c => c.Details.Select(d => d.Value)))
            .Concat(Said));

        bool stopped;

        // This machine's host goes: its pipe drops, as when the host restarts.
        public async Task StopHostAsync() { stopped = true; await Host.DisposeAsync(); }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            if (!stopped) { await Host.DisposeAsync(); }
        }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Sessions_that_need_sign_in_roll_up_into_a_banner_and_only_a_press_asks_the_machine_for_a_code()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { Sessions.Count: 3 } && demo.Main.Sessions.Any(c => c.Id == "demo-stalled"), "the sign-in banner");

        Assert.Equal(("Copilot on Build server needs sign-in · @demo-user", "3 sessions affected"), TestUi.Read(() => (demo.Banner!.Head, demo.Banner.Line)));
        Assert.Equal("Sign in to Copilot on Build server", TestUi.Read(() => demo.Banner!.VerbName));
        Assert.Equal(3, TestUi.Read(() => demo.Main.NeedsYou.Count(e => e.Mode == NeedsYouMode.SignIn)));
        // An error and a session not responding are on their rows, and not counted as waiting on you.
        Assert.Equal("Agent error", TestUi.Read(() => demo.Main.Sessions.Single(c => c.Id == "demo-error-quota").Line.FirstFact));
        // Both are driven by an agent: the row says what is wrong, not "Driven by an agent".
        Assert.Equal([(true, "Agent error"), (true, "Not responding")], TestUi.Read(() => new[] { "demo-error-quota", "demo-stalled" }.Select(id => demo.Main.Sessions.Single(c => c.Id == id)).Select(c => (c.Driven, c.RowStatus)).ToList()));
        Assert.Equal("Not responding", TestUi.Read(() => demo.Main.Sessions.Single(c => c.Id == "demo-stalled").Line.FirstFact));
        Assert.DoesNotContain(TestUi.Read(() => demo.Main.NeedsYou.ToList()), e => e.Card.Id is "demo-error-quota" or "demo-stalled");
        Assert.Contains("Copilot on Build server needs sign-in", demo.Said);
        // Nothing went to the machine, and there is no code anywhere, until a person presses.
        Assert.Empty(demo.Host.SignInLog);
        Assert.Null(TestUi.Read(() => demo.Main.SignInSheet));

        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code");
        var sheet = TestUi.Read(() => demo.Main.SignInSheet!);
        Assert.Equal(("WDJB-MJHT", "Code: W D J B, M J H T", "github.com/login/device", "Sign in to Copilot on Build server"), TestUi.Read(() => (sheet.Code, sheet.CodeName, sheet.Page, sheet.Title)));
        Assert.StartsWith("Waiting for sign-in · code expires ", TestUi.Read(() => demo.Banner!.Line));
        Assert.Equal(new Uri("https://github.com/login/device"), TestUi.Read(() => sheet.OpenUri));
        Assert.Equal(("Copy code for Copilot on Build server", "Open github.com to sign in to Copilot on Build server", "Cancel sign-in for Copilot on Build server"), TestUi.Read(() => (sheet.CopyName, sheet.OpenName, sheet.CancelName)));
        Assert.Equal(new[] { AuthState.Start }, demo.Host.SignInLog);
        // Never said aloud.
        Assert.DoesNotContain(demo.Said, s => s.Contains("WDJB", StringComparison.Ordinal));

        // Cancel: the flow is cancelled at the machine too, and the code goes with the sheet.
        TestUi.Read(() => { demo.Main.CancelSignIn(); return 0; });
        await Wait.UntilAsync(() => demo.Host.SignInLog.Count == 2, "the cancel");
        Assert.Equal(new[] { AuthState.Start, AuthState.Cancel }, demo.Host.SignInLog);
        Assert.Null(TestUi.Read(() => demo.Main.SignInSheet));
        Assert.DoesNotContain("WDJB", TestUi.Read(demo.Shown));
        Assert.Equal("Sign in", TestUi.Read(() => demo.Banner!.Verb));
    }

    // A press that joined another device's sign-in (busy): Cancel only closes this sheet. Sending
    // signin.cancel would end the other device's sign-in (Engineering, 6655456962).
    [Fact]
    [Trait("Category", "Network")]
    public async Task Cancel_on_a_sign_in_another_device_started_closes_the_sheet_and_cancels_nothing()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { Sessions.Count: 3 }, "the sign-in banner");
        demo.Host.SignInElsewhere();
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code");

        TestUi.Read(() => { demo.Main.CancelSignIn(); return 0; });
        await Wait.UntilAsync(() => TestUi.Read(() => demo.Main.SignInSheet) is null, "the sheet to close");
        await Task.Delay(500);
        Assert.Equal([AuthState.Start], demo.Host.SignInLog);
        Assert.DoesNotContain("WDJB", TestUi.Read(demo.Shown));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_cancelled_start_keeps_no_code_and_its_late_result_leaves_the_next_press_alone()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        // The code comes before the start's result: held for it, then cancelled with it.
        demo.Host.NextStartDelay = TimeSpan.FromMilliseconds(1500);
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInCodesHeld == 1, "the code held for the start");
        TestUi.Read(() => { demo.Main.CancelSignIn(); return 0; });
        Assert.Equal(0, TestUi.Read(() => demo.Main.SignInCodesHeld));
        // Pressed again: the first start's late result is not this one's, and closes nothing.
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "Sign in again");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code", why: () => $"sheet {demo.Main.SignInSheet?.Shape ?? "none"}; log {string.Join(",", demo.Host.SignInLog)}; banners {string.Join(" | ", demo.Main.SignIns.Select(b => $"{b.Line} {b.HasVerb}"))}");
        await Task.Delay(1800);
        Assert.True(TestUi.Read(() => demo.Main.SignInSheet is { HasCode: true }));
        Assert.Equal([AuthState.Start, AuthState.Start], demo.Host.SignInLog);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task The_abandoned_starts_result_coming_first_cancels_nothing_until_the_next_press_says_which_flow_it_is_in()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        // The first start answers at 0.8 s; the second, which joins the same flow, 0.8 s after it is
        // asked, so after the first's.
        demo.Host.NextStartDelay = TimeSpan.FromMilliseconds(800);
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is not null, "the sheet");
        TestUi.Read(() => { demo.Main.CancelSignIn(); return 0; });
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true, Busy: false }, "Sign in again");
        demo.Host.NextStartDelay = TimeSpan.FromMilliseconds(800);
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code", why: () => $"sheet {demo.Main.SignInSheet?.Shape ?? "none"}; log {string.Join(",", demo.Host.SignInLog)}");
        Assert.Equal([AuthState.Start, AuthState.Start], demo.Host.SignInLog);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task This_machines_host_going_mid_flow_closes_the_sheet_and_keeps_no_code()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code");
        Assert.Equal(3, TestUi.Read(() => demo.Main.NeedsYou.Count(e => e.Mode == NeedsYouMode.SignIn)));
        await demo.StopHostAsync();
        await Wait.UntilAsync(() => demo.Main.SignInSheet is null, "the sheet closed");
        // Needs you goes with the banner: nothing there to act on while this machine's host is away.
        await Wait.UntilAsync(() => demo.Main.NeedsYou.All(e => e.Mode != NeedsYouMode.SignIn), "Needs you without sign-in");
        Assert.Equal(0, TestUi.Read(() => demo.Main.SignInCodesHeld));
        Assert.DoesNotContain(demo.Said, s => s.Contains("recovered", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_second_recovery_within_the_minute_gets_its_own_words_and_count()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        await demo.Host.ClearSignInAsync();
        await Wait.UntilAsync(() => demo.Main.SignIns.Any(b => b.Line == "Recovered by itself"), "recovered by itself");
        // A new episode: needed again, then a person here signs in and the host clears them.
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "needed again");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code");
        await demo.Host.FinishSignInAsync("signedIn");
        await Wait.UntilAsync(() => demo.Main.SignIns.Any(b => b.Line == "Signed in \u00b7 waiting for 3 sessions to confirm"), "waiting to confirm");
        await demo.Host.ConfirmSignInAsync();
        await Wait.UntilAsync(() => demo.Main.SignIns.Any(b => b.Line == "Signed in \u00b7 3 sessions recovered"), "its own recovery", why: () => string.Join(" | ", demo.Main.SignIns.Select(b => b.Line)));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_code_that_came_before_a_slow_starts_result_is_still_there_when_the_result_comes()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        // The code comes at once; the start's result 11 s later, within its timeout.
        demo.Host.NextStartDelay = TimeSpan.FromSeconds(11);
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInCodesHeld == 1, "the code held for the start");
        await Task.Delay(TimeSpan.FromSeconds(10.3));
        // Past the ten seconds, the banners are drawn again (and what is held pruned): the code stays.
        TestUi.Read(() => { for (var i = 0; i < 5; i++) { demo.Main.Tick(); } return 0; });
        Assert.Equal(1, TestUi.Read(() => demo.Main.SignInCodesHeld));
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code", patience: TimeSpan.FromSeconds(5));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_held_code_that_runs_out_before_its_starts_result_leaves_only_that_it_did()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        demo.Host.NextCodeLife = TimeSpan.FromSeconds(1.5);
        demo.Host.NextStartDelay = TimeSpan.FromSeconds(4);
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInCodesHeld == 1, "the code held for the start");
        await Task.Delay(TimeSpan.FromSeconds(2));
        TestUi.Read(() => { for (var i = 0; i < 5; i++) { demo.Main.Tick(); } return 0; });
        Assert.Equal(0, TestUi.Read(() => demo.Main.SignInCodesHeld));
        // Once the start's result comes, the sheet says the code ran out and offers Try again.
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { Expired: true, HasCode: false, CanRetry: true }, "the expired sheet", patience: TimeSpan.FromSeconds(6),
            why: () => $"sheet {demo.Main.SignInSheet?.Shape ?? "none"}");
    }

    // Judged by growth, not milliseconds (#681): a run of "eyJ" with no dot is read once.
    [Fact]
    public void A_long_run_of_JWT_like_prefixes_is_judged_in_time_proportional_to_its_length()
    {
        Speed.IsLinear(n => string.Concat(Enumerable.Repeat("eyJ", n)), 16000, t => AuthState.TokenShaped(t));
        Speed.IsLinear(n => string.Concat(Enumerable.Repeat("eyJaaaaa ", n)), 16000, t => AuthState.TokenShaped(t));
        Assert.True(AuthState.TokenShaped("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0"));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_start_that_fails_closes_the_sheet_and_says_why_aloud_without_the_machines_words()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        demo.Host.NextStartFailure = "internal: stack trace and secrets";
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is null && demo.Banner is { HasVerb: true }, "the sheet closed");
        Assert.Contains("Couldn\u2019t start sign-in for Copilot on Build server. Try again.", demo.Said);
        Assert.DoesNotContain(demo.Said, s => s.Contains("internal", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_session_button_made_while_a_code_is_shown_keeps_nothing_that_reaches_it_once_the_code_is_cancelled()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code");
        var code = TestUi.Read(() => demo.Main.SignInSheet!.Code);
        // A title changes while the code is up: the banner's session buttons are made again.
        await demo.Host.RetitleAsync(DemoHost.SignInSessions[0], "Coordinate the release again");
        await Wait.UntilAsync(() => demo.Banner is { } b && b.Sessions.Any(s => s.Title == "Coordinate the release again"), "the renamed session");
        TestUi.Read(() => { demo.Main.CancelSignIn(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is null, "the sheet closed");
        Assert.Equal(0, TestUi.Read(() => demo.Main.SignInCodesHeld));
        var buttons = TestUi.Read(() => demo.Banner!.Sessions.ToList());
        Assert.Contains(buttons, s => s.Title == "Coordinate the release again");
        foreach (var b in buttons) { Assert.Null(Reaches(b.Open.Target, code, 0)); }

        // What a callback keeps, through the compiler's closures only: a banner, or the code itself.
        static string? Reaches(object? o, string code, int depth)
        {
            if (o is null || depth > 4) { return null; }
            foreach (var f in o.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
            {
                var v = f.GetValue(o);
                if (v is string s && s.Contains(code, StringComparison.Ordinal)) { return f.Name; }
                if (v is not null && v.GetType().Name == "Banner") { return $"{f.Name}: a banner"; }
                if (v is not null && v.GetType().Name.Contains('<', StringComparison.Ordinal) && Reaches(v, code, depth + 1) is { } inner) { return $"{f.Name}.{inner}"; }
            }
            return null;
        }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_start_refused_with_no_flow_drops_a_cancelled_presss_late_code_at_once()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        // Press A: its code comes late, after the person has cancelled.
        demo.Host.NextCodeDelay = TimeSpan.FromSeconds(1.5);
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Host.SignInLog.Count == 1 && demo.Main.SignInSheet is not null, "the first sheet");
        await Task.Delay(300);
        Assert.Equal(0, TestUi.Read(() => demo.Main.SignInCodesHeld));
        TestUi.Read(() => { demo.Main.CancelSignIn(); return 0; });
        // Press B: slow, and refused with no flow. A's late code comes while it starts, and is held for it.
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true, Busy: false }, "Sign in again");
        demo.Host.NextStartRefusal = "not now";
        demo.Host.NextStartDelay = TimeSpan.FromSeconds(4);
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInCodesHeld == 1, "A's late code held while B starts");
        await Wait.UntilAsync(() => demo.Main.SignInSheet is null && demo.Banner is { Line: "Sign-in refused \u00b7 not now" }, "B refused, the sheet closed", patience: TimeSpan.FromSeconds(8),
            why: () => $"sheet {demo.Main.SignInSheet?.Shape ?? "none"}; banner {demo.Banner?.Line}");
        // At once, not after the ten seconds an early event is otherwise held.
        Assert.Equal(0, TestUi.Read(() => demo.Main.SignInCodesHeld));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_start_that_names_another_flow_drops_a_cancelled_presss_late_code_once_its_own_are_replayed()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        // Press A: its code comes late, after the person has cancelled.
        demo.Host.NextCodeDelay = TimeSpan.FromSeconds(1.5);
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Host.SignInLog.Count == 1 && demo.Main.SignInSheet is not null, "the first sheet");
        await Task.Delay(300);
        TestUi.Read(() => { demo.Main.CancelSignIn(); return 0; });
        // Press B: slow, and started in a flow of its own. Its code and A's late one are both held for it.
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true, Busy: false }, "Sign in again");
        demo.Host.NextStartDelay = TimeSpan.FromSeconds(3);
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInCodesHeld == 2, "B's code and A's late one held while B starts");
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "B's own code", patience: TimeSpan.FromSeconds(8));
        // B's code is its flow's; A's, which nothing can claim now, is gone at once.
        Assert.Equal(1, TestUi.Read(() => demo.Main.SignInCodesHeld));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_machine_removed_takes_its_resolved_banner_with_it_at_once()
    {
        await using var fleet = new Fleeted();
        await fleet.Host.NeedSignInAsync();
        fleet.Join();
        await Wait.UntilAsync(() => fleet.Banner is { HasVerb: true }, "the sign-in banner");
        // Recovered by itself: the banner says so for a minute.
        await fleet.Host.ClearSignInAsync();
        bool Resolved() => fleet.Main.SignIns.Any(b => b.Kind == "resolved" && b.MachineKey == DemoHost.RemoteId);
        await Wait.UntilAsync(Resolved, "the resolved banner", why: () => string.Join(" | ", fleet.Main.SignIns.Select(b => $"{b.Kind} {b.MachineKey} {b.Line}")));
        // Signed out of the relay: the machine goes, and nothing of it stays on screen.
        fleet.SignOut();
        await Wait.UntilAsync(() => !Resolved(), "the resolved banner gone with its machine", patience: TimeSpan.FromSeconds(5),
            why: () => string.Join(" | ", fleet.Main.SignIns.Select(b => $"{b.Kind} {b.MachineKey} {b.Line}")));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_sign_in_entry_in_Needs_you_follows_its_sessions_since_when_a_new_failure_moves_it()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        var id = DemoHost.SignInSessions[0];
        await Wait.UntilAsync(() => demo.Main.NeedsYou.Any(e => e.Card.Id == id), "the sign-in entry");
        var entry = TestUi.Read(() => demo.Main.NeedsYou.First(e => e.Card.Id == id));
        var before = TestUi.Read(() => (entry.AskedAt, entry.Meta));
        Assert.EndsWith("4m", before.Meta, StringComparison.Ordinal);
        await demo.Host.FailSignInAgainAsync();
        await Wait.UntilAsync(() => entry.AskedAt > before.AskedAt.AddMinutes(1), "the entry's age following the new failure's since",
            why: () => $"entry {entry.AskedAt:O}; session {AuthState.Of(entry.Card.Session)?.Since:O}");
        var after = TestUi.Read(() => (Same: demo.Main.NeedsYou.Contains(entry), entry.AskedAt, entry.Meta, Since: AuthState.Of(entry.Card.Session)!.Since));
        // The same entry, so focus on it stays, with the age its order is by.
        Assert.True(after.Same);
        Assert.Equal(after.Since, after.AskedAt);
        Assert.False(after.Meta.EndsWith("4m", StringComparison.Ordinal), after.Meta);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Try_again_goes_live_once_the_host_no_longer_offers_this_device_sign_in()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        demo.Host.NextVerificationUri = "https://github.com.evil.example/login/device";
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { Refused: true, CanRetry: true }, "the refused sheet, with Try again");
        // The newest session stops claiming phone sign-in: no claim (#696).
        await demo.Host.SignInWayAsync(null);
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { Refused: true, CanRetry: false }, "Try again gone", why: () => $"sheet {demo.Main.SignInSheet?.Shape ?? "none"} {demo.Main.SignInSheet?.CanRetry}");
        TestUi.Read(() => { demo.Main.RetrySignIn(); return 0; });
        await Task.Delay(300);
        Assert.Equal([AuthState.Start], demo.Host.SignInLog);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_press_after_Cancel_waits_for_the_cancel_before_it_starts()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code");
        demo.Host.NextCancelDelay = TimeSpan.FromSeconds(1);
        TestUi.Read(() => { demo.Main.CancelSignIn(); return 0; });
        // Pressed again at once: the cancel is still stopping the old flow.
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true, Busy: false }, "Sign in again");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true } && demo.Host.SignInLog.Count == 4, "the new code", patience: TimeSpan.FromSeconds(8), why: () => string.Join(",", demo.Host.SignInLog));
        // The new start only once the old flow's cancel has finished: never joining the flow being stopped.
        Assert.Equal([AuthState.Start, AuthState.Cancel, "cancelled", AuthState.Start], demo.Host.SignInLog);
        await Task.Delay(300);
        Assert.True(TestUi.Read(() => demo.Main.SignInSheet is { HasCode: true }));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_press_whose_wait_was_cancelled_leaves_the_next_press_still_waiting_for_the_slow_cancel()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code");
        demo.Host.NextCancelDelay = TimeSpan.FromSeconds(2);
        TestUi.Read(() => { demo.Main.CancelSignIn(); return 0; });
        // Pressed again, and cancelled while it waits for the slow cancel.
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true, Busy: false }, "Sign in again");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Task.Delay(200);
        TestUi.Read(() => { demo.Main.CancelSignIn(); return 0; });
        // Pressed a third time: the old cancel still hasn't finished, and this press waits for it too.
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true, Busy: false }, "Sign in a third time");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true } && demo.Host.SignInLog.Count == 4, "the new code", patience: TimeSpan.FromSeconds(8), why: () => string.Join(",", demo.Host.SignInLog));
        Assert.Equal([AuthState.Start, AuthState.Cancel, "cancelled", AuthState.Start], demo.Host.SignInLog);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Try_again_given_up_while_its_cancel_finishes_closes_the_sheet_and_sends_nothing()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        demo.Host.NextVerificationUri = "https://github.com.evil.example/login/device";
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { Refused: true, CanRetry: true }, "the refused sheet");
        demo.Host.NextCancelDelay = TimeSpan.FromSeconds(1.5);
        TestUi.Read(() => { demo.Main.RetrySignIn(); return 0; });
        await Task.Delay(200);
        // The host stops offering this device sign-in while the cancel finishes.
        await demo.Host.SignInWayAsync(null);
        await Wait.UntilAsync(() => demo.Main.SignInSheet is null, "the sheet closed, not left on Starting", patience: TimeSpan.FromSeconds(5),
            why: () => $"sheet {demo.Main.SignInSheet?.Shape ?? "none"}; log {string.Join(",", demo.Host.SignInLog)}");
        await Task.Delay(300);
        Assert.Equal([AuthState.Start, AuthState.Cancel, "cancelled"], demo.Host.SignInLog);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_gap_in_this_machines_events_lets_the_flow_and_its_code_go_until_the_new_snapshot_is_whole()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code");
        // Events lost: a signin.done among them may never come. The recovery reads hello again before
        // it asks for the sessions, and that reply is held: the list still looks whole meanwhile.
        var hello = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        demo.Host.HoldHello = hello;
        foreach (var ch in demo.Host.Subscribers.OfType<AgentBridge.Local.LocalChannel>().Where(c => c.IsOpen)) { ch.SkipEventSeq(3); }
        await demo.Host.RetitleAsync(DemoHost.SignInSessions[0], "Coordinate the release again");
        await Wait.UntilAsync(() => demo.Host.HoldHello is null, "the recovery's hello");
        await Wait.UntilAsync(() => demo.Main.SignInSheet is null, "the sheet closed at the gap, before the new snapshot", why: () => $"sheet {demo.Main.SignInSheet?.Shape ?? "none"}");
        Assert.Equal(0, TestUi.Read(() => demo.Main.SignInCodesHeld));
        hello.SetResult();
        // Back once the replacement snapshot is whole, with a press offered again.
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true } && demo.Banner.Sessions.Any(s => s.Title == "Coordinate the release again"), "the banner back after the new snapshot");
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Code_copied_is_said_once_for_each_Copy_and_never_again_as_the_sheet_is_drawn()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code");
        var sheet = TestUi.Read(() => demo.Main.SignInSheet!);
        var said = new List<string>();
        var drawn = 0;
        sheet.CopiedSaid += said.Add;
        sheet.PropertyChanged += (_, _) => drawn++;
        TestUi.Read(() => { sheet.CopiedCode(true); return 0; });
        // The sheet drawn again, as every sync does.
        await demo.Host.RetitleAsync(DemoHost.SignInSessions[0], "Coordinate the release again");
        await Wait.UntilAsync(() => demo.Banner is { } b && b.Sessions.Any(s => s.Title == "Coordinate the release again"), "the sheet drawn again");
        Assert.True(drawn > 1);
        Assert.Equal(["Code copied"], said);
        TestUi.Read(() => { sheet.CopiedCode(true); return 0; });
        Assert.Equal(["Code copied", "Code copied"], said);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Try_again_waits_for_the_cancel_before_it_starts_again()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        demo.Host.NextVerificationUri = "https://github.com.evil.example/login/device";
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { Refused: true, CanRetry: true }, "the refused sheet");
        demo.Host.NextVerificationUri = "https://github.com/login/device";
        demo.Host.NextCancelDelay = TimeSpan.FromSeconds(1);
        TestUi.Read(() => { demo.Main.RetrySignIn(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the new code", patience: TimeSpan.FromSeconds(8), why: () => string.Join(",", demo.Host.SignInLog));
        // The new start only once the old flow's cancel has finished: never joining the flow being stopped.
        Assert.Equal([AuthState.Start, AuthState.Cancel, "cancelled", AuthState.Start], demo.Host.SignInLog);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Every_way_an_attempt_ends_is_said_aloud_never_in_the_machines_words()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        // Refused: the sheet closes, and the banner's new line isn't read on its own.
        demo.Host.NextStartRefusal = "secret machine words";
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is null && demo.Banner is { HasVerb: true, Busy: false }, "refused");
        Assert.Contains("Sign-in refused for Copilot on Build server", demo.Said);
        // A code that couldn't be shown.
        demo.Host.NextVerificationUri = "https://github.com.evil.example/login/device";
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { Refused: true }, "the refused code");
        await Wait.UntilAsync(() => demo.Said.Contains("The sign-in code for Copilot on Build server couldn\u2019t be shown"), "said", why: () => string.Join(" | ", demo.Said));
        Assert.DoesNotContain(demo.Said, s => s.Contains("secret", StringComparison.Ordinal) || s.Contains("evil", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_code_that_runs_out_is_dropped_and_the_sheet_offers_Try_again()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { HasVerb: true }, "the sign-in banner");
        demo.Host.NextCodeLife = TimeSpan.FromSeconds(2);
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code");
        await Task.Delay(2200);
        TestUi.Read(() => { for (var i = 0; i < 5; i++) { demo.Main.Tick(); } return 0; });
        Assert.True(TestUi.Read(() => demo.Main.SignInSheet is { Expired: true, HasCode: false, CanRetry: true }));
        Assert.Equal(0, TestUi.Read(() => demo.Main.SignInCodesHeld));
        Assert.Contains("Sign-in code expired for Copilot on Build server", demo.Said);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_machine_that_goes_away_mid_flow_keeps_no_code_never_reads_as_recovered_and_offers_a_press_once_back()
    {
        await using var fleet = new Fleeted();
        await fleet.Host.NeedSignInAsync();
        fleet.Join();
        await Wait.UntilAsync(() => fleet.Banner is { HasVerb: true }, "the sign-in banner");
        TestUi.Read(() => { fleet.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => fleet.Main.SignInSheet is { HasCode: true }, "the code");

        fleet.Leave();
        await Wait.UntilAsync(() => fleet.Main.SignInSheet is null, "the sheet closed");
        Assert.Equal(0, TestUi.Read(() => fleet.Main.SignInCodesHeld));
        await Task.Delay(300);
        Assert.DoesNotContain(fleet.Said, s => s.Contains("recovered", StringComparison.Ordinal));

        fleet.Join();
        await Wait.UntilAsync(() => fleet.Banner is { HasVerb: true }, "a press again once back", why: () => string.Join(" | ", fleet.Main.SignIns.Select(b => $"{b.Line} {b.HasVerb}")));
        Assert.DoesNotContain(fleet.Said, s => s.Contains("recovered", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_late_code_from_a_cancelled_machine_is_never_held_while_another_machine_starts()
    {
        await using var fleet = new Fleeted(two: true);
        await fleet.Host.NeedSignInAsync();
        await fleet.Other!.NeedSignInAsync();
        fleet.Join();
        await Wait.UntilAsync(() => fleet.BannerOn(DemoHost.RemoteId) is { HasVerb: true } && fleet.BannerOn(Fleeted.OtherId) is { HasVerb: true }, "both banners");
        // Build server: its code comes late, after the person has cancelled.
        fleet.Host.NextCodeDelay = TimeSpan.FromSeconds(1.5);
        TestUi.Read(() => { fleet.BannerOn(DemoHost.RemoteId)!.Press(); return 0; });
        await Wait.UntilAsync(() => fleet.Host.SignInLog.Count == 1 && fleet.Main.SignInSheet is not null, "the first sheet");
        await Task.Delay(300);
        TestUi.Read(() => { fleet.Main.CancelSignIn(); return 0; });
        // Build server 2 starts, its result slow: its own code is held for it, and nothing else.
        fleet.Other.NextStartDelay = TimeSpan.FromSeconds(4);
        await Wait.UntilAsync(() => fleet.BannerOn(Fleeted.OtherId) is { HasVerb: true, Busy: false }, "Sign in on the other");
        TestUi.Read(() => { fleet.BannerOn(Fleeted.OtherId)!.Press(); return 0; });
        await Wait.UntilAsync(() => fleet.Main.SignInCodesHeld == 1, "the other's own code held");
        await Task.Delay(TimeSpan.FromSeconds(1.8));
        Assert.Equal(1, TestUi.Read(() => fleet.Main.SignInCodesHeld));
        await Wait.UntilAsync(() => fleet.Main.SignInSheet is { HasCode: true }, "the other's code", patience: TimeSpan.FromSeconds(6));
    }

    // The demo's other machine through the relay, as Fleet reaches it, so that it can go away.
    sealed class Fleeted : IAsyncDisposable
    {
        readonly FakeHost localHost = new();
        readonly LockedDispatcher ui = new();
        readonly FleetTests.FakeFleetLink link = new();
        readonly HostConnection local;
        readonly Fleet fleet;
        public DemoHost Host { get; } = DemoHost.Start(remote: true);
        // A second machine of the account, as the relay lists it: "Build server 2".
        public DemoHost? Other { get; }
        public const string OtherId = "demo-remote-2";
        public MainViewModel Main { get; }
        public List<string> Said { get; } = [];

        public Fleeted(bool two = false)
        {
            localHost.Start();
            link.Endpoints[DemoHost.RemoteId] = Host.Endpoint;
            if (two) { Other = DemoHost.Start(remote: true); link.Endpoints[OtherId] = Other.Endpoint; }
            local = localHost.Connect(ui);
            Main = new MainViewModel(Build(Machine.LocalKey, local), new UsageViewModel());
            Main.Announce += s => { lock (Said) { Said.Add(s); } };
            fleet = new Fleet(link, Main, ui, h => Build(h.Id, new HostConnection(ct => link.OpenAsync(h.Id, ct), ui) { RetryDelay = TimeSpan.FromMilliseconds(100) }));
        }

        Machine Build(string key, HostConnection host)
        {
            Machine? m = null;
            return m = new Machine(key, host, new CardContext { Deliveries = new Deliveries(host, ui), Folds = new FoldStore(null), Capabilities = () => host.Capabilities, MachineName = () => m?.Name, Host = host });
        }

        public void Join() => link.Set(FleetState.Connected, Other is null ? [(DemoHost.RemoteId, "Build server")] : [(DemoHost.RemoteId, "Build server"), (OtherId, "Build server 2")]);
        public SignInBannerViewModel? BannerOn(string machine) => Main.SignIns.FirstOrDefault(b => b.Needed && b.Key.StartsWith(machine + "\n", StringComparison.Ordinal));
        public void Leave() => link.Set(FleetState.Connected);
        public void SignOut() => link.Set(FleetState.SignedOut);
        public SignInBannerViewModel? Banner => Main.SignIns.FirstOrDefault(b => b.Needed);

        public async ValueTask DisposeAsync()
        {
            await fleet.DisposeAsync();
            await local.DisposeAsync();
            await localHost.DisposeAsync();
            await Host.DisposeAsync();
            if (Other is not null) { await Other.DisposeAsync(); }
        }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Once_the_person_signs_in_the_sessions_wait_to_confirm_and_are_recovered_only_when_the_host_clears_them()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { Sessions.Count: 3 }, "the sign-in banner");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code");

        // signin.done alone: the sheet closes, and the host's recovery pending says the rest.
        await demo.Host.FinishSignInAsync("signedIn");
        await Wait.UntilAsync(() => demo.Main.SignIns.Any(b => b.Line == "Signed in · waiting for 3 sessions to confirm"), "waiting to confirm", why: () => string.Join(" | ", demo.Main.SignIns.Select(b => b.Line)));
        Assert.Null(TestUi.Read(() => demo.Main.SignInSheet));
        Assert.Equal(3, TestUi.Read(() => DemoHost.SignInSessions.Count(id => demo.Main.Sessions.Single(c => c.Id == id).Line.Text == "Signed in · waiting for the session to confirm")));
        Assert.Equal(0, TestUi.Read(() => demo.Main.NeedsYou.Count(e => e.Mode == NeedsYouMode.SignIn)));
        Assert.DoesNotContain(TestUi.Read(() => demo.Main.SignIns.ToList()), b => b.Line.Contains("recovered", StringComparison.Ordinal) || b.HasVerb);
        Assert.Contains("Copilot on Build server: Signed in · waiting for 3 sessions to confirm", demo.Said);

        // The model answered: the host clears them, and only now is it recovered.
        await demo.Host.ConfirmSignInAsync();
        await Wait.UntilAsync(() => demo.Main.SignIns.Any(b => b.Line == "Signed in · 3 sessions recovered"), "recovered", why: () => string.Join(" | ", demo.Main.SignIns.Select(b => b.Line)));
        Assert.Contains("Copilot on Build server: Signed in · 3 sessions recovered", demo.Said);
        Assert.True(TestUi.Read(() => DemoHost.SignInSessions.All(id => demo.Main.Sessions.Single(c => c.Id == id).Line.FirstFact == "Idle")));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_session_that_fails_sign_in_again_while_confirming_goes_back_to_Needs_sign_in()
    {
        await using var demo = new Demo();
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { Sessions.Count: 3 }, "the sign-in banner");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { HasCode: true }, "the code");
        await demo.Host.FinishSignInAsync("signedIn");
        await Wait.UntilAsync(() => demo.Main.SignIns.Any(b => b.Line == "Signed in · waiting for 3 sessions to confirm"), "waiting to confirm");

        await demo.Host.FailSignInAgainAsync();
        await Wait.UntilAsync(() => demo.Banner is { Line: "3 sessions affected", HasVerb: true }, "needed again", why: () => string.Join(" | ", demo.Main.SignIns.Select(b => b.Line)));
        Assert.Equal(3, TestUi.Read(() => demo.Main.NeedsYou.Count(e => e.Mode == NeedsYouMode.SignIn)));
        Assert.True(TestUi.Read(() => DemoHost.SignInSessions.All(id => demo.Main.Sessions.Single(c => c.Id == id).Line.FirstFact == "Needs sign-in")));
    }

    [Theory]
    [Trait("Category", "Network")]
    [InlineData("WDJB-MJHT", "https://github.com.evil.example/login/device")]
    [InlineData("WDJB-MJHT", "http://github.com/login/device")]
    [InlineData("wdjb-mjht", "https://github.com/login/device")]
    public async Task A_spoofed_link_or_code_is_refused_whole_with_nothing_to_open_or_copy(string userCode, string uri)
    {
        await using var demo = new Demo();
        demo.Host.NextUserCode = userCode;
        demo.Host.NextVerificationUri = uri;
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { Sessions.Count: 3 }, "the sign-in banner");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { Refused: true }, "the refusal");
        var sheet = TestUi.Read(() => demo.Main.SignInSheet!);
        Assert.Equal(("", "", (Uri?)null), TestUi.Read(() => (sheet.Code, sheet.Page, sheet.OpenUri)));
        Assert.DoesNotContain("evil", TestUi.Read(demo.Shown));
        Assert.DoesNotContain("wdjb", TestUi.Read(demo.Shown));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task No_token_the_host_sends_beside_the_code_reaches_the_app_and_its_message_is_shown_as_it_is()
    {
        await using var demo = new Demo();
        demo.Host.NextUserCode = GitHubToken;
        await demo.Host.NeedSignInAsync();
        await Wait.UntilAsync(() => demo.Banner is { Sessions.Count: 3 }, "the sign-in banner", patience: TimeSpan.FromSeconds(30),
            why: () => $"link {demo.Connection.State}, complete {demo.Connection.Sessions.Complete}, snapshots {demo.Connection.Sessions.Snapshots}, banners {string.Join(" | ", demo.Main.SignIns.Select(b => $"{b.Line} ({b.Sessions.Count})"))}");
        TestUi.Read(() => { demo.Banner!.Press(); return 0; });
        await Wait.UntilAsync(() => demo.Main.SignInSheet is { Refused: true }, "the refusal");
        Assert.DoesNotContain(GitHubToken, TestUi.Read(demo.Shown));
        // The host's message is in Details as it is, as plain words (6649775484).
        Assert.Contains(TestUi.Read(() => demo.Main.Sessions.Single(c => c.Id == "demo-error-quota").Details.ToList()), d => d.Label == "Message" && d.Value == "Claude stopped with an error.");
    }
}
