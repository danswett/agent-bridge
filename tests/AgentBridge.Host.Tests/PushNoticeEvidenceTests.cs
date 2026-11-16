using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

// #2649: what the host learns about each question notice it pushes to a phone, so the answer-away evidence can say
// the notice arrived and not only that an answer did.
public sealed class PushNoticeEvidenceTests : IDisposable
{
    const string Account = "1";
    const string Phone = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    static readonly DateTimeOffset T = new(2026, 10, 28, 4, 0, 0, TimeSpan.Zero);

    readonly FakeTimeProvider time = new(T);
    readonly Source source = new();
    readonly Sender sender = new(Account);
    readonly List<PushNoticeOutcome> outcomes = [];
    readonly string folder = Path.Combine(Path.GetTempPath(), "abnotice-" + Guid.NewGuid().ToString("N"));
    readonly P256KeyPair browser = P256KeyPair.Generate();
    PushNotifier notifier = null!;

    public PushNoticeEvidenceTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        browser.Dispose();
        try { Directory.Delete(folder, recursive: true); } catch (IOException) { }
    }

    void Start()
    {
        var store = PushStore.Load(Path.Combine(folder, "push-subscriptions.json"), Account);
        notifier = new PushNotifier(source, new MachineInfo("53d1997f0a1b2c3d4e5f60718293a4b5", "DEV", "windows", "0.1.0"), store, null, _ => { }, time)
        { NoticeOutcome = outcomes.Add };
        notifier.Attach(sender);
        notifier.Handle(new PushPeer(Phone, "iPhone", Account, true), Ops.PushSubscribe, JsonSerializer.SerializeToElement(new
        {
            endpoint = $"https://web.push.apple.com/{Phone}",
            keys = new { p256dh = B64Url.Encode(browser.Public), auth = B64Url.Encode(new byte[16]) },
            app = "https://danswett.github.io/agent-bridge/app/",
            device = "iPhone",
        }));
    }

    static Session Waiting(string id, string question) => new()
    {
        Id = id, Agent = AgentKind.Copilot, Title = "Fix the login", Status = SessionStatus.Waiting, StatusSince = T, UpdatedAt = T,
        Activity = new Activity { TurnCount = 1 },
        Question = new Question { Id = question, Kind = QuestionKind.Form, Message = "Which database?", AskedAt = T },
    };

    async Task Notify(int status)
    {
        Start();
        sender.Status = status;
        source.Upsert(Waiting("s1", "q1"));
        time.Advance(TimeSpan.FromSeconds(2));
        await notifier.PumpAsync();
    }

    [Theory]
    [InlineData(201, "delivered")]
    [InlineData(410, "gone")]
    [InlineData(503, "retrying")]
    [InlineData(400, "refused")]
    public async Task A_question_notice_tells_the_hook_once_how_it_went_to_the_phone(int status, string outcome)
    {
        await Notify(status);

        Assert.Equal(new PushNoticeOutcome(Phone, "s1", "q1", NoticeKind.Question, outcome), Assert.Single(outcomes));
    }

    [Fact]
    public async Task A_finished_turn_and_a_group_notice_never_tell_the_hook()
    {
        Start();
        sender.Status = 201;
        source.Upsert(Waiting("s1", "q1") with { Question = null, Status = SessionStatus.Working });
        source.Upsert(Waiting("s1", "q1") with { Question = null, Status = SessionStatus.Idle, Activity = new Activity { Response = "Done.", TurnCount = 2 } });
        source.Upsert(Waiting("s2", "q2"));
        source.Upsert(Waiting("s3", "q3"));
        time.Advance(TimeSpan.FromSeconds(3));
        await notifier.PumpAsync();

        Assert.True(sender.Pushes >= 3);
        Assert.All(outcomes, o => Assert.Equal(NoticeKind.Question, o.Kind));
        Assert.Equal(2, outcomes.Count);
    }

    sealed class Source : ISessionSource
    {
        List<Session> Sessions { get; } = [];
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
    }

    sealed class Sender(string account) : IPushSender
    {
        public string AccountId => account;
        public bool Connected => true;
        public int Status { get; set; } = 201;
        public int Pushes { get; private set; }
        public Task<PushResult> PushAsync(string endpoint, byte[] body, int ttl, string urgency, string? topic)
        {
            Pushes++;
            return Task.FromResult(new PushResult(Status, null));
        }
    }
}
