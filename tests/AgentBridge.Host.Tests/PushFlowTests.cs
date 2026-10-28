using System.Text;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Xunit;

// Notifications end to end: a phone registers over its Noise channel, a fixture Copilot
// session read by the real SessionSource asks a question, and the host's push goes,
// encrypted to the phone's key, through the in-process relay to its fake push service.
public sealed class PushFlowTests : IAsyncLifetime
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    readonly Fixture f = new();
    readonly P256KeyPair hostKey = P256KeyPair.Generate();
    readonly P256KeyPair browser = P256KeyPair.Generate();
    readonly byte[] auth = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
    readonly List<string> hostLog = [];
    TestRelay relay = null!;
    SessionSource source = null!;
    PushStore store = null!;
    PushNotifier notifier = null!;
    Device host = null!;
    Device phone = null!;
    string token = "";
    string events = "";
    int second = 10;

    public async Task InitializeAsync()
    {
        events = f.CopilotSession(Id, 100, name: "Asking session");
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2));
        f.Processes.Run(AgentKind.Copilot, 100);
        source = new SessionSource(f.Options(), f.Processes, TimeProvider.System);
        await source.RefreshAsync();
        relay = await TestRelay.StartAsync();
        token = relay.TokenFor(1, "octo");
        host = await relay.ConnectAsync(token, "Test host", "host", hostKey);
        var machine = new MachineInfo(host.Id, "Test host", "windows", "0.1.0");
        store = PushStore.Load(Path.Combine(f.Root, "push-subscriptions.json"), "1");
        notifier = new PushNotifier(source, machine, store,
            new PushOptions { QuestionDelay = TimeSpan.FromMilliseconds(50), FinishedDelay = TimeSpan.FromMilliseconds(50) }, l => { lock (hostLog) { hostLog.Add(l); } });
        notifier.Start();
        notifier.Attach(new RelayPushSender(host.Relay));
        Serve(host, new AnsweredAtOnce(this));
        phone = await relay.ConnectAsync(token, "iPhone", "pwa");
    }

    public async Task DisposeAsync()
    {
        await notifier.DisposeAsync();
        await relay.DisposeAsync();
        await source.DisposeAsync();
        hostKey.Dispose();
        browser.Dispose();
        f.Dispose();
    }

    void Serve(Device d, ISessionAnswerer answers)
    {
        d.Hub.JsonOptions = ProtocolJson.Options;
        d.Hub.Handler = new SessionService(source, new MachineInfo(host.Id, "Test host", "windows", "0.1.0"), answers: answers, push: notifier).HandleAsync;
    }

    async Task<SecureChannel> OpenAsync()
    {
        await phone.Relay.WaitForPeerAsync(p => p.Id == host.Id, Patience);
        return await phone.Hub.OpenAsync(host.Id);
    }

    static JsonElement Result(JsonElement response)
    {
        Assert.True(response.GetProperty("ok").GetBoolean(), response.GetRawText());
        return response.GetProperty("result");
    }

    string Endpoint(string path = "push/iphone") => $"{relay.PushService.Url}/{path}";

    async Task SubscribeAsync(SecureChannel ch, string? endpoint = null)
    {
        var r = Result(await ch.RequestAsync(Ops.PushSubscribe, new
        {
            endpoint = endpoint ?? Endpoint(),
            keys = new { p256dh = B64Url.Encode(browser.Public), auth = B64Url.Encode(auth) },
            app = "https://danswett.github.io/agent-bridge/app/",
            device = "iPhone",
        }));
        Assert.True(r.GetProperty("subscribed").GetBoolean());
    }

    async Task AskAsync(string question)
    {
        Fixture.Append(events, Copilot.ToolStart(question, "ask_user", new
        {
            message = "Which database should the session store use?",
            requestedSchema = new { properties = new { pick = new { type = "string", @enum = new[] { "a", "b" } } } },
        }, second++));
        await source.RefreshAsync();
    }

    async Task AnsweredAtTheMachineAsync(string question)
    {
        Fixture.Append(events, Copilot.Answered(question, second++));
        await source.RefreshAsync();
    }

    FakePushService.Push[] Pushes() => [.. relay.PushService.Received];

    JsonElement Open(FakePushService.Push p) => JsonDocument.Parse(WebPushCrypto.Decrypt(p.Body, browser, auth)).RootElement.GetProperty("notification");

    [Fact]
    public async Task A_question_reaches_the_phone_encrypted_through_the_relay_and_is_replaced_once_answered_at_the_machine()
    {
        var ch = await OpenAsync();
        var hello = Result(await ch.RequestAsync(Ops.Hello, new { }));
        Assert.Contains(Capabilities.Push, hello.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));
        await SubscribeAsync(ch);

        await AskAsync("q1");
        await Wait.UntilAsync(() => Pushes().Length == 1, "the question's push", Patience);
        var push = Pushes()[0];
        var n = Open(push);
        Assert.Equal("Asking session", n.GetProperty("title").GetString());
        Assert.Equal("Test host · waiting on you\nWhich database should the session store use?", n.GetProperty("body").GetString());
        Assert.Equal(("question", Id, "q1"), (n.GetProperty("data").GetProperty("kind").GetString(), n.GetProperty("data").GetProperty("session").GetString(), n.GetProperty("data").GetProperty("question").GetString()));
        Assert.Equal("/push/iphone", push.Path);
        Assert.Equal(("aes128gcm", "high", "86400", PushNotice.Topic(host.Id, Id)), (push.Headers["Content-Encoding"], push.Headers["Urgency"], push.Headers["TTL"], push.Headers["Topic"]));
        Assert.StartsWith("vapid t=", push.Headers["Authorization"]);

        // Neither the relay nor the push service can read it: the body is ciphertext, and
        // the relay's log has neither the question nor the endpoint (a capability).
        Assert.DoesNotContain("database", Encoding.UTF8.GetString(push.Body));
        Assert.DoesNotContain(relay.Logs, l => l.Contains("database") || l.Contains(Endpoint()) || l.Contains("Asking session"));

        await AnsweredAtTheMachineAsync("q1");
        await Wait.UntilAsync(() => Pushes().Length == 2, "the resolved push", Patience);
        var resolved = Open(Pushes()[1]);
        Assert.Equal(("resolved", true, n.GetProperty("tag").GetString()), (resolved.GetProperty("data").GetProperty("kind").GetString(), resolved.GetProperty("silent").GetBoolean(), resolved.GetProperty("tag").GetString()));
        Assert.Equal(PushNotice.Topic(host.Id, Id), Pushes()[1].Headers["Topic"]);
    }

    [Fact]
    public async Task A_question_answered_from_the_phone_sends_that_phone_no_resolved_notice()
    {
        var ch = await OpenAsync();
        await SubscribeAsync(ch);
        await AskAsync("q1");
        await Wait.UntilAsync(() => Pushes().Length == 1, "the question's push", Patience);

        var answer = Result(await ch.RequestAsync(Ops.SessionAnswer, new { id = Id, question = "q1", request = Guid.NewGuid().ToString(), fields = new { pick = "a" } }));
        Assert.Equal("delivered", answer.GetProperty("outcome").GetString());
        await Wait.UntilAsync(() => source.Get(Id)?.Question is null, "the question to go", Patience);
        await Task.Delay(500);

        Assert.Single(Pushes());
    }

    [Fact]
    public async Task A_question_asked_while_the_host_is_reconnecting_reaches_the_phone_once_it_is_back_exactly_once()
    {
        var ch = await OpenAsync();
        await SubscribeAsync(ch);

        await host.Relay.DisposeAsync();
        await AskAsync("q1");
        await Task.Delay(500);
        Assert.Empty(Pushes());

        var again = await relay.ConnectAsync(token, "Test host", "host", hostKey);
        notifier.Attach(new RelayPushSender(again.Relay));
        await Wait.UntilAsync(() => Pushes().Length == 1, "the push after reconnecting", Patience);
        await Task.Delay(500);
        Assert.Single(Pushes());
        Assert.Equal("question", Open(Pushes()[0]).GetProperty("data").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_subscription_the_push_service_says_is_gone_is_dropped_and_no_longer_listed()
    {
        var ch = await OpenAsync();
        await SubscribeAsync(ch, Endpoint("gone/iphone"));
        var listed = Result(await ch.RequestAsync(Ops.PushList, new { })).GetProperty("devices");
        Assert.Equal(("iPhone", "127.0.0.1", true), (listed[0].GetProperty("name").GetString(), listed[0].GetProperty("service").GetString(), listed[0].GetProperty("current").GetBoolean()));
        Assert.DoesNotContain("gone/iphone", listed.GetRawText());

        await AskAsync("q1");
        await Wait.UntilAsync(() => store.Count == 0, "the gone subscription to be dropped", Patience);
        Assert.Equal(0, Result(await ch.RequestAsync(Ops.PushList, new { })).GetProperty("devices").GetArrayLength());
        // Logged just after the store drops it, so waited for rather than raced.
        await Wait.UntilAsync(() => { lock (hostLog) { return hostLog.Any(l => l.Contains("is gone (410)")); } }, "the drop to be logged", Patience);
    }

    [Fact]
    public async Task A_phone_that_turns_notifications_off_gets_nothing_more()
    {
        var ch = await OpenAsync();
        await SubscribeAsync(ch);
        Assert.True(Result(await ch.RequestAsync(Ops.PushUnsubscribe, new { endpoint = Endpoint() })).GetProperty("removed").GetBoolean());

        await AskAsync("q1");
        await Task.Delay(500);
        Assert.Empty(Pushes());
        Assert.Equal(0, PushStore.Load(Path.Combine(f.Root, "push-subscriptions.json"), "1").Count);
    }

    // Plays the CLI for an answer from the phone: the session records it at once.
    sealed class AnsweredAtOnce(PushFlowTests t) : ISessionAnswerer
    {
        public bool Available => true;

        public async Task<AnswerResult> AnswerAsync(AnswerRequest request, CancellationToken cancellationToken = default)
        {
            await t.AnsweredAtTheMachineAsync(request.Question);
            return new AnswerResult(AnswerOutcome.Delivered);
        }
    }
}
